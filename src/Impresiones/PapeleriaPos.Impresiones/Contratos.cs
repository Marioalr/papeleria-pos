namespace PapeleriaPos.Impresiones.Contratos;

// Lo que viaja entre la caja (con el agente de cada PC que imprime) y el servicio de la sucursal para las impresiones.
// Montos en centavos, como en el resto del POS; las fechas que se muestran ya van en hora de México. Antes estaban en
// PapeleriaPos.Nucleo.Contratos: se movieron sin cambiar, y el JSON que viaja es el mismo (no lleva el nombre de las
// clases), así que una caja con lo que tenía en su bandeja lo sigue mandando igual.

// ── Impresiones (entrega 5) ─────────────────────────────────────────────────

/// <summary>Los ajustes con que salió un trabajo (del PrintTicket de Windows).</summary>
public sealed record AjustesTrabajoDto(
    string Calidad, string TipoPapel, string Tamano, double AnchoMm, double AltoMm, bool Color, bool Duplex, int Copias, int PaginasPorHoja,
    bool DelPrintTicket);

/// <summary>Cobertura de tinta de una página (fracciones 0..1) y su área en pulgadas cuadradas.</summary>
public sealed record CoberturaPaginaDto(double C, double M, double Y, double K, double Gris, double AreaPulg2, bool ConColor);

/// <summary>Lo que manda el agente de una caja por cada trabajo impreso; el servicio le pone costo y precio.</summary>
/// <param name="Id">Fijo para ese trabajo (PC, cola, número y hora): si se manda dos veces, se guarda una.</param>
/// <param name="EnviadoUtc">Cuando entró a la cola de Windows: desde ahí se cuentan sus hojas en la impresora.</param>
public sealed record TrabajoMedidoDto(
    Guid Id, Guid CajaId, Guid? AtendioId, string Pc, string Cola, long SpoolId, string Documento, string? UsuarioWindows, DateTime ImpresoUtc,
    EstadoTrabajo Estado, AjustesTrabajoDto Ajustes, IReadOnlyList<CoberturaPaginaDto> Paginas, bool Medido, string? Nota, string? MiniaturaBase64,
    DateTime? EnviadoUtc = null);

/// <summary>Solo lo recibe quien tiene «Ver costos, utilidad y margen» (lo revisa la API).</summary>
public sealed record CostoImpresionDto(long TintaCentavos, long PapelCentavos, long OtrosCentavos, long TotalCentavos, decimal MargenPorcentaje);

/// <param name="CarasContadas">Las que contó la impresora (vacío mientras no se verifica).</param>
/// <param name="PrecioSoloLoQueSalioCentavos">Con un atasco o una cancelación: lo que costaría cobrar solo lo que salió.</param>
/// <param name="PrecioCompletoCentavos">Si ya se cobra solo lo que salió, el precio del trabajo completo.</param>
public sealed record ImpresionPorCobrarDto(
    Guid Id, string Documento, string Pc, DateTime FechaLocal, int Paginas, int Caras, bool Color, string Tamano, long PrecioCentavos,
    string? Atendio, string? MiniaturaBase64, string? Nota, CostoImpresionDto? Costo = null,
    TipoTrabajoImpresion Tipo = TipoTrabajoImpresion.Impresion, int? CarasContadas = null, long? PrecioSoloLoQueSalioCentavos = null,
    long? PrecioCompletoCentavos = null);

public sealed record SolicitudNoVendida(Guid UsuarioId, MotivoNoVendida Motivo, string? Nota);

public sealed record SolicitudSoloLoQueSalio(Guid UsuarioId);

/// <summary>Una opción para elegir en una lista: la clave que entiende el cálculo y el nombre que se muestra.</summary>
public sealed record OpcionDto(string Clave, string Nombre);

/// <summary>Con qué se puede cotizar o registrar copias en la sucursal (los tipos de papel salen de su configuración).</summary>
public sealed record OpcionesImpresionDto(IReadOnlyList<OpcionDto> TiposPapel, IReadOnlyList<string> Tamanos, IReadOnlyList<OpcionDto> Calidades);

/// <param name="Tamano">Carta, Oficio o A4.</param>
/// <param name="Rango">Páginas a imprimir ("1-3, 5"); vacío, todas.</param>
public sealed record OpcionesCotizacionDto(
    string TipoPapel = "Plain", string Tamano = "Carta", bool Color = true, string Calidad = "Standard", bool Duplex = false, int Copias = 1,
    int PaginasPorHoja = 1, string? Rango = null);

/// <summary>Cotizar un PDF: la caja lo midió y la caja principal le pone precio, igual que a una impresión.</summary>
/// <param name="UsuarioId">Quien cotiza: si tiene «Ver costos, utilidad y margen», la respuesta trae el costo.</param>
public sealed record SolicitudCotizacion(Guid SucursalId, Guid? UsuarioId, IReadOnlyList<CoberturaPaginaDto> Paginas, OpcionesCotizacionDto Opciones);

/// <param name="Pagina">El número de página del documento.</param>
/// <param name="Tipo">Color, B/N o En blanco.</param>
/// <param name="PrecioCentavos">Lo que le toca a esa página por copia, sin redondear el trabajo.</param>
public sealed record PaginaCotizadaDto(int Pagina, string Tipo, decimal PrecioCentavos, decimal? CostoCentavos);

public sealed record CotizacionDto(
    long PrecioCentavos, bool CobroMinimo, int Paginas, int Copias, int Caras, int Hojas, int CarasColor, string Tamano, string TipoPapel,
    IReadOnlyList<PaginaCotizadaDto> PorPagina, IReadOnlyList<string> Avisos, CostoImpresionDto? Costo);

/// <summary>Copias hechas en el panel de la impresora: el precio sugerido con la tinta típica y el papel.</summary>
public sealed record SolicitudPrecioCopias(Guid SucursalId, int Cantidad, bool Color, string Tamano, bool Duplex);

public sealed record PrecioCopiasDto(long PrecioCentavos, string Descripcion);

/// <summary>Se registran al cobrarlas: entran a la línea "Impresiones" y cuentan contra el contador de la impresora.</summary>
/// <param name="CobradoCentavos">Lo que se cobra; vacío, el precio sugerido.</param>
public sealed record SolicitudCopias(
    Guid Id, Guid CajaId, Guid? AtendioId, int Cantidad, bool Color, string Tamano, bool Duplex, long? CobradoCentavos, string? Nota);

/// <summary>
/// Lo que hizo cada impresión de un día, con su cobro: el desglose de la línea "Impresiones", para quien tiene «Consultar
/// la bitácora» (el costo, con «Ver costos, utilidad y margen»).
/// </summary>
public sealed record ImpresionDelDiaDto(
    Guid Id, TipoTrabajoImpresion Tipo, string Documento, string Pc, DateTime FechaLocal, int Caras, int? CarasContadas, bool Color,
    long PrecioCentavos, EstadoCobroImpresion Cobro, MotivoNoVendida? Motivo, string? Atendio, CostoImpresionDto? Costo);

// ── La impresora (entrega 5, parte 2) ───────────────────────────────────────

/// <param name="Host">IP o nombre de la impresora en la red; vacío, no se lee.</param>
public sealed record ConfiguracionImpresoraDto(string? Host, int AvisoTintaPorcentaje, int MinutosEntreLecturas);

/// <summary>Cómo se lee la impresora: lo cambia quien entró a la caja con «Configurar la impresora» (la Configuración nueva, entrega 7).</summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudConfiguracionImpresora(Guid UsuarioId, ConfiguracionImpresoraDto Configuracion);

/// <summary>Aplicar la calibración de la tinta: quien entró a la caja con «Configurar la impresora».</summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudCalibracion(Guid UsuarioId);

/// <summary>Niveles en porcentaje restante (vacío: la impresora no lo dijo) y contadores de caras.</summary>
public sealed record LecturaImpresoraDto(
    DateTime TomadaLocal, int? TintaK, int? TintaC, int? TintaM, int? TintaY, int? Mantenimiento, long? Contador, long? ContadorColor,
    long? ContadorBN, string? Serie);

/// <summary>Lo que contó la impresora contra lo registrado (impresiones de las PC y copias cobradas) en un periodo.</summary>
/// <param name="SinRegistrar">Caras que contó y nadie registró: copias sin cobrar, impresiones desde el celular, páginas de prueba.</param>
public sealed record ComparacionContadorDto(
    DateTime DesdeLocal, DateTime HastaLocal, long Contadas, long? ContadasColor, int CarasImpresas, int CarasCopias, long SinRegistrar,
    long? SinRegistrarColor);

/// <param name="Error">Por qué falló la última lectura (vacío si respondió).</param>
public sealed record EstadoImpresoraDto(
    ConfiguracionImpresoraDto Configuracion, LecturaImpresoraDto? Ultima, string? Error, ComparacionContadorDto? Hoy);

/// <param name="Canal">Negro, Cian, Magenta o Amarillo.</param>
/// <param name="Observado">Puntos de nivel que bajó el cartucho en la impresora.</param>
/// <param name="Estimado">Puntos que estimó el modelo para los mismos periodos.</param>
/// <param name="Factor">La corrección que se aplicaría al costo de esa tinta.</param>
/// <param name="Actual">La corrección que se usa hoy.</param>
public sealed record CanalCalibradoDto(string Canal, double Observado, double Estimado, double? Proporcion, double Factor, double Actual, string Calidad);

/// <summary>La tinta real (niveles de la impresora) contra la estimada por el modelo: con esto se corrigen los costos.</summary>
public sealed record CalibracionDto(
    DateTime? DesdeLocal, DateTime? HastaLocal, IReadOnlyList<CanalCalibradoDto> Canales, long CarasImpresora, long CarasRegistradas,
    int? PaginasPorCaja, int PaginasPorCajaActual, IReadOnlyList<string> Cambios, DateTime? AplicadaLocal);
