using System.Net.Sockets;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Impresion;

/// <summary>
/// Impresora de tickets en la red (como la POS-8360 de la tienda, en 192.168.1.100): se le mandan los comandos
/// ESC/POS directo al puerto 9100, sin controlador de Windows. Sirve igual desde la caja que desde el servicio.
/// </summary>
public sealed class ImpresoraRed(string host, int puerto = 9100) : IImpresora
{
    public static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    public string Host { get; } = host;
    public int Puerto { get; } = puerto;

    public bool MuestraVistaPrevia => false;

    public Task ImprimirAsync(TicketDto ticket, bool abrirCajon, CancellationToken ct = default) =>
        EnviarAsync(TicketEscPos.Generar(ticket, abrirCajon), ct);

    public async Task EnviarAsync(byte[] datos, CancellationToken ct = default)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(Espera);
        using var cliente = new TcpClient();
        try
        {
            await cliente.ConnectAsync(Host, Puerto, limite.Token);
            await using var canal = cliente.GetStream();
            await canal.WriteAsync(datos, limite.Token);
            await canal.FlushAsync(limite.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new IOException($"No responde la impresora de tickets en {Host}:{Puerto}.", ex);
        }
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
            ?? throw new ArgumentException($"«{configuracion}» no es una impresora de red (tcp://ip:9100).", nameof(configuracion));
    }
}
