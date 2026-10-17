using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Las ventas de otros días y el historial de cortes (paridad con eleventa, 2026-10-17) ──
// Como «Ventas del día y devoluciones» y el «Historial de cortes» de eleventa: un día a la vez (el de hoy, o el que se
// elija), con el filtro de caja y de cajero, y la búsqueda por folio en cualquier día. Solo leen, y solo con la caja
// principal: sin conexión, la caja tiene su copia para vender y nada más.

/// <summary>Una caja o un cajero, para los filtros de esas listas.</summary>
public sealed record OpcionFiltroDto(Guid Id, string Nombre);

/// <summary>
/// Las ventas de un día, de una caja o de todas y de un cajero o de todos; o las que tienen un folio, de cualquier día y caja.
/// </summary>
/// <param name="Fecha">El día que se ve (hora de la tienda). En la búsqueda por folio, hoy.</param>
/// <param name="Hoy">Hoy en la caja principal: de ahí no se pasa.</param>
/// <param name="Ventas">Las más nuevas primero.</param>
/// <param name="Cajas">Las cajas de la sucursal, para el filtro.</param>
/// <param name="Cajeros">Quienes cobraron ese día en esas cajas (y el que se eligió), para el filtro.</param>
/// <param name="Folio">El folio que se buscó, como se leyó; vacío si es la lista de un día.</param>
/// <param name="HayMas">Ese día tiene más de las que se mandan: se ven las más nuevas.</param>
public sealed record VentasDeUnDiaDto(
    DateOnly Fecha, DateOnly Hoy, IReadOnlyList<VentaDeUnDiaDto> Ventas, IReadOnlyList<OpcionFiltroDto> Cajas, IReadOnlyList<OpcionFiltroDto> Cajeros,
    string? Folio = null, bool HayMas = false);

/// <summary>Una venta de la lista, con lo que enseña eleventa y su estado.</summary>
/// <param name="Folio">El de la caja (A-000123): con él se abre la devolución.</param>
/// <param name="FolioEleventa">En el piloto, el que le dio eleventa (el que salió impreso, si llegó a tiempo).</param>
/// <param name="TotalCentavos">Lo vendido, sin el redondeo al peso (como en el ticket).</param>
/// <param name="Formas">Cómo se pagó: efectivo, tarjeta, transferencia o crédito, en ese orden.</param>
/// <param name="DevueltoCentavos">Lo que se devolvió de ella (sin las cancelaciones).</param>
/// <param name="DevueltaCompleta">Ya se devolvió todo lo que llevaba.</param>
/// <param name="SeDevuelve">
/// Todavía está en los días para devolver (7, contados desde que se cobró, con la hora de la caja principal), como lo dice la
/// ventana de la devolución.
/// </param>
public sealed record VentaDeUnDiaDto(
    Guid Id, string Folio, int? FolioEleventa, DateTime FechaLocal, Guid CajaId, string Caja, Guid CajeroId, string Cajero, long TotalCentavos,
    int Articulos, IReadOnlyList<FormaPago> Formas, bool Cancelada, long DevueltoCentavos, bool DevueltaCompleta, bool SeDevuelve);

/// <summary>
/// El historial de cortes de un día: los turnos que tocan ese día (se abrieron, se cerraron o seguían abiertos en él), como
/// en el corte del día, cada uno con su corte del turno.
/// </summary>
/// <param name="Fecha">El día que se ve (hora de la tienda).</param>
/// <param name="Hoy">Hoy en la caja principal: de ahí no se pasa.</param>
/// <param name="Turnos">En el orden en que se abrieron.</param>
/// <param name="Cajas">Las cajas de la sucursal, para el filtro.</param>
/// <param name="Cajeros">Los cajeros de los turnos de ese día (y el que se eligió), para el filtro.</param>
/// <param name="Piloto">El piloto con eleventa: el corte de la caja se hace allá.</param>
public sealed record HistorialCortesDto(
    DateOnly Fecha, DateOnly Hoy, IReadOnlyList<TurnoConCorteDto> Turnos, IReadOnlyList<OpcionFiltroDto> Cajas, IReadOnlyList<OpcionFiltroDto> Cajeros,
    bool Piloto);

/// <summary>Un turno del historial, con su corte del turno (el Z).</summary>
/// <param name="Corte">
/// El corte del turno, tal como lo guarda la caja principal (el de una caja sin conexión ya se cuadró al subir): lo esperado,
/// lo contado, la diferencia, quién lo hizo y cuándo. Vacío si el turno sigue abierto o si su corte se hizo en eleventa.
/// </param>
/// <param name="CorteEnEleventa">
/// El piloto con eleventa: el corte de este turno se hace (o se hizo) en eleventa, y aquí no hay lo contado: sigue abierto
/// en el piloto, o se cerró sin corte Z (así lo cierra el corte de eleventa cuando la caja lo toma). Solo para convivir con
/// eleventa.
/// </param>
public sealed record TurnoConCorteDto(
    Guid Id, Guid CajaId, string Caja, Guid CajeroId, string Cajero, DateTime AbiertoLocal, DateTime? CerradoLocal, CorteDto? Corte,
    bool CorteEnEleventa);
