using System.Net.Sockets;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Impresion;

/// <summary>
/// No se pudo conectar con la impresora de tickets: no se le mandó nada, así que se puede volver a intentar sin que el
/// ticket salga dos veces. Pasa si está apagada o fuera de la red, y puede pasar mientras imprime lo de la otra caja (la
/// POS-8360 de la tienda la comparten las dos): la cola de la caja lo vuelve a intentar sola (<see cref="ColaDeImpresion"/>).
/// </summary>
public sealed class ErrorAlConectar(string mensaje, Exception? causa = null) : IOException(mensaje, causa);

/// <summary>
/// Impresora de tickets en la red (como la POS-8360 de la tienda): se le mandan los comandos ESC/POS directo al
/// puerto 9100, sin controlador de Windows. Sirve igual desde la caja que desde el servicio.
/// <para>
/// Si se cierra la conexión mientras la impresora sigue imprimiendo (el logo tarda), descarta lo que no alcanzó a
/// procesar y el ticket sale cortado. Por eso se manda en partes y al final se le pregunta el estado del papel con
/// GS r 1, que la impresora contesta en orden: cuando responde, ya imprimió todo lo anterior.
/// </para>
/// <para>
/// Si no se pudo conectar, lanza <see cref="ErrorAlConectar"/> (no se mandó nada); si ya se mandó algo y algo falla (no
/// confirmó, sin papel), un <see cref="IOException"/>: puede haber salido, y no se vuelve a mandar solo.
/// </para>
/// </summary>
public sealed class ImpresoraRed(string host, int puerto = 9100, TimeSpan? esperaConexion = null, TimeSpan? esperaImpresion = null) : IImpresora
{
    private const int TamanoParte = 1024;
    private static readonly byte[] PreguntarPapel = [0x1D, (byte)'r', 1];

    private readonly TimeSpan _esperaConexion = esperaConexion ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _esperaImpresion = esperaImpresion ?? TimeSpan.FromSeconds(30);

    public string Host { get; } = host;
    public int Puerto { get; } = puerto;

    public bool MuestraVistaPrevia => false;

    public async Task EnviarAsync(byte[] datos, CancellationToken ct = default)
    {
        using var cliente = new TcpClient();
        var conectado = false;
        try
        {
            using (var limite = Limite(_esperaConexion, ct))
                await cliente.ConnectAsync(Host, Puerto, limite.Token);
            conectado = true;

            await using var canal = cliente.GetStream();
            using (var limite = Limite(_esperaImpresion, ct))
            {
                var respuesta = new byte[1];
                int leidos;
                try
                {
                    for (var i = 0; i < datos.Length; i += TamanoParte)
                        await canal.WriteAsync(datos.AsMemory(i, Math.Min(TamanoParte, datos.Length - i)), limite.Token);
                    await canal.WriteAsync(PreguntarPapel, limite.Token);
                    leidos = await canal.ReadAsync(respuesta, limite.Token);
                }
                catch (IOException ex)
                {
                    // Ya se mandó algo: pudo salir una parte.
                    throw new IOException("Se cortó la conexión con la impresora de tickets a medio ticket.", ex);
                }
                if (leidos == 0)
                    throw new IOException("La impresora cerró la conexión antes de terminar de imprimir.");
                // Bits 2 y 3: sin papel.
                if ((respuesta[0] & 0x0C) != 0)
                    throw new IOException("Se acabó el papel de la impresora de tickets.");
            }
        }
        catch (SocketException ex) when (!conectado)
        {
            throw new ErrorAlConectar($"No responde la impresora de tickets en {Host}:{Puerto}.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            if (!conectado)
                throw new ErrorAlConectar($"No responde la impresora de tickets en {Host}:{Puerto}.", ex);
            throw new IOException("La impresora de tickets no confirmó que terminó de imprimir.", ex);
        }
    }

    private static CancellationTokenSource Limite(TimeSpan espera, CancellationToken ct)
    {
        var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(espera);
        return limite;
    }
}

public static class Impresoras
{
    /// <summary>
    /// "tcp://192.168.1.100:9100" es una impresora de red; vacío, sin impresora (vista previa). Cualquier otro texto
    /// es el nombre de una impresora de Windows, que solo sabe usar la caja de Windows (<paramref name="windows"/>).
    /// </summary>
    public static IImpresora Crear(string? configuracion, Func<string, IImpresora>? windows = null)
    {
        if (string.IsNullOrWhiteSpace(configuracion))
            return new SinImpresora();
        if (configuracion.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(configuracion);
            return new ImpresoraRed(uri.Host, uri.Port > 0 ? uri.Port : 9100);
        }
        return windows?.Invoke(configuracion)
            ?? throw new ArgumentException($"«{configuracion}» no es una impresora de red (tcp://ip:9100).");
    }
}
