using System.Text.Json.Serialization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Los reportes de ventas (entrega 9, parte 1, 2026-10-22; docs/propuesta-interfaz.md «Reportes») ──
// Los que el dueño usa en eleventa: hoy, la semana, el mes y el año a la fecha, siempre contra el mismo periodo del año
// pasado (Reportes.PeriodoDeReporte dice cuál). Solo leen y solo con la caja principal, para quien tiene «Ver reportes de
// ventas» en «Puede»; costos, utilidad y margen, solo para quien tiene «Ver costos, utilidad y margen» (a los demás no se les
// mandan, ni vacíos).

/// <summary>De qué es el reporte. Viaja por su número: las nuevas van al final.</summary>
public enum PeriodoReporte
{
    /// <summary>Un día, por hora.</summary>
    Hoy,

    /// <summary>De lunes a domingo, por día.</summary>
    Semana,

    /// <summary>Un mes, por día.</summary>
    Mes,

    /// <summary>Un año, por mes.</summary>
    Anio,
}

/// <summary>
/// El reporte de ventas de un periodo contra el mismo periodo del año pasado. Lo de este POS (las ventas con sus
/// devoluciones y cancelaciones) más el historial de eleventa donde lo haya: la importación final ya no trae lo que mandó el
/// puente, así que nada cuenta dos veces. En el piloto con eleventa solo está lo de la caja 2, sin comparar.
/// </summary>
/// <param name="Periodo">Hoy, la semana, el mes o el año del día que se pidió.</param>
/// <param name="Desde">El primer día del periodo (hora de la tienda).</param>
/// <param name="Hasta">Su último día (incluido).</param>
/// <param name="Hoy">Hoy en la caja principal: no hay reporte de un periodo que todavía no llega.</param>
/// <param name="AhoraLocal">Si el periodo incluye hoy, la hora de la caja principal: el periodo va hasta ahora («a la fecha»).</param>
/// <param name="DesdeAnterior">El primer día del mismo periodo del año pasado.</param>
/// <param name="HastaAnterior">Su último día (incluido).</param>
/// <param name="CorteAnteriorLocal">
/// A la fecha, hasta cuándo se compara el año pasado (sin incluirlo): el mismo momento, en el día que le toca. Vacío si el
/// periodo ya pasó (se comparan completos).
/// </param>
/// <param name="Actual">Las cifras del periodo.</param>
/// <param name="Anterior">
/// Las del año pasado, hasta el mismo momento. Vacío si no hay ninguna venta en todo ese periodo (ni en el historial de
/// eleventa) y en el piloto, donde no hay con qué comparar.
/// </param>
/// <param name="Serie">La gráfica: por hora (hoy), por día (la semana y el mes) o por mes (el año).</param>
/// <param name="Departamentos">De lo que más se vendió a lo que menos; al final las impresiones y el artículo común.</param>
/// <param name="Cajeros">Lo cobrado por cada cajero y sus diferencias de corte, de lo vendido en este POS: el historial de eleventa no trae el cajero.</param>
/// <param name="FormasDePago">Cómo se pagó, de lo vendido en este POS (el historial no trae la forma de pago); lo devuelto, restado de cómo se regresó.</param>
/// <param name="MasVendidos">Lo que más se vende en el periodo, por lo vendido.</param>
/// <param name="ConUtilidad">Quien lo pidió ve costos, utilidad y margen: si no, no viene ninguno.</param>
/// <param name="Piloto">El piloto con eleventa: solo lo que vendió la caja 2; lo de la caja principal está en los reportes de eleventa.</param>
public sealed record ReporteVentasDto(
    PeriodoReporte Periodo, DateOnly Desde, DateOnly Hasta, DateOnly Hoy, DateTime? AhoraLocal, DateOnly DesdeAnterior, DateOnly HastaAnterior,
    DateTime? CorteAnteriorLocal, CifrasReporteDto Actual, CifrasReporteDto? Anterior, IReadOnlyList<PuntoReporteDto> Serie,
    IReadOnlyList<DepartamentoReporteDto> Departamentos, IReadOnlyList<CajeroReporteDto> Cajeros, IReadOnlyList<FormaPagoReporteDto> FormasDePago,
    IReadOnlyList<ProductoReporteDto> MasVendidos, bool ConUtilidad, bool Piloto)
{
    /// <summary>El periodo incluye hoy: va hasta ahora, y el año pasado se compara hasta el mismo momento.</summary>
    [JsonIgnore]
    public bool ALaFecha => AhoraLocal is not null;
}

/// <summary>
/// Las cifras de un periodo. La venta neta es la del corte del día: lo cobrado en los tickets que no se cancelaron menos lo
/// devuelto en el periodo (de tickets de cualquier día). Del historial de eleventa, sus tickets sin los cancelados: eleventa
/// ya les bajó lo devuelto.
/// </summary>
/// <param name="VentaCentavos">La venta neta: <paramref name="VendidoCentavos"/> menos <paramref name="DevueltoCentavos"/>.</param>
/// <param name="Tickets">Los cobrados que no se cancelaron (con los del historial).</param>
/// <param name="VendidoCentavos">Lo cobrado en esos tickets, antes de las devoluciones, sin el redondeo al peso (como el ticket).</param>
/// <param name="DevueltoCentavos">Lo devuelto en el periodo, sin las cancelaciones.</param>
/// <param name="Devoluciones">Cuántas devoluciones.</param>
/// <param name="Cancelados">Los tickets cobrados en el periodo que después se cancelaron: no cuentan.</param>
/// <param name="CanceladosCentavos">Lo que sumaban.</param>
/// <param name="DelHistorialCentavos">De la venta neta, lo que sale del historial de eleventa.</param>
/// <param name="DelHistorialTickets">De los tickets, los del historial de eleventa.</param>
/// <param name="Utilidad">
/// Lo vendido menos su costo, como en el corte del día (sin el artículo común, que no tiene costo; del historial, con el costo
/// que se capturó en eleventa). Solo para quien ve la utilidad.
/// </param>
public sealed record CifrasReporteDto(
    long VentaCentavos, int Tickets, long VendidoCentavos, long DevueltoCentavos, int Devoluciones, int Cancelados, long CanceladosCentavos,
    long DelHistorialCentavos, int DelHistorialTickets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UtilidadDelDiaDto? Utilidad = null)
{
    /// <summary>La venta neta entre los tickets; vacío sin tickets.</summary>
    [JsonIgnore]
    public long? TicketPromedioCentavos => Tickets > 0 ? Redondeo.Dividir(VentaCentavos, Tickets) : null;
}

/// <summary>Un punto de la gráfica: una hora, un día o un mes, y el que le toca del año pasado.</summary>
/// <param name="InicioLocal">Cuándo empieza (hora de la tienda).</param>
/// <param name="InicioAnteriorLocal">Cuándo empieza el que le toca del año pasado; vacío si no hay (el 29 de febrero) o en el piloto.</param>
/// <param name="ActualCentavos">La venta neta; vacío si todavía no llega.</param>
/// <param name="AnteriorCentavos">La del año pasado, completa; vacío si no hay con qué comparar.</param>
/// <param name="EnCurso">Es el de ahora: va a la mitad.</param>
public sealed record PuntoReporteDto(
    DateTime InicioLocal, DateTime? InicioAnteriorLocal, long? ActualCentavos, long? AnteriorCentavos, bool EnCurso = false);

/// <summary>De qué es un renglón de las ventas por departamento.</summary>
public enum ClaseVenta
{
    Departamento,

    /// <summary>Las impresiones del módulo de impresión: su costo es el de cada trabajo.</summary>
    Impresiones,

    /// <summary>Lo que se vendió como artículo común: no tiene costo.</summary>
    ArticuloComun,
}

/// <summary>Lo vendido de un departamento en el periodo (menos lo devuelto) y en el mismo periodo del año pasado.</summary>
/// <param name="AnteriorCentavos">Vacío si no hay con qué comparar.</param>
/// <param name="CostoCentavos">Su costo; vacío para quien no ve la utilidad (y en el artículo común, que no tiene).</param>
public sealed record DepartamentoReporteDto(
    string Nombre, ClaseVenta Clase, long VentaCentavos, long? AnteriorCentavos,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CostoCentavos = null)
{
    [JsonIgnore]
    public long? UtilidadCentavos => VentaCentavos - CostoCentavos;

    /// <summary>En centésimas (3250 = 32.5 %); vacío sin el costo o sin venta.</summary>
    [JsonIgnore]
    public int? MargenCentesimas => CostoCentavos is { } costo && VentaCentavos > 0 ? Margen.Calcular(VentaCentavos, costo) : null;
}

/// <summary>Lo de un cajero en el periodo, de lo vendido en este POS.</summary>
/// <param name="Tickets">Los que cobró y no se cancelaron.</param>
/// <param name="VentaCentavos">Lo que cobró menos lo que se devolvió en el periodo de tickets suyos.</param>
/// <param name="Cortes">Los cortes de sus turnos hechos en el periodo (en el piloto el corte se hace en eleventa: no hay).</param>
/// <param name="DiferenciaCentavos">La suma de sus diferencias de corte: negativo, faltó.</param>
/// <param name="FaltanteCentavos">Lo que faltó en los cortes en que faltó.</param>
/// <param name="SobranteCentavos">Lo que sobró en los cortes en que sobró.</param>
public sealed record CajeroReporteDto(
    Guid Id, string Nombre, int Tickets, long VentaCentavos, int Cortes, long DiferenciaCentavos, long FaltanteCentavos, long SobranteCentavos);

/// <summary>Lo de una forma de pago: lo cobrado así (el efectivo, sin el redondeo al peso), menos lo devuelto así.</summary>
public sealed record FormaPagoReporteDto(FormaPago Forma, long Centavos);

/// <summary>Un producto de lo que más se vende: lo vendido menos lo devuelto (los juntados, en el que quedó).</summary>
/// <param name="CantidadMilesimas">En su unidad base (piezas, metros, kilos), en milésimas.</param>
public sealed record ProductoReporteDto(Guid VarianteId, string Nombre, UnidadBase Unidad, long CantidadMilesimas, long VentaCentavos)
{
    [JsonIgnore]
    public decimal Cantidad => CantidadMilesimas / (decimal)Nucleo.Cantidad.Escala;
}
