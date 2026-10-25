namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// El tipo de cada movimiento del kardex. La base los guarda por su nombre y las cajas los reciben por su número: nunca se
/// reordenan ni se renombran, y los nuevos van al final.
/// </summary>
public enum TipoMovimiento
{
    InventarioInicial,
    Venta,
    Devolucion,
    Compra,
    Ajuste,
    Merma,
    TraspasoSalida,
    TraspasoEntrada,
    ConsumoServicio,
    AperturaPaquete,
    Conteo,

    /// <summary>
    /// «Agregar inventario», como en eleventa (2026-10-24): lo que llegó de un producto, sin proveedor ni nota
    /// (<see cref="EntradaInventario"/>). No es <see cref="Compra"/>: la compra es de un proveedor, con su folio y su nota, y así
    /// los movimientos, su Excel y el kardex la dicen aparte.
    /// </summary>
    AgregarInventario,
}

public static class TipoMovimientoExtensiones
{
    public static bool PuedeEntrar(this TipoMovimiento tipo) => tipo is
        TipoMovimiento.InventarioInicial or TipoMovimiento.Compra or TipoMovimiento.Devolucion
        or TipoMovimiento.TraspasoEntrada or TipoMovimiento.Ajuste or TipoMovimiento.Conteo
        or TipoMovimiento.AperturaPaquete or TipoMovimiento.AgregarInventario;

    public static bool PuedeSalir(this TipoMovimiento tipo) => tipo is
        TipoMovimiento.Venta or TipoMovimiento.Merma or TipoMovimiento.TraspasoSalida
        or TipoMovimiento.ConsumoServicio or TipoMovimiento.Ajuste or TipoMovimiento.Conteo
        or TipoMovimiento.AperturaPaquete;

    public static bool RequiereMotivo(this TipoMovimiento tipo) => tipo is TipoMovimiento.Ajuste or TipoMovimiento.Merma;
}
