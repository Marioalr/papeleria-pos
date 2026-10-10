using System.Globalization;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos.Documentos;

/// <summary>Un renglón como viene en el documento del proveedor. El importe ya lleva impuestos y descuentos.</summary>
public sealed record LineaLeida(string? Codigo, string Descripcion, string? Unidad, decimal Cantidad, long ImporteCentavos);

/// <param name="Avisos">Lo que conviene revisar de la lectura, para mostrarlo junto a los renglones.</param>
public sealed record DocumentoLeido(
    TipoDocumentoProveedor Tipo, string? Folio, string? Uuid, string? RfcEmisor, string? NombreEmisor, long? TotalCentavos, IReadOnlyList<LineaLeida> Lineas,
    IReadOnlyList<string> Avisos);

/// <summary>
/// Lee el documento del proveedor (docs/fase-2-inventario.md, entrega 2): el XML de una factura o el PDF de una nota.
/// Los errores son para mostrarlos tal cual (InvalidOperationException).
/// </summary>
public static class LectorDocumentos
{
    public const int BytesMaximos = 10 * 1024 * 1024;

    public static DocumentoLeido Leer(byte[] contenido)
    {
        if (contenido.Length == 0)
            throw new InvalidOperationException("El archivo está vacío.");
        if (contenido.Length > BytesMaximos)
            throw new InvalidOperationException("El archivo pesa más de 10 MB.");
        var inicio = contenido.AsSpan(0, Math.Min(contenido.Length, 1024));
        if (inicio.IndexOf("%PDF"u8) >= 0)
            return LectorNotaPdf.Leer(contenido);
        if (inicio.IndexOf("<"u8) >= 0)
            return LectorCfdi.Leer(contenido);
        throw new InvalidOperationException("El archivo no es el XML de una factura ni un PDF.");
    }

    internal static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);

    internal static decimal? Decimal(string? texto) =>
        decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var valor) ? valor : null;
}
