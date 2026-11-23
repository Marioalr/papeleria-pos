using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

// El celular cobra (dueño, 2026-11-22; docs/celular-como-caja.md, «Cobrar en el celular»): el ticket armado en el celular se cobra en
// una sola llamada, en el turno abierto de una caja de su sucursal. Como al mandarlo, el celular solo dice qué y cuánto: los precios y
// las promociones los pone el servicio con su catálogo, nunca el celular. Cotizar es el mismo cálculo sin cobrar.

/// <summary>Lo que manda el celular para cobrar un ticket (<c>POST /api/cajas/{id}/cobrar</c>) o cotizarlo (<c>…/cotizar</c>).</summary>
/// <param name="Id">
/// Lo pone el celular, uno por ticket: es el Id de la venta. Si la respuesta no llega y lo manda otra vez, no se cobra dos veces: contesta
/// la venta que ya quedó (<see cref="CobroDelCelularDto.YaCobrada"/>). Al cotizar puede ir vacío.
/// </param>
/// <param name="Renglones">Como al mandarlo a una caja: productos por variante o código, o artículos comunes con su precio.</param>
/// <param name="Cobros">
/// Cómo paga: efectivo (el monto es lo que entregó el cliente), tarjeta (exacta, con su referencia si se quiere) o transferencia
/// (exacta; queda por verificar). Al cotizar es opcional: con él, dice cuánto falta o cuánto es el cambio.
/// </param>
/// <param name="Caja">
/// La caja en cuyo turno entra el cobro (de <c>para-mandar</c>, las de <c>turnoAbierto</c>). Vacía: la única de la sucursal con turno
/// abierto. No se llama <c>CajaId</c>: ese nombre, en el cuerpo, es el de quien llama.
/// </param>
/// <param name="CajeroId">
/// Quién cobra: el usuario que entró en el celular (<c>POST /api/sesion</c>). Vacío: el cajero del turno donde entra el cobro.
/// </param>
/// <param name="TotalVisto">
/// El total que el celular le enseñó al cliente (sin redondeo). Si viene y el del servicio es otro (cambió un precio o una promoción
/// después de su copia), no se cobra: 409 <c>total-distinto</c>, para revisarlo con el cliente.
/// </param>
public sealed record SolicitudCobroDelCelular(
    Guid Id, IReadOnlyList<RenglonMandado> Renglones, IReadOnlyList<CobroSolicitado>? Cobros = null, Guid? Caja = null, Guid? CajeroId = null,
    long? TotalVisto = null);

/// <summary>Un renglón del ticket como lo cobra el servicio: con su precio, su importe y su promoción o su mayoreo.</summary>
/// <param name="ArticuloComun">Sin variante: descripción y precio a mano, sin inventario.</param>
public sealed record RenglonCotizadoDto(
    Guid? VarianteId, Guid? PresentacionId, string Descripcion, long CantidadMilesimas, long PrecioCentavos, long ImporteCentavos,
    long AhorroMayoreoCentavos, long AhorroPromocionCentavos, string? Promocion, bool ArticuloComun);

/// <summary>Con los cobros que se mandaron: lo que se cobra en efectivo (ya redondeado al peso), lo que falta o el cambio.</summary>
/// <param name="SinEfectivoCentavos">Lo de tarjeta y transferencia: exacto.</param>
/// <param name="EfectivoCentavos">Lo que se cobra en efectivo, al peso (0 si tarjeta y transferencia cubren todo).</param>
/// <param name="RedondeoCentavos">Lo que el redondeo le sumó (+) o quitó (−) al efectivo.</param>
/// <param name="RecibidoCentavos">El efectivo que entregó el cliente.</param>
public sealed record PagoCotizadoDto(
    long SinEfectivoCentavos, long EfectivoCentavos, long RedondeoCentavos, long RecibidoCentavos, long FaltanCentavos, long CambioCentavos);

/// <summary>
/// <c>POST /api/cajas/{id}/cotizar</c>: el ticket con los precios y las promociones de hoy del servicio, sin cobrar. Es el mismo cálculo que
/// el cobro (la misma función), así que no se separan.
/// </summary>
/// <param name="TotalCentavos">Exacto: la suma de los renglones, con IVA. Es el que se paga con tarjeta o transferencia.</param>
/// <param name="EfectivoCentavos">Lo que se cobra si todo es en efectivo: el total al peso (fase 3).</param>
/// <param name="RedondeoCentavos"><paramref name="EfectivoCentavos"/> menos <paramref name="TotalCentavos"/>.</param>
/// <param name="Pago">Solo si se mandaron cobros.</param>
public sealed record CotizacionDelCelularDto(
    IReadOnlyList<RenglonCotizadoDto> Lineas, int Renglones, decimal Articulos, long TotalCentavos, long EfectivoCentavos, long RedondeoCentavos,
    PagoCotizadoDto? Pago);

/// <summary><c>POST /api/cajas/{id}/cobrar</c>: la venta cobrada, con su folio y el cambio.</summary>
/// <param name="Folio">El del POS, de la serie de la caja donde entró (como cualquier venta en línea de esa caja).</param>
/// <param name="FolioEleventa">En el piloto, el que le dio eleventa si contestó a tiempo; si no, se le manda después y aquí va vacío.</param>
/// <param name="CajaId">La caja en cuyo turno entró (su corte cuenta el efectivo).</param>
/// <param name="TotalCentavos">Exacto, con IVA.</param>
/// <param name="RedondeoCentavos">El del efectivo al peso: lo que pagó el cliente es el total más esto.</param>
/// <param name="CobradoCentavos">El total más el redondeo.</param>
/// <param name="RecibidoCentavos">El efectivo que entregó el cliente (0 sin efectivo).</param>
/// <param name="YaCobrada">El Id ya era una venta (se perdió la respuesta, o ya se cobró en una caja): es esa, como se cobró, y no se cobró otra vez.</param>
/// <param name="Ticket">El ticket como lo imprime la caja (para enseñarlo o compartirlo); el celular no imprime.</param>
public sealed record CobroDelCelularDto(
    Guid Id, string Folio, int? FolioEleventa, Guid CajaId, string Caja, string Cajero, int Renglones, decimal Articulos, long TotalCentavos,
    long RedondeoCentavos, long CobradoCentavos, long RecibidoCentavos, long CambioCentavos, IReadOnlyList<CobroTicketDto> Cobros, bool YaCobrada,
    TicketDto Ticket);
