using System.Net;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Solo las cajas dadas de alta usan la API desde la red: cada llamada trae la llave de la caja. La caja principal
/// (la misma PC del servicio) no la necesita. Sin llave solo se puede preguntar si el servicio está vivo y pedir el alta.
/// </summary>
public static class SeguridadCajas
{
    public const string Encabezado = ClienteCaja.EncabezadoLlave;
    internal const string CajaDeLaLlave = "caja-de-la-llave";

    public static IApplicationBuilder UsarLlavesDeCaja(this IApplicationBuilder app) => app.Use(async (contexto, siguiente) =>
    {
        var ruta = contexto.Request.Path;
        if (!ruta.StartsWithSegments("/api"))
        {
            await siguiente(contexto);
            return;
        }

        // Con llave, se sabe qué caja es (y que sigue viva: su latido pasa por aquí, para la alerta de caja sin conexión).
        var llave = contexto.Request.Headers[Encabezado].ToString();
        if (llave.Length > 0)
        {
            var huella = LlaveCaja.Huella(llave);
            var db = contexto.RequestServices.GetRequiredService<PosDbContext>();
            if (await db.Cajas.Where(c => c.LlaveHuella == huella && c.Activa).Select(c => (Guid?)c.Id).SingleOrDefaultAsync() is { } cajaId)
            {
                contexto.Items[CajaDeLaLlave] = cajaId;
                contexto.RequestServices.GetService<PresenciaCajas>()?.Visto(cajaId, db.Reloj.GetUtcNow().UtcDateTime);
            }
        }

        if (contexto.Items.ContainsKey(CajaDeLaLlave) || ruta.StartsWithSegments("/api/estado") || ruta.StartsWithSegments("/api/alta")
            || EsLaMismaPc(contexto.Connection.RemoteIpAddress))
        {
            await siguiente(contexto);
            return;
        }
        contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await contexto.Response.WriteAsJsonAsync(new ErrorDto("Esta PC no está dada de alta como caja: el dueño la da de alta con su PIN.", "caja-sin-alta"));
    });

    /// <summary>Sin dirección es una llamada dentro del mismo proceso (las pruebas).</summary>
    private static bool EsLaMismaPc(IPAddress? ip) =>
        ip is null || IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    /// <summary>
    /// Una caja dada de alta solo opera su propia caja: el turno, la caja de la ruta y el <c>CajaId</c> de lo que
    /// manda tienen que ser los suyos.
    /// </summary>
    public static RouteGroupBuilder SoloSuCaja(this RouteGroupBuilder grupo) =>
        grupo.AddEndpointFilter(async (contexto, siguiente) =>
        {
            var http = contexto.HttpContext;
            if (!http.Items.TryGetValue(CajaDeLaLlave, out var valor) || valor is not Guid propia)
                return await siguiente(contexto);

            var pedidas = new List<Guid>();
            if (http.Request.RouteValues.TryGetValue("cajaId", out var caja) && Guid.TryParse(caja?.ToString(), out var cajaId))
                pedidas.Add(cajaId);
            if (http.Request.RouteValues.TryGetValue("turnoId", out var turno) && Guid.TryParse(turno?.ToString(), out var turnoId))
            {
                var db = http.RequestServices.GetRequiredService<PosDbContext>();
                if (await db.Turnos.Where(t => t.Id == turnoId).Select(t => (Guid?)t.CajaId).SingleOrDefaultAsync() is { } deTurno)
                    pedidas.Add(deTurno);
            }
            foreach (var argumento in contexto.Arguments)
            {
                if (argumento?.GetType().GetProperty("CajaId")?.GetValue(argumento) is Guid enCuerpo)
                    pedidas.Add(enCuerpo);
            }

            return pedidas.All(p => p == propia)
                ? await siguiente(contexto)
                : Results.Json(new ErrorDto("Esta PC está dada de alta como otra caja.", "otra-caja"), statusCode: StatusCodes.Status403Forbidden);
        });
}
