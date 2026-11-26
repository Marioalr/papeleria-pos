using System.Globalization;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// Cómo se escriben y se leen los folios de los tickets (2026-11-22, regla del dueño: «los folios, de preferencia sin letra»).
/// <list type="bullet">
/// <item>Lo que registra en línea la caja principal (el servicio) sale SIN letra, de un solo consecutivo de la sucursal: «004812».
/// Da igual qué caja lo cobró: la caja principal siempre contesta por sí misma, así que ni siquiera lleva letra.</item>
/// <item>Solo una caja secundaria que cobra sin conexión (contingencia) usa su letra, para que sus folios no choquen con los de la
/// caja principal ni con los de otra caja: «B-000123».</item>
/// <item>Lo de antes se queda con su letra («A-4812»): no se renumera nada.</item>
/// <item>Se muestran sin ceros a la izquierda (2026-11-26, pedido del dueño): «4812», «B-123». Para buscar se sigue aceptando con
/// ceros («004812», «A-004812»), como en los tickets que ya se imprimieron.</item>
/// </list>
/// </summary>
public static partial class Folios
{
    /// <summary>La serie de lo que registra la caja principal en línea y la de la caja principal misma: ninguna.</summary>
    public const string SinLetra = "";

    /// <summary>«4812» sin letra; «B-123» con la de una caja secundaria (o la de antes). Sin ceros a la izquierda.</summary>
    public static string Texto(string? serie, int folio) =>
        string.IsNullOrEmpty(serie)
            ? folio.ToString(CultureInfo.InvariantCulture)
            : $"{serie}-{folio.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Como <see cref="Texto(string?, int)"/>; vacío si todavía no tiene folio.</summary>
    public static string? Texto(string? serie, int? folio) => folio is { } f ? Texto(serie, f) : null;

    /// <summary>
    /// Lo que se escribió para buscar un ticket: «A-004812», «a4812», «4812», «004812» o «-004812».
    /// <see cref="FolioLeido.Serie"/> vacía (null) es que no se dijo letra: puede ser de cualquiera (también el folio de eleventa, en el
    /// piloto); «» (<see cref="SinLetra"/>) es que se pidió sin letra a propósito («-004812», el que la ventana usa cuando ya eligió).
    /// </summary>
    public static FolioLeido? Leer(string? texto)
    {
        var limpio = (texto ?? "").Trim().ToUpperInvariant();
        if (Forma().Match(limpio) is not { Success: true } partes)
            return null;
        var numero = int.Parse(partes.Groups["numero"].Value, CultureInfo.InvariantCulture);
        var serie = partes.Groups["serie"].Success ? partes.Groups["serie"].Value
            : partes.Groups["sinletra"].Success ? SinLetra
            : null;
        return new FolioLeido(serie, numero);
    }

    /// <summary>
    /// El folio de la caja como se le pide al servicio sin que se confunda con el de eleventa: sin letra, con «-» adelante
    /// («4812» o «004812» → «-4812»); con letra, igual.
    /// </summary>
    public static string Inequivoco(string folio) =>
        Leer(folio) is { Serie: null or "" } leido ? "-" + leido.Numero.ToString(CultureInfo.InvariantCulture) : folio;

    /// <summary>La serie de una caja como se ve en las pantallas: «Sin letra» la de la caja principal.</summary>
    public static string SerieParaVer(string? serie) => string.IsNullOrEmpty(serie) ? "Sin letra" : serie;

    /// <summary>La serie de lo que sale en la línea de un ticket ya cobrado («B-123» → «B»; «4812» → «»).</summary>
    public static string SerieDe(string folio) => Leer(folio)?.Serie ?? SinLetra;

    /// <summary>«A-000123», «A123», «123», «000123» o «-000123» (ya en mayúsculas).</summary>
    [GeneratedRegex(@"^(?:(?<serie>[A-Z]{1,3})-?|(?<sinletra>-))?0*(?<numero>[0-9]{1,9})$")]
    private static partial Regex Forma();
}

/// <param name="Serie">La letra que se escribió; vacía (null) si no se dijo, «» si se pidió sin letra.</param>
/// <param name="Numero">El consecutivo.</param>
public sealed record FolioLeido(string? Serie, int Numero);
