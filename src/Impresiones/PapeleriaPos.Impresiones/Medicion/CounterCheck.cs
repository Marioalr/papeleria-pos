// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Caras registradas en un periodo: impresas desde las PC y copias cobradas en la impresora.</summary>
public sealed record RegisteredSides(int PrintSides, int PrintColorSides, int CopySides, int CopyColorSides)
{
    public static readonly RegisteredSides None = new(0, 0, 0, 0);
}

/// <summary>
/// Compara lo que contó la impresora (sus contadores total y de color) con lo registrado en la app: impresiones de
/// las PC y copias cobradas. Lo que sobra son caras sin registrar: copias que no se cobraron, impresiones desde el
/// celular, páginas de prueba… Es una estimación: los contadores se leen cada pocos minutos y un trabajo que cruza el
/// límite del periodo puede caer de un lado o del otro.
/// </summary>
public sealed record CounterCheck(
    DateTime FromUtc,
    DateTime ToUtc,
    long Counted,
    long? CountedColor,
    RegisteredSides Registered)
{
    public int RegisteredTotal => Registered.PrintSides + Registered.CopySides;
    public int RegisteredColor => Registered.PrintColorSides + Registered.CopyColorSides;

    /// <summary>Caras que contó la impresora y no están registradas (nunca negativo).</summary>
    public long Unregistered => Math.Max(0, Counted - RegisteredTotal);

    /// <summary>De las sin registrar, cuántas a color (null si la impresora no reporta el contador de color).</summary>
    public long? UnregisteredColor => CountedColor is { } c ? Math.Clamp(c - RegisteredColor, 0, Unregistered) : null;
    public long? UnregisteredMono => UnregisteredColor is { } c ? Unregistered - c : null;

    /// <summary>
    /// Lecturas del contador que encierran el periodo: la última antes de <paramref name="fromUtc"/> (o la primera
    /// dentro) y la última hasta <paramref name="toUtc"/>. Los trabajos se cuentan entre esas dos lecturas. Null si no
    /// hay dos lecturas distintas o el contador retrocedió (se cambió la impresora).
    /// </summary>
    public static CounterCheck? Compute(IEnumerable<PrinterStatus> history, DateTime fromUtc, DateTime toUtc,
        Func<DateTime, DateTime, RegisteredSides> registered)
    {
        var points = history.Where(s => s.LifeCount is not null && s.TakenUtc <= toUtc).OrderBy(s => s.TakenUtc).ToList();
        if (points.Count < 2) return null;
        var start = points.LastOrDefault(s => s.TakenUtc <= fromUtc) ?? points[0];
        var end = points[^1];
        if (end.TakenUtc <= start.TakenUtc || end.LifeCount < start.LifeCount) return null;
        long? color = start.ColorCount is { } c0 && end.ColorCount is { } c1 && c1 >= c0 ? c1 - c0 : null;
        return new CounterCheck(start.TakenUtc, end.TakenUtc, end.LifeCount!.Value - start.LifeCount!.Value, color,
            registered(start.TakenUtc, end.TakenUtc));
    }
}
