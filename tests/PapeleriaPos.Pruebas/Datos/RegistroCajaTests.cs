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
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
        await MigracionPermisos.AplicarAsync(db);
        // 10 piezas a $4 cada una.
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra,
            Cantidad.Piezas(10), 4000, _base.Reloj.Ahora.UtcDateTime));
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Beto, otro cajero (sin «Hacer el corte del turno de otro»), con su PIN 4444.</summary>
    private static async Task<Guid> OtroCajeroAsync(PosDbContext db)
    {
        if (await db.Usuarios.FirstOrDefaultAsync(u => u.NombreUsuario == "beto") is { } ya)
            return ya.Id;
        var beto = new Usuario { Nombre = "Beto", NombreUsuario = "beto" };
        beto.EstablecerPin("4444");
        db.Usuarios.Add(beto);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        return beto.Id;
    }

    /// <summary>
    /// El cajero cierra su propio turno a ciegas (2026-11-20, docs/propuestas/corte-y-salir.md), sin «Hacer el corte del turno de otro»:
    /// el servicio lo deja. Antes de cerrarlo, ni el corte X ni el informativo le dicen cuánto debería haber; puede cortar el suyo y
    /// no el de otro.
    /// </summary>
    [Fact]
    public async Task El_cajero_cierra_su_propio_turno_a_ciegas_y_no_el_de_otro()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        await registro.CobrarAsync(VentaDe(turno, 2), [new CobroSolicitado(FormaPago.Efectivo, 2000)]);
        var beto = await OtroCajeroAsync(db);

        // Lo esperado no sale antes del corte: ni con el corte X (que lo trae) ni con el informativo.
        await Assert.ThrowsAsync<PermisoRequerido>(() => registro.CorteXAsync(turno.Id, _cajera.Id));
        var suyo = await registro.InformativoAsync(turno.Id, _cajera.Id);
        Assert.True(suyo.PuedeCortar);
        Assert.Null(suyo.EsperadoCentavos);
        Assert.False(suyo.VeElDia);
        // Otro cajero: ni lo puede cerrar ni ve lo esperado.
        var deOtro = await registro.InformativoAsync(turno.Id, beto);
        Assert.False(deOtro.PuedeCortar);
        Assert.Null(deOtro.EsperadoCentavos);
        await Assert.ThrowsAsync<PermisoRequerido>(() => registro.CorteZAsync(turno.Id, beto, 52000));

        // La cajera, con su PIN: cierra el suyo; ya cerrado, ve lo esperado y su diferencia.
        var corte = await registro.CorteZConPinAsync(turno.Id, _cajera.Id, "1111", 51000);
        Assert.Equal((52000L, -1000L), (corte.EsperadoCentavos, corte.DiferenciaCentavos!.Value));
        Assert.Null(await registro.TurnoAbiertoAsync(_caja.Id));
        await using var lectura = _base.Contexto();
        var bitacora = await lectura.Bitacora.Where(b => b.Accion == AccionControlada.CorteZ).OrderBy(b => b.FechaUtc).ToListAsync();
        Assert.Equal([ResultadoBitacora.Negado, ResultadoBitacora.Hecho], bitacora.Select(b => b.Resultado));
        Assert.Equal(_cajera.Id, bitacora[1].UsuarioId);
    }

    /// <summary>
    /// El PIN para cerrar el turno (el dueño, 2026-11-20; provisional): sin él o equivocado no se cierra, con los intentos y la espera de
    /// la entrada; con el bueno, sí. Lo manda la caja otra vez con el mismo Id (no le llegó la respuesta): es el mismo corte.
    /// </summary>
    [Fact]
    public async Task Sin_el_pin_bueno_no_se_cierra_el_turno()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);

        Assert.Equal(FichasEntrada.EscribeTuPin, (await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "", 50000))).Message);
        var malo = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "9999", 50000));
        Assert.Equal(FichasEntrada.PinIncorrecto(Usuario.IntentosPermitidos - 1), malo.Message);
        Assert.NotNull(await registro.TurnoAbiertoAsync(_caja.Id));
        // El PIN de otro (el del encargado) tampoco cierra el turno de la cajera a nombre de ella.
        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "2222", 50000));
        Assert.NotNull(await registro.TurnoAbiertoAsync(_caja.Id));
        Assert.Empty(await db.Cortes.Where(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z).ToListAsync());

        // Con los intentos de la entrada: al quinto, a esperar, y ni con el bueno mientras.
        for (var k = 0; k < Usuario.IntentosPermitidos - 3; k++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "0000", 50000));
        var bloqueo = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "0000", 50000));
        Assert.Equal(FichasEntrada.DemasiadosIntentos, bloqueo.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CorteZConPinAsync(turno.Id, _cajera.Id, "1111", 50000));
        Assert.NotNull(await registro.TurnoAbiertoAsync(_caja.Id));

        // Pasada la espera, con el bueno se cierra; la caja lo manda otra vez con el mismo Id y es el mismo corte (sin volver a pedir el PIN).
        _base.Reloj.Ahora += Usuario.TiempoDeBloqueo + TimeSpan.FromSeconds(1);
        var id = Guid.NewGuid();
        var corte = await registro.CorteZConPinAsync(turno.Id, _cajera.Id, "1111", 50000, id);
        Assert.Equal(id, corte.Id);
        Assert.Equal(id, (await registro.CorteZConPinAsync(turno.Id, _cajera.Id, null, 50000, id)).Id);
        Assert.Null(await registro.TurnoAbiertoAsync(_caja.Id));
    }

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
    public async Task El_corte_z_del_turno_de_otro_lo_hace_quien_tiene_el_permiso_con_el_total_y_cierra_el_turno()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        await registro.CobrarAsync(VentaDe(turno, 2), [new CobroSolicitado(FormaPago.Efectivo, 2000)]);
        await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 5000, "Papel de baño", _cajera.Id);

        // Otro cajero no tiene «Hacer el corte del turno de otro»: queda negado en la bitácora y el turno sigue abierto.
        var beto = await OtroCajeroAsync(db);
        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => registro.CorteZAsync(turno.Id, beto, 46000));
        var z = await registro.CorteZAsync(turno.Id, _encargado.Id, 46000); // $460 contados contra $470 esperados: faltan $10

        Assert.Contains(_encargado.Nombre, sinPermiso.Message);
        Assert.Equal(50000 + 2000 - 5000, z.EsperadoCentavos);
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
