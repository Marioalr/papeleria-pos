using System.Globalization;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Trabajos mandados casi al mismo tiempo, como los lotes de Control Print (QueueViewModel.AssignBatches): lo que imprimió un
/// cliente de una vez. Van del más nuevo al más viejo.
/// </summary>
/// <param name="PrimeroLocal">El más viejo del lote, en hora de la tienda.</param>
/// <param name="UltimoLocal">El más nuevo.</param>
public sealed record LoteDeImpresiones<T>(IReadOnlyList<T> Trabajos, DateTime PrimeroLocal, DateTime UltimoLocal, long TotalCentavos)
{
    /// <summary>«Hoy 10:31», «Hoy 10:31–10:33» o «lun 26/10 18:02», como en Control Print.</summary>
    public string Cuando(DateTime ahoraLocal)
    {
        var dia = PrimeroLocal.Date == ahoraLocal.Date
            ? "Hoy"
            : PrimeroLocal.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es-MX")).Replace(".", "");
        var desde = PrimeroLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        var hasta = UltimoLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        return desde == hasta ? $"{dia} {desde}" : $"{dia} {desde}–{hasta}";
    }
}

public static class LotesDeImpresiones
{
    /// <summary>Dos trabajos a esto o menos uno del otro van en el mismo lote (como en Control Print).</summary>
    public static readonly TimeSpan Separacion = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Los lotes, del más nuevo al más viejo: se corta donde dos trabajos seguidos están a más de <see cref="Separacion"/>. Cada
    /// lote, con sus trabajos del más nuevo al más viejo.
    /// </summary>
    public static List<LoteDeImpresiones<T>> Agrupar<T>(IEnumerable<T> trabajos, Func<T, DateTime> detectadoUtc, Func<T, long> precioCentavos)
    {
        var ordenados = trabajos.OrderByDescending(detectadoUtc).ToList();
        var lotes = new List<LoteDeImpresiones<T>>();
        var inicio = 0;
        for (var i = 1; i <= ordenados.Count; i++)
        {
            if (i < ordenados.Count && detectadoUtc(ordenados[i - 1]) - detectadoUtc(ordenados[i]) <= Separacion)
                continue;
            var lote = ordenados.GetRange(inicio, i - inicio);
            lotes.Add(new LoteDeImpresiones<T>(lote, HoraMexico.ALocal(detectadoUtc(lote[^1])), HoraMexico.ALocal(detectadoUtc(lote[0])),
                lote.Sum(precioCentavos)));
            inicio = i;
        }
        return lotes;
    }
}
