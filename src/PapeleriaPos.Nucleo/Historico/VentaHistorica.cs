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

    /// <summary>
    /// Un artículo común de eleventa (código <c>COMUN-…</c>, departamento «Productos Comunes»): no es un producto del catálogo
    /// (su variante es la inactiva que se le creó al importar) y no tiene costo (eleventa le pone de ganancia todo el precio).
    /// Los reportes de ventas lo cuentan como el artículo común de aquí: aparte y fuera de la utilidad (revisión del PR #131,
    /// 2026-10-22).
    /// </summary>
    public bool EsArticuloComun { get; set; }
}
