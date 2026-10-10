using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace PapeleriaPos.Impresion;

/// <summary>Un producto de la hoja de códigos.</summary>
public sealed record ProductoEnHoja(string Departamento, string Nombre, string Codigo, long? PrecioCentavos = null);

/// <summary>
/// La hoja de códigos enmicada del mostrador (docs/fase-2-inventario.md, entrega 4, parte 2): lo diminuto que no lleva
/// etiqueta (gomas, clips sueltos) con su nombre y su código de barras, para escanearlo desde ahí. Hoja carta, tres
/// columnas, separada por departamento si se pide. Se imprime al 100 % como las etiquetas.
/// </summary>
public static class HojaDeCodigos
{
    /// <summary>Escapa lo que rompería la página (&lt;, &amp;, comillas) y deja los acentos y la ñ como están.</summary>
    internal static readonly HtmlEncoder Escapar = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Barras anchas para que el lector las lea a través de la mica.</summary>
    public const decimal ModuloMm = 0.33m, AltoBarrasMm = 9m;

    /// <param name="titulo">Arriba de cada hoja: la sucursal y la fecha.</param>
    /// <param name="porDepartamento">Separar por departamento (en el orden en que aparece cada uno).</param>
    public static string Html(string titulo, IReadOnlyList<ProductoEnHoja> productos, bool porDepartamento)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Hoja de códigos</title><style>");
        html.Append("""
            @page { size: letter; margin: 10mm; }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; background: #fff; }
            body { font-family: Arial, Helvetica, sans-serif; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            h1 { font-size: 12pt; margin: 0 0 3mm; }
            h2 { font-size: 10pt; margin: 4mm 0 2mm; padding-bottom: 1mm; border-bottom: 0.3mm solid #000; break-after: avoid; }
            .productos { display: grid; grid-template-columns: repeat(3, 1fr); gap: 2mm 3mm; }
            .producto { break-inside: avoid; border: 0.2mm solid #999; border-radius: 1.5mm; padding: 2mm; display: flex; flex-direction: column; align-items: center; gap: 1mm; }
            .nombre { font-size: 9pt; font-weight: bold; text-align: center; line-height: 1.15; }
            .precio { font-size: 9pt; }
            .barras { display: block; }
            .texto-codigo { font-family: "Courier New", monospace; font-size: 7pt; line-height: 1; }
            .sin-lugar { font-size: 7pt; color: #b42318; text-align: center; }
            @media screen { html { overflow: hidden; } body { padding: 10mm; width: 215.9mm; } }
            """);
        html.Append("</style></head><body>");
        html.Append($"<h1>{Escapar.Encode(titulo)}</h1>");

        var grupos = porDepartamento
            ? productos.GroupBy(p => p.Departamento).Select(g => (Titulo: g.Key, Productos: g.ToList()))
            : [(Titulo: "", Productos: productos.ToList())];
        foreach (var (grupo, deGrupo) in grupos)
        {
            if (grupo.Length > 0)
                html.Append($"<h2>{Escapar.Encode(grupo)}</h2>");
            html.Append("<div class=\"productos\">");
            foreach (var p in deGrupo)
                html.Append(Producto(p));
            html.Append("</div>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static string Producto(ProductoEnHoja p)
    {
        var html = new StringBuilder("<div class=\"producto\">");
        html.Append($"<div class=\"nombre\">{Escapar.Encode(p.Nombre)}</div>");
        if (p.PrecioCentavos is { } precio)
            html.Append($"<div class=\"precio\">{(precio / 100m).ToString("$#,##0.00", Mexico)}</div>");
        var barras = CodigosDeBarras.Para(p.Codigo);
        // Una columna mide unos 63 mm: un código muy largo se adelgaza hasta donde el lector todavía lo lee.
        var modulo = Math.Min(ModuloMm, Math.Floor(58m / (barras.Modulos.Length + 2 * barras.ZonaEnBlanco) * 1000) / 1000);
        if (modulo < HojaEtiquetas.ModuloMinimoMm)
            html.Append($"<div class=\"sin-lugar\">El código {Escapar.Encode(p.Codigo)} es demasiado largo para la hoja</div>");
        else
            html.Append(CodigosDeBarras.Svg(barras, modulo, AltoBarrasMm));
        html.Append($"<div class=\"texto-codigo\">{Escapar.Encode(p.Codigo)}</div></div>");
        return html.ToString();
    }
}
