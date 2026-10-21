namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// Suma del kardex de una variante en una sucursal. Es una copia para consultar rápido: se actualiza junto con cada
/// movimiento y se puede reconstruir desde el kardex en cualquier momento.
/// </summary>
public class Existencia
{
    public Guid SucursalId { get; set; }
    public Guid VarianteId { get; set; }
    public Cantidad Cantidad { get; set; }

    /// <summary>Valor del inventario: suma de los costos de los movimientos.</summary>
    public long CostoCentavos { get; set; }

    /// <summary>Centavos por unidad base, sin redondear. Vacío si no hay existencia.</summary>
    public decimal? CostoPromedio => Cantidad.EsPositiva ? CostoCentavos * Cantidad.Escala / (decimal)Cantidad.Milesimas : null;

    /// <summary>
    /// La versión de las existencias con que cambió por última vez la cantidad (entrega 5, parte 2): las cajas piden «lo que
    /// cambió desde la mía» y ven «Quedan 3» al día. La pone la base al guardar; una venta no sube la del catálogo.
    /// </summary>
    public long Version { get; set; }

    public void Aplicar(MovimientoInventario movimiento)
    {
        if (movimiento.SucursalId != SucursalId || movimiento.VarianteId != VarianteId)
            throw new InvalidOperationException("El movimiento es de otra sucursal o variante.");

        Cantidad += movimiento.Cantidad;
        CostoCentavos = checked(CostoCentavos + movimiento.CostoCentavos);
    }

    /// <summary>Recalcula todas las existencias desde el kardex.</summary>
    public static List<Existencia> Reconstruir(IEnumerable<MovimientoInventario> movimientos) =>
        movimientos
            .GroupBy(m => (m.SucursalId, m.VarianteId))
            .Select(g =>
            {
                var existencia = new Existencia { SucursalId = g.Key.SucursalId, VarianteId = g.Key.VarianteId };
                foreach (var m in g)
                    existencia.Aplicar(m);
                return existencia;
            })
            .ToList();
}
