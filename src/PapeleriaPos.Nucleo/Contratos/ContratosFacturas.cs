using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// Inventario › «Recibir con factura» (dueño, 2026-11-27; docs/recibir-con-factura.md): se carga el XML de la factura del proveedor
// (el CFDI), que dice lo que debe llegar; quien recibe escribe lo que llegó de cada renglón, como al recibir un traspaso, y la
// recibe. Por dentro es una compra (Compra.ConFactura): al recibirla entra al kardex con el costo de la factura.

/// <summary>En qué va una factura que se recibe.</summary>
public enum EstadoFactura
{
    /// <summary>Se cargó: todavía no entra nada.</summary>
    PorRecibir,

    /// <summary>Quien no puede recibirla con cambios los dejó marcados: la recibe quien sí puede.</summary>
    EnRevision,

    /// <summary>Entró (en el piloto con eleventa, solo quedó el documento).</summary>
    Recibida,

    /// <summary>Se descartó sin recibirla: la misma factura se puede cargar otra vez.</summary>
    Descartada,
}

/// <summary>Las facturas de la sucursal, de lo más reciente a lo más viejo, y lo que puede quien pregunta.</summary>
/// <param name="PuedeModificar">Tiene «Recibir con factura: modificar lo que llegó»: la recibe con cambios.</param>
/// <param name="VeCostos">Tiene «Compras: la nota y los costos»: ve los importes de la factura.</param>
/// <param name="Piloto">En el piloto con eleventa: por qué no mueve las existencias aquí. Vacío fuera del piloto.</param>
public sealed record FacturasDto(IReadOnlyList<FacturaResumenDto> Facturas, bool PuedeModificar, bool VeCostos, string? Piloto);

/// <param name="Folio">El de la compra: «C-123».</param>
/// <param name="Documento">La serie y el folio de la factura: «KPU108918».</param>
/// <param name="FechaLocal">Lo último que le pasó: se cargó, se marcó, se recibió o se descartó.</param>
/// <param name="Usuario">Quién hizo eso último.</param>
/// <param name="TotalCentavos">El total de la factura; vacío para quien no ve los costos.</param>
public sealed record FacturaResumenDto(
    Guid Id, string Folio, string Proveedor, string? Documento, EstadoFactura Estado, DateTime FechaLocal, string Usuario, int Renglones, int Cambios,
    long? TotalCentavos);

/// <summary>Una factura con sus renglones.</summary>
/// <param name="Creo">Quién la cargó; <paramref name="Cerro"/>, quién la recibió o la descartó.</param>
/// <param name="Marco">Quién marcó los cambios para revisión.</param>
/// <param name="MovioExistencias">Al recibirla entró al inventario (en el piloto con eleventa, no).</param>
/// <param name="TotalCentavos">El total de la factura; vacío para quien no ve los costos.</param>
/// <param name="Avisos">Lo que conviene revisar de la lectura (un servicio que no se tomó, la suma que no cuadra, otro RFC).</param>
/// <param name="YaEstaba">Al cargarla: esa factura (su folio fiscal) ya estaba; es la que ya estaba, sin cambiarle nada.</param>
public sealed record FacturaDto(
    Guid Id, string Folio, Guid ProveedorId, string Proveedor, string? Rfc, string? Documento, string? Uuid, EstadoFactura Estado, DateTime CreadoLocal,
    string Creo, DateTime? CerradoLocal, string? Cerro, DateTime? MarcadoLocal, string? Marco, bool MovioExistencias, bool PuedeModificar, bool VeCostos,
    string? Piloto, long? TotalCentavos, IReadOnlyList<string> Avisos, IReadOnlyList<FacturaRenglonDto> Renglones, bool YaEstaba = false);

/// <param name="CodigoProveedor">La clave del proveedor (el NoIdentificacion del concepto).</param>
/// <param name="Descripcion">Como la dice la factura; en uno que no venía, el producto de aquí.</param>
/// <param name="Unidad">La unidad como la escribe la factura (Pieza, CAJA, H87).</param>
/// <param name="Cantidad">Lo que dice la factura, en su unidad; cero en uno que no venía.</param>
/// <param name="Llego">Lo que llegó, en la unidad de la factura.</param>
/// <param name="Agregado">Llegó y no venía en la factura.</param>
/// <param name="Cambio">Cambió contra la factura (otra cantidad, no venía, otro producto, emparejado a mano).</param>
/// <param name="VarianteId">Con qué producto de aquí va; vacío: sin emparejar.</param>
/// <param name="PresentacionId">En qué presentación: lo que es cada unidad de la factura (la pieza, la caja de 12).</param>
/// <param name="Producto">Como se llama aquí; vacío sin emparejar.</param>
/// <param name="Presentacion">El nombre de la presentación («Pieza», «Caja 12»).</param>
/// <param name="Factor">Cuántas de la unidad base trae cada unidad de la factura (12 en la caja de 12).</param>
/// <param name="UnidadBase">La unidad en que se cuenta el producto de aquí (piezas, metros…); pieza sin emparejar.</param>
/// <param name="Reconocido">El POS lo emparejó solo al cargarla (por la clave del proveedor, o el código de barras).</param>
/// <param name="SugerenciaVarianteId">Sin emparejar: el producto de aquí que se parece a la descripción (se ofrece, no se empareja solo).</param>
/// <param name="ImporteCentavos">Lo que cobra la factura por el renglón, con descuento e IVA; vacío para quien no ve los costos.</param>
/// <param name="Existencia">Lo que hay ahora en esta sucursal; vacío sin emparejar.</param>
/// <param name="PresentacionReconocidaId">Con qué presentación lo emparejó el POS al cargarla; vacía si no lo reconoció.</param>
public sealed record FacturaRenglonDto(
    Guid Id, string? CodigoProveedor, string Descripcion, string? Unidad, decimal Cantidad, decimal Llego, string? Nota, bool Agregado, bool Cambio,
    Guid? VarianteId, Guid? PresentacionId, string? Producto, string? Presentacion, decimal Factor, UnidadBase UnidadBase, bool Reconocido,
    Guid? SugerenciaVarianteId, Guid? SugerenciaPresentacionId, string? Sugerencia, long? ImporteCentavos, decimal? Existencia,
    Guid? PresentacionReconocidaId = null);

/// <summary>El XML de la factura que llegó con la mercancía.</summary>
/// <param name="Archivo">Cómo se llama (para la bitácora).</param>
/// <param name="ContenidoBase64">Sus bytes.</param>
public sealed record SolicitudImportarFactura(Guid UsuarioId, Guid SucursalId, string Archivo, string ContenidoBase64, Guid? CajaId = null);

/// <summary>Lo que quien recibe dice de un renglón.</summary>
/// <param name="Id">El renglón; vacío: uno que se agrega (llegó y no venía).</param>
/// <param name="VarianteId">Con qué producto de aquí va; vacío: sin emparejar.</param>
/// <param name="PresentacionId">La presentación de ese producto que es cada unidad de la factura.</param>
/// <param name="LlegoMilesimas">Lo que llegó, en la unidad de la factura; cero: no llegó (o se quita uno agregado).</param>
public sealed record RenglonDeFactura(Guid? Id, Guid? VarianteId, Guid? PresentacionId, long LlegoMilesimas, string? Nota);

/// <summary>Lo que se revisó de una factura: todos sus renglones, como quedan, y qué se hace (como en los traspasos).</summary>
public sealed record SolicitudRecibirFactura(
    Guid UsuarioId, Guid SucursalId, IReadOnlyList<RenglonDeFactura> Renglones, PasoDeRecepcion Paso, Guid? CajaId = null);

/// <summary>De qué producto es una variante.</summary>
public sealed record ProductoDeVarianteDto(Guid ProductoId);

/// <summary>Descartar una factura que no se recibió.</summary>
public sealed record SolicitudDeFactura(Guid UsuarioId, Guid SucursalId, Guid? CajaId = null);
