namespace PapeleriaPos.Nucleo.Catalogo;

public enum TipoDecisionDepuracion
{
    /// <summary>Ya no existe: sale de la venta y su existencia queda en cero.</summary>
    DarDeBaja,

    /// <summary>Estaba capturado dos veces: códigos, existencia y proveedores pasan al otro.</summary>
    Juntar,

    /// <summary>Se vende por otra unidad (el listón en metro, no en kilo).</summary>
    CambiarUnidad,
}

/// <summary>
/// Lo que se decidió al depurar el catálogo importado (docs/plan.md, depuración del inventario). Guarda el ID de
/// eleventa de lo que se tocó para repetir la misma decisión en la importación final, antes del piloto.
/// </summary>
public class DecisionDepuracion
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public TipoDecisionDepuracion Tipo { get; set; }
    public Guid VarianteId { get; set; }

    /// <summary>Al juntar: la variante que se queda.</summary>
    public Guid? DestinoId { get; set; }

    /// <summary>El ID en eleventa de la variante (y del destino), si vino de la importación.</summary>
    public string? OrigenEleventa { get; set; }
    public string? DestinoEleventa { get; set; }

    /// <summary>Al cambiar la unidad: la nueva, cuántas nuevas hay en una de antes (vacío: se cuenta de nuevo) y el precio de menudeo.</summary>
    public UnidadBase? Unidad { get; set; }
    public long? FactorMilesimas { get; set; }
    public long? PrecioCentavos { get; set; }

    /// <summary>Cómo se lee ("«Goma blanca» se juntó con «Goma Pelikan blanca»").</summary>
    public required string Descripcion { get; set; }

    public Guid? UsuarioId { get; set; }
    public DateTime FechaUtc { get; set; }
}
