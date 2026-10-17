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
/// red que se cae a media llamada: el puente se queda esperando.</item>
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
            _ = DelPuenteAsync(cliente.GetStream(), servidor.GetStream());
            _ = DeFirebirdAsync(servidor.GetStream(), cliente.GetStream());
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

    private async Task DelPuenteAsync(NetworkStream puente, NetworkStream firebird)
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
                        _callado = true;
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

    private async Task DeFirebirdAsync(NetworkStream firebird, NetworkStream puente)
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
                // Callado: el puente espera esta respuesta para siempre.
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
        _oyente.Stop();
        lock (_abiertos)
            foreach (var conexion in _abiertos)
                conexion.Dispose();
    }
}
