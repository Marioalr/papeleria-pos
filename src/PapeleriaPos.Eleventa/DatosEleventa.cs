namespace PapeleriaPos.Eleventa;

// Las filas tal como vienen de eleventa (esquema 216). Los montos son pesos con IVA incluido y las fechas, hora local.

public sealed record FilaDepartamento(int Id, string Nombre, bool Activo);

/// <param name="TipoVenta">'U' por unidad; 'D' a granel (admite fracciones).</param>
/// <param name="MedidaId">1 pieza, 3 metro, 4 kilo, 5 litro (tabla MEDIDAS).</param>
/// <param name="Precio">Precio final con impuestos (PFINAL).</param>
/// <param name="InventarioMinimo">
/// <c>DINVMINIMO</c>: el inventario mínimo de «Productos bajos en inventario», en la unidad del producto (piezas, metros,
/// kilos). 0 o vacío: no tiene. En la tienda va igual al máximo (base del 2026-10-24).
/// </param>
/// <param name="InventarioMaximo"><c>DINVMAXIMO</c>: el máximo, igual; 0 o vacío: no tiene.</param>
public sealed record FilaProducto(
    int Id, string Codigo, string Descripcion, char TipoVenta, int? MedidaId, double Costo, double Precio, double Mayoreo,
    int? DepartamentoId, bool Eliminado, double InventarioMinimo = 0, double InventarioMaximo = 0);

public sealed record FilaExistencia(int ProductoId, double Cantidad);

public sealed record FilaPromocion(string Codigo, double Desde, double PrecioConImpuestos);

/// <param name="Total">Lo que queda en el ticket: eleventa le baja lo devuelto (la cancelación no).</param>
/// <param name="CreadoEn">
/// Cuándo se abrió el ticket (hora de la tienda). En los que escribió el puente es su marca: cuándo se empezó la venta en la
/// caja nueva, en diezmilésimas de segundo (la importación final la usa para no traerlos dos veces).
/// </param>
public sealed record FilaTicket(int Id, int? Folio, DateTime? VendidoEn, double Total, bool Cancelado, DateTime? CreadoEn = null);

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

    /// <summary>
    /// Las devoluciones y cancelaciones (<c>DEVOLUCIONES</c>, sin sus artículos: eleventa ya se los bajó a sus tickets). Solo
    /// las lee la importación, para saber si las de los tickets de la caja 2 ya están en la base (la importación final).
    /// </summary>
    public List<DevolucionLeida> Devoluciones { get; init; } = [];

    /// <summary>Lo del piloto que la lectura trae en la misma foto (vacío si no se pidió).</summary>
    public DatosDelPiloto? Piloto { get; init; }
}

// ── Solo del piloto (el puente): lo que la lectura de cada pocos minutos trae además del catálogo ──

/// <summary>
/// Lo que la lectura le pregunta a eleventa para el piloto, en la misma foto que el catálogo y las existencias
/// (docs/migracion-eleventa.md, «Lo que falta para el piloto»).
/// </summary>
/// <param name="CajaId">La caja de eleventa de la caja 2 (<c>CAJAS.ID</c>): sus turnos abiertos.</param>
/// <param name="Turnos">Los turnos que adoptó la caja nueva y siguen sin su corte: si eleventa ya los cerró.</param>
/// <param name="Tickets">Los tickets que mandó la caja 2 en los últimos días: lo que se les devolvió o canceló en eleventa (la vuelta).</param>
/// <param name="Huella">Lo que escribió el puente: si eleventa ya no lo tiene, se restauró un respaldo (<see cref="RestauracionDeEleventa"/>).</param>
public sealed record ConsultaDelPiloto(int CajaId, IReadOnlyCollection<int> Turnos, IReadOnlyCollection<int>? Tickets = null, HuellaDelPuente? Huella = null);

/// <param name="Inicio">Hora de la tienda, como todo en eleventa.</param>
public sealed record FilaTurno(int Id, int CajaId, DateTime Inicio, DateTime? Termino, decimal? EfectivoAlCierre);

/// <summary>Lo que faltó (salida) o sobró (entrada) al cerrar un turno en eleventa: el movimiento del corte que lo anota.</summary>
public sealed record FilaCierreDeTurno(int TurnoId, bool Falto, decimal Monto);

public sealed class DatosDelPiloto
{
    /// <summary>Los turnos abiertos de la caja y los adoptados (abiertos o ya cerrados).</summary>
    public List<FilaTurno> Turnos { get; init; } = [];

    public List<FilaCierreDeTurno> Cierres { get; init; } = [];

    /// <summary>Las devoluciones y cancelaciones de los tickets que mandó la caja 2 (las suyas también: la vuelta las reconoce).</summary>
    public List<DevolucionLeida> Devoluciones { get; init; } = [];

    /// <summary>
    /// Eleventa ya no tiene lo que escribió el puente (se restauró un respaldo): entonces no se aplica nada de esta lectura
    /// y el puente se detiene. Vacío si todo sigue ahí (o no se pidió revisarlo).
    /// </summary>
    public Restauracion? Restauracion { get; init; }
}

/// <summary>
/// Las listas <c>IN</c> de Firebird 2.0 admiten menos de 1500 elementos: se mandan de a 1000. Son números, así que van en
/// el texto de la consulta.
/// </summary>
internal static class ListasIn
{
    public const int Tamano = 1000;

    public static IEnumerable<string> Grupos(IEnumerable<int> numeros) =>
        numeros.Distinct().Chunk(Tamano).Select(g => string.Join(", ", g.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
}
