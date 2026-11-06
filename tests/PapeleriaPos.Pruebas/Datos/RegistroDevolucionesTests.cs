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

    /// <summary>
    /// Se vende de más, llega mercancía a otro costo y queda poca: la siguiente venta guarda un costo real (antes, negativo) y se
    /// puede cancelar. Antes el cajero leía «El costo de una entrada no puede ser negativo» y no podía hacerlo.
    /// </summary>
    [Fact]
    public async Task Vender_de_mas_y_recibir_a_otro_costo_deja_la_siguiente_venta_con_costo_y_cancelable()
    {
        await using var db = _base.Contexto();
        await VenderAsync(db, 12); // De las 10 a $50: quedan −2 con valor −$100.
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_caja.SucursalId, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(3), 3000,
            _base.Reloj.Ahora.UtcDateTime));

        var venta = await VenderAsync(db, 1);
        Assert.Equal(1000, venta.Lineas.Single().CostoCentavos);
        var cancelacion = await new RegistroDevoluciones(db).CancelarAsync(venta.Id, _dueno.Id, _caja.Id, "Se equivocó", null);

        Assert.True(cancelacion.EsCancelacion);
    }

    /// <summary>Un ticket que ya quedó guardado con costo negativo (de antes del arreglo) se cancela y se devuelve; entra a costo cero.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_ticket_guardado_con_costo_negativo_se_cancela_y_se_devuelve(bool cancelar)
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 2);
        var linea = venta.Lineas.Single();
        linea.CostoCentavos = -3038;
        await db.SaveChangesAsync();

        if (cancelar)
            await new RegistroDevoluciones(db).CancelarAsync(venta.Id, _dueno.Id, _caja.Id, "Se equivocó", null);
        else
            await new RegistroDevoluciones(db).DevolverAsync(venta.Id, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], _dueno.Id, _caja.Id, "Defecto", null);

        await using var lectura = _base.Contexto();
        var entrada = await lectura.MovimientosInventario.SingleAsync(m => m.Tipo == TipoMovimiento.Devolucion);
        Assert.Equal((Cantidad.Piezas(cancelar ? 2 : 1), 0L), (entrada.Cantidad, entrada.CostoCentavos));
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

    /// <summary>«A1» y «1», como en «Ventas del día» (auditoría, 2026-11-04); antes solo «A-1» lo encontraba.</summary>
    [Theory]
    [InlineData("A-000001")]
    [InlineData("a-1")]
    [InlineData("A1")]
    [InlineData("a000001")]
    [InlineData(" 1 ")]
    [InlineData("000001")]
    public async Task El_ticket_se_encuentra_con_el_folio_escrito_de_varias_formas(string folio)
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 1);

        Assert.Equal(venta.Id, (await new RegistroDevoluciones(db).PorFolioAsync(_caja.SucursalId, folio, cajaId: _caja.Id))?.Id);
    }

    [Theory]
    [InlineData("B-1")]
    [InlineData("A-2")]
    [InlineData("2")]
    [InlineData("A-")]
    [InlineData("hola")]
    public async Task Un_folio_que_no_esta_no_encuentra_nada(string folio)
    {
        await using var db = _base.Contexto();
        await VenderAsync(db, 1);

        Assert.Null(await new RegistroDevoluciones(db).PorFolioAsync(_caja.SucursalId, folio, cajaId: _caja.Id));
    }

    /// <summary>En el piloto, solo el número es el folio de eleventa (lo que trae el ticket): no se confunde con el de la caja.</summary>
    [Fact]
    public async Task En_el_piloto_solo_el_numero_no_busca_el_folio_de_la_caja()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 1);
        var otra = await VenderAsync(db, 1);
        db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = otra.Id, FolioEleventa = 77, CreadoUtc = _base.Reloj.Ahora.UtcDateTime });
        await db.SaveChangesAsync();
        var registro = new RegistroDevoluciones(db);

        Assert.Equal(otra.Id, (await registro.PorFolioAsync(_caja.SucursalId, "77", cajaId: _caja.Id))?.Id);
        Assert.Null(await registro.PorFolioAsync(_caja.SucursalId, "1", cajaId: _caja.Id));
        Assert.Equal(venta.Id, (await registro.PorFolioAsync(_caja.SucursalId, "A1", cajaId: _caja.Id))?.Id);
    }

    /// <summary>Lo vendido de un producto que después se juntó con otro regresa al que quedó (auditoría, 2026-11-04), no al inactivo, que ya no se vende.</summary>
    [Fact]
    public async Task Lo_devuelto_de_un_producto_ya_juntado_entra_al_que_quedo()
    {
        await using var db = _base.Contexto();
        var destino = new Variante { ProductoId = _catalogo.Pluma.Id, Descripcion = "Negro" };
        db.Add(destino);
        db.Add(new Presentacion { VarianteId = destino.Id, Nombre = "Pieza", EsBase = true });
        await db.SaveChangesAsync();
        var venta = await VenderAsync(db, 2);
        await new RegistroDepuracion(db).JuntarAsync(_dueno.Id, _caja.SucursalId, _catalogo.Azul.Id, destino.Id);
        var antes = (await new RegistroInventario(db).ExistenciaAsync(_caja.SucursalId, destino.Id)).Cantidad;

        await new RegistroDevoluciones(db).DevolverAsync(venta.Id, [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(1))],
            _cajera.Id, _caja.Id, "No le gustó", null);

        await using var lectura = _base.Contexto();
        var inventario = new RegistroInventario(lectura);
        Assert.Equal(antes + Cantidad.Piezas(1), (await inventario.ExistenciaAsync(_caja.SucursalId, destino.Id)).Cantidad);
        Assert.Equal(Cantidad.Cero, (await inventario.ExistenciaAsync(_caja.SucursalId, _catalogo.Azul.Id)).Cantidad);
    }

    [Fact]
    public async Task Media_pieza_no_se_devuelve_y_lo_dice_sin_texto_de_programador()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 3);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new RegistroDevoluciones(db).DevolverAsync(venta.Id,
            [new LineaPorDevolver(venta.Lineas.Single().Id, new Cantidad(500))], _cajera.Id, _caja.Id, "No le gustó", null));

        Assert.Equal("De «Pluma azul» se devuelven piezas enteras.", error.Message);
    }

    /// <summary>Compraron 3 y devolvieron 2: para el sugerido de compra se vendió 1 (auditoría, 2026-11-04). Antes contaba 3 y pedía de más.</summary>
    [Fact]
    public async Task Lo_devuelto_no_cuenta_como_vendido_en_el_sugerido()
    {
        await using var db = _base.Contexto();
        var venta = await VenderAsync(db, 3);
        // Lo de hoy no cuenta todavía: se ve al día siguiente.
        _base.Reloj.Ahora = _base.Reloj.Ahora.AddDays(1);
        var antes = (await new RegistroSugerido(db).DeVariantesAsync(_caja.SucursalId, [_catalogo.Azul.Id]))[_catalogo.Azul.Id].Resultado;
        Assert.True(antes.VentaDiaria > 0);

        await new RegistroDevoluciones(db).DevolverAsync(venta.Id, [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(2))],
            _dueno.Id, _caja.Id, "No le gustó", null);

        await using var lectura = _base.Contexto();
        var despues = (await new RegistroSugerido(lectura).DeVariantesAsync(_caja.SucursalId, [_catalogo.Azul.Id]))[_catalogo.Azul.Id].Resultado;
        Assert.Equal(antes.VentaDiaria / 3, despues.VentaDiaria, 2);
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

        await Assert.ThrowsAsync<PermisoRequerido>(() => registro.VerificarTransferenciaAsync(cobro.Id, _cajera.Id, true));
        var verificado = await registro.VerificarTransferenciaAsync(cobro.Id, _encargado.Id, llego: false);

        Assert.Equal(Verificacion.Rechazada, verificado.Verificacion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.VerificarTransferenciaAsync(cobro.Id, _encargado.Id, true));
    }
}
