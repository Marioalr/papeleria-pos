namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Encuentra productos capturados dos veces con otro nombre: "Pluma punto fino Bic Azul", "PLUMA BIC AZUL PUNTO FINO"
/// y "Pluma p. fino Bic Azul" son el mismo. Dos nombres son el mismo si tienen las mismas palabras en cualquier orden,
/// sin importar mayúsculas ni acentos, y aceptando abreviaturas ("p." por "punto", "cuad." por "cuaderno").
/// <para>
/// Es estricto a propósito: "Pluma Bic Azul" y "Pluma Bic Negra" no son el mismo aunque se parezcan mucho.
/// </para>
/// </summary>
public static class NombresParecidos
{
    public static IReadOnlyList<string> Palabras(string nombre) =>
        Texto.Normalizar(nombre)
            .Split([' ', '·', ',', '/', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd('.'))
            .Where(p => p.Length > 0)
            .ToList();

    public static bool SonElMismo(string a, string b) => SonElMismo(Palabras(a), Palabras(b));

    public static bool SonElMismo(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count || a.Count == 0)
            return false;

        // Primero las palabras idénticas; las que sobran tienen que emparejarse como abreviaturas.
        var restoA = a.ToList();
        var restoB = b.ToList();
        foreach (var palabra in a)
        {
            if (restoB.Remove(palabra))
                restoA.Remove(palabra);
        }

        foreach (var palabra in restoA.OrderBy(p => p.Length))
        {
            var pareja = restoB.FirstOrDefault(otra => EsAbreviatura(palabra, otra) || EsAbreviatura(otra, palabra));
            if (pareja is null)
                return false;
            restoB.Remove(pareja);
        }
        return restoB.Count == 0;
    }

    /// <summary>
    /// Agrupa los nombres que son el mismo. Solo compara los que tienen las mismas iniciales, así funciona igual con
    /// 50 mil productos.
    /// </summary>
    public static List<List<T>> Agrupar<T>(IEnumerable<T> elementos, Func<T, string> nombre)
    {
        var grupos = new List<List<T>>();
        var porIniciales = elementos
            .Select(e => (Elemento: e, Palabras: Palabras(nombre(e))))
            .Where(x => x.Palabras.Count > 0)
            .GroupBy(x => string.Concat(x.Palabras.Select(p => p[0]).Order()));

        foreach (var candidatos in porIniciales)
        {
            var pendientes = candidatos.ToList();
            while (pendientes.Count > 0)
            {
                var primero = pendientes[0];
                var grupo = pendientes.Where(x => SonElMismo(primero.Palabras, x.Palabras)).ToList();
                pendientes.RemoveAll(grupo.Contains);
                if (grupo.Count > 1)
                    grupos.Add(grupo.Select(x => x.Elemento).ToList());
            }
        }
        return grupos;
    }

    /// <summary>
    /// Qué tanto se parecen dos nombres, de 0 a 1, para sugerir el producto de un renglón del documento del proveedor
    /// ("PLUMA BIC P/FINO ROJA" se parece a "Pluma punto fino Bic Rojo"). A diferencia de <see cref="SonElMismo"/>, no
    /// exige las mismas palabras: cuenta las que coinciden (iguales, abreviadas o con otra terminación, como rojo y
    /// roja) entre todas las de los dos.
    /// </summary>
    public static double Parecido(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var resto = b.ToList();
        var coinciden = 0;
        foreach (var palabra in a)
        {
            var pareja = resto.FirstOrDefault(otra => otra == palabra) ?? resto.FirstOrDefault(otra => SeParecen(palabra, otra));
            if (pareja is null)
                continue;
            resto.Remove(pareja);
            coinciden++;
        }
        return 2.0 * coinciden / (a.Count + b.Count);
    }

    private static bool SeParecen(string a, string b) =>
        EsAbreviatura(a, b) || EsAbreviatura(b, a)
        || (a.Length >= 4 && b.Length >= 4 && !a.Any(char.IsDigit) && a.Length == b.Length && a[..^1] == b[..^1]);

    /// <summary>
    /// Una abreviatura quita al menos dos letras ("p" por "punto", "cuad" por "cuaderno"): así "AA" no se confunde
    /// con "AAA". Los números no se abrevian: "1" no es "12" ni "100".
    /// </summary>
    private static bool EsAbreviatura(string corta, string larga) =>
        corta.Length <= larga.Length - 2 && !corta.Any(char.IsDigit) && larga.StartsWith(corta, StringComparison.Ordinal);
}
