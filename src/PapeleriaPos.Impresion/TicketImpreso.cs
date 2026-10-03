using System.Text;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Impresion;

/// <summary>Dónde se imprime: la caja de Windows manda los bytes a la impresora; el navegador solo muestra la vista previa.</summary>
public interface IImpresora
{
    /// <summary>Sin impresora (en el navegador, en pruebas): la caja muestra el ticket en pantalla.</summary>
    bool MuestraVistaPrevia { get; }

    Task ImprimirAsync(TicketDto ticket, bool abrirCajon, CancellationToken ct = default);
}

/// <summary>No imprime: la caja muestra la vista previa. Para el navegador y para probar sin la POS-8360.</summary>
public sealed class SinImpresora : IImpresora
{
    public bool MuestraVistaPrevia => true;

    public Task ImprimirAsync(TicketDto ticket, bool abrirCajon, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>El ticket en texto, tal como saldrá impreso. Sirve de vista previa y para las pruebas.</summary>
public static class TicketTexto
{
    public static string Generar(TicketDto ticket)
    {
        var sb = new StringBuilder();
        foreach (var elemento in DisenoTicket.Armar(ticket))
        {
            var (texto, alineacion) = elemento switch
            {
                Renglon r => (r.Texto, r.Alineacion),
                Qr q => ($"[QR {q.Datos}]", Alineacion.Centro),
                _ => ("", Alineacion.Izquierda),
            };
            sb.AppendLine(Alinear(texto, alineacion).TrimEnd());
        }
        return sb.ToString();
    }

    internal static string Alinear(string texto, Alineacion alineacion)
    {
        if (texto.Length >= DisenoTicket.Columnas)
            return texto[..DisenoTicket.Columnas];
        return alineacion switch
        {
            Alineacion.Centro => texto.PadLeft((DisenoTicket.Columnas + texto.Length) / 2).PadRight(DisenoTicket.Columnas),
            Alineacion.Derecha => texto.PadLeft(DisenoTicket.Columnas),
            _ => texto,
        };
    }
}

/// <summary>
/// El ticket como comandos ESC/POS para la impresora térmica de 80 mm (POS-8360): texto en la página de códigos 850
/// para que salgan los acentos y la ñ, QR con el folio, corte de papel y, si se pide, apertura del cajón.
/// </summary>
public static class TicketEscPos
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;

    private static readonly Encoding Pc850;

    static TicketEscPos()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Pc850 = Encoding.GetEncoding(850);
    }

    public static byte[] Generar(TicketDto ticket, bool abrirCajon)
    {
        var b = new List<byte>();
        b.AddRange([Esc, (byte)'@']);           // reinicia la impresora
        b.AddRange([Esc, (byte)'t', 2]);         // página de códigos 850
        if (abrirCajon)
            b.AddRange([Esc, (byte)'p', 0, 25, 250]);

        foreach (var elemento in DisenoTicket.Armar(ticket))
        {
            switch (elemento)
            {
                case Renglon r:
                    b.AddRange([Esc, (byte)'a', (byte)r.Alineacion]);
                    b.AddRange([Esc, (byte)'E', (byte)(r.Negrita ? 1 : 0)]);
                    b.AddRange([Gs, (byte)'!', (byte)(r.Grande ? 0x01 : 0x00)]);
                    b.AddRange(Pc850.GetBytes(r.Texto.Length > DisenoTicket.Columnas ? r.Texto[..DisenoTicket.Columnas] : r.Texto));
                    b.Add(0x0A);
                    break;
                case Qr q:
                    b.AddRange([Esc, (byte)'a', 1]);
                    AgregarQr(b, q.Datos);
                    break;
            }
        }

        b.AddRange([Esc, (byte)'a', 0, Gs, (byte)'!', 0, Esc, (byte)'E', 0]);
        b.AddRange([Esc, (byte)'d', 4]);         // avanza para que el pie pase la cuchilla
        b.AddRange([Gs, (byte)'V', 66, 0]);      // corte parcial
        return b.ToArray();
    }

    private static void AgregarQr(List<byte> b, string datos)
    {
        var bytes = Encoding.ASCII.GetBytes(datos);
        var largo = bytes.Length + 3;
        b.AddRange([Gs, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0]);                 // modelo 2
        b.AddRange([Gs, (byte)'(', (byte)'k', 3, 0, 49, 67, 6]);                     // tamaño del módulo
        b.AddRange([Gs, (byte)'(', (byte)'k', 3, 0, 49, 69, 48]);                    // corrección de errores L
        b.AddRange([Gs, (byte)'(', (byte)'k', (byte)(largo % 256), (byte)(largo / 256), 49, 80, 48]);
        b.AddRange(bytes);
        b.AddRange([Gs, (byte)'(', (byte)'k', 3, 0, 49, 81, 48]);                    // imprime
        b.Add(0x0A);
    }
}
