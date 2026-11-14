using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// La tienda de una base, para reconocerla sin abrirla (la mudanza de la base a otra PC): los nombres de sus sucursales en el orden de
/// api/estado (por nombre), unidos con «; ». Es lo que dice «tienda» en mudanza.txt y en la marca pos.db.mudada. La base no tiene un
/// identificador que dure (el «base» de api/estado es otro en cada arranque del servicio), así que esto es todo lo que se puede comparar
/// desde fuera: una identidad débil, que sirve para negar («esa PC tiene otra tienda») y para ver si otra PC sigue viva con esta tienda.
/// </summary>
public static class NombreDeLaTienda
{
    /// <summary>El de esas sucursales, como lo escribe quien saca el paquete.</summary>
    public static string De(IEnumerable<string> sucursales) => string.Join("; ", sucursales);

    /// <summary>
    /// Si dos textos dicen la misma tienda: recortando los espacios (los de las orillas y los de junto a cada «;») y sin distinguir
    /// mayúsculas. Una tienda sin nombre nunca es «la misma» que otra: sin sucursales no hay con qué comparar.
    /// </summary>
    public static bool EsElMismo(string? uno, string? otro) =>
        Normal(uno) is { Length: > 0 } nombre && string.Equals(nombre, Normal(otro), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string? texto) =>
        string.Join(";", (texto ?? "").Split(';').Select(parte => parte.Trim()).Where(parte => parte.Length > 0));
}

/// <summary>Las versiones del programa («1.0.57», «0.0.0-local»), para saber cuál es más nueva sin depender de cómo se escriban.</summary>
public static class VersionDelPrograma
{
    /// <summary>Los números con que empieza («1.0.57-algo» son 1, 0 y 57), hasta cuatro; vacío si no empieza con números.</summary>
    public static IReadOnlyList<int>? Numeros(string? texto)
    {
        texto = (texto ?? "").Trim();
        var fin = 0;
        while (fin < texto.Length && (char.IsAsciiDigit(texto[fin]) || texto[fin] == '.'))
            fin++;
        var partes = texto[..fin].Split('.');
        if (partes.Length is < 1 or > 4)
            return null;
        var numeros = new List<int>();
        foreach (var parte in partes)
        {
            if (parte.Length is 0 or > 9 || !int.TryParse(parte, NumberStyles.None, CultureInfo.InvariantCulture, out var numero))
                return null;
            numeros.Add(numero);
        }
        return numeros;
    }

    /// <summary>Más de 0 si <paramref name="una"/> es más nueva que <paramref name="otra"/>, 0 si son la misma; vacío si alguna no se entiende.</summary>
    public static int? Comparar(string? una, string? otra)
    {
        if (Numeros(una) is not { } a || Numeros(otra) is not { } b)
            return null;
        for (var i = 0; i < 4; i++)
        {
            var diferencia = (i < a.Count ? a[i] : 0).CompareTo(i < b.Count ? b[i] : 0);
            if (diferencia != 0)
                return diferencia;
        }
        return 0;
    }
}

/// <summary>Una caja de la base que se muda (<c>caja.N.*</c> de mudanza.txt).</summary>
public sealed record CajaMudada(string Nombre, string Serie, bool ConTurnos);

/// <summary>
/// Una entrada de <c>eleventa-cajas</c>: la caja del POS con esa serie escribía allá en eleventa como esa caja (<c>CAJAS.ID</c>), y con
/// ese cajero si su entrada de <c>Eleventa:Cajas</c> traía uno propio.
/// </summary>
public sealed record CajaDelPuenteMudada(string Serie, int Caja, int? Cajero);

/// <summary>
/// Lo que dice <c>mudanza.txt</c>, el resumen del «paquete de mudanza» (docs/instalacion.md): lo escribe el exe del servicio en la PC de
/// donde sale la base (<c>mudanza-sacar</c>) y aquí solo se lee. Es «clave=valor», como todo lo del instalador.
/// <para>
/// De la configuración de la otra PC solo viaja una lista corta de valores del puente con eleventa, nunca su servicio.json: un archivo
/// ajeno no entra a un servicio que corre como SYSTEM. Por eso todo lo que de aquí puede llegar a servicio.json o a una línea de órdenes se
/// revisa al leerlo, y lo que no se entiende se dice (FormatException) en lugar de adivinarlo: el nombre de la base (con él se arma una
/// ruta), el equipo, las series y los ajustes, que son una lista cerrada. Nunca trae una clave.
/// </para>
/// </summary>
public sealed class ResumenDeMudanza
{
    public const string NombreDelArchivo = "mudanza.txt";

    /// <summary>El único formato que hay. Otro lo escribió un programa que este no conoce: no se adivina.</summary>
    public const string Formato = "1";

    /// <summary>Lo que ya no es un mudanza.txt (son unas decenas de renglones): no se carga.</summary>
    private const long TamanoMaximo = 1024 * 1024;

    private const int MuchasCajas = 500;

    private const string PlantillaDelArchivo = "pos-0000-00-00-mudanza-0000.db";

    private enum Tipo
    {
        Texto,
        SiNo,
        Entero,
        Decimal,
    }

    /// <summary>
    /// Los ajustes del puente que viajan en <c>eleventa-ajustes</c>, con su tipo: una lista cerrada (los productos de impresiones y
    /// copias y los plazos). Lo que no está aquí no entra a servicio.json por este camino: nunca una ruta (Gbak, CarpetaRespaldos) ni la clave.
    /// <para>
    /// Los plazos, además, con lo más que pueden valer (<c>Maximo</c>): van tal cual al servicio.json de esta PC, y el servicio los usa en
    /// esperas que .NET no acepta tan largas (más de unos 49 días; <c>DiasDeRespaldos</c>, en fechas): con uno así su lectura o su bandeja
    /// de eleventa se cae al arrancar (y el servicio con ella) o falla cada cobro. Un paquete sacado de una PC cuyo puente trabaja no los
    /// trae: solo uno editado a mano o dañado. Los topes quedan a la mitad o menos de donde truena y dejan pasar cualquier plazo con que
    /// se trabaja de verdad, también el 0 (el servicio lo sube a su mínimo): la mudanza no se detiene por un ajuste que allá servía.
    /// </para>
    /// </summary>
    private static readonly (string Nombre, Tipo Tipo, int Maximo)[] AjustesQueViajan =
    [
        ("ProductoImpresiones", Tipo.Texto, 0), ("ProductoCopias", Tipo.Texto, 0), ("RespaldoObligatorio", Tipo.SiNo, 0),
        ("SegundosAlCobrar", Tipo.Decimal, 1_000_000), ("SegundosEntreEnvios", Tipo.Entero, 1_000_000), ("Minutos", Tipo.Entero, 30_000),
        ("SegundosMaximos", Tipo.Entero, 1_000_000), ("DiasDeRespaldos", Tipo.Entero, 100_000),
    ];

    /// <summary>
    /// Las claves de mudanza.txt que van siempre (aunque vacías), en su orden: las de antes de las cajas, las de cada caja
    /// (<c>caja.N.…</c>, tantas como diga «cajas»), las de después y, al final, un <c>aviso.N</c> por cada uno que diga «avisos».
    /// </summary>
    internal static readonly string[] ClavesAntesDeLasCajas =
    [
        "formato", "paquete", "hecho-utc", "hecho-local", "equipo", "ips", "puerto", "version", "archivo", "bytes", "sha256", "tienda", "productos",
        "ventas", "ultima-venta", "cajas",
    ];

    internal static readonly string[] ClavesDeCadaCaja = ["id", "nombre", "serie", "equipo", "con-llave", "con-turnos", "turno-abierto", "eleventa-caja"];

    internal static readonly string[] ClavesDespuesDeLasCajas =
    [
        "eleventa", "eleventa-escribir", "eleventa-servidor", "eleventa-puerto", "eleventa-base", "eleventa-base-de-la-bandeja", "eleventa-usuario",
        "eleventa-caja", "eleventa-cajero", "eleventa-cajas", "eleventa-ajustes", "eleventa-pendientes", "eleventa-por-revisar", "eleventa-detenido",
        "controlprint", "controlprint-servidor", "avisos",
    ];

    private ResumenDeMudanza(ArchivoDeClaves claves) => Claves = claves;

    /// <summary>El archivo tal cual, para repetirlo (lo que el asistente enseña sale de aquí).</summary>
    public ArchivoDeClaves Claves { get; }

    /// <summary>El identificador de ESTE paquete (el que inventó quien lo sacó): está también en la marca, en traida.txt y en mudanza-puesta.txt.</summary>
    public required string Paquete { get; init; }

    /// <summary>Cuándo se sacó, en la hora de esa PC («2026-11-14 08:15»).</summary>
    public string? HechoLocal { get; init; }

    /// <summary>El nombre de la PC de donde salió la base.</summary>
    public required string Equipo { get; init; }

    /// <summary>Las IP de esa PC y el puerto de su servicio: ahí se le pregunta si volvió a trabajar.</summary>
    public required IReadOnlyList<IPAddress> Ips { get; init; }

    public required int Puerto { get; init; }

    /// <summary>La versión del programa que lo sacó.</summary>
    public required string Version { get; init; }

    /// <summary>El nombre del archivo de la base dentro de la carpeta (<c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>), su tamaño y su SHA-256.</summary>
    public required string Archivo { get; init; }

    public required long Bytes { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>La tienda de esa base (<see cref="NombreDeLaTienda"/>).</summary>
    public required string Tienda { get; init; }

    public string? Productos { get; init; }

    public string? Ventas { get; init; }

    public string? UltimaVenta { get; init; }

    public required IReadOnlyList<CajaMudada> Cajas { get; init; }

    /// <summary>Si allá se leía eleventa (<c>Eleventa:Activo</c>) y si se le mandaba cada venta (<c>Eleventa:Escribir</c>).</summary>
    public required bool Eleventa { get; init; }

    public required bool EleventaEscribir { get; init; }

    /// <summary>Si allá había puente: leía o escribía (lo mismo que <c>leer</c> llama «eleventa» en esta PC).</summary>
    public bool HabiaPuente => Eleventa || EleventaEscribir;

    public string? EleventaServidor { get; init; }

    public required int EleventaPuerto { get; init; }

    /// <summary>La ruta de la base de eleventa en la configuración de allá, y la que tiene anotada la bandeja que viaja en la base.</summary>
    public string? EleventaBase { get; init; }

    public string? EleventaBaseDeLaBandeja { get; init; }

    public required string EleventaUsuario { get; init; }

    /// <summary>El cajero de omisión de allá. (La caja de omisión de allá no se usa nunca: la de esta PC es solo la que aquí se conteste.)</summary>
    public int? EleventaCajero { get; init; }

    /// <summary>Lo que debe quedar en <c>Eleventa:Cajas</c>: cada caja que ya vendió, con el número con que escribía allá.</summary>
    public required IReadOnlyList<CajaDelPuenteMudada> EleventaCajas { get; init; }

    /// <summary>Los ajustes del puente que allá no estaban en su valor de siempre, ya con su tipo de JSON.</summary>
    public required IReadOnlyList<(string Nombre, JsonNode Valor)> EleventaAjustes { get; init; }

    public required int EleventaPendientes { get; init; }

    public required int EleventaPorRevisar { get; init; }

    public bool ControlPrint { get; init; }

    public string? ControlPrintServidor { get; init; }

    /// <summary>«Caja 2» (serie B): la caja de esa serie como la conoce el dueño; si el resumen no la trae, solo su serie.</summary>
    public string NombreDeLaSerie(string serie) =>
        Cajas.FirstOrDefault(c => string.Equals(c.Serie, serie, StringComparison.OrdinalIgnoreCase)) is { } caja
            ? $"«{caja.Nombre}» (serie {caja.Serie})"
            : $"la caja de la serie {serie}";

    /// <summary>
    /// El mudanza.txt de esa carpeta. Si no se puede leer, IOException; si no se entiende, FormatException con lo que le pasa (sin su
    /// contenido).
    /// </summary>
    public static ResumenDeMudanza DeLaCarpeta(string carpeta) => De(LeerClaves(Path.Combine(carpeta, NombreDelArchivo)));

    /// <summary>
    /// Cómo se dice que el mudanza.txt de esa carpeta no se entiende, con lo que le pasa (el mensaje de la FormatException de
    /// <see cref="De"/> o de <see cref="LeerClaves"/>, que nunca trae su contenido).
    /// </summary>
    public static string NoSeEntiende(string carpeta, FormatException porQue) =>
        $"El paquete de mudanza de {carpeta} no se entiende ({NombreDelArchivo}: {porQue.Message}).";

    /// <summary>El archivo como «clave=valor», todavía sin revisar (para ver de qué versión es antes de entenderlo todo).</summary>
    public static ArchivoDeClaves LeerClaves(string ruta)
    {
        if (new FileInfo(ruta) is { Exists: true, Length: > TamanoMaximo })
            throw new FormatException("es demasiado grande para ser el de un paquete");
        return ArchivoDeClaves.Leer(ruta);
    }

    public static ResumenDeMudanza De(ArchivoDeClaves claves)
    {
        string? Dato(string clave) => claves[clave]?.Trim() is { Length: > 0 } texto ? texto : null;
        string Obligatorio(string clave) => Dato(clave) ?? throw new FormatException($"le falta «{clave}»");
        bool SiNo(string clave) => claves.SiNo(clave) ?? throw new FormatException($"«{clave}» va como si o no");
        int Entero(string clave, int minimo, int maximo, int? siFalta = null)
        {
            if (Dato(clave) is not { } texto)
                return siFalta ?? throw new FormatException($"le falta «{clave}»");
            return texto.Length <= 9 && int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero >= minimo && numero <= maximo
                ? numero
                : throw new FormatException($"«{clave}» no es un número que se pueda usar");
        }

        if (Obligatorio("formato") != Formato)
            throw new FormatException($"es de un formato que este instalador no conoce (el {Obligatorio("formato")})");
        // Quien lo saca escribe todas sus claves (aunque vayan vacías) y de un golpe, con los avisos al final: si falta una, el archivo
        // quedó cortado (una USB que falla) y no se sabe qué más le falta. Las de cada caja y cada aviso, tantas como dice.
        void QueEste(string clave)
        {
            if (claves[clave] is null)
                throw new FormatException($"le falta «{clave}»");
        }
        foreach (var clave in ClavesAntesDeLasCajas.Concat(ClavesDespuesDeLasCajas))
            QueEste(clave);
        var (cuantasCajas, cuantosAvisos) = (Entero("cajas", 0, MuchasCajas, siFalta: 0), Entero("avisos", 0, MuchasCajas, siFalta: 0));
        for (var i = 1; i <= cuantasCajas; i++)
            foreach (var dato in ClavesDeCadaCaja)
                QueEste($"caja.{i}.{dato}");
        for (var i = 1; i <= cuantosAvisos; i++)
            QueEste($"aviso.{i}");
        var paquete = Obligatorio("paquete");
        if (!Guid.TryParseExact(paquete, "N", out _))
            throw new FormatException("«paquete» no es el identificador de un paquete");
        // Con el nombre del archivo se arma una ruta (y va a la línea de órdenes de restaurar.ps1): solo el nombre que pone quien lo saca.
        var archivo = Obligatorio("archivo");
        if (!EsElNombreDeLaBase(archivo))
            throw new FormatException($"«archivo» no es el nombre de la base de un paquete (va como {PlantillaDelArchivo.Replace('0', '#')})");
        var bytes = Dato("bytes") is { Length: <= 18 } tamano && long.TryParse(tamano, NumberStyles.None, CultureInfo.InvariantCulture, out var cuantos) && cuantos > 0
            ? cuantos
            : throw new FormatException("«bytes» no es el tamaño de la base");
        var sha256 = Obligatorio("sha256");
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new FormatException("«sha256» no es un SHA-256");
        // El equipo también va a una línea de órdenes (restaurar.ps1 -PorMudanza): un nombre de PC, sin espacios ni comillas.
        var equipo = Obligatorio("equipo");
        if (equipo.Length > 63 || !equipo.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new FormatException("«equipo» no es el nombre de una PC");
        var version = Obligatorio("version");
        if (VersionDelPrograma.Numeros(version) is null)
            throw new FormatException("«version» no es una versión del programa");

        var cajas = new List<CajaMudada>();
        for (var i = 1; i <= cuantasCajas; i++)
            cajas.Add(new CajaMudada(Dato($"caja.{i}.nombre") ?? $"Caja {i}", Dato($"caja.{i}.serie") ?? "", claves.SiNo($"caja.{i}.con-turnos") == true));

        var usuario = Dato("eleventa-usuario") ?? UsuarioDeSiempre;
        if (usuario.Length > 63)
            throw new FormatException("«eleventa-usuario» no es un usuario de Firebird");
        return new ResumenDeMudanza(claves)
        {
            Paquete = paquete,
            HechoLocal = Dato("hecho-local"),
            Equipo = equipo,
            Ips = Direcciones(Dato("ips")),
            Puerto = Entero("puerto", 1, 65535, siFalta: BusquedaDeLaPrincipal.PuertoDelServicio),
            Version = version,
            Archivo = archivo,
            Bytes = bytes,
            Sha256 = sha256.ToLowerInvariant(),
            Tienda = Obligatorio("tienda"),
            Productos = Dato("productos"),
            Ventas = Dato("ventas"),
            UltimaVenta = Dato("ultima-venta"),
            Cajas = cajas,
            Eleventa = SiNo("eleventa"),
            EleventaEscribir = SiNo("eleventa-escribir"),
            EleventaServidor = Dato("eleventa-servidor"),
            EleventaPuerto = Entero("eleventa-puerto", 1, 65535, siFalta: PuertoDeSiempre),
            EleventaBase = Dato("eleventa-base"),
            EleventaBaseDeLaBandeja = Dato("eleventa-base-de-la-bandeja"),
            EleventaUsuario = usuario,
            // Un 0 es que falta, como en servicio.json.
            EleventaCajero = Entero("eleventa-cajero", 0, 999_999_999, siFalta: 0) is > 0 and var cajero ? cajero : null,
            EleventaCajas = CajasDelPuente(Dato("eleventa-cajas")),
            EleventaAjustes = Ajustes(Dato("eleventa-ajustes")),
            EleventaPendientes = Entero("eleventa-pendientes", 0, 999_999_999, siFalta: 0),
            EleventaPorRevisar = Entero("eleventa-por-revisar", 0, 999_999_999, siFalta: 0),
            ControlPrint = claves.SiNo("controlprint") == true,
            ControlPrintServidor = Dato("controlprint-servidor"),
        };
    }

    /// <summary>El puerto y el usuario de Firebird de siempre (los de <c>OpcionesEleventa</c>): los que no hace falta escribir.</summary>
    public const int PuertoDeSiempre = 3050;

    public const string UsuarioDeSiempre = "SYSDBA";

    /// <summary>Solo <c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>, sin carpetas ni nada más: es un archivo de la carpeta del paquete.</summary>
    public static bool EsElNombreDeLaBase(string nombre)
    {
        if (nombre.Length != PlantillaDelArchivo.Length)
            return false;
        for (var i = 0; i < nombre.Length; i++)
            if (PlantillaDelArchivo[i] == '0' ? !char.IsAsciiDigit(nombre[i]) : nombre[i] != PlantillaDelArchivo[i])
                return false;
        return true;
    }

    /// <summary>
    /// Las IPv4 de una lista «ip;ip» (como <c>ips</c> de mudanza.txt y de traida.txt). Lo que no es una IPv4 escrita con números no
    /// cuenta: de un archivo que llega en una USB nunca se saca un nombre al que conectarse.
    /// </summary>
    public static IReadOnlyList<IPAddress> Direcciones(string? texto) =>
        (texto ?? "").Split(';', ',')
            .Select(parte => parte.Trim())
            .Where(parte => parte.Length > 0 && parte.All(c => char.IsAsciiDigit(c) || c == '.') && parte.Count(c => c == '.') == 3)
            .Select(parte => IPAddress.TryParse(parte, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null)
            .OfType<IPAddress>()
            .Distinct()
            .Take(16)
            .ToList();

    /// <summary>«B=3» o «B=3/1», separadas con «;»: la serie, su caja de eleventa y, si acaso, su cajero.</summary>
    private static List<CajaDelPuenteMudada> CajasDelPuente(string? texto)
    {
        var cajas = new List<CajaDelPuenteMudada>();
        foreach (var entrada in (texto ?? "").Split(';').Select(e => e.Trim()).Where(e => e.Length > 0))
        {
            var igual = entrada.IndexOf('=');
            var serie = igual > 0 ? entrada[..igual].Trim() : "";
            var numeros = igual > 0 ? entrada[(igual + 1)..].Split('/') : [];
            // La serie va a ser el nombre de una sección de servicio.json: letras y números (con «:» sería otra sección).
            if (serie.Length is 0 or > 10 || !serie.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') || numeros.Length is < 1 or > 2
                || NumeroDeEleventa(numeros[0]) is not { } caja)
                throw new FormatException($"«eleventa-cajas» no se entiende (en «{entrada}»): va como serie=caja o serie=caja/cajero");
            // El cajero propio de esa caja, si su entrada de allá lo traía. Un 0 es que no lo trae, como en servicio.json.
            int? cajero = null;
            if (numeros.Length == 2 && numeros[1].Trim() is var cifras && !(cifras.Length is > 0 and <= 9 && cifras.All(c => c == '0')))
                cajero = NumeroDeEleventa(cifras) ?? throw new FormatException($"«eleventa-cajas» no se entiende (en «{entrada}»): el cajero va con cifras");
            var nueva = new CajaDelPuenteMudada(serie, caja, cajero);
            // Dos sucursales pueden tener una caja con la misma serie: es la misma entrada de Eleventa:Cajas, si dice lo mismo.
            if (cajas.FirstOrDefault(c => string.Equals(c.Serie, serie, StringComparison.OrdinalIgnoreCase)) is { } repetida)
            {
                if (repetida with { Serie = serie } != nueva)
                    throw new FormatException($"«eleventa-cajas» trae dos veces la serie {serie}, con números distintos");
                continue;
            }
            cajas.Add(nueva);
        }
        return cajas;
    }

    private static int? NumeroDeEleventa(string texto) =>
        texto.Trim() is { Length: > 0 and <= 9 } cifras && int.TryParse(cifras, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero > 0
            ? numero
            : null;

    /// <summary>«Minutos=10;ProductoCopias=COPIA»: solo los de la lista cerrada, cada uno con el tipo con que lo lee el servicio.</summary>
    private static List<(string Nombre, JsonNode Valor)> Ajustes(string? texto)
    {
        var ajustes = new List<(string Nombre, JsonNode Valor)>();
        foreach (var entrada in (texto ?? "").Split(';').Select(e => e.Trim()).Where(e => e.Length > 0))
        {
            var igual = entrada.IndexOf('=');
            var nombre = igual > 0 ? entrada[..igual].Trim() : entrada;
            var valor = igual > 0 ? entrada[(igual + 1)..].Trim() : "";
            var conocido = AjustesQueViajan.FirstOrDefault(a => string.Equals(a.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
            if (igual <= 0 || conocido.Nombre is null)
                throw new FormatException($"«eleventa-ajustes» trae «{nombre}», que no es un ajuste del puente que viaje en una mudanza");
            JsonNode? nodo = conocido.Tipo switch
            {
                Tipo.Texto => JsonValue.Create(valor),
                Tipo.SiNo => valor.ToLowerInvariant() switch
                {
                    "si" or "sí" or "true" or "1" => JsonValue.Create(true),
                    "no" or "false" or "0" => JsonValue.Create(false),
                    _ => null,
                },
                Tipo.Entero => valor.Length <= 9 && int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var entero) ? JsonValue.Create(entero) : null,
                _ => valor.Length <= 9 && int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var sinDecimales)
                    ? JsonValue.Create(sinDecimales)
                    : valor.Length <= 20 && double.TryParse(valor, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var conDecimales) && double.IsFinite(conDecimales)
                        ? JsonValue.Create(conDecimales)
                        : null,
            };
            if (nodo is null)
                throw new FormatException($"«eleventa-ajustes» trae «{conocido.Nombre}» con un valor que no se entiende");
            // Los plazos (ya se leyeron sin signo, con cifras y si acaso un punto decimal): de 0 a su máximo.
            if (conocido.Tipo is Tipo.Entero or Tipo.Decimal
                && !(double.TryParse(valor, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cuanto) && cuanto <= conocido.Maximo))
                throw new FormatException(
                    $"«eleventa-ajustes» trae «{conocido.Nombre}» con un valor que el servicio no puede usar (va de 0 a {conocido.Maximo.ToString(CultureInfo.InvariantCulture)})");
            ajustes.RemoveAll(a => a.Nombre == conocido.Nombre);
            ajustes.Add((conocido.Nombre, nodo));
        }
        return ajustes;
    }

    /// <summary>
    /// El párrafo que enseña el asistente antes de traerla: de qué equipo es, cuándo se sacó, la tienda, cuánto trae, sus cajas y cómo llega
    /// el puente con eleventa.
    /// </summary>
    public string Texto()
    {
        var cajas = Cajas.Count == 0
            ? "Sin cajas."
            : $"{(Cajas.Count == 1 ? "Caja" : "Cajas")}: {string.Join(", ", Cajas.Select(c => $"{c.Nombre} (serie {c.Serie})"))}.";
        static string Cuantos(string? numero, string uno, string varios) => $"{numero ?? "0"} {(numero == "1" ? uno : varios)}";
        return $"La base de {Equipo}, sacada el {HechoLocal ?? "(no dice cuándo)"}. Tienda: {Tienda}. {Cuantos(Productos, "producto", "productos")} y "
            + $"{Cuantos(Ventas, "venta", "ventas")}{(UltimaVenta is null ? "" : $" (la última, el {UltimaVenta})")}. {cajas} {ComoLlegaElPuente()}";
    }

    /// <summary>Si el puente con eleventa llega escribiendo (y cada caja como cuál de allá) o sin escribir: llega como estaba.</summary>
    public string ComoLlegaElPuente()
    {
        if (EleventaEscribir)
        {
            var cadaCaja = EleventaCajas.Count == 0
                ? "ninguna caja tiene todavía su caja de eleventa (lo que se cobre espera en la bandeja)"
                : string.Join("; ", EleventaCajas.Select(c => $"lo que cobre {NombreDeLaSerie(c.Serie)} entra a eleventa como la caja {c.Caja}"));
            return $"El puente llega ESCRIBIENDO en {EleventaBase ?? "(falta la base)"}: {cadaCaja}.";
        }
        return Eleventa
            ? "El puente llega SIN escribir: lo que se cobre en el POS no llega a eleventa hasta que tú lo prendas."
            : "Allá no había puente con eleventa.";
    }
}

/// <summary>Cómo está una carpeta que debería tener un paquete de mudanza (lo que dice <c>mudanza-ver</c> en «paquete»).</summary>
public enum EstadoDelPaquete
{
    /// <summary>Completo: su resumen se entiende y la base está, con su tamaño.</summary>
    Bien,

    /// <summary>No existe la carpeta, o no tiene ni mudanza.txt ni la base de un paquete: ahí no se ha sacado nada.</summary>
    NoHay,

    /// <summary>
    /// Falta la base, no mide lo que debe o mudanza.txt no se entiende; o está la base sin mudanza.txt, que se escribe al final: se quedó a
    /// medias al sacarlo.
    /// </summary>
    Incompleto,

    /// <summary>Ya se trajo: está el traida.txt de este paquete y su base ya no (el instalador la borra al quedar bien la PC nueva).</summary>
    YaTraido,

    /// <summary>Lo sacó una versión del programa más nueva que la de este instalador.</summary>
    OtraVersion,
}

/// <summary>
/// El «paquete de mudanza» (docs/instalacion.md): la carpeta en que viaja la base de la tienda de una PC a otra, por omisión
/// <c>mudanza\</c> junto al instalador. Trae la base (<c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>, un respaldo), <c>mudanza.txt</c>
/// (<see cref="ResumenDeMudanza"/>) y, cuando la PC nueva ya la trajo, <c>traida.txt</c>. El ayudante no abre la base (no lleva SQLite):
/// solo sabe su tamaño y su SHA-256.
/// </summary>
public sealed class PaqueteDeMudanza
{
    public const string ArchivoDeTraida = "traida.txt";

    private PaqueteDeMudanza(string carpeta, EstadoDelPaquete estado, string? error, ResumenDeMudanza? resumen = null)
    {
        Carpeta = carpeta;
        Estado = estado;
        Error = error;
        Resumen = resumen;
    }

    public string Carpeta { get; }

    public EstadoDelPaquete Estado { get; }

    /// <summary>Por qué no está bien, dicho para el dueño; vacío si lo está.</summary>
    public string? Error { get; }

    /// <summary>
    /// Lo que dice su mudanza.txt, si se entendió: siempre con <see cref="EstadoDelPaquete.Bien"/>, y también cuando lo que falla es la
    /// base (falta, no mide lo que debe o ya se trajo), para decir de qué paquete se habla.
    /// </summary>
    public ResumenDeMudanza? Resumen { get; }

    /// <summary>Como lo dice <c>mudanza-ver</c>: bien, no-hay, incompleto, ya-traido u otra-version.</summary>
    public string NombreDelEstado => Estado switch
    {
        EstadoDelPaquete.Bien => "bien",
        EstadoDelPaquete.NoHay => "no-hay",
        EstadoDelPaquete.YaTraido => "ya-traido",
        EstadoDelPaquete.OtraVersion => "otra-version",
        _ => "incompleto",
    };

    /// <summary>La carpeta como llega de una línea de órdenes: sin espacios de más ni la comilla que deja «"E:\mudanza\"».</summary>
    public static string Limpia(string? carpeta) => (carpeta ?? "").Trim().Trim('"').Trim();

    /// <summary>
    /// Cómo está el paquete de esa carpeta, sin leer la base entera (para la página del asistente: es rápido). Con
    /// <paramref name="versionDelInstalador"/>, además, que no lo haya sacado un programa más nuevo: su base traería cosas que el de
    /// esta PC no conoce, y el servicio no arrancaría con ella.
    /// </summary>
    public static PaqueteDeMudanza Ver(string? carpeta, string? versionDelInstalador = null)
    {
        var limpia = Limpia(carpeta);
        if (limpia.Length == 0)
            return new(limpia, EstadoDelPaquete.NoHay, "Falta la carpeta del paquete de mudanza.");
        try
        {
            if (!Directory.Exists(limpia))
                return new(limpia, EstadoDelPaquete.NoHay,
                    $"No está la carpeta {limpia}: conecta la USB donde se sacó el paquete de mudanza (o elige la carpeta donde quedó).");
            var rutaDelResumen = Path.Combine(limpia, ResumenDeMudanza.NombreDelArchivo);
            if (!File.Exists(rutaDelResumen))
            {
                // mudanza.txt es lo último que se escribe al sacarlo (la marca va antes): con la base y sin él, el paquete quedó a medias y la
                // PC de donde salió pudo quedar con la marca, con la que el instalador allá solo ofrece terminar o cancelar.
                if (BaseSinSuResumen(limpia) is { } suelta)
                    return new(limpia, EstadoDelPaquete.Incompleto,
                        $"En {limpia} no hay un paquete de mudanza completo: está la base ({suelta}), pero no su {ResumenDeMudanza.NombreDelArchivo}, que es lo "
                        + "último que se escribe al sacarlo (¿se fue la luz o se quitó la USB mientras se sacaba?). Para sacarlo otra vez: en la PC de donde se "
                        + $"sacó, corre este instalador; si ofrece «Cancelar la mudanza», elígela primero y córrelo otra vez. Después elige «{OpcionDeSacar}».");
                return new(limpia, EstadoDelPaquete.NoHay,
                    $"En {limpia} no hay un paquete de mudanza. Se saca en la PC que hoy tiene la base: corre ahí este instalador y elige «{OpcionDeSacar}».");
            }
            var claves = ResumenDeMudanza.LeerClaves(rutaDelResumen);
            // Antes de entenderlo todo: el de un programa más nuevo puede venir escrito de otra forma, y lo que hay que decir es eso.
            var suVersion = claves["version"]?.Trim();
            if (versionDelInstalador?.Trim() is { Length: > 0 } laMia && VersionDelPrograma.Comparar(suVersion, laMia) is > 0)
                return new(limpia, EstadoDelPaquete.OtraVersion,
                    $"Este paquete lo sacó la versión {suVersion} del programa, más nueva que la de este instalador ({laMia}). Usa aquí el instalador de la "
                    + $"versión {suVersion} (el mismo con que se sacó) o uno más nuevo.");
            var resumen = ResumenDeMudanza.De(claves);
            var laBase = new FileInfo(Path.Combine(limpia, resumen.Archivo));
            if (!laBase.Exists)
            {
                if (Traida(limpia, resumen.Paquete) is { } traida)
                    return new(limpia, EstadoDelPaquete.YaTraido,
                        $"Este paquete ya se trajo{(Dato(traida, "equipo") is { } nueva ? $" a {nueva}" : "")}{(Dato(traida, "cuando") is { } cuando ? $" ({cuando})" : "")}: "
                        + $"su base ya no está en {limpia} (quedó en esa PC). Lo que falta es terminar la mudanza en {resumen.Equipo}: corre ahí este instalador.",
                        resumen);
                return new(limpia, EstadoDelPaquete.Incompleto,
                    $"Al paquete de mudanza de {limpia} le falta la base ({resumen.Archivo}): quedó incompleto. {ComoSacarloOtraVez(resumen.Equipo)}", resumen);
            }
            if (laBase.Length != resumen.Bytes)
                return new(limpia, EstadoDelPaquete.Incompleto,
                    $"La base del paquete de mudanza ({resumen.Archivo}) mide {laBase.Length.ToString(CultureInfo.InvariantCulture)} bytes y debería medir "
                    + $"{resumen.Bytes.ToString(CultureInfo.InvariantCulture)}: la copia quedó incompleta (¿se quitó la USB antes de tiempo?). {ComoSacarloOtraVez(resumen.Equipo)}",
                    resumen);
            return new(limpia, EstadoDelPaquete.Bien, null, resumen);
        }
        catch (FormatException ex)
        {
            return new(limpia, EstadoDelPaquete.Incompleto, $"{ResumenDeMudanza.NoSeEntiende(limpia, ex)} Sácalo otra vez en la PC de donde viene.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // También una carpeta que Windows no entiende (un nombre con caracteres que no van): no hay paquete que se pueda leer ahí.
            return new(limpia, EstadoDelPaquete.Incompleto, $"No se pudo leer el paquete de mudanza de {limpia}: {ex.Message}");
        }
    }

    /// <summary>
    /// La base del paquete, leída entera: si es la que se sacó (su SHA-256). Vacío si lo es; si no, por qué, dicho para el dueño. Solo con
    /// el paquete <see cref="EstadoDelPaquete.Bien"/>.
    /// </summary>
    public string? ProblemaDeLaBase()
    {
        var resumen = Resumen ?? throw new InvalidOperationException("El paquete no está bien: no hay base que revisar.");
        string huella;
        try
        {
            using var laBase = new FileStream(Path.Combine(Carpeta, resumen.Archivo), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            huella = Convert.ToHexString(SHA256.HashData(laBase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"No se pudo leer la base del paquete de mudanza ({resumen.Archivo}): {ex.Message}";
        }
        return string.Equals(huella, resumen.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"La base del paquete de mudanza ({resumen.Archivo}) no es la que se sacó de {resumen.Equipo}: se dañó al copiarse (su SHA-256 no cuadra). "
                + ComoSacarloOtraVez(resumen.Equipo);
    }

    /// <summary>
    /// El traida.txt de esa carpeta, si es de ese paquete: lo deja la PC nueva al quedar bien (su equipo, sus IP y su puerto: con eso la
    /// PC de donde salió la base sabe a dónde conectarse al terminar). Vacío si no está o es de otro paquete.
    /// </summary>
    public static ArchivoDeClaves? Traida(string carpeta, string paquete)
    {
        var ruta = Path.Combine(carpeta, ArchivoDeTraida);
        if (!File.Exists(ruta) || new FileInfo(ruta).Length > 64 * 1024)
            return null;
        var traida = ArchivoDeClaves.Leer(ruta);
        return string.Equals(traida["paquete"]?.Trim(), paquete, StringComparison.OrdinalIgnoreCase) ? traida : null;
    }

    private static string? Dato(ArchivoDeClaves archivo, string clave) => archivo[clave]?.Trim() is { Length: > 0 } texto ? texto : null;

    /// <summary>
    /// El nombre de la base de un paquete (<c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>) que haya en esa carpeta sin su mudanza.txt, la más
    /// reciente; vacío si no hay o si la carpeta no se deja leer (entonces se dice, como siempre, que ahí no hay paquete).
    /// </summary>
    private static string? BaseSinSuResumen(string carpeta)
    {
        try
        {
            return Directory.EnumerateFiles(carpeta).Select(Path.GetFileName).OfType<string>().Where(ResumenDeMudanza.EsElNombreDeLaBase)
                .Order(StringComparer.Ordinal).LastOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Como se llama en la página «La base de la tienda» la opción que saca el paquete (papeleria-pos.iss).</summary>
    private const string OpcionDeSacar = "Mudar la base de esta PC a otra PC";

    /// <summary>Con la marca puesta allá, «sacar» se niega: primero se cancela, y el paquete se saca en otra corrida del instalador.</summary>
    private static string ComoSacarloOtraVez(string equipo) =>
        $"Para sacarlo otra vez: en {equipo}, corre este instalador y elige «Cancelar la mudanza»; después córrelo otra vez y elige «{OpcionDeSacar}».";
}

/// <summary>
/// La marca <c>pos.db.mudada</c> que deja <c>mudanza-sacar</c> junto a la base de la PC de donde salió: mientras exista, el servicio de
/// esa PC no arranca, y el instalador solo ofrece ahí terminar la mudanza o cancelarla. La escribe el exe del servicio (JSON); aquí solo
/// se lee, para decirle al asistente cuándo se sacó y a dónde.
/// </summary>
public sealed record MarcaDeMudanza(string? Paquete, string? Tienda, DateTimeOffset? Cuando, string? Carpeta)
{
    public const string NombreDelArchivo = "pos.db.mudada";

    public static string Ruta(string datos) => Path.Combine(datos, NombreDelArchivo);

    /// <summary>Cuándo se sacó, en la hora de esta PC («2026-11-14 08:15»), como lo enseña el asistente.</summary>
    public string? CuandoLocal => Cuando?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// La de esta PC; vacía si no hay. Lo que cuenta es que el archivo exista (así lo ven también el servicio y los scripts): si no se
    /// puede leer o no se entiende, la PC sigue «mudada» y solo faltan sus datos.
    /// </summary>
    public static MarcaDeMudanza? Leer(string datos)
    {
        var ruta = Ruta(datos);
        if (!File.Exists(ruta))
            return null;
        try
        {
            if (new FileInfo(ruta).Length <= 64 * 1024 && JsonNode.Parse(Json.SinBom(File.ReadAllBytes(ruta)), null, Json.ConComentarios) is JsonObject marca)
            {
                string? Texto(string nombre) => Json.Texto(Json.Valor(marca, nombre))?.Trim() is { Length: > 0 } texto ? texto : null;
                return new MarcaDeMudanza(Texto("paquete"), Texto("tienda"),
                    DateTimeOffset.TryParse(Texto("cuandoUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cuando) ? cuando : null,
                    Texto("carpeta"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Existe y no se deja leer o no es JSON: mudada, sin sus datos.
        }
        return new MarcaDeMudanza(null, null, null, null);
    }
}

/// <summary>
/// La nota <c>mudanza-puesta.txt</c> que deja instalar-pc.ps1 junto a la base de la PC nueva en cuanto la base de un paquete de mudanza
/// ya es su <c>pos.db</c> («clave=valor»: <c>paquete</c>, <c>equipo</c>, <c>archivo</c>, <c>sha256</c>, <c>cuando</c>). Con ella, volver a
/// correr el instalador con el mismo paquete no trae la base otra vez: solo termina lo que faltó. Aquí solo se lee.
/// </summary>
public static class NotaDeLaBasePuesta
{
    public const string NombreDelArchivo = "mudanza-puesta.txt";

    public static string Ruta(string datos) => Path.Combine(datos, NombreDelArchivo);

    /// <summary>
    /// El paquete que dice la nota de esta PC; vacío si no hay nota o no lo dice. Si está y no se deja leer, IOException o
    /// UnauthorizedAccessException: quien decide con ella (<c>mudanza-revisar</c>) no adivina.
    /// </summary>
    public static string? Paquete(string datos)
    {
        var ruta = Ruta(datos);
        if (!File.Exists(ruta) || new FileInfo(ruta).Length > 64 * 1024)
            return null;
        return ArchivoDeClaves.Leer(ruta)["paquete"]?.Trim() is { Length: > 0 } paquete ? paquete : null;
    }
}
