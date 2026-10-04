using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;

namespace PapeleriaPos.Medicion;

/// <summary>
/// Vigila la cola de la WF-6590 en esta PC y manda cada trabajo impreso, medido, a la caja principal. Es el monitor de
/// Control Print (PrintMonitor) sin su base propia: solo observa, nunca pausa ni altera la impresión, y solo quita de la
/// cola lo que Windows ya imprimió y ya quedó guardado para mandarse.
/// </summary>
/// <param name="mandar">Guarda el trabajo para subirlo (la bandeja de la caja); si falla, se reintenta en la siguiente revisión.</param>
public sealed class AgenteImpresion(string cola, Func<TrabajoMedidoDto, Task> mandar, bool quitarImpresos = true) : IAsyncDisposable
{
    /// <summary>Margen antes de quitar de la cola un trabajo que terminó con error (por si se quiere reiniciar).</summary>
    public static readonly TimeSpan MargenError = TimeSpan.FromMinutes(2);

    private readonly AnalysisOptions _opciones = new();
    private readonly Channel<bool> _senales = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _alto = new();
    private readonly HashSet<(int Id, DateTime Enviado)> _mandados = [];
    private readonly Dictionary<(int Id, DateTime Enviado), DateTime> _conErrorDesde = [];
    private PrintQueueClient? _cliente;
    private PrinterChangeWatcher? _vigia;
    private PrintTicketReader? _tickets;
    private bool _conserva;
    private volatile bool _reconectar;
    private Task? _trabajo;

    /// <summary>Avisos para el registro (cola no encontrada, no conserva documentos…).</summary>
    public event Action<string>? Aviso;

    public string Cola => cola;

    public void Iniciar() => _trabajo ??= Task.Run(CorrerAsync);

    private void Revisar() => _senales.Writer.TryWrite(true);

    private async Task CorrerAsync()
    {
        var ct = _alto.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Conectar();
                await ConciliarAsync();
                while (!ct.IsCancellationRequested)
                {
                    // Revisión de respaldo: cada minuto (por si se pierde una notificación o falló un envío).
                    using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    espera.CancelAfter(TimeSpan.FromMinutes(1));
                    try
                    {
                        await _senales.Reader.ReadAsync(espera.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                    // Pequeña espera para juntar las ráfagas de cambios de un mismo trabajo.
                    await Task.Delay(300, ct);
                    await ConciliarAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Aviso?.Invoke($"La vigilancia de «{cola}» se detuvo ({ex.Message}); se reintenta en 15 s.");
                Desconectar();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        Desconectar();
    }

    private void Conectar()
    {
        if (_vigia is not null)
            return;
        _cliente = new PrintQueueClient(cola);
        var (driver, _, conserva) = _cliente.GetPrinterInfo();
        _conserva = conserva;
        if (!conserva)
            Aviso?.Invoke($"La cola «{cola}» no conserva los documentos impresos: el costo se estimará por páginas.");
        _tickets = new PrintTicketReader(cola);
        _vigia = new PrinterChangeWatcher(cola, Revisar, _ =>
        {
            _reconectar = true;
            Revisar();
        });
        Aviso?.Invoke($"Vigilando «{cola}» (driver {driver}, conserva impresos: {conserva}).");
    }

    private void Desconectar()
    {
        _vigia?.Dispose();
        _vigia = null;
        _tickets?.Dispose();
        _tickets = null;
        _cliente?.Dispose();
        _cliente = null;
    }

    private async Task ConciliarAsync()
    {
        if (_reconectar)
        {
            _reconectar = false;
            Desconectar();
        }
        if (_cliente is null)
            Conectar();
        var vistos = new HashSet<(int, DateTime)>();
        foreach (var job in _cliente!.GetJobs())
        {
            var clave = (job.JobId, job.SubmittedUtc);
            vistos.Add(clave);
            var impreso = job.Status.HasFlag(SpoolJobStatus.Printed) && !job.Status.HasFlag(SpoolJobStatus.Printing);
            var conError = _conserva && TerminoConError(job);

            // El contenido se lee cuando Windows ya lo imprimió (antes los datos están incompletos). Sin "conservar
            // impresos" ya no habrá datos después, así que se estima por páginas en cuanto termina de encolarse.
            if (!_mandados.Contains(clave) && !job.IsSpooling && !job.IsDeleting && (!_conserva || impreso || conError))
            {
                var medido = Medir(job, leerContenido: _conserva);
                await mandar(Trabajo(job, medido, conError));
                _mandados.Add(clave);
            }

            var quitable = impreso;
            if (conError)
            {
                if (!_conErrorDesde.TryGetValue(clave, out var desde))
                    _conErrorDesde[clave] = desde = DateTime.UtcNow;
                quitable = DateTime.UtcNow - desde >= MargenError;
            }
            if (quitable && quitarImpresos && _conserva && _mandados.Contains(clave) && _cliente.DeleteJob(job.JobId))
                _conErrorDesde.Remove(clave);
        }
        _mandados.RemoveWhere(c => !vistos.Contains(c));
        foreach (var c in _conErrorDesde.Keys.Where(c => !vistos.Contains(c)).ToList())
            _conErrorDesde.Remove(c);
    }

    /// <summary>Windows ya no lo manda pero la impresora reportó un problema (atasco, cancelación en el panel).</summary>
    private static bool TerminoConError(SpoolJob j) =>
        j.Status.HasFlag(SpoolJobStatus.Complete) && j.Status.HasFlag(SpoolJobStatus.Error) &&
        !j.Status.HasFlag(SpoolJobStatus.Printing) && !j.Status.HasFlag(SpoolJobStatus.Printed) && !j.IsDeleting;

    private JobAnalysis Medir(SpoolJob job, bool leerContenido)
    {
        var ajustes = _tickets!.Read(job.DevMode);
        var paginas = Math.Max(job.TotalPages, job.PagesPrinted);
        if (!leerContenido)
            return JobAnalyzer.Estimate(ajustes, paginas, _opciones, "La cola no conserva documentos impresos: costo estimado por páginas.");
        try
        {
            using var datos = _cliente!.OpenJobData(job.JobId);
            return JobAnalyzer.Analyze(datos, ajustes, paginas, _opciones, _tickets.Read);
        }
        catch (Exception ex)
        {
            return JobAnalyzer.Estimate(ajustes, paginas, _opciones, $"No se pudo leer el contenido: {ex.Message}");
        }
    }

    private TrabajoMedidoDto Trabajo(SpoolJob job, JobAnalysis a, bool conError)
    {
        var s = a.Settings;
        return new TrabajoMedidoDto(
            IdFijo(Environment.MachineName, cola, job.JobId, job.SubmittedUtc), Guid.Empty, null, Environment.MachineName, cola, job.JobId,
            job.Document, job.UserName, DateTime.UtcNow, conError ? EstadoTrabajo.TerminoConError : EstadoTrabajo.Impreso,
            new AjustesTrabajoDto(s.Quality, s.MediaType, s.MediaSizeName, s.MediaWidthMm, s.MediaHeightMm, s.Color, s.Duplex, s.Copies,
                s.PagesPerSheet, s.FromPrintTicket),
            a.Pages.Select(p => new CoberturaPaginaDto(p.C, p.M, p.Y, p.K, p.Gray, p.AreaSqIn, p.HasColor)).ToList(),
            a.Level == AnalysisLevel.Measured,
            conError ? "Terminó con error en la impresora (atasco o cancelación)." : a.Note,
            a.Thumbnail is { } png ? Convert.ToBase64String(png) : null, job.SubmittedUtc);
    }

    /// <summary>El mismo trabajo siempre tiene el mismo ID: si se manda otra vez (la caja se reinició), no se duplica.</summary>
    public static Guid IdFijo(string pc, string cola, int jobId, DateTime enviadoUtc) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{pc}|{cola}|{jobId}|{enviadoUtc:O}")).AsSpan(0, 16));

    public async ValueTask DisposeAsync()
    {
        _alto.Cancel();
        _senales.Writer.TryComplete();
        if (_trabajo is not null)
        {
            try
            {
                await _trabajo.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Desconectar();
        _alto.Dispose();
    }
}
