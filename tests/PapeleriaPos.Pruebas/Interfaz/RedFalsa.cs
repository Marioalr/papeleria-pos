using System.Net;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Un reloj que avanza solo cuando se le dice, con la hora local igual a la UTC: lo que se anota en la bitácora de red no depende
/// de la zona de la PC que corre las pruebas.
/// </summary>
internal sealed class RelojDeLaRed(DateTimeOffset inicio) : TimeProvider
{
    public DateTimeOffset Ahora { get; set; } = inicio;

    public override DateTimeOffset GetUtcNow() => Ahora;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Avanzar(TimeSpan tiempo) => Ahora += tiempo;

    public static RelojDeLaRed Nuevo() => new(new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.Zero));
}

/// <summary>La tarjeta de red de esta PC, como se le pida: por Wi-Fi en la red de la tienda, salvo que se cambie.</summary>
internal sealed class EquipoFalso : IRedDelEquipo
{
    public static readonly IPAddress RouterDeLaTienda = IPAddress.Parse("192.168.0.1");

    public FotoDeLaRed Foto { get; set; } = new("Wi-Fi", TipoDeConexion.WiFi, IPAddress.Parse("192.168.0.11"), 24, RouterDeLaTienda, true, "Tienda", 80,
        "50:2b:73:aa:bb:cc", Difusion: 1000, Descartados: 0);

    public FotoDeLaRed Leer(IPAddress? hacia) => Foto;
}

/// <summary>
/// Las pruebas de la red sin red: cada una contesta lo que se le diga (vacío: no contestó), al momento. Cuenta cuántas se hicieron
/// y a qué, para ver que el vigía no le pega a lo que no debe (al 9100 de la impresora).
/// </summary>
internal sealed class PruebasFalsas : IPruebasDeRed
{
    public Func<IPAddress, TimeSpan?> Ping { get; set; } = _ => TimeSpan.FromMilliseconds(3);

    public Func<string, int, TimeSpan?> Conectar { get; set; } = (_, _) => TimeSpan.FromMilliseconds(20);

    public Func<IPAddress, string, TimeSpan?> Dns { get; set; } = (_, _) => TimeSpan.FromMilliseconds(4);

    private readonly List<string> _hechas = [];

    public IReadOnlyList<string> Hechas
    {
        get
        {
            lock (_hechas)
                return [.. _hechas];
        }
    }

    /// <summary>Sin internet: no conecta a nada de afuera (el router sí contesta, salvo que se diga otra cosa).</summary>
    public PruebasFalsas SinInternet()
    {
        Conectar = (host, _) => IPAddress.TryParse(host, out var ip) && ip.GetAddressBytes()[0] == 192 ? TimeSpan.FromMilliseconds(2) : null;
        return this;
    }

    public Task<TimeSpan?> PingAsync(IPAddress ip, TimeSpan espera, CancellationToken ct = default)
    {
        Anotar($"ping {ip}");
        return Task.FromResult(Ping(ip));
    }

    public Task<TimeSpan?> ConectarAsync(string host, int puerto, TimeSpan espera, CancellationToken ct = default)
    {
        Anotar($"tcp {host}:{puerto}");
        return Task.FromResult(Conectar(host, puerto));
    }

    public Task<TimeSpan?> PreguntarDnsAsync(IPAddress servidor, string nombre, TimeSpan espera, CancellationToken ct = default)
    {
        Anotar($"dns {servidor} {nombre}");
        return Task.FromResult(Dns(servidor, nombre));
    }

    private void Anotar(string que)
    {
        lock (_hechas)
            _hechas.Add(que);
    }
}

/// <summary>Una carpeta propia en la carpeta temporal, para la bitácora de una prueba; se borra al terminar.</summary>
internal sealed class CarpetaDePrueba : IDisposable
{
    public string Ruta { get; } = Path.Combine(Path.GetTempPath(), $"red-tienda-{Guid.NewGuid():N}");

    public CarpetaDePrueba() => Directory.CreateDirectory(Ruta);

    /// <summary>Todas las líneas de los archivos de la bitácora (en orden de días), sin la fecha y la hora.</summary>
    public List<string> Lineas(string nombre = "red-caja") =>
        Directory.EnumerateFiles(Ruta, $"{nombre}-*.log").Order(StringComparer.Ordinal)
            .SelectMany(Leer)
            .Where(l => l.Length > 21)
            .Select(l => l[21..])
            .ToList();

    /// <summary>
    /// Mientras la bitácora puede estar escribiendo (en Windows, File.ReadAllLines no deja leer un archivo abierto para escribir),
    /// como la lee la bitácora: compartido.
    /// </summary>
    private static List<string> Leer(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(flujo);
        var lineas = new List<string>();
        while (lector.ReadLine() is { } linea)
            lineas.Add(linea);
        return lineas;
    }

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(Ruta);
}
