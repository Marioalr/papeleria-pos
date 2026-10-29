using System.Text.Json;
using System.Text.Json.Serialization;

namespace PapeleriaPos.Impresiones.ControlPrint;

// Lo que contesta la API de Control Print 1.12 (protocolo 1), con nombres en español. Cada campo dice con
// [JsonPropertyName] cómo se llama allá (ControlPrint.Core: JobRecord, DaySummary, QuoteRecord, PrinterStatus…; los
// manda en camelCase). Los campos que esta versión no conoce no se pierden: quedan en «Otros» (salvo en los usuarios,
// de los que solo se guarda lo que los identifica).

// ── Enum (los nombres de Control Print en JsonStringEnumMemberName; los números son los suyos) ──────────────────

public enum EstadoTrabajoControlPrint
{
    Desconocido = -1,
    /// <summary>Visto en la cola, aún sin analizar.</summary>
    [JsonStringEnumMemberName("Detected")] Detectado = 0,
    /// <summary>Con costo calculado; puede seguir imprimiéndose.</summary>
    [JsonStringEnumMemberName("Analyzed")] Analizado = 1,
    /// <summary>La impresora terminó el trabajo.</summary>
    [JsonStringEnumMemberName("Printed")] Impreso = 2,
    /// <summary>Se quitó de la cola antes de imprimirse.</summary>
    [JsonStringEnumMemberName("Cancelled")] Cancelado = 3,
    /// <summary>Salió de la cola sin poder analizarse (no es una no vendida).</summary>
    [JsonStringEnumMemberName("NotAnalyzed")] SinAnalizar = 4,
}

public enum CobroControlPrint
{
    Desconocido = -1,
    [JsonStringEnumMemberName("Pending")] PorCobrar = 0,
    [JsonStringEnumMemberName("Paid")] Cobrado = 1,
    /// <summary>Se imprimió pero no se vendió, con su motivo.</summary>
    [JsonStringEnumMemberName("Unsold")] NoVendido = 2,
}

public enum MotivoNoVendidaControlPrint
{
    Desconocido = -1,
    [JsonStringEnumMemberName("NotWanted")] NoEraLoQueQueria = 1,
    [JsonStringEnumMemberName("BadPrint")] SalioMal = 2,
    [JsonStringEnumMemberName("PaperJam")] Atasco = 3,
    /// <summary>Otro (con nota obligatoria).</summary>
    [JsonStringEnumMemberName("Other")] Otro = 9,
}

/// <summary>De dónde sale el precio cobrado.</summary>
public enum TipoPrecioControlPrint
{
    Desconocido = -1,
    [JsonStringEnumMemberName("Calculated")] Calculado = 0,
    /// <summary>Escrito a mano (solo el administrador).</summary>
    [JsonStringEnumMemberName("Manual")] AMano = 1,
    /// <summary>El botón de precio especial (con el nombre del cliente).</summary>
    [JsonStringEnumMemberName("Special")] Especial = 2,
    /// <summary>Solo lo que salió: la impresora contó menos hojas.</summary>
    [JsonStringEnumMemberName("Partial")] SoloLoQueSalio = 3,
    /// <summary>Copias del panel de la impresora: lo que se cobró al registrarlas.</summary>
    [JsonStringEnumMemberName("Copies")] Copias = 4,
    /// <summary>El de una cotización guardada.</summary>
    [JsonStringEnumMemberName("Quoted")] Cotizado = 5,
}

public enum TipoTrabajoControlPrint
{
    Desconocido = -1,
    /// <summary>Impreso desde una PC (lo registró su monitor).</summary>
    [JsonStringEnumMemberName("Print")] Impresion = 0,
    /// <summary>Copias hechas en el panel de la impresora (se registran a mano al cobrarlas).</summary>
    [JsonStringEnumMemberName("PanelCopy")] CopiaEnElPanel = 1,
}

public enum NivelAnalisisControlPrint
{
    Desconocido = -1,
    /// <summary>Por el número de páginas, sin ver el contenido.</summary>
    [JsonStringEnumMemberName("Estimated")] Estimado = 1,
    /// <summary>La tinta medida página por página.</summary>
    [JsonStringEnumMemberName("Measured")] Medido = 2,
}

public enum RolControlPrint
{
    Desconocido = -1,
    [JsonStringEnumMemberName("Admin")] Administrador = 1,
    [JsonStringEnumMemberName("Employee")] Empleado = 2,
    /// <summary>Quien usa Control Print sin iniciar sesión (y a quien pertenecen sus trabajos).</summary>
    [JsonStringEnumMemberName("Guest")] Invitado = 3,
}

/// <summary>Quién ve los trabajos en Control Print (Ajustes).</summary>
public enum VisibilidadTrabajosControlPrint
{
    Desconocido = -1,
    [JsonStringEnumMemberName("All")] Todos = 0,
    /// <summary>Cada quien los que atendió; el administrador, todos. Sin sesión, el cliente solo ve los del invitado.</summary>
    [JsonStringEnumMemberName("OwnOnly")] CadaQuienLosSuyos = 1,
}

// ── Trabajos ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Un trabajo de Control Print (su JobRecord): una impresión de una PC o unas copias del panel.</summary>
public sealed class TrabajoControlPrint
{
    [JsonPropertyName("id")] public long Id { get; init; }
    /// <summary>La PC cuyo monitor lo registró.</summary>
    [JsonPropertyName("sourcePc")] public string PcOrigen { get; init; } = "";
    [JsonPropertyName("kind")] public TipoTrabajoControlPrint Tipo { get; init; }
    /// <summary>La cola de Windows («Copias en la impresora» en las copias del panel).</summary>
    [JsonPropertyName("printer")] public string Impresora { get; init; } = "";
    /// <summary>El número del trabajo en la cola de Windows (se repite entre PC).</summary>
    [JsonPropertyName("spoolJobId")] public int NumeroEnLaCola { get; init; }
    [JsonPropertyName("submittedUtc")] public DateTime EnviadoUtc { get; init; }
    [JsonPropertyName("document")] public string Documento { get; init; } = "";
    /// <summary>El usuario de Windows que imprimió.</summary>
    [JsonPropertyName("userName")] public string UsuarioWindows { get; init; } = "";
    [JsonPropertyName("machine")] public string Equipo { get; init; } = "";
    [JsonPropertyName("datatype")] public string TipoDeDatos { get; init; } = "";
    [JsonPropertyName("detectedUtc")] public DateTime DetectadoUtc { get; init; }
    [JsonPropertyName("analyzedUtc")] public DateTime? AnalizadoUtc { get; init; }
    [JsonPropertyName("finishedUtc")] public DateTime? TerminadoUtc { get; init; }
    [JsonPropertyName("state")] public EstadoTrabajoControlPrint Estado { get; init; }

    [JsonPropertyName("pages")] public int Paginas { get; init; }
    [JsonPropertyName("copies")] public int Copias { get; init; }
    [JsonPropertyName("sides")] public int Caras { get; init; }
    /// <summary>Caras que salen a color (el resto, en B/N).</summary>
    [JsonPropertyName("colorSides")] public int CarasColor { get; init; }
    [JsonPropertyName("sheets")] public int Hojas { get; init; }
    [JsonPropertyName("color")] public bool Color { get; init; }
    [JsonPropertyName("quality")] public string Calidad { get; init; } = "";
    [JsonPropertyName("mediaType")] public string TipoPapel { get; init; } = "";
    [JsonPropertyName("mediaName")] public string NombrePapel { get; init; } = "";
    [JsonPropertyName("paperSize")] public string TamanoPapel { get; init; } = "";
    [JsonPropertyName("duplex")] public bool DobleCara { get; init; }
    [JsonPropertyName("pagesPerSheet")] public int PaginasPorHoja { get; init; }

    /// <summary>Tinta gastada por canal (los puntos de la calibración).</summary>
    [JsonPropertyName("inkC")] public double TintaC { get; init; }
    [JsonPropertyName("inkM")] public double TintaM { get; init; }
    [JsonPropertyName("inkY")] public double TintaY { get; init; }
    [JsonPropertyName("inkK")] public double TintaK { get; init; }
    [JsonPropertyName("costInk")] public decimal CostoTinta { get; init; }
    [JsonPropertyName("costPaper")] public decimal CostoPapel { get; init; }
    [JsonPropertyName("costMaintenance")] public decimal CostoMantenimiento { get; init; }
    [JsonPropertyName("costPower")] public decimal CostoLuz { get; init; }
    [JsonPropertyName("costExtras")] public decimal CostoExtras { get; init; }
    /// <summary>Depreciación de la impresora (va dentro del costo total).</summary>
    [JsonPropertyName("costDepreciation")] public decimal CostoDepreciacion { get; init; }
    [JsonPropertyName("costTotal")] public decimal CostoTotal { get; init; }
    [JsonPropertyName("marginPercent")] public decimal MargenPorcentaje { get; init; }
    /// <summary>Margen propio del papel con que se calculó; vacío = el mismo que el resto.</summary>
    [JsonPropertyName("paperMarginPercent")] public decimal? MargenPapelPorcentaje { get; init; }
    /// <summary>El precio calculado.</summary>
    [JsonPropertyName("price")] public decimal Precio { get; init; }
    /// <summary>El precio puesto (a mano, especial, solo lo que salió, cotizado o de las copias); vacío = el calculado.</summary>
    [JsonPropertyName("priceOverride")] public decimal? PrecioPuesto { get; init; }
    [JsonPropertyName("priceKind")] public TipoPrecioControlPrint TipoPrecio { get; init; }
    [JsonPropertyName("priceSetUtc")] public DateTime? PrecioPuestoUtc { get; init; }
    [JsonPropertyName("priceSetBy")] public string? PrecioPuestoPor { get; init; }
    /// <summary>El cliente del precio especial o de la cotización.</summary>
    [JsonPropertyName("specialName")] public string? ClienteDelPrecio { get; init; }

    [JsonPropertyName("level")] public NivelAnalisisControlPrint? NivelAnalisis { get; init; }
    /// <summary>La nota del análisis (por qué no se midió, advertencias).</summary>
    [JsonPropertyName("note")] public string? NotaAnalisis { get; init; }
    /// <summary>El desglose completo en JSON: costos por canal, los ajustes con que salió y la tinta de cada página.</summary>
    [JsonPropertyName("detailJson")] public string? DesgloseJson { get; init; }
    /// <summary>PNG; solo viene en <see cref="ClienteControlPrint.TrabajoAsync"/> (la búsqueda no las trae).</summary>
    [JsonPropertyName("thumbnail")] public byte[]? Miniatura { get; init; }

    /// <summary>El nombre que le puso quien atendió (cliente, trabajo…).</summary>
    [JsonPropertyName("label")] public string? Etiqueta { get; init; }
    [JsonPropertyName("payment")] public CobroControlPrint Cobro { get; init; }
    [JsonPropertyName("notes")] public string? Notas { get; init; }
    /// <summary>Quién y cuándo cambió el cobro por última vez.</summary>
    [JsonPropertyName("paymentBy")] public string? CobroPor { get; init; }
    [JsonPropertyName("paymentUtc")] public DateTime? CobroUtc { get; init; }
    [JsonPropertyName("unsoldReason")] public MotivoNoVendidaControlPrint? MotivoNoVendida { get; init; }
    [JsonPropertyName("unsoldNote")] public string? NotaNoVendida { get; init; }

    /// <summary>El usuario de Control Print que atendía al imprimirse (el invitado si nadie había entrado).</summary>
    [JsonPropertyName("ownerId")] public long? AtendioId { get; init; }
    [JsonPropertyName("ownerName")] public string? AtendioNombre { get; init; }
    /// <summary>Quién y cuándo lo reasignó a otro usuario.</summary>
    [JsonPropertyName("ownerSetBy")] public string? ReasignadoPor { get; init; }
    [JsonPropertyName("ownerSetUtc")] public DateTime? ReasignadoUtc { get; init; }

    /// <summary>Las caras que contó la impresora (vacío = sin verificar); menos que <see cref="Caras"/>: no salieron todas.</summary>
    [JsonPropertyName("countedSides")] public int? CarasContadas { get; init; }

    // Calculados por Control Print (sus propiedades de solo lectura también viajan).
    /// <summary>Analizado o impreso: tiene precio.</summary>
    [JsonPropertyName("isChargeable")] public bool Cobrable { get; init; }
    [JsonPropertyName("hasPrintShortfall")] public bool FaltaronCaras { get; init; }
    /// <summary>Lo que se cobra: el precio puesto o, si no hay, el calculado.</summary>
    [JsonPropertyName("effectivePrice")] public decimal PrecioACobrar { get; init; }
    /// <summary>La etiqueta o, si no hay, el documento.</summary>
    [JsonPropertyName("displayName")] public string? NombreQueSeVe { get; init; }

    /// <summary>Lo que mandó Control Print y esta versión no conoce.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Otros { get; init; }
}

/// <summary>
/// Qué trabajos pedir (su JobQuery). Las fechas son UTC y van sobre la hora en que se detectó el trabajo; los más nuevos
/// primero (por número). Si Control Print está en «cada quien ve los suyos», el servidor solo devuelve los del invitado.
/// </summary>
public sealed record BusquedaTrabajosControlPrint
{
    [JsonPropertyName("fromUtc")] public DateTime? DesdeUtc { get; init; }
    [JsonPropertyName("toUtc")] public DateTime? HastaUtc { get; init; }
    [JsonPropertyName("payment")] public CobroControlPrint? Cobro { get; init; }
    /// <summary>Solo los que tienen precio (analizados o impresos).</summary>
    [JsonPropertyName("onlyChargeable")] public bool SoloCobrables { get; init; }
    [JsonPropertyName("onlySpecial")] public bool SoloEspeciales { get; init; }
    [JsonPropertyName("ownerId")] public long? AtendioId { get; init; }
    /// <summary>En el documento, la etiqueta, el cliente, las notas, quién atendió o la PC; «#123» o «123» también por número.</summary>
    [JsonPropertyName("text")] public string? Texto { get; init; }
    /// <summary>Cuántos como máximo (0 o menos: todos).</summary>
    [JsonPropertyName("limit")] public int Limite { get; init; } = 200;
    [JsonPropertyName("offset")] public int Saltar { get; init; }
}

/// <summary>Una página de la búsqueda y cuántos cumplen el filtro en total.</summary>
public sealed record PaginaTrabajosControlPrint(
    [property: JsonPropertyName("jobs")] IReadOnlyList<TrabajoControlPrint> Trabajos,
    [property: JsonPropertyName("total")] int Total);

/// <summary>El resumen de un día de Control Print (su DaySummary): sin cancelados ni sin analizar.</summary>
/// <param name="Precio">Lo vendido (sin las no vendidas).</param>
/// <param name="CostoNoVendidas">El costo de las no vendidas (pérdida).</param>
public sealed record ResumenDiaControlPrint(
    [property: JsonPropertyName("jobs")] int Trabajos,
    [property: JsonPropertyName("sides")] int Caras,
    [property: JsonPropertyName("cost")] decimal Costo,
    [property: JsonPropertyName("price")] decimal Precio,
    [property: JsonPropertyName("collected")] decimal Cobrado,
    [property: JsonPropertyName("unsold")] int NoVendidas,
    [property: JsonPropertyName("unsoldCost")] decimal CostoNoVendidas,
    [property: JsonPropertyName("pending")] decimal PorCobrar);

/// <summary>Las caras registradas en un periodo (su RegisteredSides): para comparar con el contador de la impresora.</summary>
public sealed record CarasRegistradasControlPrint(
    [property: JsonPropertyName("printSides")] int CarasImpresas,
    [property: JsonPropertyName("printColorSides")] int CarasImpresasColor,
    [property: JsonPropertyName("copySides")] int CarasCopias,
    [property: JsonPropertyName("copyColorSides")] int CarasCopiasColor);

// ── La impresora ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Una lectura de la impresora (su PrinterStatus): niveles en % restante y contadores; vacío = no se supo.</summary>
public sealed class LecturaImpresoraControlPrint
{
    [JsonPropertyName("takenUtc")] public DateTime TomadaUtc { get; init; }
    [JsonPropertyName("inkK")] public int? TintaK { get; init; }
    [JsonPropertyName("inkC")] public int? TintaC { get; init; }
    [JsonPropertyName("inkM")] public int? TintaM { get; init; }
    [JsonPropertyName("inkY")] public int? TintaY { get; init; }
    /// <summary>Vida restante de la caja de mantenimiento (%).</summary>
    [JsonPropertyName("maintenance")] public int? CajaMantenimiento { get; init; }
    [JsonPropertyName("lifeCount")] public long? ContadorTotal { get; init; }
    [JsonPropertyName("colorCount")] public long? ContadorColor { get; init; }
    [JsonPropertyName("monoCount")] public long? ContadorBN { get; init; }
    [JsonPropertyName("serial")] public string? Serie { get; init; }
    [JsonPropertyName("errors")] public List<string>? Errores { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Otros { get; init; }
}

/// <summary>Cómo está el servidor (su ServerHealth): lo que sale en la barra de abajo de las PC conectadas.</summary>
public sealed record SaludControlPrint(
    [property: JsonPropertyName("printerText")] string? Impresora,
    [property: JsonPropertyName("printerOk")] bool ImpresoraBien,
    [property: JsonPropertyName("monitorText")] string? Monitor,
    [property: JsonPropertyName("monitorOk")] bool MonitorBien);

// ── Cotizaciones ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Una cotización guardada (su QuoteRecord). El PDF no se guarda, solo su nombre.</summary>
public sealed class CotizacionControlPrint
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("createdUtc")] public DateTime CreadaUtc { get; init; }
    [JsonPropertyName("createdBy")] public string? CreadaPor { get; init; }
    [JsonPropertyName("ownerId")] public long? UsuarioId { get; init; }
    [JsonPropertyName("sourcePc")] public string PcOrigen { get; init; } = "";
    [JsonPropertyName("fileName")] public string Archivo { get; init; } = "";
    /// <summary>Páginas del documento.</summary>
    [JsonPropertyName("pageCount")] public int PaginasDocumento { get; init; }
    [JsonPropertyName("pageRange")] public string? Rango { get; init; }
    /// <summary>Páginas cotizadas (las del rango).</summary>
    [JsonPropertyName("pages")] public int Paginas { get; init; }
    [JsonPropertyName("copies")] public int Copias { get; init; }
    [JsonPropertyName("sides")] public int Caras { get; init; }
    [JsonPropertyName("sheets")] public int Hojas { get; init; }
    [JsonPropertyName("colorSides")] public int CarasColor { get; init; }
    /// <summary>Papel, tamaño, color, calidad, doble cara, copias, páginas por hoja y rango, tal como los manda.</summary>
    [JsonPropertyName("options")] public JsonElement Opciones { get; init; }
    [JsonPropertyName("costTotal")] public decimal CostoTotal { get; init; }
    /// <summary>El precio normal calculado.</summary>
    [JsonPropertyName("price")] public decimal Precio { get; init; }
    /// <summary>Lo que se le cotizó al cliente (el especial, si se aplicó).</summary>
    [JsonPropertyName("charged")] public decimal Cotizado { get; init; }
    [JsonPropertyName("special")] public bool Especial { get; init; }
    [JsonPropertyName("clientName")] public string? Cliente { get; init; }
    /// <summary>El resumen para el cliente (sin costos), como se copió.</summary>
    [JsonPropertyName("summary")] public string Resumen { get; init; } = "";
    [JsonPropertyName("validUntilUtc")] public DateTime ValeHastaUtc { get; init; }
    /// <summary>El trabajo al que se le cobró este precio.</summary>
    [JsonPropertyName("jobId")] public long? TrabajoId { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Otros { get; init; }
}

// ── Usuarios, configuración y cambios ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Un usuario de Control Print: solo lo que lo identifica. Lo demás que mande el servidor (fechas, y lo que agregue una
/// versión nueva, como un hash o una sesión) se descarta al leerlo: nunca llega a guardarse.
/// </summary>
public sealed record UsuarioControlPrint(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Nombre,
    [property: JsonPropertyName("role")] RolControlPrint Rol,
    [property: JsonPropertyName("active")] bool Activo);

/// <summary>Las dos configuraciones compartidas de Control Print.</summary>
public enum ConfiguracionControlPrint
{
    /// <summary>Costos: tinta, papel, mantenimiento, luz, depreciación, márgenes, mayoreo y calibración («cost»).</summary>
    Costos,
    /// <summary>Ajustes de la app: la impresora, cada cuánto se lee, quién ve los trabajos, respaldos, sesiones («app»).</summary>
    Ajustes,
}

/// <summary>
/// De los ajustes de la app de Control Print (su AppSettings), lo que el POS usa. Ese JSON va con los nombres en
/// PascalCase (SettingsStore), no en camelCase como el resto de la API.
/// </summary>
public sealed record AjustesControlPrint
{
    /// <summary>La IP o el nombre de la impresora que lee Control Print.</summary>
    [JsonPropertyName("PrinterHost")] public string? Impresora { get; init; }
    [JsonPropertyName("StatusPollMinutes")] public int MinutosEntreLecturas { get; init; }
    [JsonPropertyName("JobsVisibility")] public VisibilidadTrabajosControlPrint VisibilidadTrabajos { get; init; }
}

/// <summary>
/// Los contadores de cambios del servidor (su ChangeVersions): uno por tipo de dato, sube cuando algo cambia; arrancan en 1.
/// </summary>
public sealed record VersionesControlPrint(
    [property: JsonPropertyName("jobs")] long Trabajos,
    [property: JsonPropertyName("settings")] long Configuracion,
    [property: JsonPropertyName("users")] long Usuarios,
    [property: JsonPropertyName("status")] long Impresora,
    [property: JsonPropertyName("appVersion")] string VersionApp)
{
    /// <summary>Lo que manda un cliente nuevo: el servidor contesta de inmediato con los de ahora.</summary>
    public static readonly VersionesControlPrint Ninguna = new(0, 0, 0, 0, "");
}

// ── Lo que solo usa el cliente ─────────────────────────────────────────────────────────────────────────────────

internal sealed record SaludoControlPrint(
    [property: JsonPropertyName("name")] string? Nombre,
    [property: JsonPropertyName("appVersion")] string? VersionApp,
    [property: JsonPropertyName("protocolVersion")] int VersionProtocolo,
    [property: JsonPropertyName("proof")] string? Prueba);

internal sealed record ErrorDeControlPrint(
    [property: JsonPropertyName("kind")] string? Tipo,
    [property: JsonPropertyName("message")] string? Mensaje);
