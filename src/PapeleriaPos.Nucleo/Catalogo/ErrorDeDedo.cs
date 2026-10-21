namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Un error de dedo en la búsqueda. Si nada coincide exacto, cada pedazo de 4 letras o más puede diferir en una letra
/// (cambiada, de más, de menos o dos volteadas) del principio de una palabra o de la palabra entera: «sobre manial» da
/// los sobres manila, en lugar de «no existe» (docs/propuesta-interfaz.md).
/// </summary>
public static class ErrorDeDedo
{
    /// <summary>El pedazo es el principio de una palabra o, con 4 letras o más, se le parece con una letra de diferencia.</summary>
    public static bool Coincide(string pedazo, IEnumerable<string> palabras)
    {
        var conErrores = AdmiteErrores(pedazo);
        return palabras.Any(w => Coincide(pedazo, w, conErrores));
    }

    /// <summary>
    /// Lo mismo con una sola palabra: la búsqueda la revisa una vez por cada palabra del catálogo, no por cada producto
    /// (<see cref="IndiceDeBusqueda"/>).
    /// </summary>
    public static bool Coincide(string pedazo, string palabra) => Coincide(pedazo, palabra, AdmiteErrores(pedazo));

    /// <summary>Con menos de cuatro letras sobran los parecidos, y los números no tienen errores de dedo.</summary>
    private static bool AdmiteErrores(string pedazo) => pedazo.Length >= 4 && pedazo.Any(char.IsLetter);

    private static bool Coincide(string pedazo, string palabra, bool conErrores) =>
        palabra.StartsWith(pedazo, StringComparison.Ordinal)
        || (conErrores && (DifierenEnUna(pedazo, palabra.Length > pedazo.Length ? palabra.AsSpan(0, pedazo.Length) : palabra.AsSpan()) || DifierenEnUna(pedazo, palabra)));

    /// <summary>Si a y b difieren en a lo más una letra: cambiada, de más, de menos o dos volteadas («manial» y «manila»).</summary>
    public static bool DifierenEnUna(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        int i = 0, j = 0, diferencias = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }
            if (++diferencias > 1)
                return false;
            if (a.Length == b.Length && i + 1 < a.Length && a[i] == b[j + 1] && a[i + 1] == b[j])
            {
                i += 2;
                j += 2;
            }
            else if (a.Length > b.Length)
            {
                i++;
            }
            else if (b.Length > a.Length)
            {
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }
        return diferencias + (a.Length - i) + (b.Length - j) <= 1;
    }
}
