using System.Globalization;

namespace PapeleriaPos.Instalacion;

/// <summary>Lo que es esta PC en la tienda (docs/instalacion.md, «Qué es esta PC»).</summary>
public enum RolDeLaPc
{
    /// <summary>«Caja principal»: el servicio con la base y la caja; las otras cajas y los celulares se conectan a ella. Como hasta hoy.</summary>
    Principal,

    /// <summary>«La única caja»: el servicio y la caja; los celulares sí se conectan, otra caja desde otra PC no.</summary>
    Unica,

    /// <summary>«Otra caja»: solo la caja, que se conecta a la caja principal. Sin servicio, así que sin servicio.json.</summary>
    Otra,
}

/// <summary>
/// Las respuestas del asistente que llegan a servicio.json y a caja.json (el archivo respuestas.txt que deja el asistente en su carpeta
/// temporal; docs/instalacion.md). Nunca traen la clave de eleventa ni el código de Control Print: esos los pone el asistente directo en
/// las variables del servicio.
/// </summary>
public sealed record Respuestas
{
    public required RolDeLaPc Rol { get; init; }

    /// <summary>«Otra caja»: la IP (o el nombre) de la caja principal, con su puerto si no es el 5080. Vacía: la caja la busca al abrirla.</summary>
    public string? Principal { get; init; }

    /// <summary>La impresora de tickets como la lee la caja: vacía (en pantalla), «tcp://IP:9100» o el nombre de una impresora de Windows.</summary>
    public string Impresora { get; init; } = "";

    /// <summary>La cola que mide la caja (ColaImpresion); vacía, ninguna.</summary>
    public string? Cola { get; init; }

    /// <summary>El lector por puerto COM («COM3»); vacío, como teclado.</summary>
    public string? LectorPuerto { get; init; }

    public int LectorBaudios { get; init; } = 9600;

    /// <summary>Si la tienda tiene Control Print (solo con servicio); vacío, no aplica.</summary>
    public bool? ControlPrint { get; init; }

    public string? ControlPrintServidor { get; init; }

    /// <summary>Si esta caja convive con eleventa (solo con servicio); vacío, no aplica.</summary>
    public bool? Eleventa { get; init; }

    public string? EleventaServidor { get; init; }

    public string? EleventaBase { get; init; }

    public int? EleventaCaja { get; init; }

    public int? EleventaCajero { get; init; }

    public string? EleventaGbak { get; init; }

    public string? EleventaRespaldos { get; init; }

    public bool ConServicio => Rol != RolDeLaPc.Otra;

    /// <summary>Las del archivo del asistente. Lo que no se entiende se dice (FormatException), en lugar de escribir algo a medias.</summary>
    public static Respuestas De(ArchivoDeClaves archivo)
    {
        var rol = (archivo["rol"] ?? "").Trim().ToLowerInvariant() switch
        {
            "principal" => RolDeLaPc.Principal,
            "unica" or "única" => RolDeLaPc.Unica,
            "otra" => RolDeLaPc.Otra,
            var otro => throw new FormatException($"«rol» va como principal, unica u otra (está «{otro}»)."),
        };
        string? puerto = null;
        var baudios = 9600;
        var lector = (archivo["lector"] ?? "teclado").Trim().ToLowerInvariant();
        if (lector == "com")
        {
            puerto = NombreDelPuerto(archivo["lector-puerto"]);
            if (Texto(archivo["lector-baudios"]) is { } velocidad
                && (!int.TryParse(velocidad, NumberStyles.None, CultureInfo.InvariantCulture, out baudios) || baudios is < 50 or > 921_600))
                throw new FormatException($"La velocidad del lector va de 50 a 921600 baudios (está «{velocidad}»).");
        }
        else if (lector != "teclado")
            throw new FormatException($"«lector» va como teclado o com (está «{lector}»).");

        var conServicio = rol != RolDeLaPc.Otra;
        var controlPrint = conServicio ? archivo.SiNo("controlprint") : null;
        var eleventa = conServicio ? archivo.SiNo("eleventa") : null;
        var respuestas = new Respuestas
        {
            Rol = rol,
            Principal = rol == RolDeLaPc.Otra ? Texto(archivo["principal"]) : null,
            Impresora = (archivo["impresora"] ?? "").Trim(),
            Cola = Texto(archivo["cola"]),
            LectorPuerto = puerto,
            LectorBaudios = baudios,
            ControlPrint = controlPrint,
            ControlPrintServidor = controlPrint == true ? Obligatorio(archivo, "controlprint-servidor", "la IP de la PC de Control Print") : null,
            Eleventa = eleventa,
            EleventaServidor = eleventa == true ? Obligatorio(archivo, "eleventa-servidor", "la IP de la PC de eleventa") : null,
            EleventaBase = eleventa == true ? Obligatorio(archivo, "eleventa-base", "la ruta de la base de eleventa") : null,
            EleventaCaja = eleventa == true ? Numero(archivo, "eleventa-caja", "el número de la caja en eleventa") : null,
            EleventaCajero = eleventa == true ? Numero(archivo, "eleventa-cajero", "el número del cajero en eleventa") : null,
            EleventaGbak = eleventa == true ? Texto(archivo["eleventa-gbak"]) : null,
            EleventaRespaldos = eleventa == true ? Texto(archivo["eleventa-respaldos"]) : null,
        };
        if (respuestas.Impresora.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            && !(Uri.TryCreate(respuestas.Impresora, UriKind.Absolute, out var impresora) && impresora.Host.Length > 0))
            throw new FormatException($"La impresora de tickets por la red va como tcp://IP:9100 (está «{respuestas.Impresora}»).");
        if (respuestas.Principal is { } principal && BusquedaDeLaPrincipal.Direccion(principal) is null)
            throw new FormatException($"La dirección de la caja principal no se entiende (está «{principal}»): va su IP, como 192.168.1.10.");
        return respuestas;
    }

    private static string? Texto(string? valor) => valor?.Trim() is { Length: > 0 } texto ? texto : null;

    private static string Obligatorio(ArchivoDeClaves archivo, string clave, string que) =>
        Texto(archivo[clave]) ?? throw new FormatException($"Falta {que} («{clave}»).");

    private static int? Numero(ArchivoDeClaves archivo, string clave, string que)
    {
        if (Texto(archivo[clave]) is not { } texto)
            return null;
        return int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero > 0
            ? numero
            : throw new FormatException($"{char.ToUpperInvariant(que[0])}{que[1..]} va con cifras (está «{texto}»).");
    }

    /// <summary>«com3», «COM3» o «3» son COM3, como los lee la caja (AjustesLector.NombreDelPuerto).</summary>
    public static string NombreDelPuerto(string? texto)
    {
        var nombre = (texto ?? "").Trim();
        if (nombre.Length == 0)
            throw new FormatException("Falta el puerto COM del lector (por ejemplo, COM3).");
        if (int.TryParse(nombre, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero is > 0 and < 256)
            return $"COM{numero}";
        if (nombre.StartsWith("com", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(nombre[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var com) && com is > 0 and < 256)
            return $"COM{com}";
        throw new FormatException($"El puerto del lector va como COM1, COM2, COM3… (está «{nombre}»).");
    }
}
