using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Impresion;

public enum Simbologia
{
    Ean13,
    Code128,
}

/// <summary>Un código de barras como franjas de un módulo: verdadero es barra, falso es espacio. Sin las zonas en blanco.</summary>
public sealed record Barras(Simbologia Simbologia, string Texto, bool[] Modulos)
{
    /// <summary>Lo que hay que dejar en blanco a cada lado, en módulos, para que el lector lo encuentre.</summary>
    public int ZonaEnBlanco => Simbologia == Simbologia.Ean13 ? 9 : 10;
}

/// <summary>
/// Los códigos de barras de las etiquetas (docs/fase-2-inventario.md, entrega 4). Los internos son EAN-13 que empiezan
/// con 2 (los genera el POS) y salen como EAN-13, que es lo más corto para 13 dígitos; lo demás (los códigos que
/// eleventa inventó, con letras) sale en Code 128.
/// </summary>
public static class CodigosDeBarras
{
    /// <summary>Las barras como dibujo SVG del tamaño exacto, para la hoja de etiquetas y la hoja de códigos.</summary>
    public static string Svg(Barras barras, decimal moduloMm, decimal altoMm)
    {
        var svg = new System.Text.StringBuilder();
        svg.Append($"<svg class=\"barras\" width=\"{HojaEtiquetas.Mm(moduloMm * barras.Modulos.Length)}\" height=\"{HojaEtiquetas.Mm(altoMm)}\" ");
        svg.Append($"viewBox=\"0 0 {barras.Modulos.Length} 1\" preserveAspectRatio=\"none\" shape-rendering=\"crispEdges\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"");
        for (var i = 0; i < barras.Modulos.Length;)
        {
            if (!barras.Modulos[i])
            {
                i++;
                continue;
            }
            var inicio = i;
            while (i < barras.Modulos.Length && barras.Modulos[i])
                i++;
            svg.Append($"M{inicio} 0h{i - inicio}v1h-{i - inicio}z");
        }
        svg.Append("\" fill=\"#000\"/></svg>");
        return svg.ToString();
    }

    public static Barras Para(string codigo) =>
        Ean13.EsValido(codigo) ? new Barras(Simbologia.Ean13, codigo, CodificarEan13(codigo)) : new Barras(Simbologia.Code128, codigo, CodificarCode128(codigo));

    // ── EAN-13 ──────────────────────────────────────────────────────────────

    /// <summary>Cada dígito en EAN-13: con L o G a la izquierda (según el primer dígito) y con R a la derecha.</summary>
    public static readonly IReadOnlyList<string> L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    public static readonly IReadOnlyList<string> G = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    public static readonly IReadOnlyList<string> R = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];

    /// <summary>El primer dígito no se dibuja: decide si cada dígito de la izquierda va con paridad L o G.</summary>
    private static readonly string[] Paridad = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    public static bool[] CodificarEan13(string codigo)
    {
        if (!Ean13.EsValido(codigo))
            throw new ArgumentException($"«{codigo}» no es un EAN-13 válido.");
        var franjas = new System.Text.StringBuilder("101");
        var paridad = Paridad[codigo[0] - '0'];
        for (var i = 1; i <= 6; i++)
            franjas.Append((paridad[i - 1] == 'L' ? L : G)[codigo[i] - '0']);
        franjas.Append("01010");
        for (var i = 7; i <= 12; i++)
            franjas.Append(R[codigo[i] - '0']);
        franjas.Append("101");
        return franjas.ToString().Select(c => c == '1').ToArray();
    }

    // ── Code 128 ────────────────────────────────────────────────────────────

    /// <summary>Anchos de barra y espacio de cada símbolo (0 a 102, inicio A, B y C, y el de fin).</summary>
    public static readonly IReadOnlyList<string> Patrones =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int CodigoB = 100, CodigoC = 99, InicioB = 104, InicioC = 105, Fin = 106;

    /// <summary>Los valores de los símbolos, con inicio, verificación y fin. Los dígitos van de dos en dos (juego C).</summary>
    public static List<int> Simbolos(string texto)
    {
        if (texto.Length == 0 || texto.Any(c => c < ' ' || c > '~'))
            throw new ArgumentException($"«{texto}» no se puede poner en Code 128: solo letras, números y signos sin acento.");

        static int Digitos(string t, int desde)
        {
            var n = 0;
            while (desde + n < t.Length && char.IsAsciiDigit(t[desde + n]))
                n++;
            return n;
        }

        var simbolos = new List<int>();
        var enC = Digitos(texto, 0) is var inicio && (inicio == texto.Length ? inicio >= 2 : inicio >= 4) && inicio % 2 == 0;
        simbolos.Add(enC ? InicioC : InicioB);
        var i = 0;
        while (i < texto.Length)
        {
            var digitos = Digitos(texto, i);
            // Los dígitos van en pares (juego C) cuando son 4 o más (o los últimos 2 o más); uno suelto va en B.
            var conviene = digitos >= 4 || (digitos >= 2 && i + digitos == texto.Length);
            if (conviene && digitos >= 2)
            {
                if (!enC)
                {
                    if (digitos % 2 == 1)
                    {
                        simbolos.Add(texto[i] - ' ');
                        i++;
                        digitos--;
                    }
                    simbolos.Add(CodigoC);
                    enC = true;
                }
                for (; digitos >= 2; digitos -= 2, i += 2)
                    simbolos.Add((texto[i] - '0') * 10 + (texto[i + 1] - '0'));
                continue;
            }
            if (enC)
            {
                simbolos.Add(CodigoB);
                enC = false;
            }
            simbolos.Add(texto[i] - ' ');
            i++;
        }

        var suma = simbolos[0];
        for (var p = 1; p < simbolos.Count; p++)
            suma += simbolos[p] * p;
        simbolos.Add(suma % 103);
        simbolos.Add(Fin);
        return simbolos;
    }

    public static bool[] CodificarCode128(string texto)
    {
        var modulos = new List<bool>();
        foreach (var simbolo in Simbolos(texto))
        {
            var barra = true;
            foreach (var ancho in Patrones[simbolo])
            {
                modulos.AddRange(Enumerable.Repeat(barra, ancho - '0'));
                barra = !barra;
            }
        }
        return modulos.ToArray();
    }
}
