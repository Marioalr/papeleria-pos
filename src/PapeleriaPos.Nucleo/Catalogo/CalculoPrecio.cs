namespace PapeleriaPos.Nucleo.Catalogo;

public sealed record PrecioAplicado(Precio Precio, long ImporteCentavos)
{
    /// <summary>El menudeo del mismo grupo de escalas (la de cantidad más baja): contra él se mide el mayoreo.</summary>
    public Precio? Menudeo { get; init; }

    /// <summary>Lo que costaría la misma cantidad a precio de menudeo.</summary>
    public long ImporteMenudeoCentavos { get; init; }

    /// <summary>Si se aplicó una escala de mayoreo que de verdad baja el precio.</summary>
    public bool EsMayoreo => Menudeo is { } m && Precio.CantidadMinima > m.CantidadMinima && ImporteMenudeoCentavos > ImporteCentavos;

    public long AhorroCentavos => EsMayoreo ? ImporteMenudeoCentavos - ImporteCentavos : 0;
}

/// <summary>Las escalas que tocan a una presentación: las suyas propias (raro) o las de la unidad base, de menor a mayor.</summary>
/// <param name="DePresentacion">Si son precios propios de la presentación: las cantidades son de presentaciones, no de piezas.</param>
public sealed record EscalasDePrecio(bool DePresentacion, IReadOnlyList<Precio> Escalas);

public static class CalculoPrecio
{
    /// <summary>
    /// Precio que aplica al vender <paramref name="cantidad"/> de una presentación en una sucursal:
    /// <list type="number">
    /// <item>Si la presentación tiene precio propio (raro), se usa ese, por cada presentación.</item>
    /// <item>Si no, el precio por unidad base: el paquete de 12 cuesta 12 piezas y cuenta como 12 para el mayoreo.</item>
    /// </list>
    /// En ambos casos se prefieren los precios de la sucursal sobre los base, y se toma la escala más alta que
    /// alcance la cantidad. Devuelve <c>null</c> si la variante no tiene precio.
    /// </summary>
    public static PrecioAplicado? Calcular(
        IEnumerable<Precio> precios, Guid sucursalId, Presentacion presentacion, Cantidad cantidad)
    {
        var deLaVariante = precios.Where(p => p.VarianteId == presentacion.VarianteId).ToList();

        var propias = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId == presentacion.Id), sucursalId);
        if (ElegirEscala(propias, cantidad) is { } propio)
        {
            var menudeo = propias.MinBy(p => p.CantidadMinima)!;
            return new PrecioAplicado(propio, cantidad.Importe(propio.PrecioCentavos))
            {
                Menudeo = menudeo,
                ImporteMenudeoCentavos = cantidad.Importe(menudeo.PrecioCentavos),
            };
        }

        var unidadesBase = cantidad.Por(presentacion.Factor);
        var porUnidad = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId is null), sucursalId);
        if (ElegirEscala(porUnidad, unidadesBase) is not { } escala)
            return null;
        var deMenudeo = porUnidad.MinBy(p => p.CantidadMinima)!;
        return new PrecioAplicado(escala, unidadesBase.Importe(escala.PrecioCentavos))
        {
            Menudeo = deMenudeo,
            ImporteMenudeoCentavos = unidadesBase.Importe(deMenudeo.PrecioCentavos),
        };
    }

    /// <summary>Las escalas de una presentación en la sucursal, con la misma regla que <see cref="Calcular"/> (F11 las muestra).</summary>
    public static EscalasDePrecio Escalas(IEnumerable<Precio> precios, Guid sucursalId, Presentacion presentacion)
    {
        var deLaVariante = precios.Where(p => p.VarianteId == presentacion.VarianteId).ToList();
        var propias = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId == presentacion.Id), sucursalId);
        return propias.Count > 0
            ? new EscalasDePrecio(true, propias.OrderBy(p => p.CantidadMinima).ToList())
            : new EscalasDePrecio(false, DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId is null), sucursalId).OrderBy(p => p.CantidadMinima).ToList());
    }

    private static List<Precio> DeSucursalOBase(IEnumerable<Precio> precios, Guid sucursalId)
    {
        var lista = precios.ToList();
        var deSucursal = lista.Where(p => p.SucursalId == sucursalId).ToList();
        return deSucursal.Count > 0 ? deSucursal : lista.Where(p => p.SucursalId is null).ToList();
    }

    /// <summary>La escala más alta que alcanza la cantidad; si no alcanza ninguna (medio metro), la más baja.</summary>
    private static Precio? ElegirEscala(List<Precio> escalas, Cantidad cantidad)
    {
        var abs = cantidad.Abs();
        return escalas.Where(p => p.CantidadMinima <= abs).MaxBy(p => p.CantidadMinima)
            ?? escalas.MinBy(p => p.CantidadMinima);
    }
}
