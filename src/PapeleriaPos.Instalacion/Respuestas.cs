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

/// <summary>Lo que se pide hacer con la base de la tienda en esta corrida (la página «La base de la tienda»; docs/instalacion.md).</summary>
public enum MudanzaPedida
{
    /// <summary>Nada: la base se queda donde está (o se empieza sin base). Lo de siempre.</summary>
    Ninguna,

    /// <summary>«Mudar la base de esta PC a otra PC»: sacar el paquete de mudanza; el servicio de esta PC queda detenido.</summary>
    Sacar,

    /// <summary>«Traer la base de otra PC»: poner aquí la del paquete de mudanza.</summary>
    Traer,

    /// <summary>«Terminar la mudanza»: la PC de donde salió la base queda como «Otra caja» de la nueva.</summary>
    Terminar,

    /// <summary>«Cancelar la mudanza»: la PC de donde salió la base vuelve a trabajar con ella.</summary>
    Cancelar,
}

/// <summary>
/// Las respuestas del asistente que llegan a servicio.json y a caja.json (el archivo respuestas.txt que deja el asistente en su carpeta
/// temporal; docs/instalacion.md). Nunca traen la clave de eleventa ni el código de Control Print: esos los pone el asistente directo en
/// las variables del servicio.
/// </summary>
public sealed record Respuestas
{
    public required RolDeLaPc Rol { get; init; }

    /// <summary>
    /// La mudanza de la base, si se pidió. Aquí solo se revisa que vaya con lo que necesita: ni <c>escribir</c> ni <c>revisar</c> cambian
    /// nada por ella (lo de la mudanza se pide aparte, con <c>--mudanza</c>).
    /// </summary>
    public MudanzaPedida Mudanza { get; init; }

    /// <summary>
    /// La carpeta del paquete de mudanza: al sacar, donde dejarlo; al traer, donde está (obligatoria); al terminar, donde el asistente vio
    /// su traida.txt (si lo vio).
    /// </summary>
    public string? MudanzaPaquete { get; init; }

    /// <summary>Al traer: «Sí, apartar la de aquí». Sin ella, la base no se trae encima de una PC que ya tiene tienda.</summary>
    public bool MudanzaEncima { get; init; }

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

    /// <summary>Lo que se dice si se pide traer la base de otra PC a una que no va a tener el servicio.</summary>
    public const string TraerEsDeUnaPcConServicio =
        "La base de otra PC solo se trae a la PC que va a tener el servicio: «Caja principal» o «La única caja», no «Otra caja».";

    /// <summary>
    /// Las del archivo del asistente. Lo que no se puede usar se dice (<see cref="RespuestaNoValida"/>, la primera en el orden de las
    /// páginas), en lugar de escribir algo a medias.
    /// </summary>
    public static Respuestas De(ArchivoDeClaves archivo)
    {
        var (respuestas, errores) = Revisar(archivo);
        return errores.Count > 0 ? throw errores[0] : respuestas!;
    }

    /// <summary>
    /// Todas las que no se pueden usar, en el orden de las páginas del asistente, cada una con su clave (la que dice en qué página va); y
    /// las respuestas si no hay ninguna. Es lo que revisa el asistente antes de cambiar nada en la PC (la orden <c>revisar</c>): el
    /// asistente deja pasar lo mismo que esto.
    /// </summary>
    public static (Respuestas? Respuestas, IReadOnlyList<RespuestaNoValida> Errores) Revisar(ArchivoDeClaves archivo)
    {
        var errores = new List<RespuestaNoValida>();
        void Error(string clave, string mensaje) => errores.Add(new RespuestaNoValida(clave, mensaje));

        // «Qué es esta PC».
        RolDeLaPc? rol = (archivo["rol"] ?? "").Trim().ToLowerInvariant() switch
        {
            "principal" => RolDeLaPc.Principal,
            "unica" or "única" => RolDeLaPc.Unica,
            "otra" => RolDeLaPc.Otra,
            _ => null,
        };
        if (rol is null)
            Error("rol", $"«rol» va como principal, unica u otra (está «{(archivo["rol"] ?? "").Trim()}»).");

        // «La base de la tienda» (la mudanza a otra PC): solo si viene. Sin ella no se revisa ni cambia nada, como hasta hoy.
        var loQuePide = (archivo["mudanza"] ?? "").Trim().ToLowerInvariant();
        MudanzaPedida? mudanza = loQuePide switch
        {
            "" => MudanzaPedida.Ninguna,
            "sacar" => MudanzaPedida.Sacar,
            "traer" => MudanzaPedida.Traer,
            "terminar" => MudanzaPedida.Terminar,
            "cancelar" => MudanzaPedida.Cancelar,
            _ => null,
        };
        // La carpeta del paquete: al sacar, donde dejarlo; al traer, donde está; al terminar, donde se vio su traida.txt. Sin mudanza (o
        // al cancelar) no cuenta, aunque venga.
        var mudanzaPaquete = mudanza is MudanzaPedida.Sacar or MudanzaPedida.Traer or MudanzaPedida.Terminar ? Texto(archivo["mudanza-paquete"]) : null;
        var mudanzaEncima = false;
        var conLaBase = rol is RolDeLaPc.Principal or RolDeLaPc.Unica;
        switch (mudanza)
        {
            case null:
                Error("mudanza", $"«mudanza» va como sacar, traer, terminar o cancelar, o vacía (está «{loQuePide}»).");
                break;
            // La base vive con el servicio: sacarla, traerla o cancelar es de una PC que lo lleva.
            case MudanzaPedida.Sacar or MudanzaPedida.Cancelar when rol == RolDeLaPc.Otra:
                Error("mudanza", mudanza == MudanzaPedida.Sacar
                    ? "La base solo se saca para mudarla de una PC que la tiene: «Caja principal» o «La única caja», no «Otra caja»."
                    : "Cancelar la mudanza deja la base en esta PC: va con «Caja principal» o «La única caja», no con «Otra caja».");
                break;
            case MudanzaPedida.Traer:
                if (rol == RolDeLaPc.Otra)
                    Error("mudanza", TraerEsDeUnaPcConServicio);
                if (mudanzaPaquete is null)
                    Error("mudanza-paquete", "Falta la carpeta del paquete de mudanza («mudanza-paquete»): la de la USB donde se sacó.");
                mudanzaEncima = SiNo(archivo, "mudanza-encima", Error) == true;
                break;
            // Al terminar, la PC de donde salió la base deja de tener el servicio.
            case MudanzaPedida.Terminar when conLaBase:
                Error("mudanza", "Terminar la mudanza deja esta PC como «Otra caja» de la PC nueva: va con «Otra caja».");
                break;
        }

        // «Dónde está la caja principal» (solo «Otra caja»).
        var principal = rol == RolDeLaPc.Otra ? Texto(archivo["principal"]) : null;
        if (principal is not null && BusquedaDeLaPrincipal.Direccion(principal) is null)
            Error("principal", $"La dirección de la caja principal no se entiende (está «{principal}»): va su IP, como 192.168.1.10 (con su puerto, de 1 a 65535, si no es el 5080).");
        // Al terminar una mudanza no se busca después: la caja queda apuntando a la PC que ya tiene su base.
        else if (principal is null && rol == RolDeLaPc.Otra && mudanza == MudanzaPedida.Terminar)
            Error("principal", "Para terminar la mudanza hace falta la dirección de la caja principal (la PC a donde se llevó la base): su IP, como 192.168.1.10.");

        // «Las impresoras de esta PC».
        var impresora = (archivo["impresora"] ?? "").Trim();
        // Como la lee la caja (ImpresoraRed.Crear): lo que entiende Uri, con su IP; un puerto de más de 65535 no se entiende.
        if (impresora.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            && !(Uri.TryCreate(impresora, UriKind.Absolute, out var red) && red.Host.Length > 0))
            Error("impresora", $"La impresora de tickets por la red va como tcp://IP:9100 (está «{impresora}»): su IP, y su puerto de 1 a 65535.");

        // «El lector de códigos».
        string? puerto = null;
        var baudios = 9600;
        var lector = (archivo["lector"] ?? "teclado").Trim().ToLowerInvariant();
        if (lector == "com")
        {
            try
            {
                puerto = NombreDelPuerto(archivo["lector-puerto"]);
            }
            catch (FormatException ex)
            {
                Error("lector-puerto", ex.Message);
            }
            if (Texto(archivo["lector-baudios"]) is { } velocidad
                && (!int.TryParse(velocidad, NumberStyles.None, CultureInfo.InvariantCulture, out baudios) || baudios is < 50 or > 921_600))
                Error("lector-baudios", $"La velocidad del lector va de 50 a 921600 baudios (está «{velocidad}»).");
        }
        else if (lector != "teclado")
            Error("lector", $"«lector» va como teclado o com (está «{lector}»).");

        // «Control Print» y «eleventa» (solo con el servicio).
        var conServicio = rol is RolDeLaPc.Principal or RolDeLaPc.Unica;
        bool? controlPrint = null, eleventa = null;
        string? controlPrintServidor = null, eleventaServidor = null, eleventaBase = null, eleventaRespaldos = null;
        int? eleventaCaja = null, eleventaCajero = null;
        if (conServicio)
        {
            controlPrint = SiNo(archivo, "controlprint", Error);
            if (controlPrint == true)
                controlPrintServidor = Servidor(archivo, "controlprint-servidor", "la IP de la PC de Control Print", Error);

            eleventa = SiNo(archivo, "eleventa", Error);
            if (eleventa == true)
            {
                eleventaServidor = Servidor(archivo, "eleventa-servidor", "la IP de la PC de eleventa", Error);
                eleventaBase = Texto(archivo["eleventa-base"]);
                if (eleventaBase is null)
                    Error("eleventa-base", "Falta la ruta de la base de eleventa («eleventa-base»).");
                eleventaCaja = Numero(archivo, "eleventa-caja", "el número de la caja en eleventa", Error);
                eleventaCajero = Numero(archivo, "eleventa-cajero", "el número del cajero en eleventa", Error);
                eleventaRespaldos = Texto(archivo["eleventa-respaldos"]);
                if (eleventaRespaldos is not null && eleventaRespaldos.Contains("onedrive", StringComparison.OrdinalIgnoreCase))
                    Error("eleventa-respaldos", "Los respaldos de eleventa nunca van en OneDrive (son datos de la tienda): elige una carpeta de esta PC.");
            }
        }

        if (rol is not { } elegido || errores.Count > 0)
            return (null, errores);
        return (new Respuestas
        {
            Rol = elegido,
            Mudanza = mudanza ?? MudanzaPedida.Ninguna,
            MudanzaPaquete = mudanzaPaquete,
            MudanzaEncima = mudanzaEncima,
            Principal = principal,
            Impresora = impresora,
            Cola = Texto(archivo["cola"]),
            LectorPuerto = puerto,
            LectorBaudios = baudios,
            ControlPrint = controlPrint,
            ControlPrintServidor = controlPrintServidor,
            Eleventa = eleventa,
            EleventaServidor = eleventaServidor,
            EleventaBase = eleventaBase,
            EleventaCaja = eleventaCaja,
            EleventaCajero = eleventaCajero,
            EleventaGbak = eleventa == true ? Texto(archivo["eleventa-gbak"]) : null,
            EleventaRespaldos = eleventaRespaldos,
        }, errores);
    }

    private static string? Texto(string? valor) => valor?.Trim() is { Length: > 0 } texto ? texto : null;

    /// <summary>«si» o «no»; vacío, no aplica. Otra cosa no se entiende.</summary>
    private static bool? SiNo(ArchivoDeClaves archivo, string clave, Action<string, string> error)
    {
        var valor = archivo.SiNo(clave);
        if (valor is null && Texto(archivo[clave]) is { } texto)
            error(clave, $"«{clave}» va como si o no (está «{texto}»).");
        return valor;
    }

    /// <summary>La IP (o el nombre) de una PC, con su puerto si acaso: obligatoria.</summary>
    private static string? Servidor(ArchivoDeClaves archivo, string clave, string que, Action<string, string> error)
    {
        var texto = Texto(archivo[clave]);
        if (texto is null)
            error(clave, $"Falta {que} («{clave}»).");
        else if (!EsServidor(texto))
            error(clave, $"{char.ToUpperInvariant(que[0])}{que[1..]} no se entiende (está «{texto}»): va su IP, como 192.168.1.10 (con su puerto, de 1 a 65535, si acaso).");
        return texto;
    }

    /// <summary>
    /// La IP o el nombre de una PC, con su puerto si acaso (de 1 a 65535): «192.168.1.10», «192.168.1.10:9100», «caja1». Sin espacios, sin
    /// ruta, sin empezar con «.» ni con «..» en medio (no lo entiende Uri), y sin nada más. Es lo mismo que deja pasar el asistente
    /// (EsDireccion, en papeleria-pos.iss).
    /// </summary>
    public static bool EsServidor(string? texto)
    {
        texto = (texto ?? "").Trim();
        if (texto.Length == 0 || !texto.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')
            || texto.StartsWith('.') || texto.Contains("..", StringComparison.Ordinal))
            return false;
        var dosPuntos = texto.IndexOf(':');
        if (dosPuntos >= 0)
        {
            var puerto = texto[(dosPuntos + 1)..];
            if (dosPuntos == 0 || !int.TryParse(puerto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) || numero is < 1 or > 65535)
                return false;
        }
        return Uri.TryCreate("tcp://" + texto, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
    }

    /// <summary>
    /// Un número de eleventa (la caja, el cajero): con cifras, hasta 9. Vacío o 0 es que todavía falta (como lo trae la plantilla). Lo
    /// mismo que el asistente (NumeroDeEleventa, en papeleria-pos.iss).
    /// </summary>
    private static int? Numero(ArchivoDeClaves archivo, string clave, string que, Action<string, string> error)
    {
        if (Texto(archivo[clave]) is not { } texto)
            return null;
        if (texto.Length > 9 || !int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero))
        {
            error(clave, $"{char.ToUpperInvariant(que[0])}{que[1..]} va con cifras, o vacío si todavía no se sabe (está «{texto}»).");
            return null;
        }
        return numero > 0 ? numero : null;
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

/// <summary>Una respuesta del asistente que no se puede usar: cuál es (su clave en respuestas.txt, la que dice en qué página va) y por qué.</summary>
public sealed class RespuestaNoValida(string clave, string mensaje) : FormatException(mensaje)
{
    public string Clave { get; } = clave;
}
