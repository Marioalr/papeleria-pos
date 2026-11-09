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
/// <param name="Tramite">
/// El trámite del que es (2026-10-31): lo pone el agente solo si esta caja lo mandó a imprimir desde el panel de sus trámites
/// (<see cref="TrabajosDeTramites"/>), nunca por el nombre. Sin él, es una impresión como cualquiera.
/// </param>
/// <param name="Impresora">
/// El número de la impresora de esa cola en la sucursal (2026-11-03), como lo dice caja.json (<c>ColasImpresion</c>). Vacío (una caja de
/// antes, o <c>ColaImpresion</c> sola): la primera de la sucursal.
/// </param>
public sealed record TrabajoMedidoDto(
    Guid Id, Guid CajaId, Guid? AtendioId, string Pc, string Cola, long SpoolId, string Documento, string? UsuarioWindows, DateTime ImpresoUtc,
    EstadoTrabajo Estado, AjustesTrabajoDto Ajustes, IReadOnlyList<CoberturaPaginaDto> Paginas, bool Medido, string? Nota, string? MiniaturaBase64,
    DateTime? EnviadoUtc = null, TrabajoDeTramite? Tramite = null, int? Impresora = null);

/// <summary>Solo lo recibe quien tiene «Ver costos, utilidad y margen» (lo revisa la API).</summary>
/// <param name="MargenPorcentaje">El margen del trabajo; con las curvas, el promedio de sus caras.</param>
/// <param name="Margen">
/// De dónde salió el margen si no es uno para todo (2026-11-08): «curva: 12 % de tinta → 210 % × 85 % (40 caras)» o «mayoreo: costo del trabajo
/// 100–500 → 80 %». Vacío en los trabajos de antes.
/// </param>
public sealed record CostoImpresionDto(long TintaCentavos, long PapelCentavos, long OtrosCentavos, long TotalCentavos, decimal MargenPorcentaje,
    string? Margen = null);

/// <param name="CarasContadas">Las que contó la impresora (vacío mientras no se verifica).</param>
/// <param name="PrecioSoloLoQueSalioCentavos">Con un atasco o una cancelación: lo que costaría cobrar solo lo que salió.</param>
/// <param name="PrecioCompletoCentavos">Si ya se cobra solo lo que salió, el precio del trabajo completo.</param>
/// <param name="Impresora">
/// De qué impresora salió (2026-11-03; en las copias, en cuál se hicieron), si la sucursal tiene más de una: con una sola, vacío (no hace
/// falta decirlo).
/// </param>
/// <param name="MiniaturaBase64">Ya no viene en la lista (revisión de #155, 2026-11-08): se pide aparte (<paramref name="ConMiniatura"/>).</param>
/// <param name="Estado">Si la impresora lo detuvo (atasco, cancelación): F2 lo dice aunque no se lea el contador.</param>
/// <param name="Medido">Medido página por página; si no, su costo es estimado por páginas.</param>
/// <param name="CarasColor">Las caras con tinta de color: con el driver en color, una hoja de texto es B/N.</param>
/// <param name="ConMiniatura">Si tiene miniatura: F2 la pide por su Id al mostrarla (GET /impresiones/{id}/miniatura).</param>
public sealed record ImpresionPorCobrarDto(
    Guid Id, string Documento, string Pc, DateTime FechaLocal, int Paginas, int Caras, bool Color, string Tamano, long PrecioCentavos,
    string? Atendio, string? MiniaturaBase64, string? Nota, CostoImpresionDto? Costo = null,
    TipoTrabajoImpresion Tipo = TipoTrabajoImpresion.Impresion, int? CarasContadas = null, long? PrecioSoloLoQueSalioCentavos = null,
    long? PrecioCompletoCentavos = null, string? Impresora = null, EstadoTrabajo Estado = EstadoTrabajo.Impreso, bool Medido = true,
    int Copias = 1, int? CarasColor = null, string? TipoPapel = null, bool Duplex = false, bool ConMiniatura = false,
    TarjetaDelContadorDto? Contador = null);

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
/// <param name="Impresora">En qué impresora se imprimiría (su número, 2026-11-03): con sus costos. Vacío: la primera.</param>
public sealed record SolicitudCotizacion(
    Guid SucursalId, Guid? UsuarioId, IReadOnlyList<CoberturaPaginaDto> Paginas, OpcionesCotizacionDto Opciones, int? Impresora = null);

/// <param name="Pagina">El número de página del documento.</param>
/// <param name="Tipo">Color, B/N o En blanco.</param>
/// <param name="PrecioCentavos">Lo que le toca a esa página por copia, sin redondear el trabajo.</param>
public sealed record PaginaCotizadaDto(int Pagina, string Tipo, decimal PrecioCentavos, decimal? CostoCentavos);

public sealed record CotizacionDto(
    long PrecioCentavos, bool CobroMinimo, int Paginas, int Copias, int Caras, int Hojas, int CarasColor, string Tamano, string TipoPapel,
    IReadOnlyList<PaginaCotizadaDto> PorPagina, IReadOnlyList<string> Avisos, CostoImpresionDto? Costo);

/// <summary>Copias hechas en el panel de la impresora: el precio sugerido con la tinta típica y el papel.</summary>
/// <param name="Impresora">En el panel de qué impresora (su número, 2026-11-03): con sus costos. Vacío: la primera.</param>
public sealed record SolicitudPrecioCopias(Guid SucursalId, int Cantidad, bool Color, string Tamano, bool Duplex, int? Impresora = null);

public sealed record PrecioCopiasDto(long PrecioCentavos, string Descripcion);

/// <summary>Se registran al cobrarlas: entran a la línea "Impresiones" y cuentan contra el contador de la impresora.</summary>
/// <param name="CobradoCentavos">Lo que se cobra; vacío, el precio sugerido.</param>
/// <param name="Impresora">
/// En el panel de qué impresora se hicieron (su número, 2026-11-03): cuentan contra su contador y con sus costos. Vacío: la primera.
/// </param>
public sealed record SolicitudCopias(
    Guid Id, Guid CajaId, Guid? AtendioId, int Cantidad, bool Color, string Tamano, bool Duplex, long? CobradoCentavos, string? Nota,
    int? Impresora = null);

/// <summary>
/// Lo que hizo cada impresión de un día, con su cobro: el desglose de la línea "Impresiones", para quien tiene «Consultar
/// la bitácora» (el costo, con «Ver costos, utilidad y margen»).
/// </summary>
/// <param name="Impresora">De qué impresora salió, si la sucursal tiene más de una (2026-11-03).</param>
public sealed record ImpresionDelDiaDto(
    Guid Id, TipoTrabajoImpresion Tipo, string Documento, string Pc, DateTime FechaLocal, int Caras, int? CarasContadas, bool Color,
    long PrecioCentavos, EstadoCobroImpresion Cobro, MotivoNoVendida? Motivo, string? Atendio, CostoImpresionDto? Costo, string? Impresora = null);

// ── Control Print, solo leyendo: las tarjetas de su cola en F2 (2026-10-25) ──

/// <summary>
/// Un trabajo «por cobrar» de Control Print como su tarjeta (QueueView): lo que se ve para cobrarlo en esta caja. Control Print
/// solo se lee: cobrarlo aquí no lo marca allá.
/// </summary>
/// <param name="Id">Su número en Control Print («#1234»).</param>
/// <param name="DetectadoUtc">Cuándo lo vio Control Print: con el número, lo identifica.</param>
/// <param name="Titulo">La etiqueta que le pusieron o, si no hay, el documento.</param>
/// <param name="Resumen">«3 págs × 2 copias · Color · Carta · Papel normal · Dúplex», como en Control Print.</param>
/// <param name="HoraLocal">Cuándo lo vio Control Print, en hora de la tienda.</param>
/// <param name="Pc">La PC que imprimió.</param>
/// <param name="Atendio">Quién atendía en Control Print («Sin sesión» si nadie había entrado).</param>
/// <param name="PrecioCentavos">Lo que se cobra: el precio puesto en Control Print o el calculado (pesos cerrados).</param>
/// <param name="Especial">Con precio especial: para quién (vacío si no lo tiene).</param>
/// <param name="RevisarHojas">
/// La impresora contó menos hojas y nadie lo ha resuelto: qué contó (vacío si no hace falta). Así no se cobra aquí (2026-10-29): se
/// resuelve en la caja principal y después se cobra.
/// </param>
/// <param name="CopiasEnImpresora">Copias del panel de la impresora: van en la línea de copias (en el piloto, COP en eleventa; 2026-10-29).</param>
/// <param name="CostoCentavos">Solo para quien tiene «Ver costos, utilidad y margen».</param>
/// <param name="Impresora">
/// La cola de Windows donde se imprimió, como la anotó Control Print, si su cola por cobrar trae trabajos de más de una (2026-11-03):
/// así se sabe de qué impresora sale. Vacío con una sola, y en las copias del panel.
/// </param>
public sealed record TrabajoControlPrintDto(
    long Id, DateTime DetectadoUtc, string Titulo, string Resumen, DateTime HoraLocal, string Pc, string? Atendio, long PrecioCentavos,
    string? Especial = null, bool Cotizado = false, bool CopiasEnImpresora = false, string? RevisarHojas = null, long? CostoCentavos = null,
    string? Impresora = null);

/// <summary>La cola de Control Print en esta caja: sus trabajos por cobrar, sin los que ya se cobraron aquí.</summary>
/// <param name="Configurado">Este servicio tiene la sección «ControlPrint»; sin ella no hay tarjetas (como antes).</param>
/// <param name="Contesta">Si la última vez contestó; vacío mientras se conecta por primera vez.</param>
/// <param name="Aviso">Por qué no contesta o qué falta (para mostrarlo tal cual).</param>
/// <param name="Servidor">Cómo se llama la PC de Control Print («PRINCIPAL»), o su dirección si todavía no contesta.</param>
/// <param name="LeidaLocal">Cuándo se leyó su cola por última vez.</param>
/// <param name="Version">Cambia cuando cambia lo que se ve: con la misma, el servicio contesta sin la lista (204).</param>
/// <param name="SoloInvitado">Control Print está en «cada quien ve sus trabajos»: sin sesión solo se ven los de «Sin sesión».</param>
/// <param name="Mas">Cuántos más hay por cobrar de los que se muestran.</param>
/// <param name="CobradasDosVeces">
/// Lo que se cobró aquí y después también en Control Print, en la caja principal (2026-10-29), de los últimos días y mientras la venta
/// de aquí siga cobrada: qué trabajo, en qué venta y quién lo cobró allá, para revisarlo con el cliente.
/// </param>
public sealed record ColaControlPrintDto(
    bool Configurado, bool? Contesta, string? Aviso, string? Servidor, DateTime? LeidaLocal, long Version, IReadOnlyList<TrabajoControlPrintDto> Trabajos,
    bool SoloInvitado = false, int Mas = 0, IReadOnlyList<string>? CobradasDosVeces = null);

/// <summary>Lo que falta por cobrar, sin las listas: para el chip de arriba y para saber si F2 tiene que volver a leer.</summary>
/// <param name="DeLaSucursal">Las del módulo de la sucursal (las de la caja 2 y las copias del panel registradas aquí).</param>
/// <param name="FirmaDeLaSucursal">Cambia cuando cambian las del módulo.</param>
/// <param name="DeControlPrint">Las tarjetas de Control Print (sin las que ya se cobraron aquí).</param>
/// <param name="VersionControlPrint">La de <see cref="ColaControlPrintDto.Version"/>.</param>
/// <param name="ControlPrintAviso">Por qué Control Print no contesta (vacío si contesta o no está configurado).</param>
/// <param name="CobradasDosVeces">Lo cobrado aquí y también en Control Print (<see cref="ColaControlPrintDto.CobradasDosVeces"/>): para el chip.</param>
public sealed record ResumenPorCobrarDto(
    int DeLaSucursal, long FirmaDeLaSucursal, int DeControlPrint, long VersionControlPrint, bool ControlPrintConfigurado, bool? ControlPrintContesta,
    string? ControlPrintAviso, IReadOnlyList<string>? CobradasDosVeces = null);

/// <summary>
/// Cómo ve este servicio a Control Print (GET /api/controlprint): lo lee <c>poner-codigo-controlprint.ps1</c> después de poner
/// el código. Nunca lleva el código.
/// </summary>
/// <param name="TieneCodigo">El servicio tiene el código (la variable ControlPrint__Codigo).</param>
/// <param name="Huella">La del certificado de Control Print que quedó fijada (configurada o del primer saludo).</param>
/// <param name="HuellaConfigurada">Viene de ControlPrint:Huella (si no, se fijó en el primer saludo y se guardó junto a la base).</param>
/// <param name="PorCobrar">Cuántos trabajos por cobrar leyó (de los días que se ven).</param>
/// <param name="Lecturas">Cuántas veces se ha leído su cola desde que arrancó el servicio.</param>
/// <param name="Avisos">
/// Lo demás que hay que saber: el código en un archivo (se ignora), los costos de fábrica de Control Print (no se usan) y lo cobrado
/// aquí y también allá.
/// </param>
public sealed record EstadoControlPrintDto(
    bool Configurado, string? Servidor, int Puerto, bool TieneCodigo, bool? Contesta, string? Nombre, string? Version, string? Huella,
    bool HuellaConfigurada, DateTime? LeidaLocal, int PorCobrar, int Lecturas, string? Error, bool SoloInvitado, IReadOnlyList<string>? Avisos = null);

// ── Los costos de las impresiones en la Configuración (2026-10-31) ──────────

/// <summary>Un cartucho: lo que cuesta y lo que rinde (las páginas que dice su caja, medidas como en la norma ISO).</summary>
/// <param name="Tinta">Negro, Cian, Magenta o Amarillo.</param>
public sealed record CartuchoCostoDto(string Tinta, decimal Precio, int Rendimiento);

/// <summary>Un papel de un tamaño: lo que cuesta su paquete y cuántas hojas trae (de ahí sale el precio por hoja).</summary>
/// <param name="Tamano">Carta, Oficio o A4.</param>
/// <param name="Grupo">«Normal», «Fotográfico brillante», «Cartulina»…</param>
public sealed record PapelCostoDto(string Tamano, string Grupo, decimal PrecioPaquete, int HojasPaquete);

/// <summary>Un tramo del margen: desde ese costo (del trabajo o de cada hoja), ese margen.</summary>
public sealed record TramoMargenDto(decimal Desde, decimal Margen);

/// <summary>Cómo se elige el margen de un trabajo (en Control Print, el escalado del mayoreo).</summary>
public enum MargenDeImpresiones
{
    /// <summary>El mismo para todo.</summary>
    Unico,

    /// <summary>Por tramos del costo del trabajo: entre más grande, menos margen.</summary>
    PorCostoDelTrabajo,

    /// <summary>Por tramos del costo de cada hoja.</summary>
    PorCostoDeLaHoja,

    /// <summary>
    /// Por las curvas (2026-11-08): cada cara con el margen de su tinta, por el factor de las caras del trabajo (<see cref="CurvasMargenDto"/>).
    /// </summary>
    PorCurvas,
}

/// <summary>Un punto de una curva del margen, con sus cantidades exactas.</summary>
/// <param name="X">En la curva por tinta, la tinta de la cara (%); en la curva por caras, las caras impresas del trabajo.</param>
/// <param name="Y">En la curva por tinta, el margen (%); en la curva por caras, el factor (%) que multiplica ese margen.</param>
public sealed record PuntoCurvaDto(decimal X, decimal Y);

/// <summary>
/// Las curvas del margen (2026-11-08). Entre dos puntos, recta sobre el logaritmo de x; antes del primero y después del último, el valor del
/// extremo. Cada una con 2 a 12 puntos, x mayor que cero y sin repetir.
/// </summary>
/// <param name="PorTinta">
/// La tinta de cada cara (%: la suma de sus canales sobre el área; texto ≈ 5 %, con fotos ≈ 40 %, toda llena ≈ 100 % o más, hasta 400 %) → su
/// margen (%, de 0 a 1,000).
/// </param>
/// <param name="PorCaras">Las caras impresas del trabajo (páginas × copias entre N por hoja) → el factor del margen (%, de 0 a 200).</param>
public sealed record CurvasMargenDto(IReadOnlyList<PuntoCurvaDto> PorTinta, IReadOnlyList<PuntoCurvaDto> PorCaras);

/// <summary>
/// Los márgenes de una impresora con sus propios consumibles (2026-11-08, el dueño: «el mismo documento se vende al precio que calcula cada
/// impresora con sus costos y su configuración de márgenes»). El cobro mínimo y el redondeo siguen siendo de la sucursal.
/// </summary>
public sealed record MargenesImpresionDto(
    MargenDeImpresiones Margen, decimal MargenUnico, IReadOnlyList<TramoMargenDto> TramosPorTrabajo, IReadOnlyList<TramoMargenDto> TramosPorHoja,
    decimal? MargenPapel, CurvasMargenDto Curvas);

/// <summary>Los márgenes de una impresora como deben quedar; vacíos, vuelve a los de la sucursal.</summary>
public sealed record SolicitudMargenesImpresora(Guid UsuarioId, MargenesImpresionDto? Margenes);

/// <summary>
/// El probador de las curvas: un trabajo de <paramref name="Caras"/> caras, todas con <paramref name="Tinta"/> % de tinta, con los costos de la
/// impresora (vacío: la primera) y las curvas que se están escribiendo (aunque no estén guardadas ni elegidas).
/// </summary>
/// <param name="Tamano">Carta, Oficio o A4.</param>
/// <param name="TipoPapel">El tipo de papel del driver (Plain…), como al cotizar.</param>
public sealed record SolicitudProbarCurvas(
    Guid UsuarioId, int? Impresora, CurvasMargenDto Curvas, decimal Tinta, int Caras, bool Color, string Tamano, string TipoPapel);

/// <summary>Lo que daría el probador: el margen de cada cara, el factor, el precio y su desglose; y el precio con el margen de hoy.</summary>
/// <param name="MargenPorTinta">El de la curva por tinta para esa tinta (%).</param>
/// <param name="Factor">El de la curva por caras para esas caras (%).</param>
/// <param name="Margen">El de cada cara: margen por tinta × factor (%).</param>
/// <param name="PrecioCaraCentavos">Lo que le toca a cada cara, antes de redondear el trabajo.</param>
/// <param name="PrecioHoyCentavos">El mismo trabajo con el margen como está hoy (el modo elegido).</param>
/// <param name="MargenHoy">De dónde sale el de hoy («mayoreo: …»); vacío con uno para todo.</param>
public sealed record PruebaCurvasDto(
    decimal MargenPorTinta, decimal Factor, decimal Margen, decimal CostoCaraCentavos, decimal PrecioCaraCentavos, long PrecioCentavos, bool CobroMinimo,
    long TintaCentavos, long PapelCentavos, long OtrosCentavos, long CostoCentavos, long PrecioHoyCentavos, decimal MargenHoyPorcentaje, string? MargenHoy);

/// <summary>Comparar las curvas con lo que se imprimió de verdad (los últimos <paramref name="Dias"/> días).</summary>
public sealed record SolicitudCompararCurvas(Guid UsuarioId, int? Impresora, CurvasMargenDto Curvas, int Dias = 30);

/// <summary>Lo impreso en esos días: lo que se cobró (o se pidió), lo que daría hoy con sus costos de hoy, y con las curvas.</summary>
/// <param name="Trabajos">Los que se compararon: impresiones y copias del panel; sin las no vendidas ni las incluidas en un trámite.</param>
/// <param name="HoyCentavos">Con los costos y el margen de hoy (sin las curvas): la diferencia contra esto es solo de las curvas.</param>
/// <param name="Mayores">Las diferencias más grandes entre hoy y con las curvas, primero las mayores.</param>
public sealed record ComparacionCurvasDto(
    int Dias, int Trabajos, long CobradoCentavos, long HoyCentavos, long ConCurvasCentavos, IReadOnlyList<DiferenciaCurvasDto> Mayores);

/// <param name="Tinta">La tinta promedio de sus caras (%).</param>
/// <param name="Margen">Cómo salió con las curvas («curva: …»).</param>
public sealed record DiferenciaCurvasDto(
    DateTime FechaLocal, string Documento, string? Impresora, int Caras, decimal Tinta, long CobradoCentavos, long HoyCentavos, long ConCurvasCentavos,
    string? Margen);

/// <summary>
/// Lo principal de los costos de impresión de la sucursal (<c>Medicion.CostSettings</c>), dicho en español: cada cartucho, el papel de
/// cada tamaño, la caja de mantenimiento, el margen (con sus tramos), el cobro mínimo y el redondeo. Lo demás (la calibración, la luz,
/// la depreciación, los tipos de papel…) se queda como está.
/// </summary>
/// <param name="MargenUnico">El de <see cref="MargenDeImpresiones.Unico"/>, en porcentaje: precio = costo × (1 + margen).</param>
/// <param name="MinimoATodos">El cobro mínimo vale para todo trabajo; si no, solo para los de una cara.</param>
/// <param name="RedondeoA">El precio se redondea hacia arriba a este múltiplo (1: pesos cerrados); 0, al centavo.</param>
/// <param name="MargenPapel">
/// El margen propio del papel, en porcentaje (el papel suele llevar menos que la tinta); vacío, el mismo margen que lo demás
/// (revisión del 2026-10-31: se usaba al calcular, pero no se veía).
/// </param>
/// <param name="Curvas">Las curvas del margen (2026-11-08); vacías al pedir un cambio (una caja de antes), se quedan como están.</param>
public sealed record CostosImpresionDto(
    IReadOnlyList<CartuchoCostoDto> Cartuchos, IReadOnlyList<PapelCostoDto> Papeles, decimal CajaMantenimiento, MargenDeImpresiones Margen,
    decimal MargenUnico, IReadOnlyList<TramoMargenDto> TramosPorTrabajo, IReadOnlyList<TramoMargenDto> TramosPorHoja, decimal CobroMinimo,
    bool MinimoATodos, decimal RedondeoA, decimal? MargenPapel = null, CurvasMargenDto? Curvas = null);

/// <summary>
/// Los costos de la sucursal para Configuración › Impresoras e impresiones: solo para quien tiene «Ver costos, utilidad y margen» (lo
/// revisa el servicio).
/// </summary>
/// <param name="DeControlPrint">
/// Este servicio lee la cola de Control Print (la caja 2): los costos vienen de allá, se guardan solos y aquí solo se ven.
/// </param>
/// <param name="LeidosLocal">Con Control Print: cuándo se leyeron de allá por última vez desde que arrancó el servicio.</param>
/// <param name="GuardadosLocal">Cuándo cambiaron por última vez los de la sucursal (de Control Print o de aquí).</param>
/// <param name="Aviso">Lo que hay que saber de ellos (los de fábrica de Control Print, que no se usan); vacío si nada.</param>
/// <param name="Faltan">Los precios que faltan (en cero): sin ellos, el precio sale de menos. También los de las impresoras con sus propios consumibles.</param>
/// <param name="PuedeCambiar">Quien pregunta los cambia aquí: con «Configurar la impresora» y sin Control Print.</param>
/// <param name="Impresoras">
/// Las impresoras activas con sus propios consumibles (2026-11-03: la L5590, con sus botellas): las demás usan los de arriba. Esos se
/// cambian aquí aunque haya Control Print (allá no están), con «Configurar la impresora».
/// </param>
public sealed record EstadoCostosImpresionDto(
    CostosImpresionDto Costos, bool DeControlPrint, DateTime? LeidosLocal, DateTime? GuardadosLocal, string? Aviso, IReadOnlyList<string> Faltan,
    bool PuedeCambiar, IReadOnlyList<ConsumiblesImpresoraDto>? Impresoras = null);

/// <summary>Los consumibles propios de una impresora (2026-11-03): la tinta de la L5590 va en botellas y no usa los cartuchos de la sucursal.</summary>
/// <param name="Numero">El de la impresora.</param>
/// <param name="Tintas">Negro, Cian, Magenta y Amarillo: el precio de cada botella (o cartucho) y lo que rinde.</param>
/// <param name="CajaMantenimiento">El precio de su caja de mantenimiento; vacío: no tiene.</param>
/// <param name="Faltan">Los precios que le faltan (en cero).</param>
/// <param name="PaginasPorCaja">Las caras que dura su caja de mantenimiento; vacío: no tiene.</param>
/// <param name="Margenes">Sus propios márgenes (2026-11-08); vacío: los de la sucursal.</param>
public sealed record ConsumiblesImpresoraDto(
    int Numero, string Nombre, PerfilImpresora Perfil, IReadOnlyList<CartuchoCostoDto> Tintas, decimal? CajaMantenimiento, IReadOnlyList<string> Faltan,
    int? PaginasPorCaja = null, MargenesImpresionDto? Margenes = null);

/// <summary>Los consumibles propios de una impresora como deben quedar: quien entró con «Configurar la impresora» y «Ver costos, utilidad y margen».</summary>
/// <param name="CajaMantenimiento">El precio de su caja; vacío, como está. Si no tenía, se le pone solo con <paramref name="PaginasPorCaja"/>.</param>
/// <param name="PaginasPorCaja">Las caras que dura su caja (para ponérsela si no tenía, o cambiarlas; después la calibración las corrige).</param>
public sealed record SolicitudConsumiblesImpresora(
    Guid UsuarioId, IReadOnlyList<CartuchoCostoDto> Tintas, decimal? CajaMantenimiento = null, int? PaginasPorCaja = null);

/// <summary>Los costos como deben quedar: quien entró a la caja con «Configurar la impresora» y «Ver costos, utilidad y margen».</summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudCostosImpresion(Guid UsuarioId, CostosImpresionDto Costos);

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
/// <param name="OtraSerie">
/// En su IP contesta otra impresora (otra serie; 2026-11-08): qué pasa y qué hacer. Mientras, sus lecturas no se guardan ni verifican sus hojas.
/// </param>
public sealed record EstadoImpresoraDto(
    ConfiguracionImpresoraDto Configuracion, LecturaImpresoraDto? Ultima, string? Error, ComparacionContadorDto? Hoy, string? OtraSerie = null);

// ── Varias impresoras por sucursal (2026-11-03) ─────────────────────────────

/// <summary>Una impresora de la sucursal y cómo está: sus niveles, su contador contra lo registrado y cómo se lee.</summary>
/// <param name="Numero">Su número en la sucursal: el que va en caja.json (<c>ColasImpresion</c>).</param>
/// <param name="ConsumiblesPropios">Su tinta no es la de la sucursal (la L5590): sus precios van aparte en los costos.</param>
/// <param name="Contador">Sus tarjetas «Copias o celular» (2026-11-08): cómo están y lo del día. Vacío de un servicio de antes.</param>
public sealed record ImpresoraDto(int Numero, string Nombre, PerfilImpresora Perfil, bool Activa, EstadoImpresoraDto Estado, bool ConsumiblesPropios,
    ContadorDeImpresoraDto? Contador = null);

/// <summary>Una impresora nueva en la sucursal: quien entró a la caja con «Configurar la impresora».</summary>
/// <param name="Host">Su IP o nombre en la red; vacío, no se lee (se escribe después).</param>
public sealed record SolicitudNuevaImpresora(Guid UsuarioId, string Nombre, PerfilImpresora Perfil, string? Host = null);

/// <summary>
/// Cambiar una impresora: cómo se lee, su nombre o darla de baja (o de alta otra vez). Lo vacío se queda como está. Quien entró a la caja
/// con «Configurar la impresora».
/// </summary>
/// <param name="Contador">Sus tarjetas «Copias o celular» (2026-11-08): si se ponen, la tinta con que se estiman y la pausa que cierra una.</param>
public sealed record SolicitudCambioImpresora(Guid UsuarioId, ConfiguracionImpresoraDto? Configuracion = null, string? Nombre = null, bool? Activa = null,
    AjustesContadorDto? Contador = null);

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

// ── Las caras que nadie registró: las tarjetas «Copias o celular» (2026-11-08) ──

/// <summary>
/// Lo que la tarjeta «Copias o celular» dice de su contador: las caras que contó la impresora y nadie registró (copias del panel o lo
/// impreso desde un celular), entre qué horas, y con qué tinta se estimó su precio (Carta, papel normal).
/// </summary>
/// <param name="Contadas">Las que contó la impresora para esta tarjeta.</param>
/// <param name="ContadasColor">De ellas, a color; vacío si la impresora no lo dice: el cajero elige.</param>
/// <param name="DesdeLocal">La lectura de antes de la primera cara.</param>
/// <param name="HastaLocal">La lectura de la última cara.</param>
public sealed record TarjetaDelContadorDto(int Contadas, int? ContadasColor, DateTime DesdeLocal, DateTime HastaLocal, double CoberturaBN, double CoberturaColor);

/// <summary>
/// Partir o corregir una tarjeta «Copias o celular»: cuántas de sus caras fueron copias del panel (salen como copias, en su línea) y
/// cuántas se cobran como del celular (lo demás que se le cuenta no se cobra). Lo que se escribe no puede pasar de lo que contó la impresora.
/// </summary>
/// <param name="CajaId">La caja que la parte: las copias quedan registradas en ella.</param>
public sealed record SolicitudPartirTarjeta(Guid UsuarioId, Guid CajaId, int CopiasBN, int CopiasColor, int CelularBN, int CelularColor);

/// <summary>«Ya se cobró como copias»: la tarjeta se junta con unas copias del panel ya registradas.</summary>
public sealed record SolicitudLigarTarjeta(Guid UsuarioId, Guid CopiasId);

/// <summary>Unas copias del panel registradas hoy, para ligar con una tarjeta «Copias o celular».</summary>
/// <param name="Impresora">En cuál se registraron, si la sucursal tiene más de una.</param>
public sealed record CopiasParaLigarDto(Guid Id, string Documento, DateTime FechaLocal, int Caras, int CarasColor, long PrecioCentavos,
    EstadoCobroImpresion Cobro, string? Impresora);

/// <summary>Las tarjetas «Copias o celular» de una impresora, como deben quedar (Configuración › Impresoras e impresiones).</summary>
/// <param name="CoberturaBN">Tinta negra por cara en B/N (%).</param>
/// <param name="CoberturaColor">Cada tinta por cara a color (%).</param>
/// <param name="PausaSegundos">Sin caras nuevas durante esto, se cierra la tarjeta.</param>
public sealed record AjustesContadorDto(bool Activas, double CoberturaBN, double CoberturaColor, int PausaSegundos);

/// <summary>
/// Lo del día de una impresora: lo que contó, lo registrado y lo que fue a tarjetas, cobradas o no.
/// </summary>
/// <param name="Contadas">Caras que contó la impresora.</param>
/// <param name="Impresas">Las registradas desde las PC (lo que salió).</param>
/// <param name="Copias">Las copias del panel registradas (a mano o al partir una tarjeta).</param>
/// <param name="EnTarjetas">Las que fueron a tarjetas «Copias o celular».</param>
/// <param name="Cobradas">De ellas, en tarjetas cobradas.</param>
/// <param name="PorCobrar">En tarjetas que siguen por cobrar.</param>
/// <param name="NoVendidas">En tarjetas marcadas como no vendidas.</param>
/// <param name="SinTarjeta">Las que nadie registró y todavía no están en una tarjeta (la que va a salir, o lo de antes de ponerlas).</param>
public sealed record ConciliacionDelDiaDto(long Contadas, int Impresas, int Copias, int EnTarjetas, int Cobradas, int PorCobrar, int NoVendidas,
    long SinTarjeta);

/// <summary>Las tarjetas «Copias o celular» de una impresora y lo del día.</summary>
/// <param name="Hoy">Vacío sin dos lecturas del contador hoy.</param>
public sealed record ContadorDeImpresoraDto(AjustesContadorDto Ajustes, ConciliacionDelDiaDto? Hoy);
