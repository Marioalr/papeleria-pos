// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés. En lugar de JobRecord recibe
// CalibrationJob: lo que el POS guarda de cada impresión.


namespace PapeleriaPos.Medicion;

public enum CalibrationQuality
{
    /// <summary>Todavía no hay dos lecturas comparables.</summary>
    NoData,
    /// <summary>El nivel ha bajado menos de 3 puntos: aún no se corrige nada.</summary>
    Collecting,
    /// <summary>Entre 3 y 10 puntos observados: corrección parcial.</summary>
    Preliminary,
    /// <summary>10 puntos o más observados.</summary>
    Reliable,
}

/// <param name="ObservedPoints">Puntos de nivel (%) que bajó el cartucho en la impresora.</param>
/// <param name="EstimatedPoints">Puntos que el modelo estimó para los mismos periodos.</param>
/// <param name="Ratio">Real ÷ estimado (null si no hay datos suficientes).</param>
/// <param name="Factor">Corrección a aplicar al costo de esta tinta.</param>
public sealed record ChannelCalibration(InkChannel Channel, double ObservedPoints, double EstimatedPoints,
    double? Ratio, double Factor, CalibrationQuality Quality);

/// <summary>Un consumible cambiado (el nivel subió).</summary>
public sealed record SupplyChange(DateTime Utc, string Supply, int From, int To);

/// <summary>Una impresión registrada: cuándo salió, sus caras y la tinta que estimó el modelo (in²).</summary>
public sealed record CalibrationJob(DateTime PrintedUtc, int Sides, int ColorSides, double InkC, double InkM, double InkY, double InkK);

public sealed record CalibrationResult
{
    public DateTime ComputedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public IReadOnlyList<ChannelCalibration> Channels { get; init; } = [];

    /// <summary>Páginas que contó la impresora en el periodo (todas las fuentes).</summary>
    public long PrinterPages { get; init; }
    public long PrinterColorPages { get; init; }
    public long PrinterMonoPages { get; init; }
    /// <summary>Caras registradas desde esta PC.</summary>
    public long OurSides { get; init; }
    public long OurColorSides { get; init; }
    /// <summary>Páginas de otras fuentes (copias en la impresora, otros equipos).</summary>
    public long ExternalColorPages { get; init; }
    public long ExternalMonoPages { get; init; }

    public double MaintenanceObservedPoints { get; init; }
    public long MaintenancePages { get; init; }
    public int? MaintenancePagesPerBox { get; init; }
    public CalibrationQuality MaintenanceQuality { get; init; }

    public IReadOnlyList<SupplyChange> Changes { get; init; } = [];
}

/// <summary>
/// Compara el consumo real (niveles que reporta la impresora) contra el consumo estimado por Control Print y
/// calcula factores de corrección. Los tramos donde se cambió un consumible se excluyen, y las páginas que no
/// pasaron por esta PC se estiman con el promedio por cara de los trabajos registrados.
/// </summary>
public static class Calibrator
{
    /// <summary>Un salto hacia arriba de al menos esto (puntos) se interpreta como cambio de cartucho.</summary>
    const int InkReplacementJump = 5;
    const int BoxReplacementJump = 10;
    /// <summary>Puntos observados con los que el factor medido pesa al 100 %.</summary>
    const double FullWeightPoints = 20;

    static readonly InkChannel[] Channels = [InkChannel.Black, InkChannel.Cyan, InkChannel.Magenta, InkChannel.Yellow];

    public static CalibrationResult Calibrate(IReadOnlyList<PrinterStatus> history, IReadOnlyList<CalibrationJob> jobs, CostSettings cfg)
    {
        var statuses = history.OrderBy(s => s.TakenUtc).ToList();
        var timed = jobs.Select(j => (Time: j.PrintedUtc, Job: j)).OrderBy(t => t.Time).ToList();
        var avg = PerSideAverages(timed.Select(t => t.Job).ToList(), cfg);

        var observed = new Dictionary<InkChannel, double>();
        var estimated = new Dictionary<InkChannel, double>();
        var usedPairs = new Dictionary<InkChannel, int>();
        var changes = new List<SupplyChange>();
        long printerPages = 0, printerColor = 0, printerMono = 0, ourSides = 0, ourColor = 0, extColorTotal = 0, extMonoTotal = 0;
        double boxObserved = 0;
        long boxPages = 0;
        int boxPairs = 0;

        for (int i = 1; i < statuses.Count; i++)
        {
            var a = statuses[i - 1];
            var b = statuses[i];
            var inInterval = timed.Where(t => t.Time > a.TakenUtc && t.Time <= b.TakenUtc).Select(t => t.Job).ToList();
            long sides = inInterval.Sum(j => (long)j.Sides);
            long colorSides = inInterval.Sum(j => (long)j.ColorSides);
            ourSides += sides;
            ourColor += colorSides;

            long dLife = a.LifeCount is { } la && b.LifeCount is { } lb ? Math.Max(0, lb - la) : 0;
            long extColor = 0, extMono = 0;
            if (a.ColorCount is { } ca && b.ColorCount is { } cb && a.MonoCount is { } ma && b.MonoCount is { } mb)
            {
                long dColor = Math.Max(0, cb - ca), dMono = Math.Max(0, mb - ma);
                printerColor += dColor;
                printerMono += dMono;
                extColor = Math.Max(0, dColor - colorSides);
                extMono = Math.Max(0, dMono - (sides - colorSides));
            }
            else
            {
                extMono = Math.Max(0, dLife - sides);
            }
            printerPages += dLife;
            extColorTotal += extColor;
            extMonoTotal += extMono;

            foreach (var ch in Channels)
            {
                if (Level(a, ch) is not { } from || Level(b, ch) is not { } to) continue;
                if (to - from >= InkReplacementJump)
                {
                    changes.Add(new SupplyChange(b.TakenUtc, Name(ch), from, to));
                    continue;
                }
                double capacity = CostCalculator.Capacity(cfg, ch);
                if (capacity <= 0) continue;
                double ours = inInterval.Sum(j => Ink(j, ch));
                double external = extColor * avg.Color[ch] + extMono * avg.Mono[ch];
                observed[ch] = observed.GetValueOrDefault(ch) + (from - to);
                estimated[ch] = estimated.GetValueOrDefault(ch) + (ours + external) / capacity * 100.0;
                usedPairs[ch] = usedPairs.GetValueOrDefault(ch) + 1;
            }

            if (a.Maintenance is { } boxFrom && b.Maintenance is { } boxTo)
            {
                if (boxTo - boxFrom >= BoxReplacementJump)
                {
                    changes.Add(new SupplyChange(b.TakenUtc, "Caja de mantenimiento", boxFrom, boxTo));
                }
                else
                {
                    boxObserved += boxFrom - boxTo;
                    boxPages += dLife;
                    boxPairs++;
                }
            }
        }

        var channelResults = Channels.Select(ch =>
        {
            if (usedPairs.GetValueOrDefault(ch) == 0) return new ChannelCalibration(ch, 0, 0, null, 1.0, CalibrationQuality.NoData);
            double obs = observed[ch], est = estimated[ch];
            double? ratio = obs >= 1 && est >= 0.5 ? obs / est : null;
            var quality = Quality(obs);
            double factor = 1.0;
            if (ratio is { } r && quality >= CalibrationQuality.Preliminary)
            {
                double weight = Math.Min(1.0, obs / FullWeightPoints);
                factor = Math.Round(1 + (Math.Clamp(r, 0.4, 2.5) - 1) * weight, 3);
            }
            return new ChannelCalibration(ch, obs, Math.Round(est, 2), ratio is { } rr ? Math.Round(rr, 3) : null, factor, quality);
        }).ToList();

        int? pagesPerBox = boxObserved >= 4 && boxPages > 0 ? (int)Math.Round(boxPages * 100.0 / boxObserved) : null;
        return new CalibrationResult
        {
            FromUtc = statuses.FirstOrDefault()?.TakenUtc,
            ToUtc = statuses.LastOrDefault()?.TakenUtc,
            Channels = channelResults,
            PrinterPages = printerPages,
            PrinterColorPages = printerColor,
            PrinterMonoPages = printerMono,
            OurSides = ourSides,
            OurColorSides = ourColor,
            ExternalColorPages = extColorTotal,
            ExternalMonoPages = extMonoTotal,
            MaintenanceObservedPoints = boxObserved,
            MaintenancePages = boxPages,
            MaintenancePagesPerBox = pagesPerBox,
            MaintenanceQuality = boxPairs == 0 ? CalibrationQuality.NoData : boxObserved < 4 ? CalibrationQuality.Collecting
                : boxObserved < 10 ? CalibrationQuality.Preliminary : CalibrationQuality.Reliable,
            Changes = changes,
        };
    }


    static CalibrationQuality Quality(double observedPoints) => observedPoints switch
    {
        < 3 => CalibrationQuality.Collecting,
        < 10 => CalibrationQuality.Preliminary,
        _ => CalibrationQuality.Reliable,
    };

    /// <summary>Tinta promedio (in², modelo) por cara a color y por cara B/N de los trabajos registrados.</summary>
    static (Dictionary<InkChannel, double> Color, Dictionary<InkChannel, double> Mono) PerSideAverages(
        List<CalibrationJob> jobs, CostSettings cfg)
    {
        // Sin datos propios: página ISO (5 % por canal) con el factor de limpiezas.
        double iso = cfg.IsoCoveragePercent / 100.0 * cfg.IsoPageAreaSqIn * cfg.CleaningFactor;
        var color = Channels.ToDictionary(ch => ch, _ => iso);
        var mono = Channels.ToDictionary(ch => ch, ch => ch == InkChannel.Black ? iso : 0.0);

        var colorJobs = jobs.Where(j => j.ColorSides > 0).ToList();
        long colorSides = colorJobs.Sum(j => (long)j.Sides);
        if (colorSides > 0)
            foreach (var ch in Channels) color[ch] = colorJobs.Sum(j => Ink(j, ch)) / colorSides;

        var monoJobs = jobs.Where(j => j.ColorSides == 0 && j.Sides > 0).ToList();
        long monoSides = monoJobs.Sum(j => (long)j.Sides);
        if (monoSides > 0) mono[InkChannel.Black] = monoJobs.Sum(j => j.InkK) / monoSides;
        return (color, mono);
    }

    static double Ink(CalibrationJob j, InkChannel ch) => ch switch
    {
        InkChannel.Cyan => j.InkC,
        InkChannel.Magenta => j.InkM,
        InkChannel.Yellow => j.InkY,
        _ => j.InkK,
    };

    static int? Level(PrinterStatus s, InkChannel ch) => ch switch
    {
        InkChannel.Cyan => s.InkC,
        InkChannel.Magenta => s.InkM,
        InkChannel.Yellow => s.InkY,
        _ => s.InkK,
    };

    public static string Name(InkChannel ch) => ch switch
    {
        InkChannel.Cyan => "Cian",
        InkChannel.Magenta => "Magenta",
        InkChannel.Yellow => "Amarillo",
        _ => "Negro",
    };
}
