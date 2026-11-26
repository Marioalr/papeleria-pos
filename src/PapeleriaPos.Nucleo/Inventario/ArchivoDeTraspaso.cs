using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>Un renglón del archivo de un traspaso.</summary>
/// <param name="Codigos">
/// Los códigos del producto por pieza (los de su presentación base), el principal primero: con ellos se empata en el catálogo de
/// la sucursal que recibe. Vacío si allá no tiene código.
/// </param>
/// <param name="Nombre">Como se llama en la sucursal que manda: para leerlo, y para empatarlo a mano si su código no está.</param>
/// <param name="Milesimas">Cuántos van, en su unidad base y en milésimas (12 piezas: 12000).</param>
public sealed record RenglonDeArchivo(IReadOnlyList<string> Codigos, string Nombre, UnidadBase Unidad, long Milesimas);

/// <summary>Lo que dice el archivo de un traspaso, sin su huella.</summary>
/// <param name="Id">El del traspaso: el mismo en las dos sucursales.</param>
/// <param name="Origen">La sucursal que manda, por su nombre.</param>
/// <param name="Destino">A dónde va, como lo escribió quien lo armó.</param>
/// <param name="Fecha">Cuándo se exportó (UTC).</param>
/// <param name="Quien">Quién lo exportó, por su nombre.</param>
public sealed record ContenidoDeTraspaso(
    Guid Id, string Origen, string Destino, DateTime Fecha, string Quien, string? Nota, IReadOnlyList<RenglonDeArchivo> Renglones);

/// <summary>El archivo no es un traspaso, o no se puede usar: lo que se le dice a quien lo importa.</summary>
public sealed class ArchivoDeTraspasoInvalido(string mensaje) : ArgumentException(mensaje);

/// <summary>
/// El archivo con que viaja un traspaso de una sucursal a otra (<see cref="Traspaso"/>, provisional): un JSON que se lee a simple
/// vista, con lo que va y una <b>huella</b> (SHA-256 de su contenido). Sin costos: el archivo anda en una memoria, y los costos
/// solo los ve quien tiene el permiso; lo que entra se valúa al costo de la sucursal que recibe.
/// <para>
/// La huella dice si el archivo se dañó o si alguien lo cambió a mano sin saber: al importarlo se vuelve a calcular y, si no
/// coincide, no entra. No es un candado contra quien sepa recalcularla (las dos sucursales no comparten ningún secreto; eso llega
/// con el servidor). Para eso está la <see cref="Clave"/>: ocho letras de la huella que la sucursal que manda ve al exportar y le
/// dicta a la que recibe (por teléfono o por mensaje), que la ve al importar; si no son las mismas, el archivo no es el que salió.
/// </para>
/// </summary>
public static class ArchivoDeTraspaso
{
    /// <summary>Lo que distingue a estos archivos de cualquier otro JSON.</summary>
    public const string Formato = "papeleria-pos.traspaso";

    /// <summary>La versión del formato. Una más nueva no se importa: primero se actualiza el POS de la sucursal que recibe.</summary>
    public const int Version = 1;

    public const string Extension = ".traspaso.json";
    public const string Tipo = "application/json";

    /// <summary>Lo más que pesa un archivo que se importa: 500 renglones con sus códigos caben de sobra.</summary>
    public const int BytesMaximos = 1024 * 1024;

    private sealed record Sobre(
        string Formato, int Version, Guid Id, string Origen, string Destino, DateTime Fecha, string Quien, string? Nota,
        IReadOnlyList<RenglonDeArchivo> Renglones, string? Huella);

    /// <summary>Para la huella: siempre igual, sin espacios y con las letras tal cual.</summary>
    private static readonly JsonSerializerOptions Compacto = Opciones(conSangria: false);

    /// <summary>Para el archivo: con sangría, para leerlo a simple vista.</summary>
    private static readonly JsonSerializerOptions Legible = Opciones(conSangria: true);

    private static JsonSerializerOptions Opciones(bool conSangria) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = conSangria,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>La huella de lo que dice el archivo: SHA-256, en hexadecimal, de su contenido escrito siempre igual.</summary>
    public static string Huella(ContenidoDeTraspaso contenido) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Normal(contenido), Compacto)));

    /// <summary>«4F2A-91C3»: las primeras ocho letras de la huella, para dictarla.</summary>
    public static string Clave(string huella) => huella.Length >= 8 ? $"{huella[..4]}-{huella[4..8]}".ToUpperInvariant() : huella.ToUpperInvariant();

    /// <summary>El texto del archivo, con su huella.</summary>
    public static string Escribir(ContenidoDeTraspaso contenido)
    {
        var c = Normal(contenido);
        return JsonSerializer.Serialize(new Sobre(Formato, Version, c.Id, c.Origen, c.Destino, c.Fecha, c.Quien, c.Nota, c.Renglones, Huella(c)), Legible);
    }

    /// <summary>«Traspaso a Sucursal 2 2026-11-26 4F2A-91C3.traspaso.json»: sin lo que Windows no deja en un nombre.</summary>
    public static string Nombre(ContenidoDeTraspaso contenido, DateOnly dia)
    {
        var destino = new string(contenido.Destino.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return $"Traspaso a {(destino.Length == 0 ? "sucursal" : destino)} {dia:yyyy-MM-dd} {Clave(Huella(contenido))}{Extension}";
    }

    /// <summary>
    /// Lee el archivo y lo revisa: que sea un traspaso, de una versión que este POS entiende, completo y con su huella intacta.
    /// </summary>
    /// <returns>Lo que dice y su huella.</returns>
    /// <exception cref="ArchivoDeTraspasoInvalido">No se puede usar, con el motivo en palabras de la tienda.</exception>
    public static (ContenidoDeTraspaso Contenido, string Huella) Leer(ReadOnlySpan<byte> datos)
    {
        if (datos.Length > BytesMaximos)
            throw new ArchivoDeTraspasoInvalido("El archivo pesa demasiado para ser un traspaso: elige el que se exportó en la otra sucursal.");
        // Con o sin la marca de UTF-8 al principio (el Bloc de notas la pone al guardar).
        if (datos.StartsWith(Encoding.UTF8.Preamble))
            datos = datos[Encoding.UTF8.Preamble.Length..];
        Sobre? sobre;
        try
        {
            sobre = JsonSerializer.Deserialize<Sobre>(datos, Compacto);
        }
        catch (JsonException)
        {
            throw new ArchivoDeTraspasoInvalido(NoEs);
        }
        if (sobre is null || sobre.Formato != Formato)
            throw new ArchivoDeTraspasoInvalido(NoEs);
        if (sobre.Version > Version)
            throw new ArchivoDeTraspasoInvalido(
                "Este traspaso se exportó con una versión más nueva del POS: actualiza el POS de esta sucursal y vuelve a importarlo.");
        if (sobre.Version < 1 || sobre.Id == Guid.Empty || sobre.Renglones is null || sobre.Origen is null || sobre.Destino is null || sobre.Quien is null
            || sobre.Renglones.Any(r => r is null || r.Nombre is null || r.Codigos is null || r.Codigos.Any(c => c is null) || !Enum.IsDefined(r.Unidad)))
            throw new ArchivoDeTraspasoInvalido(Alterado);
        var contenido = new ContenidoDeTraspaso(sobre.Id, sobre.Origen, sobre.Destino, sobre.Fecha.ToUniversalTime(), sobre.Quien, sobre.Nota, sobre.Renglones);
        var huella = Huella(contenido);
        if (!string.Equals(huella, sobre.Huella, StringComparison.OrdinalIgnoreCase))
            throw new ArchivoDeTraspasoInvalido(Alterado);
        if (contenido.Renglones.Count is 0 or > Traspaso.RenglonesMaximos || contenido.Renglones.Any(r => r.Milesimas <= 0))
            throw new ArchivoDeTraspasoInvalido(Alterado);
        return (Normal(contenido), huella);
    }

    public const string NoEs = "Ese archivo no es un traspaso: elige el que se exportó en la otra sucursal (termina en «.traspaso.json»).";

    public const string Alterado =
        "El archivo del traspaso está dañado o lo cambiaron después de exportarlo: no se importó nada. Pide que lo exporten otra vez en la sucursal que lo mandó.";

    /// <summary>Como se escribe siempre: la fecha en UTC y sin fracciones de segundo, y la nota vacía como si no hubiera.</summary>
    private static ContenidoDeTraspaso Normal(ContenidoDeTraspaso c) => c with
    {
        Fecha = new DateTime(c.Fecha.Ticks - c.Fecha.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
        Nota = string.IsNullOrWhiteSpace(c.Nota) ? null : c.Nota,
    };
}
