namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Copia de cada cambio de precio. La capa de datos la escribe sola al guardar un <see cref="Precio"/>.</summary>
public class PrecioHistorial
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid PrecioId { get; set; }
    public Guid VarianteId { get; set; }
    public Guid? PresentacionId { get; set; }
    public Guid? SucursalId { get; set; }
    public Cantidad CantidadMinima { get; set; }

    /// <summary>Vacío cuando el precio es nuevo.</summary>
    public long? PrecioAnteriorCentavos { get; set; }

    /// <summary>Vacío cuando el precio se quitó.</summary>
    public long? PrecioNuevoCentavos { get; set; }

    public DateTime FechaUtc { get; set; }

    /// <summary>
    /// La versión del catálogo con que quedó el cambio (entrega 5): el cobro de una caja la compara con la de su copia, para
    /// no cobrar precios que el cajero no vio. Una escala de mayoreo que se quitó solo deja rastro aquí. Vacía en los cambios
    /// de antes.
    /// </summary>
    public long? VersionCatalogo { get; set; }
}
