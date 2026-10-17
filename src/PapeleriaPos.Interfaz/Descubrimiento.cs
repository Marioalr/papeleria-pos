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
        var encontrados = new List<Uri>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
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

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        try
        {
            while (true)
            {
                var recibido = await udp.ReceiveAsync(limite.Token);
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
