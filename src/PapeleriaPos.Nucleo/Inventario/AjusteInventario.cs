namespace PapeleriaPos.Nucleo.Inventario;

public enum CausaMerma
{
    Danado,
    Caducado,
    Perdido,
    Otro,
}

/// <summary>
/// Un ajuste o una merma (docs/fase-2-inventario.md, entrega 3): el documento de su movimiento del kardex, con quién lo
/// hizo, quién lo autorizó y por qué. El ajuste corrige la existencia a la que se encontró; la merma saca lo dañado,
/// caducado o perdido y su costo es pérdida.
/// </summary>
public class AjusteInventario
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid VarianteId { get; init; }

    /// <summary><see cref="TipoMovimiento.Ajuste"/> o <see cref="TipoMovimiento.Merma"/>.</summary>
    public TipoMovimiento Tipo { get; init; }

    /// <summary>Con signo, en unidad base: lo que entró (+) o salió (−).</summary>
    public Cantidad Cantidad { get; init; }

    /// <summary>Con signo, como su movimiento.</summary>
    public long CostoCentavos { get; init; }

    public CausaMerma? Causa { get; init; }
    public required string Motivo { get; init; }
    public Guid UsuarioId { get; init; }
    public Guid? AutorizoId { get; init; }
    public DateTime FechaUtc { get; init; }
}
