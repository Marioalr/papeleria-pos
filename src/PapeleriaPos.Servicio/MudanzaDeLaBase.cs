using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La marca de que la base de esta PC se sacó para mudarla a otra PC (<c>pos.db.mudada</c>, junto a la base): de qué paquete, de qué
/// tienda (los nombres de sus sucursales, como en <c>mudanza.txt</c>), cuándo, a qué carpeta y qué archivo. La leen el instalador y su
/// ayudante para ofrecer «Terminar la mudanza» o «Cancelar la mudanza»; mientras exista, el servicio de esta PC no arranca
/// (<see cref="MudanzaDeLaBase.NoArrancarConLaMarca(string, ILogger)"/>).
/// </summary>
public sealed record MarcaDeMudanza(string Paquete, string Tienda, DateTime CuandoUtc, string Carpeta, string Archivo, string Sha256, string Equipo);

/// <summary>Cómo terminó <c>mudanza-sacar</c>: sin <see cref="Error"/>, quedaron el paquete completo y la marca; con él, no se sacó.</summary>
/// <param name="Error">Por qué no se sacó, dicho para enseñarlo tal cual; vacío si quedó.</param>
/// <param name="Marca">La marca que quedó junto a la base.</param>
public sealed record ResultadoDeMudanza(
    string? Error, string Paquete = "", string Carpeta = "", string Archivo = "", long Bytes = 0, string Sha256 = "", string Tienda = "",
    DateTime HechoLocal = default, IReadOnlyList<string>? Avisos = null, string? Marca = null, int Productos = 0, int Ventas = 0, int Cajas = 0)
{
    public bool Bien => Error is null;
}

/// <summary>
/// La mudanza de la base a otra PC con un «paquete de mudanza» (2026-11-11; docs/instalacion.md, «La base de la tienda»). La base viaja
/// en una carpeta (la USB que ya lleva el instalador a las dos PC): no por la red, y sin los secretos (la clave de eleventa y el código de
/// Control Print se escriben en la PC nueva, en sus casillas de siempre). Aquí está lo de la PC que entrega la base:
/// <code>
/// PapeleriaPos.Servicio.exe mudanza-sacar --carpeta &lt;carpeta del paquete&gt; [--salida &lt;archivo clave=valor&gt;]
/// </code>
/// Lo corre el instalador como administrador, con el servicio ya detenido y deshabilitado (<see cref="CorrerAsync"/>): hace un respaldo
/// revisado de la base, lo copia a la carpeta con su resumen (<c>mudanza.txt</c>, lo que lee el ayudante del instalador en la otra PC) y
/// deja junto a la base la marca <c>pos.db.mudada</c>. Con la marca, el servicio de esta PC no arranca aunque alguien lo habilite
/// (<see cref="NoArrancarConLaMarca(string, ILogger)"/>): nunca dos PC vivas con la misma base, vendiendo y escribiendo en eleventa cada
/// una por su lado. No escribe nada en la base.
/// <para>
/// <b>La marca va antes que el resumen.</b> Un paquete está completo cuando tiene su <c>mudanza.txt</c>, que se escribe al final; la
/// marca se deja justo antes. Así nunca hay un paquete completo con esta PC sin marca: si se va la luz (o cierran el programa) entre los
/// dos, queda la marca con el paquete a medias, y eso se cancela con el instalador sin perder nada. Al revés quedaría un paquete que
/// sirve en la USB y esta PC libre de volver a vender: dos bases vivas en cuanto alguien lo trajera a la otra PC.
/// </para>
/// <para>
/// Si algo falla, no queda nada del paquete en la carpeta ni la marca (el respaldo de <c>respaldos\</c> sí: es un respaldo más) y esta PC
/// sigue trabajando como antes. Mientras a <c>mudanza.txt</c> no se le haya empezado a dar su nombre, consta que el paquete quedó sin
/// terminar aunque la USB ya no esté, y la marca se quita. La marca solo se queda en dos casos raros, que el error dice con todas sus
/// letras y que se resuelven con «Cancelar la mudanza» en el instalador: se llevaron la USB justo cuando <c>mudanza.txt</c> tomaba su
/// nombre (no se puede comprobar que el paquete quedó sin él), o la marca no se dejó borrar.
/// </para>
/// </summary>
/// <param name="cadenaConexion">La base del servicio, como la arma su configuración.</param>
/// <param name="carpetaRespaldos">Donde se guardan los respaldos de esta PC: ahí queda también el de la mudanza.</param>
/// <param name="eleventa">El puente con eleventa de esta PC, de su configuración. Su clave no se usa ni se escribe en ningún lado.</param>
public sealed class MudanzaDeLaBase(string cadenaConexion, string carpetaRespaldos, OpcionesEleventa eleventa)
{
    public const string Verbo = "mudanza-sacar";

    public const string Uso =
        "Uso: PapeleriaPos.Servicio.exe mudanza-sacar --carpeta <carpeta del paquete> [--salida <archivo clave=valor>]. Lo corre el instalador, "
        + "como administrador y con el servicio de esta PC detenido.";

    /// <summary>El resumen del paquete: se escribe al final y de un golpe. Un paquete sin él está incompleto por definición.</summary>
    public const string ArchivoDelResumen = "mudanza.txt";

    /// <summary>Lo deja la PC nueva en la carpeta cuando ya trajo la base (instalar-pc.ps1): al sacar otro paquete ahí, se quita.</summary>
    public const string ArchivoDeLaTraida = "traida.txt";

    /// <summary>Cómo se escribe <c>mudanza.txt</c>: si cambia lo que quiere decir una clave, cambia el número.</summary>
    public const int Formato = 1;

    /// <summary>Lo que se pide libre, además del tamaño de la base, en la carpeta del paquete y en la de respaldos.</summary>
    public const long Margen = 32L * 1024 * 1024;

    /// <summary>Lo que se espera al servicio de esta PC para saber que NO contesta.</summary>
    public static readonly TimeSpan EsperaDelServicio = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Las veces que se intenta abrir la base sin compartirla, con un segundo entre una y otra, antes de decir que otro programa la tiene
    /// abierta: el antivirus la suelta enseguida; un servicio que sigue vivo, no.
    /// </summary>
    private const int IntentosDeAbrirLaBase = 5;

    /// <summary>
    /// Como nombra este verbo la base de un paquete (<c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>). En la carpeta de un paquete, un archivo con
    /// ese nombre exacto solo puede ser eso: una copia entera de la base de la tienda.
    /// </summary>
    private static readonly Regex BaseDeUnPaquete =
        new(@"^pos-[0-9]{4}-[0-9]{2}-[0-9]{2}-mudanza-[0-9]{4}\.db$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Lo que dice <c>--salida</c> mientras no termina: si el programa se cierra a la mitad, el instalador enseña esto.</summary>
    public const string SeCerroAntesDeTerminar =
        "El programa se cerró antes de terminar de sacar la base: el paquete no quedó. Corre el instalador en esta PC otra vez: si ofrece "
        + "«Cancelar la mudanza», elígelo, y después saca la base de nuevo.";

    /// <summary>Lo que lleva la marca junto al nombre de la base: <c>pos.db.mudada</c>.</summary>
    private const string FinalDeLaMarca = ".mudada";

    private const string Temporal = ".tmp";

    /// <summary>
    /// Como el pedido de restaurar: se lee en el Bloc de notas. Las letras con acento salen escritas con su número (\u00F3): así no
    /// importa con qué codificación lo lea Windows PowerShell.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>«clave=valor» como lo escribe el ayudante del instalador: UTF-8 con BOM (así lo leen Inno y Windows PowerShell).</summary>
    private static readonly UTF8Encoding Utf8ConBom = new(encoderShouldEmitUTF8Identifier: true);

    // ── Lo de esta PC (las pruebas ponen lo suyo) ───────────────────────────

    /// <summary>El nombre de esta PC: el de las cajas que se dieron de alta aquí.</summary>
    public string Equipo { get; init; } = Environment.MachineName;

    /// <summary>Las IPv4 de esta PC que no son de loopback: con ellas la PC nueva revisa que esta no haya vuelto a trabajar.</summary>
    public Func<IReadOnlyList<string>> Ips { get; init; } = IpsDeEstaPc;

    /// <summary>El puerto donde atiende el servicio de esta PC (<c>Urls</c>).</summary>
    public int Puerto { get; init; } = 5080;

    /// <summary>La PC de Control Print de la configuración de esta PC; vacío si no lo usa. Su código no se lee.</summary>
    public string? ControlPrint { get; init; }

    /// <summary>
    /// La PC de Control Print que viaja en el paquete: la de <see cref="ControlPrint"/>, si es una IP o el nombre de una PC. Si ahí hay
    /// otra cosa —el código de conexión puesto por error, que es un secreto— no viaja ni se repite en ningún lado, como hace la caja
    /// con lo mismo (<c>ColaControlPrint</c>): el paquete dice que aquí se usaba Control Print, sin su PC, y lo avisa.
    /// </summary>
    private string? PcDeControlPrint =>
        ControlPrint?.Trim() is { Length: > 0 } servidor && Uri.CheckHostName(servidor) != UriHostNameType.Unknown && !ProtocoloControlPrint.PareceCodigo(servidor)
            ? servidor
            : null;

    public TimeProvider Reloj { get; init; } = TimeProvider.System;

    public IEspacioEnDisco Disco { get; init; } = new EspacioEnDisco();

    /// <summary>
    /// Si el servicio de esta PC contesta en ese puerto (<see cref="ElServicioContestaAsync"/>): la base se saca con el servicio detenido. Las
    /// pruebas lo cambian: no hay red de verdad.
    /// </summary>
    public Func<int, CancellationToken, Task<bool>> ElServicioContesta { get; init; } = ElServicioContestaAsync;

    /// <summary>«Archivos de programa» de esta PC: la carpeta del paquete no va ahí (desinstalar se la llevaría).</summary>
    public IReadOnlyList<string> CarpetasDelPrograma { get; init; } = ArchivosDePrograma();

    /// <summary>Para las pruebas: la copia a la carpeta del paquete, que puede fallar a la mitad (la USB se llenó, o la quitaron).</summary>
    internal Func<Stream, Stream, CancellationToken, Task>? Copiar { get; init; }

    /// <summary>Los momentos del final en que la USB puede irse: lo que queda depende de cuál (<see cref="AlLlegarA"/>).</summary>
    internal enum Momento
    {
        /// <summary>Ya comprobada la copia, justo antes de dejar la marca.</summary>
        AntesDeLaMarca,

        /// <summary>Con la marca puesta, antes de empezar a escribir el resumen.</summary>
        AntesDelResumen,

        /// <summary>Con el resumen ya escrito aparte, justo antes de darle su nombre.</summary>
        AntesDeNombrarElResumen,
    }

    /// <summary>Para las pruebas: se llama al llegar a cada <see cref="Momento"/> (ahí se llevan la USB).</summary>
    internal Action<Momento>? AlLlegarA { get; init; }

    /// <summary>La versión del programa que saca el paquete, sin lo de después del «+»: la PC nueva no trae uno de una versión más nueva.</summary>
    public string Version { get; init; } =
        typeof(MudanzaDeLaBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    private readonly string _cadena = SinPool(cadenaConexion);

    /// <summary>El archivo de la base; nada si está en memoria (entonces no hay qué mudar).</summary>
    public string? Base { get; } = ArchivoDeLaBase(cadenaConexion);

    /// <summary>La marca (<c>pos.db.mudada</c>), junto a la base.</summary>
    public string? Marca => Base is null ? null : Base + FinalDeLaMarca;

    // ── La marca y el arranque del servicio ─────────────────────────────────

    /// <summary>Dónde va la marca de esa base (<c>pos.db.mudada</c>, junto a ella); nada si la base está en memoria.</summary>
    public static string? ArchivoDeLaMarca(string cadenaConexion) => ArchivoDeLaBase(cadenaConexion) is { } archivo ? archivo + FinalDeLaMarca : null;

    /// <summary>La marca de esa base, si existe (aunque no se pueda leer: cuenta que esté).</summary>
    public static string? MarcaQueExiste(string cadenaConexion) => ArchivoDeLaMarca(cadenaConexion) is { } marca && Path.Exists(marca) ? marca : null;

    /// <summary>Lo que dice una marca; nada si no está o no se entiende (para el servicio cuenta igual: con que exista, no arranca).</summary>
    public static MarcaDeMudanza? LeerMarca(string archivo)
    {
        try
        {
            return File.Exists(archivo) ? JsonSerializer.Deserialize<MarcaDeMudanza>(File.ReadAllText(archivo), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Con la marca junto a la base, el servicio no arranca (como <c>RestauracionDeRespaldos.NoArrancarSinLaBase</c>): la base de esta PC
    /// ya se sacó para mudarla y otra PC la tiene, o la va a tener. Lo dice en el registro (el de eventos de Windows también) y sale la
    /// excepción con lo mismo, que termina el programa con un error. Se llama al arrancar, antes de todo lo demás: sin tocar la base, sin
    /// tareas de fondo y sin atender. Basta con que la marca exista, aunque no se pueda leer. Sin la marca, no hace nada.
    /// </summary>
    public static void NoArrancarConLaMarca(string cadenaConexion, ILogger log)
    {
        if (MarcaQueExiste(cadenaConexion) is not { } marca)
            return;
        var mensaje = $"La base de esta PC se sacó para mudarla a otra PC el {DiaYHora(CuandoSeSaco(marca))}. Este servicio no arranca mientras exista "
            + $"{marca}. Para terminar o cancelar la mudanza: corre el instalador en esta PC.";
        log.LogCritical("{Mensaje}", mensaje);
        throw new InvalidOperationException(mensaje);
    }

    /// <summary>
    /// <see cref="NoArrancarConLaMarca(string, ILogger)"/> desde Program.cs, donde todavía no existe el servicio: con la marca, el registro
    /// se arma solo para decirlo, con lo que ya está configurado hasta ahí (el archivo del día y, en Windows, el Visor de eventos) y nada
    /// más, y se escribe todo antes de que salga la excepción. Sin la marca, no arma nada.
    /// </summary>
    public static void NoArrancarConLaMarca(string cadenaConexion, IServiceCollection soloElRegistro)
    {
        if (MarcaQueExiste(cadenaConexion) is null)
            return;
        using var servicios = soloElRegistro.BuildServiceProvider();
        NoArrancarConLaMarca(cadenaConexion, servicios.GetRequiredService<ILogger<MudanzaDeLaBase>>());
    }

    /// <summary>Cuándo se sacó, en la hora de la tienda: lo que dice la marca o, si no se entiende, cuándo se escribió.</summary>
    private static DateTime CuandoSeSaco(string marca)
    {
        if (LeerMarca(marca) is { CuandoUtc: var cuando } && cuando != default)
            return HoraMexico.ALocal(cuando.ToUniversalTime());
        try
        {
            return HoraMexico.ALocal(File.Exists(marca) ? File.GetLastWriteTimeUtc(marca) : Directory.GetLastWriteTimeUtc(marca));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HoraMexico.ALocal(DateTime.UtcNow);
        }
    }

    // ── El verbo ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Desde la línea de órdenes, con la configuración de esta PC (la misma del servicio): la base, su carpeta de respaldos, el puerto,
    /// el puente con eleventa (sin su clave: ni se lee) y la PC de Control Print (sin su código). Lo que resulta va a <c>--salida</c>
    /// («clave=valor», como lo que deja el ayudante del instalador: <c>bien</c>, y <c>error</c> o los datos del paquete con sus avisos)
    /// y, en pocos renglones y sin acentos, a la pantalla (la lee instalar-pc.ps1 en Windows PowerShell 5.1).
    /// </summary>
    /// <param name="elServicioContesta">Las pruebas: en lugar de preguntarle por HTTP al servicio de esta PC.</param>
    /// <returns>0 solo si quedaron el paquete completo y la marca; 1 si no.</returns>
    public static async Task<int> CorrerAsync(IConfiguration configuracion, IReadOnlyList<string> args, TextWriter salida, TextWriter errores,
        Func<int, CancellationToken, Task<bool>>? elServicioContesta = null, CancellationToken ct = default)
    {
        string? carpeta = null, archivoDeSalida = null;
        var malDada = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--carpeta" && i + 1 < args.Count && carpeta is null)
                carpeta = args[++i];
            else if (args[i] == "--salida" && i + 1 < args.Count && archivoDeSalida is null)
                archivoDeSalida = args[++i];
            else
                malDada = true;
        }
        if (malDada || string.IsNullOrWhiteSpace(carpeta) || archivoDeSalida is { } dada && string.IsNullOrWhiteSpace(dada))
        {
            errores.WriteLine(Uso);
            return 1;
        }
        // Antes de tocar nada: que lo que resulte se pueda decir. Y si el programa se cierra a la mitad, el archivo ya dice que no quedó.
        if (archivoDeSalida is not null)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(archivoDeSalida)) is { Length: > 0 } suCarpeta)
                    Directory.CreateDirectory(suCarpeta);
                Guardar(archivoDeSalida, DeLaSalida(new ResultadoDeMudanza(SeCerroAntesDeTerminar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errores.WriteLine(ParaLaPantalla($"No se sacó la base: no se puede escribir el archivo de salida ({archivoDeSalida}): {ex.Message}"));
                return 1;
            }
        }
        ResultadoDeMudanza resultado;
        try
        {
            resultado = await DeLaConfiguracion(configuracion, elServicioContesta).SacarAsync(carpeta, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Lo que no se pudo ni empezar (la configuración no se entiende, por ejemplo): también queda dicho en --salida.
            resultado = new ResultadoDeMudanza($"No se pudo sacar la base: {ex.Message}");
        }
        var escrita = true;
        if (archivoDeSalida is not null)
        {
            try
            {
                Guardar(archivoDeSalida, DeLaSalida(resultado));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ya quedó como quedó (el código de salida lo dice): solo falta el detalle, que va a la pantalla.
                escrita = false;
                errores.WriteLine(ParaLaPantalla($"No se pudo escribir el archivo de salida ({archivoDeSalida}): {ex.Message}"));
                // Si quedó sacada, el archivo de antes de empezar («no quedó») no se queda para contradecir al 0: instalar-pc.ps1 lo
                // tomaría por que no se sacó, con el paquete bueno en la USB y esta PC ya sin atender. Sin archivo, decide por el 0 y la marca.
                if (resultado.Bien)
                    BorrarSiSePuede(archivoDeSalida);
            }
        }
        if (resultado.Error is { } error)
        {
            errores.WriteLine(ParaLaPantalla($"No se sacó la base: {error}"));
            return 1;
        }
        var avisos = resultado.Avisos ?? [];
        salida.WriteLine(ParaLaPantalla($"Se sacó la base de «{resultado.Tienda}» para mudarla: {resultado.Productos} productos, {resultado.Ventas} ventas, "
            + $"{resultado.Cajas} {(resultado.Cajas == 1 ? "caja" : "cajas")}."));
        salida.WriteLine(ParaLaPantalla($"El paquete de mudanza quedó en {resultado.Carpeta}: {resultado.Archivo} ({RespaldoDto.TamanoDe(resultado.Bytes)})."));
        salida.WriteLine(ParaLaPantalla($"El servicio de esta PC ya no arranca mientras exista {resultado.Marca}."
            + (avisos.Count == 0 ? "" : avisos.Count == 1 ? " Hay 1 aviso." : $" Hay {avisos.Count} avisos.")));
        // Sin --salida (o si no se pudo escribir), los avisos no se pierden.
        if (archivoDeSalida is null || !escrita)
        {
            foreach (var aviso in avisos)
                salida.WriteLine(ParaLaPantalla($"Aviso: {aviso}"));
        }
        return 0;
    }

    /// <summary>Con la configuración de esta PC, como la lee el servicio (servicio.json y lo demás).</summary>
    /// <param name="elServicioContesta">Las pruebas: en lugar de preguntarle por HTTP al servicio de esta PC.</param>
    public static MudanzaDeLaBase DeLaConfiguracion(IConfiguration configuracion, Func<int, CancellationToken, Task<bool>>? elServicioContesta = null)
    {
        var cadena = RutasServicio.CadenaConexion(configuracion);
        return new MudanzaDeLaBase(cadena, RutasServicio.CarpetaRespaldos(configuracion, cadena), EleventaSinSuClave(configuracion))
        {
            Puerto = PuertoDe(configuracion["Urls"]),
            // Solo la PC de Control Print: su código (la variable de entorno del servicio) no se lee.
            ControlPrint = configuracion["ControlPrint:Servidor"] is { } servidor && servidor.Trim().Length > 0 ? servidor.Trim() : null,
            ElServicioContesta = elServicioContesta ?? ElServicioContestaAsync,
        };
    }

    /// <summary>
    /// El puente con eleventa de esa configuración (su sección «Eleventa», como la enlaza el servicio), sin su clave: la mudanza no la
    /// necesita, así que ni se lee. Se enlaza lo demás de la sección, y lo que no se leyó no puede llegar a ningún archivo, a la pantalla
    /// ni al mensaje de un error.
    /// </summary>
    public static OpcionesEleventa EleventaSinSuClave(IConfiguration configuracion)
    {
        const string seccion = "Eleventa";
        var sinLaClave = new ConfigurationBuilder()
            .AddInMemoryCollection(configuracion.GetSection(seccion).GetChildren()
                .Where(hijo => !string.Equals(hijo.Key, nameof(OpcionesEleventa.Clave), StringComparison.OrdinalIgnoreCase))
                .SelectMany(hijo => hijo.AsEnumerable()))
            .Build();
        return sinLaClave.GetSection(seccion).Get<OpcionesEleventa>() ?? new OpcionesEleventa();
    }

    /// <summary>El puerto de la primera dirección de <c>Urls</c> («http://*:5080»); sin ninguna que se entienda, el de siempre.</summary>
    public static int PuertoDe(string? urls)
    {
        foreach (var url in (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var direccion) && direccion.Port > 0)
                return direccion.Port;
        }
        return 5080;
    }

    // ── Sacar ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saca el paquete a <paramref name="carpeta"/>: (1) revisa que se pueda (la base existe, está al día y tiene tienda; no se había
    /// sacado ya; el servicio de esta PC no contesta ni tiene abierta la base; la carpeta se puede escribir, no está dentro de los datos
    /// ni del programa, y hay espacio); (2) hace el respaldo de siempre en la carpeta de respaldos
    /// (<c>pos-AAAA-MM-DD-mudanza-HHmm.db</c>, revisado como cualquier respaldo); (3) junta el resumen de ese respaldo y de la
    /// configuración; (4) lo copia a la carpeta (quitando antes el paquete que hubiera, y las copias sueltas de intentos que no
    /// terminaron), lo vuelve a leer de ahí y compara su SHA-256; después, si la copia sigue ahí, deja la marca y, lo último, escribe
    /// <c>mudanza.txt</c> (por qué en ese orden: ver la clase). Si algo falla, quita lo que alcanzó a dejar y el error lo dice, para
    /// enseñarlo tal cual.
    /// </summary>
    public async Task<ResultadoDeMudanza> SacarAsync(string carpeta, CancellationToken ct = default)
    {
        string? copia = null, rutaDelResumen = null, marcaPuesta = null;
        // Si a mudanza.txt ya se le empezó a dar su nombre. Antes de eso consta que el paquete quedó sin él, pase lo que pase con la USB.
        var resumenConNombre = false;
        try
        {
            carpeta = Path.GetFullPath(carpeta);
            var marca = await RevisarAsync(carpeta, ct);

            // 2. El respaldo de siempre, en la carpeta de respaldos de esta PC: se queda ahí como un respaldo más.
            var hechoUtc = SinFracciones(Reloj.GetUtcNow().UtcDateTime);
            var hechoLocal = HoraMexico.ALocal(hechoUtc);
            string respaldo;
            try
            {
                respaldo = Respaldos.Hacer(_cadena, carpetaRespaldos, hechoLocal, "mudanza-" + hechoLocal.ToString("HHmm", CultureInfo.InvariantCulture));
            }
            catch (CopiaDanadaException ex)
            {
                throw new NoSeSaca($"El respaldo de la base no pasó la revisión: la base de esta PC puede estar dañada y así no se muda. {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                throw new NoSeSaca($"No se pudo hacer el respaldo de la base en {carpetaRespaldos}: {ex.Message}");
            }
            var archivo = Path.GetFileName(respaldo);

            // 3. El resumen, del respaldo mismo (es lo que viaja), y su huella ya con el archivo cerrado.
            var resumen = await ResumirAsync(respaldo, ct);
            var bytes = new FileInfo(respaldo).Length;
            var sha256 = await HuellaAsync(respaldo, ct);

            // 4. A la carpeta del paquete. El que hubiera ya no sirve: primero su resumen, así queda incompleto desde ese momento.
            var paquete = Guid.NewGuid().ToString("N");
            QuitarElPaqueteAnterior(carpeta);
            // Desde aquí, lo que haya en la carpeta con estos nombres es de este paquete: si no se termina, se quita.
            (copia, rutaDelResumen) = (Path.Combine(carpeta, archivo), Path.Combine(carpeta, ArchivoDelResumen));
            try
            {
                await using (var origen = new FileStream(respaldo, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                await using (var destino = new FileStream(copia, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    await (Copiar is { } comoDigaLaPrueba ? comoDigaLaPrueba(origen, destino, ct) : origen.CopyToAsync(destino, 1024 * 1024, ct));
                    // Hasta el disco (la USB): que no se quede en la memoria de Windows al expulsarla.
                    destino.Flush(flushToDisk: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new NoSeSaca($"No se pudo copiar la base a {carpeta}: {ex.Message}");
            }
            if (new FileInfo(copia).Length != bytes || await HuellaAsync(copia, ct) != sha256)
                throw new NoSeSaca($"La copia de la base en {carpeta} no quedó igual que el respaldo (¿la USB está fallando?): prueba con otra.");
            // Lo que va a decir el resumen, ya armado: después de la marca solo falta escribirlo.
            var claves = DelPaquete(paquete, hechoUtc, hechoLocal, archivo, bytes, sha256, resumen);

            // 5. La marca: desde aquí esta PC ya no atiende. Antes que el resumen, para que nunca haya un paquete completo sin ella.
            AlLlegarA?.Invoke(Momento.AntesDeLaMarca);
            // Solo si la copia sigue en la carpeta (se pudieron llevar la USB mientras tanto): sin ella no hay paquete que terminar, y
            // esto acaba como cualquier falla de la copia, sin marca.
            if (!File.Exists(copia))
                throw new NoSeSaca($"La copia de la base ya no está en {carpeta} (¿quitaron la USB antes de terminar?): no se sacó la base.");
            marcaPuesta = marca;
            try
            {
                EscribirYVaciar(marca + Temporal,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new MarcaDeMudanza(paquete, resumen.Tienda, hechoUtc, carpeta, archivo, sha256, Equipo), Json)));
                Mover(marca + Temporal, marca);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new NoSeSaca($"No se pudo dejar la marca de la mudanza junto a la base ({marca}): {ex.Message}");
            }

            // 6. El resumen: al final y de un golpe. Con él, el paquete está completo.
            AlLlegarA?.Invoke(Momento.AntesDelResumen);
            try
            {
                Guardar(rutaDelResumen, claves, alDarleSuNombre: () =>
                {
                    resumenConNombre = true;
                    AlLlegarA?.Invoke(Momento.AntesDeNombrarElResumen);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new NoSeSaca($"No se pudo escribir {rutaDelResumen}: {ex.Message}");
            }
            return new ResultadoDeMudanza(null, paquete, carpeta, archivo, bytes, sha256, resumen.Tienda, hechoLocal, resumen.Avisos, marca,
                resumen.Productos, resumen.Ventas, resumen.Cajas.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new ResultadoDeMudanza((ex is NoSeSaca ? ex.Message : $"No se pudo sacar la base: {ex.Message}")
                + Deshacer(carpeta, copia, rutaDelResumen, resumenConNombre, marcaPuesta));
        }
        catch (OperationCanceledException)
        {
            Deshacer(carpeta, copia, rutaDelResumen, resumenConNombre, marcaPuesta);
            throw;
        }
    }

    /// <summary>Lo que no se puede sacar, dicho para enseñarlo tal cual.</summary>
    private sealed class NoSeSaca(string mensaje) : Exception(mensaje);

    /// <summary>El paso 1: lo que tiene que cumplirse antes de tocar nada. Devuelve dónde va la marca.</summary>
    private async Task<string> RevisarAsync(string carpeta, CancellationToken ct)
    {
        if (Base is not { } base_ || Marca is not { } marca)
            throw new NoSeSaca("La base de este servicio no es un archivo: no hay nada que mudar.");
        if (!File.Exists(base_))
            throw new NoSeSaca($"No está la base de esta PC ({base_}): no hay nada que mudar.");
        try
        {
            await using var db = new PosDbContext(BaseDeDatos.Opciones(_cadena));
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
                throw new NoSeSaca($"A la base de esta PC ({base_}) le faltan cambios de esta versión del programa: arranca el servicio para ponerla al día "
                    + "y saca el paquete otra vez.");
            if (!await db.Sucursales.AnyAsync(ct))
                throw new NoSeSaca($"La base de esta PC ({base_}) no tiene tienda: no hay nada que mudar.");
        }
        catch (SqliteException ex)
        {
            throw new NoSeSaca($"No se pudo leer la base de esta PC ({base_}): {ex.Message}");
        }
        if (Path.Exists(marca))
            throw new NoSeSaca($"La base de esta PC ya se sacó para mudarla (el {DiaYHora(CuandoSeSaco(marca))}): cancela la mudanza primero (corre el "
                + "instalador en esta PC y elige «Cancelar la mudanza») y sácala otra vez.");
        if (await ElServicioContesta(Puerto, ct))
            throw new NoSeSaca($"El servicio de esta PC está contestando (http://localhost:{Entero(Puerto)}): la base se saca con el servicio detenido; lo hace "
                + "el instalador.");
        // Que no conteste no basta: un servicio que todavía arranca (migra antes de atender) o que se está deteniendo tampoco contesta, y
        // con la marca recién puesta seguiría de largo hasta atender. Mientras vive, tiene abierta la base.
        if (await OtroProgramaLaTieneAbiertaAsync(base_, ct))
            throw new NoSeSaca($"Otro programa tiene abierta la base de esta PC ({base_}): si es el servicio, sigue vivo (arrancando o deteniéndose). La "
                + "base se saca con el servicio detenido; lo hace el instalador.");

        // La carpeta del paquete: fuera de los datos de esta PC (al terminar la mudanza se apartan) y del programa (desinstalar se lo lleva).
        var datos = Path.GetDirectoryName(base_) ?? ".";
        if (EstaDentro(carpeta, datos))
            throw new NoSeSaca($"La carpeta del paquete ({carpeta}) está dentro de la carpeta de datos de esta PC ({datos}): elige otra (la USB).");
        if (CarpetasDelPrograma.FirstOrDefault(c => c.Length > 0 && EstaDentro(carpeta, c)) is { } delPrograma)
            throw new NoSeSaca($"La carpeta del paquete ({carpeta}) está dentro de Archivos de programa ({delPrograma}): elige otra (la USB).");
        // La misma carpeta que la de respaldos (cuando no es la de siempre, junto a la base): el respaldo y su copia serían el mismo archivo.
        if (EstaDentro(carpeta, carpetaRespaldos) && EstaDentro(carpetaRespaldos, carpeta))
            throw new NoSeSaca($"La carpeta del paquete ({carpeta}) es la carpeta de respaldos de esta PC: elige otra (la USB).");
        bool seVeEnRespaldos;
        try
        {
            Directory.CreateDirectory(carpeta);
            // Un archivo de prueba que se borra solo al cerrarlo: sin un segundo paso que el antivirus pueda estorbar.
            var prueba = $"mudanza-{Guid.NewGuid():N}{Temporal}";
            using (new FileStream(Path.Combine(carpeta, prueba), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                seVeEnRespaldos = File.Exists(Path.Combine(carpetaRespaldos, prueba));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new NoSeSaca($"No se puede escribir en la carpeta del paquete ({carpeta}): {ex.Message} Revisa que la USB esté conectada y que no esté "
                + "protegida contra escritura.");
        }
        // La carpeta de respaldos escrita de otra forma (por un acceso directo, o como unidad de red): lo que se deja en una se ve en la
        // otra. Ahí no se saca el paquete: al dejarlo se quitan de la carpeta las bases de paquetes anteriores, y serían respaldos.
        if (seVeEnRespaldos)
            throw new NoSeSaca($"La carpeta del paquete ({carpeta}) es la carpeta de respaldos de esta PC ({carpetaRespaldos}): elige otra (la USB).");

        // El espacio: el tamaño de la base (con lo que siga en su -wal) y un margen, en las dos carpetas. Lo que ocupa el paquete que se
        // va a reemplazar en la carpeta cuenta como libre.
        var necesario = new FileInfo(base_).Length + (File.Exists(base_ + "-wal") ? new FileInfo(base_ + "-wal").Length : 0) + Margen;
        try
        {
            Directory.CreateDirectory(carpetaRespaldos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoSeSaca($"No se puede escribir en la carpeta de respaldos de esta PC ({carpetaRespaldos}): {ex.Message}");
        }
        if (Disco.Libre(carpetaRespaldos) is { } enRespaldos && enRespaldos < necesario)
            throw new NoSeSaca($"No hay espacio en el disco de esta PC para el respaldo de la mudanza ({carpetaRespaldos}): hacen falta "
                + $"{RespaldoDto.TamanoDe(necesario)} libres y hay {RespaldoDto.TamanoDe(enRespaldos)}.");
        var seLibera = BasesDePaquetesAnteriores(carpeta).Sum(anterior => new FileInfo(anterior).Length);
        if (Disco.Libre(carpeta) is { } enLaCarpeta && enLaCarpeta + seLibera < necesario)
            throw new NoSeSaca($"No hay espacio en {carpeta} para el paquete de mudanza: hacen falta {RespaldoDto.TamanoDe(necesario)} libres y hay "
                + $"{RespaldoDto.TamanoDe(enLaCarpeta + seLibera)}.");
        return marca;
    }

    // ── El resumen (lo que lleva mudanza.txt) ───────────────────────────────

    private sealed record CajaMudada(Guid Id, string Nombre, string Serie, string? Equipo, bool ConLlave, bool ConTurnos, bool TurnoAbierto, int? EleventaCaja);

    private sealed record Resumen(
        string Tienda, int Productos, int Ventas, DateTime? UltimaVentaUtc, IReadOnlyList<CajaMudada> Cajas, string? BaseDeLaBandeja, string EleventaCajas,
        string EleventaAjustes, int Pendientes, int PorRevisar, bool Detenido, IReadOnlyList<string> Avisos);

    /// <summary>
    /// Lo que hay en el respaldo (el archivo que viaja, no la base de esta PC: así el resumen dice justo lo que lleva el paquete) y cómo
    /// está el puente con eleventa aquí, con los avisos para quien hace la mudanza.
    /// </summary>
    private async Task<Resumen> ResumirAsync(string respaldo, CancellationToken ct)
    {
        await using var db = new PosDbContext(BaseDeDatos.Opciones(new SqliteConnectionStringBuilder { DataSource = respaldo, Pooling = false }.ToString()));
        // Las sucursales como las dice /api/estado (por nombre): con ese texto se reconoce «la misma tienda» en la otra PC.
        var tienda = string.Join("; ", await db.Sucursales.OrderBy(s => s.Nombre).Select(s => s.Nombre).ToListAsync(ct));
        var productos = await db.Productos.CountAsync(p => p.Activo, ct);
        var ventas = await db.Ventas.CountAsync(v => v.PagadaUtc != null, ct);
        var ultimaVenta = await db.Ventas.MaxAsync(v => v.PagadaUtc, ct);
        var conTurnos = (await db.Turnos.Select(t => t.CajaId).Distinct().ToListAsync(ct)).ToHashSet();
        var conTurnoAbierto = (await db.Turnos.Where(t => t.CerradoUtc == null).Select(t => t.CajaId).Distinct().ToListAsync(ct)).ToHashSet();
        var cajas = (await db.Cajas.AsNoTracking().ToListAsync(ct))
            .OrderBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Serie, StringComparer.Ordinal)
            .Select(c => new CajaMudada(c.Id, c.Nombre, c.Serie, c.Equipo, c.TieneEquipo, conTurnos.Contains(c.Id), conTurnoAbierto.Contains(c.Id),
                eleventa.DeLaSerie(c.Serie)?.Caja))
            .ToList();
        var baseDeLaBandeja = await db.Set<BaseDeLaBandeja>().AsNoTracking().Select(b => b.Ruta).FirstOrDefaultAsync(ct);
        var pendientes = await db.EnviosAEleventa.Where(e => e.Estado == EstadoEnvioEleventa.Pendiente).Select(e => e.Tipo).ToListAsync(ct);
        var porRevisar = await db.EnviosAEleventa.CountAsync(e => e.Estado == EstadoEnvioEleventa.PorRevisar, ct);
        var detencion = await RestauracionDeEleventa.DetencionAsync(db, ct);

        var avisos = new List<string>();
        if (pendientes.Count > 0)
        {
            var soloVentas = pendientes.All(t => t == TipoEnvioEleventa.Venta);
            var (una, varias) = soloVentas ? ("venta", "ventas") : ("operación de la caja", "operaciones de la caja (ventas, devoluciones, entradas o salidas)");
            var cuantas = pendientes.Count == 1 ? $"Hay 1 {una} que todavía no llega a eleventa" : $"Hay {Entero(pendientes.Count)} {varias} que todavía no llegan a eleventa";
            // Con el puente sin escribir no se manda nada, tampoco en la PC nueva (llega como estaba): no se promete.
            avisos.Add(eleventa.Escribir
                ? cuantas + (pendientes.Count == 1 ? ": viaja con la base y la manda la PC nueva, sin repetirse." : ": viajan con la base y las manda la PC nueva, sin repetirse.")
                : cuantas + (pendientes.Count == 1 ? ": viaja con la base, pero el puente llega sin escribir y no se manda hasta que lo prendas en la PC nueva."
                    : ": viajan con la base, pero el puente llega sin escribir y no se mandan hasta que lo prendas en la PC nueva."));
        }
        if (porRevisar > 0)
            avisos.Add((porRevisar == 1 ? "Hay 1 cosa por revisar con eleventa (no pasó sola): viaja con la base y se atiende"
                : $"Hay {Entero(porRevisar)} cosas por revisar con eleventa (no pasaron solas): viajan con la base y se atienden") + " en la PC nueva, como siempre.");
        if (detencion is not null)
            avisos.Add($"El puente con eleventa está detenido desde el {DiaYHora(HoraMexico.ALocal(detencion.DetectadaUtc))}: llega detenido a la PC nueva y "
                + "no manda nada hasta que se reanude desde una caja.");
        // El corte de una caja que le manda a eleventa se hace en eleventa: cortarla allá a media mudanza deja fuera lo que falta por subir.
        foreach (var caja in cajas.Where(c => c.TurnoAbierto))
            avisos.Add($"La caja {Dicha(caja)} tiene un turno abierto: se queda abierto"
                + (eleventa.Escribir ? "; no hagas el corte en eleventa hasta terminar la mudanza." : " y se sigue en él al terminar la mudanza."));
        // Las que no son de esta PC recuerdan la llave de este servicio, que no se muda: no reconocen al de la PC nueva.
        var deOtros = cajas.Where(c => c is { ConLlave: true, Equipo: not null } && !string.Equals(c.Equipo, Equipo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (deOtros.Count == 1)
            avisos.Add($"La caja {Dicha(deOtros[0])} está dada de alta desde otro equipo («{deOtros[0].Equipo}»): después de la mudanza se da de alta otra vez.");
        else if (deOtros.Count > 1)
            avisos.Add($"Hay {Entero(deOtros.Count)} cajas dadas de alta desde otros equipos ({string.Join(", ", deOtros.Select(c => $"{c.Nombre} en «{c.Equipo}»"))}): "
                + "después de la mudanza cada una se da de alta otra vez.");

        // Eleventa:Cajas para la PC nueva: cada caja que ya tuvo turnos, con el número con que escribe aquí (lo pendiente de la bandeja y su
        // turno adoptado se buscan con ese número: con otro, se escribirían dos veces). Su cajero, solo si aquí tiene el suyo.
        var entradas = new List<(CajaMudada Caja, int Numero, string Texto)>();
        foreach (var caja in cajas.Where(c => c.ConTurnos))
        {
            if (caja.EleventaCaja is { } numero)
            {
                var suCajero = eleventa.Cajas.TryGetValue(caja.Serie.Trim(), out var propia) ? Numero(propia.Cajero) : "";
                entradas.Add((caja, numero, $"{caja.Serie}={Entero(numero)}" + (suCajero.Length > 0 ? "/" + suCajero : "")));
            }
            else if (eleventa.Escribir)
                avisos.Add($"La caja {Dicha(caja)} no tiene caja de eleventa: lo suyo espera en la bandeja.");
            // Sin escribir en eleventa nada entra a la bandeja, pero esa caja tampoco viaja en eleventa-cajas: si en la PC nueva se prende
            // el puente con la caja de allá, escribiría como ella. Solo si aquí hay puente (lee, o tiene su caja puesta): sin nada de
            // eleventa no hay de qué avisar.
            else if (eleventa.Activo || eleventa.SinEscribir)
                avisos.Add($"La caja {Dicha(caja)} no tiene caja de eleventa: antes de prender el puente en la PC nueva, ponle la suya en Eleventa:Cajas.");
        }
        foreach (var repetida in entradas.GroupBy(e => e.Numero).Where(g => g.Count() > 1))
            avisos.Add($"Las cajas {Juntas(repetida.Select(e => Dicha(e.Caja)).ToList())} tienen la misma caja de eleventa ({Entero(repetida.Key)}): así "
                + "estaban en esta PC y así llegan a la PC nueva; cada una debería llevar la suya.");

        var ajustes = new List<string>();
        foreach (var (clave, valor) in AjustesQueCambiaron())
        {
            // «Clave=valor» separados con «;»: uno que lleva «;» no cabe en la lista, y se dice.
            if (valor.Contains(';'))
                avisos.Add($"El ajuste «{clave}» del puente con eleventa de esta PC no cabe en el paquete (lleva «;»): ponlo a mano en la PC nueva.");
            else
                ajustes.Add($"{clave}={valor}");
        }
        if (ControlPrint is not null && PcDeControlPrint is null)
            avisos.Add("En esta PC, «ControlPrint:Servidor» no es la IP ni el nombre de la PC de Control Print (no se repite aquí, por si era el código de "
                + "conexión): no viaja en el paquete. Ponla en la PC nueva.");
        return new Resumen(tienda, productos, ventas, ultimaVenta, cajas, baseDeLaBandeja, string.Join(';', entradas.Select(e => e.Texto)),
            string.Join(';', ajustes), pendientes.Count, porRevisar, detencion is not null, avisos);
    }

    private static string Dicha(CajaMudada caja) => $"«{caja.Nombre}» (serie {caja.Serie})";

    /// <summary>«a y b», «a, b y c».</summary>
    private static string Juntas(IReadOnlyList<string> cosas) =>
        cosas.Count < 2 ? string.Concat(cosas) : $"{string.Join(", ", cosas.Take(cosas.Count - 1))} y {cosas[^1]}";

    /// <summary>
    /// Los ajustes del puente que aquí no están en su valor por omisión, de la lista cerrada que viaja (nunca rutas ni la clave): la PC
    /// nueva los pone tal cual en su servicio.json, así que van como se escriben ahí.
    /// </summary>
    private IEnumerable<(string Clave, string Valor)> AjustesQueCambiaron()
    {
        var deOmision = new OpcionesEleventa();
        if (!string.Equals(eleventa.ProductoImpresiones, deOmision.ProductoImpresiones, StringComparison.Ordinal))
            yield return (nameof(OpcionesEleventa.ProductoImpresiones), eleventa.ProductoImpresiones ?? "");
        if (!string.Equals(eleventa.ProductoCopias, deOmision.ProductoCopias, StringComparison.Ordinal))
            yield return (nameof(OpcionesEleventa.ProductoCopias), eleventa.ProductoCopias ?? "");
        if (eleventa.RespaldoObligatorio != deOmision.RespaldoObligatorio)
            yield return (nameof(OpcionesEleventa.RespaldoObligatorio), eleventa.RespaldoObligatorio ? "true" : "false");
        if (!eleventa.SegundosAlCobrar.Equals(deOmision.SegundosAlCobrar))
            yield return (nameof(OpcionesEleventa.SegundosAlCobrar), eleventa.SegundosAlCobrar.ToString(CultureInfo.InvariantCulture));
        if (eleventa.SegundosEntreEnvios != deOmision.SegundosEntreEnvios)
            yield return (nameof(OpcionesEleventa.SegundosEntreEnvios), Entero(eleventa.SegundosEntreEnvios));
        if (eleventa.Minutos != deOmision.Minutos)
            yield return (nameof(OpcionesEleventa.Minutos), Entero(eleventa.Minutos));
        if (eleventa.SegundosMaximos != deOmision.SegundosMaximos)
            yield return (nameof(OpcionesEleventa.SegundosMaximos), Entero(eleventa.SegundosMaximos));
        if (eleventa.DiasDeRespaldos != deOmision.DiasDeRespaldos)
            yield return (nameof(OpcionesEleventa.DiasDeRespaldos), Entero(eleventa.DiasDeRespaldos));
    }

    /// <summary>
    /// <c>mudanza.txt</c>: el contrato con el ayudante del instalador, que lo lee en la otra PC. Todas las claves van siempre, aunque
    /// vacías (salvo las numeradas), y en este orden.
    /// </summary>
    private List<(string Clave, string Valor)> DelPaquete(string paquete, DateTime hechoUtc, DateTime hechoLocal, string archivo, long bytes, string sha256,
        Resumen r)
    {
        var claves = new List<(string, string)>
        {
            ("formato", Entero(Formato)),
            ("paquete", paquete),
            ("hecho-utc", hechoUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            ("hecho-local", Minuto(hechoLocal)),
            ("equipo", Equipo),
            ("ips", string.Join(';', Ips())),
            ("puerto", Entero(Puerto)),
            ("version", Version),
            ("archivo", archivo),
            ("bytes", bytes.ToString(CultureInfo.InvariantCulture)),
            ("sha256", sha256),
            ("tienda", r.Tienda),
            ("productos", Entero(r.Productos)),
            ("ventas", Entero(r.Ventas)),
            ("ultima-venta", r.UltimaVentaUtc is { } ultima ? Minuto(HoraMexico.ALocal(ultima)) : ""),
            ("cajas", Entero(r.Cajas.Count)),
        };
        for (var i = 0; i < r.Cajas.Count; i++)
        {
            var (caja, n) = (r.Cajas[i], Entero(i + 1));
            claves.Add(($"caja.{n}.id", caja.Id.ToString()));
            claves.Add(($"caja.{n}.nombre", caja.Nombre));
            claves.Add(($"caja.{n}.serie", caja.Serie));
            claves.Add(($"caja.{n}.equipo", caja.Equipo ?? ""));
            claves.Add(($"caja.{n}.con-llave", SiNo(caja.ConLlave)));
            claves.Add(($"caja.{n}.con-turnos", SiNo(caja.ConTurnos)));
            claves.Add(($"caja.{n}.turno-abierto", SiNo(caja.TurnoAbierto)));
            claves.Add(($"caja.{n}.eleventa-caja", caja.EleventaCaja is { } suya ? Entero(suya) : ""));
        }
        claves.AddRange(
        [
            ("eleventa", SiNo(eleventa.Activo)),
            ("eleventa-escribir", SiNo(eleventa.Escribir)),
            ("eleventa-servidor", eleventa.Servidor ?? ""),
            ("eleventa-puerto", Entero(eleventa.Puerto)),
            ("eleventa-base", eleventa.Base ?? ""),
            ("eleventa-base-de-la-bandeja", r.BaseDeLaBandeja ?? ""),
            ("eleventa-usuario", eleventa.Usuario ?? ""),
            ("eleventa-caja", Numero(eleventa.Caja)),
            ("eleventa-cajero", Numero(eleventa.Cajero)),
            ("eleventa-cajas", r.EleventaCajas),
            ("eleventa-ajustes", r.EleventaAjustes),
            ("eleventa-pendientes", Entero(r.Pendientes)),
            ("eleventa-por-revisar", Entero(r.PorRevisar)),
            ("eleventa-detenido", SiNo(r.Detenido)),
            ("controlprint", SiNo(ControlPrint is not null)),
            ("controlprint-servidor", PcDeControlPrint ?? ""),
        ]);
        AgregarAvisos(claves, r.Avisos);
        return claves;
    }

    /// <summary>Lo que va a <c>--salida</c>: <c>bien=no</c> con su <c>error</c>, o <c>bien=si</c> con lo del paquete y sus avisos.</summary>
    private static List<(string Clave, string Valor)> DeLaSalida(ResultadoDeMudanza r)
    {
        if (r.Error is { } error)
            return [("bien", "no"), ("error", error)];
        var claves = new List<(string, string)>
        {
            ("bien", "si"),
            ("paquete", r.Paquete),
            ("carpeta", r.Carpeta),
            ("archivo", r.Archivo),
            ("bytes", r.Bytes.ToString(CultureInfo.InvariantCulture)),
            ("sha256", r.Sha256),
            ("tienda", r.Tienda),
            ("hecho-local", Minuto(r.HechoLocal)),
        };
        AgregarAvisos(claves, r.Avisos ?? []);
        return claves;
    }

    private static void AgregarAvisos(List<(string, string)> claves, IReadOnlyList<string> avisos)
    {
        claves.Add(("avisos", Entero(avisos.Count)));
        for (var i = 0; i < avisos.Count; i++)
            claves.Add(($"aviso.{Entero(i + 1)}", avisos[i]));
    }

    private static string SiNo(bool valor) => valor ? "si" : "no";

    private static string Entero(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    /// <summary>«AAAA-MM-DD HH:mm».</summary>
    private static string Minuto(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>«14/11/2026 08:15», para los textos que se enseñan: igual en cualquier región de Windows.</summary>
    private static string DiaYHora(DateTime local) => local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Un número de eleventa (una caja, un cajero); un 0, como lo trae la plantilla, es que falta.</summary>
    private static string Numero(int? valor) => valor is { } numero && numero > 0 ? Entero(numero) : "";

    private static DateTime SinFracciones(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    // ── Los archivos ─────────────────────────────────────────────────────────

    /// <summary>
    /// «clave=valor», un dato por renglón, como lo que se pasan el instalador y su ayudante: el primer renglón es un comentario («#») y
    /// un valor va en un solo renglón (sus saltos de línea se vuelven espacios). Se escribe aparte, hasta el disco, y al final toma su
    /// nombre: nunca queda a medias. Su carpeta ya tiene que estar: si la del paquete desapareció (se llevaron la USB), no se hace otra
    /// para dejar ahí un resumen sin su base.
    /// </summary>
    /// <param name="alDarleSuNombre">
    /// Se llama ya escrito aparte, justo antes de darle su nombre: quien llama sabe así si se llegó a ese punto. Si falla antes, el
    /// archivo no quedó con su nombre; si falla después (se llevaron la USB a medio cambio), no se sabe.
    /// </param>
    private static void Guardar(string ruta, IReadOnlyList<(string Clave, string Valor)> claves, Action? alDarleSuNombre = null)
    {
        var texto = new StringBuilder("# Papelería POS: la mudanza de la base a otra PC, un dato por renglón. No se cambia a mano.\r\n");
        foreach (var (clave, valor) in claves)
            texto.Append(clave).Append('=').Append(valor.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        var temporal = ruta + Temporal;
        try
        {
            EscribirYVaciar(temporal, [.. Utf8ConBom.GetPreamble(), .. Utf8ConBom.GetBytes(texto.ToString())]);
            alDarleSuNombre?.Invoke();
            Mover(temporal, ruta, pisando: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BorrarSiSePuede(temporal);
            throw;
        }
    }

    private static void EscribirYVaciar(string ruta, byte[] contenido)
    {
        using var archivo = new FileStream(ruta, FileMode.Create, FileAccess.Write, FileShare.None);
        archivo.Write(contenido);
        archivo.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Cambia de nombre, con unos intentos (como al respaldar y al restaurar): el antivirus puede tener abierto un momento un archivo
    /// recién escrito. Sin <paramref name="pisando"/>, lo que ya esté con ese nombre no se toca.
    /// </summary>
    private static void Mover(string origen, string destino, bool pisando = false)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                File.Move(origen, destino, pisando);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && intento < 5 && File.Exists(origen)
                && (pisando || !Path.Exists(destino)))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task<string> HuellaAsync(string archivo, CancellationToken ct)
    {
        await using var flujo = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(flujo, ct));
    }

    /// <summary>
    /// La base del paquete que ya está en la carpeta (la que nombra su <c>mudanza.txt</c>), si está: solo un respaldo de esa carpeta
    /// (<c>pos-….db</c>, sin ruta), para que un resumen ajeno no haga borrar otra cosa.
    /// </summary>
    private static string? BaseDelPaqueteAnterior(string carpeta)
    {
        var resumen = Path.Combine(carpeta, ArchivoDelResumen);
        if (!File.Exists(resumen))
            return null;
        string? nombre = null;
        try
        {
            foreach (var renglon in File.ReadLines(resumen, Encoding.UTF8))
            {
                if (renglon.TrimStart('\uFEFF').StartsWith("archivo=", StringComparison.OrdinalIgnoreCase))
                    nombre = renglon[(renglon.IndexOf('=') + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (nombre is not { Length: > 0 } || nombre != Path.GetFileName(nombre) || !nombre.StartsWith("pos-", StringComparison.OrdinalIgnoreCase)
            || !nombre.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            return null;
        var ruta = Path.Combine(carpeta, nombre);
        return File.Exists(ruta) ? ruta : null;
    }

    /// <summary>
    /// Las bases de paquetes anteriores que hay en la carpeta, para quitarlas al dejar ahí uno nuevo: la que nombra su
    /// <c>mudanza.txt</c> (<see cref="BaseDelPaqueteAnterior"/>) y las que quedaron sueltas, sin resumen, de un intento que no terminó
    /// (se fue la luz a media copia, o se llevaron la USB antes de poder borrarla). Esas llevan el nombre de su minuto, así que el
    /// siguiente intento no las pisa, y nadie más las va a borrar: cada una es una copia entera de la base de la tienda. Solo las de
    /// nombre exacto (<see cref="BaseDeUnPaquete"/>) y solo las de esa carpeta: lo demás que haya en la USB no es de la mudanza.
    /// </summary>
    private static List<string> BasesDePaquetesAnteriores(string carpeta)
    {
        var bases = new List<string>();
        if (BaseDelPaqueteAnterior(carpeta) is { } conSuResumen)
            bases.Add(conSuResumen);
        try
        {
            if (Directory.Exists(carpeta))
                bases.AddRange(Directory.EnumerateFiles(carpeta)
                    .Where(suelta => BaseDeUnPaquete.IsMatch(Path.GetFileName(suelta)) && !bases.Contains(suelta, StringComparer.OrdinalIgnoreCase)).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La carpeta no se dejó leer: lo que haya, se queda; si estorba para dejar el paquete nuevo, lo dirá la copia.
        }
        return bases;
    }

    /// <summary>
    /// El paquete que ya estaba en la carpeta se reemplaza: primero su <c>mudanza.txt</c> (desde ahí está incompleto y nadie lo trae),
    /// luego su base, las que quedaron sueltas de intentos que no terminaron (<see cref="BasesDePaquetesAnteriores"/>), la nota de que
    /// ya se había traído y un resumen que se quedó a medio escribir. Si no se puede, no se sigue.
    /// </summary>
    private static void QuitarElPaqueteAnterior(string carpeta)
    {
        var susBases = BasesDePaquetesAnteriores(carpeta);
        try
        {
            File.Delete(Path.Combine(carpeta, ArchivoDelResumen));
            foreach (var suBase in susBases)
                File.Delete(suBase);
            File.Delete(Path.Combine(carpeta, ArchivoDeLaTraida));
            if (Path.Combine(carpeta, ArchivoDelResumen + Temporal) is var aMedias && File.Exists(aMedias))
                File.Delete(aMedias);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoSeSaca($"No se pudo quitar el paquete de mudanza que ya estaba en {carpeta}: {ex.Message}");
        }
    }

    /// <summary>
    /// No se pudo terminar: se quita lo que se alcanzó a dejar, en el orden en que nunca queda un paquete completo con esta PC sin marca:
    /// primero el resumen (sin él, lo demás ya no es un paquete), después la copia de la base y, al final, la marca. La marca solo se
    /// quita si consta que el paquete quedó sin resumen. Consta si a <c>mudanza.txt</c> ni se le empezó a dar su nombre
    /// (<paramref name="resumenConNombre"/>), esté o no la carpeta al alcance; si ya se le estaba dando, solo si se ve que no quedó:
    /// con la carpeta ya fuera de alcance (se llevaron la USB justo entonces) no se sabe, y esta PC se queda con la marca. Devuelve lo
    /// que hay que agregar al error; vacío si quedó limpio.
    /// </summary>
    private static string Deshacer(string carpeta, string? copia, string? resumen, bool resumenConNombre, string? marca)
    {
        var sinResumen = resumen is null || !resumenConNombre || (BorrarSiSePuede(resumen) && Directory.Exists(carpeta) && !File.Exists(resumen));
        var sinCopia = copia is null || BorrarSiSePuede(copia);
        // Con la carpeta fuera de alcance, que la copia «ya no esté» no dice nada: pudo quedarse en la USB.
        var alAlcance = Directory.Exists(carpeta);
        if (marca is not null)
        {
            BorrarSiSePuede(marca + Temporal);
            if (Path.Exists(marca))
            {
                if (!sinResumen)
                    return $" No se pudo comprobar que el paquete de {carpeta} quedó sin terminar, así que esta PC se queda con la marca de la mudanza "
                        + $"({marca}) y su servicio no arranca: así ese paquete no se trae a otra PC con esta trabajando. Corre el instalador en esta "
                        + "PC y elige «Cancelar la mudanza»; después saca la base otra vez.";
                if (!QuitarLaMarca(marca))
                    return $" Además no se pudo quitar la marca de la mudanza ({marca}): mientras exista, el servicio de esta PC no arranca. Corre el "
                        + "instalador en esta PC y elige «Cancelar la mudanza».";
            }
        }
        if (!sinCopia)
            return $" Además no se pudo borrar {copia}: bórrala a mano (es una copia de la base de la tienda, sin terminar).";
        // Sin la marca nadie va a pasar por «Cancelar la mudanza», que es quien dice que se borre lo que quedó en la USB: se dice aquí.
        return copia is null || alAlcance ? ""
            : $" En {carpeta} pudo quedar la copia de la base ({Path.GetFileName(copia)}), sin terminar: si sigue ahí, bórrala a mano.";
    }

    /// <summary>
    /// Quita la marca que se acababa de poner, con unos intentos (el antivirus puede tenerla abierta un momento): si se quedara por
    /// eso, esta PC dejaría de atender sin que su base se hubiera sacado.
    /// </summary>
    private static bool QuitarLaMarca(string marca)
    {
        for (var intento = 1; ; intento++)
        {
            if (BorrarSiSePuede(marca))
                return true;
            if (intento >= 5)
                return false;
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
    }

    private static bool BorrarSiSePuede(string archivo)
    {
        try
        {
            File.Delete(archivo);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(archivo);
        }
    }

    // ── Esta PC ──────────────────────────────────────────────────────────────

    private static string? ArchivoDeLaBase(string cadenaConexion)
    {
        var datos = new SqliteConnectionStringBuilder(cadenaConexion);
        return datos.Mode == SqliteOpenMode.Memory || datos.DataSource is "" or ":memory:" ? null : Path.GetFullPath(datos.DataSource);
    }

    /// <summary>Sin el pool: al terminar no queda ninguna conexión guardada con la base abierta (ni con el respaldo).</summary>
    private static string SinPool(string cadenaConexion) => new SqliteConnectionStringBuilder(cadenaConexion) { Pooling = false }.ToString();

    /// <summary>Si <paramref name="carpeta"/> es <paramref name="raiz"/> o está dentro de ella (en Windows, sin distinguir mayúsculas).</summary>
    private static bool EstaDentro(string carpeta, string raiz)
    {
        static string ConSeparador(string ruta)
        {
            ruta = Path.GetFullPath(ruta);
            return Path.EndsInDirectorySeparator(ruta) ? ruta : ruta + Path.DirectorySeparatorChar;
        }
        return ConSeparador(carpeta).StartsWith(ConSeparador(raiz), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ArchivosDePrograma() =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Las IPv4 de las tarjetas de red en uso de esta PC, sin las de loopback.</summary>
    public static IReadOnlyList<string> IpsDeEstaPc()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(t => t.OperationalStatus == OperationalStatus.Up && t.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(t => t.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString()).Distinct().ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Si el servicio de esta PC contesta <c>/api/estado</c> en ese puerto antes de <see cref="EsperaDelServicio"/>: cualquier respuesta
    /// cuenta. Sin el proxy de la cuenta: es esta misma PC. Se le pregunta a la vez por sus dos direcciones de esta PC (127.0.0.1 y
    /// ::1), cada una con su espera, y no por el nombre «localhost»: Windows lo intenta primero por ::1 y, si el servicio solo atiende
    /// por IPv4, el rechazo puede gastarse toda la espera antes de llegar a 127.0.0.1.
    /// </summary>
    public static async Task<bool> ElServicioContestaAsync(int puerto, CancellationToken ct = default)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = EsperaDelServicio };
        async Task<bool> ContestaEnAsync(string deEstaPc)
        {
            try
            {
                using var respuesta = await http.GetAsync($"http://{deEstaPc}:{Entero(puerto)}/api/estado", HttpCompletionOption.ResponseHeadersRead, ct);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return false;
            }
        }
        return (await Task.WhenAll(ContestaEnAsync("127.0.0.1"), ContestaEnAsync("[::1]"))).Any(contesta => contesta);
    }

    /// <summary>
    /// Si otro programa tiene abierta la base. En Windows un archivo no se deja abrir sin compartirlo mientras alguien más lo tenga
    /// abierto, y el servicio tiene abierta su base mientras trabaja con ella: también al arrancar (la revisa y la migra antes de
    /// atender) y al detenerse (sus conexiones siguen abiertas hasta que su proceso termina), que es cuando no contesta por HTTP. Con
    /// unos intentos, porque el antivirus puede tenerla un momento. No se escribe ni se lee nada de ella. Fuera de Windows ese candado
    /// es de cortesía: solo lo respeta otro programa de .NET, y SQLite no.
    /// </summary>
    private static async Task<bool> OtroProgramaLaTieneAbiertaAsync(string archivo, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                using (new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.None, 1))
                    return false;
            }
            catch (IOException ex) when (LaTieneOtro(ex))
            {
                if (intento >= IntentosDeAbrirLaBase)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No es que alguien la tenga abierta: lo que pase con ella lo dirá el respaldo.
                return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>
    /// Si un archivo no se dejó abrir porque otro lo tiene abierto: en Windows, «el proceso no tiene acceso al archivo porque está siendo
    /// utilizado por otro proceso» (32) o tiene una parte bloqueada (33), dentro del HRESULT (0x80070020); fuera de Windows, que no se
    /// dejó abrir estando ahí.
    /// </summary>
    private static bool LaTieneOtro(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF0000) == 0x80070000 && (ex.HResult & 0xFFFF) is 32 or 33
        : ex is not FileNotFoundException and not DirectoryNotFoundException;

    /// <summary>
    /// Para la pantalla: sin acentos, así se lee igual en la consola de Windows PowerShell 5.1, que es donde lo lee instalar-pc.ps1 (como lo
    /// que escribe <see cref="JuntarDatosDelPiloto"/>). Lo que va a los archivos sí los lleva.
    /// </summary>
    private static string ParaLaPantalla(string texto)
    {
        var sin = new StringBuilder(texto.Length);
        foreach (var letra in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letra) != UnicodeCategory.NonSpacingMark)
                sin.Append(letra);
        }
        return sin.ToString().Normalize(NormalizationForm.FormC);
    }
}
