using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El primer cobro después de arrancar tardaba 60 veces más que los demás, porque en él se compilaba todo (1.3 s en una PC rápida, 3.2 s
/// a media CPU, con la auditoría de 2026-11-04): en una PC como la caja 2 puede pasar los 2.5 s que espera la caja, y esa venta sale con el
/// folio de la caja y deja la red «lenta» un rato. Al arrancar se le da una vuelta al camino del cobro, en segundo plano, y se deshace (<see cref="ApiCaja.CalentarCobroAsync"/>).
/// Si no se puede (la base ocupada), el primer cobro tarda como antes. Se apaga con Cobro:Calentar (en desarrollo y en las pruebas).
/// </summary>
public sealed class CalentarElCobro(IServiceScopeFactory alcances, RevisionAlArrancar revision, ILogger<CalentarElCobro> log) : BackgroundService
{
    /// <summary>Para las pruebas: si le dio la vuelta (<see cref="BackgroundService.ExecuteTask"/> dice cuándo terminó).</summary>
    public bool Calento { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        if (revision.BaseDanada)
            return;
        var inicio = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await using var alcance = alcances.CreateAsyncScope();
            var s = alcance.ServiceProvider;
            Calento = await ApiCaja.CalentarCobroAsync(s.GetRequiredService<PosDbContext>(), s.GetRequiredService<CatalogoEnMemoria>(),
                s.GetRequiredService<IOptions<OpcionesTicket>>().Value, s.GetRequiredService<EnvioVentasEleventa>(), s.GetRequiredService<TimeProvider>(), ct);
            if (Calento)
                log.LogInformation("El cobro quedó listo en {Tardo}.", EnvioVentasEleventa.Milisegundos(System.Diagnostics.Stopwatch.GetElapsedTime(inicio)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Se detuvo el servicio.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nunca detiene al servicio: lo único que se pierde es que el primer cobro no tarde de más.
            log.LogWarning(ex, "No se pudo dejar listo el cobro al arrancar: el primero tardará un poco más.");
        }
    }
}
