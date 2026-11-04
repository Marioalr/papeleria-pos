using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Solo las cajas dadas de alta usan la API: cada llamada trae la llave de la caja, también la de la misma PC del servicio. Sin llave solo
/// se puede preguntar si el servicio está vivo, pedir el alta y pedirle que pruebe quién es (la caja que lo encontró en la red, antes de
/// mandarle su llave). La página del servicio trae la suya, de memoria (<see cref="ConexionCaja.LlaveDeLaPagina"/>), y desde esta misma PC
/// quedan sin llave los scripts (<see cref="SinLlaveDesdeEstaPc"/>).
/// </summary>
public static partial class SeguridadCajas
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
            // De memoria (entrega 5, parte 2): el latido, el aviso y el cobro de cada caja ya no leen la base para esto.
            var llaves = contexto.RequestServices.GetService<LlavesDeCajas>();
            var deLaLlave = llaves is not null ? await llaves.CajaAsync(db, huella)
                : await db.Cajas.Where(c => c.LlaveHuella == huella && c.Activa).Select(c => (Guid?)c.Id).SingleOrDefaultAsync();
            if (deLaLlave is { } cajaId)
            {
                contexto.Items[CajaDeLaLlave] = cajaId;
                contexto.RequestServices.GetService<PresenciaCajas>()?.Visto(cajaId, db.Reloj.GetUtcNow().UtcDateTime);
            }
        }

        // La identidad, sin llave: la caja que encontró al servicio en la red le pide que pruebe quién es antes de mandarle su
        // llave (Descubrimiento); el servicio solo contesta a la red de la tienda.
        if (contexto.Items.ContainsKey(CajaDeLaLlave) || ruta.StartsWithSegments("/api/estado") || ruta.StartsWithSegments("/api/alta")
            || ruta.StartsWithSegments("/api/identidad") || EsDeLaPagina(contexto, llave) || SinLlaveDesdeEstaPc(contexto))
        {
            await siguiente(contexto);
            return;
        }
        contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await contexto.Response.WriteAsJsonAsync(new ErrorDto("Esta PC no está dada de alta como caja: el dueño la da de alta con su PIN.", "caja-sin-alta"));
    });

    /// <summary>Sin dirección es una llamada dentro del mismo proceso (las pruebas).</summary>
    internal static bool EsLaMismaPc(IPAddress? ip) =>
        ip is null || IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    private static bool EsDeLaPagina(HttpContext contexto, string llave) =>
        llave.Length > 0 && contexto.RequestServices.GetService<ConexionCaja>()?.LlaveDeLaPagina is { } propia
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(propia), Encoding.UTF8.GetBytes(llave));

    /// <summary>
    /// Sin llave, desde esta misma PC (auditoría, 2026-11-04). Antes pasaba todo: en Edge, solo escribiendo direcciones, salían los usuarios,
    /// los reportes y el hash de cada PIN; con PowerShell se cambiaba el PIN del dueño. Ahora, solo lo que usan los scripts y las guías en
    /// Edge, que no leen la llave de la caja; lo demás, como desde otra PC. Y solo si el Host es esta PC: una página de internet abierta
    /// aquí no llega con otro nombre que apunte a 127.0.0.1. La caja de la prueba con una persona sí sigue sin alta con su servicio: su base
    /// es de prueba. Lo que queda: la llave de caja.json la descifra cualquier cuenta de esta PC, y con una llave se le puede cambiar el PIN
    /// a otro (docs/plan.md, «Recomendaciones», la sesión en el servicio).
    /// </summary>
    private static bool SinLlaveDesdeEstaPc(HttpContext contexto)
    {
        var ip = contexto.Connection.RemoteIpAddress;
        if (ip is null)
            return true;
        if (!EsLaMismaPc(ip) || contexto.Request.Host.Host.ToLowerInvariant() is not ("localhost" or "127.0.0.1" or "[::1]"))
            return false;
        return contexto.RequestServices.GetService<ModoPrueba>()?.Activo == true || EsDeLosScripts(contexto.Request);
    }

    private static bool EsDeLosScripts(HttpRequest pedido)
    {
        var ruta = pedido.Path.Value ?? "";
        if (ruta.Equals("/api/sesion", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsPost(pedido.Method);
        // «Respaldar ahora», de juntar-datos-piloto.ps1; pide quién, y ese id ya no sale sin su PIN.
        if (RutaDeRespaldos().IsMatch(ruta))
            return HttpMethods.IsGet(pedido.Method) || HttpMethods.IsPost(pedido.Method);
        return HttpMethods.IsGet(pedido.Method) && (ruta.Equals("/api/cajas", StringComparison.OrdinalIgnoreCase)
            || ruta.Equals("/api/eleventa", StringComparison.OrdinalIgnoreCase) || ruta.Equals("/api/eleventa/envios", StringComparison.OrdinalIgnoreCase)
            || ruta.Equals("/api/controlprint", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^/api/cajas/[^/]+/configuracion/respaldos$", RegexOptions.IgnoreCase)]
    private static partial Regex RutaDeRespaldos();

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
