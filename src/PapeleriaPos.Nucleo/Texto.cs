using System.Globalization;
using System.Text;

namespace PapeleriaPos.Nucleo;

public static class Texto
{
    /// <summary>
    /// Para comparar sin importar mayúsculas, acentos ni espacios de más: " Pelikán " → "PELIKAN".
    /// La ñ se conserva: en español es otra letra, no una n con acento.
    /// </summary>
    public static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        var espacioPendiente = false;
        foreach (var c in texto)
        {
            if (char.IsWhiteSpace(c))
            {
                espacioPendiente = sb.Length > 0;
                continue;
            }
            if (espacioPendiente)
            {
                sb.Append(' ');
                espacioPendiente = false;
            }
            if (c is 'ñ' or 'Ñ')
            {
                sb.Append('Ñ');
                continue;
            }
            foreach (var parte in c.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(parte) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToUpperInvariant(parte));
            }
        }
        return sb.ToString();
    }
}
