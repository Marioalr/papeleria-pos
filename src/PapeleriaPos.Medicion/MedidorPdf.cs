namespace PapeleriaPos.Medicion;

/// <summary>Un PDF medido para cotizarlo: la tinta de cada página (fracción de la hoja) y una miniatura de la primera.</summary>
/// <param name="Nota">Lo que hay que saber del resultado (por ejemplo, que solo se midieron las primeras páginas).</param>
public sealed record DocumentoMedido(IReadOnlyList<PageCoverage> Paginas, byte[]? MiniaturaPng, string? Nota);

/// <summary>
/// Mide un PDF con el mismo método que las impresiones, para cotizarlo antes de imprimir. Lo hace la PC donde está el
/// archivo (la caja de Windows); el precio lo calcula la caja principal.
/// </summary>
public interface IMedidorPdf
{
    /// <param name="avance">Páginas medidas y total, para mostrar el avance.</param>
    /// <exception cref="InvalidDataException">Con un mensaje para mostrar: no es PDF, tiene contraseña, está dañado.</exception>
    Task<DocumentoMedido> MedirAsync(Stream pdf, IProgress<(int Hechas, int Total)>? avance = null, CancellationToken ct = default);
}
