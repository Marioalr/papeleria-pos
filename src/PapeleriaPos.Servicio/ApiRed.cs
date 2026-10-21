using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La red local de la caja principal: sus redes (de cable y Wi-Fi), para no contestar sin llave a quien no es de la tienda.
/// Se leen a lo más cada <see cref="Vigencia"/> (una pregunta repetida sin parar no recorre las tarjetas cada vez). Las pruebas
/// ponen las suyas.
/// </summary>
public sealed class RedLocalDelServicio(Func<IReadOnlyList<(IPAddress Direccion, int Prefijo)>>? subredes = null)
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(30);

    private sealed record Leidas(IReadOnlyList<(IPAddress Direccion, int Prefijo)> Subredes, long Desde);

    /// <summary>Se cambia de un jalón: la leen a la vez el UDP y las peticiones.</summary>
    private volatile Leidas? _leidas;

    public IReadOnlyList<(IPAddress Direccion, int Prefijo)> Subredes()
    {
        if (_leidas is { } leidas && System.Diagnostics.Stopwatch.GetElapsedTime(leidas.Desde) < Vigencia)
            return leidas.Subredes;
        var nuevas = (subredes ?? RedLocal.Subredes)();
        _leidas = new Leidas(nuevas, System.Diagnostics.Stopwatch.GetTimestamp());
        return nuevas;
    }

    /// <summary>Si quien pregunta es de la red de la tienda (o de esta misma PC).</summary>
    public bool EsDeLaRedLocal(IPAddress? quien) => RedLocal.EsDeLaRedLocal(quien, Subredes());
}

/// <summary>
/// La red de la tienda en el POS, del lado del servicio (docs/arquitectura.md, «La red de la matriz»): que una caja pruebe que
/// este es su servicio antes de cambiarse a él (<see cref="Descubrimiento"/>), y el resumen de la bitácora de red de la caja
/// principal para Configuración › Acerca de.
/// </summary>
public static class ApiRed
{
    public static void MapearApiRed(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores();

        // Sin llave (la caja todavía no sabe si este es su servicio, y no le manda su llave a quien no lo ha probado), solo desde
        // la red de la tienda: la prueba de que este servicio dio de alta a esa caja, hecha con la huella de su llave sobre un reto
        // de la caja y la dirección en que llegó la pregunta. No dice nada más: ni de qué caja es la huella ni la huella.
        api.MapGet("/identidad", async (Guid? caja, string? reto, HttpContext http, PosDbContext db, RedLocalDelServicio redLocal, Lazy<LlaveDelServicio> llave) =>
        {
            if (!redLocal.EsDeLaRedLocal(http.Connection.RemoteIpAddress))
                return Results.Json(new ErrorDto("Solo desde la red de la tienda.", "fuera-de-la-red"), statusCode: StatusCodes.Status403Forbidden);
            if (caja is not { } cajaId || !Descubrimiento.EsRetoValido(reto))
                return Results.Json(new ErrorDto("Falta la caja o el reto.", "sin-reto"), statusCode: StatusCodes.Status400BadRequest);
            if (http.Connection.LocalIpAddress is not { } local)
                return Results.Json(new ErrorDto("No se sabe en qué dirección llegó la pregunta.", "sin-direccion"), statusCode: StatusCodes.Status503ServiceUnavailable);
            var huella = await db.Cajas.AsNoTracking().Where(c => c.Id == cajaId && c.Activa && c.LlaveHuella != null).Select(c => c.LlaveHuella).SingleOrDefaultAsync();
            if (huella is null)
                return Results.Json(new ErrorDto("Esa caja no está dada de alta en este servicio.", "caja-sin-alta"), statusCode: StatusCodes.Status404NotFound);
            var direccion = Descubrimiento.Direccion(local, http.Connection.LocalPort);
            var prueba = Descubrimiento.Prueba(huella, cajaId, reto!, direccion);
            // Y lo mismo firmado con la llave propia del servicio: una copia de la base en otra PC no la tiene.
            return Results.Ok(new IdentidadServicioDto(prueba, llave.Value.Publica, llave.Value.Firmar(Descubrimiento.MensajeFirmado(cajaId, reto!, direccion, prueba))));
        });

        // Con llave (o desde esta PC): las últimas caídas de la bitácora de red de la caja principal y lo que ve ahora.
        api.MapGet("/red", (HttpContext http) =>
        {
            var vigia = http.RequestServices.GetService<VigiaDeLaRed>();
            if (vigia is null)
                return new ResumenDeRedDto([], null, null);
            return new ResumenDeRedDto(
                vigia.Bitacora.Caidas().Select(c => new CaidaDeRedDto(c.Que, c.Desde, c.Hasta, c.FinAproximado)).ToList(), vigia.Bitacora.Carpeta, vigia.Estado);
        });
    }
}

/// <summary>
/// El vigía de la red en la caja principal: cada medio minuto revisa si hay internet y si contesta su router (lo dice en su
/// estado, para la franja de las cajas) y lo anota en su bitácora de red (<c>red-servicio-AAAA-MM-DD.log</c>, junto a la base),
/// también cuándo cada caja dejó de latir y cuándo volvió.
/// </summary>
public sealed class VigiaDeRedDelServicio(VigiaDeLaRed vigia) : BackgroundService
{
    /// <summary>Aparte desde el principio: leer la bitácora de los días anteriores no detiene el arranque del servicio.</summary>
    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => vigia.VigilarAsync(ct), CancellationToken.None);
}

/// <summary>
/// Cuándo cada caja deja de latir y cuándo vuelve (su latido llega cada 5 s): la vista de la caja principal en la bitácora de red.
/// Una caja que todavía no ha latido desde que arrancó el servicio no se cuenta.
/// </summary>
public sealed class CajasQueLaten(PresenciaCajas presencia, IServiceScopeFactory alcances, TimeProvider reloj)
{
    /// <summary>Sin latido en este rato, la caja dejó de latir (cerrada o sin red).</summary>
    public static readonly TimeSpan Callada = TimeSpan.FromSeconds(30);

    private readonly Dictionary<Guid, bool> _vivas = [];

    public async Task RevisarAsync(BitacoraDeRed bitacora, CancellationToken ct)
    {
        List<(Guid Id, string Nombre)> cajas;
        using (var alcance = alcances.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            cajas = (await db.Cajas.AsNoTracking().Where(c => c.Activa && c.LlaveHuella != null).Select(c => new { c.Id, c.Nombre }).ToListAsync(ct))
                .Select(c => (c.Id, c.Nombre)).ToList();
        }
        var ahora = reloj.GetUtcNow().UtcDateTime;
        foreach (var (id, nombre) in cajas)
        {
            if (presencia.UltimaVez(id) is not { } vista)
                continue;
            var viva = ahora - vista < Callada;
            if (_vivas.TryGetValue(id, out var antes) && antes == viva)
                continue;
            _vivas[id] = viva;
            // Una caja en un Wi-Fi malo puede dejar de latir y volver a cada rato: la primera vez en el periodo del resumen se anota,
            // las demás se cuentan (el resumen dice cuántas).
            if (!viva)
                bitacora.AnotarOContar($"«{nombre}» dejó de latir", $"«{nombre}» dejó de latir: su último latido fue a las "
                    + $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(vista, DateTimeKind.Utc), reloj.LocalTimeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture)} "
                    + "(se cerró o perdió la red).");
            else if (antes is false)
                bitacora.AnotarOContar($"«{nombre}» volvió a latir", $"«{nombre}» volvió a latir.");
            else
                bitacora.Anotar($"«{nombre}» está conectada.");
        }
    }
}

/// <summary>Lo de la red en la configuración del servicio («Red» en appsettings.json).</summary>
public static class ConfiguracionDeRed
{
    /// <summary>
    /// «Red:Internet»: a qué se conecta para saber si hay internet («1.1.1.1:443»); sin nada, a dos muy conocidos por su IP.
    /// </summary>
    public static IReadOnlyList<(string Host, int Puerto)> Internet(IConfiguration configuracion)
    {
        var lista = configuracion.GetSection("Red:Internet").Get<string[]>() ?? [];
        var servidores = lista.Select(Servidor).OfType<(string, int)>().ToList();
        return servidores.Count > 0 ? servidores : OpcionesDelVigia.InternetPorOmision;
    }

    /// <summary>
    /// «Red:Equipos»: otros equipos a los que se les hace ping, por su IP (el grabador, el módem, una impresora). En el piloto, también
    /// la PC de eleventa (la caja principal de hoy) si está por IP: solo ping, nunca una conexión a su Firebird (cada una que se
    /// abre y se cierra sin hablarle queda en su registro).
    /// </summary>
    public static IReadOnlyList<(string Que, IPAddress Ip)> Equipos(IConfiguration configuracion, OpcionesEleventa? eleventa = null)
    {
        var equipos = configuracion.GetSection("Red:Equipos").GetChildren()
            .Select(e => (Que: e.Key, Ip: IPAddress.TryParse(e.Value, out var ip) ? ip : null))
            .Where(e => e.Ip is not null)
            .Select(e => (e.Que, e.Ip!))
            .ToList();
        if (eleventa is { Activo: true } or { Escribir: true } && IPAddress.TryParse(eleventa.Servidor, out var deEleventa)
            && !IPAddress.IsLoopback(deEleventa) && equipos.All(e => !e.Item2.Equals(deEleventa)))
            equipos.Add(("la PC de eleventa", deEleventa));
        return equipos;
    }

    /// <summary>En el piloto, la PC de eleventa por nombre (Eleventa:Servidor): con el router trabado no se encuentra.</summary>
    public static IEnumerable<string> PorNombre(OpcionesEleventa eleventa)
    {
        if (eleventa is { Activo: true } or { Escribir: true } && eleventa.Servidor is { Length: > 0 } servidor && !RedLocal.EsIp(servidor)
            && !string.Equals(servidor, "localhost", StringComparison.OrdinalIgnoreCase))
            yield return $"La PC de eleventa está configurada por nombre («{servidor}»): si el router se traba, no se encuentra. Mejor por su IP.";
    }

    /// <summary>«host:puerto»; sin puerto, el de HTTPS.</summary>
    private static (string, int)? Servidor(string texto)
    {
        var partes = texto.Trim().Split(':');
        return partes switch
        {
            [var host] when host.Length > 0 => (host, 443),
            [var host, var puerto] when host.Length > 0 && int.TryParse(puerto, out var p) && p is > 0 and <= 65535 => (host, p),
            _ => null,
        };
    }
}
