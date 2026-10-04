using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Encontrar la caja principal en la red sin saber su dirección: la caja pregunta a toda la red por UDP y el servicio
/// contesta con su puerto. Sirve si la IP cambió o para dar de alta una caja nueva.
/// </summary>
public static class Descubrimiento
{
    public const int Puerto = 5081;
    public const string Pregunta = "PAPELERIAPOS?";
    public const string Respuesta = "PAPELERIAPOS";

    /// <summary>Lo que contesta el servicio: "PAPELERIAPOS 5080".</summary>
    public static byte[] ArmarRespuesta(int puertoHttp) => Encoding.UTF8.GetBytes($"{Respuesta} {puertoHttp}");

    public static bool EsPregunta(ReadOnlySpan<byte> mensaje) => Encoding.UTF8.GetString(mensaje) == Pregunta;

    /// <summary>Las direcciones de los servicios que contestaron, sin repetir.</summary>
    public static async Task<List<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default)
    {
        var encontrados = new List<Uri>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        await udp.SendAsync(Encoding.UTF8.GetBytes(Pregunta), new IPEndPoint(IPAddress.Broadcast, Puerto), ct);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        try
        {
            while (true)
            {
                var recibido = await udp.ReceiveAsync(limite.Token);
                var partes = Encoding.UTF8.GetString(recibido.Buffer).Split(' ');
                if (partes is [Respuesta, var puerto] && int.TryParse(puerto, out var p)
                    && new Uri($"http://{recibido.RemoteEndPoint.Address}:{p}") is var uri && !encontrados.Contains(uri))
                    encontrados.Add(uri);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Se acabó el tiempo de espera.
        }
        return encontrados;
    }
}
