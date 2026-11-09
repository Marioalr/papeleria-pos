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

    /// <summary>
    /// Solo el contador (con su color, B/N y serie), para leerlo seguido mientras la impresora se mueve (las tarjetas «Copias o celular»,
    /// 2026-11-08). Si el lector no lo tiene aparte, la lectura completa.
    /// </summary>
    Task<PrinterStatus> LeerContadorAsync(string host, CancellationToken ct) => LeerAsync(host, ct);
}

public sealed class LectorImpresora : ILectorImpresora
{
    public Task<PrinterStatus> LeerAsync(string host, CancellationToken ct) => new PrinterStatusReader(host).ReadAsync(ct);

    public Task<PrinterStatus> LeerContadorAsync(string host, CancellationToken ct) => new PrinterStatusReader(host).ReadCounterAsync(ct);
}

/// <summary>
/// Las impresoras de cada sucursal, vistas desde la caja principal (cada una por su cuenta desde el 2026-11-03): las lee cada pocos
/// minutos y poco después de cada impresión (los contadores se actualizan al terminar), guarda sus lecturas y verifica con el contador
/// de cada una las hojas que de verdad salieron de ella (<see cref="PageAuditor"/>): un trabajo de la L5590 nunca se cuenta con el
/// contador de una WF-6590. La verificación vive en memoria: si el servicio se reinicia a media impresión, ese lote simplemente no se
/// verifica.
/// <para>
/// Con cada lectura revisa también sus tarjetas «Copias o celular» (<see cref="TarjetasDelContador"/>, 2026-11-08): mientras haya caras
/// que acomodar (la impresora se mueve, o se acaba de registrar algo), la lee cada <see cref="AjustesDelContador.MientrasSeMueve"/> solo
/// el contador; si no, a su paso de siempre. Con Control Print en la caja principal (<paramref name="controlPrint"/>), no en las que comparte
/// con él.
/// </para>
/// </summary>
public sealed class VigilanciaImpresora(IServiceScopeFactory alcances, ILectorImpresora lector, TimeProvider reloj, ILogger<VigilanciaImpresora> log,
    ControlPrintEnElServicio? controlPrint = null)
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

        /// <summary>La siguiente lectura de solo el contador, mientras sus tarjetas tienen caras que acomodar.</summary>
        public DateTime? SiguienteRapida;
        public DateTime? UltimaRespuesta;
        public DateTime FallandoDesde = DateTime.MaxValue;
        public string? Error;
        public string? OtraSerie;
    }

    /// <summary>Lo de cada impresora, por su Id.</summary>
    private readonly ConcurrentDictionary<Guid, Estado> _impresoras = new();
    private readonly SemaphoreSlim _despertar = new(0, 1);

    /// <summary>La lectura que va de cada impresora: cada una por su cuenta, para que una apagada (unos 12 s sin respuesta) no atrase a las demás.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _leyendo = new();

    private DateTime Ahora => reloj.GetUtcNow().UtcDateTime;
    private Estado De(Guid impresoraId) => _impresoras.GetOrAdd(impresoraId, _ => new Estado());

    /// <summary>Por qué falló la última lectura de esa impresora (vacío si respondió o no se ha leído).</summary>
    public string? Error(Guid impresoraId) => _impresoras.TryGetValue(impresoraId, out var e) ? e.Error : null;

    /// <summary>Si en la dirección de esa impresora contesta otra serie: qué pasa y qué hacer (vacío si no, o si no se ha leído).</summary>
    public string? OtraSerie(Guid impresoraId) => _impresoras.TryGetValue(impresoraId, out var e) ? e.OtraSerie : null;

    /// <summary>
    /// Llegó un trabajo del agente: entra al lote que se verifica con el contador. La base es la última lectura tomada
    /// antes de que se mandara a la cola; sin ella (o si el trabajo se mandó antes de la base del lote abierto), el lote no
    /// se puede verificar y se descarta: con el contador se puede no ver un faltante, pero nunca inventarlo.
    /// </summary>
    public async Task TrabajoRegistradoAsync(TrabajoImpresion t, CancellationToken ct = default)
    {
        if (t.Tipo != TipoTrabajoImpresion.Impresion || t.EnviadoUtc is not { } enviado)
            return;
        using var alcance = alcances.CreateScope();
        var servicio = ImpresionesDelPos.Impresora(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
        // El lote de la impresora de donde salió: cada una con su contador.
        var impresora = t.ImpresoraId is { } id ? await servicio.PorIdAsync(id, ct) : await servicio.PrimeraAsync(t.SucursalId, ct: ct);
        if (impresora is null)
            return;
        var e = De(impresora.Id);
        var antes = await servicio.AntesDeAsync(impresora, enviado - MargenDeReloj, ct);
        await e.Candado.WaitAsync(ct);
        try
        {
            if (e.Auditor.IsOpen && (e.BaseUtc is not { } baseUtc || enviado - MargenDeReloj < baseUtc))
            {
                // Sin el nombre del documento: es de un cliente, y el registro sale de la tienda en la copia del piloto.
                log.LogInformation("Lote de impresión sin verificar: el trabajo {Id} se mandó antes de la lectura base.", t.Id);
                e.Auditor = new PageAuditor();
                e.BaseUtc = null;
                return;
            }
            if (!e.Auditor.IsOpen)
                e.BaseUtc = antes?.TomadaUtc;
            e.Auditor.JobDetected(t.Id, antes?.Contador, Ahora);
            e.Auditor.JobFinished(Ahora);
            Pronto(e, TrasImprimir);
            // Con sus tarjetas puestas, el contador de cerca: lo que se mandó se acomoda contra lo que cuenta.
            if (AjustesDelContador.De(impresora).Activas)
                Rapida(e);
        }
        finally
        {
            e.Candado.Release();
        }
    }

    /// <summary>Lee ya una impresora (botón "Leer ahora" o al cambiar cómo se lee). Devuelve el error si no respondió.</summary>
    public async Task<string?> LeerAhoraAsync(Guid impresoraId, CancellationToken ct = default)
    {
        using var alcance = alcances.CreateScope();
        var impresora = await ImpresionesDelPos.Impresora(alcance.ServiceProvider.GetRequiredService<PosDbContext>()).PorIdAsync(impresoraId, ct);
        if (impresora is null)
            return "Esa impresora no existe.";
        if (string.IsNullOrWhiteSpace(impresora.Host))
            return "Escribe la IP de la impresora.";
        await LeerAsync(impresora, ct);
        return Error(impresoraId);
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
                    foreach (var impresora in await ImpresionesDelPos.Impresora(db).ConImpresoraAsync(ct))
                    {
                        var e = De(impresora.Id);
                        var completa = Ahora >= e.SiguientePeriodica || e.SiguientePronto <= Ahora;
                        if ((completa || e.SiguienteRapida <= Ahora) && (!_leyendo.TryGetValue(impresora.Id, out var va) || va.IsCompleted))
                            _leyendo[impresora.Id] = EnSuVueltaAsync(impresora, !completa, ct);
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
        await Task.WhenAll(_leyendo.Values).ContinueWith(_ => { }, CancellationToken.None);
    }

    /// <summary>La lectura de una impresora en la vuelta de la vigilancia: lo que falle se registra y la vigilancia sigue.</summary>
    private async Task EnSuVueltaAsync(Impresora impresora, bool soloElContador, CancellationToken ct)
    {
        try
        {
            await LeerAsync(impresora, ct, soloElContador);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // La base ocupada o un error inesperado no detienen la vigilancia: a la siguiente vuelta se intenta otra vez.
            log.LogError(ex, "Error al leer la impresora {Numero}.", impresora.Numero);
        }
    }

    private async Task LeerAsync(Impresora impresora, CancellationToken ct, bool soloElContador = false)
    {
        var e = De(impresora.Id);
        await e.Candado.WaitAsync(ct);
        try
        {
            e.SiguienteRapida = null;
            if (soloElContador)
            {
                await LeerElContadorAsync(impresora, e, ct);
                return;
            }
            e.SiguientePeriodica = Ahora + TimeSpan.FromMinutes(Math.Max(1, impresora.MinutosEntreLecturas));
            e.SiguientePronto = null;
            PrinterStatus lectura;
            try
            {
                lectura = await lector.LeerAsync(impresora.Host!, ct) with { TakenUtc = Ahora };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lectura = new PrinterStatus { TakenUtc = Ahora, Errors = [ex.Message] };
            }

            using var alcance = alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var servicio = ImpresionesDelPos.Impresora(db);
            if (lectura.InkK is null && lectura.LifeCount is null)
            {
                // Lo técnico (SNMP, la página, en inglés) va al registro; en la pantalla, qué revisar.
                log.LogWarning("La impresora {Numero} no respondió en {Host}: {Detalle}", impresora.Numero, impresora.Host, string.Join("; ", lectura.Errors));
                e.OtraSerie = null;
                e.Error = $"No contesta en {impresora.Host}: revisa que esté prendida y conectada, y que su IP siga siendo esa. Se vuelve a intentar sola.";
                await SinRespuestaAsync(db, impresora, e, ct);
                return;
            }
            if (lectura.Serial is null && e.OtraSerie is not null)
            {
                // Sin su serie no se sabe si ya se corrigió la IP: mientras contestaba otra, tampoco se guarda.
                e.Auditor = new PageAuditor();
                e.BaseUtc = null;
                return;
            }
            if (lectura.Errors.Count > 0)
                log.LogInformation("La impresora {Numero} contestó a medias: {Detalle}", impresora.Numero, string.Join("; ", lectura.Errors));
            (e.Error, e.UltimaRespuesta, e.FallandoDesde) = (null, lectura.TakenUtc, DateTime.MaxValue);
            (_, e.OtraSerie) = await servicio.GuardarLecturaAsync(impresora, lectura, ct);
            if (e.OtraSerie is not null)
            {
                // Su contador es de otra impresora: el lote abierto no se puede verificar (nunca se inventa un faltante).
                if (e.Auditor.IsOpen)
                    log.LogInformation("Lote de impresión sin verificar: en la IP de la impresora {Numero} contesta otra serie.", impresora.Numero);
                e.Auditor = new PageAuditor();
                e.BaseUtc = null;
                return;
            }

            await DespuesDeLeerAsync(db, impresora, e, lectura, ct);
        }
        finally
        {
            e.Candado.Release();
        }
    }

    /// <summary>
    /// Solo el contador, mientras sus tarjetas tienen caras que acomodar (2026-11-08). Se guarda si el contador se movió (con los niveles de
    /// la última lectura completa: esta no los lee); si no contesta, se espera a la lectura de siempre.
    /// </summary>
    private async Task LeerElContadorAsync(Impresora impresora, Estado e, CancellationToken ct)
    {
        PrinterStatus lectura;
        try
        {
            lectura = await lector.LeerContadorAsync(impresora.Host!, ct) with { TakenUtc = Ahora };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation("La impresora {Numero} no dio su contador: {Detalle}", impresora.Numero, ex.Message);
            return;
        }
        if (lectura.LifeCount is null || e.OtraSerie is not null)
            return;
        using var alcance = alcances.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var servicio = ImpresionesDelPos.Impresora(db);
        var ultima = await servicio.UltimaAsync(impresora, ct);
        if (ultima is null || ultima.Contador != lectura.LifeCount || ultima.ContadorColor != lectura.ColorCount)
        {
            lectura = lectura with
            {
                InkK = ultima?.TintaK, InkC = ultima?.TintaC, InkM = ultima?.TintaM, InkY = ultima?.TintaY, Maintenance = ultima?.Mantenimiento,
            };
            (_, e.OtraSerie) = await servicio.GuardarLecturaAsync(impresora, lectura, ct);
            if (e.OtraSerie is not null)
            {
                e.Auditor = new PageAuditor();
                e.BaseUtc = null;
                return;
            }
        }
        await DespuesDeLeerAsync(db, impresora, e, lectura, ct);
    }

    /// <summary>Con una lectura de su contador: el lote que se verifica y sus tarjetas «Copias o celular».</summary>
    private async Task DespuesDeLeerAsync(PosDbContext db, Impresora impresora, Estado e, PrinterStatus lectura, CancellationToken ct)
    {
        var servicio = ImpresionesDelPos.Impresora(db);
        if (lectura.LifeCount is { } contador && e.Auditor.IsOpen)
        {
            var info = await servicio.InfoAsync(e.Auditor.Jobs, ct);
            PageAuditor.JobInfo Info(Guid id) => info.GetValueOrDefault(id, new PageAuditor.JobInfo(0, false, Finished: true));
            if (e.Auditor.OnCounter(contador, Ahora, Info) is { } conteo)
            {
                e.BaseUtc = null;
                await servicio.AplicarVerificacionAsync(conteo, impresora, ct);
            }
            else if (e.Auditor.WantsReading(Info))
            {
                Pronto(e, MientrasSeVerifica);
            }
        }
        if (lectura.LifeCount is null)
            return;
        try
        {
            if (await new TarjetasDelContador(db, ImpresionesDelPos.Pos(db)).RevisarAsync(impresora.Id, lectura, controlPrint?.Cola.Configurado == true, ct))
                Rapida(e);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Lo de las tarjetas no detiene la vigilancia: se vuelve a revisar con la siguiente lectura.
            log.LogError(ex, "Error al revisar las tarjetas del contador de la impresora {Numero}.", impresora.Numero);
        }
    }

    private void Rapida(Estado e)
    {
        var cuando = Ahora + AjustesDelContador.MientrasSeMueve;
        if (e.SiguienteRapida is not { } antes || cuando < antes)
            e.SiguienteRapida = cuando;
    }

    /// <summary>
    /// Un aviso por cada vez que una impresora deja de responder, si pasa en horario de la tienda (con más de una, dice cuál). El horario
    /// y el aviso son del POS: se piden por el puerto, sobre la misma base, y el aviso se guarda aquí.
    /// </summary>
    private async Task SinRespuestaAsync(PosDbContext db, Impresora impresora, Estado e, CancellationToken ct)
    {
        if (e.FallandoDesde == DateTime.MaxValue)
            e.FallandoDesde = Ahora;
        var pos = ImpresionesDelPos.Pos(db);
        if (Ahora - e.FallandoDesde < SinRespuesta || pos.FueraDeHorario(impresora.SucursalId, Ahora))
            return;
        var desde = e.UltimaRespuesta is { } u ? $"desde las {HoraMexico.ALocal(u):HH:mm}" : $"desde que arrancó el servicio";
        var varias = (await ImpresionesDelPos.Impresora(db).ImpresorasAsync(impresora.SucursalId, ct: ct)).Count > 1;
        if (pos.Avisar(impresora.SucursalId, AvisoImpresiones.Impresora,
                $"Impresora {(varias ? $"{impresora.Nombre} " : "")}sin responder\nNo contesta en {impresora.Host} {desde}: ¿está apagada o cambió de IP?",
                $"impresora:sin-respuesta:{impresora.Id:N}:{e.FallandoDesde:yyyyMMddHHmm}"))
            await db.SaveChangesAsync(ct);
    }

    private void Pronto(Estado e, TimeSpan espera)
    {
        var cuando = Ahora + espera;
        if (e.SiguientePronto is not { } antes || cuando < antes)
            e.SiguientePronto = cuando;
        // Dos impresoras pueden llegar juntas (cada una con su candado): mirar y soltar no es atómico.
        if (_despertar.CurrentCount == 0)
        {
            try
            {
                _despertar.Release();
            }
            catch (SemaphoreFullException)
            {
                // La otra ya la despertó.
            }
        }
    }
}
