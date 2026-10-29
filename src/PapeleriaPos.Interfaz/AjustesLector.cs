using System.Globalization;
using System.Text.Json;

namespace PapeleriaPos.Interfaz;

/// <summary>Con qué termina cada código que manda el lector por el puerto («Fin» en caja.json).</summary>
public enum FinDelCodigo
{
    /// <summary>Lo que mande: CR, LF o CRLF (también un tabulador o un ETX), o una pausa sin nada. Por omisión.</summary>
    Auto,

    /// <summary>Un retorno (CR, 0D): lo de eleventa casi siempre. Un LF que venga detrás (CRLF) no estorba.</summary>
    CR,

    /// <summary>Un salto de línea (LF, 0A). Un CR que venga antes no estorba.</summary>
    LF,

    /// <summary>CR y LF: cualquiera de los dos termina el código.</summary>
    CRLF,

    /// <summary>Un tabulador (09): algunos lectores se configuran así.</summary>
    Tab,

    /// <summary>Sin carácter de fin: el código termina con la pausa (si de todos modos llega un CR o un LF, también termina).</summary>
    Silencio,
}

/// <summary>La paridad del puerto, como la dice eleventa («Ninguna») o Windows («None»).</summary>
public enum ParidadLector { Ninguna, Impar, Par, Marca, Espacio }

/// <summary>El control de flujo del puerto: casi siempre ninguno.</summary>
public enum ControlDeFlujoLector { Ninguno, Hardware, XonXoff }

/// <summary>
/// El lector de códigos de barras por puerto COM (serie) de esta caja: «Lector» en caja.json (piloto alfa, 2026-10-25). En la caja
/// 2 el lector está configurado como puerto COM, como lo tiene eleventa en esa PC (eleventa guarda esto en cada PC, no en su base):
/// el puerto y la velocidad se copian de ahí (docs/pruebas-en-tienda.md). Sin la sección, la caja solo lee el lector como teclado,
/// como siempre; con ella, también.
/// <code>"Lector": { "Puerto": "COM3", "Baudios": 9600, "Paridad": "None", "BitsDeDatos": 8, "BitsDeParada": 1, "Fin": "CR" }</code>
/// Todo menos el puerto es opcional: 9600 baudios, 8 bits, sin paridad, 1 bit de parada (9600 8N1), y el código termina con CR, LF
/// o CRLF, o con una pausa.
/// </summary>
/// <param name="Puerto">«COM3» (también «3», o «com3»).</param>
/// <param name="Baudios">La velocidad («Bits por segundo» en eleventa y en Windows).</param>
/// <param name="Silencio">
/// La pausa que termina un código que llega sin carácter de fin. Dentro de un código no hay pausas: a 9600 baudios cada carácter
/// tarda 1 ms, y por USB llegan juntos (a lo más cada 16 ms). Con un carácter de fin no se espera nada.
/// </param>
/// <param name="Dtr">Avisarle al lector que la caja está lista (DTR). Algunos lectores USB en modo COM no mandan nada sin esto.</param>
/// <param name="Rts">Lo mismo con RTS: un lector que espera permiso para mandar (control de flujo por hardware) manda.</param>
/// <param name="SoloAlFrente">
/// Soltar el puerto mientras la ventana de la caja no está al frente, para que eleventa (abierto en la caja 2 durante el piloto) lo
/// pueda abrir; al volver al frente, la caja lo vuelve a tomar si está libre. Apagado por omisión: sirve solo si eleventa abre el
/// puerto cuando lo necesita y no al arrancar (se ve en el ensayo, docs/pruebas-en-tienda.md).
/// </param>
public sealed record AjustesLector(
    string Puerto, int Baudios = 9600, ParidadLector Paridad = ParidadLector.Ninguna, int BitsDeDatos = 8, decimal BitsDeParada = 1,
    FinDelCodigo Fin = FinDelCodigo.Auto, int Silencio = AjustesLector.SilencioPorOmision, ControlDeFlujoLector ControlDeFlujo = ControlDeFlujoLector.Ninguno,
    bool Dtr = true, bool Rts = true, bool SoloAlFrente = false)
{
    public const int SilencioPorOmision = 100;

    /// <summary>
    /// Lo menos de la pausa: arriba de lo que tarda un adaptador USB en pasar lo que llega (hasta unos 16 ms), para que un código
    /// nunca se parta en dos por eso (revisión del PR #142).
    /// </summary>
    public const int SilencioMinimo = 50;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>«COM3 a 9600 baudios (8N1), el código termina con CR, LF o CRLF, o con una pausa».</summary>
    public string Resumen =>
        $"{Puerto} a {Baudios.ToString("#,##0", Mexico)} baudios ({BitsDeDatos}{Letra(Paridad)}{BitsDeParada.ToString("0.#", CultureInfo.InvariantCulture)}"
        + $"{(ControlDeFlujo == ControlDeFlujoLector.Ninguno ? "" : $", control de flujo {Nombre(ControlDeFlujo)}")}), {FinTexto}";

    /// <summary>«el código termina con CR, LF o CRLF, o con una pausa».</summary>
    public string FinTexto => Fin switch
    {
        FinDelCodigo.CR => "el código termina con CR",
        FinDelCodigo.LF => "el código termina con LF",
        FinDelCodigo.CRLF => "el código termina con CR o LF",
        FinDelCodigo.Tab => "el código termina con un tabulador",
        FinDelCodigo.Silencio => $"el código termina con una pausa de {Silencio} ms",
        _ => "el código termina con CR, LF o CRLF, o con una pausa",
    };

    private static char Letra(ParidadLector paridad) => paridad switch
    {
        ParidadLector.Impar => 'O',
        ParidadLector.Par => 'E',
        ParidadLector.Marca => 'M',
        ParidadLector.Espacio => 'S',
        _ => 'N',
    };

    private static string Nombre(ControlDeFlujoLector control) => control == ControlDeFlujoLector.Hardware ? "por hardware" : "Xon/Xoff";

    /// <summary>
    /// Lee «Lector» de caja.json sin tronar nunca: un error de dedo ahí no impide abrir la caja (la sección se guarda aparte, tal
    /// cual, y se lee aquí). Sin la sección (o con <c>null</c>): sin lector por puerto y sin problema. Con un valor que no se
    /// entiende: sin lector y el problema, que la caja dice arriba y en Configuración › Acerca de › Lector.
    /// </summary>
    public static (AjustesLector? Ajustes, string? Problema) Leer(JsonElement? seccion)
    {
        if (seccion is not { } s || s.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (null, null);
        if (s.ValueKind != JsonValueKind.Object)
            return (null, "«Lector» en caja.json va entre llaves: { \"Puerto\": \"COM3\", \"Baudios\": 9600 }.");
        var campos = s.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        try
        {
            var puerto = NombreDelPuerto(Texto(campos, "Puerto") ?? throw new FormatException("falta «Puerto» (por ejemplo, \"Puerto\": \"COM3\")."));
            var ajustes = new AjustesLector(puerto)
            {
                Baudios = Entero(campos, "Baudios", 9600, 50, 921_600),
                Paridad = Lista(campos, "Paridad", ParidadLector.Ninguna, Paridades),
                BitsDeDatos = Entero(campos, "BitsDeDatos", 8, 5, 8),
                BitsDeParada = Parada(campos),
                Fin = Lista(campos, "Fin", FinDelCodigo.Auto, Fines),
                Silencio = Entero(campos, "Silencio", SilencioPorOmision, SilencioMinimo, 2000),
                ControlDeFlujo = Lista(campos, "ControlDeFlujo", ControlDeFlujoLector.Ninguno, Controles),
                Dtr = SiNo(campos, "Dtr", true),
                Rts = SiNo(campos, "Rts", true),
                SoloAlFrente = SiNo(campos, "SoloAlFrente", false),
            };
            return (ajustes, null);
        }
        catch (FormatException ex)
        {
            return (null, $"«Lector» en caja.json: {ex.Message}");
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or InvalidOperationException)
        {
            // Lo que sea: un error de dedo ahí nunca impide abrir la caja (revisión del PR #142).
            return (null, $"«Lector» en caja.json no se entiende: {ex.Message}");
        }
    }

    /// <summary>«com3», «COM3» o «3» son COM3; otro nombre (en Linux, /dev/ttyUSB0) se queda como está.</summary>
    public static string NombreDelPuerto(string texto)
    {
        var nombre = texto.Trim();
        if (nombre.Length == 0)
            throw new FormatException("«Puerto» está vacío (por ejemplo, \"Puerto\": \"COM3\").");
        if (int.TryParse(nombre, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero is > 0 and < 256)
            return $"COM{numero}";
        if (!nombre.StartsWith("com", StringComparison.OrdinalIgnoreCase) || nombre.Length <= 3 || !nombre[3..].All(char.IsAsciiDigit))
            return nombre;
        // «COM» y un número que no cabe (COM99999999999) no es un puerto: se dice, sin tronar (revisión del PR #142).
        return int.TryParse(nombre[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var com) && com > 0
            ? $"COM{com}"
            : throw new FormatException($"«Puerto» no es un puerto COM de Windows (está «{nombre}»): COM1, COM2, COM3…");
    }

    private static readonly Dictionary<string, ParidadLector> Paridades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = ParidadLector.Ninguna, ["Ninguna"] = ParidadLector.Ninguna, ["Sin"] = ParidadLector.Ninguna, ["N"] = ParidadLector.Ninguna,
        ["Odd"] = ParidadLector.Impar, ["Impar"] = ParidadLector.Impar, ["O"] = ParidadLector.Impar,
        ["Even"] = ParidadLector.Par, ["Par"] = ParidadLector.Par, ["E"] = ParidadLector.Par,
        ["Mark"] = ParidadLector.Marca, ["Marca"] = ParidadLector.Marca, ["M"] = ParidadLector.Marca,
        ["Space"] = ParidadLector.Espacio, ["Espacio"] = ParidadLector.Espacio, ["S"] = ParidadLector.Espacio,
    };

    private static readonly Dictionary<string, FinDelCodigo> Fines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto"] = FinDelCodigo.Auto, ["Automatico"] = FinDelCodigo.Auto, ["Automático"] = FinDelCodigo.Auto,
        ["CR"] = FinDelCodigo.CR, ["Enter"] = FinDelCodigo.CR, ["Retorno"] = FinDelCodigo.CR,
        ["LF"] = FinDelCodigo.LF,
        ["CRLF"] = FinDelCodigo.CRLF, ["CR+LF"] = FinDelCodigo.CRLF, ["CR LF"] = FinDelCodigo.CRLF, ["CR/LF"] = FinDelCodigo.CRLF,
        ["Tab"] = FinDelCodigo.Tab, ["Tabulador"] = FinDelCodigo.Tab,
        ["Silencio"] = FinDelCodigo.Silencio, ["Pausa"] = FinDelCodigo.Silencio, ["Ninguno"] = FinDelCodigo.Silencio, ["Nada"] = FinDelCodigo.Silencio,
    };

    private static readonly Dictionary<string, ControlDeFlujoLector> Controles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = ControlDeFlujoLector.Ninguno, ["Ninguno"] = ControlDeFlujoLector.Ninguno, ["Sin"] = ControlDeFlujoLector.Ninguno,
        ["Hardware"] = ControlDeFlujoLector.Hardware, ["RTS/CTS"] = ControlDeFlujoLector.Hardware, ["RequestToSend"] = ControlDeFlujoLector.Hardware,
        ["XonXoff"] = ControlDeFlujoLector.XonXoff, ["Xon/Xoff"] = ControlDeFlujoLector.XonXoff, ["XOnXOff"] = ControlDeFlujoLector.XonXoff, ["Software"] = ControlDeFlujoLector.XonXoff,
    };

    private static string? Texto(Dictionary<string, JsonElement> campos, string nombre) =>
        campos.TryGetValue(nombre, out var valor) && valor.ValueKind != JsonValueKind.Null
            ? valor.ValueKind == JsonValueKind.String ? valor.GetString() : valor.GetRawText()
            : null;

    private static int Entero(Dictionary<string, JsonElement> campos, string nombre, int omision, int desde, int hasta)
    {
        if (Texto(campos, nombre) is not { } texto)
            return omision;
        if (!int.TryParse(texto.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var numero) || numero < desde || numero > hasta)
            throw new FormatException($"«{nombre}» va de {desde} a {hasta.ToString("#,##0", Mexico)} (está «{texto}»).");
        return numero;
    }

    private static T Lista<T>(Dictionary<string, JsonElement> campos, string nombre, T omision, Dictionary<string, T> valores)
    {
        if (Texto(campos, nombre) is not { } texto)
            return omision;
        return valores.TryGetValue(texto.Trim(), out var valor)
            ? valor
            : throw new FormatException($"«{nombre}» no puede ser «{texto}»: {string.Join(", ", valores.Keys.Take(6).Select(k => $"«{k}»"))}…");
    }

    private static decimal Parada(Dictionary<string, JsonElement> campos)
    {
        if (Texto(campos, "BitsDeParada") is not { } texto)
            return 1;
        return texto.Trim().ToLowerInvariant() switch
        {
            "1" or "one" or "uno" => 1,
            "1.5" or "1,5" or "onepointfive" => 1.5m,
            "2" or "two" or "dos" => 2,
            _ => throw new FormatException($"«BitsDeParada» es 1, 1.5 o 2 (está «{texto}»)."),
        };
    }

    private static bool SiNo(Dictionary<string, JsonElement> campos, string nombre, bool omision)
    {
        if (!campos.TryGetValue(nombre, out var valor) || valor.ValueKind == JsonValueKind.Null)
            return omision;
        return valor.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when valor.GetString()?.Trim().ToLowerInvariant() is "true" or "si" or "sí" => true,
            JsonValueKind.String when valor.GetString()?.Trim().ToLowerInvariant() is "false" or "no" => false,
            _ => throw new FormatException($"«{nombre}» es true o false (está {valor.GetRawText()})."),
        };
    }
}
