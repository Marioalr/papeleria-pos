using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using QRCoder;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El conteo completo por departamento (fase 2, entrega 3, parte 3). Los celulares no llaman a esta API: abren la página
/// /conteo del servicio, que la usa desde la misma PC.
/// </summary>
public static class ApiConteos
{
    public const string PaginaCelular = "conteo";

    public static void MapearApiConteos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/conteos-completos", (Guid sucursalId, PosDbContext db) =>
            new RegistroConteoCompleto(db).DepartamentosAsync(sucursalId));

        api.MapPost("/sucursales/{sucursalId:guid}/conteos-completos", (Guid sucursalId, SolicitudAbrirConteo s, PosDbContext db) =>
            new RegistroConteoCompleto(db).AbrirAsync(sucursalId, s));

        api.MapGet("/conteos-completos/{conteoId:guid}", (Guid conteoId, Guid usuarioId, bool? revision, PosDbContext db) =>
            new RegistroConteoCompleto(db).ConteoAsync(conteoId, usuarioId, revision ?? false));

        api.MapPost("/conteos-completos/{conteoId:guid}/capturas", (Guid conteoId, SolicitudCaptura s, PosDbContext db) =>
            new RegistroConteoCompleto(db).CapturarAsync(conteoId, s));

        api.MapPost("/conteos-completos/{conteoId:guid}/capturas/{capturaId:guid}/quitar",
            (Guid conteoId, Guid capturaId, SolicitudConteoCompleto s, PosDbContext db) => new RegistroConteoCompleto(db).QuitarAsync(conteoId, capturaId, s.UsuarioId));

        api.MapPost("/conteos-completos/{conteoId:guid}/cerrar", (Guid conteoId, SolicitudConteoCompleto s, PosDbContext db) =>
            new RegistroConteoCompleto(db).CerrarAsync(conteoId, s.UsuarioId));

        api.MapPost("/conteos-completos/{conteoId:guid}/cancelar", (Guid conteoId, SolicitudConteoCompleto s, PosDbContext db) =>
            new RegistroConteoCompleto(db).CancelarAsync(conteoId, s.UsuarioId));

        // Dónde abrir la página del conteo en el celular: la IP de esta PC en la red de la tienda y su código QR.
        api.MapGet("/conteo-celular", (HttpContext http) => Direccion(http.Connection.LocalPort is > 0 and var puerto ? puerto : 5080));
    }

    public static DireccionCelularDto Direccion(int puerto)
    {
        var direcciones = DireccionesEnLaRed().Select(ip => $"http://{ip}:{puerto}/{PaginaCelular}").ToList();
        if (direcciones.Count == 0)
            return new DireccionCelularDto([], null);
        using var generador = new QRCodeGenerator();
        using var datos = generador.CreateQrCode(direcciones[0], QRCodeGenerator.ECCLevel.M);
        return new DireccionCelularDto(direcciones, new SvgQRCode(datos).GetGraphic(5));
    }

    /// <summary>Las IPv4 de esta PC en las redes con salida (las de la tienda); sin ninguna así, las privadas que tenga.</summary>
    private static List<IPAddress> DireccionesEnLaRed()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.GetIPProperties())
            .ToList();
        static IEnumerable<IPAddress> IPv4(IPInterfaceProperties p) =>
            p.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        var conSalida = interfaces
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(IPv4).ToList();
        return (conSalida.Count > 0 ? conSalida : interfaces.SelectMany(IPv4).Where(EsPrivada)).Distinct().ToList();
    }

    private static bool EsPrivada(IPAddress ip) =>
        ip.GetAddressBytes() is [10, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..];
}
