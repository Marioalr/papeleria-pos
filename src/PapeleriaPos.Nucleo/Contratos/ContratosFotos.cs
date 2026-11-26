namespace PapeleriaPos.Nucleo.Contratos;

// ── Las fotos de los productos (entrega 13, parte 2) ─────────────────────────
// Se toman con el celular (la página del conteo, «Fotos») o se suben desde la PC (Productos, F3): el servicio las achica y
// las guarda. Viajan a las cajas solo por su huella, en la copia; cada caja baja aparte las que cambiaron y las enseña de su
// base, sin la red: en Ctrl+M, en la lista de la búsqueda y en el verificador.

/// <summary>
/// La foto de un producto como va en la copia de cada caja: de qué producto (la variante) es y su huella. La foto se baja
/// aparte (<c>api/fotos/{variante}</c>), una sola vez: con la misma huella, la caja ya la tiene.
/// </summary>
public sealed record FotoDeProductoDto(Guid VarianteId, string Huella);

/// <summary>
/// Todas las fotos (de qué producto es cada una y su huella), con la versión del catálogo con que se leyeron: para la caja
/// sin copia (el navegador), que enseña las del servicio de su misma PC.
/// </summary>
public sealed record FotosDto(long Version, IReadOnlyList<FotoDeProductoDto> Fotos);

/// <summary>
/// La foto de un producto, nueva o en lugar de la que tenía, como llega (una foto del celular o una imagen de la PC), en
/// base64: el servicio la revisa, la achica y la guarda chica. Quien entró, con «Productos, precios y etiquetas».
/// </summary>
/// <param name="UsuarioId">
/// Quien entró. Con la llave de un celular dado de alta puede ir vacío (docs/celular-como-caja.md, paso 12): queda a nombre del
/// celular.
/// </param>
/// <param name="SucursalId">La de la caja; con la llave de un celular no se usa (cuenta la del celular).</param>
/// <param name="Desde">De dónde llegó («el celular», «la PC»): solo para la bitácora. Con la llave de un celular, su nombre.</param>
public sealed record SolicitudFoto(Guid UsuarioId, Guid SucursalId, string ImagenBase64, string? Desde = null);

/// <summary>
/// Quitar la foto de un producto: quien entró, con «Productos, precios y etiquetas». Con la llave de un celular dado de alta, los dos
/// pueden ir vacíos, como en <see cref="SolicitudFoto"/>.
/// </summary>
public sealed record SolicitudQuitarFoto(Guid UsuarioId, Guid SucursalId);

/// <summary>Cómo quedó la foto guardada: su huella, su tamaño y la foto misma (en base64), para enseñarla enseguida.</summary>
/// <param name="Bytes">Lo que pesa la foto guardada.</param>
public sealed record FotoGuardadaDto(Guid VarianteId, string Huella, int Ancho, int Alto, int Bytes, string JpegBase64);
