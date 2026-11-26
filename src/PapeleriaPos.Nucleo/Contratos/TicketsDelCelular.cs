namespace PapeleriaPos.Nucleo.Contratos;

// El ticket armado en el celular, mandado a una caja (docs/celular-como-caja.md, «Mandar el ticket a una caja»; la preventa de
// la fase 8). El celular solo dice qué y cuánto: los precios los pone el servicio con su catálogo y la caja al armarlo, nunca
// el celular.

/// <summary>
/// Un renglón del ticket del celular: el producto (su variante y presentación de la copia) o, si no los sabe, el código. O un
/// artículo común (2026-11-22): sin variante, con su descripción y su precio a mano, sin inventario.
/// </summary>
/// <param name="Codigo">El código con que el producto está en el catálogo (la clave del QR); se usa solo sin <paramref name="VarianteId"/>.</param>
/// <param name="Descripcion">La del artículo común (hasta <see cref="ArticuloComunDelCelular.LargoDescripcion"/> letras).</param>
/// <param name="PrecioCentavos">
/// Solo en el artículo común: el precio por pieza, con IVA. Con él, el renglón es artículo común (el código, si viene, no se busca).
/// Un producto del catálogo nunca lleva precio: lo pone el servicio.
/// </param>
public sealed record RenglonMandado(
    Guid? VarianteId, Guid? PresentacionId, string? Codigo, long CantidadMilesimas, string? Descripcion = null, long? PrecioCentavos = null);

/// <summary>Los límites del artículo común que llega del celular (al mandar, cotizar o cobrar).</summary>
public static class ArticuloComunDelCelular
{
    public const int LargoDescripcion = 80;

    /// <summary>$99,999.99: más que eso es un error de dedo.</summary>
    public const long PrecioMaximoCentavos = 9_999_999;
}

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
/// <param name="TurnoAbierto">
/// Tiene un turno abierto (2026-11-22): el celular puede cobrar en él (<c>POST /api/cajas/{id}/cobrar</c> con <c>caja</c>), aunque no
/// tenga la venta abierta.
/// </param>
/// <param name="Impresion">
/// Si el ticket de lo que el celular cobre en esa caja puede salir en su impresora, y por dónde (2026-11-26, paso 11): para la casilla
/// «Imprimir ticket» del celular.
/// </param>
public sealed record CajaParaMandarDto(
    Guid Id, string Nombre, string Serie, bool Recibe, string? Atiende, int? Esperan, bool TurnoAbierto = false, DondeImprimeDto? Impresion = null);

/// <summary>Un ticket que espera a que la caja lo recoja: las líneas como las arma la caja (sin precios) y sus productos.</summary>
/// <param name="De">El nombre de la caja que lo mandó («Celular de Luis»): sale en su ficha de la espera.</param>
public sealed record TicketRecibidoDto(
    Guid Id, string De, DateTime MandadoUtc, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<ProductoVentaDto> Productos, long TotalCentavos);

/// <summary>Los que la caja ya guardó en su espera: el servicio deja de dárselos y le contesta al celular.</summary>
public sealed record SolicitudRecibidos(IReadOnlyList<Guid> Ids);
