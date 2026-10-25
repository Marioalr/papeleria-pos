using System.Globalization;
using System.Text;

namespace PapeleriaPos.Nucleo;

public static class Texto
{
    /// <summary>
    /// Para comparar sin importar mayúsculas, acentos ni espacios de más: " Pelikán " → "PELIKAN". También sin los acentos
    /// sueltos que a veces quedan en un nombre («¨», «´», «`», «^»: 2026-10-24, en tres nombres de la tienda), que si no la
    /// búsqueda sin acentos no los encuentra. La ñ se conserva: en español es otra letra, no una n con acento.
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
            if (c is 'ñ' or 'Ñ')
            {
                Agregar('Ñ');
                continue;
            }
            foreach (var parte in c.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(parte) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.ModifierSymbol))
                    Agregar(char.ToUpperInvariant(parte));
            }
        }
        return sb.ToString();

        // El espacio que quedó pendiente va antes de la letra que sigue: un acento suelto entre dos espacios no deja dos.
        void Agregar(char letra)
        {
            if (espacioPendiente)
            {
                sb.Append(' ');
                espacioPendiente = false;
            }
            sb.Append(letra);
        }
    }
}
