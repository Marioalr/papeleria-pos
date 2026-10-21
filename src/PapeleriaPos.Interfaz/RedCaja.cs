using System.Net;
using System.Net.Sockets;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La red de la caja con la caja principal (entrega 5, parte 2, docs/propuesta-interfaz.md, «Rendimiento»): dos clientes
/// HTTP, cada uno con sus conexiones. El interactivo lleva lo que espera el cajero (cobrar, entrar, el turno), con un plazo
/// por operación (<see cref="Transporte"/>); el de fondo, lo que la caja hace sola (el latido, el aviso por SSE, los
/// cambios y la copia completa, la bandeja de salida). Así bajar la copia completa o el aviso, que se queda conectado, no
/// ocupan las conexiones del cobro.
/// <para>
/// Cada conexión, con keepalive de TCP corto: si el Wi-Fi se reconecta y una conexión queda medio muerta (la otra punta ya no
/// la conoce), se descubre en unos segundos y no en minutos; y las conexiones se renuevan cada poco, para no usar una vieja.
/// </para>
/// </summary>
public sealed class RedCaja : IDisposable
{
    /// <summary>Sin nada por una conexión este rato, TCP pregunta si la otra punta sigue ahí.</summary>
    public static readonly TimeSpan KeepAliveTras = TimeSpan.FromSeconds(5);

    /// <summary>Cada cuánto vuelve a preguntar si no contesta.</summary>
    public static readonly TimeSpan KeepAliveCada = TimeSpan.FromSeconds(2);

    /// <summary>Cuántas veces pregunta antes de darla por muerta: en total, unos 11 s sin respuesta.</summary>
    public const int KeepAliveIntentos = 3;

    /// <summary>Lo más que vive una conexión: después se abre otra (barato en la red de la tienda).</summary>
    public static readonly TimeSpan VidaDeLaConexion = TimeSpan.FromMinutes(2);

    /// <summary>Una conexión sin usar este rato se cierra: la siguiente operación abre una nueva, no una que quizá ya murió.</summary>
    public static readonly TimeSpan ConexionSinUsar = TimeSpan.FromSeconds(30);

    /// <summary>
    /// El tope de lo interactivo que no tiene plazo propio (lo de atrás: reportes, el catálogo, el inventario). Lo de la venta
    /// lleva el suyo, mucho menor (<see cref="Transporte.PlazoLectura"/>, <see cref="Transporte.PlazoEscritura"/>).
    /// </summary>
    public static readonly TimeSpan TopeInteractivo = TimeSpan.FromSeconds(15);

    public RedCaja(HttpMessageHandler interactivo, HttpMessageHandler fondo)
    {
        Interactivo = new HttpClient(interactivo) { Timeout = TopeInteractivo };
        // Sin tope propio: el aviso se queda conectado; lo demás lleva su plazo en cada llamada.
        Fondo = new HttpClient(fondo) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>La de la caja de verdad: conectar a la caja principal tarda en la red de la tienda milisegundos.</summary>
    public static RedCaja Crear() => new(Manejador(TimeSpan.FromSeconds(2)), Manejador(TimeSpan.FromSeconds(3)));

    public HttpClient Interactivo { get; }

    public HttpClient Fondo { get; }

    /// <summary>Un manejador con el keepalive de TCP y las conexiones que se renuevan.</summary>
    /// <param name="conectar">Lo más que se espera a conectar (si la caja principal está apagada, falla en este tiempo).</param>
    /// <remarks>Sin proxy: la llave de la caja (X-Caja-Llave) va directo a la caja principal, nunca a un proxy de la red (WPAD).</remarks>
    public static SocketsHttpHandler Manejador(TimeSpan conectar) => new()
    {
        UseProxy = false,
        ConnectTimeout = conectar,
        PooledConnectionLifetime = VidaDeLaConexion,
        PooledConnectionIdleTimeout = ConexionSinUsar,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        ConnectCallback = ConectarAsync,
    };

    /// <summary>Abre la conexión con el keepalive de TCP puesto (<see cref="ConKeepAlive"/>).</summary>
    internal static async ValueTask<Stream> ConectarAsync(SocketsHttpConnectionContext contexto, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            ConKeepAlive(socket);
            await socket.ConnectAsync(contexto.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// El keepalive de TCP: tras <see cref="KeepAliveTras"/> sin nada, pregunta cada <see cref="KeepAliveCada"/> y, si no
    /// contesta <see cref="KeepAliveIntentos"/> veces, la conexión se da por muerta. Lo que el sistema no permita poner se queda
    /// como está (la conexión sirve igual).
    /// </summary>
    public static void ConKeepAlive(Socket socket)
    {
        Poner(() => socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true));
        Poner(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)KeepAliveTras.TotalSeconds));
        Poner(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)KeepAliveCada.TotalSeconds));
        Poner(() => socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveIntentos));

        static void Poner(Action poner)
        {
            try
            {
                poner();
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
            {
                // Una versión de Windows que no lo tiene: la conexión sirve igual, solo tarda más en notar que murió.
            }
        }
    }

    public void Dispose()
    {
        Interactivo.Dispose();
        Fondo.Dispose();
    }
}
