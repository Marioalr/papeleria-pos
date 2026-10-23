using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo vendido de un renglón: de una variante del catálogo, de las impresiones (<paramref name="EsImpresiones"/>) o del artículo
/// común (sin variante), con su importe y su costo (el que guardó cada venta al cobrar). Lo devuelto va en negativo.
/// </summary>
/// <param name="ServiciosSinCostoCentavos">
/// De su importe, lo de servicios sin costo: lo del catálogo que no lleva inventario y se cobró con costo 0 (sus recetas llegan
/// con la fase 4). Cuenta completo como utilidad.
/// </param>
internal readonly record struct RenglonVendido(Guid? VarianteId, bool EsImpresiones, long ImporteCentavos, long CostoCentavos, long ServiciosSinCostoCentavos)
{
    /// <summary>Un renglón de una venta, o lo devuelto de él (en negativo), con el importe y el costo que se dan.</summary>
    public static RenglonVendido De(Nucleo.Ventas.VentaLinea linea, long importeCentavos, long costoCentavos) =>
        new(linea.VarianteId, linea.EsImpresiones, importeCentavos, costoCentavos,
            linea.VarianteId is not null && !linea.EsImpresiones && !linea.LlevaInventario && linea.CostoCentavos == 0 ? importeCentavos : 0);
}

/// <summary>
/// Lo vendido por departamento y la utilidad, como los arma el corte del día de eleventa (2026-10-17): el corte del día y los
/// reportes de ventas (entrega 9) lo cuentan igual, aquí. Cada renglón suma en el departamento del producto de su variante (los
/// productos sin departamento conocido, en «Sin departamento»); las impresiones y el artículo común van aparte. La utilidad es
/// lo vendido con costo (los departamentos y las impresiones, sin el artículo común, que no tiene) menos su costo.
/// </summary>
internal static class VendidoPorDepartamento
{
    public const string SinDepartamento = "Sin departamento";

    /// <param name="departamentoDe">El departamento del producto de cada variante.</param>
    /// <param name="nombres">El nombre de cada departamento.</param>
    /// <param name="conCostos">Con el costo de cada departamento y de las impresiones: solo para quien ve la utilidad.</param>
    public static (VentasPorDepartamentoDto PorDepartamento, UtilidadDelDiaDto Utilidad) Armar(
        IReadOnlyCollection<RenglonVendido> renglones, IReadOnlyDictionary<Guid, Guid> departamentoDe, IReadOnlyDictionary<Guid, string> nombres,
        bool conCostos)
    {
        var departamentos = renglones.Where(r => r.VarianteId is not null && !r.EsImpresiones)
            .GroupBy(r => departamentoDe.TryGetValue(r.VarianteId!.Value, out var d) ? nombres.GetValueOrDefault(d, SinDepartamento) : SinDepartamento)
            .Select(g => (Nombre: g.Key, Venta: g.Sum(r => r.ImporteCentavos), Costo: g.Sum(r => r.CostoCentavos)))
            .Where(d => d.Venta != 0 || d.Costo != 0)
            .OrderByDescending(d => d.Venta).ThenBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var impresiones = renglones.Where(r => r.EsImpresiones).ToList();
        var (ventaImpresiones, costoImpresiones) = (impresiones.Sum(r => r.ImporteCentavos), impresiones.Sum(r => r.CostoCentavos));
        var porDepartamento = new VentasPorDepartamentoDto(
            departamentos.Select(d => new DepartamentoDelDiaDto(d.Nombre, d.Venta, conCostos ? d.Costo : null)).ToList(),
            ventaImpresiones, conCostos ? costoImpresiones : null,
            renglones.Where(r => r.VarianteId is null && !r.EsImpresiones).Sum(r => r.ImporteCentavos));
        var utilidad = new UtilidadDelDiaDto(departamentos.Sum(d => d.Venta) + ventaImpresiones, departamentos.Sum(d => d.Costo) + costoImpresiones,
            renglones.Sum(r => r.ServiciosSinCostoCentavos));
        return (porDepartamento, utilidad);
    }
}
