using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Una cotización guardada (2026-11-08, hito 3 de docs/impresion.md): un PDF que se midió en una caja y al que la caja principal le puso
/// precio, con los ajustes con que se imprimiría y la impresora. Se guarda sola al terminar de medir, con lo último que se vio en pantalla, y
/// vale <see cref="DiasQueVale"/> días, como en Control Print. Cuando ese documento se imprime, el trabajo se liga a ella
/// (<see cref="LoCotizado"/>): F2 lo dice y, si salió igual, se cobra lo cotizado. Además deja el par «cotizado contra impreso» con que se mide
/// qué tan preciso es cotizar en cada impresora.
/// <para>
/// Del documento solo se guarda su nombre (para reconocerlo al imprimirlo) y cuántas páginas tiene: ni miniatura ni texto (la regla del
/// módulo: una CURP no se queda en la base).
/// </para>
/// </summary>
public class CotizacionImpresion
{
    /// <summary>Días que vale el precio cotizado (como Control Print).</summary>
    public const int DiasQueVale = 15;

    /// <summary>Lo pone la caja que cotiza: cada cambio de opciones vuelve a guardar la misma.</summary>
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid SucursalId { get; set; }

    /// <summary>La impresora en que se imprimiría (con sus costos y su corrección).</summary>
    public Guid? ImpresoraId { get; set; }

    /// <summary>La caja donde se cotizó.</summary>
    public Guid? CajaId { get; set; }

    /// <summary>Quien cotizó (vacío si nadie había entrado).</summary>
    public Guid? UsuarioId { get; set; }

    public DateTime CreadaUtc { get; set; }
    public DateTime ModificadaUtc { get; set; }
    public DateTime VenceUtc { get; set; }

    /// <summary>El nombre del archivo, tal cual se eligió: con él se reconoce al imprimirlo. Nada más del documento.</summary>
    public required string Documento { get; set; }

    /// <summary>Páginas del documento completo.</summary>
    public int PaginasDocumento { get; set; }

    /// <summary>Las páginas que se cotizaron («1-3, 5»); vacío, todas.</summary>
    public string? Rango { get; set; }

    /// <summary>Páginas cotizadas (por copia).</summary>
    public int Paginas { get; set; }

    public int Copias { get; set; }
    public int Caras { get; set; }
    public int Hojas { get; set; }
    public int CarasColor { get; set; }
    public bool Color { get; set; }
    public bool Duplex { get; set; }
    public int PaginasPorHoja { get; set; } = 1;

    /// <summary>Carta, Oficio o A4 (como <see cref="TrabajoImpresion.Tamano"/>).</summary>
    public required string Tamano { get; set; }

    /// <summary>El nombre del tipo de papel, como el de los trabajos («Papel normal»).</summary>
    public required string TipoPapel { get; set; }

    /// <summary>La calidad del driver (Standard, StandardVivid…), como la de los trabajos.</summary>
    public required string Calidad { get; set; }

    public long PrecioCentavos { get; set; }
    public long CostoCentavos { get; set; }

    /// <summary>La tinta de lo cotizado en pesos, sin la corrección de la impresora: contra la de lo impreso se aprende la corrección.</summary>
    public decimal TintaSinCorregir { get; set; }

    /// <summary>La corrección de la tinta con que se cotizó (la de la impresora en ese momento).</summary>
    public double FactorCotizacion { get; set; } = 1.0;

    // Lo que pasó al imprimirlo.

    /// <summary>El trabajo impreso que se ligó a ella (uno solo: una cotización se usa una vez).</summary>
    public Guid? TrabajoId { get; set; }

    public DateTime? LigadaUtc { get; set; }

    /// <summary>El precio que calculó la impresión de lo mismo (antes de cobrar lo cotizado).</summary>
    public long? PrecioImpresoCentavos { get; set; }

    /// <summary>La tinta de lo impreso en pesos.</summary>
    public decimal? TintaImpresa { get; set; }

    /// <summary>Se imprimió con los mismos ajustes y las mismas páginas: el par sirve para medir qué tan preciso es cotizar.</summary>
    public bool MismosAjustes { get; set; }

    public bool Vigente(DateTime utc) => TrabajoId is null && utc <= VenceUtc;
}

/// <summary>
/// Cómo se reconoce lo cotizado al imprimirlo y qué se cobra (2026-11-08). Control Print pedía las mismas caras exactas y casi nunca lo
/// reconocía: aquí basta el mismo documento con casi las mismas páginas. Si además salió con los mismos ajustes y su precio está cerca de lo
/// cotizado, se cobra lo cotizado (lo que se le prometió al cliente); si no, F2 enseña los dos y el cajero elige.
/// </summary>
public static class LoCotizado
{
    /// <summary>Minutos antes de cotizar en que ya cuenta lo impreso (el reloj de una caja puede ir un poco atrás).</summary>
    public const int MinutosAntes = 5;

    /// <summary>
    /// Lo más que se aleja el precio impreso de lo cotizado para cobrar lo cotizado sin preguntar: el 20 % de lo cotizado, o $2 si es más
    /// (en un trabajo chico, un peso de redondeo ya es mucho por ciento).
    /// </summary>
    public static long Tolerancia(long cotizadoCentavos) => Math.Max(200, cotizadoCentavos * 20 / 100);

    public static bool DentroDeLaTolerancia(long cotizadoCentavos, long impresoCentavos) =>
        Math.Abs(impresoCentavos - cotizadoCentavos) <= Tolerancia(cotizadoCentavos);

    /// <summary>Páginas de más o de menos con que todavía es lo cotizado: una, o el 10 % en uno largo (el navegador agrega una hoja, un rango distinto).</summary>
    public static int ToleranciaPaginas(int paginas) => Math.Max(1, (int)Math.Ceiling(paginas * 0.1));

    /// <summary>Sin la extensión .pdf, sin espacios de más y en mayúsculas.</summary>
    public static string NormalizarNombre(string nombre)
    {
        var n = nombre.Trim();
        if (n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// ¿Es el mismo documento? Los programas nombran el trabajo distinto: Acrobat usa el nombre del archivo; Edge o Chrome le agregan el suyo
    /// («Microsoft Edge - Tarea.pdf») o la ruta. Basta con que el nombre del trabajo contenga el del archivo (como en Control Print).
    /// </summary>
    public static bool MismoDocumento(string archivo, string documento)
    {
        var a = NormalizarNombre(archivo);
        var d = NormalizarNombre(Path.GetFileName(documento.Replace('/', '\\').Split('\\')[^1]));
        if (a.Length == 0 || d.Length == 0)
            return false;
        return d == a || d.Contains(a, StringComparison.Ordinal) || NormalizarNombre(documento).Contains(a, StringComparison.Ordinal);
    }

    /// <summary>
    /// ¿Este trabajo es la impresión de lo cotizado? De la misma sucursal, el mismo documento, impreso después de cotizar (o
    /// <see cref="MinutosAntes"/> antes) y mientras vale, con casi las mismas páginas por copia, y la cotización no se usó ya.
    /// </summary>
    public static bool Coincide(CotizacionImpresion c, TrabajoImpresion t) =>
        c.TrabajoId is null && t.Tipo == TipoTrabajoImpresion.Impresion && t.SucursalId == c.SucursalId
        && t.ImpresoUtc >= c.CreadaUtc.AddMinutes(-MinutosAntes) && t.ImpresoUtc <= c.VenceUtc
        && Math.Abs(t.Paginas - c.Paginas) <= ToleranciaPaginas(c.Paginas)
        && MismoDocumento(c.Documento, t.Documento);

    /// <summary>
    /// Salió igual que lo cotizado: las mismas páginas, copias, doble cara, páginas por hoja, tamaño, papel y calidad, y el mismo color (un
    /// documento sin color da lo mismo en color que en B/N: la impresora lo saca solo con negro).
    /// </summary>
    public static bool MismosAjustes(CotizacionImpresion c, TrabajoImpresion t) =>
        t.Paginas == c.Paginas && t.Copias == c.Copias && t.Duplex == c.Duplex
        && t.Caras == (c.Paginas + c.PaginasPorHoja - 1) / Math.Max(1, c.PaginasPorHoja) * c.Copias
        && string.Equals(t.Tamano, c.Tamano, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.TipoPapel, c.TipoPapel, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.Calidad, c.Calidad, StringComparison.OrdinalIgnoreCase)
        && (t.Color == c.Color || (t.CarasColor == 0 && c.CarasColor == 0));

    /// <summary>De las cotizaciones que coinciden con el trabajo, la que más se le parece: la de sus mismos ajustes, la de las páginas más cercanas y la más nueva.</summary>
    public static CotizacionImpresion? Elegir(IEnumerable<CotizacionImpresion> cotizaciones, TrabajoImpresion t) =>
        cotizaciones.Where(c => Coincide(c, t))
            .OrderByDescending(c => MismosAjustes(c, t))
            .ThenBy(c => Math.Abs(t.Paginas - c.Paginas))
            .ThenByDescending(c => c.CreadaUtc)
            .FirstOrDefault();

    /// <summary>Se cobra lo cotizado sin preguntar: salió igual y su precio está dentro de la tolerancia.</summary>
    public static bool SeCobraSinPreguntar(CotizacionImpresion c, TrabajoImpresion t, long impresoCentavos) =>
        MismosAjustes(c, t) && DentroDeLaTolerancia(c.PrecioCentavos, impresoCentavos);

    /// <summary>Pares que hacen falta para sugerir la corrección de una impresora.</summary>
    public const int ParesParaSugerir = 5;

    /// <summary>
    /// Qué tan preciso es cotizar en una impresora, con los pares cotizado-impreso que salieron igual (<see cref="CotizacionImpresion.MismosAjustes"/>):
    /// cuántos, la diferencia promedio del precio impreso contra el cotizado (positiva: lo impreso salió más caro) y, con
    /// <see cref="ParesParaSugerir"/> o más, la corrección de la tinta que los igualaría (la tinta impresa entre la cotizada sin corregir, de 0.5
    /// a 2). La corrección solo toca la tinta: el papel y lo demás ya salen iguales.
    /// </summary>
    public static PrecisionDeCotizar Precision(IEnumerable<CotizacionImpresion> cotizaciones)
    {
        var pares = cotizaciones.Where(c => c is { TrabajoId: not null, MismosAjustes: true, PrecioImpresoCentavos: not null, PrecioCentavos: > 0 })
            .ToList();
        if (pares.Count == 0)
            return new PrecisionDeCotizar(0, null, null);
        var diferencia = pares.Average(c => (double)(c.PrecioImpresoCentavos!.Value - c.PrecioCentavos) / c.PrecioCentavos) * 100;
        var conTinta = pares.Where(c => c.TintaSinCorregir > 0 && c.TintaImpresa is not null).ToList();
        double? sugerido = null;
        if (conTinta.Count >= ParesParaSugerir)
        {
            var proporcion = (double)(conTinta.Sum(c => c.TintaImpresa!.Value) / conTinta.Sum(c => c.TintaSinCorregir));
            sugerido = Math.Round(Math.Clamp(proporcion, 0.5, 2.0), 2);
        }
        return new PrecisionDeCotizar(pares.Count, Math.Round(diferencia, 1), sugerido);
    }
}

/// <param name="Pares">Cotizaciones que se imprimieron igual.</param>
/// <param name="DiferenciaPorcentaje">Promedio de (impreso − cotizado) / cotizado, en %: positivo, cotizar dio menos.</param>
/// <param name="FactorSugerido">La corrección de la tinta que igualaría lo cotizado con lo impreso (con pocos pares, vacío).</param>
public sealed record PrecisionDeCotizar(int Pares, double? DiferenciaPorcentaje, double? FactorSugerido);
