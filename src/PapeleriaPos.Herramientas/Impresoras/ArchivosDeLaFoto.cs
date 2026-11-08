using System.Globalization;
using System.Text;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>
/// Guarda una foto tal cual se leyó, en una carpeta por impresora y momento: una caminata SNMP por archivo (como snmpwalk -On), el
/// «st», lo de IPP (con los nombres ya enmascarados), lo de eSCL y las páginas de Web Config con sus números.
/// </summary>
public static class ArchivosDeLaFoto
{
    public static async Task GuardarAsync(FotoImpresora f, string dir, IReadOnlyList<(string Raiz, string Nombre)> arboles)
    {
        Directory.CreateDirectory(dir);
        var foto = new StringBuilder();
        foto.AppendLine(CultureInfo.InvariantCulture, $"Impresora: {f.Host}");
        foto.AppendLine(CultureInfo.InvariantCulture, $"Foto: {f.Inicio:yyyy-MM-dd HH:mm:ss} a {f.Fin:HH:mm:ss}");
        foto.AppendLine(CultureInfo.InvariantCulture, $"Dice ser: {f.Modelo ?? "—"}; serie: {f.Serie ?? "—"}");

        foreach (var c in f.Snmp)
        {
            var nombre = arboles.Where(a => a.Raiz == c.Raiz).Select(a => a.Nombre).FirstOrDefault() ?? c.Raiz;
            var texto = new StringBuilder();
            texto.AppendLine(($"# {c.Raiz} ({nombre}): {c.Entradas.Count} datos, {c.Version ?? "sin versión"}"
                + (c.Cortada ? ", CORTADA por el tope" : "") + (c.Error is { } err ? $", error: {err}" : "")));
            foreach (var e in c.Entradas)
                texto.AppendLine(CultureInfo.InvariantCulture, $".{e.Oid} = {e.NombreDelTipo}: {e.Texto}");
            await File.WriteAllTextAsync(Path.Combine(dir, $"snmp-{nombre}.txt"), texto.ToString());
            foto.AppendLine(($"SNMP {c.Raiz}: {c.Entradas.Count} datos" + (c.Cortada ? " (cortada)" : "") + (c.Error is { } er ? $" ({er})" : "")));
        }

        if (f.St2 is { } st2)
        {
            var texto = new StringBuilder();
            if (st2.Error is { } e)
                texto.AppendLine(CultureInfo.InvariantCulture, $"No: {e}");
            texto.AppendLine(CultureInfo.InvariantCulture, $"Serie: {st2.Serie ?? "—"}");
            texto.AppendLine(CultureInfo.InvariantCulture, $"Color (campo 0x36): {st2.Color?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
            texto.AppendLine(CultureInfo.InvariantCulture, $"B/N (campo 0x36): {st2.BlancoYNegro?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
            foreach (var (codigo, datos) in st2.Campos.OrderBy(x => x.Key))
                texto.AppendLine(CultureInfo.InvariantCulture, $"Campo 0x{codigo:X2} ({datos.Length} bytes): {Convert.ToHexString(datos)}");
            await File.WriteAllTextAsync(Path.Combine(dir, "st2.txt"), texto.ToString());
            foto.AppendLine(CultureInfo.InvariantCulture, $"ST2: {st2.Error ?? "sí"}");
        }

        var ipp = new StringBuilder();
        ipp.AppendLine(($"# IPP {f.Ipp.Url ?? "(ninguna dirección contestó)"}" + (f.Ipp.Error is { } ie ? $" · {ie}" : "")));
        if (f.Ipp.Atributos is { } atributos)
        {
            ipp.AppendLine(CultureInfo.InvariantCulture, $"Get-Printer-Attributes: {atributos.EstadoTexto}");
            foreach (var g in atributos.Grupos)
                foreach (var a in g.Atributos)
                    ipp.AppendLine(CultureInfo.InvariantCulture, $"[{g.Etiqueta}] {a.Nombre} = {a.Texto}");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "ipp-atributos.txt"), ipp.ToString());
        var trabajos = new StringBuilder();
        trabajos.AppendLine("# Get-Jobs (my-jobs=false). Los nombres de documentos y de quien imprimió van enmascarados: 3 letras, la extensión y cuántas tenía.");
        foreach (var (cuales, r) in new[] { ("completed", f.Ipp.Terminados), ("not-completed", f.Ipp.Pendientes) })
        {
            trabajos.AppendLine(CultureInfo.InvariantCulture, $"## which-jobs={cuales}: {r?.EstadoTexto ?? "no contestó"}");
            foreach (var t in r?.Trabajos ?? [])
            {
                trabajos.AppendLine(CultureInfo.InvariantCulture, $"- trabajo {t["job-id"]}");
                foreach (var a in t.Atributos)
                    trabajos.AppendLine(CultureInfo.InvariantCulture, $"    {a.Nombre} = {a.Texto}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "ipp-trabajos.txt"), trabajos.ToString());
        foto.AppendLine(CultureInfo.InvariantCulture, $"IPP: {(f.Ipp.Atributos is not null ? f.Ipp.Url : $"no ({f.Ipp.Error})")}");

        foreach (var l in f.Escl)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"escl-{l.Nombre}.{(l.Codigo == 200 ? "xml" : "txt")}"),
                l.Codigo == 200 ? l.Cuerpo : $"{l.Url}: {l.Estado}\n{l.Cuerpo}");
            foto.AppendLine(CultureInfo.InvariantCulture, $"eSCL {l.Nombre}: {l.Estado} ({l.Url})");
        }

        var numeros = new StringBuilder();
        foreach (var l in f.Web)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"web-{l.Nombre}.{(l.Cuerpo is null ? "txt" : "html")}"), l.Cuerpo ?? $"{l.Url}: {l.Estado}");
            foto.AppendLine(CultureInfo.InvariantCulture, $"Web {l.Nombre}: {l.Estado} ({l.Url})");
            numeros.AppendLine(CultureInfo.InvariantCulture, $"# {l.Nombre} ({l.Url}): {l.Estado}");
            if (l is { Codigo: 200, Cuerpo: { } html })
                foreach (var (clave, valor) in FotoImpresora.NumerosDeLaPagina(html))
                    numeros.AppendLine(CultureInfo.InvariantCulture, $"{clave}\t{valor}");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "web-numeros.txt"), numeros.ToString());
        await File.WriteAllTextAsync(Path.Combine(dir, "foto.txt"), foto.ToString());
    }
}
