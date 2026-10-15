// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.Buffers.Binary;
using System.Printing.Interop;
using System.Xml.Linq;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>
/// Traduce el DEVMODE de un trabajo a <see cref="JobSettings"/> usando el proveedor de PrintTicket del
/// driver. Si falla, usa los campos públicos del DEVMODE.
/// </summary>
public sealed class PrintTicketReader : IDisposable
{
    static readonly XNamespace Psf = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    static readonly XNamespace Psk = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    readonly string queueName;

    public PrintTicketReader(string queueName) => this.queueName = queueName;

    public JobSettings Read(byte[]? devMode)
    {
        if (devMode is null || devMode.Length < 72) return new JobSettings();
        try
        {
            // PrintTicketConverter queda ligado al hilo que lo crea; se crea uno por consulta.
            using var converter = new PrintTicketConverter(queueName, PrintTicketConverter.MaxPrintSchemaVersion);
            var ticket = converter.ConvertDevModeToPrintTicket(devMode);
            using var s = ticket.GetXmlStream();
            return Parse(new StreamReader(s).ReadToEnd(), FromDevMode(devMode));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"PrintTicket no disponible para '{queueName}', se usa el DEVMODE público: {ex.Message}");
            return FromDevMode(devMode);
        }
    }

    /// <summary>Interpreta un PrintTicket XML; los valores ausentes se toman de <paramref name="fallback"/>.</summary>
    public static JobSettings Parse(string xml, JobSettings? fallback = null)
    {
        var s = fallback ?? new JobSettings();
        var root = XDocument.Parse(xml).Root!;

        XElement? Feature(string name) => root.Elements(Psf + "Feature")
            .FirstOrDefault(f => (string?)f.Attribute("name") == name);
        XElement? Option(string feature) => Feature(feature)?.Element(Psf + "Option");
        string? OptionName(string feature) => LocalName((string?)Option(feature)?.Attribute("name"));
        string? Param(string name) => root.Elements(Psf + "ParameterInit")
            .FirstOrDefault(p => (string?)p.Attribute("name") == name)?.Element(Psf + "Value")?.Value;
        string? Scored(XElement? option, string name) => option?.Elements(Psf + "ScoredProperty")
            .FirstOrDefault(p => (string?)p.Attribute("name") == name)?.Element(Psf + "Value")?.Value;

        var size = Option("psk:PageMediaSize");
        double width = double.TryParse(Scored(size, "psk:MediaSizeWidth"), out var wu) ? wu / 1000.0 : s.MediaWidthMm;
        double height = double.TryParse(Scored(size, "psk:MediaSizeHeight"), out var hu) ? hu / 1000.0 : s.MediaHeightMm;
        int copies = int.TryParse(Param("psk:JobCopiesAllDocuments"), out var c) && c > 0 ? c : s.Copies;
        var nupOption = Option("psk:JobNUpAllDocumentsContiguously");
        int nup = int.TryParse(Scored(nupOption, "psk:PagesPerSheet"), out var n) && n > 0 ? n : 1;

        return s with
        {
            Quality = OptionName("psk:PageOutputQuality") ?? s.Quality,
            MediaType = OptionName("psk:PageMediaType") ?? s.MediaType,
            MediaSizeName = LocalName((string?)size?.Attribute("name")) ?? s.MediaSizeName,
            MediaWidthMm = width,
            MediaHeightMm = height,
            Color = OptionName("psk:PageOutputColor") is { } col ? col == "Color" : s.Color,
            Duplex = OptionName("psk:JobDuplexAllDocumentsContiguously") is { } d ? d.StartsWith("TwoSided") : s.Duplex,
            Copies = copies,
            PagesPerSheet = nup,
            FromPrintTicket = true,
        };
    }

    /// <summary>Lectura mínima de los campos públicos de DEVMODEW.</summary>
    internal static JobSettings FromDevMode(ReadOnlySpan<byte> dm)
    {
        static short S(ReadOnlySpan<byte> dm, int off) =>
            dm.Length >= off + 2 ? BinaryPrimitives.ReadInt16LittleEndian(dm[off..]) : (short)0;
        short paper = S(dm, 78), copies = S(dm, 86), color = S(dm, 92), duplex = S(dm, 94);
        var (name, w, h) = paper switch
        {
            5 => ("NorthAmericaLegal", 215.9, 355.6),
            9 => ("ISOA4", 210.0, 297.0),
            _ => ("NorthAmericaLetter", 215.9, 279.4),
        };
        return new JobSettings
        {
            MediaSizeName = name, MediaWidthMm = w, MediaHeightMm = h,
            Color = color != 1,
            Duplex = duplex > 1,
            Copies = copies > 0 ? copies : 1,
        };
    }

    static string? LocalName(string? qualified) =>
        qualified is null ? null : qualified[(qualified.IndexOf(':') + 1)..];

    public void Dispose() { }
}
