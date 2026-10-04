// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
namespace PapeleriaPos.Medicion;

/// <summary>
/// Costos y parámetros configurables. Los valores por defecto corresponden a la WF-6590 con cartuchos
/// originales T748XXL; los factores marcados como "supuesto" se ajustan con la calibración.
/// </summary>
public sealed class CostSettings
{
    public string Currency { get; set; } = "MXN";

    /// <summary>Porcentaje de ganancia: precio = costo × (1 + margen/100).</summary>
    public decimal MarginPercent { get; set; } = 100m;

    /// <summary>
    /// Margen propio del papel (%): el papel suele llevar menos ganancia que la tinta. Null = el mismo margen que el
    /// resto del costo (con mayoreo, el del tramo).
    /// </summary>
    public decimal? PaperMarginPercent { get; set; }

    /// <summary>El precio se redondea hacia arriba a este múltiplo.</summary>
    public decimal RoundTo { get; set; } = 1m;

    /// <summary>Cobro mínimo: si el precio calculado es menor, se cobra este monto.</summary>
    public decimal MinimumPrice { get; set; } = 2m;

    /// <summary>A qué trabajos aplica el cobro mínimo.</summary>
    public MinimumScope MinimumApplies { get; set; } = MinimumScope.SinglePage;

    /// <summary>Escalado automático del margen (mayoreo).</summary>
    public ScalingMode Scaling { get; set; } = ScalingMode.ByJobCost;

    /// <summary>Tramos por costo total del trabajo: desde ese costo aplica ese margen.</summary>
    public List<MarginTier> JobCostTiers { get; set; } =
    [
        new() { From = 0, Margin = 100 },
        new() { From = 100, Margin = 80 },
        new() { From = 500, Margin = 60 },
        new() { From = 1000, Margin = 50 },
    ];

    /// <summary>Tramos por costo de cada hoja impresa: desde ese costo por hoja aplica ese margen.</summary>
    public List<MarginTier> SheetCostTiers { get; set; } =
    [
        new() { From = 0, Margin = 100 },
        new() { From = 1, Margin = 80 },
        new() { From = 3, Margin = 60 },
        new() { From = 6, Margin = 50 },
    ];

    /// <summary>Margen que corresponde a un trabajo según el escalado (o el margen base si está desactivado).</summary>
    public (decimal Margin, string? Tier) MarginFor(decimal jobCost, int sides)
    {
        var (tiers, metric, unit) = Scaling switch
        {
            ScalingMode.ByJobCost => (JobCostTiers, jobCost, "costo del trabajo"),
            ScalingMode.BySheetCost => (SheetCostTiers, sides > 0 ? jobCost / sides : jobCost, "costo por hoja"),
            _ => (null, 0m, ""),
        };
        if (tiers is not { Count: > 0 }) return (MarginPercent, null);

        var ordered = tiers.OrderBy(t => t.From).ToList();
        int index = Math.Max(0, ordered.FindLastIndex(t => t.From <= metric));
        var tier = ordered[index];
        string range = index + 1 < ordered.Count
            ? $"{tier.From:0.##}–{ordered[index + 1].From:0.##}"
            : $"más de {tier.From:0.##}";
        return (tier.Margin, $"mayoreo: {unit} {range} → {tier.Margin:0.#} %");
    }

    /// <summary>Regla del botón "Aplicar precio especial".</summary>
    public SpecialPriceSettings Special { get; set; } = new();

    public InkCartridge Black { get; set; } = new() { Name = "Negro", Sku = "T748XXL120-AL", Price = 1200, IsoYield = 10000 };
    public InkCartridge Cyan { get; set; } = new() { Name = "Cian", Sku = "T748XXL220-AL", Price = 1200, IsoYield = 7000 };
    public InkCartridge Magenta { get; set; } = new() { Name = "Magenta", Sku = "T748XXL320-AL", Price = 1200, IsoYield = 7000 };
    public InkCartridge Yellow { get; set; } = new() { Name = "Amarillo", Sku = "T748XXL420-AL", Price = 1200, IsoYield = 7000 };

    /// <summary>Cobertura por canal de la página de prueba ISO/IEC 24712 (supuesto de la industria).</summary>
    public double IsoCoveragePercent { get; set; } = 5;

    /// <summary>Área de la página ISO en in² (carta).</summary>
    public double IsoPageAreaSqIn { get; set; } = 93.5;

    /// <summary>Tinta extra por limpiezas de cabezal y carga inicial (el rendimiento ISO no la incluye). Supuesto.</summary>
    public double CleaningFactor { get; set; } = 1.10;

    /// <summary>Multiplicador de tinta por calidad del driver. Supuesto.</summary>
    public Dictionary<string, double> QualityFactors { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Draft"] = 0.55,
        ["Standard"] = 1.0,
        ["StandardVivid"] = 1.15,
        ["HighQuality"] = 1.3,
        ["AdvancedSetting"] = 1.3,
    };

    /// <summary>Tipos de papel del driver: nombre visible, grupo de precio y multiplicador de tinta.</summary>
    public List<MediaSetting> Media { get; set; } =
    [
        new() { Key = "Plain", Name = "Papel normal", Group = "Normal", InkFactor = 1.0 },
        new() { Key = "Letterhead", Name = "Membrete", Group = "Normal", InkFactor = 1.0 },
        new() { Key = "Recycled", Name = "Reciclado", Group = "Normal", InkFactor = 1.0 },
        new() { Key = "Color", Name = "Papel de color", Group = "Normal", InkFactor = 1.0 },
        new() { Key = "Preprinted", Name = "Preimpreso", Group = "Normal", InkFactor = 1.0 },
        new() { Key = "EnvelopePlain", Name = "Sobre", Group = "Sobre", InkFactor = 1.0 },
        new() { Key = "ThickPaper", Name = "Cartulina", Group = "Cartulina", InkFactor = 1.0 },
        new() { Key = "PhotographicMatte", Name = "Fotográfico mate", Group = "Fotográfico mate", InkFactor = 1.3 },
        new() { Key = "PhotographicSemiGloss", Name = "Fotográfico semibrillante", Group = "Fotográfico brillante", InkFactor = 1.5 },
        new() { Key = "EpsonPhotoPaperGlossy", Name = "Fotográfico brillante", Group = "Fotográfico brillante", InkFactor = 1.5 },
        new() { Key = "PhotographicHighGloss", Name = "Fotográfico premium brillante", Group = "Fotográfico brillante", InkFactor = 1.5 },
        new() { Key = "HighResolution", Name = "Fotográfico ultra premium", Group = "Fotográfico brillante", InkFactor = 1.5 },
    ];

    /// <summary>Precio por hoja según tamaño y grupo de papel.</summary>
    public List<PaperPrice> Papers { get; set; } =
    [
        new() { Size = PaperSize.Carta, Group = "Normal", PackSheets = 500 },
        new() { Size = PaperSize.Oficio, Group = "Normal", PackSheets = 500 },
        new() { Size = PaperSize.A4, Group = "Normal", PackSheets = 500 },
        new() { Size = PaperSize.Carta, Group = "Fotográfico brillante", PackSheets = 20 },
        new() { Size = PaperSize.Carta, Group = "Fotográfico mate", PackSheets = 20 },
        new() { Size = PaperSize.Carta, Group = "Cartulina", PackSheets = 100 },
    ];

    public MaintenanceBox Maintenance { get; set; } = new();
    public PowerSettings Power { get; set; } = new();
    public PrinterDepreciation Depreciation { get; set; } = new();
    public List<ExtraCost> Extras { get; set; } = [];

    /// <summary>Factores medidos con los niveles reales de la impresora (los ajusta la calibración).</summary>
    public CalibrationSettings Calibration { get; set; } = new();

    /// <summary>Como la guarda Control Print (enums como texto): así se importa su configuración tal cual.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Copia profunda (vía JSON).</summary>
    public CostSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<CostSettings>(
            System.Text.Json.JsonSerializer.Serialize(this, Json), Json)!;

    public InkCartridge Ink(InkChannel c) => c switch
    {
        InkChannel.Black => Black,
        InkChannel.Cyan => Cyan,
        InkChannel.Magenta => Magenta,
        _ => Yellow,
    };

    public MediaSetting MediaFor(string key) =>
        Media.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? new MediaSetting { Key = key, Name = key, Group = "Normal", InkFactor = 1.0 };

    public double QualityFactor(string quality) => QualityFactors.GetValueOrDefault(quality, 1.0);

    /// <summary>Lista de precios que faltan por capturar (precio 0).</summary>
    public IEnumerable<string> MissingPrices()
    {
        foreach (var c in new[] { Black, Cyan, Magenta, Yellow })
            if (c.Price <= 0) yield return $"Cartucho {c.Name}";
        if (Maintenance.BoxPrice <= 0) yield return "Caja de mantenimiento";
        foreach (var p in Papers.Where(p => p.Group == "Normal" && p.PricePerSheet <= 0))
            yield return $"Papel {p.Size} normal";
    }
}

public enum InkChannel { Black, Cyan, Magenta, Yellow }

public enum MinimumScope { SinglePage, AllJobs }

public enum ScalingMode { None, ByJobCost, BySheetCost }

/// <summary>
/// Regla del precio especial (primera versión: un margen propio). Nunca supera el precio calculado normal.
/// Más reglas y la base de clientes especiales vendrán después (ver docs/plan.md).
/// </summary>
public sealed class SpecialPriceSettings
{
    public decimal MarginPercent { get; set; } = 50m;
    /// <summary>Si el precio especial también respeta el cobro mínimo.</summary>
    public bool ApplyMinimum { get; set; } = true;
}

/// <summary>Tramo de escalado: a partir de <see cref="From"/> (costo) se aplica <see cref="Margin"/> (%).</summary>
public sealed class MarginTier
{
    public decimal From { get; set; }
    public decimal Margin { get; set; }
}

public enum PaperSize { Carta, Oficio, A4, Otro }

public sealed class InkCartridge
{
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Price { get; set; }
    public int IsoYield { get; set; }
}

public sealed class MediaSetting
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "Normal";
    public double InkFactor { get; set; } = 1.0;
}

/// <summary>Papel por paquete: el precio por hoja se deriva del precio y las hojas del paquete.</summary>
public sealed class PaperPrice : System.ComponentModel.INotifyPropertyChanged
{
    decimal packPrice;
    int packSheets = 500;

    public PaperSize Size { get; set; }
    public string Group { get; set; } = "Normal";

    public decimal PackPrice
    {
        get => packPrice;
        set { packPrice = value; Changed(nameof(PackPrice)); }
    }

    public int PackSheets
    {
        get => packSheets;
        set { packSheets = value; Changed(nameof(PackSheets)); }
    }

    public decimal PricePerSheet => packSheets > 0 ? packPrice / packSheets : 0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    void Changed(string name)
    {
        PropertyChanged?.Invoke(this, new(name));
        PropertyChanged?.Invoke(this, new(nameof(PricePerSheet)));
    }
}

public sealed class MaintenanceBox
{
    public string Sku { get; set; } = "T6712";
    public decimal BoxPrice { get; set; } = 850;
    /// <summary>Páginas que dura una caja. Supuesto inicial; se calibra con el nivel que reporta la impresora.</summary>
    public int PagesPerBox { get; set; } = 40000;
}

/// <summary>
/// Depreciación de la impresora en línea recta: (precio de compra − valor final) entre los meses de vida útil, y lo de
/// cada mes entre las caras que se imprimen al mes. Se cobra por cara impresa, como el desgaste.
/// </summary>
public sealed class PrinterDepreciation
{
    public bool Enabled { get; set; } = true;
    public decimal PurchasePrice { get; set; } = 15000m;
    public decimal ResidualValue { get; set; }
    public int Months { get; set; } = 24;
    /// <summary>Caras impresas al mes. Estimación inicial; se calcula con el contador de la impresora.</summary>
    public int SidesPerMonth { get; set; } = 5000;

    [System.Text.Json.Serialization.JsonIgnore]
    public decimal PerMonth => Months > 0 ? Math.Max(0, PurchasePrice - ResidualValue) / Months : 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public decimal PerSide => !Enabled || SidesPerMonth <= 0 ? 0 : PerMonth / SidesPerMonth;

    public const double DaysPerMonth = 365.25 / 12;

    /// <summary>
    /// Caras al mes según el contador de páginas de la impresora (cuenta todo lo que la desgasta). Null con menos de un
    /// día de lecturas. Si el contador retrocede (se cambió la impresora) solo cuenta desde ahí.
    /// </summary>
    public static (int SidesPerMonth, double Days)? EstimateFromCounter(IEnumerable<(DateTime TakenUtc, long? LifeCount)> history)
    {
        var points = history.Where(s => s.LifeCount is not null).OrderBy(s => s.TakenUtc).ToList();
        int start = 0;
        for (int i = 1; i < points.Count; i++)
            if (points[i].LifeCount < points[i - 1].LifeCount) start = i;
        if (points.Count - start < 2) return null;
        var (first, last) = (points[start], points[^1]);
        double days = (last.TakenUtc - first.TakenUtc).TotalDays;
        if (days < 1) return null;
        double perMonth = (last.LifeCount!.Value - first.LifeCount!.Value) / days * DaysPerMonth;
        return ((int)Math.Round(perMonth), days);
    }
}

public sealed class PowerSettings
{
    public double Watts { get; set; } = 25;
    public double PagesPerMinute { get; set; } = 24;
    public decimal PricePerKwh { get; set; } = 3.0m;
}

public sealed class CalibrationSettings
{
    /// <summary>Aplica los factores medidos a los precios.</summary>
    public bool Enabled { get; set; } = true;
    public double C { get; set; } = 1.0;
    public double M { get; set; } = 1.0;
    public double Y { get; set; } = 1.0;
    public double K { get; set; } = 1.0;
    /// <summary>Solo se usan datos posteriores a esta fecha (se fija al reiniciar la calibración).</summary>
    public DateTime? SinceUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    public double Factor(InkChannel ch) => !Enabled ? 1.0 : ch switch
    {
        InkChannel.Cyan => C,
        InkChannel.Magenta => M,
        InkChannel.Yellow => Y,
        _ => K,
    };
}

public enum ExtraPer { Page, Sheet, Job }

public sealed class ExtraCost
{
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public ExtraPer Per { get; set; } = ExtraPer.Job;
}
