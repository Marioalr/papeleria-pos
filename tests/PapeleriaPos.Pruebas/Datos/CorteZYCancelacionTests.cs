using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El corte Z y la cancelación de una entrada o salida no se cruzan (revisión del PR #133): el corte lee todo con el candado de
/// escritura ya tomado, así que una cancelación que llega a la vez espera a que termine y ya no encuentra el turno abierto; y lo
/// que el corte contó es lo que quedó. Antes, una cancelación entre las lecturas del corte y su guardado pasaba, y el corte
/// guardado contaba la entrada cancelada: un faltante de $200 que no era. También, el historial de cortes dice cuántas se
/// cancelaron, como el corte al cerrarlo.
/// </summary>
public sealed class CorteZYCancelacionTests : IAsyncLifetime
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"corte-cancelacion-{Guid.NewGuid():N}.db");
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private Caja _caja = null!;

    private string Cadena => $"Data Source={_ruta};Pooling=False";

    private PosDbContext Contexto(params IInterceptor[] interceptores) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(Cadena).AddInterceptors(interceptores).Options);

    public async Task InitializeAsync()
    {
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _cajera.EstablecerPin("1111");
        _encargado.EstablecerPin("2222");
        await using var db = Contexto();
        await BaseDeDatos.PrepararAsync(db);
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _encargado);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
    }

    public Task DisposeAsync()
    {
        ArchivosDePrueba.BorrarBase(_ruta);
        return Task.CompletedTask;
    }

    /// <summary>El turno de la cajera con $500 de fondo, una entrada de $200 y una salida de $50.</summary>
    private async Task<(Turno Turno, MovimientoCaja Entrada)> TurnoConDosAsync()
    {
        await using var db = Contexto();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        var entrada = await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Entrada, 20000, "Cambio en monedas", _cajera.Id);
        await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 5000, "Garrafón", _cajera.Id);
        return (turno, entrada);
    }

    /// <summary>Cada comando que manda el contexto y si iba dentro de una transacción.</summary>
    private sealed class Espia : DbCommandInterceptor
    {
        public ConcurrentQueue<(string Sql, bool EnTransaccion)> Comandos { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Comandos.Enqueue((command.CommandText, command.Transaction is not null));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Antes del primer guardado del corte (ya leyó todo), arranca algo aparte, sin esperarlo.</summary>
    private sealed class AntesDeGuardar(Action accion) : SaveChangesInterceptor
    {
        private bool _hecho;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_hecho)
            {
                _hecho = true;
                accion();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task El_corte_Z_lee_el_turno_y_lo_que_cuenta_dentro_de_su_transaccion()
    {
        var (turno, _) = await TurnoConDosAsync();
        var espia = new Espia();

        await using (var db = Contexto(espia))
            await new RegistroCaja(db).CorteZAsync(turno.Id, _encargado.Id, 65000);

        bool Lee(string sql, string tabla) => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && sql.Contains($"FROM \"{tabla}\"", StringComparison.Ordinal);
        var comandos = espia.Comandos.ToList();
        Assert.Contains(comandos, c => Lee(c.Sql, "Turnos"));
        Assert.Contains(comandos, c => Lee(c.Sql, "MovimientosCaja"));
        Assert.All(comandos, c => Assert.True(c.EnTransaccion, c.Sql));
    }

    /// <summary>
    /// La cancelación sale justo cuando el corte ya leyó lo del turno: espera a que el corte termine, y entonces el turno ya está
    /// cerrado. El corte contó la entrada (no estaba cancelada) y la entrada se queda.
    /// </summary>
    [Fact]
    public async Task Una_cancelacion_a_la_vez_que_el_corte_espera_a_que_termine_y_ya_no_entra()
    {
        var (turno, entrada) = await TurnoConDosAsync();
        Task? cancelacion = null;
        var aLaVez = new AntesDeGuardar(() => cancelacion = Task.Run(async () =>
        {
            await using var otro = Contexto();
            await new RegistroCaja(otro).CancelarMovimientoAsync(entrada.Id, new SolicitudCancelarMovimiento(Guid.NewGuid(), _encargado.Id, _caja.Id,
                "Se registró dos veces"));
        }));

        Corte corte;
        await using (var db = Contexto(aLaVez))
            corte = await new RegistroCaja(db).CorteZAsync(turno.Id, _encargado.Id, 65000);

        var rechazo = await Assert.ThrowsAsync<InvalidOperationException>(() => cancelacion!);
        Assert.Equal("Una entrada o salida se cancela en su caja y en su mismo turno, antes de su corte.", rechazo.Message);
        await using var lectura = Contexto();
        Assert.False((await lectura.MovimientosCaja.AsNoTracking().SingleAsync(m => m.Id == entrada.Id)).Cancelado);
        var guardado = await lectura.Cortes.AsNoTracking().SingleAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z);
        Assert.Equal((20000L, 65000L, 0L), (guardado.EntradasCentavos, guardado.EsperadoCentavos, guardado.DiferenciaCentavos ?? 0));
        Assert.Equal(65000, corte.EsperadoCentavos);
    }

    /// <summary>Al revés: la cancelación termina antes de que el corte empiece, y el corte ya no la cuenta.</summary>
    [Fact]
    public async Task Lo_cancelado_antes_del_corte_no_cuenta_y_el_historial_dice_cuantas_se_cancelaron()
    {
        var (turno, entrada) = await TurnoConDosAsync();
        await using (var db = Contexto())
            await new RegistroCaja(db).CancelarMovimientoAsync(entrada.Id, new SolicitudCancelarMovimiento(Guid.NewGuid(), _encargado.Id, _caja.Id,
                "Se registró dos veces"));
        await using (var db = Contexto())
            Assert.Equal(45000, (await new RegistroCaja(db).CorteZAsync(turno.Id, _encargado.Id, 45000)).EsperadoCentavos);

        await using var lectura = Contexto();
        var historial = await new HistorialDeCortes(lectura).DelDiaAsync(_caja.Id, _encargado.Id, null, null, null);
        var corte = Assert.Single(historial.Turnos).Corte!;
        Assert.Equal((1, 0), (corte.EntradasCanceladas, corte.SalidasCanceladas));
        Assert.Equal("Entradas (1 cancelada)", DisenoComprobante.ConCanceladas("Entradas", corte.EntradasCanceladas));
        Assert.Contains(TicketTexto.Generar(DisenoComprobante.Corte(corte, "Caja 1")).Split(Environment.NewLine),
            r => r.StartsWith("Entradas (1 cancelada)", StringComparison.Ordinal));
    }
}
