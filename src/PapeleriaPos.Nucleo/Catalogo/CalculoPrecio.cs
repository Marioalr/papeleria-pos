namespace PapeleriaPos.Nucleo.Catalogo;

public sealed record PrecioAplicado(Precio Precio, long ImporteCentavos);

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

        var propio = ElegirEscala(
            DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId == presentacion.Id), sucursalId), cantidad);
        if (propio is not null)
            return new PrecioAplicado(propio, cantidad.Importe(propio.PrecioCentavos));

        var unidadesBase = cantidad.Por(presentacion.Factor);
        var porUnidad = ElegirEscala(
            DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId is null), sucursalId), unidadesBase);
        return porUnidad is null ? null : new PrecioAplicado(porUnidad, unidadesBase.Importe(porUnidad.PrecioCentavos));
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
