namespace PapeleriaPos.Nucleo.Inventario;

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
}

public static class TipoMovimientoExtensiones
{
    public static bool PuedeEntrar(this TipoMovimiento tipo) => tipo is
        TipoMovimiento.InventarioInicial or TipoMovimiento.Compra or TipoMovimiento.Devolucion
        or TipoMovimiento.TraspasoEntrada or TipoMovimiento.Ajuste or TipoMovimiento.Conteo
        or TipoMovimiento.AperturaPaquete;

    public static bool PuedeSalir(this TipoMovimiento tipo) => tipo is
        TipoMovimiento.Venta or TipoMovimiento.Merma or TipoMovimiento.TraspasoSalida
        or TipoMovimiento.ConsumoServicio or TipoMovimiento.Ajuste or TipoMovimiento.Conteo
        or TipoMovimiento.AperturaPaquete;

    public static bool RequiereMotivo(this TipoMovimiento tipo) => tipo is TipoMovimiento.Ajuste or TipoMovimiento.Merma;
}
