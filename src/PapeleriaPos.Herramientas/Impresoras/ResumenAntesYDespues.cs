using System.Globalization;
using System.Text;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>
/// El <c>resumen.md</c> del modo «antes y después»: para leerlo en casa y decidir (docs/pruebas-en-tienda.md). Arriba, qué contestó cada
/// impresora y una tabla con lo que se movió en cada acción; abajo, por acción y por impresora, qué cambió y cuánto. Lo completo queda
/// en los archivos de cada acción.
/// </summary>
public static class ResumenAntesYDespues
{
    /// <summary>Cuántos renglones de cada clase de cambio entran en el resumen (los demás, en el archivo de cambios).</summary>
    public const int RenglonesPorClase = 25;

    public static string Escribir(DateTimeOffset inicio, IReadOnlyDictionary<string, FotoImpresora> primeras, IReadOnlyList<string> hosts,
        IReadOnlyList<AccionMedida> acciones, string? interrumpida)
    {
        var t = new StringBuilder();
        t.AppendLine(CultureInfo.InvariantCulture, $"# Antes y después en las impresoras ({inicio:yyyy-MM-dd HH:mm})");
        t.AppendLine();
        t.AppendLine("Solo se leyó (SNMP Get, GetNext y GetBulk con la comunidad «public»; IPP Get-Printer-Attributes y Get-Jobs; GET de eSCL y de "
            + "Web Config, sin contraseña): no se cambió nada en las impresoras ni se mandó nada a imprimir. Los nombres de documentos y de "
            + "quien imprimió (IPP) van enmascarados: sus 3 primeras letras, la extensión y cuántas letras tenía. Son datos de la tienda: no "
            + "van al repositorio.");
        t.AppendLine();
        t.AppendLine("## Las impresoras");
        t.AppendLine();
        t.AppendLine("| IP | Dice ser | Serie | SNMP | ST2 (color / B/N) | IPP | eSCL | Web Config |");
        t.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var host in hosts)
        {
            if (!primeras.TryGetValue(host, out var f))
            {
                t.AppendLine(CultureInfo.InvariantCulture, $"| {host} | (sin foto) | | | | | | |");
                continue;
            }
            var snmp = f.Snmp.Sum(c => c.Entradas.Count);
            var arboles = string.Join(", ", f.Snmp.Where(c => c.Entradas.Count > 0).Select(c => $"{c.Raiz} ({c.Entradas.Count}{(c.Cortada ? ", cortada" : "")})"));
            var st2 = f.St2 switch
            {
                { Error: null, Color: { } c, BlancoYNegro: { } b } => $"{N(c)} / {N(b)}",
                { Error: null } => "contestó, sin el campo 0x36",
                { Error: { } e } => $"no ({e})",
                null => "—",
            };
            var ipp = f.Ipp.Atributos is not null ? $"{f.Ipp.Url}; guarda {f.Ipp.Trabajos.Count()} trabajo(s)" : $"no ({f.Ipp.Error})";
            var escl = string.Join("; ", f.Escl.Select(e => $"{e.Nombre}: {e.Estado}"));
            var web = string.Join("; ", f.Web.Select(w => $"{w.Nombre}: {w.Estado}"));
            t.AppendLine(CultureInfo.InvariantCulture,
                $"| {host} | {Celda(f.Modelo ?? "—")} | {Celda(f.Serie ?? "—")} | {(snmp > 0 ? $"{N(snmp)} datos: {arboles}" : "no contestó")} | {Celda(st2)} | {Celda(ipp)} | {Celda(escl)} | {Celda(web)} |");
        }
        t.AppendLine();

        t.AppendLine("## Lo que se movió en cada acción");
        t.AppendLine();
        if (acciones.Count == 0)
        {
            t.AppendLine("Todavía no hay acciones.");
        }
        else
        {
            t.AppendLine("Caras: el contador que ya lee el módulo (prtMarkerLifeCount). Color y B/N: el «st» de Epson. «Otros»: cuántos datos más "
                + "cambiaron, sin los relojes. IPP: trabajos que aparecieron o cambiaron (en la foto o vigilando).");
            t.AppendLine();
            t.AppendLine("| # | Acción | Hora | " + string.Join(" | ", hosts) + " |");
            t.AppendLine("|---|---|---|" + string.Concat(hosts.Select(_ => "---|")));
            foreach (var a in acciones)
            {
                var celdas = hosts.Select(h => a.Comparaciones.FirstOrDefault(c => c.Host == h) is { } c ? Celda(Corta(c, a)) : "—");
                t.AppendLine(CultureInfo.InvariantCulture, $"| {a.Numero} | {Celda(a.Etiqueta)} | {a.Inicio:HH:mm:ss}–{a.Fin:HH:mm:ss} | {string.Join(" | ", celdas)} |");
            }
        }
        if (interrumpida is not null)
        {
            t.AppendLine();
            t.AppendLine(CultureInfo.InvariantCulture, $"La acción {interrumpida} quedó a medias (se detuvo antes de la foto de después): no se comparó.");
        }

        foreach (var a in acciones)
        {
            t.AppendLine();
            t.AppendLine(CultureInfo.InvariantCulture, $"## {a.Numero}. «{a.Etiqueta}» ({a.Inicio:HH:mm:ss} a {a.Fin:HH:mm:ss})");
            if (a.Nota is not null)
            {
                t.AppendLine();
                t.AppendLine(a.Nota);
            }
            foreach (var c in a.Comparaciones)
            {
                t.AppendLine();
                var f = primeras.GetValueOrDefault(c.Host);
                t.AppendLine(CultureInfo.InvariantCulture, $"### {c.Host}{(f?.Modelo is { } m ? $" · {m}" : "")}{(f?.Serie is { } s ? $" · serie {s}" : "")}");
                t.AppendLine();
                Impresora(t, c, a);
            }
            if (a.Vigilados.Count > 0)
            {
                t.AppendLine();
                t.AppendLine("### Vigilando IPP durante la acción");
                t.AppendLine();
                foreach (var v in a.Vigilados)
                    t.AppendLine(CultureInfo.InvariantCulture, $"- {v.Hora:HH:mm:ss} {v.Host}, trabajo {v.Id}: {v.Texto}");
            }
        }
        return t.ToString();
    }

    private static void Impresora(StringBuilder t, ComparacionImpresora c, AccionMedida a)
    {
        var contador = c.Cambios.FirstOrDefault(x => x.Clave == CambiosEntreFotos.Contador);
        var linea = contador is { } k ? $"- **Caras:** {Signo(k.Diferencia)} ({k.Antes} → {k.Despues})" : c.ConContador ? "- **Caras:** 0" : "- **Caras:** no dio su contador";
        if (c.Color is not null || c.BlancoYNegro is not null)
            linea += $"; **ST2:** color {Signo(c.Color ?? 0)}, B/N {Signo(c.BlancoYNegro ?? 0)}";
        if (c.Cambios.Count > 0 || c.SinComparar.Count == 0)
            t.AppendLine(linea + ".");

        var resto = c.Cambios.Where(x => !x.EsReloj && !x.EsDeLosContadores).ToList();
        Clase(t, "SNMP", resto.Where(x => x.Parte.StartsWith("snmp", StringComparison.Ordinal)));
        Clase(t, "ST2, campos", resto.Where(x => x.Parte == "st2"));
        Clase(t, "IPP, la impresora", resto.Where(x => x.Parte == "ipp impresora"));
        var trabajos = resto.Where(x => x.Parte == "ipp trabajos").GroupBy(x => x.Clave.Split(' ')[1]).ToList();
        if (trabajos.Count > 0)
        {
            t.AppendLine("- **IPP, trabajos que aparecieron o cambiaron:**");
            foreach (var g in trabajos)
            {
                var nuevo = g.All(x => x.Antes is null);
                t.AppendLine(($"  - trabajo {g.Key}{(nuevo ? " (nuevo)" : "")}: "
                    + string.Join(", ", g.Where(x => !x.Clave.EndsWith(" job-id", StringComparison.Ordinal)).Select(x => $"{x.Clave[(x.Clave.IndexOf(' ', 8) + 1)..]}={(nuevo ? x.Despues : $"{x.Antes ?? "—"} → {x.Despues ?? "—"}")}"))));
            }
        }
        Clase(t, "eSCL", resto.Where(x => x.Parte.StartsWith("escl", StringComparison.Ordinal)));
        Clase(t, "Web Config", resto.Where(x => x.Parte.StartsWith("web", StringComparison.Ordinal)));
        var relojes = c.Cambios.Count(x => x.EsReloj);
        if (resto.Count == 0 && contador is null && c.SinComparar.Count == 0)
            t.AppendLine(relojes > 0 ? $"- No cambió nada más (solo {relojes} reloj(es): cuánto lleva prendida, la hora)." : "- No cambió nada más.");
        else if (relojes > 0)
            t.AppendLine(CultureInfo.InvariantCulture, $"- Relojes (cambian solos): {relojes}.");
        foreach (var s in c.SinComparar)
            t.AppendLine(CultureInfo.InvariantCulture, $"- Sin comparar: {s}.");
        t.AppendLine(CultureInfo.InvariantCulture, $"- Todo, en `{a.Carpeta}/{AntesYDespues.Archivo(c.Host)}-cambios.txt`.");
    }

    /// <summary>Una clase de cambios: primero los números (los posibles contadores), luego lo demás; a lo más <see cref="RenglonesPorClase"/>.</summary>
    private static void Clase(StringBuilder t, string titulo, IEnumerable<Cambio> cambios)
    {
        var lista = cambios.OrderBy(x => x.Diferencia is null).ToList();
        if (lista.Count == 0)
            return;
        t.AppendLine(CultureInfo.InvariantCulture, $"- **{titulo}** ({lista.Count}):");
        foreach (var x in lista.Take(RenglonesPorClase))
            t.AppendLine(CultureInfo.InvariantCulture, $"  - {Celda(CambiosEntreFotos.Renglon(x))}");
        if (lista.Count > RenglonesPorClase)
            t.AppendLine(CultureInfo.InvariantCulture, $"  - y {lista.Count - RenglonesPorClase} más (en el archivo de cambios).");
    }

    private static string Corta(ComparacionImpresora c, AccionMedida a)
    {
        if (c.Cambios.Count == 0 && c.SinComparar.Count > 0)
            return "sin comparar";
        var partes = new List<string> { $"caras {Signo(c.Caras)}" };
        if (c.Color is not null || c.BlancoYNegro is not null)
            partes.Add($"color {Signo(c.Color ?? 0)}, B/N {Signo(c.BlancoYNegro ?? 0)}");
        var otros = c.Cambios.Count(x => !x.EsReloj && !x.EsDeLosContadores && x.Parte != "ipp trabajos");
        if (otros > 0)
            partes.Add($"otros {otros}");
        var ipp = c.TrabajosIpp.Union(a.Vigilados.Where(v => v.Host == c.Host).Select(v => v.Id)).Count();
        if (ipp > 0)
            partes.Add($"IPP {ipp}");
        return string.Join(" · ", partes);
    }

    private static string Signo(long? n) => n is { } v ? v.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture) : "?";

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Celda(string texto) => texto.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}
