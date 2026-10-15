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

public class RegistroCajaTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private Caja _caja = null!;
    private readonly Precio _precio;

    public RegistroCajaTests() =>
        _precio = new Precio { VarianteId = _catalogo.Azul.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _cajera.EstablecerPin("1111");
        _encargado.EstablecerPin("2222");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _encargado, _precio);
        await db.SaveChangesAsync();
        // 10 piezas a $4 cada una.
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra,
            Cantidad.Piezas(10), 4000, _base.Reloj.Ahora.UtcDateTime));
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private Venta VentaDe(Turno turno, int piezas)
    {
        var venta = new Venta { SucursalId = turno.SucursalId, CajaId = turno.CajaId, TurnoId = turno.Id, CajeroId = turno.CajeroId, Serie = _caja.Serie };
        var producto = new ProductoEnVenta(_catalogo.Azul.Id, "Pluma punto fino Azul", Impuesto.Iva16, UnidadBase.Pieza, true);
        venta.AgregarProducto(producto, _catalogo.Pieza, Cantidad.Piezas(piezas), [_precio]);
        return venta;
    }

    [Fact]
    public async Task Cobrar_asigna_folios_consecutivos_y_descuenta_inventario_al_costo_promedio()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);

        var primera = await registro.CobrarAsync(VentaDe(turno, 3), [new CobroSolicitado(FormaPago.Efectivo, 5000)]);
        var segunda = await registro.CobrarAsync(VentaDe(turno, 1), [new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1")]);

        Assert.Equal(("A-000001", "A-000002"), (primera.FolioTexto, segunda.FolioTexto));
        Assert.Equal(1200, primera.Lineas.Single().CostoCentavos);

        await using var lectura = _base.Contexto();
        var existencia = await new RegistroInventario(lectura).ExistenciaAsync(_catalogo.Matriz.Id, _catalogo.Azul.Id);
        Assert.Equal(Cantidad.Piezas(6), existencia.Cantidad);
        Assert.Equal(2400, existencia.CostoCentavos);
        var guardada = await lectura.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Folio == 1);
        Assert.Equal((3000L, 2000L), (guardada.TotalCentavos, guardada.Cobros.Single().CambioCentavos));
        Assert.Equal(1, await lectura.AperturasCajon.CountAsync(a => a.Motivo == MotivoApertura.Venta));
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Un_cobro_que_no_alcanza_no_guarda_nada_ni_gasta_folio()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registro.CobrarAsync(VentaDe(turno, 3), [new CobroSolicitado(FormaPago.Efectivo, 1000)]));
        var venta = await registro.CobrarAsync(VentaDe(turno, 1), [new CobroSolicitado(FormaPago.Efectivo, 1000)]);

        Assert.Equal(1, venta.Folio);
        await using var lectura = _base.Contexto();
        Assert.Equal(1, await lectura.Ventas.CountAsync());
    }

    [Fact]
    public async Task Una_caja_no_abre_dos_turnos()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.AbrirTurnoAsync(_caja.Id, _encargado.Id, 0));
    }

    [Fact]
    public async Task Las_salidas_de_efectivo_llevan_motivo()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 0);

        await Assert.ThrowsAsync<ArgumentException>(() => registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 1000, " ", _cajera.Id));
        await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 1000, "Garrafón de agua", _cajera.Id);

        Assert.Equal(1, await db.AperturasCajon.CountAsync(a => a.Motivo == MotivoApertura.SinVenta));
    }

    [Fact]
    public async Task El_corte_z_lo_hace_quien_tiene_el_permiso_con_el_total_y_cierra_el_turno()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        await registro.CobrarAsync(VentaDe(turno, 2), [new CobroSolicitado(FormaPago.Efectivo, 2000)]);
        await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 5000, "Papel de baño", _cajera.Id);

        // La cajera no tiene el permiso: queda negado en la bitácora y el turno sigue abierto.
        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => registro.CorteZAsync(turno.Id, _cajera.Id, 46000));
        var x = await registro.CorteXAsync(turno.Id, _cajera.Id);
        var z = await registro.CorteZAsync(turno.Id, _encargado.Id, 46000); // $460 contados contra $470 esperados: faltan $10

        Assert.Contains(_encargado.Nombre, sinPermiso.Message);
        Assert.Equal(50000 + 2000 - 5000, x.EsperadoCentavos);
        Assert.Equal(-1000, z.DiferenciaCentavos);
        Assert.Null(await registro.TurnoAbiertoAsync(_caja.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CobrarAsync(VentaDe(turno, 1), [new CobroSolicitado(FormaPago.Efectivo, 1000)]));

        await using var lectura = _base.Contexto();
        // Ya no se cuenta billete por billete: solo el total.
        Assert.Equal(0, await lectura.CortesConteo.CountAsync());
        var bitacora = await lectura.Bitacora.Where(b => b.Accion == AccionControlada.CorteZ).OrderBy(b => b.FechaUtc).ToListAsync();
        Assert.Equal([ResultadoBitacora.Negado, ResultadoBitacora.Hecho], bitacora.Select(b => b.Resultado));
        Assert.Equal((_encargado.Id, -1000L), (bitacora[1].UsuarioId!.Value, bitacora[1].MontoCentavos!.Value));
        Assert.StartsWith($"Corte del turno de {_cajera.Nombre}", bitacora[1].Detalle);
    }

    [Fact]
    public async Task El_pin_se_guarda_y_verifica_desde_la_base()
    {
        await using var db = _base.Contexto();
        var ana = await db.Usuarios.SingleAsync(u => u.NombreUsuario == "ana");

        Assert.Equal(ResultadoPin.Correcto, ana.VerificarPin("1111", _base.Reloj.Ahora.UtcDateTime));
        Assert.DoesNotContain("1111", ana.PinHash);
    }
}
