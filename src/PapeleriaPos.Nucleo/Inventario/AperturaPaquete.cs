namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// Abrir un paquete (docs/fase-2-inventario.md, entrega 2): cuando el paquete y la pieza son productos distintos
/// (como vienen de eleventa), sale el paquete y entran sus piezas con el mismo costo. Es el documento de los dos
/// movimientos <see cref="TipoMovimiento.AperturaPaquete"/> del kardex: el rastro no se pierde.
/// </summary>
public class AperturaPaquete
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid OrigenId { get; init; }

    /// <summary>Cuántos paquetes se abrieron (en la unidad del paquete).</summary>
    public Cantidad Cantidad { get; init; }

    public Guid DestinoId { get; init; }

    /// <summary>Cuántas piezas salieron de todos (en la unidad de la pieza).</summary>
    public Cantidad CantidadDestino { get; init; }

    public long CostoCentavos { get; init; }
    public Guid UsuarioId { get; init; }
    public DateTime FechaUtc { get; init; }
}
