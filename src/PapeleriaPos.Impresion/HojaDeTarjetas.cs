using System.Text;

namespace PapeleriaPos.Impresion;

/// <summary>Una tarjeta de precio: el nombre del producto, su precio y el código que escanea el cajero.</summary>
public sealed record TarjetaDePrecioEnHoja(string Nombre, long PrecioCentavos, string Codigo);

/// <summary>Dónde va una tarjeta en su hoja: su renglón y su columna (desde cero, de arriba abajo y de izquierda a derecha).</summary>
public sealed record TarjetaEnLaHoja(int Fila, int Columna, TarjetaDePrecioEnHoja Tarjeta);

/// <summary>Una hoja de tarjetas, con las suyas en orden.</summary>
public sealed record PaginaDeTarjetas(int Numero, IReadOnlyList<TarjetaEnLaHoja> Tarjetas);

/// <summary>
/// Las tarjetas de precio (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala», punto 5): una por producto,
/// para los exhibidores y la mesa de la temporada, con el precio grande para que el cliente lo vea sin preguntar y el código
/// para que el cajero la escanee en lugar del producto. Salen en hojas carta, <see cref="PorHoja"/> por hoja con su línea para
/// recortar, en la impresora de inyección o láser, como la hoja de códigos: todo en milímetros, al 100 %.
/// </summary>
public static class HojaDeTarjetas
{
    public const int Columnas = 3, Filas = 6, PorHoja = Columnas * Filas;

    /// <summary>Lo que mide cada tarjeta, en milímetros: tres por renglón y seis renglones caben en una hoja carta con su orilla.</summary>
    public const decimal AnchoMm = 65m, AltoMm = 42m;

    /// <summary>La orilla, para que las tarjetas queden al centro de la hoja.</summary>
    public const decimal MargenIzquierdoMm = (215.9m - Columnas * AnchoMm) / 2, MargenSuperiorMm = (279.4m - Filas * AltoMm) / 2;

    /// <summary>Las hojas, en orden: de izquierda a derecha y de arriba abajo, sin saltarse lugares. Siempre al menos una.</summary>
    public static IReadOnlyList<PaginaDeTarjetas> Paginas(IReadOnlyList<TarjetaDePrecioEnHoja> tarjetas)
    {
        var paginas = tarjetas.Chunk(PorHoja)
            .Select((deLaHoja, i) => new PaginaDeTarjetas(i + 1, deLaHoja.Select((t, k) => new TarjetaEnLaHoja(k / Columnas, k % Columnas, t)).ToList()))
            .ToList();
        return paginas.Count == 0 ? [new PaginaDeTarjetas(1, [])] : paginas;
    }

    /// <summary>
    /// Qué tan grande sale el precio, en puntos: lo más grande que cabe a lo ancho («$1,234.50» es más largo que «$45.00»).
    /// </summary>
    public static decimal TamanoDelPrecio(string precio) => precio.Length switch
    {
        <= 6 => 34m,
        <= 8 => 28m,
        <= 10 => 23m,
        _ => 19m,
    };

    /// <summary>Las hojas como página HTML, lista para la vista previa y para imprimir.</summary>
    public static string Html(IReadOnlyList<TarjetaDePrecioEnHoja> tarjetas)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Tarjetas de precio</title><style>");
        html.Append(Estilos());
        html.Append("</style></head><body>");
        foreach (var pagina in Paginas(tarjetas))
        {
            html.Append("<div class=\"hoja\">");
            foreach (var t in pagina.Tarjetas)
            {
                var x = MargenIzquierdoMm + t.Columna * AnchoMm;
                var y = MargenSuperiorMm + t.Fila * AltoMm;
                html.Append($"<div class=\"tarjeta\" style=\"left:{HojaEtiquetas.Mm(x)};top:{HojaEtiquetas.Mm(y)}\">");
                html.Append(Contenido(t.Tarjeta));
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static string Contenido(TarjetaDePrecioEnHoja t)
    {
        var precio = HojaDeTemporada.Precio(t.PrecioCentavos);
        var html = new StringBuilder();
        html.Append($"<div class=\"nombre\">{HojaEtiquetas.Escapar.Encode(t.Nombre)}</div>");
        html.Append($"<div class=\"precio\" style=\"font-size:{TamanoDelPrecio(precio).ToString(System.Globalization.CultureInfo.InvariantCulture)}pt\">{precio}</div>");
        var barras = CodigosDeBarras.Para(t.Codigo);
        // Barras anchas, como en la hoja de códigos: el lector las lee aunque la tarjeta esté en un portatarjetas.
        var modulo = Math.Min(HojaDeCodigos.ModuloMm, Math.Floor((AnchoMm - 8m) / (barras.Modulos.Length + 2 * barras.ZonaEnBlanco) * 1000) / 1000);
        html.Append(modulo < HojaEtiquetas.ModuloMinimoMm
            ? "<div class=\"sin-lugar\">El código es demasiado largo para dibujarlo</div>"
            : CodigosDeBarras.Svg(barras, modulo, 9m));
        html.Append($"<div class=\"texto-codigo\">{HojaEtiquetas.Escapar.Encode(t.Codigo)}</div>");
        return html.ToString();
    }

    private static string Estilos() => $$"""
        @page { size: letter; margin: 0; }
        * { box-sizing: border-box; }
        html, body { margin: 0; padding: 0; background: #fff; }
        body { font-family: Arial, Helvetica, sans-serif; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        .hoja { position: relative; width: 215.9mm; height: 279.4mm; overflow: hidden; break-after: page; }
        .hoja:last-child { break-after: auto; }
        .tarjeta { position: absolute; width: {{HojaEtiquetas.Mm(AnchoMm)}}; height: {{HojaEtiquetas.Mm(AltoMm)}}; padding: 2.5mm 4mm 2mm; border: 0.2mm dashed #999;
            display: flex; flex-direction: column; align-items: center; justify-content: space-between; overflow: hidden; text-align: center; }
        .nombre { width: 100%; font-size: 9pt; line-height: 1.15; overflow: hidden; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
        .precio { font-weight: bold; line-height: 1; white-space: nowrap; }
        .barras { display: block; }
        .texto-codigo { font-family: "Courier New", monospace; font-size: 7pt; line-height: 1; letter-spacing: 0.3pt; }
        .sin-lugar { font-size: 7pt; color: #b42318; }
        @media screen { html { overflow: hidden; } .hoja { box-shadow: 0 0 0 1px #c9d2dc; margin-bottom: 8mm; } }
        """;
}
