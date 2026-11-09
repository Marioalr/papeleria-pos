namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Por qué variante de lo escaneado coincidió un código del catálogo, en el orden en que se prueba.</summary>
public enum VarianteDeCodigo
{
    /// <summary>El código tal cual.</summary>
    TalCual,

    /// <summary>Un UPC-A de 12 que empieza con 0, sin ese 0 (11 dígitos).</summary>
    UpcA11,

    /// <summary>Un EAN-13 que empieza con 0, como UPC-A de 12 (sin ese 0).</summary>
    Ean13AUpcA,

    /// <summary>Un EAN-13 que empieza con 00, como UPC-A de 11 (sin los dos ceros).</summary>
    Ean13AUpcA11,

    /// <summary>Un UPC-E (8 dígitos) expandido a su UPC-A de 12.</summary>
    UpcEAUpcA,
}

/// <summary>
/// La regla para encontrar un código de fábrica escrito de otra forma en el catálogo (decidida el 2026-10-11, la misma de la
/// app del celular, <c>nucleo/Catalogo.kt</c>): primero el código tal cual y, si no está, en este orden y nada más: el UPC-A
/// de 12 que empieza con 0 sin ese 0 (11); el EAN-13 que empieza con 0 como UPC-A de 12 y luego, si ese empieza con 0, de 11;
/// el UPC-E expandido a su UPC-A de 12. No se adivinan ni se completan dígitos, no se le quitan ceros a otros códigos y solo
/// se prueban variantes de un código con su dígito verificador bien: un código del catálogo con el verificador mal solo se
/// encuentra tal cual.
/// <para>
/// El lector de la caja escribe el código como texto, sin decir de qué simbología es (la app sí lo sabe): aquí se deduce del
/// largo. 12 dígitos es un UPC-A; 13, un EAN-13; 8, un UPC-E si se expande a un UPC-A válido. Un EAN-8 de 8 dígitos que
/// también es un UPC-E válido se prueba además expandido: es la única diferencia con la app, y solo se nota si el catálogo
/// no tiene el EAN-8 y sí tiene ese UPC-A.
/// </para>
/// </summary>
public static class VariantesDeCodigo
{
    /// <summary>
    /// Las formas en que se busca <paramref name="codigo"/> (ya sin espacios en las orillas) después de tal cual, en orden;
    /// ninguna si no es un UPC-A, EAN-13 o UPC-E válido.
    /// </summary>
    public static IEnumerable<(string Clave, VarianteDeCodigo Variante)> De(string codigo)
    {
        switch (codigo.Length)
        {
            case 12 when codigo[0] == '0' && Gtin.EsValido(codigo):
                yield return (codigo[1..], VarianteDeCodigo.UpcA11);
                break;
            case 13 when codigo[0] == '0' && Gtin.EsValido(codigo):
                yield return (codigo[1..], VarianteDeCodigo.Ean13AUpcA);
                if (codigo[1] == '0')
                    yield return (codigo[2..], VarianteDeCodigo.Ean13AUpcA11);
                break;
            case 8 when Gtin.UpcEAUpcA(codigo) is { } upcA:
                yield return (upcA, VarianteDeCodigo.UpcEAUpcA);
                break;
        }
    }
}

/// <summary>Los códigos de fábrica (GTIN de 8, 12, 13 o 14 dígitos) y el UPC-E: el dígito verificador, con pesos 3 y 1 desde la derecha.</summary>
public static class Gtin
{
    /// <summary>Si <paramref name="codigo"/> es un GTIN de 8, 12, 13 o 14 dígitos con su verificador bien.</summary>
    public static bool EsValido(string codigo)
    {
        if (codigo.Length is not (8 or 12 or 13 or 14))
            return false;
        var suma = 0;
        for (var i = 0; i < codigo.Length - 1; i++)
        {
            if (!char.IsAsciiDigit(codigo[i]))
                return false;
            // Desde la derecha (sin el verificador): el último pesa 3, el anterior 1, y así.
            suma += (codigo[i] - '0') * ((codigo.Length - 2 - i) % 2 == 0 ? 3 : 1);
        }
        return char.IsAsciiDigit(codigo[^1]) && (10 - suma % 10) % 10 == codigo[^1] - '0';
    }

    /// <summary>
    /// Un UPC-E (8 dígitos: sistema 0 o 1, seis dígitos y verificador) a su UPC-A de 12; vacío si no es un UPC-E válido (el
    /// verificador es el del UPC-A expandido).
    /// </summary>
    public static string? UpcEAUpcA(string upcE)
    {
        if (upcE.Length != 8 || !upcE.All(char.IsAsciiDigit) || upcE[0] is not ('0' or '1'))
            return null;
        var d = upcE[1..7];
        var cuerpo = d[5] switch
        {
            '0' or '1' or '2' => $"{d[0]}{d[1]}{d[5]}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{d[5]}",
        };
        var upcA = $"{upcE[0]}{cuerpo}{upcE[7]}";
        return EsValido(upcA) ? upcA : null;
    }
}
