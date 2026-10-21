using System.Globalization;
using System.Text;

namespace PapeleriaPos.Impresion;

/// <summary>Un producto en la hoja de la temporada: su foto (JPEG; vacía, sus iniciales), su nombre, su precio y su código.</summary>
public sealed record ProductoDeHoja(string Nombre, string Iniciales, long? PrecioCentavos, string Codigo, byte[]? Foto = null);

/// <summary>Un lugar de la mesa de la temporada con sus productos, en su orden.</summary>
public sealed record LugarDeHoja(string Nombre, IReadOnlyList<ProductoDeHoja> Productos);

/// <summary>Lo que lleva la hoja: la temporada, sus fechas, la sucursal, el día en que se imprime y sus lugares.</summary>
public sealed record DatosDeHojaDeTemporada(string Temporada, string? Fechas, string Sucursal, DateOnly Fecha, IReadOnlyList<LugarDeHoja> Lugares);

/// <summary>Un renglón de una página de la hoja: el nombre de un lugar, o hasta <see cref="HojaDeTemporada.Columnas"/> productos.</summary>
public abstract record FilaDeHoja;

/// <summary>El nombre de un lugar; <paramref name="Sigue"/> si sus productos empezaron en la página anterior.</summary>
public sealed record LugarEnLaHoja(string Lugar, bool Sigue) : FilaDeHoja;

public sealed record ProductosEnLaHoja(IReadOnlyList<ProductoDeHoja> Productos) : FilaDeHoja;

/// <summary>Una página de la hoja, con sus renglones de arriba abajo.</summary>
public sealed record PaginaDeHoja(int Numero, IReadOnlyList<FilaDeHoja> Filas);

/// <summary>
/// La hoja de la temporada (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala», punto 3): la foto, el nombre,
/// el precio y el código de cada producto, en el orden de la mesa, lugar por lugar. Reemplaza la lista que hoy se escribe a
/// mano y se guarda con la mercancía: con la foto ya no hay que adivinar qué producto es cada nombre, y el código se escanea
/// desde la hoja. Es una página HTML para la impresora de inyección o láser, como la hoja de códigos: todo se mide en
/// milímetros y cada hoja carta se arma aquí (<see cref="Paginas"/>), así sale igual en cualquier impresora al 100 %.
/// </summary>
public static class HojaDeTemporada
{
    /// <summary>Productos por renglón.</summary>
    public const int Columnas = 3;

    /// <summary>Lo que mide cada cosa, en milímetros: la orilla, el título de cada hoja, el nombre de un lugar y un renglón de productos.</summary>
    public const decimal MargenMm = 10m, TituloMm = 16m, LugarMm = 9m, RenglonMm = 40m;

    /// <summary>Lo que queda para los renglones en cada hoja carta.</summary>
    public const decimal DisponibleMm = 279.4m - 2 * MargenMm - TituloMm;

    /// <summary>Lo que mide la foto (o sus iniciales) en la hoja.</summary>
    public const decimal FotoMm = 22m;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>
    /// Las hojas, en orden: cada lugar empieza con su nombre y sigue con sus productos de tres en tres. El nombre de un lugar
    /// nunca se queda solo al pie de una hoja: si su primer renglón no cabe, los dos pasan a la siguiente; si un lugar sigue en
    /// otra hoja, ahí se repite su nombre («sigue»). Los lugares sin productos no salen. Siempre hay al menos una hoja.
    /// </summary>
    public static IReadOnlyList<PaginaDeHoja> Paginas(IReadOnlyList<LugarDeHoja> lugares)
    {
        var paginas = new List<PaginaDeHoja>();
        var filas = new List<FilaDeHoja>();
        var usado = 0m;
        foreach (var lugar in lugares)
        {
            var renglones = lugar.Productos.Chunk(Columnas).ToList();
            for (var i = 0; i < renglones.Count; i++)
            {
                var conNombre = i == 0 || filas.Count == 0;
                if (usado + (conNombre ? LugarMm : 0) + RenglonMm > DisponibleMm && filas.Count > 0)
                {
                    paginas.Add(new PaginaDeHoja(paginas.Count + 1, filas));
                    (filas, usado, conNombre) = ([], 0, true);
                }
                if (conNombre)
                {
                    filas.Add(new LugarEnLaHoja(lugar.Nombre, Sigue: i > 0));
                    usado += LugarMm;
                }
                filas.Add(new ProductosEnLaHoja(renglones[i]));
                usado += RenglonMm;
            }
        }
        paginas.Add(new PaginaDeHoja(paginas.Count + 1, filas));
        return paginas;
    }

    /// <summary>La hoja como página HTML, lista para la vista previa y para imprimir.</summary>
    public static string Html(DatosDeHojaDeTemporada datos)
    {
        var paginas = Paginas(datos.Lugares);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>");
        html.Append(HojaEtiquetas.Escapar.Encode($"Hoja de la temporada {datos.Temporada}"));
        html.Append("</title><style>");
        html.Append(Estilos());
        html.Append("</style></head><body>");
        foreach (var pagina in paginas)
        {
            html.Append("<div class=\"hoja\"><div class=\"titulo\"><div>");
            html.Append($"<h1>{HojaEtiquetas.Escapar.Encode(datos.Temporada)}</h1>");
            html.Append($"<p>{HojaEtiquetas.Escapar.Encode(Subtitulo(datos))}</p></div>");
            html.Append($"<span class=\"pagina\">Hoja {pagina.Numero} de {paginas.Count}</span></div>");
            foreach (var fila in pagina.Filas)
            {
                switch (fila)
                {
                    case LugarEnLaHoja lugar:
                        html.Append($"<div class=\"lugar\">{HojaEtiquetas.Escapar.Encode(lugar.Lugar)}{(lugar.Sigue ? " <small>(sigue)</small>" : "")}</div>");
                        break;
                    case ProductosEnLaHoja productos:
                        html.Append("<div class=\"renglon\">");
                        foreach (var p in productos.Productos)
                            html.Append(Producto(p));
                        html.Append("</div>");
                        break;
                }
            }
            html.Append("</div>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    /// <summary>«Navidad · del 20 de noviembre al 1 de enero · Matriz · impresa el 17/10/2026».</summary>
    public static string Subtitulo(DatosDeHojaDeTemporada datos) =>
        string.Join(" · ", new[] { datos.Fechas, datos.Sucursal, $"impresa el {datos.Fecha.ToString("d/M/yyyy", CultureInfo.InvariantCulture)}" }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>El precio como sale en la hoja y en las tarjetas: «$1,234.50»; sin precio, «sin precio».</summary>
    public static string Precio(long? centavos) => centavos is { } c ? (c / 100m).ToString("$#,##0.00", Mexico) : "sin precio";

    private static string Producto(ProductoDeHoja p)
    {
        var html = new StringBuilder("<div class=\"producto\"><div class=\"arriba\">");
        html.Append(p.Foto is { Length: > 0 } foto
            ? $"<img class=\"foto\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(foto)}\" alt=\"\">"
            : $"<div class=\"foto iniciales\">{HojaEtiquetas.Escapar.Encode(p.Iniciales)}</div>");
        html.Append($"<div class=\"texto\"><div class=\"nombre\">{HojaEtiquetas.Escapar.Encode(p.Nombre)}</div>");
        html.Append($"<div class=\"precio{(p.PrecioCentavos is null ? " sin" : "")}\">{Precio(p.PrecioCentavos)}</div></div></div>");
        var barras = CodigosDeBarras.Para(p.Codigo);
        // La celda mide unos 62 mm: un código largo se adelgaza hasta donde el lector todavía lo lee.
        var modulo = Math.Min(0.3m, Math.Floor(56m / (barras.Modulos.Length + 2 * barras.ZonaEnBlanco) * 1000) / 1000);
        html.Append(modulo < HojaEtiquetas.ModuloMinimoMm
            ? $"<div class=\"sin-lugar\">El código es demasiado largo para dibujarlo</div>"
            : CodigosDeBarras.Svg(barras, modulo, 7m));
        html.Append($"<div class=\"texto-codigo\">{HojaEtiquetas.Escapar.Encode(p.Codigo)}</div></div>");
        return html.ToString();
    }

    private static string Estilos() => $$"""
        @page { size: letter; margin: 0; }
        * { box-sizing: border-box; }
        html, body { margin: 0; padding: 0; background: #fff; }
        body { font-family: Arial, Helvetica, sans-serif; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        .hoja { position: relative; width: 215.9mm; height: 279.4mm; padding: {{Mm(MargenMm)}}; overflow: hidden; break-after: page; }
        .hoja:last-child { break-after: auto; }
        .titulo { height: {{Mm(TituloMm)}}; display: flex; align-items: flex-start; justify-content: space-between; gap: 4mm; border-bottom: 0.4mm solid #000; }
        .titulo h1 { margin: 0; font-size: 16pt; }
        .titulo p { margin: 1mm 0 0; font-size: 9pt; color: #333; }
        .pagina { font-size: 9pt; color: #333; white-space: nowrap; }
        .lugar { height: {{Mm(LugarMm)}}; padding-top: 3mm; font-size: 10.5pt; font-weight: bold; text-transform: uppercase; letter-spacing: 0.3pt; }
        .lugar small { font-weight: normal; text-transform: none; }
        .renglon { height: {{Mm(RenglonMm)}}; display: grid; grid-template-columns: repeat({{Columnas}}, 1fr); gap: 0 3mm; padding: 1.5mm 0; }
        .producto { border: 0.2mm solid #999; border-radius: 1.5mm; padding: 1.5mm 2mm; display: flex; flex-direction: column; gap: 1mm; overflow: hidden; }
        .arriba { display: flex; gap: 2mm; align-items: flex-start; }
        .foto { width: {{Mm(FotoMm)}}; height: {{Mm(FotoMm)}}; flex: none; object-fit: contain; border-radius: 1mm; }
        .iniciales { display: flex; align-items: center; justify-content: center; background: #f1ede4; color: #6b5d43; font-size: 18pt; font-weight: bold; }
        .texto { min-width: 0; display: flex; flex-direction: column; gap: 1mm; }
        .nombre { font-size: 8.5pt; line-height: 1.15; overflow: hidden; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 4; }
        .precio { font-size: 15pt; font-weight: bold; }
        .precio.sin { font-size: 9pt; font-weight: normal; color: #b42318; }
        .barras { display: block; align-self: center; }
        .texto-codigo { align-self: center; font-family: "Courier New", monospace; font-size: 7pt; line-height: 1; }
        .sin-lugar { font-size: 7pt; color: #b42318; }
        @media screen { html { overflow: hidden; } .hoja { box-shadow: 0 0 0 1px #c9d2dc; margin-bottom: 8mm; } }
        """;

    private static string Mm(decimal valor) => HojaEtiquetas.Mm(valor);
}
