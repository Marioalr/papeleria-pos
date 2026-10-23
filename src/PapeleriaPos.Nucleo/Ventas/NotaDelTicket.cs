namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// La nota del ticket (F4 al cobrar, como en eleventa; 2026-10-22): uno o dos renglones del ticket de 80 mm, por ejemplo a
/// dónde se entrega o un comentario para el cliente. Se escribe en un solo renglón de la pantalla; el ticket la parte.
/// </summary>
public static class NotaDelTicket
{
    /// <summary>
    /// Lo más largo, en caracteres: con «Nota: » delante cabe en dos renglones del ticket (48 por renglón con la letra normal).
    /// </summary>
    public const int Maximo = 80;

    /// <summary>
    /// Como se guarda: sin espacios de más ni saltos de renglón; vacía, null. Más larga que <see cref="Maximo"/> no se guarda:
    /// <see cref="ArgumentException"/> con el porqué.
    /// </summary>
    public static string? Normalizar(string? nota)
    {
        var limpia = Limpia(nota);
        if (limpia is { Length: > Maximo })
            throw new ArgumentException($"La nota del ticket va hasta {Maximo} letras (dos renglones del ticket): tiene {limpia.Length}.");
        return limpia;
    }

    /// <summary>
    /// La de una venta que ya se cobró sin conexión y llega con la bandeja: no se rechaza (ya pasó); si viniera más larga, se
    /// guarda hasta donde cabe.
    /// </summary>
    public static string? Recortada(string? nota) => Limpia(nota) is { } limpia ? limpia.Length > Maximo ? limpia[..Maximo].TrimEnd() : limpia : null;

    /// <summary>
    /// Sin caracteres de control (revisión del PR #133: llegarían a la impresora como comandos, como abrir el cajón) ni espacios
    /// de más: cada uno cuenta como un espacio.
    /// </summary>
    private static string? Limpia(string? nota)
    {
        if (nota is null)
            return null;
        var sinControles = string.Concat(nota.Select(c => char.IsControl(c) ? ' ' : c));
        if (string.IsNullOrWhiteSpace(sinControles))
            return null;
        return string.Join(' ', sinControles.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
