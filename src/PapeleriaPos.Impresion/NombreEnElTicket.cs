using System.Globalization;
using System.Text;

namespace PapeleriaPos.Impresion;

/// <summary>
/// El nombre de un producto como sale en el ticket con «Nombres con mayúsculas» (Configuración › Ticket; dueño, 2026-11-21): cada
/// palabra empieza con mayúscula y sigue en minúsculas, también los nombres que llegaron de eleventa todo en mayúsculas
/// («CUADERNO PROF 100H» → «Cuaderno Prof 100h»). Solo cambia lo impreso: el nombre guardado se queda como está.
/// </summary>
/// <remarks>
/// Reglas (decididas el 2026-11-21, por revisar con el dueño):
/// <list type="bullet">
/// <item>Las palabras chicas que unen («de», «del», «la», «y», «con», «para», «x» de «por»…) van en minúsculas, salvo la primera del nombre
/// o la primera dentro de un paréntesis: «Cuaderno de Dibujo», «(Caja de 12)».</item>
/// <item>Lo que empieza con número va en minúsculas: «100h», «21g», «2.5cm», «21x28»; salvo el grado de un lápiz («2B», «4H»,
/// «HB»), que es un número del 1 al 9 con H, B o F, y lo de las memorias («32GB»).</item>
/// <item>Una unidad sola después de un número va en minúsculas: «21 g», «500 ml», «100 hojas», «12 pzas»; la «x» es de las chicas
/// («21 x 28», «Nylon C10 x Metro»).</item>
/// <item>Las siglas que se conocen se quedan en mayúsculas (USB, HDMI, LED, PVC, XL…), igual que una letra con número
/// («A4», «C5»).</item>
/// <item>Lo que alguien escribió a propósito con mayúsculas y minúsculas mezcladas («iPhone», «BiC») se queda igual; y en un
/// nombre que trae minúsculas, una palabra toda en mayúsculas también («CURP impresa», «Copia B/N»).</item>
/// <item>Después de «/», «-» y «(» también va mayúscula: «Blanco/Negro», «Semi-Rígido» (eso último, por revisar).</item>
/// <item>«NO. 2» sale «No. 2»: «no» no es de las chicas.</item>
/// </list>
/// Los acentos y la ñ salen bien en mayúscula y en minúscula («ÑANDÚ» → «Ñandú»).
/// </remarks>
public static class NombreEnElTicket
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private static readonly HashSet<string> Chicas = new(StringComparer.Ordinal)
    {
        "a", "al", "con", "de", "del", "e", "el", "en", "la", "las", "lo", "los", "o", "para", "por", "sin", "u", "x", "y",
    };

    private static readonly HashSet<string> Unidades = new(StringComparer.Ordinal)
    {
        "cm", "cms", "g", "gr", "grs", "hojas", "hjs", "h", "kg", "kgs", "l", "lt", "lts", "m", "mg", "ml", "mm", "mts", "oz",
        "pz", "pza", "pzas", "pzs", "pulg",
    };

    private static readonly HashSet<string> Siglas = new(StringComparer.Ordinal)
    {
        "ABC", "CD", "CFE", "CURP", "DVD", "HB", "HD", "HDMI", "IMSS", "INE", "IVA", "LED", "NSS", "PC", "PDF", "PET", "PVC", "RFC", "SAT", "SD",
        "SEP", "UV", "USB", "XL", "XXL", "XS", "XXS", "3D",
    };

    /// <summary>El nombre con mayúscula al empezar cada palabra, con las reglas de arriba.</summary>
    public static string ConMayusculas(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            return nombre;
        // Un nombre escrito con minúsculas (no el de eleventa, todo en mayúsculas): lo que trae en mayúsculas es a propósito
        // («CURP impresa», «Copia B/N»).
        var conMinusculas = nombre.Split(' ').Where(p => !p.Any(char.IsDigit)).Select(p => new string(p.Where(char.IsLetter).ToArray()))
            .Any(p => p.Length > 0 && (p == p.ToLower(Mexico) || (p.Length > 1 && EsCapitalizada(p))));
        var palabras = nombre.Split(' ');
        var sb = new StringBuilder(nombre.Length);
        var primera = true;
        string? anterior = null;
        for (var i = 0; i < palabras.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            var palabra = palabras[i];
            if (palabra.Length == 0)
                continue;
            sb.Append(Palabra(palabra, primera, anterior, conMinusculas));
            primera = palabra.EndsWith('(');
            anterior = palabra;
        }
        return sb.ToString();
    }

    private static string Palabra(string palabra, bool primera, string? anterior, bool conMinusculas)
    {
        // Los signos de alrededor («(», «"», «,») no cuentan para decidir; se quedan como están.
        var inicio = 0;
        while (inicio < palabra.Length && !char.IsLetterOrDigit(palabra[inicio]))
            inicio++;
        if (inicio == palabra.Length)
            return palabra;
        var fin = palabra.Length;
        while (fin > inicio && !char.IsLetterOrDigit(palabra[fin - 1]))
            fin--;
        var antes = palabra[..inicio];
        var nucleo = palabra[inicio..fin];
        var despues = palabra[fin..];
        primera |= antes.Contains('(');
        return antes + Nucleo(nucleo, primera, anterior, conMinusculas) + despues;
    }

    private static string Nucleo(string nucleo, bool primera, string? anterior, bool conMinusculas)
    {
        var minusculas = nucleo.ToLower(Mexico);
        var mayusculas = nucleo.ToUpper(Mexico);
        if (conMinusculas && nucleo.Length > 1 && nucleo == mayusculas && nucleo != minusculas)
            return nucleo;
        // Escrito a propósito con mayúsculas y minúsculas mezcladas: se respeta (salvo la primera letra en minúscula de una
        // palabra común, que no es el caso de «iPhone»: ese ya trae mezcladas).
        if (nucleo != minusculas && nucleo != mayusculas && !EsCapitalizada(nucleo))
            return nucleo;
        if (Siglas.Contains(mayusculas) || EsGradoDeLapiz(mayusculas))
            return mayusculas;
        if (char.IsDigit(nucleo[0]))
            return DeMemoria(mayusculas) ? mayusculas : minusculas;
        // Una letra con número, como el tamaño del papel: «A4», «C5».
        if (nucleo.Length <= 3 && nucleo.Skip(1).All(char.IsDigit) && nucleo.Length > 1)
            return mayusculas;
        if (!primera && anterior is not null && EsNumero(anterior) && Unidades.Contains(minusculas))
            return minusculas;
        if (!primera && Chicas.Contains(minusculas))
            return minusculas;
        return Partes(minusculas);
    }

    /// <summary>«blanco/negro» → «Blanco/Negro»: mayúscula después de «/» y «-».</summary>
    private static string Partes(string minusculas)
    {
        var sb = new StringBuilder(minusculas.Length);
        var empieza = true;
        foreach (var c in minusculas)
        {
            sb.Append(empieza && char.IsLetter(c) ? char.ToUpper(c, Mexico) : c);
            empieza = c is '/' or '-' || (empieza && !char.IsLetterOrDigit(c));
        }
        return sb.ToString();
    }

    private static bool EsCapitalizada(string nucleo) =>
        char.IsUpper(nucleo[0]) && nucleo[1..] == nucleo[1..].ToLower(Mexico);

    /// <summary>«32GB», «512MB»: lo de las memorias se escribe en mayúsculas.</summary>
    private static bool DeMemoria(string mayusculas) =>
        mayusculas.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ',') is "GB" or "MB" or "TB" or "KB";

    /// <summary>«2B», «4H», «HB», «F»: del 1 al 9 con H, B o F.</summary>
    private static bool EsGradoDeLapiz(string mayusculas) =>
        mayusculas.Length == 2 && mayusculas[0] is >= '1' and <= '9' && mayusculas[1] is 'H' or 'B' or 'F';

    private static bool EsNumero(string palabra)
    {
        var limpio = palabra.Trim('(', ')', ',', '"');
        return limpio.Length > 0 && char.IsDigit(limpio[^1]) && limpio.All(c => char.IsDigit(c) || c is '.' or ',' or '/' or '½' or '¼');
    }
}
