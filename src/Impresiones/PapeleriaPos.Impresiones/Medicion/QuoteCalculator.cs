// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

using System.Globalization;

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Cómo se imprimiría el documento que se cotiza.</summary>
public sealed record QuoteOptions
{
    /// <summary>Tipo de papel del driver (Plain, PhotographicHighGloss, …), como en Costos → tipos de papel.</summary>
    public string MediaType { get; init; } = "Plain";
    public PaperSize Size { get; init; } = PaperSize.Carta;
    public bool Color { get; init; } = true;
    public string Quality { get; init; } = "Standard";
    public bool Duplex { get; init; }
    public int Copies { get; init; } = 1;
    public int PagesPerSheet { get; init; } = 1;
    /// <summary>Páginas a imprimir ("1-3, 5"); vacío = todas.</summary>
    public string? PageRange { get; init; }

    /// <summary>
    /// Corrección de la tinta medida al cotizar, propia de cada impresora (2026-11-08): lo que la tinta de lo impreso salió de más (o de
    /// menos) contra la de lo cotizado, con los pares que se van juntando en la tienda. 1 = sin corregir. Solo toca la tinta: el papel, las
    /// caras y lo demás ya son exactos.
    /// </summary>
    public double CorrectionFactor { get; init; } = 1.0;
}

/// <summary>Precio de una cotización: el mismo cálculo que una impresión real (mínimo, mayoreo y redondeo incluidos).</summary>
public sealed record QuoteResult(
    JobSettings Settings,
    IReadOnlyList<int> Pages,
    CostBreakdown Cost,
    IReadOnlyList<PageCost> PageCosts,
    decimal SpecialPrice)
{
    public decimal Price => Cost.Price;

    /// <summary>La tinta sin la corrección de la impresora (<see cref="QuoteOptions.CorrectionFactor"/>): con ella se aprende la corrección.</summary>
    public decimal UncorrectedInkCost { get; init; }
}

public static class QuoteCalculator
{
    /// <summary>Tamaños que se cotizan (los del papel que se vende).</summary>
    public static IReadOnlyList<PaperSize> Sizes { get; } = [PaperSize.Carta, PaperSize.Oficio, PaperSize.A4];

    /// <summary>Medidas en mm y nombre del Print Schema de cada tamaño.</summary>
    public static (double WidthMm, double HeightMm, string Name) Dimensions(PaperSize size) => size switch
    {
        PaperSize.Oficio => (216, 340, "MexicoOficio"),
        PaperSize.A4 => (210, 297, "ISOA4"),
        _ => (215.9, 279.4, "NorthAmericaLetter"),
    };

    public static JobSettings SettingsFor(QuoteOptions o)
    {
        var (w, h, name) = Dimensions(o.Size);
        return new JobSettings
        {
            MediaType = o.MediaType,
            MediaSizeName = name,
            MediaWidthMm = w,
            MediaHeightMm = h,
            Color = o.Color,
            Quality = o.Quality,
            Duplex = o.Duplex,
            Copies = Math.Max(1, o.Copies),
            PagesPerSheet = Math.Max(1, o.PagesPerSheet),
        };
    }

    /// <summary>
    /// Cotiza un documento medido, con el mismo cálculo que una impresión (<see cref="CostCalculator.Calculate"/>). <paramref name="measured"/>
    /// es la cobertura de cada página del documento (fracción de la página). Al imprimir, el programa (Acrobat, Edge, Chrome) ajusta cada
    /// página al área imprimible de la hoja («Ajustar», girándola si va acostada): la tinta de la página es su cobertura por el área que ocupa
    /// ya ajustada (2026-11-08; antes se suponía que llenaba el área imprimible: una página A4 u oficio en carta salía de más). Sin la medida
    /// de la página, como antes.
    /// </summary>
    public static QuoteResult Calculate(IReadOnlyList<PageCoverage> measured, QuoteOptions options, CostSettings cfg)
    {
        if (measured.Count == 0) throw new ArgumentException("El documento no tiene páginas.");
        var pages = ParsePageRange(options.PageRange, measured.Count);
        var settings = SettingsFor(options);
        double printableW = Math.Max(0, settings.MediaWidthMm - 6) / 25.4, printableH = Math.Max(0, settings.MediaHeightMm - 6) / 25.4;
        double factor = options.CorrectionFactor is > 0 and < 10 ? options.CorrectionFactor : 1.0;
        var coverage = pages.Select(p => Corrected(measured[p - 1], factor) with { AreaSqIn = PrintedArea(measured[p - 1], printableW, printableH) })
            .ToList();
        var analysis = new JobAnalysis(settings, coverage, AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var cost = CostCalculator.Calculate(analysis, cfg);
        var perPage = PageCosts.Compute(cost, settings, coverage);
        decimal special = CostCalculator.SpecialPrice(cost.Total, cost.PaperCost, cost.Sides, cost.Price, cfg);
        // El costo de la tinta es proporcional a la cobertura: sin la corrección es el mismo dividido entre ella.
        return new QuoteResult(settings, pages, cost, perPage, special) { UncorrectedInkCost = cost.InkCost / (decimal)factor };
    }

    /// <summary>
    /// El área (in²) que ocupa una página ya ajustada a la hoja: su tamaño por la escala de «Ajustar» al área imprimible, de pie o acostada
    /// (lo que quede más grande, como hacen los programas). Sin su tamaño, el área imprimible completa.
    /// </summary>
    public static double PrintedArea(PageCoverage page, double printableWidthIn, double printableHeightIn)
    {
        if (page.WidthIn is not (> 0 and var w) || page.HeightIn is not (> 0 and var h))
            return printableWidthIn * printableHeightIn;
        double scale = Math.Max(Math.Min(printableWidthIn / w, printableHeightIn / h), Math.Min(printableWidthIn / h, printableHeightIn / w));
        return w * h * scale * scale;
    }

    static PageCoverage Corrected(PageCoverage p, double factor) => factor == 1.0
        ? p
        : p with { C = p.C * factor, M = p.M * factor, Y = p.Y * factor, K = p.K * factor, Gray = p.Gray * factor };

    /// <summary>
    /// Páginas de un rango como "1-3, 5, 8-" (8 en adelante) o "-4" (hasta la 4); vacío = todas. En orden y sin repetir.
    /// </summary>
    /// <exception cref="FormatException">Con un mensaje para mostrar.</exception>
    public static IReadOnlyList<int> ParsePageRange(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(1, pageCount).ToList();
        var set = new SortedSet<int>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = raw.Replace('–', '-').Replace(" ", "");
            int dash = part.IndexOf('-');
            int from, to;
            if (dash < 0)
            {
                from = to = Number(part, raw);
            }
            else
            {
                string a = part[..dash], b = part[(dash + 1)..];
                if (a.Length == 0 && b.Length == 0) throw new FormatException($"«{raw}» no es un rango de páginas.");
                from = a.Length == 0 ? 1 : Number(a, raw);
                to = b.Length == 0 ? pageCount : Number(b, raw);
            }
            if (from > to) throw new FormatException($"En «{raw}» la primera página es mayor que la última.");
            if (from > pageCount)
                throw new FormatException($"El documento tiene {pageCount} página{(pageCount == 1 ? "" : "s")}; no existe la {from}.");
            for (int p = from; p <= Math.Min(to, pageCount); p++) set.Add(p);
        }
        if (set.Count == 0) throw new FormatException("Escribe qué páginas imprimir, por ejemplo 1-3, 5.");
        return [.. set];
    }

    static int Number(string s, string part) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1
            ? n
            : throw new FormatException($"«{part}» no es un número de página válido.");
}
