namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// Un renglón del kardex. Solo se agregan: nunca se editan ni se borran, y un error se corrige con otro movimiento.
/// Se crean con <see cref="Kardex"/>, que valida y calcula el costo de las salidas.
/// </summary>
public class MovimientoInventario
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid VarianteId { get; init; }
    public DateTime FechaUtc { get; init; }
    public TipoMovimiento Tipo { get; init; }

    /// <summary>Con signo, en unidad base: + entra, − sale.</summary>
    public Cantidad Cantidad { get; init; }

    /// <summary>Costo total del movimiento, con signo. Total y no unitario para que el valor del inventario sea exacto.</summary>
    public long CostoCentavos { get; init; }

    /// <summary>De dónde salió (venta, compra, conteo…).</summary>
    public string? DocumentoTipo { get; init; }
    public Guid? DocumentoId { get; init; }

    /// <summary>Obligatorio en ajustes y mermas.</summary>
    public string? Motivo { get; init; }
}
