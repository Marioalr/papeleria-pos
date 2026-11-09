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
    /// <summary>
    /// De dónde salió el margen, si no es el único: el tramo de mayoreo («mayoreo: costo del trabajo 0–100 → 100 %») o las curvas («curva: 12 %
    /// de tinta → 210 % × 85 % (40 caras)»).
    /// </summary>
    public string? MarginTier { get; init; }
    /// <summary>Con las curvas: la tinta promedio de las caras del trabajo (%), como la lee la curva por hoja.</summary>
    public decimal? CoveragePercent { get; init; }
    /// <summary>Con las curvas: el factor de la curva por trabajo (%) para sus caras.</summary>
    public decimal? SidesFactor { get; init; }
    /// <summary>
    /// Con las curvas: el margen (%) de cada página lógica, el de la cara donde sale (ya con el factor); <see cref="MarginPercent"/> es el
    /// promedio del trabajo. Vacío con un margen para todo el trabajo.
    /// </summary>
    public IReadOnlyList<decimal>? PageMargins { get; init; }
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
        if (cfg.Scaling == ScalingMode.ByCurves && sidesPerCopy > 0)
            return WithCurves(result, analysis, cfg, nup, sidesPerCopy);
        var (margin, tier) = cfg.MarginFor(result.Total, sides);
        decimal price = PriceFor(result.Total, margin, cfg.RoundTo, result.PaperCost, cfg.PaperMarginPercent);
        return Finish(result, cfg, margin, tier, price);
    }

    static CostBreakdown Finish(CostBreakdown result, CostSettings cfg, decimal margin, string? tier, decimal price)
    {
        bool minimumApplies = cfg.MinimumApplies == MinimumScope.AllJobs || result.Sides == 1;
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

    /// <summary>
    /// La tinta de una cara (%): la suma de los canales de sus páginas (en B/N, el gris) sobre el área de la página, cada página reducida 1/N
    /// con N páginas por hoja. Una carta de texto da ≈ 5 %; con fotos, ≈ 40 %; toda llena de color, 100 % o más (hasta 400 % con los cuatro
    /// canales llenos). Sin los factores de calidad, papel o limpiezas: esos ya suben el costo.
    /// </summary>
    public static decimal SideCoverage(IEnumerable<PageCoverage> pagesOnSide, bool color, int pagesPerSheet)
    {
        double ink = 0, area = 0;
        foreach (var p in pagesOnSide)
        {
            ink += (color ? p.C + p.M + p.Y + p.K : p.Gray) * p.AreaSqIn;
            area = Math.Max(area, p.AreaSqIn);
        }
        if (area <= 0) return 0;
        return Math.Round((decimal)(ink / (Math.Max(1, pagesPerSheet) * area) * 100), 2);
    }

    /// <summary>
    /// El precio con las curvas (2026-11-08): cada cara lleva el margen de su tinta por el factor de las caras del trabajo. El costo de cada
    /// cara es su tinta (repartida por canal según su cobertura, como <see cref="PageCosts"/>) más su parte, pareja, de lo demás por cara: el
    /// papel (una hoja a doble cara se reparte entre sus dos caras), la caja, la luz, la depreciación y los extras por página o por hoja. Los
    /// extras por trabajo llevan el margen promedio de las caras. El papel con margen propio lo sigue llevando; luego, redondeo y mínimo.
    /// </summary>
    static CostBreakdown WithCurves(CostBreakdown result, JobAnalysis analysis, CostSettings cfg, int nup, int sidesPerCopy)
    {
        var s = analysis.Settings;
        var pages = analysis.Pages;
        int copies = result.Copies, sides = result.Sides;
        decimal factor = MarginCurves.Evaluate(cfg.Curves.BySides, sides) ?? 100m;

        // Tinta de cada página (de una copia), repartida por canal según su cobertura.
        double[] Weights(Func<PageCoverage, double> f) => pages.Select(p => f(p) * p.AreaSqIn).ToArray();
        var channels = new (double[] W, decimal Cost)[]
        {
            (Weights(p => s.Color ? p.C : 0), result.InkCostC), (Weights(p => s.Color ? p.M : 0), result.InkCostM),
            (Weights(p => s.Color ? p.Y : 0), result.InkCostY), (Weights(p => s.Color ? p.K : p.Gray), result.InkCostK),
        };
        decimal PageInk(int i) => channels.Sum(ch =>
        {
            double sum = ch.W.Sum();
            return sum <= 0 ? 0 : ch.Cost * (decimal)(ch.W[i] / sum);
        }) / copies;

        decimal jobExtras = cfg.Extras.Where(e => e.Per == ExtraPer.Job).Sum(e => e.Amount);
        decimal perSide = sides > 0 ? (result.Total - result.InkCost - jobExtras) / sides : 0;
        decimal paperPerSide = sides > 0 ? result.PaperCost / sides : 0;
        decimal? paperMargin = cfg.PaperMarginPercent;

        var pageMargins = new decimal[pages.Count];
        decimal sidesPrice = 0, sidesCost = 0, weightedCoverage = 0;
        var coverages = new List<decimal>(sidesPerCopy);
        var margins = new List<decimal>(sidesPerCopy);
        for (int side = 0; side < sidesPerCopy; side++)
        {
            int first = side * nup, last = Math.Min(pages.Count, first + nup);
            decimal coverage = SideCoverage(pages.Skip(first).Take(last - first), s.Color, nup);
            decimal margin = (MarginCurves.Evaluate(cfg.Curves.ByCoverage, coverage) ?? cfg.MarginPercent) * factor / 100m;
            decimal ink = 0;
            for (int i = first; i < last; i++)
            {
                ink += PageInk(i);
                pageMargins[i] = margin;
            }
            decimal cost = ink + perSide;
            decimal price = (cost - paperPerSide) * (1 + margin / 100m) + paperPerSide * (1 + (paperMargin ?? margin) / 100m);
            sidesCost += cost * copies;
            sidesPrice += price * copies;
            weightedCoverage += coverage;
            coverages.Add(coverage);
            margins.Add(margin);
        }

        // El margen promedio: el que, con la regla de siempre (PriceFor), da el mismo precio. Con él se cobran los extras por trabajo y «solo
        // lo que salió» (que guarda un margen por trabajo).
        decimal paperTotal = paperMargin is null ? 0 : Math.Min(result.PaperCost, sidesCost);
        decimal paperPrice = paperTotal * (1 + (paperMargin ?? 0) / 100m);
        decimal average = sidesCost - paperTotal > 0
            ? ((sidesPrice - paperPrice) / (sidesCost - paperTotal) - 1) * 100m
            : margins.Count > 0 ? margins.Average() : cfg.MarginPercent * factor / 100m;
        decimal raw = sidesPrice + jobExtras * (1 + average / 100m);
        decimal coverageAverage = sidesPerCopy > 0 ? Math.Round(weightedCoverage / sidesPerCopy, 1) : 0;

        string Pct(decimal v) => $"{v:0.#} %";
        decimal baseMin = factor > 0 ? margins.Min() * 100m / factor : MarginCurves.Evaluate(cfg.Curves.ByCoverage, coverages.Max()) ?? 0;
        decimal baseMax = factor > 0 ? margins.Max() * 100m / factor : MarginCurves.Evaluate(cfg.Curves.ByCoverage, coverages.Min()) ?? 0;
        string caras = $"{sides} cara{(sides == 1 ? "" : "s")}";
        string tier = coverages.Min() == coverages.Max() || baseMin == baseMax
            ? $"curva: {Pct(coverageAverage)} de tinta → {Pct(baseMax)} × {Pct(factor)} ({caras})"
            : $"curva: de {Pct(coverages.Min())} a {Pct(coverages.Max())} de tinta → de {Pct(baseMax)} a {Pct(baseMin)} × {Pct(factor)} ({caras}); "
              + $"en promedio {Pct(average)}";

        return Finish(result with
        {
            CoveragePercent = coverageAverage,
            SidesFactor = factor,
            PageMargins = pageMargins,
        }, cfg, Math.Round(average, 2), tier, RoundUp(raw, cfg.RoundTo, result.Total));
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
        return RoundUp(raw, roundTo, cost);
    }

    /// <summary>Hacia arriba al múltiplo indicado (0: al centavo); sin costo, cero.</summary>
    static decimal RoundUp(decimal raw, decimal roundTo, decimal cost)
    {
        if (cost <= 0) return 0;
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
