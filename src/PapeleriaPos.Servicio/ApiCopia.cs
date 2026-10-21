using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La copia de cada caja al día (entrega 5, parte 2, docs/propuesta-interfaz.md, «Rendimiento»): la caja principal avisa por
/// SSE cuando sube una versión y la caja pide solo lo que cambió desde la suya. Con la llave de la caja y solo para su caja,
/// como la copia completa. Una caja de antes no usa nada de esto: sigue bajando la copia completa.
/// </summary>
public static class ApiCopia
{
    /// <summary>Cada cuánto se revisan las versiones para avisar (de memoria: <see cref="VersionesDeLaCopia"/>).</summary>
    private static readonly TimeSpan Revision = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Cada cuánto, sin cambios, va un comentario «vivo» (<c>Avisos:SegundosVivo</c>; 15 s): así la caja nota en segundos una
    /// conexión que quedó medio muerta (no le llega nada) y se vuelve a conectar.
    /// </summary>
    public const double SegundosVivo = 15;

    public static void MapearApiCopia(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Lo que cambió desde la versión de la caja (y desde su versión de existencias, y las de lo más vendido y lo elegido que
        // usa su búsqueda); si no se puede dar desde ahí, lo dice y la caja baja la copia completa.
        api.MapGet("/cajas/{cajaId:guid}/cambios", (Guid cajaId, long desde, long existencias, [FromQuery(Name = "base")] Guid laBase, int formato,
                PosDbContext db, CatalogoEnMemoria catalogo, VersionesDeLaCopia versiones, IOptions<OpcionesTicket> opciones, CancellationToken ct,
                long vendidos = 0, long elegidos = 0) =>
            CopiaDeLaCaja.CambiosAsync(db, catalogo, versiones, cajaId, desde, existencias, laBase, formato, opciones.Value, ct, vendidos, elegidos));

        // El aviso: un canal de solo ida (text/event-stream) con las versiones al conectarse y cada vez que cambia alguna.
        api.MapGet("/cajas/{cajaId:guid}/avisos", (HttpContext http, VersionesDeLaCopia versiones, IConfiguration configuracion,
                IHostApplicationLifetime vida) =>
            AvisarAsync(http, versiones, TimeSpan.FromSeconds(configuracion.GetValue("Avisos:SegundosVivo", SegundosVivo)), vida.ApplicationStopping));
    }

    /// <summary>
    /// Las versiones al conectarse y cada vez que cambia alguna («event: versiones»), y un comentario «: vivo» cuando pasa un
    /// rato sin nada. Termina cuando la caja se va o el servicio se detiene (no lo detiene esperando a que se vaya).
    /// </summary>
    private static async Task AvisarAsync(HttpContext http, VersionesDeLaCopia versiones, TimeSpan vivo, CancellationToken deteniendo)
    {
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deteniendo);
        var ct = fin.Token;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        VersionesCopiaDto? avisadas = null;
        var ultimo = Stopwatch.GetTimestamp();
        try
        {
            await http.Response.StartAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var actuales = await versiones.ActualesAsync(ct);
                if (actuales != avisadas)
                {
                    await EscribirAsync(http, $"event: versiones\ndata: {JsonSerializer.Serialize(actuales, JsonSerializerOptions.Web)}\n\n", ct);
                    (avisadas, ultimo) = (actuales, Stopwatch.GetTimestamp());
                }
                else if (Stopwatch.GetElapsedTime(ultimo) >= vivo)
                {
                    await EscribirAsync(http, ": vivo\n\n", ct);
                    ultimo = Stopwatch.GetTimestamp();
                }
                await Task.Delay(Revision, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // La caja se fue (o el servicio se detiene): se vuelve a conectar sola.
        }
        catch (IOException)
        {
            // Lo mismo, visto al escribir.
        }
    }

    private static async Task EscribirAsync(HttpContext http, string texto, CancellationToken ct)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(texto), ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
