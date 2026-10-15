// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Desglose del costo y precio de un trabajo.</summary>
public sealed record CostBreakdown
{
    public int LogicalPages { get; init; }
    public int Copies { get; init; }
    /// <summary>Caras impresas en total (todas las copias).</summary>
    public int Sides { get; init; }
    /// <summary>Caras que salen a color; las demás la impresora las imprime solo con negro.</summary>
    public int ColorSides { get; init; }
    /// <summary>Hojas de papel en total (todas las copias).</summary>
    public int Sheets { get; init; }
    public PaperSize PaperSize { get; init; }
    public string MediaName { get; init; } = "";
    public string PaperGroup { get; init; } = "";
    /// <summary>
    /// Tinta estimada por canal, en in² cubiertas al 100 %, según el modelo (sin calibración: es la base con la
    /// que la calibración compara el consumo real).
    /// </summary>
    public double InkC { get; init; }
    public double InkM { get; init; }
    public double InkY { get; init; }
    public double InkK { get; init; }
    public double InkFactor { get; init; }
    /// <summary>Factores de calibración aplicados al costo de cada tinta (1 = sin corrección).</summary>
    public double CalC { get; init; } = 1;
    public double CalM { get; init; } = 1;
    public double CalY { get; init; } = 1;
    public double CalK { get; init; } = 1;
    public decimal InkCostC { get; init; }
    public decimal InkCostM { get; init; }
    public decimal InkCostY { get; init; }
    public decimal InkCostK { get; init; }
    public decimal InkCost => InkCostC + InkCostM + InkCostY + InkCostK;
    public decimal PaperCost { get; init; }
    public decimal MaintenanceCost { get; init; }
    public decimal PowerCost { get; init; }
    public decimal ExtrasCost { get; init; }
    /// <summary>Depreciación de la impresora (por cara impresa).</summary>
    public decimal DepreciationCost { get; init; }
    public decimal Total => InkCost + PaperCost + MaintenanceCost + PowerCost + ExtrasCost + DepreciationCost;
    /// <summary>Margen aplicado (el del tramo de mayoreo si el escalado está activo).</summary>
    public decimal MarginPercent { get; init; }
    /// <summary>Margen propio del papel; null = el mismo que <see cref="MarginPercent"/>.</summary>
    public decimal? PaperMarginPercent { get; init; }
    public decimal BaseMarginPercent { get; init; }
    /// <summary>Descripción del tramo de mayoreo aplicado, si hay.</summary>
    public string? MarginTier { get; init; }
    /// <summary>El precio calculado era menor que el cobro mínimo.</summary>
    public bool MinimumApplied { get; init; }
    public decimal Price { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class CostCalculator
{
    public static CostBreakdown Calculate(JobAnalysis analysis, CostSettings cfg)
    {
        var s = analysis.Settings;
        var warnings = new List<string>();
        int copies = Math.Max(1, s.Copies);
        int nup = Math.Max(1, s.PagesPerSheet);
        int logical = analysis.Pages.Count;
        int sidesPerCopy = (logical + nup - 1) / nup;
        int sheetsPerCopy = s.Duplex ? (sidesPerCopy + 1) / 2 : sidesPerCopy;
        int sides = sidesPerCopy * copies, sheets = sheetsPerCopy * copies;

        var media = cfg.MediaFor(s.MediaType);
        double factor = cfg.QualityFactor(s.Quality) * media.InkFactor * cfg.CleaningFactor;

        // Tinta en in² cubiertas: cobertura × área; con N páginas por hoja cada página se reduce 1/N.
        double c = 0, m = 0, y = 0, k = 0;
        foreach (var p in analysis.Pages)
        {
            if (s.Color)
            {
                c += p.C * p.AreaSqIn; m += p.M * p.AreaSqIn; y += p.Y * p.AreaSqIn; k += p.K * p.AreaSqIn;
            }
            else
            {
                k += p.Gray * p.AreaSqIn;
            }
        }
        double scale = copies * factor / nup;
        c *= scale; m *= scale; y *= scale; k *= scale;

        // Caras a color: páginas con color (en modo color) reducidas por N por hoja, por copia.
        int colorPages = s.Color ? analysis.Pages.Count(p => p.HasColor) : 0;
        int colorSides = Math.Min(sides, (colorPages + nup - 1) / nup * copies);

        var cal = cfg.Calibration;
        decimal Ink(InkChannel ch, double sqIn)
        {
            var cart = cfg.Ink(ch);
            double capacity = Capacity(cfg, ch);
            return capacity <= 0 ? 0 : cart.Price * (decimal)(sqIn * cal.Factor(ch) / capacity);
        }

        var size = Classify(s.MediaWidthMm, s.MediaHeightMm);
        decimal sheetPrice = PaperPrice(cfg, size, media.Group, s, warnings);
        decimal maintenance = cfg.Maintenance.PagesPerBox > 0
            ? cfg.Maintenance.BoxPrice / cfg.Maintenance.PagesPerBox * sides : 0;
        double kwhPerSide = cfg.Power.PagesPerMinute > 0
            ? cfg.Power.Watts * (60.0 / cfg.Power.PagesPerMinute) / 3600.0 / 1000.0 : 0;
        decimal power = (decimal)(kwhPerSide * sides) * cfg.Power.PricePerKwh;
        decimal depreciation = Math.Round(cfg.Depreciation.PerSide * sides, 4);
        decimal extras = cfg.Extras.Sum(e => e.Per switch
        {
            ExtraPer.Page => e.Amount * sides,
            ExtraPer.Sheet => e.Amount * sheets,
            _ => e.Amount,
        });

        // Los faltantes de papel ya se avisan arriba con el tamaño concreto del trabajo.
        warnings.AddRange(cfg.MissingPrices().Where(p => !p.StartsWith("Papel"))
            .Select(p => $"Falta capturar el precio: {p}"));
        if (analysis.Level == AnalysisLevel.Estimated)
            warnings.Add("Costo estimado por número de páginas (no se pudo medir el contenido).");

        var result = new CostBreakdown
        {
            LogicalPages = logical, Copies = copies, Sides = sides, ColorSides = colorSides, Sheets = sheets,
            PaperSize = size, MediaName = media.Name, PaperGroup = media.Group,
            InkC = c, InkM = m, InkY = y, InkK = k, InkFactor = factor,
            CalC = cal.Factor(InkChannel.Cyan), CalM = cal.Factor(InkChannel.Magenta),
            CalY = cal.Factor(InkChannel.Yellow), CalK = cal.Factor(InkChannel.Black),
            InkCostC = Ink(InkChannel.Cyan, c), InkCostM = Ink(InkChannel.Magenta, m),
            InkCostY = Ink(InkChannel.Yellow, y), InkCostK = Ink(InkChannel.Black, k),
            PaperCost = sheetPrice * sheets,
            MaintenanceCost = maintenance,
            PowerCost = power,
            ExtrasCost = extras,
            DepreciationCost = depreciation,
            MarginPercent = cfg.MarginPercent,
            Warnings = warnings,
        };
        var (margin, tier) = cfg.MarginFor(result.Total, sides);
        decimal price = PriceFor(result.Total, margin, cfg.RoundTo, result.PaperCost, cfg.PaperMarginPercent);
        bool minimumApplies = cfg.MinimumApplies == MinimumScope.AllJobs || sides == 1;
        bool minimum = minimumApplies && result.Total > 0 && price < cfg.MinimumPrice;
        if (minimum) price = cfg.MinimumPrice;
        return result with
        {
            MarginPercent = margin,
            PaperMarginPercent = cfg.PaperMarginPercent,
            BaseMarginPercent = cfg.MarginPercent,
            MarginTier = tier,
            MinimumApplied = minimum,
            Price = price,
        };
    }

    /// <summary>Tinta de un cartucho completo en in² cubiertas al 100 % (rendimiento ISO × cobertura ISO × área).</summary>
    public static double Capacity(CostSettings cfg, InkChannel ch) =>
        cfg.Ink(ch).IsoYield * cfg.IsoCoveragePercent / 100.0 * cfg.IsoPageAreaSqIn;

    /// <summary>
    /// Precio especial de un trabajo: su costo con el margen especial (y el cobro mínimo si la regla lo pide),
    /// nunca más alto que su precio calculado normal. El papel con margen propio usa el menor de los dos.
    /// </summary>
    public static decimal SpecialPrice(decimal cost, decimal paperCost, int sides, decimal calculatedPrice, CostSettings cfg)
    {
        decimal special = cfg.Special.MarginPercent;
        decimal? paperMargin = cfg.PaperMarginPercent is { } pm ? Math.Min(pm, special) : null;
        decimal price = PriceFor(cost, special, cfg.RoundTo, paperCost, paperMargin);
        bool minimumApplies = cfg.MinimumApplies == MinimumScope.AllJobs || sides == 1;
        if (cfg.Special.ApplyMinimum && minimumApplies && cost > 0 && price < cfg.MinimumPrice) price = cfg.MinimumPrice;
        return Math.Min(price, calculatedPrice);
    }

    /// <summary>
    /// Precio = costo × (1 + margen), redondeado hacia arriba al múltiplo indicado. Si el papel tiene margen propio, la
    /// parte del papel (<paramref name="paperCost"/>, incluida en <paramref name="cost"/>) lleva ese margen.
    /// </summary>
    public static decimal PriceFor(decimal cost, decimal marginPercent, decimal roundTo,
        decimal paperCost = 0, decimal? paperMarginPercent = null)
    {
        if (cost <= 0) return 0;
        decimal paper = paperMarginPercent is null ? 0 : Math.Clamp(paperCost, 0, cost);
        decimal raw = (cost - paper) * (1 + marginPercent / 100m) + paper * (1 + (paperMarginPercent ?? 0) / 100m);
        if (roundTo <= 0) return Math.Round(raw, 2);
        // Tolerancia para no subir un peso por errores de redondeo (ej. 12.000000001).
        return Math.Ceiling(Math.Round(raw / roundTo, 6)) * roundTo;
    }

    /// <summary>Clasifica el tamaño por dimensiones: las variantes de oficio (Legal, México, Folio, Oficio 9) cuentan como Oficio.</summary>
    public static PaperSize Classify(double widthMm, double heightMm)
    {
        double w = Math.Min(widthMm, heightMm), h = Math.Max(widthMm, heightMm);
        bool Near(double a, double b) => Math.Abs(a - b) <= 3;
        if (Near(w, 215.9) && Near(h, 279.4)) return PaperSize.Carta;
        if (Near(w, 210) && Near(h, 297)) return PaperSize.A4;
        if (w >= 213 && w <= 218 && h >= 312 && h <= 358) return PaperSize.Oficio;
        return PaperSize.Otro;
    }

    static decimal PaperPrice(CostSettings cfg, PaperSize size, string group, JobSettings s, List<string> warnings)
    {
        var exact = cfg.Papers.FirstOrDefault(p => p.Size == size && p.Group == group && p.PricePerSheet > 0);
        if (exact is not null) return exact.PricePerSheet;

        // Sin precio para ese tamaño: se escala el precio carta del mismo grupo por área.
        var carta = cfg.Papers.FirstOrDefault(p => p.Size == PaperSize.Carta && p.Group == group && p.PricePerSheet > 0);
        if (carta is not null)
        {
            double ratio = s.MediaWidthMm * s.MediaHeightMm / (215.9 * 279.4);
            warnings.Add($"Sin precio de papel {size} ({group}); se estimó desde carta.");
            return Math.Round(carta.PricePerSheet * (decimal)ratio, 4);
        }
        warnings.Add($"Sin precio de papel {size} ({group}).");
        return 0;
    }
}
