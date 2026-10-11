namespace PapeleriaPos.Eleventa;

// Las filas tal como vienen de eleventa (esquema 216). Los montos son pesos con IVA incluido y las fechas, hora local.

public sealed record FilaDepartamento(int Id, string Nombre, bool Activo);

/// <param name="TipoVenta">'U' por unidad; 'D' a granel (admite fracciones).</param>
/// <param name="MedidaId">1 pieza, 3 metro, 4 kilo, 5 litro (tabla MEDIDAS).</param>
/// <param name="Precio">Precio final con impuestos (PFINAL).</param>
public sealed record FilaProducto(
    int Id, string Codigo, string Descripcion, char TipoVenta, int? MedidaId, double Costo, double Precio, double Mayoreo,
    int? DepartamentoId, bool Eliminado);

public sealed record FilaExistencia(int ProductoId, double Cantidad);

public sealed record FilaPromocion(string Codigo, double Desde, double PrecioConImpuestos);

public sealed record FilaTicket(int Id, int? Folio, DateTime? VendidoEn, double Total, bool Cancelado);

/// <param name="TotalArticulo">Vacío en las líneas viejas: entonces el importe es precio × cantidad.</param>
/// <param name="Ganancia">Utilidad por pieza (precio menos costo): la de la línea es la cantidad por esta.</param>
public sealed record FilaLinea(
    int TicketId, string Codigo, string Nombre, double Cantidad, double? TotalArticulo, double PrecioFinal, double Ganancia,
    int? DepartamentoId, bool UsaMayoreo);

public sealed class DatosEleventa
{
    public List<FilaDepartamento> Departamentos { get; init; } = [];
    public List<FilaProducto> Productos { get; init; } = [];
    public List<FilaExistencia> Existencias { get; init; } = [];
    public List<FilaPromocion> Promociones { get; init; } = [];
    public List<FilaTicket> Tickets { get; init; } = [];
    public List<FilaLinea> Lineas { get; init; } = [];
}
