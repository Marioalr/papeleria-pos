using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Servicio.RestaurarRespaldoTests;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// La mudanza de la base a otra PC (diseno-v2, 3.7: «de punta a punta, sin Windows») en UNA corrida, con las piezas de verdad: el servicio
/// de la caja 2 (su tienda, su caja dada de alta con su llave, ventas y lo pendiente del puente con eleventa), <c>mudanza-sacar</c>
/// (<see cref="MudanzaDeLaBase"/>), el ayudante del instalador en medio (<c>mudanza-ver</c> y <c>mudanza-revisar</c> en la principal,
/// <c>escribir --mudanza</c>, <c>leer</c>; en la caja 2 <c>mudanza-viva</c>, <c>probar --esta-caja</c> y <c>escribir --mudada-a</c>), el
/// servicio de la principal arrancando con el pedido de restaurar.ps1 y la caja de verdad (<see cref="ClienteCaja"/> y
/// <see cref="Contingencia"/>) abierta con el caja.json que dejó el ayudante. Lo único escrito aquí es lo que hacen los scripts de
/// PowerShell, que no corren sin Windows (copiar la base del paquete, lo que hace restaurar.ps1 con los archivos, las notas que deja
/// instalar-pc.ps1): cada uno dice de qué parte de cuál script sale.
/// <para>
/// Y los caminos de regreso que se pueden probar sin Windows: otra corrida con la misma USB (la nota mudanza-puesta.txt: «ya estaba»), el
/// paquete ya traído (traida.txt y sin su base), cancelar con la base ya viva en la principal, y la caja 2 que volvió a trabajar con su
/// base (la principal ya no trae ese paquete).
/// </para>
/// </summary>
public sealed class MudanzaDePuntaAPuntaTests : IDisposable
{
    /// <summary>La caja 2 de la tienda, de donde sale la base.</summary>
    private const string Equipo = "CAJA-2";

    /// <summary>La principal, a donde se lleva la base: ahí viven eleventa y Control Print.</summary>
    private const string LaPrincipalSeLlama = "PRINCIPAL";

    /// <summary>De documentación (TEST-NET): la red de la tienda de la prueba. Nunca se les habla de verdad.</summary>
    private const string IpDeLaCaja2 = "192.0.2.72";

    private const string IpDeLaPrincipal = "192.0.2.10";

    /// <summary>Donde atiende el servicio de la principal, visto desde la red de la tienda.</summary>
    private const string EnLaPrincipal = IpDeLaPrincipal + ":5080";

    private const string BaseDeEleventa = PaqueteDePrueba.BaseDeEleventa;

    /// <summary>La clave de Firebird de la caja 2: va en la variable del servicio (Eleventa__Clave), nunca en un archivo ni en el paquete.</summary>
    private const string ClaveDeEleventa = "clave-de-firebird-de-la-prueba";

    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaTienda = [(IPAddress.Parse(IpDeLaPrincipal), 24)];

    /// <summary>
    /// Con los servicios de verdad en memoria y toda la suite corriendo a la par, una respuesta que ya está lista puede tardar: aquí se
    /// revisa lo que se entiende de ella, no lo que tarda (como en <see cref="MudanzaEstaCajaTests"/>).
    /// </summary>
    private static readonly PlazosDeLaMudanza Plazos = RespuestasDePrueba.ConPaciencia with { ALaPrincipal = TimeSpan.FromMinutes(2) };

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"mudanza-punta-{Guid.NewGuid():N}");

    public MudanzaDePuntaAPuntaTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    /// <summary>La carpeta del paquete en la USB (la «mudanza» junto al instalador): fuera de los datos de las dos PC.</summary>
    private string Usb => Path.Combine(_carpeta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta, "salida.txt");

    private string Resumen => Path.Combine(Usb, "mudanza.txt");

    private static string Sha256De(string archivo) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivo))).ToLowerInvariant();

    private static string AhoraLocal(string formato) => DateTime.Now.ToString(formato, CultureInfo.InvariantCulture);

    // ── Las dos PC ──────────────────────────────────────────────────────────

    /// <summary>
    /// La caja 2 como la ve el ayudante: la PC de las pruebas (su nombre, su IP, su servicio todavía registrado y lo que Windows cifró para
    /// ella), con sus datos donde su servicio tiene la base: ahí quedan la marca, servicio.json y la carpeta de su caja (caja.json y su
    /// base local), como en C:\ProgramData\PapeleriaPos.
    /// </summary>
    private LaPcDeLaBase LaCaja2(string datos)
    {
        var pc = new PcDePrueba(Path.Combine(_carpeta, "caja-2")) { NombreDeLaPc = Equipo }.ConServicio();
        pc.IpsDeLaTienda.Clear();
        pc.IpsDeLaTienda.Add(IpDeLaCaja2);
        return new LaPcDeLaBase(pc, datos);
    }

    /// <summary>La principal: todavía sin el servicio (el sábado no lo tiene), con su IP en la red de la tienda.</summary>
    private PcDePrueba LaPrincipal()
    {
        var pc = new PcDePrueba(Path.Combine(_carpeta, "principal")) { NombreDeLaPc = LaPrincipalSeLlama };
        pc.IpsDeLaTienda.Clear();
        pc.IpsDeLaTienda.Add(IpDeLaPrincipal);
        return pc;
    }

    /// <summary>
    /// Una PC de las pruebas (<see cref="PcDePrueba"/>) con sus datos en otra carpeta: la de la base de su servicio de verdad
    /// (<see cref="PrincipalQueSeRestaura"/> la pone donde quiere).
    /// </summary>
    private sealed class LaPcDeLaBase(PcDePrueba pc, string datos) : IEquipo
    {
        public string Datos => datos;

        public string Programa => pc.Programa;

        public string? ExeDelServicio() => pc.ExeDelServicio();

        public string? Version(string exe) => pc.Version(exe);

        public IReadOnlyList<ImpresoraDeWindows> Impresoras() => pc.Impresoras();

        public IReadOnlyList<string> PuertosCom() => pc.PuertosCom();

        public IReadOnlyList<string> Ips() => pc.Ips();

        public IReadOnlyList<string> Gbak() => pc.Gbak();

        public string? IniDeEleventa() => pc.IniDeEleventa();

        public string Nombre() => pc.Nombre();

        public IReadOnlyList<string> IpsPropias() => pc.IpsPropias();

        public byte[]? AbrirLoDeEstaPc(byte[] cifrado) => pc.AbrirLoDeEstaPc(cifrado);
    }

    /// <summary>En la red de la prueba nadie contesta a la búsqueda por UDP: cada quien solo tiene la dirección que se le dio.</summary>
    private sealed class NadieMasEnLaRed : IBuscadorDeLaPrincipal
    {
        public Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>([]);
    }

    // ── La caja 2: su servicio, su puente y su caja ─────────────────────────

    /// <summary>
    /// El puente de la caja 2 como lo dejó el dueño en su servicio.json (prendido, escribiendo como la caja 3 de eleventa con el cajero 7,
    /// sin Cajas) y su clave en la variable del servicio, enlazado como lo enlaza <c>mudanza-sacar</c> (sin leer la clave).
    /// </summary>
    private static OpcionesEleventa ElPuenteDeLaCaja2(string datos)
    {
        var ruta = Path.Combine(datos, ServicioJson.Archivo);
        File.WriteAllText(ruta, $$"""
            {
              "Eleventa": {
                "Activo": true,
                "Escribir": true,
                "Servidor": "{{IpDeLaPrincipal}}",
                "Puerto": 3050,
                "Base": "{{BaseDeEleventa.Replace(@"\", @"\\")}}",
                "Caja": 3,
                "Cajero": 7,
                "Gbak": "C:\\Program Files (x86)\\Firebird\\Firebird_2_0\\bin\\gbak.exe",
                "CarpetaRespaldos": "C:\\ProgramData\\PapeleriaPos\\respaldos-eleventa"
              }
            }
            """);
        var configuracion = new ConfigurationManager();
        ConfiguracionDeLaPc.Agregar(configuracion, ruta);
        configuracion.AddInMemoryCollection(new Dictionary<string, string?> { ["Eleventa:Clave"] = ClaveDeEleventa });
        return MudanzaDeLaBase.EleventaSinSuClave(configuracion);
    }

    /// <summary>
    /// <c>mudanza-sacar</c> en la caja 2 (su servicio ya detenido y deshabilitado por el instalador), el sábado 14 a las 08:15 de la tienda:
    /// su servicio no contesta.
    /// </summary>
    private Task<ResultadoDeMudanza> SacarAsync(PrincipalQueSeRestaura caja2, OpcionesEleventa puente) =>
        new MudanzaDeLaBase($"Data Source={caja2.Ruta};Pooling=False", caja2.CarpetaRespaldos, puente)
        {
            Equipo = Equipo,
            Ips = () => [IpDeLaCaja2],
            Reloj = new RelojDePrueba(new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc)),
            ElServicioContesta = (_, _) => Task.FromResult(false),
        }.SacarAsync(Usb);

    /// <summary>
    /// caja.json como lo guarda la caja al darse de alta con el servicio de su misma PC (ConfiguracionCaja.Guardar): su llave cifrada para
    /// esta PC y la llave pública del servicio que la dio de alta.
    /// </summary>
    private static void GuardarComoLaCaja(IEquipo pc, Guid caja, string llave, string? llaveDelServicio) =>
        new ConfiguracionDeLaCaja("http://localhost:5080/", caja, "", Convert.ToBase64String(PcDePrueba.CifrarParaEstaPc(Encoding.UTF8.GetBytes(llave))),
            LlaveDelServicio: llaveDelServicio, DireccionDeConfianza: "http://localhost:5080/").Guardar(CajaJson.Ruta(pc.Datos));

    /// <summary>
    /// La conexión con que abre la caja de verdad (MainWindow, en src/PapeleriaPos.Caja): de su caja.json, la dirección, su alta (la llave,
    /// abierta con lo que Windows cifró para esta PC), la llave del servicio que recuerda y la dirección de confianza; y si en su PC está el
    /// servicio (servicio.json: «Terminar la mudanza» lo aparta).
    /// </summary>
    private static ConexionCaja ComoAbreLaCaja(IEquipo pc, bool servicioEnEstaPc, Func<ConexionCaja, Task>? alGuardar = null)
    {
        var guardada = ConfiguracionDeLaCaja.Leer(CajaJson.Ruta(pc.Datos));
        return new ConexionCaja
        {
            Servicio = new Uri(guardada.Servicio),
            CajaId = guardada.CajaId,
            Llave = guardada.LlaveProtegida is { } protegida && pc.AbrirLoDeEstaPc(Convert.FromBase64String(protegida)) is { } abierta
                ? Encoding.UTF8.GetString(abierta)
                : null,
            LlaveDelServicio = guardada.LlaveDelServicio,
            DireccionDeConfianza = new Uri(guardada.DireccionDeConfianza ?? guardada.Servicio),
            PideAlta = true,
            ServicioEnEstaPc = servicioEnEstaPc,
            AlGuardar = alGuardar,
        };
    }

    // ── La principal: su servicio y lo que hacen ahí los scripts ─────────────

    /// <summary>
    /// El servicio de verdad de la principal, con su base en sus datos (pos.db y respaldos\, como en la tienda). Lo que le llega viene de la
    /// caja 2 por la red de la tienda, a la IP de la principal (la prueba de identidad se firma con esa dirección).
    /// </summary>
    private static FabricaDePrueba ServicioDeLaPrincipal(string datos) => new(b =>
    {
        b.UseSetting("ConnectionStrings:Pos", $"Data Source={Path.Combine(datos, "pos.db")};Pooling=False").UseSetting("Respaldos:Activo", "false")
            .UseSetting("Telegram:Activo", "false");
        b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new EnLaRedDeLaTienda(IpDeLaCaja2, IpDeLaPrincipal));
            s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
        });
    });

    /// <summary>Mueve un archivo con unos intentos, como los scripts: el antivirus (o un hilo del servicio que se detiene) puede tenerlo abierto.</summary>
    private static void Mover(string de, string a)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                File.Move(de, a);
                return;
            }
            catch (IOException) when (intento < 50)
            {
                ArchivosDePrueba.SoltarBase(de);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// instalar-pc.ps1, «Traer», paso 3 (Mudanza-Traer): la base del paquete a <c>&lt;datos&gt;\respaldos</c>, por un «.copiando», y que
    /// llegó igual (su SHA-256 es el de mudanza.txt).
    /// </summary>
    private void CopiarALosRespaldos(string datos, string archivo, string sha256)
    {
        var respaldos = Path.Combine(datos, "respaldos");
        Directory.CreateDirectory(respaldos);
        var destino = Path.Combine(respaldos, archivo);
        File.Copy(Path.Combine(Usb, archivo), destino + ".copiando");
        Assert.Equal(sha256, Sha256De(destino + ".copiando"));
        Mover(destino + ".copiando", destino);
    }

    /// <summary>
    /// <c>restaurar.ps1 -Carpeta &lt;datos&gt;\respaldos -Respaldo &lt;archivo&gt; -Si -PorMudanza &lt;equipo&gt;</c> (instalacion/restaurar.ps1,
    /// «Restaurar», pasos 4 y 5), con el servicio ya detenido: aparta la base de ahora con lo que la acompaña en
    /// <c>antes-de-restaurar-…</c> (nunca la borra), copia el respaldo junto a la base, deja la marca para revisarlo al arrancar y el pedido
    /// (como lo escribe Windows PowerShell 5.1: ConvertTo-Json de su <c>[ordered]@{…}</c>, con <c>porMudanza</c> al final) y la copia pasa a
    /// ser pos.db. Devuelve la carpeta de la base de antes.
    /// </summary>
    private static string ComoLaPoneRestaurarPs1(string datos, string archivo, string porMudanza)
    {
        var laBase = Path.Combine(datos, "pos.db");
        var sello = AhoraLocal("yyyy-MM-dd_HH.mm.ss");
        var antes = Path.Combine(datos, $"antes-de-restaurar-{sello}");
        for (var n = 2; Directory.Exists(antes); n++)
            antes = Path.Combine(datos, $"antes-de-restaurar-{sello}-{n}");
        Directory.CreateDirectory(antes);
        foreach (var nombre in new[] { "pos.db", "pos.db-wal", "pos.db-shm", "pos.db-journal", "pos.db.en-uso", "pos.db.danada", "pos.db.restaurar" })
        {
            if (File.Exists(Path.Combine(datos, nombre)))
                Mover(Path.Combine(datos, nombre), Path.Combine(antes, nombre));
        }
        var elegido = new FileInfo(Path.Combine(datos, "respaldos", archivo));
        var copia = laBase + ".restaurando";
        File.Copy(elegido.FullName, copia);
        var sinBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var descripcion = string.Format(CultureInfo.InvariantCulture, "el respaldo del {0:dd/MM/yyyy} a las {0:HH:mm} (otro)", elegido.LastWriteTime);
        File.WriteAllText(laBase + ".danada", $"restaurar.ps1 puso {descripcion} ({archivo}) el {AhoraLocal("dd/MM/yyyy HH:mm")}: se revisa al arrancar.", sinBom);
        var ahora = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(laBase + ".restaurar", $$"""
            {
                "id":  "{{Guid.NewGuid()}}",
                "archivo":  "{{archivo}}",
                "tipo":  "Otro",
                "fechaUtc":  "{{elegido.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)}}",
                "usuarioId":  "00000000-0000-0000-0000-000000000000",
                "cajaId":  "00000000-0000-0000-0000-000000000000",
                "sucursalId":  "00000000-0000-0000-0000-000000000000",
                "pedidaUtc":  "{{ahora}}",
                "reinicioAutomatico":  true,
                "paso":  "Puesta",
                "carpeta":  "{{Path.GetFileName(antes)}}",
                "hechaUtc":  "{{ahora}}",
                "fallo":  null,
                "porElScript":  true,
                "porMudanza":  "{{porMudanza}}"
            }
            """, sinBom);
        Mover(copia, laBase);
        return antes;
    }

    /// <summary>
    /// instalar-pc.ps1, Dejar-Nota-De-Puesta: con restaurar.ps1 en 0 (o en 2 con la base ya puesta), la nota de que la base de ESTE paquete
    /// ya es la de esta PC («clave=valor», UTF-8 con BOM, su renglón de comentario arriba).
    /// </summary>
    private static void DejarLaNotaDePuesta(string datos, ClavesEscritas claves) =>
        File.WriteAllLines(NotaDeLaBasePuesta.Ruta(datos),
        [
            "# Papeleria POS: la base de este paquete de mudanza ya se puso en esta PC (instalar-pc.ps1)",
            $"paquete={claves["paquete"]}", $"equipo={claves["equipo"]}", $"archivo={claves["archivo"]}", $"sha256={claves["sha256"]}",
            $"cuando={AhoraLocal("yyyy-MM-dd HH:mm")}",
        ], new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>
    /// instalar-pc.ps1, Mudanza-Cerrar-La-Traida (el paso revisar): si el servicio de la principal contesta con la tienda del paquete y sin
    /// aviso de la base, deja en el paquete traida.txt (el nombre de esta PC, sus IP como las dice «leer» del ayudante, el puerto y cuándo) y
    /// le quita su base: ya quedó aquí como respaldo.
    /// </summary>
    private async Task CerrarLaTraidaAsync(PcDePrueba principal, HttpMessageHandler desdeLaPrincipal, ClavesEscritas claves)
    {
        var estado = (await new HttpClient(desdeLaPrincipal, disposeHandler: false).GetFromJsonAsync<EstadoServicioDto>("http://localhost:5080/api/estado"))!;
        Assert.Equal((false, null, claves["tienda"]), (estado.TiendaNueva, estado.AvisoBase, string.Join("; ", estado.Sucursales)));
        var ips = (await BienAsync(principal, RedDePrueba.SinNadie(), "leer"))["ips"]!.Split(',', ';').Select(ip => ip.Trim()).Where(ip => ip.Length > 0);
        File.WriteAllLines(Path.Combine(Usb, PaqueteDeMudanza.ArchivoDeTraida),
        [
            "# Papeleria POS: la base de este paquete de mudanza ya se trajo a esta PC (instalar-pc.ps1). Falta terminar en la PC de donde salio.",
            $"paquete={claves["paquete"]}", $"equipo={principal.Nombre()}", $"ips={string.Join(';', ips)}", "puerto=5080",
            $"cuando={AhoraLocal("yyyy-MM-dd HH:mm")}",
        ], new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Delete(Path.Combine(Usb, claves["archivo"]));
    }

    /// <summary>
    /// Las respuestas del asistente en la principal, como las prellena con lo del paquete (lo que dijo <c>mudanza-ver</c>): el puente de
    /// allá, el servidor de aquí, el número de caja de esta PC vacío y el gbak de esta PC.
    /// </summary>
    private string RespuestasDeLaPrincipal(ArchivoDeClaves visto)
    {
        var ruta = Path.Combine(_carpeta, "respuestas-principal.txt");
        RespuestasDePrueba.ParaTraer(Usb, RespuestasDePrueba.Gbak(Path.Combine(_carpeta, "principal")), ("eleventa-servidor", visto["propone-eleventa-servidor"]!),
            ("eleventa-base", visto["eleventa-base"]!), ("eleventa-cajero", visto["eleventa-cajero"]!)).Guardar(ruta);
        return ruta;
    }

    // ── El ayudante, como lo corren el asistente e instalar-pc.ps1 ──────────

    private async Task<(int Codigo, ArchivoDeClaves Dice)> AyudanteAsync(IEquipo pc, HttpMessageHandler red, PlazosDeLaMudanza plazos, params string[] orden)
    {
        var codigo = await Ayudante.CorrerAsync([.. orden, "--salida", Salida], pc, red, plazos);
        return (codigo, ArchivoDeClaves.Leer(Salida));
    }

    private async Task<ArchivoDeClaves> BienAsync(IEquipo pc, HttpMessageHandler red, params string[] orden)
    {
        var (codigo, dice) = await AyudanteAsync(pc, red, Plazos, orden);
        Assert.True(codigo == 0, $"{orden[0]} terminó con {codigo}: {dice["error"]}");
        return dice;
    }

    // ── De punta a punta ─────────────────────────────────────────────────────

    /// <summary>
    /// El sábado entero, sin Windows. En la caja 2: su tienda, su caja dada de alta con su llave (caja.json como lo guarda la caja), dos
    /// ventas con lo suyo pendiente de llegar a eleventa, una venta más cobrada en línea por la caja de verdad, y su puente escribiendo como
    /// la caja 3 (sin Cajas). Se saca el paquete (con la marca, su servicio ya no arranca); la caja, abierta otra vez, cobra con su copia. En
    /// la principal: el asistente enseña el paquete y prellena las respuestas, la revisión de antes de cambiar nada pasa, el servicio recién
    /// instalado arranca con su base vacía y restaurar.ps1 -PorMudanza le pone la del paquete: es la misma tienda, conoce la llave de la caja,
    /// tiene las ventas, lo pendiente quedó «ya se intentó» y su bitácora dice de dónde vino. Otra corrida con la misma USB dice «ya estaba»;
    /// el puente queda escrito como estaba (Cajas con la caja 3, escribiendo, sin caja de omisión); el paquete queda «ya traído». En la caja 2:
    /// cancelar ya no se puede (la base está viva en la principal), la principal prueba que conoce la llave de la caja y entrega la suya, y
    /// caja.json queda apuntándole. La caja de verdad, abierta con ese caja.json, comprueba a la principal con la llave que dejó el instalador
    /// y le sube lo que cobró con su copia, sin pedir alta y sin que su folio choque; y sigue cobrando en línea con el folio que sigue.
    /// </summary>
    [Fact]
    public async Task De_la_caja_2_a_la_principal_con_el_ayudante_en_medio_la_caja_de_verdad_sube_lo_suyo_sin_pedir_alta()
    {
        // ════ La caja 2 de hoy ════
        await using var caja2 = await PrincipalQueSeRestaura.CrearAsync();
        var ventas = await DosVentasAsync(caja2);
        var http = caja2.EnMarcha!.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var alta = await Leer<AltaCajaDto>(await http.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, caja.Id, null, null, Equipo)));
        var turno = await Leer<TurnoDto>(await http.GetAsync($"api/cajas/{caja.Id}/turno"));
        var tienda = string.Join("; ", (await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"))).Sucursales);
        var pcDeLaCaja2 = LaCaja2(caja2.Carpeta);
        var puenteDeLaCaja2 = ElPuenteDeLaCaja2(pcDeLaCaja2.Datos);
        GuardarComoLaCaja(pcDeLaCaja2, caja.Id, alta.Llave, alta.LlaveDelServicio);
        var protegida = ConfiguracionDeLaCaja.Leer(CajaJson.Ruta(pcDeLaCaja2.Datos)).LlaveProtegida;
        var baseLocal = Path.Combine(pcDeLaCaja2.Datos, "Caja", "caja-local.db");
        var aSuServicio = new HttpClient(caja2.ParaLasCajas, disposeHandler: false);

        // La caja de verdad de esa PC, abierta con su caja.json: entra el cajero y cobra en línea, en el turno que ya estaba abierto.
        Guid cajero;
        Func<int, SolicitudCobro> cobro;
        TicketDto enLinea;
        var conexion = ComoAbreLaCaja(pcDeLaCaja2, servicioEnEstaPc: true);
        var abierta = new Contingencia(new AlmacenLocal(baseLocal), aSuServicio, conexion);
        try
        {
            var cliente = new ClienteCaja(aSuServicio, conexion, abierta);
            // Hasta que conteste y baje su copia (con la PC ocupada, un latido puede pasarse de su plazo: el siguiente lo logra).
            for (var latido = 1; latido <= 10 && !(abierta.EnLinea && abierta.Paquete is not null); latido++)
                await abierta.LatidoAsync();
            Assert.True(abierta.EnLinea);
            cajero = (await cliente.EntrarAsync("cajero", "1111")).UsuarioId;
            Assert.Equal(turno.Id, (await cliente.TurnoAsync(caja.Id))!.Id);
            var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
            var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
            cobro = piezas => new SolicitudCobro(caja.Id, turno.Id, cajero,
                [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, piezas * 1000L, pluma.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 100000)]);
            enLinea = await cliente.CobrarAsync(cobro(1));
            Assert.False(enLinea.EnContingencia);
        }
        finally
        {
            // Antes de tocar nada, el instalador cierra la caja.
            abierta.Cerrar(TimeSpan.FromSeconds(20));
        }

        // ════ Paso 1, en la caja 2: «Mudar la base de esta PC a otra PC» ════
        // El instalador detiene el servicio (y lo deja deshabilitado). Dos ventas todavía no llegan a eleventa.
        await caja2.DetenerAsync();
        List<Guid> pendientes;
        await using (var db = caja2.Servicio.Base())
        {
            var envios = ventas.Select(v => new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = v, CreadoUtc = DateTime.UtcNow }).ToList();
            db.EnviosAEleventa.AddRange(envios);
            await db.SaveChangesAsync();
            pendientes = envios.Select(e => e.Id).ToList();
        }

        Assert.Null((await SacarAsync(caja2, puenteDeLaCaja2)).Error);

        var claves = ClavesEscritas.De(Resumen);
        Assert.Equal((tienda, "1", Equipo, "si", "si", "3"), (claves["tienda"], claves["cajas"], claves["caja.1.equipo"], claves["caja.1.con-llave"],
            claves["caja.1.turno-abierto"], claves["ventas"]));
        Assert.Equal(("si", "si", "3", "7", "A=3", "2"), (claves["eleventa"], claves["eleventa-escribir"], claves["eleventa-caja"], claves["eleventa-cajero"],
            claves["eleventa-cajas"], claves["eleventa-pendientes"]));
        Assert.DoesNotContain(ClaveDeEleventa, File.ReadAllText(Resumen));
        // Con la marca, el servicio de esa PC ya no arranca, ni aunque alguien lo arranque a mano: en su IP ya nadie contesta.
        Assert.Contains("se sacó para mudarla a otra PC", Assert.ThrowsAny<Exception>(() => caja2.Arrancar()).ToString());

        // Entre el paso 1 y el 3 alguien abre la caja de esa PC: su servicio ya no contesta y cobra con su copia. Lo cobrado se queda en su
        // bandeja para subirlo después.
        TicketDto conLaCopia;
        var otraVez = ComoAbreLaCaja(pcDeLaCaja2, servicioEnEstaPc: true);
        var sinSuServicio = new Contingencia(new AlmacenLocal(baseLocal), aSuServicio, otraVez);
        try
        {
            var cliente = new ClienteCaja(aSuServicio, otraVez, sinSuServicio);
            await sinSuServicio.LatidoAsync();
            Assert.False(sinSuServicio.EnLinea);
            Assert.Equal(cajero, (await cliente.EntrarAsync("cajero", "1111")).UsuarioId);
            conLaCopia = await cliente.CobrarAsync(cobro(2));
            Assert.True(conLaCopia.EnContingencia);
            Assert.Equal((1, Folio(enLinea) + 1), (sinSuServicio.Pendientes, Folio(conLaCopia)));
        }
        finally
        {
            sinSuServicio.Cerrar(TimeSpan.FromSeconds(20));
        }

        // ════ Paso 2, en la principal: «Caja principal» y «Traer la base de otra PC» ════
        var principal = LaPrincipal();
        var desdeLaPrincipal = new RedDePrueba();

        // La página «La base de la tienda»: el paquete que encontró junto al instalador, y lo que propone para esta PC.
        var visto = await BienAsync(principal, desdeLaPrincipal, "mudanza-ver", "--paquete", Usb, "--version", claves["version"]);
        Assert.Equal(("bien", claves["paquete"], "", "127.0.0.1"), (visto["paquete"], visto["paquete-id"], visto["error"], visto["propone-eleventa-servidor"]));
        Assert.Equal(("A=3", "si", BaseDeEleventa, "7"), (visto["eleventa-cajas"], visto["eleventa-escribir"], visto["eleventa-base"], visto["eleventa-cajero"]));
        Assert.StartsWith($"La base de {Equipo}, sacada el 2026-11-14 08:15. Tienda: {tienda}.", visto["texto"]);
        Assert.EndsWith($"El puente llega ESCRIBIENDO en {BaseDeEleventa}: lo que cobre «Caja 1» (serie A) entra a eleventa como la caja 3.", visto["texto"]);
        var avisos = Enumerable.Range(1, int.Parse(visto["avisos"]!, CultureInfo.InvariantCulture)).Select(n => visto[$"aviso.{n}"]).ToList();
        Assert.Contains("Hay 2 ventas que todavía no llegan a eleventa: viajan con la base y las manda la PC nueva, sin repetirse.", avisos);
        Assert.Contains("La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto; no hagas el corte en eleventa hasta terminar la mudanza.", avisos);

        // La revisión de antes de cambiar nada (sin el servicio todavía): el paquete entero (con su SHA-256), que la caja 2 no volvió a
        // trabajar (se le preguntó en su IP y no contestó: su servicio no arranca con la marca) y el servicio.json que va a quedar.
        var respuestas = RespuestasDeLaPrincipal(visto);
        string[] revisar = ["mudanza-revisar", "--paquete", Usb, "--respuestas", respuestas, "--version", claves["version"]];
        var revisado = await BienAsync(principal, desdeLaPrincipal, revisar);
        Assert.Equal(
            [
                ("bien", "si"), ("ya-estaba", "no"), ("aparta-base", "no"), ("archivo", claves["archivo"]), ("equipo", Equipo), ("paquete", claves["paquete"]),
                ("paquete-id", claves["paquete"]), ("tienda", tienda), ("error", ""), ("clave", ""),
            ],
            revisado.Datos);
        Assert.Equal([$"{IpDeLaCaja2}:5080"], desdeLaPrincipal.Preguntadas);

        // Se instala el servicio (instalar-servicio.ps1 deja la plantilla de servicio.json) y arranca con su base vacía: una tienda nueva.
        Directory.CreateDirectory(principal.Datos);
        File.Copy(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "instalacion", "plantilla-servicio.json"), Path.Combine(principal.Datos, ServicioJson.Archivo));
        principal.ConServicio();
        var laBaseNueva = Path.Combine(principal.Datos, "pos.db");
        await using (var vacia = new PosDbContext(BaseDeDatos.Opciones($"Data Source={laBaseNueva};Pooling=False")))
            await BaseDeDatos.PrepararAsync(vacia);
        await using (var recienInstalado = ServicioDeLaPrincipal(principal.Datos))
            Assert.True((await Leer<EstadoServicioDto>(await recienInstalado.CreateClient().GetAsync("api/estado"))).TiendaNueva);

        // «Traer»: la base del paquete a los respaldos de la principal y restaurar.ps1 -PorMudanza la pone (el equipo, con lo que el script
        // deja pasar a su línea de órdenes). Arranca el servicio con ella y, con restaurar.ps1 en 0, queda la nota de que ya está aquí.
        CopiarALosRespaldos(principal.Datos, claves["archivo"], claves["sha256"]);
        var deAntes = ComoLaPoneRestaurarPs1(principal.Datos, claves["archivo"], Regex.Replace(claves["equipo"], "[^A-Za-z0-9._-]", ""));
        await using var nueva = ServicioDeLaPrincipal(principal.Datos);
        var estado = await Leer<EstadoServicioDto>(await nueva.CreateClient().GetAsync("api/estado"));
        DejarLaNotaDePuesta(principal.Datos, claves);
        desdeLaPrincipal.EsElServicio("localhost:5080", nueva.Server.CreateHandler());
        var desdeLaCaja2 = new RedDePrueba().EsElServicio(EnLaPrincipal, nueva.Server.CreateHandler());

        // Es la misma tienda, ya no una nueva, y la base pasó su revisión.
        Assert.Equal((false, null, tienda), (estado.TiendaNueva, estado.AvisoBase, string.Join("; ", estado.Sucursales)));
        Assert.False(File.Exists(laBaseNueva + ".restaurar"));
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={laBaseNueva};Pooling=False")))
        {
            // Sus ventas: las dos de antes y la que la caja cobró en línea (la que cobró con su copia todavía no llega).
            Assert.Equal(ventas.Append(enLinea.VentaId!.Value).Order(), (await db.Ventas.Where(v => v.PagadaUtc != null).Select(v => v.Id).ToListAsync()).Order());
            // Lo pendiente del puente, como «ya se intentó»: antes de mandarlo se busca en eleventa, para no repetirlo.
            var envios = await db.EnviosAEleventa.AsNoTracking().Where(e => pendientes.Contains(e.Id)).ToListAsync();
            Assert.Equal(2, envios.Count);
            Assert.All(envios, e => Assert.Equal((EstadoEnvioEleventa.Pendiente, 1), (e.Estado, e.Intentos)));
            // La bitácora dice de dónde vino la base.
            var anotada = await db.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("Se mudó aquí"));
            Assert.Equal($"Se mudó aquí la base de {Equipo} (respaldo {claves["archivo"]}). La base de antes quedó en {deAntes}", anotada.Detalle);
        }
        // La base vacía de la instalación quedó aparte, no borrada.
        Assert.True(File.Exists(Path.Combine(deAntes, "pos.db")));
        // Conoce la llave de la caja de la caja 2: desde allá, sin la llave no entra; con ella, sí (sin alta ni PIN), a su turno de siempre.
        var conLaLlave = new HttpClient(desdeLaCaja2, disposeHandler: false) { BaseAddress = new Uri($"http://{EnLaPrincipal}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await conLaLlave.GetAsync($"api/cajas/{caja.Id}/turno")).StatusCode);
        conLaLlave.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        Assert.Equal(turno.Id, (await Leer<TurnoDto>(await conLaLlave.GetAsync($"api/cajas/{caja.Id}/turno"))).Id);

        // Otra corrida con la misma USB si aquí se hubiera quedado «a medias» (restaurar.ps1 en 2: la base ya puesta y el servicio sin
        // contestar a tiempo): «leer» dice qué paquete ya se puso aquí (con eso el asistente vuelve a elegir «Traer») y la revisión, con el
        // servicio contestando con la tienda del paquete, dice que ya estaba: no se trae otra vez.
        Assert.Equal(claves["paquete"], (await BienAsync(principal, RedDePrueba.SinNadie(), "leer"))["mudanza-puesta"]);
        var otraCorrida = await BienAsync(principal, desdeLaPrincipal, revisar);
        Assert.Equal(("si", "si", "no", ""), (otraCorrida["bien"], otraCorrida["ya-estaba"], otraCorrida["aparta-base"], otraCorrida["error"]));

        // El puente con la base: servicio.json de la principal con lo de allá (la plantilla, en servicio.json.anterior), leído como lo lee el
        // servicio al arrancar.
        var servicioJson = Path.Combine(principal.Datos, ServicioJson.Archivo);
        var laPlantilla = File.ReadAllBytes(servicioJson);
        var escrito = await BienAsync(principal, RedDePrueba.SinNadie(), "escribir", "--respuestas", respuestas, "--mudanza", Usb);
        Assert.Equal(("", "cambio"), (escrito["error"], escrito["servicio-json"]));
        Assert.Equal(laPlantilla, File.ReadAllBytes(servicioJson + ".anterior"));
        var configuracion = new ConfigurationManager();
        ConfiguracionDeLaPc.Agregar(configuracion, servicioJson);
        var puente = configuracion.GetSection("Eleventa").Get<OpcionesEleventa>()!;
        // Escribiendo, como allá; la caja de la caja 2 (serie A) sigue escribiendo como la caja 3 de eleventa, con su cajero de antes; la
        // principal no hereda la caja 3 de omisión: lo que cobre una caja sin su número espera en la bandeja, nunca escribe como otra.
        Assert.Equal((true, true, "127.0.0.1", BaseDeEleventa), (puente.Activo, puente.Escribir, puente.Servidor, puente.Base));
        Assert.Equal(["A"], puente.Cajas.Keys);
        Assert.Equal((3, 7), puente.DeLaSerie("A"));
        Assert.Null(puente.Caja);
        Assert.Null(puente.DeLaSerie("B"));
        Assert.Null(puente.ProblemaDeLasCajas());
        Assert.Null(puente.Clave);

        // Otra corrida si el paso revisar no hubiera terminado (el servicio no contestó a tiempo): ya estaba, conteste o no el servicio (eso
        // lo dice después el paso revisar), y la configuración de la mudanza ya está escrita: no cambia nada.
        var escritoAntes = File.ReadAllBytes(servicioJson);
        var (codigoSinRespuesta, sinRespuesta) = await AyudanteAsync(principal, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar, revisar);
        Assert.Equal((0, "si", "si", ""), (codigoSinRespuesta, sinRespuesta["bien"], sinRespuesta["ya-estaba"], sinRespuesta["error"]));
        var otraVezEscrito = await BienAsync(principal, RedDePrueba.SinNadie(), "escribir", "--respuestas", respuestas, "--mudanza", Usb);
        Assert.Equal(("", "igual"), (otraVezEscrito["error"], otraVezEscrito["servicio-json"]));
        Assert.Equal(escritoAntes, File.ReadAllBytes(servicioJson));
        Assert.Equal(laPlantilla, File.ReadAllBytes(servicioJson + ".anterior"));

        // Al terminar el paso revisar, el servicio contesta con la tienda del paquete: traida.txt en el paquete, y el paquete sin su base.
        await CerrarLaTraidaAsync(principal, desdeLaPrincipal, claves);
        // Ya traído: el asistente ya no lo ofrece para traer, y la revisión tampoco lo deja.
        var yaTraido = await BienAsync(principal, RedDePrueba.SinNadie(), "mudanza-ver", "--paquete", Usb, "--version", claves["version"]);
        Assert.Equal("ya-traido", yaTraido["paquete"]);
        Assert.StartsWith($"Este paquete ya se trajo a {LaPrincipalSeLlama} (", yaTraido["error"]);
        Assert.EndsWith($"Lo que falta es terminar la mudanza en {Equipo}: corre ahí este instalador.", yaTraido["error"]);
        var (codigoYaTraido, revisadoYaTraido) = await AyudanteAsync(principal, desdeLaPrincipal, Plazos, revisar);
        Assert.Equal((2, "no", "mudanza-paquete"), (codigoYaTraido, revisadoYaTraido["bien"], revisadoYaTraido["clave"]));
        Assert.StartsWith("Este paquete ya se trajo", revisadoYaTraido["error"]);

        // ════ Paso 3, en la caja 2 ════
        var marca = MudanzaDeLaBase.LeerMarca(caja2.Ruta + ".mudada")!;
        Assert.Equal((claves["paquete"], tienda, Usb), (marca.Paquete, marca.Tienda, marca.Carpeta));
        var traida = ArchivoDeClaves.Leer(Path.Combine(Usb, PaqueteDeMudanza.ArchivoDeTraida));

        // Si ahí se eligiera «Cancelar la mudanza», no se cancela: la base ya está viva en la principal (en la IP que dice la USB).
        var codigoViva = await Ayudante.CorrerAsync(["mudanza-viva", "--tienda", marca.Tienda, "--direcciones", traida["ips"]!, "--salida", Salida],
            pcDeLaCaja2, desdeLaCaja2, Plazos, _ => Task.FromResult<IReadOnlyList<Uri>>([]));
        var viva = ArchivoDeClaves.Leer(Salida);
        Assert.Equal((0, "si", IpDeLaPrincipal, LaPrincipalSeLlama, ""), (codigoViva, viva["viva"], viva["donde"], viva["equipo"], viva["error"]));

        // «Terminar la mudanza», paso 1: la principal contesta, con la tienda de la marca, y prueba que conoce la llave de esta caja.
        var reto = Descubrimiento.NuevoReto();
        var identidad = await Leer<IdentidadServicioDto>(await new HttpClient(desdeLaCaja2, disposeHandler: false)
            .GetAsync($"http://{EnLaPrincipal}/api/identidad?caja={caja.Id}&reto={reto}"));
        var dijo = await BienAsync(pcDeLaCaja2, desdeLaCaja2, "probar", "--direccion", IpDeLaPrincipal, "--esta-caja", "si");
        Assert.Equal(("si", "si", "si", ""), (dijo["contesta"], dijo["tienda"], dijo["conoce"], dijo["conoce-motivo"]));
        Assert.Equal(marca.Tienda, dijo["sucursales"]);
        // La llave pública que entrega es la del servicio de la principal (pos.llave no se muda: no es la que la caja recordaba).
        Assert.Equal(identidad.LlaveDelServicio, dijo["llave-del-servicio"]);
        Assert.NotEqual(alta.LlaveDelServicio, dijo["llave-del-servicio"]);
        Assert.DoesNotContain(alta.Llave, File.ReadAllText(Salida));

        // Paso 2: caja.json apunta a la principal, con la llave de su servicio y la misma alta (lo de antes, en caja.json.anterior).
        var respuestasDeLaCaja2 = Path.Combine(_carpeta, "respuestas-caja-2.txt");
        new ArchivoDeClaves().Poner("rol", "otra").Poner("principal", IpDeLaPrincipal).Poner("impresora", "").Poner("cola", "").Poner("lector", "teclado")
            .Poner("mudanza", "terminar").Poner("mudanza-paquete", Usb).Guardar(respuestasDeLaCaja2);
        var cajaJsonDeAntes = File.ReadAllBytes(CajaJson.Ruta(pcDeLaCaja2.Datos));
        var convertida = await BienAsync(pcDeLaCaja2, RedDePrueba.SinNadie(), "escribir", "--respuestas", respuestasDeLaCaja2, "--mudada-a", IpDeLaPrincipal,
            "--llave-del-servicio", dijo["llave-del-servicio"]!);
        Assert.Equal(("", "no-aplica", "cambio"), (convertida["error"], convertida["servicio-json"], convertida["caja-json"]));
        Assert.Equal(cajaJsonDeAntes, File.ReadAllBytes(CajaJson.Ruta(pcDeLaCaja2.Datos) + ".anterior"));
        var guardada = ConfiguracionDeLaCaja.Leer(CajaJson.Ruta(pcDeLaCaja2.Datos));
        Assert.Equal(($"http://{EnLaPrincipal}/", $"http://{EnLaPrincipal}/", dijo["llave-del-servicio"], (Guid?)caja.Id, protegida),
            (guardada.Servicio, guardada.DireccionDeConfianza, guardada.LlaveDelServicio, guardada.CajaId, guardada.LlaveProtegida));

        // ════ La caja de la caja 2, ya «Otra caja»: abierta con ese caja.json, su misma base local y lo que cobró con su copia ════
        // (Pasos 3 y 4 de «Terminar»: su servicio se quita y servicio.json se aparta: ya no hay servicio en esta PC.)
        var guardados = new List<Uri>();
        var conLaPrincipal = ComoAbreLaCaja(pcDeLaCaja2, servicioEnEstaPc: false, c =>
        {
            lock (guardados)
                guardados.Add(c.Servicio);
            return Task.CompletedTask;
        });
        // Al abrirla (Contingencia.Iniciar), la dirección guardada tiene que probar quién es antes de que la caja le mande su llave.
        conLaPrincipal.SinComprobar = true;
        var porLaRed = new HttpClient(desdeLaCaja2, disposeHandler: false);
        var reencuentro = new ReencuentroDeLaPrincipal(conLaPrincipal, new NadieMasEnLaRed(), porLaRed, redes: () => RedDeLaTienda);
        var otraCaja = new Contingencia(new AlmacenLocal(baseLocal), porLaRed, conLaPrincipal, redDeLaCaja: new RedDeLaCaja(conLaPrincipal, reencuentro: reencuentro));
        var pidioAlta = new List<string>();
        otraCaja.SinAlta += porque =>
        {
            lock (pidioAlta)
                pidioAlta.Add(porque);
        };
        try
        {
            Assert.Equal((1, 0), (otraCaja.Pendientes, otraCaja.ConError));
            // La principal prueba quién es (tiene 2 s para contestar: con la PC ocupada, se le vuelve a pedir en el siguiente latido) y la
            // caja sube lo suyo.
            for (var latido = 1; latido <= 10 && !(otraCaja.EnLinea && otraCaja.Pendientes == 0 && otraCaja.Paquete?.Base == estado.Base); latido++)
                await otraCaja.LatidoAsync();

            // Sin pedir alta: la principal probó ser la suya con la llave de esta caja y firmó con la llave que dejó el instalador (la caja
            // no tuvo que aprender ni guardar nada), y la caja le volvió a mandar su llave.
            Assert.Empty(pidioAlta);
            Assert.Null(reencuentro.AltaPorque);
            Assert.Empty(guardados);
            Assert.False(conLaPrincipal.SinComprobar);
            Assert.Equal((new Uri($"http://{EnLaPrincipal}/"), (Guid?)caja.Id, alta.Llave, dijo["llave-del-servicio"]),
                (conLaPrincipal.Servicio, conLaPrincipal.CajaId, conLaPrincipal.Llave, conLaPrincipal.LlaveDelServicio));
            Assert.True(otraCaja.EnLinea);
            Assert.Equal((0, 0), (otraCaja.Pendientes, otraCaja.ConError));
            Assert.Equal(estado.Base, otraCaja.Paquete?.Base);

            // Lo que cobró con su copia entró una vez, en su turno de siempre (en la base mudada sigue abierto), con su folio.
            await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={laBaseNueva};Pooling=False")))
            {
                var subida = await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == conLaCopia.VentaId);
                Assert.Equal((turno.Id, conLaCopia.Folio, true), (subida.TurnoId, subida.FolioTexto, subida.EnContingencia));
            }

            // Y sigue cobrando en línea con la principal, en ese turno, con el folio que sigue: ninguno se repite.
            var cliente = new ClienteCaja(porLaRed, conLaPrincipal, otraCaja);
            Assert.Equal(turno.Id, (await cliente.TurnoAsync(caja.Id))!.Id);
            var siguiente = await cliente.CobrarAsync(cobro(1));
            Assert.False(siguiente.EnContingencia);
            Assert.Equal(Folio(conLaCopia) + 1, Folio(siguiente));
            await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={laBaseNueva};Pooling=False")))
            {
                var folios = await db.Ventas.AsNoTracking().Where(v => v.TurnoId == turno.Id && v.PagadaUtc != null).Select(v => v.FolioTexto).ToListAsync();
                Assert.Equal(5, folios.Count);
                Assert.Equal(folios.Count, folios.Distinct().Count());
                Assert.Contains(enLinea.Folio, folios);
                Assert.Contains(siguiente.Folio, folios);
            }
            Assert.Empty(pidioAlta);
        }
        finally
        {
            otraCaja.Cerrar(TimeSpan.FromSeconds(20));
        }

        // Todo lo que se le preguntó a la red desde la caja 2 (el ayudante y la caja) fue a la principal, en la dirección de caja.json.
        Assert.All(desdeLaCaja2.Preguntadas, direccion => Assert.Equal(EnLaPrincipal, direccion));
    }

    // ── El regreso: la caja 2 vuelve a trabajar con su base ─────────────────

    /// <summary>
    /// En la caja 2 se canceló la mudanza (instalar-pc.ps1 le cambia el nombre a la marca y su servicio vuelve a arrancar con su base)
    /// después de sacar el paquete: la principal ya no lo trae. El asistente todavía lo ofrece (no pregunta a la red), pero la revisión de
    /// antes de cambiar nada le pregunta a la caja 2 en la IP del paquete, la encuentra vendiendo con la misma tienda (su /api/estado de
    /// verdad) y lo detiene, sin dejar nada en la principal: serían dos bases vivas de la misma tienda.
    /// </summary>
    [Fact]
    public async Task Si_la_caja_2_vuelve_a_trabajar_con_su_base_la_principal_ya_no_trae_su_paquete()
    {
        await using var caja2 = await PrincipalQueSeRestaura.CrearAsync();
        await DosVentasAsync(caja2);
        await caja2.DetenerAsync();
        Assert.Null((await SacarAsync(caja2, ElPuenteDeLaCaja2(caja2.Carpeta))).Error);
        var claves = ClavesEscritas.De(Resumen);
        var principal = LaPrincipal();
        var desdeLaPrincipal = new RedDePrueba();
        var visto = await BienAsync(principal, desdeLaPrincipal, "mudanza-ver", "--paquete", Usb, "--version", claves["version"]);
        string[] revisar = ["mudanza-revisar", "--paquete", Usb, "--respuestas", RespuestasDeLaPrincipal(visto), "--version", claves["version"]];

        // Lo normal: la caja 2 no contesta (con la marca su servicio no arranca), y el paquete se puede traer.
        Assert.Equal("si", (await BienAsync(principal, desdeLaPrincipal, revisar))["bien"]);

        // «Cancelar la mudanza» en la caja 2: la marca cambia de nombre y su servicio vuelve a atender con su base.
        var marca = caja2.Ruta + ".mudada";
        File.Move(marca, marca + ".cancelada");
        caja2.Arrancar();
        desdeLaPrincipal.EsElServicio($"{IpDeLaCaja2}:5080", caja2.EnMarcha!.Server.CreateHandler());

        Assert.Equal("bien", (await BienAsync(principal, desdeLaPrincipal, "mudanza-ver", "--paquete", Usb, "--version", claves["version"]))["paquete"]);
        var (codigo, volvio) = await AyudanteAsync(principal, desdeLaPrincipal, Plazos, revisar);

        Assert.Equal((2, "no", "no", "mudanza-paquete"), (codigo, volvio["bien"], volvio["ya-estaba"], volvio["clave"]));
        Assert.Equal($"{Equipo} ({IpDeLaCaja2}) volvió a trabajar con su base: la mudanza se canceló allá y este paquete ya no sirve. Sácalo otra vez.",
            volvio["error"]);
        // En la principal no cambió nada: ni base, ni la nota de que se puso.
        Assert.False(File.Exists(Path.Combine(principal.Datos, "pos.db")));
        Assert.False(File.Exists(NotaDeLaBasePuesta.Ruta(principal.Datos)));
    }
}
