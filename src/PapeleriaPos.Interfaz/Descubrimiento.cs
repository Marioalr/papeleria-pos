using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Encontrar la caja principal en la red sin saber su dirección: la caja pregunta a toda la red por UDP y el servicio
/// contesta con su puerto. Sirve si la IP cambió o para dar de alta una caja nueva.
/// <para>
/// Lo que contesta por UDP no prueba nada: cualquiera en la red puede contestar «PAPELERIAPOS 5080». Por eso, antes de
/// cambiarse a una dirección encontrada (<see cref="ReencuentroDeLaPrincipal"/>), la caja le pide al servicio que pruebe que
/// es su caja principal (<see cref="RutaIdentidad"/>): le manda un reto al azar y el servicio contesta con un HMAC del reto
/// hecho con la huella de la llave de esa caja (lo único que guarda de ella), con el número de la caja y con la dirección
/// en que recibió la pregunta (<see cref="Prueba"/>). Solo el servicio que dio de alta a esa caja la tiene; la llave nunca
/// viaja (la caja no la manda a quien todavía no probó quién es), un reto nuevo cada vez evita que se repita una respuesta
/// vieja, y la dirección evita que otro equipo pase la pregunta a la caja principal de verdad y se quede en medio.
/// </para>
/// </summary>
public static class Descubrimiento
{
    public const int Puerto = 5081;
    public const string Pregunta = "PAPELERIAPOS?";
    public const string Respuesta = "PAPELERIAPOS";

    /// <summary>Lo que se le pide al servicio para que pruebe quién es, sin llave: <c>api/identidad?caja=…&amp;reto=…</c>.</summary>
    public const string RutaIdentidad = "api/identidad";

    /// <summary>Con qué empieza lo que se firma: si un día cambia la forma, una prueba de antes no sirve.</summary>
    private const string Version = "papeleria-pos/identidad/1";

    /// <summary>Con qué empieza lo que el servicio firma con su propia llave (<see cref="MensajeFirmado"/>).</summary>
    private const string VersionDeLaFirma = "papeleria-pos/identidad-del-servicio/1";

    /// <summary>Lo que contesta el servicio: "PAPELERIAPOS 5080".</summary>
    public static byte[] ArmarRespuesta(int puertoHttp) => Encoding.UTF8.GetBytes($"{Respuesta} {puertoHttp}");

    public static bool EsPregunta(ReadOnlySpan<byte> mensaje) => Encoding.UTF8.GetString(mensaje) == Pregunta;

    /// <summary>
    /// Las direcciones de los servicios que contestaron, sin repetir. La pregunta va a toda la red (255.255.255.255) y a la
    /// difusión de cada red de esta PC (con dos tarjetas, Windows manda la primera solo por una): un paquete por red, no se
    /// recorre la red equipo por equipo.
    /// </summary>
    public static async Task<List<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        SinCortesPorIcmp(udp.Client);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var pregunta = Encoding.UTF8.GetBytes(Pregunta);
        foreach (var destino in RedLocal.Difusiones(RedLocal.Subredes()).Prepend(IPAddress.Broadcast).Distinct())
        {
            try
            {
                await udp.SendAsync(pregunta, new IPEndPoint(destino, Puerto), ct);
            }
            catch (SocketException)
            {
                // Una red que no deja difundir (o se acaba de caer): las demás sí.
            }
        }

        return await EscucharAsync(t => udp.ReceiveAsync(t), espera, ct);
    }

    /// <summary>Lo más que se recuerda de lo que contesta: una lluvia de respuestas no llena la memoria.</summary>
    public const int TopeDeRespuestas = 256;

    /// <summary>
    /// Lo que contesta durante <paramref name="espera"/>, sin repetir. En Windows, un «puerto inalcanzable» (ICMP) de un equipo al
    /// que le llegó la pregunta (o uno falso) hace que la recepción truene con «conexión reiniciada»: no es el fin, se sigue
    /// escuchando hasta que se acabe el tiempo. Si la red se va, lo que contestó antes sirve.
    /// </summary>
    internal static async Task<List<Uri>> EscucharAsync(Func<CancellationToken, ValueTask<UdpReceiveResult>> recibir, TimeSpan espera, CancellationToken ct)
    {
        var encontrados = new List<Uri>();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        try
        {
            while (encontrados.Count < TopeDeRespuestas)
            {
                UdpReceiveResult recibido;
                try
                {
                    recibido = await recibir(limite.Token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.NetworkReset)
                {
                    continue;
                }
                if (LeerRespuesta(recibido.Buffer, recibido.RemoteEndPoint.Address) is { } uri && !encontrados.Contains(uri))
                    encontrados.Add(uri);
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
        return encontrados;
    }

    /// <summary>SIO_UDP_CONNRESET: que Windows no corte la recepción de UDP por un «puerto inalcanzable» (ICMP).</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// En Windows, que un «puerto inalcanzable» (ICMP) que llegue no corte la recepción de este socket de UDP (sin esto, cualquiera
    /// en la red puede cortar la búsqueda con uno falso). En los demás sistemas no hace falta.
    /// </summary>
    public static void SinCortesPorIcmp(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException)
        {
            // Sin eso, la recepción ya sigue escuchando tras «conexión reiniciada» (EscucharAsync).
        }
    }

    /// <summary>La dirección del servicio que contestó (su IP y el puerto que dijo); vacía si no es una respuesta del POS.</summary>
    public static Uri? LeerRespuesta(ReadOnlySpan<byte> mensaje, IPAddress quien)
    {
        var partes = Encoding.UTF8.GetString(mensaje).Split(' ');
        if (partes is not [Respuesta, var puerto] || !int.TryParse(puerto, out var p) || p is <= 0 or > 65535)
            return null;
        var ip = RedLocal.Normal(quien);
        return new UriBuilder("http", ip.ToString(), p, "/").Uri;
    }

    // ── Probar que es la caja principal de esta caja ────────────────────────

    /// <summary>Un reto nuevo, al azar (32 bytes en base64 para URL).</summary>
    public static string NuevoReto() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Un reto que se puede firmar: de 16 a 128 letras, números, «-» o «_».</summary>
    public static bool EsRetoValido(string? reto) =>
        reto is { Length: >= 16 and <= 128 } && reto.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// La prueba: HMAC-SHA256 de la versión, el número de la caja, el reto y la dirección (<see cref="Direccion(IPAddress, int)"/>),
    /// con la huella de la llave de la caja (<c>LlaveCaja.Huella</c>, lo que guarda el servicio) como llave. En hexadecimal.
    /// </summary>
    public static string Prueba(string huellaDeLaLlave, Guid cajaId, string reto, string direccion)
    {
        var mensaje = Encoding.UTF8.GetBytes($"{Version}\n{cajaId:D}\n{reto}\n{direccion}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(huellaDeLaLlave), mensaje));
    }

    /// <summary>Si la prueba que contestó es la que se esperaba (comparadas sin delatar por el tiempo cuánto coinciden).</summary>
    public static bool EsLaPrueba(string? contesto, string esperada) =>
        contesto is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(contesto.ToUpperInvariant()), Encoding.ASCII.GetBytes(esperada));

    /// <summary>
    /// Lo que el servicio firma con su propia llave (ECDSA P-256, SHA-256): lo mismo de la prueba y la prueba. Esa llave no está
    /// en la base ni en sus respaldos (en Windows, cifrada para esa PC): una copia de la base en otra PC sabe la huella de la llave
    /// de la caja, pero no firma como el servicio que la caja conoce.
    /// </summary>
    public static byte[] MensajeFirmado(Guid cajaId, string reto, string direccion, string prueba) =>
        Encoding.UTF8.GetBytes($"{VersionDeLaFirma}\n{cajaId:D}\n{reto}\n{direccion}\n{prueba}");

    /// <summary>Si <paramref name="firma"/> es de esa llave pública (en base64) sobre ese mensaje; cualquier cosa rara, no.</summary>
    public static bool FirmaValida(string? llavePublica, byte[] mensaje, string? firma)
    {
        if (llavePublica is not { Length: > 0 and <= 1024 } || firma is not { Length: > 0 and <= 1024 })
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(llavePublica), out _);
            return ecdsa.VerifyData(mensaje, Convert.FromBase64String(firma), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Unas letras para reconocer la llave de un servicio en la bitácora («a1b2c3d4»), sin escribirla entera.</summary>
    public static string HuellaDeLaLlave(string llavePublica) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(llavePublica)))[..8].ToLowerInvariant();

    /// <summary>
    /// La dirección con que se firma: la IP y el puerto en que el servicio recibió la pregunta, que son los mismos a los que la
    /// caja se conectó (en la red de la tienda no hay nada en medio). «192.168.0.10:5080».
    /// </summary>
    public static string Direccion(IPAddress ip, int puerto)
    {
        var normal = RedLocal.Normal(ip);
        return normal.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{normal}]:{puerto}" : $"{normal}:{puerto}";
    }

    /// <summary>La de una dirección encontrada (http://192.168.0.10:5080/): su IP y su puerto. Vacía si no es una IP.</summary>
    public static string? Direccion(Uri servicio) =>
        IPAddress.TryParse(servicio.Host.Trim('[', ']'), out var ip) ? Direccion(ip, servicio.Port) : null;
}
