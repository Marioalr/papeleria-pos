using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Un intermediario entre el puente y el Firebird de prueba (2.5) para ver cómo se porta con el de la tienda (2.0.1):
/// <list type="bullet">
/// <item>con <see cref="SoloProtocolo10"/>, esconde los protocolos 11 y siguientes al conectar, así que se habla el
/// protocolo 10, el que negocia Firebird 2.0.1 (y con el que FirebirdClient no sabe cancelar);</item>
/// <item>con <see cref="Disparador"/>, en cuanto el puente manda ese texto, deja de pasar lo que contesta Firebird, como la
/// red que se cae a media llamada: el puente se queda esperando;</item>
/// <item>con <see cref="Retener"/>, en vez de tirarlo, retiene lo que contesta Firebird a esa conexión hasta
/// <see cref="Soltar"/>: la llamada va lenta, no muerta, y termina tarde. Las demás conexiones pasan.</item>
/// </list>
/// Cortesía de la revisión del 2026-10-17.
/// </summary>
public sealed class ProxyFirebird : IDisposable
{
    private readonly TcpListener _oyente = new(IPAddress.Loopback, 0);
    private readonly string _servidor;
    private readonly int _puerto;
    private readonly List<TcpClient> _abiertos = [];
    private readonly List<int> _aceptados = [];

    public ProxyFirebird(string servidor, int puerto)
    {
        (_servidor, _puerto) = (servidor, puerto);
        _oyente.Start();
        Puerto = ((IPEndPoint)_oyente.LocalEndpoint).Port;
        _ = AceptarAsync();
    }

    public int Puerto { get; }

    public bool SoloProtocolo10 { get; set; }

    /// <summary>Lo que, al pasar del puente a Firebird, corta las respuestas (todas, de todas las conexiones).</summary>
    public string? Disparador { get; set; }

    public bool Callado => _callado;

    /// <summary>Retener (en vez de tirar) lo que contesta Firebird a la conexión del disparador, hasta <see cref="Soltar"/>.</summary>
    public bool Retener { get; set; }

    private readonly TaskCompletionSource _soltar = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lo retenido sigue su camino: la llamada lenta termina.</summary>
    public void Soltar() => _soltar.TrySetResult();

    /// <summary>
    /// La red vuelve: las conexiones nuevas pasan otra vez. Las que se quedaron calladas se cortan (como cuando la red se cae
    /// a media llamada): lo que esperaban ya se perdió.
    /// </summary>
    public void Reanudar()
    {
        List<TcpClient> callados;
        lock (_abiertos)
        {
            callados = [.. _abiertos];
            _abiertos.Clear();
        }
        Disparador = null;
        _callado = false;
        foreach (var conexion in callados)
            conexion.Dispose();
    }

    private volatile bool _callado;

    /// <summary>El protocolo que aceptó Firebird en cada conexión.</summary>
    public IReadOnlyList<int> Aceptados
    {
        get
        {
            lock (_aceptados)
                return _aceptados.ToList();
        }
    }

    private async Task AceptarAsync()
    {
        while (true)
        {
            TcpClient cliente;
            try
            {
                cliente = await _oyente.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var servidor = new TcpClient();
            await servidor.ConnectAsync(_servidor, _puerto);
            lock (_abiertos)
                _abiertos.AddRange([cliente, servidor]);
            var retenida = new Retenida();
            _ = DelPuenteAsync(cliente.GetStream(), servidor.GetStream(), retenida);
            _ = DeFirebirdAsync(servidor.GetStream(), cliente.GetStream(), retenida);
        }
    }

    private static async Task<byte[]> LeerAsync(NetworkStream s, int n)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b);
        return b;
    }

    private static int Entero(byte[] b, int i = 0) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(i));
    private static int Relleno(int n) => (4 - n % 4) % 4;

    /// <summary>Si lo que contesta Firebird a una conexión se retiene.</summary>
    private sealed class Retenida
    {
        public volatile bool Si;
    }

    private async Task DelPuenteAsync(NetworkStream puente, NetworkStream firebird, Retenida retenida)
    {
        try
        {
            // op_connect: op, op_attach, versión, arquitectura, ruta, cuántos protocolos, usuario y, por cada protocolo,
            // (versión, arquitectura, mínimo, máximo, peso).
            var paquete = new List<byte>(await LeerAsync(puente, 16));
            var largo = await LeerAsync(puente, 4);
            paquete.AddRange(largo);
            paquete.AddRange(await LeerAsync(puente, Entero(largo) + Relleno(Entero(largo))));
            var cuantos = await LeerAsync(puente, 4);
            paquete.AddRange(cuantos);
            var usuario = await LeerAsync(puente, 4);
            paquete.AddRange(usuario);
            paquete.AddRange(await LeerAsync(puente, Entero(usuario) + Relleno(Entero(usuario))));
            var protocolos = await LeerAsync(puente, Entero(cuantos) * 20);
            for (var i = 0; i < Entero(cuantos); i++)
            {
                // Uno que Firebird no conoce: le queda el 10.
                if (SoloProtocolo10 && Entero(protocolos, i * 20) != 10)
                    BinaryPrimitives.WriteInt32BigEndian(protocolos.AsSpan(i * 20), 0x8063);
            }
            paquete.AddRange(protocolos);
            await firebird.WriteAsync(paquete.ToArray());

            var ventana = "";
            var buffer = new byte[8192];
            while (true)
            {
                var n = await puente.ReadAsync(buffer);
                if (n == 0)
                    break;
                if (Disparador is { } disparador)
                {
                    ventana += Encoding.Latin1.GetString(buffer, 0, n);
                    if (ventana.Contains(disparador))
                    {
                        // Retenida: solo esta conexión, y una vez.
                        if (Retener)
                            (retenida.Si, Disparador) = (true, null);
                        else
                            _callado = true;
                    }
                    if (ventana.Length > 4096)
                        ventana = ventana[^2048..];
                }
                await firebird.WriteAsync(buffer.AsMemory(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            // Se cerró una de las dos puntas.
        }
    }

    private async Task DeFirebirdAsync(NetworkStream firebird, NetworkStream puente, Retenida retenida)
    {
        try
        {
            var primero = true;
            var buffer = new byte[8192];
            while (true)
            {
                var n = await firebird.ReadAsync(buffer);
                if (n == 0)
                    break;
                if (primero && n >= 8)
                {
                    lock (_aceptados)
                        _aceptados.Add(Entero(buffer, 4) & 0xFF);
                    primero = false;
                }
                // Retenida: llega cuando se suelte. Callado: el puente espera esta respuesta para siempre.
                if (retenida.Si)
                    await _soltar.Task;
                if (!_callado)
                    await puente.WriteAsync(buffer.AsMemory(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Se cerró una de las dos puntas.
        }
    }

    public void Dispose()
    {
        _soltar.TrySetResult();
        _oyente.Stop();
        lock (_abiertos)
            foreach (var conexion in _abiertos)
                conexion.Dispose();
    }
}
