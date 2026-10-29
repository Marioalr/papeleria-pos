namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// Lo que dice la línea de impresiones del ticket (<see cref="Venta.FijarImpresiones"/>). Con trabajos de Control Print, o en el
/// piloto con eleventa, dice cuáles son (2026-10-25): «Impresión #1234», «Impresiones #1234, #1235 y 1 de CAJA2». Así se ve en el
/// ticket y en eleventa qué trabajo de Control Print se cobró, que allá sigue «por cobrar» (el POS nunca le escribe). Si no,
/// «Impresiones», como siempre. Las copias del panel van en su propia línea, «Copias» (2026-10-29).
/// </summary>
public static class LineaDeImpresiones
{
    public const string Nombre = "Impresiones";

    /// <summary>La línea de las copias del panel de la impresora (en el piloto, COP en eleventa).</summary>
    public const string NombreCopias = "Copias";

    /// <summary>Lo más largo que se escribe (el ticket y eleventa); lo que no cabe termina en «y N más» (la caja guarda la lista completa).</summary>
    public const int Largo = 120;

    /// <param name="controlPrint">Los números de Control Print de la línea.</param>
    /// <param name="pcsDeLasDemas">La PC de cada impresión del módulo de la línea (las de la caja 2, las copias del panel).</param>
    /// <param name="conReferencias">En el piloto: la línea dice cuáles son aunque no haya de Control Print.</param>
    /// <param name="copias">La línea de las copias del panel («Copias #1240»).</param>
    public static string Descripcion(IEnumerable<long> controlPrint, IEnumerable<string> pcsDeLasDemas, bool conReferencias, bool copias = false)
    {
        var numeros = controlPrint.Distinct().Order().ToList();
        var demas = pcsDeLasDemas.Select(pc => string.IsNullOrWhiteSpace(pc) ? "esta caja" : pc.Trim()).ToList();
        var nombre = copias ? NombreCopias : Nombre;
        if (numeros.Count == 0 && (!conReferencias || demas.Count == 0))
            return nombre;
        var titulo = copias ? NombreCopias : numeros.Count + demas.Count == 1 ? "Impresión" : Nombre;
        var donde = string.Join(" y ", demas.Distinct(StringComparer.OrdinalIgnoreCase));
        var grupos = Grupos(numeros);
        // Si no cabe, se quitan números del final y se dice cuántos más (los de Control Print que no se ven y los del módulo).
        for (var van = grupos.Count; van >= 0; van--)
        {
            var visibles = grupos.Take(van).ToList();
            var faltan = grupos.Skip(van).Sum(g => g.Cuantos);
            var texto = titulo;
            if (visibles.Count > 0)
                texto += " " + string.Join(", ", visibles.Select(g => g.Texto));
            if (faltan > 0)
                texto += visibles.Count == 0 ? $" ({faltan + demas.Count})" : $" y {faltan + demas.Count} más";
            else if (demas.Count > 0)
                texto += numeros.Count > 0 ? $" y {demas.Count} de {donde}" : demas.Count == 1 ? $" de {donde}" : $" de {donde} ({demas.Count})";
            if (texto.Length <= Largo)
                return texto;
        }
        return nombre;
    }

    /// <summary>Los números seguidos, juntos: «#1234–#1240» (de tres en adelante; dos se escriben los dos).</summary>
    private static List<(string Texto, int Cuantos)> Grupos(IReadOnlyList<long> numeros)
    {
        var grupos = new List<(string, int)>();
        for (var i = 0; i < numeros.Count;)
        {
            var fin = i;
            while (fin + 1 < numeros.Count && numeros[fin + 1] == numeros[fin] + 1)
                fin++;
            var cuantos = fin - i + 1;
            if (cuantos >= 3)
                grupos.Add(($"#{numeros[i]}–#{numeros[fin]}", cuantos));
            else
                grupos.AddRange(numeros.Skip(i).Take(cuantos).Select(n => ($"#{n}", 1)));
            i = fin + 1;
        }
        return grupos;
    }
}
