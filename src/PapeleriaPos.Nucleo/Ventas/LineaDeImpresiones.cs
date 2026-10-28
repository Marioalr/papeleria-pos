namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// Lo que dice la línea de impresiones del ticket (<see cref="Venta.FijarImpresiones"/>). Con trabajos de Control Print, o en el
/// piloto con eleventa, dice cuáles son (2026-10-25): «Impresión #1234», «Impresiones #1234, #1235 y 1 de CAJA2». Así se ve en el
/// ticket y en eleventa qué trabajo de Control Print se cobró, que allá sigue «por cobrar» (el POS nunca le escribe). Si no,
/// «Impresiones», como siempre.
/// </summary>
public static class LineaDeImpresiones
{
    public const string Nombre = "Impresiones";

    /// <summary>Lo más largo que se escribe (el ticket y eleventa); lo de más se corta con «…».</summary>
    public const int Largo = 120;

    /// <param name="controlPrint">Los números de Control Print de la línea.</param>
    /// <param name="pcsDeLasDemas">La PC de cada impresión del módulo de la línea (las de la caja 2, las copias del panel).</param>
    /// <param name="conReferencias">En el piloto: la línea dice cuáles son aunque no haya de Control Print.</param>
    public static string Descripcion(IEnumerable<long> controlPrint, IEnumerable<string> pcsDeLasDemas, bool conReferencias)
    {
        var numeros = controlPrint.Distinct().Order().Select(id => $"#{id}").ToList();
        var demas = pcsDeLasDemas.Select(pc => string.IsNullOrWhiteSpace(pc) ? "esta caja" : pc.Trim()).ToList();
        if (numeros.Count == 0 && (!conReferencias || demas.Count == 0))
            return Nombre;
        var texto = numeros.Count + demas.Count == 1 ? "Impresión" : "Impresiones";
        if (numeros.Count > 0)
            texto += " " + string.Join(", ", numeros);
        if (demas.Count > 0)
        {
            var donde = string.Join(" y ", demas.Distinct(StringComparer.OrdinalIgnoreCase));
            texto += numeros.Count > 0 ? $" y {demas.Count} de {donde}" : demas.Count == 1 ? $" de {donde}" : $" de {donde} ({demas.Count})";
        }
        return texto.Length <= Largo ? texto : texto[..(Largo - 1)].TrimEnd(' ', ',') + "…";
    }
}
