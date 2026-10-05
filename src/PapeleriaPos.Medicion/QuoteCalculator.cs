// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

using System.Globalization;

namespace PapeleriaPos.Medicion;

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
    /// Cotiza un documento medido. <paramref name="measured"/> es la cobertura de cada página del documento (fracción
    /// de la hoja): el documento se ajusta al área imprimible del papel elegido, como al imprimir "ajustar a la página".
    /// </summary>
    public static QuoteResult Calculate(IReadOnlyList<PageCoverage> measured, QuoteOptions options, CostSettings cfg)
    {
        if (measured.Count == 0) throw new ArgumentException("El documento no tiene páginas.");
        var pages = ParsePageRange(options.PageRange, measured.Count);
        var settings = SettingsFor(options);
        double area = Math.Max(0, settings.MediaWidthMm - 6) * Math.Max(0, settings.MediaHeightMm - 6) / (25.4 * 25.4);
        var coverage = pages.Select(p => measured[p - 1] with { AreaSqIn = area }).ToList();
        var analysis = new JobAnalysis(settings, coverage, AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var cost = CostCalculator.Calculate(analysis, cfg);
        var perPage = PageCosts.Compute(cost, settings, coverage);
        decimal special = CostCalculator.SpecialPrice(cost.Total, cost.PaperCost, cost.Sides, cost.Price, cfg);
        return new QuoteResult(settings, pages, cost, perPage, special);
    }

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
