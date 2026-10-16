namespace PapeleriaPos.Herramientas.Tienda;

/// <summary>
/// Para el fin de semana en la tienda (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué manda el lector y a qué ritmo.
/// El lector es un teclado: cada código llega como teclas seguidas y un Enter. En el cobro, la caja lo distingue de una
/// persona por ese ritmo (caja.js: no más de <see cref="RafagaMs"/> ms entre tecla y tecla, también antes del Enter, y al
/// menos <see cref="RafagaMinimo"/> caracteres). Si el lector de la tienda fuera más lento, un escaneo en «Pagó con» no
/// iría al ticket.
/// <para>
/// La hora de cada tecla es la de leerla en la consola, que las lee en cuanto llegan: es una medida aproximada; la caja
/// mide con la hora del teclado en la página.
/// </para>
/// </summary>
public static class PruebaLector
{
    /// <summary>RAFAGA_MS de caja.js.</summary>
    public const int RafagaMs = 50;

    /// <summary>RAFAGA_MINIMO de caja.js.</summary>
    public const int RafagaMinimo = 4;

    /// <summary>Una persona no baja de unos 80 ms entre tecla y tecla, ni con el teclado numérico (caja.js).</summary>
    private const int UnaPersonaMs = 80;

    public sealed record Tecla(char Caracter, TimeSpan Hora);

    /// <param name="Duracion">De la primera tecla al Enter.</param>
    /// <param name="EntreTeclas">El mayor tiempo entre dos teclas del código, contando el Enter.</param>
    /// <param name="PromedioEntreTeclas">El promedio entre tecla y tecla, contando el Enter.</param>
    public sealed record Codigo(string Texto, TimeSpan Duracion, TimeSpan EntreTeclas, TimeSpan PromedioEntreTeclas);

    /// <summary>Un código: sus teclas en orden (sin el Enter) y la hora del Enter.</summary>
    public static Codigo Medir(IReadOnlyList<Tecla> teclas, TimeSpan enter)
    {
        if (teclas.Count == 0)
            throw new ArgumentException("Un código lleva al menos una tecla.", nameof(teclas));
        var horas = teclas.Select(t => t.Hora).Append(enter).ToList();
        var pausas = horas.Zip(horas.Skip(1), (antes, despues) => despues - antes).ToList();
        return new Codigo(
            string.Concat(teclas.Select(t => t.Caracter)),
            enter - teclas[0].Hora,
            pausas.Max(),
            TimeSpan.FromTicks((long)pausas.Average(p => p.Ticks)));
    }

    public static bool LoReconoceLaCaja(Codigo c) =>
        c.Texto.Length >= RafagaMinimo && c.EntreTeclas <= TimeSpan.FromMilliseconds(RafagaMs);

    public static IEnumerable<string> Describir(Codigo c)
    {
        yield return $"«{Visible(c.Texto)}» ({c.Texto.Length} caracteres), en {Ms(c.Duracion)}: entre tecla y tecla, "
            + $"{Ms(c.PromedioEntreTeclas)} en promedio y {Ms(c.EntreTeclas)} a lo más.";

        if (LoReconoceLaCaja(c))
            yield return "  ✓ En el cobro, la caja lo reconoce como el lector.";
        else if (c.Texto.Length < RafagaMinimo)
            yield return $"  ✗ Tiene menos de {RafagaMinimo} caracteres: en el cobro no se distingue de alguien que teclea rápido.";
        else
            yield return $"  ✗ En el cobro se tomaría como tecleado: pasaron {Ms(c.EntreTeclas)} entre dos teclas y la caja espera "
                + $"hasta {RafagaMs} ms.";

        if (c.Texto.All(char.IsAsciiDigit) && c.Texto.Length is >= 11 and <= 13)
            yield return $"  Son {c.Texto.Length} cifras: compáralas con el código de ese producto en eleventa.";
    }

    public static IEnumerable<string> Resumen(IReadOnlyList<Codigo> codigos)
    {
        var escaneos = codigos.Where(c => c.Texto.Length >= RafagaMinimo).ToList();
        if (escaneos.Count == 0)
        {
            yield return "No se escaneó ningún código.";
            yield break;
        }
        yield return $"Escaneos que la caja reconoce como el lector en el cobro: {escaneos.Count(LoReconoceLaCaja)} de {escaneos.Count}.";

        var lentos = escaneos.Where(c => !LoReconoceLaCaja(c) && c.EntreTeclas < TimeSpan.FromMilliseconds(UnaPersonaMs)).ToList();
        if (lentos.Count > 0)
            yield return $"  Para reconocerlos todos, RAFAGA_MS en caja.js tendría que ser de unos "
                + $"{(int)Math.Ceiling(lentos.Max(c => c.EntreTeclas).TotalMilliseconds) + 10} ms (una persona no baja de unos {UnaPersonaMs}). Anótalo.";
        var comoPersona = escaneos.Count(c => c.EntreTeclas >= TimeSpan.FromMilliseconds(UnaPersonaMs));
        if (comoPersona > 0)
            yield return $"  {comoPersona} con pausas de {UnaPersonaMs} ms o más, como las de una persona (lo tecleado a mano). Si alguno fue del "
                + "lector, anótalo: por su ritmo no se distingue de alguien que teclea.";
    }

    /// <summary>Lo que no se ve (un tabulador, un carácter de control) se muestra por su código.</summary>
    private static string Visible(string texto) =>
        string.Concat(texto.Select(c => char.IsControl(c) ? $"[{(int)c:X2}]" : c.ToString()));

    private static string Ms(TimeSpan tiempo) => $"{tiempo.TotalMilliseconds:N0} ms";
}
