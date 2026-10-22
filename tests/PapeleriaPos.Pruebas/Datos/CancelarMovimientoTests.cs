using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Cancelar una entrada o salida equivocada (F7/F8, como en eleventa; 2026-10-22; antes se compensaba con la contraria): con
/// motivo y con «Cancelar una entrada o salida», solo en su caja y en su mismo turno, antes de su corte. Queda en la bitácora
/// y el corte ya no la cuenta. En el piloto con eleventa no, ni la que ya va a eleventa.
/// </summary>
public sealed class CancelarMovimientoTests : IAsyncLifetime
{
    private const string EnSuTurno = "Una entrada o salida se cancela en su caja y en su mismo turno, antes de su corte.";

    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private Caja _caja = null!;
    private Caja _otra = null!;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _otra = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 2", Serie = "B" };
        _cajera.EstablecerPin("1111");
        _encargado.EstablecerPin("2222");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _otra, _cajera, _encargado);
        await db.SaveChangesAsync();
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
        await MigracionPermisos.AplicarAsync(db);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>El turno de la cajera con una entrada de $200 y una salida de $50.</summary>
    private async Task<(Turno Turno, MovimientoCaja Entrada, MovimientoCaja Salida)> TurnoConDosAsync()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        var entrada = await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Entrada, 20000, "Cambio en monedas", _cajera.Id);
        var salida = await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 5000, "Garrafón", _cajera.Id);
        return (turno, entrada, salida);
    }

    private SolicitudCancelarMovimiento Del(Usuario quien, string motivo, Caja? caja = null) => new(Guid.NewGuid(), quien.Id, (caja ?? _caja).Id, motivo);

    [Fact]
    public async Task Se_cancela_con_su_motivo_queda_en_la_bitacora_y_el_corte_ya_no_la_cuenta()
    {
        var (turno, entrada, _) = await TurnoConDosAsync();

        await using (var db = _base.Contexto())
            Assert.True((await new RegistroCaja(db).CancelarMovimientoAsync(entrada.Id, Del(_encargado, "  Se registró dos veces "))).Cancelado);

        await using var lectura = _base.Contexto();
        var guardada = await lectura.MovimientosCaja.AsNoTracking().SingleAsync(m => m.Id == entrada.Id);
        Assert.Equal((_encargado.Id, "Se registró dos veces"), (guardada.CanceladoPorId, guardada.MotivoCancelacion));
        var bitacora = await lectura.Bitacora.AsNoTracking().SingleAsync(b => b.Accion == AccionControlada.CancelarMovimiento);
        Assert.Equal(((Guid?)_encargado.Id, (Guid?)_caja.Id, (long?)20000), (bitacora.UsuarioId, bitacora.CajaId, bitacora.MontoCentavos));
        Assert.Equal($"Canceló la entrada de efectivo {RegistroCaja.FolioDe(entrada)} de las 12:00 (Cambio en monedas). Motivo: Se registró dos veces",
            bitacora.Detalle);
        // El efectivo regresa (o vuelve a salir): el cajón se abre, como al registrarla.
        Assert.Single(await lectura.AperturasCajon.AsNoTracking().Where(a => a.Nota!.StartsWith("Cancelación de la entrada")).ToListAsync());

        // Ni el efectivo de la caja ni el corte la cuentan: $500 de fondo, menos la salida de $50.
        var registro = new RegistroCaja(lectura);
        Assert.Equal(45000, (await registro.EfectivoAsync(turno.Id)).Esperado);
        var corte = await registro.CorteZAsync(turno.Id, _encargado.Id, 45000);
        Assert.Equal((0L, 5000L, 45000L, 0L), (corte.EntradasCentavos, corte.SalidasCentavos, corte.EsperadoCentavos, corte.DiferenciaCentavos ?? 0));

        // En la lista del turno se ve cancelada, con quién y por qué; con el corte ya ninguna se cancela.
        var lista = (await registro.MovimientosDelTurnoAsync(turno.Id, piloto: false)).Movimientos;
        var vista = lista.Single(m => m.Id == entrada.Id);
        Assert.Equal((true, "Luis", "Se registró dos veces"), (vista.Cancelado, vista.CanceladoPor, vista.MotivoCancelacion));
        Assert.All(lista, m => Assert.False(m.SeCancela));
    }

    [Fact]
    public async Task Sin_motivo_sin_el_permiso_o_ya_cancelada_no_se_cancela()
    {
        var (_, entrada, _) = await TurnoConDosAsync();
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);

        await Assert.ThrowsAsync<ArgumentException>(() => registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "  ")));
        // La cajera, con lo inicial: «No» (que entre quien puede, o la contraria).
        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => registro.CancelarMovimientoAsync(entrada.Id, Del(_cajera, "Me equivoqué")));
        Assert.Equal(NivelPermiso.NoPermitido, sinPermiso.Nivel);

        await registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "Me equivoqué"));
        var otraVez = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "Otra vez")));
        Assert.Equal("Esa entrada ya se canceló.", otraVez.Message);
        Assert.Equal(1, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.CancelarMovimiento));
    }

    /// <summary>
    /// Se perdió la respuesta y la caja la manda otra vez, con el mismo Id: ya está hecha, sin otra bitácora ni otra apertura del
    /// cajón; aunque mientras tanto se haya hecho el corte. Otra solicitud para la misma sí dice que ya se canceló.
    /// </summary>
    [Fact]
    public async Task La_misma_solicitud_otra_vez_es_la_misma_cancelacion()
    {
        var (turno, entrada, _) = await TurnoConDosAsync();
        var solicitud = Del(_encargado, "Se registró dos veces");
        await using (var db = _base.Contexto())
            await new RegistroCaja(db).CancelarMovimientoAsync(entrada.Id, solicitud);
        await using (var db = _base.Contexto())
            await new RegistroCaja(db).CorteZAsync(turno.Id, _encargado.Id, 45000);

        await using var otra = _base.Contexto();
        var registro = new RegistroCaja(otra);
        var otraVez = await registro.CancelarMovimientoAsync(entrada.Id, solicitud with { Motivo = "Otra vez" });
        Assert.Equal((true, "Se registró dos veces", (Guid?)solicitud.Id), (otraVez.Cancelado, otraVez.MotivoCancelacion, otraVez.CancelacionId));
        Assert.Equal(1, await otra.Bitacora.CountAsync(b => b.Accion == AccionControlada.CancelarMovimiento));
        Assert.Equal(1, await otra.AperturasCajon.CountAsync(a => a.Nota!.StartsWith("Cancelación de la entrada")));
        var nueva = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "Otra")));
        Assert.Equal("Una entrada o salida se cancela en su caja y en su mismo turno, antes de su corte.", nueva.Message);
    }

    [Fact]
    public async Task Solo_en_su_caja_y_en_su_turno_antes_del_corte()
    {
        var (turno, entrada, salida) = await TurnoConDosAsync();
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);

        var enOtraCaja = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "x", _otra)));
        Assert.Equal(EnSuTurno, enOtraCaja.Message);

        await registro.CorteZAsync(turno.Id, _encargado.Id, 65000);
        var despues = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CancelarMovimientoAsync(salida.Id, Del(_encargado, "x")));
        Assert.Equal(EnSuTurno, despues.Message);
        Assert.False(await db.MovimientosCaja.AnyAsync(m => m.CanceladoUtc != null));
        Assert.False(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.CancelarMovimiento));
    }

    [Fact]
    public async Task En_el_piloto_y_la_que_va_a_eleventa_se_ven_pero_no_se_cancelan()
    {
        var (turno, entrada, salida) = await TurnoConDosAsync();
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);

        // El piloto: van a eleventa, que no se sabe cómo anota una cancelada. Se ven; ninguna se cancela, y dice por qué.
        var enElPiloto = await registro.MovimientosDelTurnoAsync(turno.Id, piloto: true);
        Assert.True(enElPiloto.Piloto);
        Assert.Equal(2, enElPiloto.Movimientos.Count);
        Assert.All(enElPiloto.Movimientos, m => Assert.False(m.SeCancela));
        var piloto = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registro.CancelarMovimientoAsync(entrada.Id, Del(_encargado, "Me equivoqué"), piloto: true));
        Assert.Equal(RegistroCaja.NoSeCancelaEnElPiloto, piloto.Message);

        // Sin el piloto, la que ya va a eleventa (su turno mandó algo allá) tampoco.
        db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.MovimientoDeCaja, MovimientoId = salida.Id, CreadoUtc = _base.Reloj.Ahora.UtcDateTime });
        await db.SaveChangesAsync();
        var lista = (await registro.MovimientosDelTurnoAsync(turno.Id, piloto: false)).Movimientos;
        Assert.Equal((true, false), (lista.Single(m => m.Id == entrada.Id).SeCancela, lista.Single(m => m.Id == salida.Id).SeCancela));
        var aEleventa = await Assert.ThrowsAsync<InvalidOperationException>(() => registro.CancelarMovimientoAsync(salida.Id, Del(_encargado, "Me equivoqué")));
        Assert.Equal(RegistroCaja.NoSeCancelaEnElPiloto, aEleventa.Message);
        Assert.False(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.CancelarMovimiento));

        // La caja dice lo mismo que el servicio, con o sin conexión.
        Assert.Equal(RegistroCaja.NoSeCancelaEnElPiloto, PilotoConEleventa.NoSeCancelaMovimiento);
    }
}
