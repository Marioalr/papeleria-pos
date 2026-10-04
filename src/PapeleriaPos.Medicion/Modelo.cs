// Traído de Control Print (src/ControlPrint.Core/Analysis): lo que mide el agente y usa el cálculo de costos.

namespace PapeleriaPos.Medicion;

/// <summary>Configuración de impresión de un trabajo, en términos del Print Schema de Windows.</summary>
public sealed record JobSettings
{
    /// <summary>Nombre local de la opción de calidad (Draft, Standard, StandardVivid, HighQuality, AdvancedSetting).</summary>
    public string Quality { get; init; } = "Standard";

    /// <summary>Nombre local del tipo de papel (Plain, PhotographicHighGloss, ThickPaper, ...).</summary>
    public string MediaType { get; init; } = "Plain";

    /// <summary>Nombre local del tamaño (NorthAmericaLetter, ISOA4, MexicoOficio, ...).</summary>
    public string MediaSizeName { get; init; } = "NorthAmericaLetter";

    public double MediaWidthMm { get; init; } = 215.9;
    public double MediaHeightMm { get; init; } = 279.4;
    public bool Color { get; init; } = true;
    public bool Duplex { get; init; }
    public int Copies { get; init; } = 1;
    public int PagesPerSheet { get; init; } = 1;

    /// <summary>Indica si los datos vinieron del PrintTicket (true) o del DEVMODE público (false).</summary>
    public bool FromPrintTicket { get; init; }
}

/// <summary>Cobertura de tinta estimada para una página. Fracciones 0..1 sobre el área imprimible.</summary>
public sealed record PageCoverage(double C, double M, double Y, double K, double Gray, double AreaSqIn, bool HasColor)
{
    public bool IsBlank => K == 0 && C == 0 && M == 0 && Y == 0;

    public static PageCoverage Estimated(double areaSqIn, bool color, double fraction) =>
        color ? new(fraction, fraction, fraction, fraction, fraction, areaSqIn, true)
              : new(0, 0, 0, fraction, fraction, areaSqIn, false);
}

public enum AnalysisLevel
{
    /// <summary>Estimación por número de páginas (sin contenido).</summary>
    Estimated = 1,
    /// <summary>Cobertura medida página por página desde el EMF.</summary>
    Measured = 2,
}

public sealed record JobAnalysis(
    JobSettings Settings,
    IReadOnlyList<PageCoverage> Pages,
    AnalysisLevel Level,
    byte[]? Thumbnail,
    string? Note,
    TimeSpan Elapsed,
    int ThumbnailPage = 0);

public sealed class AnalysisOptions
{
    /// <summary>
    /// Resolución de rasterizado para medir cobertura. Comparado con la imagen que el driver manda a la
    /// impresora (600 dpi): a 300 dpi el texto difiere &lt; 1 % (8.24 % vs 8.19 %); a 100 dpi el texto delgado
    /// se adelgaza y subestima ~30 %. Costo: ~36 ms y ~32 MB temporales por página carta.
    /// </summary>
    public double Dpi { get; init; } = 300;
    public int ThumbnailMaxSide { get; init; } = 320;
    /// <summary>Cobertura supuesta por canal cuando no hay contenido que medir.</summary>
    public double FallbackCoverage { get; init; } = 0.05;
    /// <summary>Límite de páginas a rasterizar; las restantes usan el promedio medido.</summary>
    public int MaxMeasuredPages { get; init; } = 500;
}

