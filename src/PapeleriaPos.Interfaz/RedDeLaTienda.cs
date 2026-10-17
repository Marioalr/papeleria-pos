using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PapeleriaPos.Interfaz;

/// <summary>Cómo sale esta PC a la red de la tienda.</summary>
public enum TipoDeConexion
{
    Cable,
    WiFi,
    Otra,
}

/// <summary>
/// La red de esta PC en un momento (la red de la tienda en el POS, docs/arquitectura.md, «La red de la matriz»): la tarjeta con
/// que sale a la red de la tienda, su IP y de dónde viene, su router y, en Windows, el Wi-Fi y su señal. Lo que pide
/// docs/notas.md («Red de la tienda») para la próxima caída: cómo está conectada, la IP (del router, fija o una 169.254), la
/// puerta de enlace y su MAC, y los paquetes de difusión y descartados de la tarjeta (una tormenta de tráfico).
/// </summary>
/// <param name="Adaptador">El nombre de la tarjeta en Windows («Wi-Fi», «Ethernet»); vacío si no tiene red.</param>
/// <param name="PorDhcp">Si la IP se la dio el router (DHCP) o está puesta a mano; vacío si no se sabe (Linux).</param>
/// <param name="Wifi">La red Wi-Fi (su nombre, o el del perfil de Windows); vacío si no se sabe o va por cable.</param>
/// <param name="Senal">La señal del Wi-Fi, de 0 a 100; vacía si no se sabe.</param>
/// <param name="MacDelRouter">La MAC del router (en Windows, por ARP): si cambia, contesta otro aparato con su IP.</param>
/// <param name="Difusion">Cuántos paquetes de difusión ha recibido la tarjeta desde que arrancó (para ver una tormenta).</param>
/// <param name="Descartados">Cuántos paquetes recibidos ha descartado la tarjeta.</param>
public sealed record FotoDeLaRed(
    string? Adaptador, TipoDeConexion Tipo, IPAddress? Ip, int? Prefijo, IPAddress? Router, bool? PorDhcp,
    string? Wifi = null, int? Senal = null, string? MacDelRouter = null, long? Difusion = null, long? Descartados = null)
{
    public static readonly FotoDeLaRed SinRed = new(null, TipoDeConexion.Otra, null, null, null, null);

    /// <summary>Una 169.254.x.x: Windows se la pone sola cuando nadie le da dirección (el router no contestó al pedirla).</summary>
    public bool Autoasignada => Ip?.GetAddressBytes() is [169, 254, _, _];

    /// <summary>«Wi-Fi «Tienda» (señal 80 %)», «cable (Ethernet)» o «sin red».</summary>
    public string Conexion => Tipo switch
    {
        _ when Adaptador is null => "sin red",
        TipoDeConexion.WiFi => $"Wi-Fi{(Wifi is { Length: > 0 } red ? $" «{red}»" : "")}{(Senal is { } senal ? $" (señal {senal} %)" : "")}",
        TipoDeConexion.Cable => $"cable ({Adaptador})",
        _ => $"«{Adaptador}»",
    };

    /// <summary>«IP 192.168.0.11/24 por DHCP», «IP 192.168.0.11/24 fija» o «sin IP».</summary>
    public string TextoIp => Ip is not { } ip
        ? "sin IP"
        : $"IP {ip}{(Prefijo is { } p ? $"/{p}" : "")}{(Autoasignada ? " (puesta por Windows: nadie le dio dirección)" : PorDhcp switch { true => " por DHCP", false => " fija", _ => "" })}";

    /// <summary>Todo en un renglón, para la bitácora: conexión, IP y router (con su MAC).</summary>
    public string Texto =>
        Adaptador is null
            ? "sin red"
            : $"{Conexion}, {TextoIp}, {(Router is { } r ? $"router {r}{(MacDelRouter is { } mac ? $" (MAC {mac})" : "")}" : "sin router")}";
}

/// <summary>Lee la red de esta PC (<see cref="FotoDeLaRed"/>). La de verdad, <see cref="RedDelEquipo"/>; las pruebas, una a mano.</summary>
public interface IRedDelEquipo
{
    /// <param name="hacia">
    /// La caja principal, si se sabe: se toma la tarjeta de su misma red. Sin ella, la que tiene router, como Windows.
    /// </param>
    FotoDeLaRed Leer(IPAddress? hacia);
}

/// <summary>
/// Lo de la red que solo sabe Windows (la caja de Windows lo pone, src/PapeleriaPos.Caja): el nombre y la señal del Wi-Fi, y
/// la MAC del router. Sin esto (Linux, el navegador, el servicio), la foto no lo trae.
/// </summary>
public interface IDetallesDeRed
{
    /// <summary>El Wi-Fi de esa tarjeta (por su Id): su nombre y su señal de 0 a 100. Vacío si no se sabe.</summary>
    (string? Red, int? Senal)? Wifi(string idDelAdaptador);

    /// <summary>La MAC de esa IP de la red local (preguntando por ARP); vacía si no contesta o no se puede.</summary>
    string? Mac(IPAddress ip);
}

/// <summary>La red de esta PC con lo que trae .NET, más lo de Windows si se lo dan (<see cref="IDetallesDeRed"/>).</summary>
public sealed class RedDelEquipo(IDetallesDeRed? detalles = null) : IRedDelEquipo
{
    public FotoDeLaRed Leer(IPAddress? hacia)
    {
        try
        {
            var candidatas = new List<(NetworkInterface Tarjeta, UnicastIPAddressInformation Ip, IPAddress? Router)>();
            foreach (var tarjeta in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (tarjeta.OperationalStatus != OperationalStatus.Up || !RedLocal.EsDeLaTienda(tarjeta.NetworkInterfaceType))
                    continue;
                var propiedades = tarjeta.GetIPProperties();
                if (propiedades.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork) is not { } ip)
                    continue;
                var router = propiedades.GatewayAddresses.Select(g => g.Address)
                    .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                candidatas.Add((tarjeta, ip, router));
            }
            if (candidatas.Count == 0)
                return FotoDeLaRed.SinRed;
            // La de la red de la caja principal; si no, la que tiene router (por donde sale Windows); si no, la primera.
            var elegida = candidatas[0];
            var enSuRed = hacia is { } destino ? candidatas.FindIndex(c => RedLocal.EnLaMismaSubred(c.Ip.Address, destino, c.Ip.PrefixLength)) : -1;
            var conRouter = candidatas.FindIndex(c => c.Router is not null);
            if (enSuRed >= 0)
                elegida = candidatas[enSuRed];
            else if (conRouter >= 0)
                elegida = candidatas[conRouter];
            return Foto(elegida.Tarjeta, elegida.Ip, elegida.Router);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return FotoDeLaRed.SinRed;
        }
    }

    private FotoDeLaRed Foto(NetworkInterface tarjeta, UnicastIPAddressInformation ip, IPAddress? router)
    {
        var tipo = tarjeta.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? TipoDeConexion.WiFi : TipoDeConexion.Cable;
        bool? dhcp = null;
        try
        {
            // Linux no lo dice.
            if (OperatingSystem.IsWindows())
                dhcp = tarjeta.GetIPProperties().GetIPv4Properties()?.IsDhcpEnabled;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            // Sin saberlo: la foto lo dice sin «por DHCP» ni «fija».
        }
        long? difusion = null, descartados = null;
        try
        {
            var estadisticas = tarjeta.GetIPStatistics();
            (difusion, descartados) = (estadisticas.NonUnicastPacketsReceived, estadisticas.IncomingPacketsDiscarded);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            // Sin cuentas de la tarjeta.
        }
        var wifi = tipo == TipoDeConexion.WiFi ? Seguro(() => detalles?.Wifi(tarjeta.Id)) : null;
        var mac = router is not null ? Seguro(() => detalles?.Mac(router)) : null;
        return new FotoDeLaRed(tarjeta.Name, tipo, ip.Address, ip.PrefixLength, router, dhcp, wifi?.Red, wifi?.Senal, mac, difusion, descartados);
    }

    /// <summary>Lo de Windows nunca tumba la lectura: si falla, la foto va sin eso.</summary>
    private static T? Seguro<T>(Func<T?> leer)
    {
        try
        {
            return leer();
        }
        catch (Exception)
        {
            return default;
        }
    }
}

/// <summary>
/// Las pruebas de la red que hace el vigía (<see cref="VigiaDeLaRed"/>), cada una con su plazo corto y sin mandar nada más que
/// la pregunta. Las de verdad, <see cref="PruebasDeRed"/>; las pruebas del POS, unas a mano (sin red de verdad).
/// </summary>
public interface IPruebasDeRed
{
    /// <summary>Un ping; vacío si no contesta a tiempo.</summary>
    Task<TimeSpan?> PingAsync(IPAddress ip, TimeSpan espera, CancellationToken ct = default);

    /// <summary>Abre una conexión TCP y la cierra, sin mandar nada; vacío si no conecta a tiempo.</summary>
    Task<TimeSpan?> ConectarAsync(string host, int puerto, TimeSpan espera, CancellationToken ct = default);

    /// <summary>Le pregunta un nombre al DNS de esa IP (el del router), sin el caché de Windows; vacío si no contesta a tiempo.</summary>
    Task<TimeSpan?> PreguntarDnsAsync(IPAddress servidor, string nombre, TimeSpan espera, CancellationToken ct = default);
}

/// <summary>Las pruebas de verdad: ping, una conexión TCP o una pregunta de DNS, cada una con su plazo.</summary>
public sealed class PruebasDeRed : IPruebasDeRed
{
    private static readonly byte[] Relleno = Encoding.ASCII.GetBytes("papeleria-pos-red");

    public async Task<TimeSpan?> PingAsync(IPAddress ip, TimeSpan espera, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reloj = Stopwatch.StartNew();
            var respuesta = await ping.SendPingAsync(ip, espera, Relleno, null, ct);
            return respuesta.Status == IPStatus.Success ? reloj.Elapsed : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<TimeSpan?> ConectarAsync(string host, int puerto, TimeSpan espera, CancellationToken ct = default)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var reloj = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(host, puerto, limite.Token);
            return reloj.Elapsed;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<TimeSpan?> PreguntarDnsAsync(IPAddress servidor, string nombre, TimeSpan espera, CancellationToken ct = default)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        using var udp = new UdpClient(servidor.AddressFamily);
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var reloj = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(PreguntaDns(id, nombre), new IPEndPoint(servidor, 53), limite.Token);
            while (true)
            {
                var recibido = await udp.ReceiveAsync(limite.Token);
                // Cualquier respuesta a esta pregunta (aunque diga que no existe o que falló afuera): el DNS del router está vivo.
                if (recibido.Buffer.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(recibido.Buffer) == id && (recibido.Buffer[2] & 0x80) != 0)
                    return reloj.Elapsed;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Una pregunta de DNS (tipo A) por ese nombre, con su número.</summary>
    internal static byte[] PreguntaDns(ushort id, string nombre)
    {
        var mensaje = new List<byte>(32 + nombre.Length);
        mensaje.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var parte in nombre.Trim('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(parte);
            mensaje.Add((byte)Math.Min(bytes.Length, 63));
            mensaje.AddRange(bytes.Take(63));
        }
        mensaje.AddRange([0, 0, 1, 0, 1]);
        return [.. mensaje];
    }
}

/// <summary>
/// Lo de la red local de la tienda: qué tarjetas cuentan (cable y Wi-Fi, no los túneles como NetBird) y si una dirección es
/// de la misma red. Así lo que el servicio contesta sin llave (buscarlo en la red, probar que es él) no sale de la tienda.
/// </summary>
public static class RedLocal
{
    /// <summary>Las tarjetas de la red de la tienda: cable o Wi-Fi. Los túneles (una VPN), el loopback y los demás, no.</summary>
    public static bool EsDeLaTienda(NetworkInterfaceType tipo) => tipo is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
        or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet
        or NetworkInterfaceType.Wireless80211;

    /// <summary>Las redes de esta PC (su IP y el largo de su máscara), solo de cable o Wi-Fi y prendidas.</summary>
    public static IReadOnlyList<(IPAddress Direccion, int Prefijo)> Subredes()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(t => t.OperationalStatus == OperationalStatus.Up && EsDeLaTienda(t.NetworkInterfaceType))
                .SelectMany(t => t.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and <= 32)
                .Select(u => (u.Address, u.PrefixLength))
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Si dos IPv4 están en la misma red con esa máscara.</summary>
    public static bool EnLaMismaSubred(IPAddress a, IPAddress b, int prefijo)
    {
        if (Normal(a) is not { AddressFamily: AddressFamily.InterNetwork } x || Normal(b) is not { AddressFamily: AddressFamily.InterNetwork } y
            || prefijo is < 0 or > 32)
            return false;
        var mascara = prefijo == 0 ? 0u : uint.MaxValue << (32 - prefijo);
        return (Numero(x) & mascara) == (Numero(y) & mascara);
    }

    /// <summary>
    /// Si quien pregunta es de la red de la tienda: esta misma PC, la misma red que alguna de sus tarjetas de cable o Wi-Fi, o
    /// una IPv6 de enlace local. Sin dirección es una llamada dentro del mismo proceso (las pruebas).
    /// </summary>
    public static bool EsDeLaRedLocal(IPAddress? remitente, IReadOnlyList<(IPAddress Direccion, int Prefijo)> propias)
    {
        if (remitente is null)
            return true;
        var ip = Normal(remitente);
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal;
        return propias.Any(p => EnLaMismaSubred(ip, p.Direccion, p.Prefijo));
    }

    /// <summary>La de difusión de cada red de esta PC (192.168.0.255 con /24): a donde va la pregunta de la caja.</summary>
    public static IReadOnlyList<IPAddress> Difusiones(IReadOnlyList<(IPAddress Direccion, int Prefijo)> propias) =>
        propias.Where(p => p.Prefijo is >= 8 and <= 30 && Normal(p.Direccion).AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(p.Direccion))
            .Select(p => Ipv4(Numero(Normal(p.Direccion)) | ~(uint.MaxValue << (32 - p.Prefijo))))
            .Distinct()
            .ToList();

    private static IPAddress Ipv4(uint numero)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, numero);
        return new IPAddress(bytes);
    }

    /// <summary>Una IPv4 escrita dentro de una IPv6 (::ffff:192.168.0.10), como IPv4.</summary>
    public static IPAddress Normal(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static uint Numero(IPAddress ipv4) => BinaryPrimitives.ReadUInt32BigEndian(ipv4.GetAddressBytes());

    /// <summary>Si el texto es una IP (y no un nombre que hay que preguntarle al DNS del router).</summary>
    public static bool EsIp(string? host) =>
        host is { Length: > 0 } && IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>El tiempo en milisegundos, como en la bitácora: «9 ms», «1,250 ms».</summary>
    public static string Ms(TimeSpan tiempo) => $"{Math.Max(1, Math.Round(tiempo.TotalMilliseconds)).ToString("#,##0", CultureInfo.InvariantCulture)} ms";
}
