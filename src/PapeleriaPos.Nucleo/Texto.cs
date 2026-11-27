using System.Globalization;
using System.Text;

namespace PapeleriaPos.Nucleo;

public static class Texto
{
    /// <summary>
    /// El inicio con mayúscula (el dueño, 2026-11-26: «va en todos los apartados»): el nombre de un producto, su variante y sus cajas,
    /// la marca, el departamento, el proveedor, la temporada o el exhibidor y sus lugares se guardan con la primera letra en
    /// mayúscula. Solo esa: lo demás se queda como se escribió («cuaderno Scribe» → «Cuaderno Scribe»). Lo que empieza con una cifra
    /// («100 hojas») se queda igual, y también una primera palabra escrita a propósito con mayúsculas por dentro («iPhone», «eBay»).
    /// Los signos de antes («¿», «“», «(») se saltan: «¿qué?» → «¿Qué?».
    /// </summary>
    public static string ConMayusculaInicial(string texto)
    {
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (char.IsDigit(c) || char.IsSurrogate(c))
                return texto;
            if (!char.IsLetter(c))
                continue;
            if (!char.IsLower(c))
                return texto;
            // «iPhone»: una mayúscula dentro de la primera palabra dice que se escribió así a propósito.
            for (var j = i + 1; j < texto.Length && !char.IsWhiteSpace(texto[j]); j++)
            {
                if (char.IsUpper(texto[j]))
                    return texto;
            }
            return string.Concat(texto.AsSpan(0, i), char.ToUpperInvariant(c).ToString(), texto.AsSpan(i + 1));
        }
        return texto;
    }

    /// <summary>
    /// <see cref="ConMayusculaInicial(string)"/> solo si cambió: lo que ya estaba guardado así (de antes, o de eleventa en el piloto)
    /// se queda como estaba, sin cambiarlo a escondidas ni marcarlo como cambio. Lo nuevo (<paramref name="antes"/> null) siempre.
    /// </summary>
    public static string ConMayusculaInicial(string escrito, string? antes) => escrito == antes ? escrito : ConMayusculaInicial(escrito);

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
            // Medio emoji (o un carácter que Unicode no admite) truena al normalizarlo solo, y con él todo el catálogo: va tal cual.
            if (char.IsSurrogate(c) || c >= '\uFFFE')
            {
                Agregar(c);
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
