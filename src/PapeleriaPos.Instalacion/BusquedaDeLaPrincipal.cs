using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>Lo que contestó una caja principal en GET /api/estado.</summary>
public sealed record EstadoDeLaPrincipal(Uri Direccion, bool Contesta, string? Version, IReadOnlyList<string> Sucursales, string? Error)
{
    /// <summary>Si esa PC está instalada como «La única caja» (<c>unaSolaCaja</c>; si no lo dice, no): no acepta otras cajas.</summary>
    public bool UnaSolaCaja { get; init; }

    /// <summary>Si esa caja principal todavía no tiene tienda (<c>tiendaNueva</c>; si no lo dice, no): falta terminar su instalación.</summary>
    public bool TiendaNueva { get; init; }

    /// <summary>Por qué esta caja todavía no se puede dar de alta con esa caja principal; vacío si sí se puede (o si no contestó).</summary>
    public string? Aviso => !Contesta
        ? null
        : UnaSolaCaja
            ? "Esa PC está instalada como «La única caja»: no acepta otras cajas. Para agregar esta, corre el instalador en esa PC y elige "
                + "«Caja principal»; después da de alta esta."
            : TiendaNueva
                ? "Esa caja principal todavía no tiene tienda: termina primero su instalación (abre la caja en esa PC y pon la tienda y la "
                    + "cuenta del dueño), y después da de alta esta."
                : null;

    /// <summary>«192.168.1.50 · Matriz · versión 1.0.60», o por qué no.</summary>
    public string Texto => Contesta
        ? $"{BusquedaDeLaPrincipal.Corta(Direccion)}{(Sucursales.Count > 0 ? " · " + string.Join(", ", Sucursales) : "")}{(Version is { Length: > 0 } ? " · versión " + Version : "")}"
        : $"{BusquedaDeLaPrincipal.Corta(Direccion)}: {Error}";
}

/// <summary>
/// Encontrar la caja principal desde el asistente, como la encuentra la caja (src/PapeleriaPos.Interfaz/Descubrimiento.cs), con el
/// mismo protocolo y sin uno nuevo: la pregunta «PAPELERIAPOS?» por UDP al 5081, a toda la red y a la difusión de cada red de esta PC, y
/// la respuesta «PAPELERIAPOS 5080» con su puerto. Lo que contesta por UDP no prueba nada (cualquiera puede contestar): por eso después se
/// le pregunta a cada una su <c>api/estado</c> y solo cuenta la que dice ser Papelería POS. Una prueba revisa que esto sea lo mismo que
/// contesta el servicio (BusquedaDeLaPrincipalTests).
/// </summary>
public static class BusquedaDeLaPrincipal
{
    public const int PuertoDeBusqueda = 5081;
    public const int PuertoDelServicio = 5080;
    public const string Pregunta = "PAPELERIAPOS?";
    public const string Respuesta = "PAPELERIAPOS";

    /// <summary>Como se llama el servicio en su api/estado.</summary>
    public const string NombreDelServicio = "Papelería POS";

    /// <summary>
    /// «192.168.1.10» basta: sin esquema se usa http, y sin puerto el 5080 (como la pantalla de alta de la caja). Vacía si no se
    /// entiende.
    /// </summary>
    public static Uri? Direccion(string? texto)
    {
        texto = (texto ?? "").Trim();
        if (texto.Length == 0 || texto.Any(char.IsWhiteSpace))
            return null;
        var conEsquema = texto.Contains("://") ? texto : "http://" + texto;
        if (!Uri.TryCreate(conEsquema, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return null;
        var conPuerto = !uri.IsDefaultPort || conEsquema[(conEsquema.IndexOf("://", StringComparison.Ordinal) + 3)..].Contains(':');
        return new UriBuilder(uri.Scheme, uri.Host, conPuerto ? uri.Port : PuertoDelServicio, "/").Uri;
    }

    /// <summary>Sin el puerto si es el 5080: «192.168.1.50» o «192.168.1.50:5090».</summary>
    public static string Corta(Uri direccion) => direccion.Port == PuertoDelServicio ? direccion.Host : direccion.Authority;

    /// <summary>
    /// Las cajas principales que contestan en la red durante <paramref name="espera"/>, sin repetir. <paramref name="destinos"/> cambia a
    /// dónde va la pregunta (las pruebas, a un puerto suyo en esta PC); sin ellos, a toda la red y a la difusión de cada red de esta PC.
    /// </summary>
    public static async Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, IReadOnlyList<IPEndPoint>? destinos = null, CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        SinCortesPorIcmp(udp.Client);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var pregunta = Encoding.UTF8.GetBytes(Pregunta);
        foreach (var destino in destinos ?? Difusiones())
        {
            try
            {
                await udp.SendAsync(pregunta, destino, ct);
            }
            catch (SocketException)
            {
                // Una red que no deja difundir (o se acaba de caer): las demás sí.
            }
        }
        var encontradas = new List<Uri>();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        try
        {
            while (encontradas.Count < 64)
            {
                UdpReceiveResult recibido;
                try
                {
                    recibido = await udp.ReceiveAsync(limite.Token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.NetworkReset)
                {
                    continue;
                }
                if (LeerRespuesta(recibido.Buffer, recibido.RemoteEndPoint.Address) is { } uri && !encontradas.Contains(uri))
                    encontradas.Add(uri);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Se acabó el tiempo de espera.
        }
        catch (SocketException)
        {
            // La red se fue mientras se esperaba: lo que contestó antes, sirve.
        }
        return encontradas;
    }

    /// <summary>La dirección del servicio que contestó (su IP y el puerto que dijo); vacía si no es una respuesta del POS.</summary>
    public static Uri? LeerRespuesta(ReadOnlySpan<byte> mensaje, IPAddress quien)
    {
        var partes = Encoding.UTF8.GetString(mensaje).Split(' ');
        if (partes is not [Respuesta, var puerto] || !int.TryParse(puerto, out var numero) || numero is <= 0 or > 65535)
            return null;
        var ip = quien.IsIPv4MappedToIPv6 ? quien.MapToIPv4() : quien;
        return new UriBuilder("http", ip.ToString(), numero, "/").Uri;
    }

    /// <summary>A dónde va la pregunta: toda la red (255.255.255.255) y la difusión de cada red de cable o Wi-Fi de esta PC.</summary>
    public static IReadOnlyList<IPEndPoint> Difusiones()
    {
        var destinos = new List<IPEndPoint> { new(IPAddress.Broadcast, PuertoDeBusqueda) };
        try
        {
            foreach (var tarjeta in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (tarjeta.OperationalStatus != OperationalStatus.Up || !EsDeLaTienda(tarjeta.NetworkInterfaceType))
                    continue;
                foreach (var direccion in tarjeta.GetIPProperties().UnicastAddresses)
                    if (Difusion(direccion.Address, direccion.PrefixLength) is { } difusion)
                        destinos.Add(new IPEndPoint(difusion, PuertoDeBusqueda));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Sin la lista de tarjetas, la pregunta va solo a toda la red.
        }
        return destinos.DistinctBy(d => d.ToString()).ToList();
    }

    /// <summary>La de difusión de esa red (192.168.0.255 con /24), como RedLocal.Difusiones de la caja; vacía si no aplica.</summary>
    public static IPAddress? Difusion(IPAddress direccion, int prefijo)
    {
        if (direccion.IsIPv4MappedToIPv6)
            direccion = direccion.MapToIPv4();
        if (direccion.AddressFamily != AddressFamily.InterNetwork || prefijo is < 8 or > 30 || IPAddress.IsLoopback(direccion))
            return null;
        var bytes = direccion.GetAddressBytes();
        var numero = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        numero |= ~(uint.MaxValue << (32 - prefijo));
        return new IPAddress([(byte)(numero >> 24), (byte)(numero >> 16), (byte)(numero >> 8), (byte)numero]);
    }

    /// <summary>Las tarjetas de la red de la tienda: cable o Wi-Fi (no los túneles como una VPN), como RedLocal.EsDeLaTienda.</summary>
    public static bool EsDeLaTienda(NetworkInterfaceType tipo) => tipo is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
        or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet
        or NetworkInterfaceType.Wireless80211;

    /// <summary>
    /// Pregunta a esa dirección su api/estado, sin proxy y con poca espera. Solo «contesta» si dice ser Papelería POS: otro programa en
    /// ese puerto no cuenta.
    /// </summary>
    public static async Task<EstadoDeLaPrincipal> ProbarAsync(Uri direccion, HttpMessageHandler? red = null, TimeSpan? espera = null,
        CancellationToken ct = default)
    {
        using var cliente = red is null
            ? new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = espera ?? TimeSpan.FromSeconds(4) })
            : new HttpClient(red, disposeHandler: false);
        cliente.Timeout = espera ?? TimeSpan.FromSeconds(4);
        try
        {
            using var respuesta = await cliente.GetAsync(new Uri(direccion, "api/estado"), ct);
            if (!respuesta.IsSuccessStatusCode)
                return new EstadoDeLaPrincipal(direccion, false, null, [], $"contestó con el código {(int)respuesta.StatusCode}, no como la caja principal");
            var json = JsonNode.Parse(await respuesta.Content.ReadAsByteArrayAsync(ct)) as JsonObject;
            if (json is null || Json.Texto(Json.Valor(json, "servicio")) != NombreDelServicio)
                return new EstadoDeLaPrincipal(direccion, false, null, [], "contesta, pero no es Papelería POS");
            var sucursales = (Json.Valor(json, "sucursales") as JsonArray)?.Select(Json.Texto).OfType<string>().ToList() ?? [];
            return new EstadoDeLaPrincipal(direccion, true, Json.Texto(Json.Valor(json, "version")), sucursales, null)
            {
                UnaSolaCaja = Json.SiNo(Json.Valor(json, "unaSolaCaja")),
                TiendaNueva = Json.SiNo(Json.Valor(json, "tiendaNueva")),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new EstadoDeLaPrincipal(direccion, false, null, [], "no contestó (¿está prendida y en la misma red?)");
        }
    }

    /// <summary>SIO_UDP_CONNRESET: que Windows no corte la recepción por un «puerto inalcanzable» (ICMP), como en la caja.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private static void SinCortesPorIcmp(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException)
        {
            // Sin eso, la recepción ya sigue escuchando tras «conexión reiniciada».
        }
    }
}
