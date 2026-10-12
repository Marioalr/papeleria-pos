namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Un sinónimo de la tienda para la búsqueda: al escribir <see cref="Palabra"/> también sale lo que se llama
/// <see cref="Equivale"/>. Los decide la tienda, uno por uno, y ninguno viene puesto: un sinónimo mal puesto estorba más
/// de lo que ayuda (en esta tienda «pluma» no es el lapicero). Viaja a las cajas con el catálogo (docs/propuesta-interfaz.md).
/// </summary>
public class Sinonimo : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Lo que se escribe, una palabra, normalizada (sin acentos y en mayúsculas).</summary>
    public required string Palabra { get; set; }

    /// <summary>Lo que también se busca, una palabra, normalizada.</summary>
    public required string Equivale { get; set; }

    /// <summary>Quitarlo lo desactiva: así las cajas se enteran con la versión del catálogo.</summary>
    public bool Activo { get; set; } = true;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Una palabra, sin acentos y en mayúsculas; null si no es una sola palabra de 2 letras o más.</summary>
    public static string? Normalizar(string texto) =>
        Texto.Normalizar(texto) is { Length: >= 2 } normal && !normal.Contains(' ') ? normal : null;
}

/// <summary>Los sinónimos de la tienda, listos para buscar: igual en línea y sin conexión.</summary>
public sealed class SinonimosDeBusqueda
{
    private readonly Dictionary<string, List<string>> _porPalabra;

    public SinonimosDeBusqueda(IEnumerable<(string Palabra, string Equivale)> sinonimos) =>
        _porPalabra = sinonimos.GroupBy(s => s.Palabra, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Equivale).Distinct().ToList(), StringComparer.Ordinal);

    public static SinonimosDeBusqueda Ninguno { get; } = new([]);

    /// <summary>
    /// Lo que también vale por un pedazo escrito: los sinónimos de esa palabra y, con 4 letras o más, de las palabras que
    /// empiezan así («bolig» toma los de «boligrafo»). Cada uno con la palabra de la tienda, para decir por qué salió.
    /// </summary>
    public IReadOnlyList<(string Palabra, string Equivale)> De(string pedazo) =>
        _porPalabra.Where(s => s.Key == pedazo || (pedazo.Length >= 4 && s.Key.StartsWith(pedazo, StringComparison.Ordinal)))
            .SelectMany(s => s.Value.Select(e => (s.Key, e)))
            .ToList();
}
