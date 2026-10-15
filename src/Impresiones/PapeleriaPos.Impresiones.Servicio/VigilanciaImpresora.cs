using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>Lee la impresora por la red (SNMP y su página de estado). Se cambia en las pruebas.</summary>
public interface ILectorImpresora
{
    Task<PrinterStatus> LeerAsync(string host, CancellationToken ct);
}

public sealed class LectorImpresora : ILectorImpresora
{
    public Task<PrinterStatus> LeerAsync(string host, CancellationToken ct) => new PrinterStatusReader(host).ReadAsync(ct);
}

/// <summary>
/// La impresora de cada sucursal, vista desde la caja principal: la lee cada pocos minutos y poco después de cada
/// impresión (los contadores se actualizan al terminar), guarda las lecturas y verifica con el contador las hojas que
/// de verdad salieron (<see cref="PageAuditor"/>). La verificación vive en memoria: si el servicio se reinicia a media
/// impresión, ese lote simplemente no se verifica.
/// </summary>
public sealed class VigilanciaImpresora(IServiceScopeFactory alcances, ILectorImpresora lector, TimeProvider reloj, ILogger<VigilanciaImpresora> log)
    : BackgroundService
{
    /// <summary>Margen por si el reloj de la PC que imprimió no coincide con el de la caja principal.</summary>
    public static readonly TimeSpan MargenDeReloj = TimeSpan.FromMinutes(2);

    /// <summary>Después de una impresión se lee a los 90 s; mientras el lote no se asienta, cada 30 s.</summary>
    public static readonly TimeSpan TrasImprimir = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MientrasSeVerifica = TimeSpan.FromSeconds(30);

    /// <summary>Sin respuesta durante este tiempo (en horario de la tienda), se avisa.</summary>
    public static readonly TimeSpan SinRespuesta = TimeSpan.FromMinutes(30);

    private sealed class Estado
    {
        public readonly SemaphoreSlim Candado = new(1, 1);
        public PageAuditor Auditor = new();
        public DateTime? BaseUtc;
        public DateTime SiguientePeriodica = DateTime.MinValue;
        public DateTime? SiguientePronto;
        public DateTime? UltimaRespuesta;
        public DateTime FallandoDesde = DateTime.MaxValue;
        public string? Error;
    }

    private readonly ConcurrentDictionary<Guid, Estado> _sucursales = new();
    private readonly SemaphoreSlim _despertar = new(0, 1);

    private DateTime Ahora => reloj.GetUtcNow().UtcDateTime;
    private Estado De(Guid sucursalId) => _sucursales.GetOrAdd(sucursalId, _ => new Estado());

    /// <summary>Por qué falló la última lectura de la sucursal (vacío si respondió o no se ha leído).</summary>
    public string? Error(Guid sucursalId) => _sucursales.TryGetValue(sucursalId, out var e) ? e.Error : null;

    /// <summary>
    /// Llegó un trabajo del agente: entra al lote que se verifica con el contador. La base es la última lectura tomada
    /// antes de que se mandara a la cola; sin ella (o si el trabajo se mandó antes de la base del lote abierto), el lote no
    /// se puede verificar y se descarta: con el contador se puede no ver un faltante, pero nunca inventarlo.
    /// </summary>
    public async Task TrabajoRegistradoAsync(TrabajoImpresion t, CancellationToken ct = default)
    {
        if (t.Tipo != TipoTrabajoImpresion.Impresion || t.EnviadoUtc is not { } enviado)
            return;
        var e = De(t.SucursalId);
        using var alcance = alcances.CreateScope();
        var impresora = ImpresionesDelPos.Impresora(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
        var antes = await impresora.AntesDeAsync(t.SucursalId, enviado - MargenDeReloj, ct);
        await e.Candado.WaitAsync(ct);
        try
        {
            if (e.Auditor.IsOpen && (e.BaseUtc is not { } baseUtc || enviado - MargenDeReloj < baseUtc))
            {
                log.LogInformation("Lote de impresión sin verificar: «{Documento}» se mandó antes de la lectura base.", t.Documento);
                e.Auditor = new PageAuditor();
                e.BaseUtc = null;
                return;
            }
            if (!e.Auditor.IsOpen)
                e.BaseUtc = antes?.TomadaUtc;
            e.Auditor.JobDetected(t.Id, antes?.Contador, Ahora);
            e.Auditor.JobFinished(Ahora);
            Pronto(e, TrasImprimir);
        }
        finally
        {
            e.Candado.Release();
        }
    }

    /// <summary>Lee ya (botón "Leer ahora" o al cambiar la configuración). Devuelve el error si no respondió.</summary>
    public async Task<string?> LeerAhoraAsync(Guid sucursalId, CancellationToken ct = default)
    {
        using var alcance = alcances.CreateScope();
        var configuracion = await ImpresionesDelPos.Impresora(alcance.ServiceProvider.GetRequiredService<PosDbContext>()).ConfiguracionAsync(sucursalId, ct);
        if (string.IsNullOrWhiteSpace(configuracion.ImpresoraHost))
            return "Escribe la IP de la impresora.";
        await LeerAsync(configuracion, ct);
        return Error(sucursalId);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (var alcance = alcances.CreateScope())
                {
                    var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
                    foreach (var configuracion in await ImpresionesDelPos.Impresora(db).ConImpresoraAsync(ct))
                    {
                        var e = De(configuracion.SucursalId);
                        if (Ahora >= e.SiguientePeriodica || e.SiguientePronto <= Ahora)
                            await LeerAsync(configuracion, ct);
                    }
                }
                await _despertar.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // La base ocupada o un error inesperado no detienen la vigilancia.
                log.LogError(ex, "Error al vigilar la impresora.");
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    private async Task LeerAsync(ConfiguracionImpresion configuracion, CancellationToken ct)
    {
        var e = De(configuracion.SucursalId);
        await e.Candado.WaitAsync(ct);
        try
        {
            e.SiguientePeriodica = Ahora + TimeSpan.FromMinutes(Math.Max(1, configuracion.MinutosEntreLecturas));
            e.SiguientePronto = null;
            PrinterStatus lectura;
            try
            {
                lectura = await lector.LeerAsync(configuracion.ImpresoraHost!, ct) with { TakenUtc = Ahora };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lectura = new PrinterStatus { TakenUtc = Ahora, Errors = [ex.Message] };
            }

            using var alcance = alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var impresora = ImpresionesDelPos.Impresora(db);
            if (lectura.InkK is null && lectura.LifeCount is null)
            {
                e.Error = lectura.Errors.Count > 0 ? string.Join("; ", lectura.Errors) : "La impresora no respondió.";
                await SinRespuestaAsync(db, configuracion, e, ct);
                return;
            }
            (e.Error, e.UltimaRespuesta, e.FallandoDesde) = (null, lectura.TakenUtc, DateTime.MaxValue);
            await impresora.GuardarLecturaAsync(configuracion.SucursalId, lectura, ct);

            if (lectura.LifeCount is { } contador && e.Auditor.IsOpen)
            {
                var info = await impresora.InfoAsync(e.Auditor.Jobs, ct);
                PageAuditor.JobInfo Info(Guid id) => info.GetValueOrDefault(id, new PageAuditor.JobInfo(0, false, Finished: true));
                if (e.Auditor.OnCounter(contador, Ahora, Info) is { } conteo)
                {
                    e.BaseUtc = null;
                    await impresora.AplicarVerificacionAsync(conteo, ct);
                }
                else if (e.Auditor.WantsReading(Info))
                {
                    Pronto(e, MientrasSeVerifica);
                }
            }
        }
        finally
        {
            e.Candado.Release();
        }
    }

    /// <summary>
    /// Un aviso por cada vez que deja de responder, si pasa en horario de la tienda. El horario y el aviso son del POS: se
    /// piden por el puerto, sobre la misma base, y el aviso se guarda aquí.
    /// </summary>
    private async Task SinRespuestaAsync(PosDbContext db, ConfiguracionImpresion configuracion, Estado e, CancellationToken ct)
    {
        if (e.FallandoDesde == DateTime.MaxValue)
            e.FallandoDesde = Ahora;
        var pos = ImpresionesDelPos.Pos(db);
        if (Ahora - e.FallandoDesde < SinRespuesta || pos.FueraDeHorario(configuracion.SucursalId, Ahora))
            return;
        var desde = e.UltimaRespuesta is { } u ? $"desde las {HoraMexico.ALocal(u):HH:mm}" : $"desde que arrancó el servicio";
        if (pos.Avisar(configuracion.SucursalId, AvisoImpresiones.Impresora,
                $"Impresora sin responder\nNo contesta en {configuracion.ImpresoraHost} {desde}: ¿está apagada o cambió de IP?\n{e.Error}",
                $"impresora:sin-respuesta:{configuracion.SucursalId:N}:{e.FallandoDesde:yyyyMMddHHmm}"))
            await db.SaveChangesAsync(ct);
    }

    private void Pronto(Estado e, TimeSpan espera)
    {
        var cuando = Ahora + espera;
        if (e.SiguientePronto is not { } antes || cuando < antes)
            e.SiguientePronto = cuando;
        if (_despertar.CurrentCount == 0)
            _despertar.Release();
    }
}
