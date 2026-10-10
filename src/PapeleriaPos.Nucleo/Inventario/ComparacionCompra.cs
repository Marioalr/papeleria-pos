namespace PapeleriaPos.Nucleo.Inventario;

public enum DiferenciaCompra
{
    /// <summary>Llegó lo que dice la nota.</summary>
    Ninguna,

    /// <summary>La nota dice más de lo que llegó: se reclama.</summary>
    Falto,

    /// <summary>Llegó más de lo que dice la nota.</summary>
    Sobro,

    /// <summary>Llegó algo que no está en la nota (otro producto, o el mismo con otro código).</summary>
    NoVieneEnLaNota,

    /// <summary>Está en la nota y no llegó nada.</summary>
    NoLlego,
}

/// <summary>Una variante de la compra: lo que dice la nota contra lo que se contó, en unidad base.</summary>
/// <param name="ImporteNotaCentavos">Lo que cobra la nota por esta variante (todos sus renglones).</param>
public sealed record ComparacionVariante(Guid VarianteId, Cantidad Nota, Cantidad Contado, long ImporteNotaCentavos)
{
    public Cantidad Diferencia => Contado - Nota;

    public DiferenciaCompra Tipo => (Nota.Milesimas, Contado.Milesimas) switch
    {
        (0, _) => DiferenciaCompra.NoVieneEnLaNota,
        (_, 0) => DiferenciaCompra.NoLlego,
        var (n, c) when n == c => DiferenciaCompra.Ninguna,
        var (n, c) when n > c => DiferenciaCompra.Falto,
        _ => DiferenciaCompra.Sobro,
    };

    /// <summary>Centavos por unidad base según la nota, sin redondear. Vacío si no viene en la nota.</summary>
    public decimal? CostoUnitario => Nota.EsPositiva ? ImporteNotaCentavos * Cantidad.Escala / (decimal)Nota.Milesimas : null;

    /// <summary>
    /// El costo con que entra lo contado al kardex: la parte proporcional de la nota (lo que faltó no entra, y no se
    /// paga: se reclama). Lo que no viene en la nota entra al <paramref name="costoConocido"/> por unidad.
    /// </summary>
    public long CostoDeEntrada(long costoConocido) =>
        Nota.EsPositiva
            ? Contado == Nota ? ImporteNotaCentavos : Redondeo.Dividir((Int128)ImporteNotaCentavos * Contado.Milesimas, Nota.Milesimas)
            : Contado.Importe(costoConocido);
}

public static class ComparacionCompra
{
    /// <summary>Junta por variante lo contado (en cualquier presentación) y lo que dice la nota (lo ya ligado).</summary>
    public static List<ComparacionVariante> Comparar(IEnumerable<CompraConteo> conteo, IEnumerable<CompraRenglon> renglones)
    {
        var contado = conteo.GroupBy(c => c.VarianteId).ToDictionary(g => g.Key, g => g.Aggregate(Cantidad.Cero, (s, c) => s + c.CantidadBase));
        var nota = renglones.Where(r => r.VarianteId is not null).GroupBy(r => r.VarianteId!.Value).ToDictionary(g => g.Key,
            g => (Cantidad: g.Aggregate(Cantidad.Cero, (s, r) => s + r.CantidadBase), Importe: g.Sum(r => r.ImporteCentavos)));
        return nota.Keys.Union(contado.Keys)
            .Select(v => new ComparacionVariante(v, nota.TryGetValue(v, out var n) ? n.Cantidad : Cantidad.Cero,
                contado.GetValueOrDefault(v, Cantidad.Cero), n.Importe))
            .ToList();
    }
}
