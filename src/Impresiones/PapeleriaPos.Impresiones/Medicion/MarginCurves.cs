// Propio del POS (2026-11-08), junto al cálculo traído de Control Print: por eso en inglés, como él.

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Un punto de una curva del margen: en <see cref="X"/> (tinta de la cara o caras del trabajo), <see cref="Y"/> (%).</summary>
public sealed class CurvePoint
{
    public decimal X { get; set; }
    public decimal Y { get; set; }

    public CurvePoint() { }

    public CurvePoint(decimal x, decimal y) => (X, Y) = (x, y);
}

/// <summary>
/// Las curvas del margen (el dueño, 2026-11-08: «el precio de venta se parece más bien a una curva logarítmica, tanto por hoja individual
/// como por trabajo total»). Con <see cref="ScalingMode.ByCurves"/>:
/// <list type="bullet">
/// <item><see cref="ByCoverage"/>: cada cara impresa lleva el margen de SU tinta (la cobertura de la cara, en %: la suma de sus canales
/// sobre el área de la página, como la mide el módulo; en B/N, el gris; con N páginas por hoja, cada página se reduce 1/N). Más tinta, menos
/// margen.</item>
/// <item><see cref="BySides"/>: un factor (%) que multiplica el margen de cada cara, según las caras impresas del trabajo (páginas × copias
/// entre N por hoja; una hoja a doble cara cuenta 2). Más caras, menos margen.</item>
/// </list>
/// Entre dos puntos la curva va en línea recta sobre un eje x logarítmico, así que con 4 o 5 puntos sale la forma de un logaritmo; antes
/// del primer punto y después del último se queda en el valor del extremo (una cara en blanco, 0 % de tinta, lleva el margen del primero).
/// </summary>
public sealed class MarginCurves
{
    /// <summary>Lo más que puede tener cada curva.</summary>
    public const int MaxPoints = 12;

    /// <summary>La tinta más alta de una cara: los cuatro canales llenos (CMYK al 100 % cada uno).</summary>
    public const decimal MaxCoverage = 400m;

    public const decimal MaxSides = 1_000_000m;

    /// <summary>El factor más alto: hasta el doble (para subirle a lo muy pequeño, si se quiere).</summary>
    public const decimal MaxFactor = 200m;

    /// <summary>
    /// Tinta de la cara (%) → margen (%). Sugeridos (2026-11-08) para que lo típico (texto, 5 %) quede como hoy (100 %, el primer tramo de
    /// fábrica) y lo muy cargado baje poco a poco; el dueño los afina con el probador y la comparación.
    /// </summary>
    public List<CurvePoint> ByCoverage { get; set; } = DefaultByCoverage();

    /// <summary>
    /// Caras impresas del trabajo → factor (%) del margen. Sugeridos para seguir a los tramos de fábrica por costo del trabajo (100 % hasta
    /// unos $100 de costo, 80, 60 y 50 %), medidos en caras de texto.
    /// </summary>
    public List<CurvePoint> BySides { get; set; } = DefaultBySides();

    public static List<CurvePoint> DefaultByCoverage() =>
    [
        new(5, 100), new(25, 100), new(60, 90), new(120, 80), new(250, 70),
    ];

    public static List<CurvePoint> DefaultBySides() =>
    [
        new(1, 100), new(100, 100), new(250, 85), new(1000, 65), new(2500, 55),
    ];

    /// <summary>
    /// El valor de la curva en <paramref name="x"/>: recta entre dos puntos sobre el logaritmo de x; fuera de los puntos, el del extremo.
    /// Vacío si la curva no tiene puntos. Los puntos se toman en orden de x (sin x repetidas ni x ≤ 0: eso lo revisa la Configuración;
    /// aquí, si llegan, se saltan).
    /// </summary>
    public static decimal? Evaluate(IReadOnlyList<CurvePoint>? points, decimal x)
    {
        var ordered = (points ?? []).Where(p => p.X > 0).OrderBy(p => p.X).ToList();
        if (ordered.Count == 0) return null;
        if (x <= ordered[0].X) return ordered[0].Y;
        if (x >= ordered[^1].X) return ordered[^1].Y;
        int i = ordered.FindLastIndex(p => p.X <= x);
        var (a, b) = (ordered[i], ordered[i + 1]);
        if (b.X == a.X) return b.Y;
        double t = (Math.Log((double)x) - Math.Log((double)a.X)) / (Math.Log((double)b.X) - Math.Log((double)a.X));
        return Math.Round(a.Y + (b.Y - a.Y) * (decimal)t, 4);
    }

    public MarginCurves Clone() => new()
    {
        ByCoverage = ByCoverage.Select(p => new CurvePoint(p.X, p.Y)).ToList(),
        BySides = BySides.Select(p => new CurvePoint(p.X, p.Y)).ToList(),
    };
}
