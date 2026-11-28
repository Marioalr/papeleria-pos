using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La hoja de un conteo del celular por capturar en eleventa (2026-11-27; docs/celular-como-caja.md, «En el piloto con eleventa: la
/// lista por capturar»): tamaño carta, un renglón por producto con diferencia, lo que había en eleventa al aceptar, la diferencia con
/// su signo, cómo debe quedar y un cuadro para palomearlo al capturarlo en eleventa › Inventario › Ajustes. Se imprime desde su marco,
/// como la hoja de códigos.
/// </summary>
public static class ListaPorCapturar
{
    private static string Numero(decimal valor) => valor.ToString("#,##0.###", CultureInfo.InvariantCulture);

    private static string ConSigno(decimal valor) => valor.ToString("+#,##0.###;-#,##0.###;0", CultureInfo.InvariantCulture);

    /// <summary>Escapa lo que rompería la página (&lt;, &amp;, comillas) y deja los acentos y la ñ como están.</summary>
    private static readonly HtmlEncoder Escapar = HtmlEncoder.Create(UnicodeRanges.All);

    private static string T(string? texto) => Escapar.Encode(texto ?? "");

    public static string Html(ConteoPorCapturarDto c)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Conteo por capturar en eleventa</title><style>");
        html.Append("""
            @page { size: letter; margin: 12mm; }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; background: #fff; }
            body { font-family: Arial, Helvetica, sans-serif; color: #000; font-size: 10pt; }
            h1 { font-size: 13pt; margin: 0 0 2mm; }
            p { margin: 0 0 2mm; }
            table { width: 100%; border-collapse: collapse; margin-top: 3mm; }
            th, td { border: 0.2mm solid #666; padding: 1.2mm 1.8mm; text-align: left; vertical-align: top; }
            th { background: #eee; font-size: 9pt; }
            tr { break-inside: avoid; }
            .num { text-align: right; white-space: nowrap; }
            .dif { font-weight: bold; font-size: 11pt; }
            .codigo { font-family: "Courier New", monospace; font-size: 8pt; color: #333; }
            .cuadro { width: 9mm; }
            .pie { margin-top: 4mm; font-size: 9pt; }
            @media screen { html { overflow: hidden; } body { padding: 12mm; width: 215.9mm; } }
            """);
        html.Append("</style></head><body>");
        html.Append("<h1>Conteo del celular por capturar en eleventa</h1>");
        html.Append($"<p>{T(c.De)}{(c.Conto is null ? "" : $" · contó {T(c.Conto)}")} · contado el {c.ContadoLocal:dd/MM/yyyy} a las {c.ContadoLocal:HH:mm}"
            + $" · aceptado el {c.AceptadoLocal:dd/MM/yyyy} a las {c.AceptadoLocal:HH:mm}{(c.Acepto is null ? "" : $" por {T(c.Acepto)}")}</p>");
        html.Append("<p>En eleventa › Inventario › Ajustes, producto por producto. Solo va lo que tiene diferencia"
            + (c.Cuadran == 0 ? "." : c.Cuadran == 1 ? " (1 producto cuadró)." : $" ({c.Cuadran} productos cuadraron).") + "</p>");
        html.Append("<table><thead><tr><th>Producto</th><th class=\"num\">Había</th><th class=\"num\">Diferencia</th><th class=\"num\">Debe quedar</th>"
            + "<th class=\"cuadro\">✓</th></tr></thead><tbody>");
        foreach (var r in c.Renglones)
            html.Append($"<tr><td>{T(r.Descripcion)}{(r.Codigo is null ? "" : $"<div class=\"codigo\">{T(r.Codigo)}</div>")}</td>"
                + $"<td class=\"num\">{Numero(r.Habia)}</td><td class=\"num dif\">{ConSigno(r.Diferencia)}</td><td class=\"num\">{Numero(r.DebeQuedar)}</td>"
                + "<td class=\"cuadro\"></td></tr>");
        html.Append("</tbody></table>");
        html.Append("<p class=\"pie\">«Había» es lo que decía eleventa al aceptar la lista. Si eleventa ya enseña otra existencia (se vendió mientras tanto), "
            + "a la que enseñe se le suma la diferencia. Al terminar, en el POS: Inventario (F4) › Conteos del celular › «Ya lo capturé en eleventa».</p>");
        html.Append("</body></html>");
        return html.ToString();
    }
}
