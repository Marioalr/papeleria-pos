namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Un PDF medido para cotizarlo: la tinta de cada página (fracción de la hoja) y una miniatura de la primera.</summary>
/// <param name="Nota">Lo que hay que saber del resultado (por ejemplo, que solo se midieron las primeras páginas).</param>
public sealed record DocumentoMedido(IReadOnlyList<PageCoverage> Paginas, byte[]? MiniaturaPng, string? Nota);

/// <summary>
/// Cómo va la medición (2026-11-08): cuántas páginas van de cuántas, y lo medido hasta ahí, para dar el precio de lo medido mientras sigue
/// (Control Print no daba precio hasta medir todas).
/// </summary>
/// <param name="Medidas">Las páginas ya medidas, en orden (una copia: no cambia después).</param>
public sealed record AvanceMedicion(int Hechas, int Total, IReadOnlyList<PageCoverage> Medidas);

/// <summary>
/// Mide un PDF con el mismo método que las impresiones, para cotizarlo antes de imprimir. Lo hace la PC donde está el
/// archivo (la caja de Windows); el precio lo calcula la caja principal.
/// </summary>
public interface IMedidorPdf
{
    /// <param name="avance">Páginas medidas, total y lo medido hasta ahí, para mostrar el avance y el precio de lo medido.</param>
    /// <exception cref="InvalidDataException">Con un mensaje para mostrar: no es PDF, tiene contraseña, está dañado.</exception>
    Task<DocumentoMedido> MedirAsync(Stream pdf, IProgress<AvanceMedicion>? avance = null, CancellationToken ct = default);
}
