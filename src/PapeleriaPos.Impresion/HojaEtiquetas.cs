using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Impresion;

/// <summary>Lo que lleva una etiqueta: casi nunca el precio (cambia y habría que reimprimir).</summary>
public sealed record Etiqueta(string Nombre, string Codigo, long? PrecioCentavos = null);

/// <summary>
/// Las hojas de etiquetas como una página HTML lista para imprimir en la WF-6590 (docs/fase-2-inventario.md, entrega 4):
/// todo se mide en milímetros desde la orilla de la hoja y el código de barras es un dibujo (SVG), así que sale igual en
/// cualquier impresora si se imprime al 100 %. El ajuste mueve todo unos milímetros si la impresora jala la hoja chueca.
/// </summary>
public static class HojaEtiquetas
{
    /// <summary>Escapa lo que rompería la página (&lt;, &amp;, comillas) y deja los acentos y la ñ como están.</summary>
    internal static readonly HtmlEncoder Escapar = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly CultureInfo Invariante = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>El módulo (la barra más delgada) más ancho y más angosto que se dibuja: debajo de 0.19 mm el lector ya no lee bien.</summary>
    public const decimal ModuloMaximoMm = 0.33m, ModuloMinimoMm = 0.19m;

    /// <param name="empezarEn">En qué etiqueta de la primera hoja empezar (1 = la primera), para usar hojas ya empezadas.</param>
    /// <param name="ajusteXMm">Mueve todo a la derecha (o a la izquierda si es negativo).</param>
    /// <param name="ajusteYMm">Mueve todo hacia abajo (o hacia arriba si es negativo).</param>
    /// <param name="soloBordes">La hoja de prueba: el contorno de cada etiqueta, para compararla a contraluz con una hoja de etiquetas.</param>
    public static string Html(
        FormatoEtiquetas formato, IReadOnlyList<Etiqueta> etiquetas, int empezarEn = 1, decimal ajusteXMm = 0, decimal ajusteYMm = 0, bool soloBordes = false)
    {
        if (formato.Problema() is { } problema)
            throw new ArgumentException(problema, nameof(formato));
        var saltar = Math.Clamp(empezarEn, 1, formato.PorHoja) - 1;
        var lugares = soloBordes ? Enumerable.Repeat<Etiqueta?>(null, formato.PorHoja).ToList() : [.. Enumerable.Repeat<Etiqueta?>(null, saltar), .. etiquetas];

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Etiquetas</title><style>");
        html.Append(Estilos(formato));
        html.Append("</style></head><body>");
        for (var inicio = 0; inicio < Math.Max(lugares.Count, 1); inicio += formato.PorHoja)
        {
            html.Append("<div class=\"hoja\">");
            for (var i = inicio; i < Math.Min(inicio + formato.PorHoja, lugares.Count); i++)
            {
                var (fila, columna) = Math.DivRem(i - inicio, formato.Columnas);
                var x = formato.MargenIzquierdoMm + columna * formato.PasoHorizontalMm + ajusteXMm;
                var y = formato.MargenSuperiorMm + fila * formato.PasoVerticalMm + ajusteYMm;
                if (soloBordes)
                {
                    html.Append($"<div class=\"etiqueta borde\" style=\"left:{Mm(x)};top:{Mm(y)}\">{i + 1}</div>");
                    continue;
                }
                if (lugares[i] is not { } etiqueta)
                    continue;
                html.Append($"<div class=\"etiqueta\" style=\"left:{Mm(x)};top:{Mm(y)}\">");
                html.Append(Contenido(formato, etiqueta));
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    /// <summary>Cuántas hojas salen.</summary>
    public static int Hojas(FormatoEtiquetas formato, int etiquetas, int empezarEn = 1) =>
        Math.Max(1, (int)Math.Ceiling((Math.Clamp(empezarEn, 1, formato.PorHoja) - 1 + etiquetas) / (decimal)formato.PorHoja));

    /// <summary>El ancho del módulo para que el código quepa en la etiqueta; vacío si no cabe ni con el más delgado.</summary>
    public static decimal? Modulo(FormatoEtiquetas formato, Barras barras)
    {
        var disponible = formato.AnchoMm - 2 * Relleno(formato);
        var modulo = Math.Min(ModuloMaximoMm, disponible / (barras.Modulos.Length + 2 * barras.ZonaEnBlanco));
        modulo = Math.Floor(modulo * 1000) / 1000;
        return modulo < ModuloMinimoMm ? null : modulo;
    }

    private static decimal Relleno(FormatoEtiquetas f) => f.AltoMm < 16 ? 1m : 2m;

    private static string Estilos(FormatoEtiquetas f)
    {
        var chica = f.AltoMm < 16;
        return $$"""
            @page { size: letter; margin: 0; }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; background: #fff; }
            body { font-family: Arial, Helvetica, sans-serif; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            .hoja { position: relative; width: {{Mm(FormatoEtiquetas.AnchoCartaMm)}}; height: {{Mm(FormatoEtiquetas.AltoCartaMm)}}; overflow: hidden; break-after: page; }
            .hoja:last-child { break-after: auto; }
            .etiqueta { position: absolute; width: {{Mm(f.AnchoMm)}}; height: {{Mm(f.AltoMm)}}; padding: {{Mm(Relleno(f))}}; overflow: hidden;
                display: flex; flex-direction: column; align-items: center; justify-content: space-between; }
            .etiqueta.borde { border: 0.2mm dashed #888; justify-content: center; font-size: 9pt; color: #888; }
            .nombre { width: 100%; font-size: {{(chica ? "5.5pt" : "7.5pt")}}; line-height: 1.15; text-align: center; overflow: hidden;
                display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: {{(chica ? 1 : 2)}}; }
            .precio { font-weight: bold; }
            .barras { display: block; }
            .texto-codigo { font-family: "Courier New", monospace; font-size: {{(chica ? "5pt" : "6.5pt")}}; line-height: 1; letter-spacing: 0.3pt; }
            .sin-lugar { font-size: 6pt; color: #b42318; text-align: center; }
            @media screen { html { overflow: hidden; } .hoja { box-shadow: 0 0 0 1px #c9d2dc; margin-bottom: 8mm; } }
            """;
    }

    private static string Contenido(FormatoEtiquetas f, Etiqueta e)
    {
        var html = new StringBuilder();
        var nombre = Escapar.Encode(e.Nombre);
        var precio = e.PrecioCentavos is { } p ? $" <span class=\"precio\">{(p / 100m).ToString("$#,##0.00", Mexico)}</span>" : "";
        html.Append($"<div class=\"nombre\">{nombre}{precio}</div>");

        var barras = CodigosDeBarras.Para(e.Codigo);
        if (Modulo(f, barras) is not { } modulo)
        {
            html.Append($"<div class=\"sin-lugar\">El código {Escapar.Encode(e.Codigo)} no cabe en esta etiqueta</div>");
            return html.ToString();
        }
        // El alto que queda: la etiqueta menos el relleno, el nombre y el texto del código.
        var chica = f.AltoMm < 16;
        var alto = Math.Max(3m, f.AltoMm - 2 * Relleno(f) - (chica ? 2.4m : 7.2m) - (chica ? 2m : 2.6m));
        html.Append(CodigosDeBarras.Svg(barras, modulo, alto));
        html.Append($"<div class=\"texto-codigo\">{Escapar.Encode(e.Codigo)}</div>");
        return html.ToString();
    }

    internal static string Mm(decimal valor) => Math.Round(valor, 3).ToString("0.###", Invariante) + "mm";
}
