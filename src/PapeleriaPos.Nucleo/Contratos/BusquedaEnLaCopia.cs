namespace PapeleriaPos.Nucleo.Contratos;

// Lo que la copia de cada caja necesita para buscar por nombre igual que el servicio (entrega 5, parte 3): la búsqueda va
// contra la copia, sin la red, y da lo mismo que daría la caja principal.

/// <summary>Cuántas veces se vendió un producto en 90 días: ordena la búsqueda cuando el texto no distingue.</summary>
public sealed record VecesVendidoDto(Guid VarianteId, int Veces);

/// <summary>Lo que esta caja eligió la última vez con un texto (su clave, sin acentos ni mayúsculas): sale primero.</summary>
public sealed record ElegidoDto(string Texto, Guid VarianteId);

public static class PrecioDeMenudeo
{
    /// <summary>
    /// El precio por pieza de menudeo: el de la sucursal si tiene, si no el general (el de la cantidad mínima más chica). Lo
    /// que dice la lista de la búsqueda, igual en el servicio y en la copia de la caja.
    /// </summary>
    public static long De(IEnumerable<PrecioDto> precios, Guid sucursalId)
    {
        var porUnidad = precios.Where(p => p.PresentacionId is null).ToList();
        var deSucursal = porUnidad.Where(p => p.SucursalId == sucursalId).ToList();
        return (deSucursal.Count > 0 ? deSucursal : porUnidad.Where(p => p.SucursalId is null)).MinBy(p => p.CantidadMinimaMilesimas)?.PrecioCentavos ?? 0;
    }
}
