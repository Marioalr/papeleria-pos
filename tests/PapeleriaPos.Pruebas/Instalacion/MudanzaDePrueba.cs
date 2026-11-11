using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Un paquete de mudanza hecho a mano, como lo deja <c>mudanza-sacar</c> (el exe del servicio) en la carpeta de la USB: la base (aquí,
/// unos bytes cualesquiera: el ayudante no la abre, solo la mide y saca su SHA-256) y <c>mudanza.txt</c> con todas sus claves, en su
/// orden. Por omisión es el de la tienda: la caja 2 (serie B), con el puente escribiendo en eleventa como la caja 3.
/// </summary>
internal sealed class PaqueteDePrueba
{
    public const string Id = "0f8fad5bd9cb469fa16570867728950e";
    public const string Archivo = "pos-2026-11-14-mudanza-0815.db";
    public const string BaseDeEleventa = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB";

    /// <summary>De documentación (TEST-NET): la PC de donde salió la base. Nunca se le pregunta de verdad: la red es la de la prueba.</summary>
    public const string IpDelOrigen = "192.0.2.77";

    private PaqueteDePrueba(string carpeta, List<(string Clave, string Valor)> claves) => (Carpeta, Claves) = (carpeta, claves);

    public string Carpeta { get; }

    /// <summary>Lo que quedó escrito en mudanza.txt, en su orden.</summary>
    public IReadOnlyList<(string Clave, string Valor)> Claves { get; }

    public string RutaDeLaBase => Path.Combine(Carpeta, Archivo);

    public string RutaDelResumen => Path.Combine(Carpeta, ResumenDeMudanza.NombreDelArchivo);

    public string this[string clave] => Claves.Last(c => c.Clave == clave).Valor;

    /// <summary>
    /// El paquete en esa carpeta. Cada cambio pone el valor de una clave de mudanza.txt (o la agrega al final); con el valor vacío
    /// (null), la quita.
    /// </summary>
    public static PaqueteDePrueba En(string carpeta, params (string Clave, string? Valor)[] cambios)
    {
        Directory.CreateDirectory(carpeta);
        var laBase = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(carpeta, Archivo), laBase);
        var claves = new List<(string Clave, string Valor)>
        {
            ("formato", "1"),
            ("paquete", Id),
            ("hecho-utc", "2026-11-14T14:15:07Z"),
            ("hecho-local", "2026-11-14 08:15"),
            ("equipo", "CAJA2"),
            ("ips", IpDelOrigen),
            ("puerto", "5080"),
            ("version", "1.0.180"),
            ("archivo", Archivo),
            ("bytes", "5000"),
            ("sha256", Convert.ToHexString(SHA256.HashData(laBase)).ToLowerInvariant()),
            ("tienda", "Matriz"),
            ("productos", "4512"),
            ("ventas", "1280"),
            ("ultima-venta", "2026-11-13 20:41"),
            ("cajas", "1"),
            ("caja.1.id", "5a1d4c3e-0000-4000-8000-000000000002"),
            ("caja.1.nombre", "Caja 2"),
            ("caja.1.serie", "B"),
            ("caja.1.equipo", "CAJA2"),
            ("caja.1.con-llave", "si"),
            ("caja.1.con-turnos", "si"),
            ("caja.1.turno-abierto", "si"),
            ("caja.1.eleventa-caja", "3"),
            ("eleventa", "si"),
            ("eleventa-escribir", "si"),
            ("eleventa-servidor", "192.0.2.106"),
            ("eleventa-puerto", "3050"),
            ("eleventa-base", BaseDeEleventa),
            ("eleventa-base-de-la-bandeja", BaseDeEleventa),
            ("eleventa-usuario", "SYSDBA"),
            ("eleventa-caja", "3"),
            ("eleventa-cajero", "1"),
            ("eleventa-cajas", "B=3"),
            ("eleventa-ajustes", ""),
            ("eleventa-pendientes", "0"),
            ("eleventa-por-revisar", "0"),
            ("eleventa-detenido", "no"),
            ("controlprint", "si"),
            ("controlprint-servidor", "192.0.2.106"),
            ("avisos", "1"),
            ("aviso.1", "La caja «Caja 2» (serie B) tiene su turno abierto: se queda abierto; no hagas el corte en eleventa hasta terminar."),
        };
        foreach (var (clave, valor) in cambios)
        {
            var donde = claves.FindIndex(c => c.Clave == clave);
            if (valor is null)
                claves.RemoveAll(c => c.Clave == clave);
            else if (donde >= 0)
                claves[donde] = (clave, valor);
            else
                claves.Add((clave, valor));
        }
        var paquete = new PaqueteDePrueba(carpeta, claves);
        paquete.Escribir();
        return paquete;
    }

    /// <summary>mudanza.txt como lo escribe .NET en Windows: UTF-8 sin BOM, con sus saltos.</summary>
    public void Escribir(string salto = "\r\n", bool bom = false) =>
        File.WriteAllText(RutaDelResumen, string.Concat(Claves.Select(c => $"{c.Clave}={c.Valor}{salto}")), new UTF8Encoding(bom));

    /// <summary>Lo que deja la PC nueva al quedar bien: traida.txt con su dirección; y la base del paquete, borrada.</summary>
    public void DejarTraida(string paquete = Id, bool borrarLaBase = true)
    {
        File.WriteAllText(Path.Combine(Carpeta, PaqueteDeMudanza.ArchivoDeTraida),
            $"paquete={paquete}\r\nequipo=PRINCIPAL\r\nips=192.0.2.50\r\npuerto=5080\r\ncuando=2026-11-14 08:40\r\n", new UTF8Encoding(true));
        if (borrarLaBase)
            File.Delete(RutaDeLaBase);
    }
}

/// <summary>Una PC a la medida de cada prueba: si tiene el servicio registrado, cómo se llama y qué IP tiene.</summary>
internal sealed class PcDePrueba(string raiz) : IEquipo
{
    public string Datos { get; } = Path.Combine(raiz, "ProgramData", "PapeleriaPos");

    public string Programa { get; } = Path.Combine(raiz, "Program Files", "PapeleriaPos");

    /// <summary>El .exe con que está registrado el servicio; vacío, esta PC no lo tiene.</summary>
    public string? Servicio { get; set; }

    public string NombreDeLaPc { get; set; } = "PRINCIPAL";

    /// <summary>Las de la red de la tienda (TEST-NET) y, aparte, las de otras tarjetas (una red virtual).</summary>
    public List<string> IpsDeLaTienda { get; } = ["192.0.2.50"];

    public List<string> IpsDeOtrasTarjetas { get; } = [];

    public string? ExeDelServicio() => Servicio;

    public string? Version(string exe) => "1.0.180";

    public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [];

    public IReadOnlyList<string> PuertosCom() => [];

    public IReadOnlyList<string> Ips() => IpsDeLaTienda;

    public IReadOnlyList<string> Gbak() => [];

    public string? IniDeEleventa() => null;

    public string Nombre() => NombreDeLaPc;

    public IReadOnlyList<string> IpsPropias() => [.. IpsDeLaTienda, .. IpsDeOtrasTarjetas];

    /// <summary>
    /// El DPAPI de las pruebas (aquí no hay Windows que cifre): lo «cifrado para esta PC» es lo mismo con esta marca delante, así que
    /// abrirlo lo devuelve en claro. Lo que no la trae (lo cifrado en otra PC, o cualquier otra cosa) no se abre, como con el de verdad.
    /// </summary>
    private static readonly byte[] CifradoAqui = "cifrado-para-esta-pc:"u8.ToArray();

    public static byte[] CifrarParaEstaPc(byte[] claro) => [.. CifradoAqui, .. claro];

    /// <summary>Cuántas veces se abrió algo cifrado para esta PC.</summary>
    public int Abiertas { get; private set; }

    public byte[]? AbrirLoDeEstaPc(byte[] cifrado)
    {
        if (!cifrado.AsSpan().StartsWith(CifradoAqui))
            return null;
        Abiertas++;
        return cifrado[CifradoAqui.Length..];
    }

    /// <summary>Con el servicio registrado (el archivo no hace falta: solo se pregunta si lo está).</summary>
    public PcDePrueba ConServicio()
    {
        Servicio = Path.Combine(Programa, "Servicio", "PapeleriaPos.Servicio.exe");
        return this;
    }

    public string Archivo(string relativo, string texto)
    {
        var ruta = Path.Combine(Datos, relativo);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, texto);
        return ruta;
    }

    /// <summary>
    /// La marca <c>pos.db.mudada</c> como la deja <c>mudanza-sacar</c> (el exe del servicio) junto a la base de la PC de donde salió:
    /// JSON en camelCase, con el paquete, la tienda, cuándo y la carpeta donde quedó el paquete.
    /// </summary>
    public string DejarLaMarca(string carpetaDelPaquete, string cuandoUtc = "2026-11-14T14:15:07.1234567Z", string tienda = "Matriz") =>
        Archivo(MarcaDeMudanza.NombreDelArchivo, new JsonObject
        {
            ["paquete"] = PaqueteDePrueba.Id,
            ["tienda"] = tienda,
            ["cuandoUtc"] = cuandoUtc,
            ["carpeta"] = carpetaDelPaquete,
            ["archivo"] = PaqueteDePrueba.Archivo,
            ["sha256"] = new string('a', 64),
            ["equipo"] = "CAJA2",
        }.ToJsonString());

    /// <summary>
    /// La nota <c>mudanza-puesta.txt</c> que deja instalar-pc.ps1 junto a la base de la PC nueva en cuanto la del paquete ya es su
    /// pos.db: «clave=valor», como todo lo que escriben los scripts (UTF-8 con BOM).
    /// </summary>
    public string DejarLaNota(string paquete = PaqueteDePrueba.Id)
    {
        Directory.CreateDirectory(Datos);
        var ruta = NotaDeLaBasePuesta.Ruta(Datos);
        File.WriteAllText(ruta, $"paquete={paquete}\r\nequipo=CAJA2\r\narchivo={PaqueteDePrueba.Archivo}\r\nsha256={new string('a', 64)}\r\ncuando=2026-11-14 08:31\r\n",
            new UTF8Encoding(true));
        return ruta;
    }
}

/// <summary>
/// caja.json como lo escribe y lo lee la caja de Windows (ConfiguracionCaja, en src/PapeleriaPos.Caja/MainWindow.xaml.cs, que las
/// pruebas no pueden usar: es de Windows): el mismo registro, con sus datos en su orden, y las mismas opciones. Así un «+» de una
/// llave en base64 queda escrito \u002B, y no hay salto de renglón al final.
/// </summary>
internal sealed record ConfiguracionDeLaCaja(
    string Servicio, Guid? CajaId, string? Impresora, string? LlaveProtegida = null, string? ColaImpresion = null, bool QuitarImpresos = true,
    bool Prueba = false, string? ServicioLocal = null, string? BaseInicial = null, string? CarpetaDatos = null, string? LlaveDelServicio = null,
    string? DireccionDeConfianza = null, JsonElement? Red = null, JsonElement? Lector = null, JsonElement? ColasImpresion = null)
{
    private static readonly JsonSerializerOptions Opciones = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>Como la guarda la caja (ConfiguracionCaja.Guardar): UTF-8 sin BOM.</summary>
    public void Guardar(string ruta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllBytes(ruta, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Opciones)));
    }

    /// <summary>Como la lee la caja al abrir (ConfiguracionCaja.Leer).</summary>
    public static ConfiguracionDeLaCaja Leer(string ruta) => JsonSerializer.Deserialize<ConfiguracionDeLaCaja>(File.ReadAllText(ruta), Opciones)!;
}

/// <summary>
/// La red de una prueba: en cada dirección contesta lo que se le diga (un JSON fijo, o el servicio de verdad en memoria); en las demás
/// no contesta nadie, como una PC apagada. Anota a quién se le preguntó.
/// </summary>
internal sealed class RedDePrueba : HttpMessageHandler
{
    public const string EstaPc = "localhost:5080";

    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _quienContesta = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las direcciones a las que se les preguntó («192.0.2.77:5080»), en orden.</summary>
    public ConcurrentQueue<string> Preguntadas { get; } = new();

    /// <summary>Una red en la que nadie debe preguntar nada: si alguien pregunta, la prueba lo ve en <see cref="Preguntadas"/>.</summary>
    public static RedDePrueba SinNadie() => new();

    /// <summary>En esa dirección contesta eso su api/estado.</summary>
    public RedDePrueba Contesta(string direccion, string json)
    {
        _quienContesta[direccion] = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    /// <summary>En esa dirección está ese servicio (el de verdad, en memoria).</summary>
    public RedDePrueba EsElServicio(string direccion, HttpMessageHandler servicio)
    {
        var invocador = new HttpMessageInvoker(servicio, disposeHandler: false);
        _quienContesta[direccion] = (peticion, ct) => invocador.SendAsync(peticion, ct);
        return this;
    }

    /// <summary>Lo que contesta un servicio de Papelería POS con esas sucursales (o sin tienda todavía).</summary>
    public static string Estado(string[] sucursales, bool tiendaNueva = false) =>
        $$"""{ "servicio": "Papelería POS", "version": "1.0.180", "sucursales": [{{string.Join(", ", sucursales.Select(s => $"\"{s}\""))}}], "tiendaNueva": {{(tiendaNueva ? "true" : "false")}} }""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var direccion = request.RequestUri!.Authority;
        Preguntadas.Enqueue(direccion);
        return _quienContesta.TryGetValue(direccion, out var contesta)
            ? contesta(request, cancellationToken)
            : Task.FromException<HttpResponseMessage>(new HttpRequestException($"Nadie contesta en {direccion} (la red de la prueba)."));
    }
}

/// <summary>Las respuestas del asistente en la PC a donde se trae la base, como las deja prellenadas con lo del paquete.</summary>
internal static class RespuestasDePrueba
{
    /// <summary>
    /// Sin esperar al servicio de esta PC más que un momento: en la red de las pruebas, quien no contesta falla en el acto y no va a
    /// contestar después. A cada pregunta, en cambio, se le da tiempo de sobra: con toda la suite corriendo a la par, hasta una
    /// respuesta que ya está lista puede tardar, y ninguna prueba depende de que ese plazo se cumpla.
    /// </summary>
    public static readonly PlazosDeLaMudanza SinEsperar =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));

    /// <summary>Con el servicio de verdad en memoria: su primera respuesta, con toda la suite corriendo a la par, tarda.</summary>
    public static readonly PlazosDeLaMudanza ConPaciencia =
        new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// «Caja principal» que trae ese paquete: eleventa y Control Print en esta misma PC (127.0.0.1), la base de eleventa de allá, el
    /// cajero de allá, el número de caja de esta PC vacío y el gbak de esta PC. Cada cambio pone su respuesta.
    /// </summary>
    public static ArchivoDeClaves ParaTraer(string carpetaDelPaquete, string gbak, params (string Clave, string Valor)[] cambios)
    {
        var archivo = new ArchivoDeClaves()
            .Poner("rol", "principal").Poner("mudanza", "traer").Poner("mudanza-paquete", carpetaDelPaquete)
            .Poner("impresora", "").Poner("cola", "").Poner("lector", "teclado")
            .Poner("controlprint", "si").Poner("controlprint-servidor", "127.0.0.1")
            .Poner("eleventa", "si").Poner("eleventa-servidor", "127.0.0.1").Poner("eleventa-base", PaqueteDePrueba.BaseDeEleventa)
            .Poner("eleventa-caja", "").Poner("eleventa-cajero", "1").Poner("eleventa-gbak", gbak)
            .Poner("eleventa-respaldos", @"C:\ProgramData\PapeleriaPos\respaldos-eleventa");
        foreach (var (clave, valor) in cambios)
            archivo.Poner(clave, valor);
        return archivo;
    }

    /// <summary>Un archivo que hace de gbak.exe: de él solo se revisa que exista.</summary>
    public static string Gbak(string carpeta)
    {
        var ruta = Path.Combine(carpeta, "firebird20", "bin", "gbak.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, "");
        return ruta;
    }
}
