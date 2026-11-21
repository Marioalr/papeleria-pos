namespace PapeleriaPos.Nucleo.Contratos;

// El ticket armado en el celular, mandado a una caja (docs/celular-como-caja.md, «Mandar el ticket a una caja»; la preventa de
// la fase 8). El celular solo dice qué y cuánto: los precios los pone el servicio con su catálogo y la caja al armarlo, nunca
// el celular.

/// <summary>Un renglón del ticket del celular: el producto (su variante y presentación de la copia) o, si no los sabe, el código.</summary>
/// <param name="Codigo">El código con que el producto está en el catálogo (la clave del QR); se usa solo sin <paramref name="VarianteId"/>.</param>
public sealed record RenglonMandado(Guid? VarianteId, Guid? PresentacionId, string? Codigo, long CantidadMilesimas);

/// <param name="Id">Lo pone el celular, uno por ticket: si la respuesta no llega y lo manda otra vez, la caja no lo recibe dos veces.</param>
/// <param name="Destino">La caja a la que va.</param>
public sealed record SolicitudMandarTicket(Guid Id, Guid Destino, IReadOnlyList<RenglonMandado> Renglones);

/// <summary>Lo que contesta el servicio al mandar: a qué caja llegó, con cuántos artículos y el total con los precios del servicio.</summary>
/// <param name="Recibido">
/// La caja lo guardó en su espera. Falso solo si la caja lo recogió pero no alcanzó a decir que lo guardó: puede que sí esté;
/// mandarlo otra vez con el mismo Id no lo duplica.
/// </param>
public sealed record TicketMandadoDto(Guid Id, Guid Destino, string Caja, int Renglones, decimal Articulos, long TotalCentavos, bool Recibido);

/// <summary>Una caja a la que se puede mandar: si tiene la venta abierta ahora, quién atiende y cuántos tickets esperan.</summary>
public sealed record CajaParaMandarDto(Guid Id, string Nombre, string Serie, bool Recibe, string? Atiende, int? Esperan);

/// <summary>Un ticket que espera a que la caja lo recoja: las líneas como las arma la caja (sin precios) y sus productos.</summary>
/// <param name="De">El nombre de la caja que lo mandó («Celular de Luis»): sale en su ficha de la espera.</param>
public sealed record TicketRecibidoDto(
    Guid Id, string De, DateTime MandadoUtc, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<ProductoVentaDto> Productos, long TotalCentavos);

/// <summary>Los que la caja ya guardó en su espera: el servicio deja de dárselos y le contesta al celular.</summary>
public sealed record SolicitudRecibidos(IReadOnlyList<Guid> Ids);
