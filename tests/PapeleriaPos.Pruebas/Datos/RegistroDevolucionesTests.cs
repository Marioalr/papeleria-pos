using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

public class RegistroDevolucionesTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private readonly Usuario _dueno = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };
    private readonly Caja _caja = new() { Nombre = "Caja 1", Serie = "A" };
    private readonly Precio _precio;
    private Turno _turno = null!;

    public RegistroDevolucionesTests() =>
        _precio = new Precio { VarianteId = _catalogo.Azul.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 15000 };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja.SucursalId = _catalogo.Matriz.Id;
        _cajera.EstablecerPin("1111");
        _encargado.EstablecerPin("2222");
        _dueno.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _encargado, _dueno, _precio);
        await db.SaveChangesAsync();
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
        await MigracionPermisos.AplicarAsync(db);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra,
            Cantidad.Piezas(10), 50000, _base.Reloj.Ahora.UtcDateTime));
        _turno = await new RegistroCaja(db).AbrirTurnoAsync(_caja.Id, _cajera.Id, 100000);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private async Task<Venta> VenderAsync(PosDbContext db, int piezas)
    {
        var venta = new Venta { SucursalId = _caja.SucursalId, CajaId = _caja.Id, TurnoId = _turno.Id, CajeroId = _cajera.Id, Serie = "A" };
        venta.AgregarProducto(new ProductoEnVenta(_catalogo.Azul.Id, "Pluma azul", Impuesto.Iva16, UnidadBase.Pieza, true), _catalogo.Pieza, Cantidad.Piezas(piezas), [_precio]);
        return await new RegistroCaja(db).CobrarAsync(venta, [new CobroSolicitado(FormaPago.Efectivo, piezas * 15000)]);
    }

    [Fact]
    public async Task Cancelar_pide_encargado_regresa_inventario_y_baja_el_efectivo_esperado()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 2); // $300, costo 2 × $50
        var devoluciones = new RegistroDevoluciones(db);

        await Assert.ThrowsAsync<PermisoRequerido>(() => devoluciones.CancelarAsync(venta.Id, _cajera.Id, _caja.Id, "Se equivocó", null));
        var autorizacion = await new ServicioControl(db).AutorizarAsync(AccionControlada.CancelarTicket, _caja.SucursalId, _caja.Id, _cajera.Id, "luis", "2222", 30000, "Cancelar");
        var cancelacion = await devoluciones.CancelarAsync(venta.Id, _cajera.Id, _caja.Id, "Se equivocó", autorizacion.Id);

        await using var lectura = _base.Contexto();
        Assert.Equal((30000L, _encargado.Id), (cancelacion.ReembolsoEfectivoCentavos, cancelacion.AutorizoId!.Value));
        Assert.Equal(EstadoVenta.Cancelada, (await lectura.Ventas.SingleAsync(v => v.Id == venta.Id)).Estado);
        var existencia = await new RegistroInventario(lectura).ExistenciaAsync(_caja.SucursalId, _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(10), 50000L), (existencia.Cantidad, existencia.CostoCentavos));
        var (esperado, _) = await new RegistroCaja(lectura).EfectivoAsync(_turno.Id);
        Assert.Equal(100000, esperado);
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Una_devolucion_chica_la_hace_el_cajero_y_lo_danado_va_a_merma()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 3);
        var linea = venta.Lineas.Single();

        var devolucion = await new RegistroDevoluciones(db).DevolverAsync(venta.Id, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1), RegresaAlInventario: false)],
            _cajera.Id, _caja.Id, "Llegó rota", null);

        Assert.Equal(15000, devolucion.ReembolsoEfectivoCentavos);
        await using var lectura = _base.Contexto();
        Assert.Equal(1, await lectura.MovimientosInventario.CountAsync(m => m.Tipo == TipoMovimiento.Merma));
        Assert.Equal(Cantidad.Piezas(7), (await new RegistroInventario(lectura).ExistenciaAsync(_caja.SucursalId, _catalogo.Azul.Id)).Cantidad);
        Assert.Equal(1, await lectura.Bitacora.CountAsync(b => b.Accion == AccionControlada.Devolucion && b.Resultado == ResultadoBitacora.Hecho));
    }

    [Fact]
    public async Task Una_devolucion_de_mas_de_200_pide_encargado()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 2);

        await Assert.ThrowsAsync<PermisoRequerido>(() => new RegistroDevoluciones(db).DevolverAsync(venta.Id,
            [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(2))], _cajera.Id, _caja.Id, "No le gustó", null));
    }

    [Fact]
    public async Task Un_precio_cambiado_solo_se_cobra_con_autorizacion_del_dueno()
    {
        await using var db = _base.Contexto();
        Venta Armar(Guid? autorizacion)
        {
            var venta = new Venta { SucursalId = _caja.SucursalId, CajaId = _caja.Id, TurnoId = _turno.Id, CajeroId = _cajera.Id, Serie = "A" };
            var linea = venta.AgregarProducto(new ProductoEnVenta(_catalogo.Azul.Id, "Pluma azul", Impuesto.Iva16, UnidadBase.Pieza, true), _catalogo.Pieza, Cantidad.Piezas(1), [_precio]);
            venta.CambiarPrecio(linea, 12000, autorizacion);
            return venta;
        }

        await Assert.ThrowsAsync<PermisoRequerido>(() => new RegistroCaja(db).CobrarAsync(Armar(null), [new CobroSolicitado(FormaPago.Efectivo, 12000)]));
        var autorizacion = await new ServicioControl(db).AutorizarAsync(AccionControlada.CambiarPrecio, _caja.SucursalId, _caja.Id, _cajera.Id, "mario", "3333", 12000, "Precio");
        var cobrada = await new RegistroCaja(db).CobrarAsync(Armar(autorizacion.Id), [new CobroSolicitado(FormaPago.Efectivo, 12000)]);

        Assert.Equal(12000, cobrada.TotalCentavos);
        Assert.Equal(1, cobrada.Folio); // el intento rechazado no gastó folio
    }

    [Fact]
    public async Task Las_transferencias_las_verifica_el_encargado_una_vez()
    {
        await using var db = _base.Contexto();
        var venta = new Venta { SucursalId = _caja.SucursalId, CajaId = _caja.Id, TurnoId = _turno.Id, CajeroId = _cajera.Id, Serie = "A" };
        venta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        await new RegistroCaja(db).CobrarAsync(venta, [new CobroSolicitado(FormaPago.Transferencia, 5000, "SPEI 9")]);
        var cobro = venta.Cobros.Single();
        var registro = new RegistroCaja(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.VerificarTransferenciaAsync(cobro.Id, "ana", "1111", true));
        var verificado = await registro.VerificarTransferenciaAsync(cobro.Id, "luis", "2222", llego: false);

        Assert.Equal(Verificacion.Rechazada, verificado.Verificacion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.VerificarTransferenciaAsync(cobro.Id, "luis", "2222", true));
    }
}
