namespace PapeleriaPos.Nucleo.Historico;

/// <summary>
/// Venta importada de eleventa. No se mezcla con las ventas del POS (no tiene folios por caja, turnos ni cortes):
/// solo alimenta el análisis ABC, la temporada y el sugerido de compra.
/// </summary>
public class VentaHistorica
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public DateTime FechaUtc { get; set; }
    public required string FolioOrigen { get; set; }
    public long TotalCentavos { get; set; }
    public bool Cancelada { get; set; }

    public List<VentaHistoricaLinea> Lineas { get; set; } = [];
}

public class VentaHistoricaLinea
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid VentaHistoricaId { get; set; }
    public Guid VarianteId { get; set; }
    public Cantidad Cantidad { get; set; }

    /// <summary>Precio por unidad base, con IVA, como lo cobró eleventa.</summary>
    public long PrecioCentavos { get; set; }

    /// <summary>Lo que se cobró por la línea, con IVA. No siempre es precio × cantidad (precio propio de un paquete).</summary>
    public long ImporteCentavos { get; set; }

    /// <summary>Costo total de la línea, como lo guardó eleventa.</summary>
    public long CostoCentavos { get; set; }
}
