// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

namespace PapeleriaPos.Medicion;

/// <summary>
/// Costo real de una página (por copia): su propia tinta más su parte de papel, caja, luz y extras.
/// <see cref="Price"/> es ese costo con el margen (el del papel a su parte de papel), sin redondear (el redondeo a pesos se aplica al trabajo completo).
/// </summary>
public sealed record PageCost(int Page, bool Color, double C, double M, double Y, double K, bool Blank,
    decimal InkCost, decimal OtherCost, decimal Cost, decimal Price);

/// <summary>
/// Costo de cada página de un trabajo ya calculado. La tinta de cada página se toma de su cobertura medida (una
/// hoja casi vacía y una llena de tinta dan costos muy distintos); papel, caja de mantenimiento, luz y extras se
/// reparten por cara impresa. La suma de las páginas por el número de copias da el costo del trabajo.
/// </summary>
public static class PageCosts
{
    public static IReadOnlyList<PageCost> Compute(CostBreakdown cost, JobSettings settings, IReadOnlyList<PageCoverage> pages)
    {
        int n = pages.Count;
        if (n == 0) return [];
        int copies = Math.Max(1, cost.Copies);
        decimal marginFactor = 1 + cost.MarginPercent / 100m;
        decimal paperFactor = 1 + (cost.PaperMarginPercent ?? cost.MarginPercent) / 100m;
        decimal paperPerPage = cost.PaperCost / (n * copies);

        // Tinta de cada página por canal, con la misma base que el cálculo del trabajo.
        double[] Weights(Func<PageCoverage, double> f) => pages.Select(p => f(p) * p.AreaSqIn).ToArray();
        var wc = Weights(p => settings.Color ? p.C : 0);
        var wm = Weights(p => settings.Color ? p.M : 0);
        var wy = Weights(p => settings.Color ? p.Y : 0);
        var wk = Weights(p => settings.Color ? p.K : p.Gray);
        decimal Share(double[] w, int i, decimal total)
        {
            double sum = w.Sum();
            return sum <= 0 ? 0 : total * (decimal)(w[i] / sum);
        }

        decimal other = cost.Total - cost.InkCost;
        var result = new List<PageCost>(n);
        for (int i = 0; i < n; i++)
        {
            decimal ink = (Share(wc, i, cost.InkCostC) + Share(wm, i, cost.InkCostM) +
                           Share(wy, i, cost.InkCostY) + Share(wk, i, cost.InkCostK)) / copies;
            decimal rest = other / (n * copies);
            decimal pageCost = ink + rest;
            var p = pages[i];
            result.Add(new PageCost(i + 1, settings.Color && p.HasColor, p.C, p.M, p.Y, settings.Color ? p.K : p.Gray,
                p.IsBlank, ink, rest, pageCost, (pageCost - paperPerPage) * marginFactor + paperPerPage * paperFactor));
        }
        return result;
    }
}
