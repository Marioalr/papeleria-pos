using System.Globalization;
using System.Text;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>Un dato que cambió entre dos fotos (vacío de un lado: apareció o desapareció).</summary>
public sealed record Cambio(string Parte, string Clave, string? Antes, string? Despues)
{
    /// <summary>Cuánto cambió, si los dos son números.</summary>
    public long? Diferencia =>
        long.TryParse(Antes, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a)
        && long.TryParse(Despues, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d - a : null;

    /// <summary>Lo que ya resume la primera línea: el contador de caras y el campo 0x36 del «st» (color y B/N).</summary>
    public bool EsDeLosContadores => Clave == CambiosEntreFotos.Contador
        || (Parte == "st2" && Clave is "color (campo 0x36)" or "B/N (campo 0x36)" or "campo 0x36");

    /// <summary>Cambia solo con el tiempo (cuánto lleva prendida, la hora): no dice nada de la acción.</summary>
    public bool EsReloj => Clave is "1.3.6.1.2.1.1.3.0" or "printer-up-time" or "printer-current-time" or "printer-state-change-time"
            or "printer-state-change-date-time" or "printer-config-change-time" or "printer-config-change-date-time"
        || Clave.StartsWith("1.3.6.1.2.1.43.18.1.1.9.", StringComparison.Ordinal) // prtAlertTime
        || Clave.EndsWith("/Age", StringComparison.Ordinal) || Clave.EndsWith(" job-printer-up-time", StringComparison.Ordinal);
}

/// <summary>Lo que cambió en una impresora entre la foto de antes y la de después.</summary>
/// <param name="ConContador">Las dos fotos traen el contador de caras: sin cambio es 0, no «no se sabe».</param>
/// <param name="ConSt2">Las dos fotos traen el color y el B/N del «st».</param>
public sealed record ComparacionImpresora(string Host, DateTimeOffset Antes, DateTimeOffset Despues, IReadOnlyList<Cambio> Cambios,
    IReadOnlyList<string> SinComparar, bool ConContador = false, bool ConSt2 = false)
{
    public long? Caras => Cambios.FirstOrDefault(c => c.Clave == CambiosEntreFotos.Contador)?.Diferencia ?? (ConContador ? 0 : null);
    public long? Color => Cambios.FirstOrDefault(c => c.Parte == "st2" && c.Clave == "color (campo 0x36)")?.Diferencia ?? (ConSt2 ? 0 : null);
    public long? BlancoYNegro => Cambios.FirstOrDefault(c => c.Parte == "st2" && c.Clave == "B/N (campo 0x36)")?.Diferencia ?? (ConSt2 ? 0 : null);

    /// <summary>Los trabajos de IPP que aparecieron o cambiaron (por su número).</summary>
    public IReadOnlyList<string> TrabajosIpp => Cambios.Where(c => c.Parte == "ipp trabajos")
        .Select(c => c.Clave.Split(' ')[1]).Distinct().ToList();
}

/// <summary>Compara dos fotos de una impresora: solo lo que cambió.</summary>
public static class CambiosEntreFotos
{
    /// <summary>prtMarkerLifeCount: el contador de caras que ya lee el módulo.</summary>
    public const string Contador = "1.3.6.1.2.1.43.10.2.1.4.1.1";

    public static ComparacionImpresora Comparar(FotoImpresora antes, FotoImpresora despues)
    {
        var cambios = new List<Cambio>();
        var sinComparar = new List<string>();
        var partesDespues = despues.Partes().ToDictionary(p => p.Nombre);
        var comparadas = 0;
        foreach (var a in antes.Partes())
        {
            if (!partesDespues.Remove(a.Nombre, out var d))
                continue;
            if (a.Error is not null || d.Error is not null)
            {
                // Si no contestó en las dos, no hay nada que decir; si en una sí y en otra no, se dice (pero no se lista como cambio).
                if ((a.Error is null) != (d.Error is null))
                    sinComparar.Add($"{a.Nombre}: {(a.Error is { } ea ? $"antes no contestó ({ea})" : $"después no contestó ({d.Error})")}");
                continue;
            }
            comparadas++;
            // Una caminata cortada por el tope: solo se compara hasta donde llegaron las dos.
            var hasta = new[] { a.CortadaEn, d.CortadaEn }.Where(o => o is not null).OrderBy(o => o, Comparer<string?>.Create((x, y) => CaminataSnmp.Comparar(x!, y!)))
                .FirstOrDefault();
            if (hasta is not null)
                sinComparar.Add($"{a.Nombre}: se cortó por el tope; se comparó hasta {hasta}");
            bool Cuenta(string clave) => hasta is null || CaminataSnmp.Comparar(clave, hasta) <= 0;
            foreach (var (clave, valor) in a.Valores)
            {
                if (hasta is not null && !Cuenta(clave))
                    continue;
                if (!d.Valores.TryGetValue(clave, out var nuevo))
                    cambios.Add(new Cambio(a.Nombre, clave, valor, null));
                else if (nuevo != valor)
                    cambios.Add(new Cambio(a.Nombre, clave, valor, nuevo));
            }
            foreach (var (clave, valor) in d.Valores)
            {
                if (!a.Valores.ContainsKey(clave) && (hasta is null || Cuenta(clave)))
                    cambios.Add(new Cambio(a.Nombre, clave, null, valor));
            }
        }
        if (comparadas == 0)
            sinComparar.Add("no contestó nada en ninguna de las dos fotos (ni SNMP ni por la red)");
        var conContador = antes.Dato(Contador) is not null && despues.Dato(Contador) is not null;
        var conSt2 = antes.St2 is { Color: not null, BlancoYNegro: not null } && despues.St2 is { Color: not null, BlancoYNegro: not null };
        return new ComparacionImpresora(antes.Host, antes.Inicio, despues.Fin, cambios, sinComparar, conContador, conSt2);
    }

    /// <summary>Qué es un OID conocido (para leer el resumen sin buscarlo).</summary>
    public static string? QueEs(string oid)
    {
        foreach (var (prefijo, que) in Conocidos)
        {
            if (CaminataSnmp.Dentro(oid, prefijo))
                return que + (oid.Length > prefijo.Length ? $" [{oid[(prefijo.Length + 1)..]}]" : "");
        }
        return null;
    }

    private static readonly (string Prefijo, string Que)[] Conocidos =
    [
        ("1.3.6.1.2.1.1.3", "sysUpTime (cuánto lleva prendida)"),
        ("1.3.6.1.2.1.43.10.2.1.4", "prtMarkerLifeCount (contador de caras)"),
        ("1.3.6.1.2.1.43.10.2.1.5", "prtMarkerPowerOnCount (caras desde que se prendió)"),
        ("1.3.6.1.2.1.43.11.1.1.9", "prtMarkerSuppliesLevel (nivel de suministro)"),
        ("1.3.6.1.2.1.43.11.1.1.6", "prtMarkerSuppliesDescription"),
        ("1.3.6.1.2.1.43.8.2.1.10", "prtInputCurrentLevel (papel en la bandeja)"),
        ("1.3.6.1.2.1.43.8.2.1.11", "prtInputStatus (bandeja)"),
        ("1.3.6.1.2.1.43.9.2.1", "prtOutput (bandeja de salida)"),
        ("1.3.6.1.2.1.43.16", "prtConsoleDisplayBuffer (lo que dice la pantalla)"),
        ("1.3.6.1.2.1.43.18.1.1", "prtAlert (alertas)"),
        ("1.3.6.1.2.1.43.5.1.1", "prtGeneral"),
        ("1.3.6.1.2.1.25.3.2.1.5", "hrDeviceStatus"),
        ("1.3.6.1.2.1.25.3.5.1.1", "hrPrinterStatus (3 lista, 4 imprimiendo, 5 calentando)"),
        ("1.3.6.1.2.1.25.3.5.1.2", "hrPrinterDetectedErrorState"),
        ("1.3.6.1.2.1.25.3.2.1.6", "hrDeviceErrors"),
        ("1.3.6.1.4.1.2699", "PWG"),
        ("1.3.6.1.4.1.1248", "privado de Epson"),
    ];

    /// <summary>Un renglón del resumen para un cambio: «qué es: antes → después (+N)».</summary>
    public static string Renglon(Cambio c)
    {
        var que = c.Parte.StartsWith("snmp", StringComparison.Ordinal) && QueEs(c.Clave) is { } q ? $"{c.Clave} · {q}" : c.Clave;
        var diferencia = c.Diferencia is { } n ? $" (**{(n > 0 ? "+" : "")}{n.ToString("N0", CultureInfo.InvariantCulture)}**)" : "";
        return $"{que}: {Corto(c.Antes) ?? "(no estaba)"} → {Corto(c.Despues) ?? "(ya no está)"}{diferencia}";
    }

    private static string? Corto(string? valor) => valor switch
    {
        null => null,
        "" => "(vacío)",
        { Length: > 80 } => valor[..77] + "…",
        _ => valor,
    };

    /// <summary>Todos los cambios, uno por renglón, para el archivo de cada impresora.</summary>
    public static string Completo(ComparacionImpresora c)
    {
        var texto = new StringBuilder();
        texto.AppendLine(CultureInfo.InvariantCulture, $"Cambios en {c.Host} entre {c.Antes:HH:mm:ss} y {c.Despues:HH:mm:ss}");
        foreach (var s in c.SinComparar)
            texto.AppendLine(CultureInfo.InvariantCulture, $"(sin comparar) {s}");
        foreach (var grupo in c.Cambios.GroupBy(x => x.Parte))
        {
            texto.AppendLine();
            texto.AppendLine(CultureInfo.InvariantCulture, $"[{grupo.Key}]");
            foreach (var cambio in grupo)
                texto.AppendLine(($"{cambio.Clave}\t{cambio.Antes ?? "(no estaba)"}\t→\t{cambio.Despues ?? "(ya no está)"}"
                    + (cambio.Diferencia is { } n ? $"\t({n:+0;-0;0})" : "")));
        }
        if (c.Cambios.Count == 0)
            texto.AppendLine("No cambió nada.");
        return texto.ToString();
    }
}
