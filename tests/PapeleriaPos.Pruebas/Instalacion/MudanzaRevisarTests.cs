using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;
using ServicioDePrueba = PapeleriaPos.Pruebas.Servicio.ServicioDePrueba;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>mudanza-revisar</c>: todo lo que se revisa ANTES de traer a una PC la base de un paquete de mudanza (docs/instalacion.md), cada
/// regla con la «clave» de la respuesta que la corrige. No cambia nada, ni en la PC ni en el paquete; sale con 0 y «bien=si», o con 2 y
/// su «error».
/// </summary>
public sealed class MudanzaRevisarTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;
    private readonly RedDePrueba _red = new();

    public MudanzaRevisarTests() => _pc = new PcDePrueba(_carpeta.Ruta);

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private const string Origen = PaqueteDePrueba.IpDelOrigen + ":5080";

    /// <summary>Las respuestas de siempre para traer ese paquete (con el gbak de esta PC), con sus cambios.</summary>
    private ArchivoDeClaves Respuestas(params (string Clave, string Valor)[] cambios) =>
        RespuestasDePrueba.ParaTraer(Usb, RespuestasDePrueba.Gbak(_carpeta.Ruta), cambios);

    /// <summary>Como lo corre instalar-pc.ps1 (y el asistente): por su línea de órdenes, con las respuestas en su archivo.</summary>
    private async Task<(int Codigo, ArchivoDeClaves Dice)> RevisarAsync(ArchivoDeClaves? respuestas = null, string version = "1.0.180",
        PlazosDeLaMudanza? plazos = null)
    {
        var archivo = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        (respuestas ?? Respuestas()).Guardar(archivo);
        var codigo = await Ayudante.CorrerAsync(["mudanza-revisar", "--paquete", Usb, "--respuestas", archivo, "--version", version, "--salida", Salida], _pc,
            _red, plazos ?? RespuestasDePrueba.SinEsperar);
        return (codigo, ArchivoDeClaves.Leer(Salida));
    }

    private async Task SePuedeAsync(ArchivoDeClaves? respuestas = null)
    {
        var (codigo, dice) = await RevisarAsync(respuestas);
        Assert.True(codigo == 0 && dice["bien"] == "si" && dice["error"] == "", dice["error"]);
    }

    private async Task<string> NoSePuedeAsync(string clave, ArchivoDeClaves? respuestas = null, string version = "1.0.180")
    {
        var (codigo, dice) = await RevisarAsync(respuestas, version);
        Assert.Equal(2, codigo);
        // Si no se puede, ni se trae ni se aparta nada: «ya-estaba», «ya-configurada» y «aparta-base» solo valen con «bien=si».
        Assert.Equal("bien=no ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
        Assert.Equal(clave, dice["clave"]);
        Assert.False(string.IsNullOrWhiteSpace(dice["error"]));
        return dice["error"]!;
    }

    /// <summary>La nota que deja instalar-pc.ps1 junto a la base en cuanto la del paquete ya es la de esta PC.</summary>
    private void DejarLaNota(string paquete = PaqueteDePrueba.Id) => _pc.DejarLaNota(paquete);

    /// <summary>
    /// La nota <c>mudanza-configurada.txt</c> como la deja instalar-pc.ps1 (Dejar-Nota-De-Configurada) después de un escribir --mudanza que
    /// salió bien: un solo renglón, <c>paquete=</c> con el id de mudanza.txt, en UTF-8 con BOM.
    /// </summary>
    private string DejarLaNotaDeConfigurada(string paquete = PaqueteDePrueba.Id)
    {
        Directory.CreateDirectory(_pc.Datos);
        var ruta = NotaDeLaConfiguracionEscrita.Ruta(_pc.Datos);
        File.WriteAllText(ruta, $"paquete={paquete}\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return ruta;
    }

    /// <summary>
    /// «bien», «ya-estaba», «ya-configurada» y «aparta-base», juntos: lo que instalar-pc.ps1 necesita para saber qué sigue (y si el escribir
    /// de después va con --mudanza).
    /// </summary>
    private static string Resumen(ArchivoDeClaves dice) =>
        $"bien={dice["bien"]} ya-estaba={dice["ya-estaba"]} ya-configurada={dice["ya-configurada"]} aparta-base={dice["aparta-base"]}";

    [Fact]
    public async Task En_una_pc_nueva_el_paquete_completo_se_puede_traer_y_revisarlo_no_cambia_nada()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        var resumenAntes = File.ReadAllBytes(paquete.RutaDelResumen);

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        // El identificador del paquete, dos veces: «paquete» y «paquete-id» (como se llama también en mudanza-ver).
        Assert.Equal(["bien", "ya-estaba", "ya-configurada", "aparta-base", "archivo", "equipo", "paquete", "paquete-id", "tienda", "error", "clave"],
            dice.Datos.Select(d => d.Clave));
        Assert.Equal(["si", "no", "no", "no", PaqueteDePrueba.Archivo, "CAJA2", PaqueteDePrueba.Id, PaqueteDePrueba.Id, "Matriz", "", ""],
            dice.Datos.Select(d => d.Valor));
        // Solo se le preguntó a la PC de donde salió la base (esta no tiene servicio a quien preguntarle).
        Assert.Equal([Origen], _red.Preguntadas);
        // Nada cambió: ni la carpeta de datos existe todavía, ni el paquete se tocó.
        Assert.False(Directory.Exists(_pc.Datos));
        Assert.Equal(["mudanza.txt", PaqueteDePrueba.Archivo], Directory.GetFileSystemEntries(Usb).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(resumenAntes, File.ReadAllBytes(paquete.RutaDelResumen));
    }

    // ── 1. El paquete ──

    [Fact]
    public async Task Sin_paquete_no_hay_que_traer()
    {
        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.Contains("No está la carpeta", error);
        Assert.Empty(_red.Preguntadas);
    }

    [Fact]
    public async Task El_paquete_incompleto_no_se_trae()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        File.WriteAllBytes(paquete.RutaDeLaBase, new byte[100]);

        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.Contains("mide 100 bytes y debería medir 5000", error);
        // Aunque no se pueda, dice de qué paquete habla.
        Assert.Equal("CAJA2", ArchivoDeClaves.Leer(Salida)["equipo"]);
        Assert.Empty(_red.Preguntadas);
    }

    [Fact]
    public async Task El_paquete_de_una_version_mas_nueva_que_este_instalador_no_se_trae()
    {
        PaqueteDePrueba.En(Usb, ("version", "1.0.181"));

        Assert.StartsWith("Este paquete lo sacó la versión 1.0.181 del programa, más nueva que la de este instalador (1.0.180)", await NoSePuedeAsync("mudanza-paquete"));
    }

    [Fact]
    public async Task El_paquete_que_ya_se_trajo_no_se_trae_otra_vez()
    {
        PaqueteDePrueba.En(Usb).DejarTraida();

        Assert.StartsWith("Este paquete ya se trajo a PRINCIPAL", await NoSePuedeAsync("mudanza-paquete"));
    }

    /// <summary>
    /// Ni con su base todavía en la USB (no se dejó borrar al quedar bien): con su traida.txt ya se trajo, y traerla otra vez (aunque sea
    /// «ya-estaba») volvería a poner el puente del paquete encima del que el dueño dejó aquí después (Enmienda 7.1).
    /// </summary>
    [Fact]
    public async Task El_paquete_que_ya_se_trajo_tampoco_con_su_base_todavia_en_la_usb()
    {
        PaqueteDePrueba.En(Usb).DejarTraida(borrarLaBase: false);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));

        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.StartsWith("Este paquete ya se trajo a PRINCIPAL (2026-11-14 08:40), pero su base sigue en", error);
        Assert.Contains("borra ese archivo de la USB", error);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>
    /// Un plazo del puente que no cabe salió tal cual del servicio.json de la PC de donde sale la base: la revisión lo dice con el mismo
    /// texto que la página (se corrige allá; sacar el paquete otra vez no lo cambia), con la clave de la página de la mudanza.
    /// </summary>
    [Fact]
    public async Task Un_plazo_que_no_cabe_dice_que_se_corrige_en_la_pc_de_donde_sale()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-ajustes", "Minutos=30001"));

        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.Contains("«Minutos» con un valor que el servicio no puede usar (va de 0 a 30000)", error);
        Assert.Contains("Ese ajuste viene del servicio.json de CAJA2 (sección «Eleventa»): sacar el paquete otra vez no lo cambia.", error);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>Lo que mudanza-ver no ve (no lee la base): la copia de la USB mide lo mismo, pero no es la que se sacó.</summary>
    [Fact]
    public async Task La_base_que_se_dano_en_la_usb_no_se_trae()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        var danada = File.ReadAllBytes(paquete.RutaDeLaBase);
        danada[2500] ^= 0x01;
        File.WriteAllBytes(paquete.RutaDeLaBase, danada);

        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.Contains("no es la que se sacó de CAJA2", error);
        Assert.Contains("su SHA-256 no cuadra", error);
        Assert.Empty(_red.Preguntadas);
    }

    [Theory]
    [InlineData("")]
    [InlineData("la de hoy")]
    public async Task Sin_saber_la_version_de_este_instalador_no_se_revisa(string version)
    {
        PaqueteDePrueba.En(Usb);

        var error = await NoSePuedeAsync("mudanza-paquete", version: version);

        Assert.StartsWith("No se sabe la versión de este instalador", error);
    }

    // ── 2. La PC de donde salió no volvió a trabajar ──

    [Theory]
    [InlineData("Matriz", new[] { "Matriz" })]
    [InlineData("Matriz", new[] { " MATRIZ " })]
    [InlineData("Centro; Matriz", new[] { "Centro", "matriz" })]
    public async Task Si_la_pc_de_donde_salio_volvio_a_trabajar_con_esa_tienda_el_paquete_ya_no_sirve(string tienda, string[] contesta)
    {
        PaqueteDePrueba.En(Usb, ("tienda", tienda));
        _red.Contesta(Origen, RedDePrueba.Estado(contesta));

        var error = await NoSePuedeAsync("mudanza-paquete");

        Assert.Equal("CAJA2 (192.0.2.77) volvió a trabajar con su base: la mudanza se canceló allá y este paquete ya no sirve. Sácalo otra vez.", error);
        Assert.False(Directory.Exists(_pc.Datos));
    }

    /// <summary>En esa dirección contesta otra cosa: otra tienda, una base todavía sin tienda u otro programa. No es la base que se sacó.</summary>
    [Theory]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Otra tienda"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Matriz", "Bodega"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": [], "tiendaNueva": true }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Matriz"], "tiendaNueva": true }""")]
    [InlineData("""{ "servicio": "Otra cosa", "sucursales": ["Matriz"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Otra"], "sucursales": ["Otra más"] }""")]
    [InlineData("esto no es JSON")]
    public async Task Lo_que_no_es_esa_tienda_viva_en_esa_direccion_no_estorba(string contesta)
    {
        PaqueteDePrueba.En(Usb);
        _red.Contesta(Origen, contesta);

        await SePuedeAsync();
    }

    /// <summary>
    /// Si una IP del paquete es de esta PC (la misma en una red virtual, o esta PC que heredó la IP de la otra), ahí contesta su propio
    /// servicio, ya con la base traída: no es la otra PC, y no se le pregunta.
    /// </summary>
    [Fact]
    public async Task A_una_ip_que_es_de_esta_pc_no_se_le_pregunta()
    {
        PaqueteDePrueba.En(Usb, ("ips", "192.0.2.50;10.99.0.1;127.0.0.1;192.0.2.77"));
        _pc.IpsDeOtrasTarjetas.Add("10.99.0.1");
        foreach (var propia in new[] { "192.0.2.50:5080", "10.99.0.1:5080", "127.0.0.1:5080" })
            _red.Contesta(propia, RedDePrueba.Estado(["Matriz"]));

        await SePuedeAsync();

        Assert.Equal([Origen], _red.Preguntadas);
    }

    /// <summary>De un archivo que llega en una USB solo salen IP escritas con números (nunca un nombre al que conectarse), y su puerto.</summary>
    [Fact]
    public async Task Solo_se_pregunta_a_las_ip_del_paquete_y_en_su_puerto()
    {
        PaqueteDePrueba.En(Usb, ("ips", "servidor.ejemplo.com;192.0.2.77;300.1.1.1;::1;192.0.2.78;192.0.2.77"), ("puerto", "5090"));

        await SePuedeAsync();

        Assert.Equal(["192.0.2.77:5090", "192.0.2.78:5090"], _red.Preguntadas.Order());
    }

    // ── 3. La base de esta PC ──

    [Fact]
    public async Task Sin_servicio_un_pos_db_que_quedo_de_antes_se_aparta_solo()
    {
        PaqueteDePrueba.En(Usb);
        _pc.Archivo("pos.db", "la de antes");

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal("si", dice["aparta-base"]);
        Assert.Equal("no", dice["ya-estaba"]);
        Assert.Equal("la de antes", File.ReadAllText(Path.Combine(_pc.Datos, "pos.db")));
    }

    [Fact]
    public async Task Si_el_servicio_de_esta_pc_no_contesta_no_se_sabe_que_base_tiene()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        // Se le espera (recién arrancado tarda): aquí dos segundos, preguntándole seguido.
        var conEspera = RespuestasDePrueba.SinEsperar with { AEstaPc = TimeSpan.FromSeconds(2), EntreIntentos = TimeSpan.FromMilliseconds(20) };

        var (codigo, dice) = await RevisarAsync(plazos: conEspera);

        Assert.Equal(2, codigo);
        Assert.Equal("bien=no ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
        Assert.Equal("mudanza-encima", dice["clave"]);
        Assert.StartsWith("El servicio de esta PC no contesta: no se sabe qué base tiene", dice["error"]);
        // Más de una pregunta, y siempre a esta PC por localhost.
        Assert.True(_red.Preguntadas.Count(d => d == RedDePrueba.EstaPc) > 1, string.Join(", ", _red.Preguntadas));
        Assert.Equal([Origen, RedDePrueba.EstaPc], _red.Preguntadas.Distinct());
    }

    [Fact]
    public async Task Con_el_servicio_recien_instalado_y_sin_tienda_se_puede_traer()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>
    /// El único error con la clave «mudanza»: con el asistente a la vista se pregunta si apartarla, y con «Sí» se revisa otra vez con
    /// mudanza-encima=si. Aunque la tienda de aquí se llame igual: sin la nota, no se sabe que sea la misma base.
    /// </summary>
    [Theory]
    [InlineData("Otra papelería")]
    [InlineData("Matriz")]
    public async Task Sobre_una_pc_que_ya_tiene_tienda_no_se_trae_sin_confirmarlo(string laDeAqui)
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([laDeAqui]));

        var error = await NoSePuedeAsync("mudanza");

        Assert.StartsWith($"Esta PC ya tiene una tienda ({laDeAqui}): la base de CAJA2 no se trae encima sin confirmarlo.", error);
        Assert.Contains("/MUDANZAENCIMA=si", error);

        var (codigo, dice) = await RevisarAsync(Respuestas(("mudanza-encima", "si")));
        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=si", Resumen(dice));
    }

    // ── 3 bis. Si este paquete ya se puso aquí (la nota), no se trae otra vez ──

    [Fact]
    public async Task Con_la_nota_de_este_paquete_y_el_servicio_con_esa_tienda_ya_estaba()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=si ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>La base ya está puesta y el servicio no arrancó o tarda: se sigue con la configuración; si al final no contesta, lo dice la revisión del final.</summary>
    [Fact]
    public async Task Con_la_nota_ya_estaba_aunque_el_servicio_no_conteste()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal("si", dice["ya-estaba"]);
    }

    [Fact]
    public async Task Con_la_nota_pero_el_servicio_sin_tienda_no_esta_y_se_trae()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>La nota dice que se puso, pero el servicio contesta con OTRA tienda: esa no es la base del paquete, y no se le escribe su puente.</summary>
    [Fact]
    public async Task Con_la_nota_pero_el_servicio_con_otra_tienda_no_es_la_del_paquete()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Otra papelería"]));

        Assert.StartsWith("Esta PC ya tiene una tienda (Otra papelería)", await NoSePuedeAsync("mudanza"));

        var (codigo, dice) = await RevisarAsync(Respuestas(("mudanza-encima", "si")));
        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=si", Resumen(dice));
    }

    [Fact]
    public async Task La_nota_de_otro_paquete_no_cuenta()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota(paquete: "11111111111111111111111111111111");
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));

        Assert.StartsWith("Esta PC ya tiene una tienda (Matriz)", await NoSePuedeAsync("mudanza"));
    }

    /// <summary>Sin servicio nadie dice qué base es: con la nota y su pos.db, es la del paquete; si el pos.db ya no está, se trae.</summary>
    [Theory]
    [InlineData(true, "si", "no")]
    [InlineData(false, "no", "no")]
    public async Task Con_la_nota_y_sin_servicio_ya_estaba_solo_si_sigue_su_pos_db(bool conBase, string yaEstaba, string apartaBase)
    {
        PaqueteDePrueba.En(Usb);
        DejarLaNota();
        if (conBase)
            _pc.Archivo("pos.db", "la del paquete");

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        Assert.Equal($"bien=si ya-estaba={yaEstaba} ya-configurada=no aparta-base={apartaBase}", Resumen(dice));
    }

    // ── 3 ter. El puente de esta PC, como está hoy (Enmienda 7.2) ──
    // Traer encima de una PC que ya tiene el servicio: restaurar.ps1 lo arranca con la base del paquete y con el servicio.json que esta PC
    // tiene hoy, y la configuración de la mudanza (escribir --mudanza, con Cajas B→3) llega hasta después. Si su puente ya escribe, en ese
    // rato mandaría lo que viaja pendiente como otra caja de eleventa (no lo encontraría allá con ese número: quedaría dos veces) o a otra
    // base (se detendría). Se niega con la clave «mudanza-encima» (la página de la mudanza) y dice qué hacer.

    private const string ApagarPrimero = "Apaga primero el puente de esta PC como dice el instructivo («Para apagar el puente», revisando antes lo pendiente) y "
        + "vuelve a intentarlo; si al final no se trae, se vuelve a prender igual. No se cambió nada.";

    /// <summary>El servicio.json de hoy de esta PC, con su sección «Eleventa» así (y lo demás de una caja principal).</summary>
    private void PuenteDeEstaPc(JsonObject eleventa) =>
        _pc.Archivo("servicio.json", new JsonObject { ["Eleventa"] = eleventa, ["ControlPrint"] = new JsonObject { ["Servidor"] = "127.0.0.1" } }.ToJsonString());

    /// <summary>Como el de una PC que ya es caja principal y escribe en eleventa como su propia caja (la 5).</summary>
    private static JsonObject QueEscribeComoLa5(string laBase = PaqueteDePrueba.BaseDeEleventa) => new()
    {
        ["Activo"] = true, ["Escribir"] = true, ["Servidor"] = "127.0.0.1", ["Base"] = laBase, ["Caja"] = 5, ["Cajero"] = 1,
    };

    /// <summary>
    /// El caso: esta PC ya es caja principal con su tienda y escribe como la caja 5, y se trae encima la base de la caja 2 (que escribía
    /// como la 3). Ni confirmando que se aparte la de aquí: se dice antes de preguntarlo (la pregunta sale solo con la clave «mudanza»).
    /// </summary>
    [Theory]
    [InlineData("no")]
    [InlineData("si")]
    public async Task Con_el_puente_de_esta_pc_escribiendo_como_otra_caja_no_se_trae_encima(string encima)
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Otra papelería"]));
        PuenteDeEstaPc(QueEscribeComoLa5());
        var antes = File.ReadAllBytes(Path.Combine(_pc.Datos, "servicio.json"));

        var error = await NoSePuedeAsync("mudanza-encima", Respuestas(("mudanza-encima", encima)));

        Assert.Equal("El puente con eleventa de esta PC ya escribe (Eleventa:Escribir en servicio.json): al poner aquí la base de CAJA2, antes de que quede su "
            + "configuración, lo pendiente de «Caja 2» (serie B) se escribiría en eleventa como la caja 5 y no como la 3 de allá, y quedaría dos veces. "
            + ApagarPrimero, error);
        Assert.DoesNotContain(RevisionDeLaMudanza.YaTieneUnaTienda, error);
        // Revisar no cambia nada.
        Assert.Equal(antes, File.ReadAllBytes(Path.Combine(_pc.Datos, "servicio.json")));
        Assert.False(File.Exists(Path.Combine(_pc.Datos, "servicio.json.anterior")));
    }

    /// <summary>Sobre un servicio sin tienda (recién instalado, o con la base vacía) también: restaurar.ps1 lo arranca igual con este servicio.json.</summary>
    [Fact]
    public async Task Tambien_sobre_un_servicio_sin_tienda()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        PuenteDeEstaPc(QueEscribeComoLa5());

        Assert.Contains("como la caja 5 y no como la 3 de allá", await NoSePuedeAsync("mudanza-encima"));
    }

    /// <summary>
    /// El reintento de la guía después de un «escribir --mudanza» que ya quedó (sin la nota de este paquete): ya escribe, pero con las
    /// mismas Cajas (B→3) y la misma base que el paquete. No estorba, y se aparta la tienda de aquí como siempre, confirmándolo.
    /// </summary>
    [Fact]
    public async Task Con_el_puente_de_esta_pc_escribiendo_como_la_caja_de_alla_si_se_trae()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        var eleventa = QueEscribeComoLa5();
        eleventa.Remove("Caja");
        eleventa["Cajas"] = new JsonObject { ["B"] = new JsonObject { ["Caja"] = 3 } };
        PuenteDeEstaPc(eleventa);

        Assert.StartsWith(RevisionDeLaMudanza.YaTieneUnaTienda, await NoSePuedeAsync("mudanza"));
        var (codigo, dice) = await RevisarAsync(Respuestas(("mudanza-encima", "si")));
        Assert.Equal(0, codigo);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=si", Resumen(dice));
    }

    /// <summary>
    /// Lo que no escribe no estorba: sin sección «Eleventa» (el reintento después de un traer que no puso la base), con «Escribir» en false
    /// (como queda la sección nueva que escribe el instalador, o el puente ya apagado con el instructivo), o sin cajero para la caja 5 (sin
    /// cajero esa caja no escribe).
    /// </summary>
    [Theory]
    [InlineData("sin-seccion")]
    [InlineData("sin-escribir")]
    [InlineData("sin-cajero")]
    [InlineData("caja-0")]
    public async Task Lo_que_no_escribe_no_estorba(string caso)
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        var eleventa = QueEscribeComoLa5();
        switch (caso)
        {
            case "sin-seccion":
                _pc.Archivo("servicio.json", """{ "ControlPrint": { "Servidor": "127.0.0.1" } }""");
                break;
            case "sin-escribir":
                eleventa["Escribir"] = false;
                PuenteDeEstaPc(eleventa);
                break;
            case "sin-cajero":
                eleventa.Remove("Cajero");
                PuenteDeEstaPc(eleventa);
                break;
            case "caja-0":
                eleventa["Caja"] = 0;
                PuenteDeEstaPc(eleventa);
                break;
        }

        var (codigo, dice) = await RevisarAsync();

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>«Escribir» escrito como texto («"true"») cuenta como lo lee el servicio, y la caja de su entrada en Cajas sin distinguir mayúsculas.</summary>
    [Fact]
    public async Task Escribir_como_texto_y_la_serie_en_minusculas_cuentan_como_las_lee_el_servicio()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        var eleventa = QueEscribeComoLa5();
        eleventa["Escribir"] = "true";
        eleventa["Cajas"] = new JsonObject { ["b"] = new JsonObject { ["Caja"] = 4, ["Cajero"] = 7 } };
        PuenteDeEstaPc(eleventa);

        Assert.Contains("como la caja 4 y no como la 3 de allá", await NoSePuedeAsync("mudanza-encima"));
    }

    /// <summary>
    /// Con las mismas Cajas, pero escribiendo en otra base de eleventa: lo que viaja pendiente es de la base de la bandeja, y con otra el
    /// puente se detiene al arrancar (y esa detención llega con la base). Sin nada pendiente ni por revisar, no.
    /// </summary>
    [Theory]
    [InlineData("2", "0", false)]
    [InlineData("0", "1", false)]
    [InlineData("0", "0", true)]
    public async Task Con_el_puente_de_esta_pc_escribiendo_en_otra_base_no_se_trae_lo_pendiente(string pendientes, string porRevisar, bool sePuede)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-pendientes", pendientes), ("eleventa-por-revisar", porRevisar));
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        var eleventa = QueEscribeComoLa5(@"C:\eleventa\PDVDATA-ENSAYO.FDB");
        eleventa.Remove("Caja");
        eleventa["Cajas"] = new JsonObject { ["B"] = new JsonObject { ["Caja"] = 3 } };
        PuenteDeEstaPc(eleventa);

        if (sePuede)
            await SePuedeAsync();
        else
            Assert.Equal(@"El puente con eleventa de esta PC ya escribe (Eleventa:Escribir en servicio.json) en otra base de eleventa («C:\eleventa\PDVDATA-ENSAYO.FDB»): "
                + $"lo que viaja pendiente o por revisar en la base de CAJA2 es de «{PaqueteDePrueba.BaseDeEleventa}», y al arrancar con ella el puente se "
                + "detendría y habría que reanudarlo a mano. " + ApagarPrimero, await NoSePuedeAsync("mudanza-encima"));
    }

    /// <summary>La misma base escrita como la ve Windows (como la compara el puente) es la misma; y sin base, este puente no escribe a ningún lado.</summary>
    [Theory]
    [InlineData(@"c:\program files (x86)\abarrotesmulticaja\db\pdvdata.fdb ")]
    [InlineData("")]
    public async Task La_misma_base_escrita_de_otra_forma_o_sin_base_no_estorba(string laDeAqui)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-pendientes", "2"));
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        var eleventa = QueEscribeComoLa5(laDeAqui);
        eleventa.Remove("Caja");
        eleventa["Cajas"] = new JsonObject { ["B"] = new JsonObject { ["Caja"] = 3 } };
        PuenteDeEstaPc(eleventa);

        await SePuedeAsync();
    }

    /// <summary>Con la nota de este paquete la base ya está puesta (ya-estaba): restaurar.ps1 no corre otra vez, y el puente de hoy ya no estorba.</summary>
    [Fact]
    public async Task Con_la_base_del_paquete_ya_puesta_el_puente_de_hoy_no_estorba()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        PuenteDeEstaPc(QueEscribeComoLa5());

        var (codigo, dice) = await RevisarAsync();

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=si ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>
    /// Sin el servicio registrado (el caso de la guía: la principal de la tienda no tiene el POS) no hay servicio que arranque con este
    /// servicio.json antes de la configuración de la mudanza: no cambia nada.
    /// </summary>
    [Fact]
    public async Task Sin_el_servicio_registrado_el_servicio_json_que_quedo_no_estorba()
    {
        PaqueteDePrueba.En(Usb);
        PuenteDeEstaPc(QueEscribeComoLa5());

        var (codigo, dice) = await RevisarAsync();

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>
    /// Un servicio.json mal escrito no se decide aquí: lo dice la revisión del servicio.json que va a quedar, con lo de siempre, y con él
    /// no se trae nada.
    /// </summary>
    [Fact]
    public async Task Con_el_servicio_y_su_servicio_json_mal_escrito_se_dice_lo_de_siempre()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        _pc.Archivo("servicio.json", "{ \"Eleventa\": { \"Escribir\": tru } }");

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(2, codigo);
        Assert.Equal("bien=no ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
        Assert.StartsWith("servicio.json está mal escrito", dice["error"]);
        Assert.EndsWith("Corrígelo o quítalo y vuelve a correr el instalador; no se cambió nada.", dice["error"]);
    }

    // ── 4. El servicio.json que va a quedar ──

    [Fact]
    public async Task Si_alla_el_puente_estaba_prendido_no_se_puede_contestar_que_no_a_eleventa()
    {
        PaqueteDePrueba.En(Usb);

        var error = await NoSePuedeAsync("eleventa", Respuestas(("eleventa", "no")));

        Assert.StartsWith("En CAJA2 el puente con eleventa estaba prendido y escribiendo", error);
        Assert.Contains("no se puede contestar «No» a eleventa", error);
    }

    /// <summary>También si allá solo escribía (sin leer): lo que cobre la caja que se mudó tiene que seguir llegando a eleventa.</summary>
    [Theory]
    [InlineData("si", "no")]
    [InlineData("no", "si")]
    public async Task Con_el_puente_de_alla_solo_leyendo_o_solo_escribiendo_tampoco(string eleventa, string escribir)
    {
        PaqueteDePrueba.En(Usb, ("eleventa", eleventa), ("eleventa-escribir", escribir));

        await NoSePuedeAsync("eleventa", Respuestas(("eleventa", "no")));
    }

    [Fact]
    public async Task Si_alla_no_habia_puente_aqui_se_puede_contestar_que_no()
    {
        PaqueteDePrueba.En(Usb, ("eleventa", "no"), ("eleventa-escribir", "no"));

        await SePuedeAsync(Respuestas(("eleventa", "no")));
    }

    [Fact]
    public async Task Con_el_puente_escribiendo_la_base_de_eleventa_no_se_cambia()
    {
        PaqueteDePrueba.En(Usb);

        var error = await NoSePuedeAsync("eleventa-base", Respuestas(("eleventa-base", @"C:\eleventa\PDVDATA-ENSAYO.FDB")));

        Assert.StartsWith("En una mudanza la base de eleventa no se cambia: en CAJA2 el puente escribía en «" + PaqueteDePrueba.BaseDeEleventa
            + @"» y aquí dice «C:\eleventa\PDVDATA-ENSAYO.FDB».", error);
    }

    /// <summary>Como la compara el puente (ServidorEleventa.RutaDe): sin los espacios ni los puntos del final, y sin distinguir mayúsculas.</summary>
    [Theory]
    [InlineData(@"c:\program files (x86)\abarrotesmulticaja\db\pdvdata.fdb")]
    [InlineData(@"  C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB . ")]
    public async Task La_misma_base_escrita_como_la_ve_windows_es_la_misma(string aqui)
    {
        PaqueteDePrueba.En(Usb);

        await SePuedeAsync(Respuestas(("eleventa-base", aqui)));
    }

    /// <summary>
    /// La bandeja viaja en la base anotada con su ruta: si allá la configuración ya decía otra (su puente estaba detenido por eso), aquí
    /// ninguna ruta sirve a las dos, y se dice que eso se arregla allá, antes de mudar la base.
    /// </summary>
    [Fact]
    public async Task La_base_de_eleventa_tambien_tiene_que_ser_la_de_la_bandeja_que_viaja()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-base-de-la-bandeja", @"C:\eleventa\OTRA.FDB"));

        var error = await NoSePuedeAsync("eleventa-base");

        Assert.Contains(@"lo que viaja en la bandeja es de «C:\eleventa\OTRA.FDB»", error);
        Assert.EndsWith($"En CAJA2 la configuración ya decía otra («{PaqueteDePrueba.BaseDeEleventa}»): eso se arregla allá, antes de mudar la base "
            + "(cancela la mudanza allá, corrígelo y sácala otra vez).", error);
        // Con la de la bandeja tampoco: no es la de la configuración de allá.
        Assert.StartsWith("En una mudanza la base de eleventa no se cambia: en CAJA2 el puente escribía en",
            await NoSePuedeAsync("eleventa-base", Respuestas(("eleventa-base", @"C:\eleventa\OTRA.FDB"))));
    }

    /// <summary>Si allá la configuración no decía la base (y la bandeja sí), la que vale es la de la bandeja.</summary>
    [Fact]
    public async Task Sin_base_en_la_configuracion_de_alla_vale_la_de_la_bandeja()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-base", ""), ("eleventa-base-de-la-bandeja", @"C:\eleventa\OTRA.FDB"));

        var error = await NoSePuedeAsync("eleventa-base");

        Assert.EndsWith("Con otra ruta el puente llegaría detenido.", error);
        await SePuedeAsync(Respuestas(("eleventa-base", @"c:\eleventa\otra.fdb")));
    }

    /// <summary>Solo leyendo y sin nada pendiente, la base de eleventa sí se puede cambiar; con algo pendiente o por revisar, no.</summary>
    [Theory]
    [InlineData("0", "0", true)]
    [InlineData("2", "0", false)]
    [InlineData("0", "1", false)]
    public async Task Con_el_puente_solo_leyendo_la_base_se_cambia_si_no_viaja_nada_pendiente(string pendientes, string porRevisar, bool sePuede)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-escribir", "no"), ("eleventa-pendientes", pendientes), ("eleventa-por-revisar", porRevisar));
        var respuestas = Respuestas(("eleventa-base", @"C:\eleventa\PDVDATA-ENSAYO.FDB"));

        if (sePuede)
            await SePuedeAsync(respuestas);
        else
            Assert.Contains("hay cosas pendientes para", await NoSePuedeAsync("eleventa-base", respuestas));
    }

    [Fact]
    public async Task Con_el_puente_escribiendo_hace_falta_el_gbak_de_esta_pc()
    {
        PaqueteDePrueba.En(Usb);
        const string dice = "Con el puente escribiendo hace falta el gbak de Firebird 2.0 de esta PC: sin el respaldo del día no se manda nada.";

        Assert.Equal($"{dice} Falta su ruta.", await NoSePuedeAsync("eleventa-gbak", Respuestas(("eleventa-gbak", ""))));
        // El de allá no sirve aquí: tiene que existir en esta PC.
        var deAlla = Path.Combine(_carpeta.Ruta, "no-esta", "gbak.exe");
        Assert.Equal($"{dice} No está «{deAlla}».", await NoSePuedeAsync("eleventa-gbak", Respuestas(("eleventa-gbak", deAlla))));
    }

    [Fact]
    public async Task Con_el_puente_solo_leyendo_el_gbak_puede_faltar()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-escribir", "no"));

        await SePuedeAsync(Respuestas(("eleventa-gbak", "")));
    }

    /// <summary>
    /// La caja de omisión de esta PC no puede ser la de una caja que se mudó: toda caja que no esté en Cajas (la de esta PC, al darla de
    /// alta) escribiría en eleventa como la caja 2, en su turno.
    /// </summary>
    [Fact]
    public async Task El_numero_de_caja_de_esta_pc_no_puede_ser_el_de_una_caja_que_se_mudo()
    {
        PaqueteDePrueba.En(Usb);

        var error = await NoSePuedeAsync("eleventa-caja", Respuestas(("eleventa-caja", "3")));

        Assert.StartsWith("La caja 3 de eleventa es la de «Caja 2» (serie B), que llega con la base", error);
        await SePuedeAsync(Respuestas(("eleventa-caja", "1")));
        await SePuedeAsync(Respuestas(("eleventa-caja", "")));
    }

    [Fact]
    public async Task Con_el_puente_escribiendo_cada_caja_que_se_muda_necesita_su_cajero()
    {
        // Allá no había cajero de omisión, y aquí tampoco se contesta.
        PaqueteDePrueba.En(Usb, ("eleventa-cajero", ""));

        var error = await NoSePuedeAsync("eleventa-cajero", Respuestas(("eleventa-cajero", "")));

        Assert.Equal("Con el puente escribiendo, a «Caja 2» (serie B) le falta el cajero de eleventa: pon el número del cajero.", error);
        await SePuedeAsync(Respuestas(("eleventa-cajero", "4")));
    }

    [Theory]
    // El de omisión de allá, si aquí se deja vacío.
    [InlineData("1", "B=3")]
    // O el suyo propio, el de su entrada de Cajas.
    [InlineData("", "B=3/7")]
    public async Task El_cajero_puede_ser_el_de_omision_de_alla_o_el_propio_de_la_caja(string deOmisionAlla, string cajas)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-cajero", deOmisionAlla), ("eleventa-cajas", cajas));

        await SePuedeAsync(Respuestas(("eleventa-cajero", "")));
    }

    // ── 4 bis. Con la configuración de este paquete ya escrita aquí (la Enmienda 7.1 bis) ──
    // La nota mudanza-configurada.txt la deja instalar-pc.ps1 después de un escribir --mudanza que salió bien. Con la de ESTE paquete y su base
    // ya puesta (ya-estaba), el escribir de después va sin --mudanza: no vuelve a poner el puente de allá encima de lo que el dueño cambió
    // después (como apagarlo con el instructivo). La revisión lo dice («ya-configurada=si», de donde lo toma instalar-pc.ps1) y revisa eso
    // mismo: lo de siempre, sin lo del paquete.

    /// <summary>
    /// La principal semanas después de la mudanza: la base del paquete ya puesta (su nota; sin el servicio registrado, con su pos.db) y el
    /// servicio.json que dejó el escribir --mudanza (Cajas B→3), con el puente ya apagado con el instructivo (Activo y Escribir en false).
    /// </summary>
    private string ConElPuenteApagadoDespues(bool conServicio = true)
    {
        PaqueteDePrueba.En(Usb);
        DejarLaNota();
        if (conServicio)
            _pc.ConServicio();
        else
            _pc.Archivo("pos.db", "la del paquete");
        PuenteDeEstaPc(new JsonObject
        {
            ["Activo"] = false, ["Escribir"] = false, ["Servidor"] = "127.0.0.1", ["Base"] = PaqueteDePrueba.BaseDeEleventa, ["Cajero"] = 1,
            ["Cajas"] = new JsonObject { ["B"] = new JsonObject { ["Caja"] = 3 } },
        });
        return Path.Combine(_pc.Datos, ServicioJson.Archivo);
    }

    /// <summary>
    /// El caso de la revisión (M5): se trae otra vez el mismo paquete, que llegaba escribiendo, y el puente de aquí ya se apagó. Sin la nota
    /// de su configuración, como siempre: «No» a eleventa no se puede. Con ella sí, conteste o no el servicio, y también sin el servicio
    /// registrado.
    /// </summary>
    [Theory]
    [InlineData("contesta")]
    [InlineData("no-contesta")]
    [InlineData("sin-servicio")]
    public async Task Con_su_configuracion_ya_escrita_aqui_se_puede_contestar_que_no_a_eleventa(string servicio)
    {
        ConElPuenteApagadoDespues(conServicio: servicio != "sin-servicio");
        if (servicio == "contesta")
            _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        var conNo = Respuestas(("eleventa", "no"));
        Assert.StartsWith("En CAJA2 el puente con eleventa estaba prendido y escribiendo", await NoSePuedeAsync("eleventa", conNo));

        DejarLaNotaDeConfigurada();
        var (codigo, dice) = await RevisarAsync(conNo);

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=si ya-configurada=si aparta-base=no", Resumen(dice));
    }

    /// <summary>
    /// Con «Sí» tampoco se piden las reglas del paquete, como la base de eleventa de allá o el gbak (el dueño pudo quitarlo después): se
    /// revisa lo de siempre de esta PC. Sin la nota, cada una se niega como siempre.
    /// </summary>
    [Theory]
    [InlineData("eleventa-gbak", "")]
    [InlineData("eleventa-base", @"C:\eleventa\PDVDATA-ENSAYO.FDB")]
    public async Task Con_su_configuracion_ya_escrita_ya_no_se_piden_la_base_ni_el_gbak_de_alla(string clave, string valor)
    {
        ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        var respuestas = Respuestas((clave, valor));
        await NoSePuedeAsync(clave, respuestas);

        DejarLaNotaDeConfigurada();
        var (codigo, dice) = await RevisarAsync(respuestas);

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=si ya-configurada=si aparta-base=no", Resumen(dice));
    }

    /// <summary>
    /// Lo que revisa es lo que va a escribir el escribir sin --mudanza, con sus reglas: el número de esta PC en eleventa no puede ser el de
    /// una caja de Eleventa:Cajas (la regla de la corrida de después de traer, no la del paquete). Lo dice igual que «revisar» sin --mudanza.
    /// </summary>
    [Fact]
    public async Task Con_su_configuracion_ya_escrita_revisa_lo_mismo_que_el_escribir_sin_mudanza()
    {
        ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        DejarLaNotaDeConfigurada();
        var respuestas = Respuestas(("eleventa-caja", "3"));

        var error = await NoSePuedeAsync("eleventa-caja", respuestas);

        Assert.StartsWith("La caja 3 de eleventa ya es la de la caja del POS con la serie B (en Eleventa:Cajas)", error);
        var sinMudanza = Ayudante.Revisar(respuestas, _pc.Datos);
        Assert.Equal((error, "eleventa-caja"), (sinMudanza["error"], sinMudanza["clave"]));
    }

    /// <summary>
    /// Y lo que revisa es lo que queda: con «No», el escribir que corre después instalar-pc.ps1 (sin --mudanza, por «ya-configurada») sale
    /// bien y deja servicio.json como estaba, con el puente apagado. Revisar no toca la nota.
    /// </summary>
    [Fact]
    public async Task Con_su_configuracion_ya_escrita_el_escribir_de_despues_deja_el_puente_apagado()
    {
        var servicioJson = ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        var nota = DejarLaNotaDeConfigurada();
        var (antes, notaDeAntes) = (File.ReadAllBytes(servicioJson), File.ReadAllBytes(nota));

        var (codigo, dice) = await RevisarAsync(Respuestas(("eleventa", "no")));
        Assert.True(codigo == 0 && dice["ya-configurada"] == "si", dice["error"]);
        Assert.Equal(notaDeAntes, File.ReadAllBytes(nota));
        // Con las mismas respuestas (las que guardó RevisarAsync), como lo corre instalar-pc.ps1.
        Assert.Equal(0, await Ayudante.CorrerAsync(["escribir", "--respuestas", Path.Combine(_carpeta.Ruta, "respuestas.txt"), "--salida", Salida], _pc, _red));

        var escrito = ArchivoDeClaves.Leer(Salida);
        Assert.Equal(("", "igual"), (escrito["error"], escrito["servicio-json"]));
        Assert.Equal(antes, File.ReadAllBytes(servicioJson));
    }

    /// <summary>La nota de la configuración de OTRO paquete no cuenta: se revisa con lo del paquete, como siempre.</summary>
    [Fact]
    public async Task La_nota_de_configurada_de_otro_paquete_no_cuenta()
    {
        ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        DejarLaNotaDeConfigurada("11111111111111111111111111111111");

        Assert.StartsWith("En CAJA2 el puente con eleventa estaba prendido", await NoSePuedeAsync("eleventa", Respuestas(("eleventa", "no"))));
        var (codigo, dice) = await RevisarAsync();
        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("bien=si ya-estaba=si ya-configurada=no aparta-base=no", Resumen(dice));
    }

    /// <summary>El id de la nota se compara sin distinguir mayúsculas y sin los espacios de más (instalar-pc.ps1 lo compara con -eq).</summary>
    [Theory]
    [InlineData("paquete=0F8FAD5BD9CB469FA16570867728950E\r\n")]
    [InlineData("# Papeleria POS\r\npaquete=  0f8fad5bd9cb469fa16570867728950e \r\n")]
    public async Task La_nota_de_configurada_se_lee_como_la_escriben_los_scripts(string nota)
    {
        ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        File.WriteAllText(DejarLaNotaDeConfigurada(), nota, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var (codigo, dice) = await RevisarAsync();

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal("si", dice["ya-configurada"]);
    }

    /// <summary>
    /// Solo cuenta con la base del paquete ya puesta. Si se trae (aquí no estaba su nota, el servicio contesta sin tienda, o sin servicio ya
    /// no está su pos.db), se escribe con su configuración (escribir --mudanza), con o sin la nota, y se revisa con lo del paquete, como
    /// siempre. Entonces ni se lee: aunque no se deje leer, no estorba.
    /// </summary>
    [Theory]
    [InlineData("sin-la-nota-de-la-base")]
    [InlineData("sin-tienda")]
    [InlineData("sin-pos-db")]
    public async Task Sin_la_base_ya_puesta_la_nota_de_configurada_no_cuenta(string caso)
    {
        PaqueteDePrueba.En(Usb);
        if (caso != "sin-la-nota-de-la-base")
            DejarLaNota();
        if (caso != "sin-pos-db")
        {
            _pc.ConServicio();
            _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado([], tiendaNueva: true));
        }
        var conNo = Respuestas(("eleventa", "no"));

        using (new FileStream(DejarLaNotaDeConfigurada(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.StartsWith("En CAJA2 el puente con eleventa estaba prendido", await NoSePuedeAsync("eleventa", conNo));
            var (codigo, dice) = await RevisarAsync();
            Assert.True(codigo == 0, dice["error"]);
            Assert.Equal("bien=si ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
        }
    }

    /// <summary>
    /// Con la base ya puesta, la nota de la configuración que está y no se deja leer (la tiene tomada otro programa) no se adivina: de ella
    /// depende si se vuelve a poner el puente de allá. Se dice, como con la nota de la base puesta.
    /// </summary>
    [Fact]
    public async Task Si_la_nota_de_configurada_no_se_deja_leer_no_se_adivina()
    {
        ConElPuenteApagadoDespues();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        string error;
        using (new FileStream(DejarLaNotaDeConfigurada(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            error = await NoSePuedeAsync("mudanza-encima");

        Assert.StartsWith($"No se pudo leer {Path.Combine(_pc.Datos, "mudanza-configurada.txt")} (la nota de que la configuración de este paquete ya se "
            + "escribió aquí)", error);
    }

    // ── Lo demás ──

    [Fact]
    public async Task Una_respuesta_que_no_se_puede_usar_se_dice_con_su_clave_antes_que_nada()
    {
        var error = await NoSePuedeAsync("eleventa-caja", Respuestas(("eleventa-caja", "tres")));

        Assert.StartsWith("Una respuesta no se puede usar: El número de la caja en eleventa va con cifras", error);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>
    /// A «Otra caja» no se le trae la base. Se corrige en «Qué es esta PC» (la clave «rol»): la clave «mudanza» de mudanza-revisar es
    /// solo la de «Esta PC ya tiene una tienda», con la que el asistente pregunta si se aparta.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("traer")]
    public async Task A_otra_caja_no_se_le_trae_la_base(string mudanza)
    {
        PaqueteDePrueba.En(Usb);
        var respuestas = new ArchivoDeClaves().Poner("rol", "otra").Poner("principal", "192.0.2.50").Poner("lector", "teclado")
            .Poner("mudanza", mudanza).Poner("mudanza-paquete", Usb);

        Assert.Contains("no «Otra caja»", await NoSePuedeAsync("rol", respuestas));
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>
    /// De todos los errores, solo el de «Esta PC ya tiene una tienda» sale con la clave «mudanza» (el asistente pregunta entonces si se
    /// aparta la de aquí): una respuesta «mudanza» que no cuadra sale con «rol».
    /// </summary>
    [Theory]
    [InlineData("principal", "llevar")]
    [InlineData("principal", "terminar")]
    public async Task Una_respuesta_mudanza_que_no_cuadra_no_sale_con_la_clave_de_la_pregunta(string rol, string mudanza)
    {
        PaqueteDePrueba.En(Usb);

        var error = await NoSePuedeAsync("rol", Respuestas(("rol", rol), ("mudanza", mudanza)));

        Assert.StartsWith("Una respuesta no se puede usar: ", error);
        Assert.DoesNotContain(RevisionDeLaMudanza.YaTieneUnaTienda, error);
    }

    /// <summary>El servicio.json de esta PC mal escrito se descubre aquí, antes de traer la base, y no después.</summary>
    [Fact]
    public async Task Un_servicio_json_mal_escrito_en_esta_pc_se_dice_antes_de_traer_nada()
    {
        PaqueteDePrueba.En(Usb);
        var ruta = _pc.Archivo("servicio.json", "{ \"Eleventa\": ");

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(2, codigo);
        Assert.Contains("servicio.json está mal escrito", dice["error"]);
        Assert.Equal("{ \"Eleventa\": ", File.ReadAllText(ruta));
    }

    /// <summary>Revisar no escribe: con todo bien, servicio.json y caja.json quedan como estaban, sin «.anterior» ni «.nuevo».</summary>
    [Fact]
    public async Task Revisar_no_escribe_ni_servicio_json_ni_caja_json()
    {
        PaqueteDePrueba.En(Usb);
        var servicio = _pc.Archivo("servicio.json", "{\r\n  // la plantilla\r\n}\r\n");
        var caja = _pc.Archivo(Path.Combine("Caja", "caja.json"), """{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "" }""");

        await SePuedeAsync();

        Assert.Equal("{\r\n  // la plantilla\r\n}\r\n", File.ReadAllText(servicio));
        Assert.Equal("""{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "" }""", File.ReadAllText(caja));
        Assert.Equal(["Caja", "servicio.json"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName).Order());
        Assert.Equal(["caja.json"], Directory.GetFiles(Path.Combine(_pc.Datos, "Caja")).Select(Path.GetFileName));
    }

    /// <summary>Lo que no cumple el servicio.json que va a quedar tampoco deja «ya-estaba»: con error no hay nada que seguir.</summary>
    [Fact]
    public async Task Con_la_base_ya_puesta_un_error_de_la_configuracion_sigue_siendo_error()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        DejarLaNota();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));

        await NoSePuedeAsync("eleventa-gbak", Respuestas(("eleventa-gbak", "")));
    }

    /// <summary>
    /// La nota está, pero no se deja leer (la tiene tomada otro programa): no se adivina si la base ya se puso. Se dice, y no se trae
    /// nada encima.
    /// </summary>
    [Fact]
    public async Task Si_la_nota_no_se_deja_leer_no_se_adivina()
    {
        PaqueteDePrueba.En(Usb);
        _pc.ConServicio();
        _red.Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Matriz"]));
        string error;
        using (new FileStream(_pc.DejarLaNota(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            error = await NoSePuedeAsync("mudanza-encima");

        Assert.StartsWith($"No se pudo leer {Path.Combine(_pc.Datos, "mudanza-puesta.txt")} (la nota de que este paquete ya se puso aquí)", error);
        // Ni se le preguntó al servicio: sin saber eso no hay nada que decidir.
        Assert.DoesNotContain(RedDePrueba.EstaPc, _red.Preguntadas);
    }

    /// <summary>Un servicio.json que no se deja leer se dice igual, con todas las claves de la salida y sin una traza.</summary>
    [Fact]
    public async Task Si_servicio_json_no_se_deja_leer_lo_dice_con_todas_sus_claves()
    {
        PaqueteDePrueba.En(Usb);
        var ruta = _pc.Archivo("servicio.json", "{ }");
        int codigo;
        ArchivoDeClaves dice;
        using (new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            (codigo, dice) = await RevisarAsync();

        Assert.Equal(2, codigo);
        Assert.Equal(["bien", "ya-estaba", "ya-configurada", "aparta-base", "archivo", "equipo", "paquete", "paquete-id", "tienda", "error", "clave"],
            dice.Datos.Select(d => d.Clave));
        Assert.Equal("bien=no ya-estaba=no ya-configurada=no aparta-base=no", Resumen(dice));
        Assert.StartsWith("No se pudo leer un archivo de esta PC: ", dice["error"]);
        Assert.Equal("CAJA2", dice["equipo"]);
        Assert.Equal("{ }", File.ReadAllText(ruta));
    }

    /// <summary>Lo que falla del disco también queda en la salida, con su código 2: quien lo corre nunca se queda sin saber qué pasó.</summary>
    [Fact]
    public async Task Si_no_esta_el_archivo_de_las_respuestas_lo_dice_y_sale_con_2()
    {
        PaqueteDePrueba.En(Usb);

        var codigo = await Ayudante.CorrerAsync(["mudanza-revisar", "--paquete", Usb, "--respuestas", Path.Combine(_carpeta.Ruta, "no-hay.txt"),
            "--version", "1.0.180", "--salida", Salida], _pc, _red, RespuestasDePrueba.SinEsperar);

        Assert.Equal(2, codigo);
        Assert.Contains("no-hay.txt", ArchivoDeClaves.Leer(Salida)["error"]);
    }

    [Theory]
    [InlineData("mudanza-revisar", "--respuestas", "r.txt", "--version", "1.0.180", "--salida", "s.txt")]
    [InlineData("mudanza-revisar", "--paquete", "usb", "--version", "1.0.180", "--salida", "s.txt")]
    [InlineData("mudanza-revisar", "--paquete", "usb", "--respuestas", "r.txt", "--salida", "s.txt")]
    [InlineData("mudanza-revisar", "--paquete", "usb", "--respuestas", "r.txt", "--version", "1.0.180")]
    public async Task Mal_dada_la_orden_sale_con_1(params string[] argumentos) =>
        Assert.Equal(1, await Ayudante.CorrerAsync(argumentos, _pc, _red));
}

/// <summary>
/// Lo mismo, contra el servicio de verdad en memoria: lo que <c>mudanza-revisar</c> entiende de su <c>api/estado</c> (las sucursales y
/// «tiendaNueva») es lo que el servicio contesta.
/// </summary>
public sealed class MudanzaRevisarConElServicioTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly RedDePrueba _red = new();

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    /// <summary>La tienda de esa base como la escribe quien saca el paquete: sus sucursales por nombre, unidas con «; ».</summary>
    private static async Task<string> TiendaAsync(ServicioDePrueba deCual)
    {
        await using var db = deCual.Base();
        return string.Join("; ", await db.Sucursales.OrderBy(s => s.Nombre).Select(s => s.Nombre).ToListAsync());
    }

    private async Task<(int Codigo, ArchivoDeClaves Dice)> RevisarAsync(PcDePrueba pc, params (string Clave, string Valor)[] cambios)
    {
        var archivo = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        RespuestasDePrueba.ParaTraer(Usb, RespuestasDePrueba.Gbak(_carpeta.Ruta), cambios).Guardar(archivo);
        var codigo = await Ayudante.CorrerAsync(["mudanza-revisar", "--paquete", Usb, "--respuestas", archivo, "--version", "1.0.180", "--salida", Salida], pc,
            _red, RespuestasDePrueba.ConPaciencia);
        return (codigo, ArchivoDeClaves.Leer(Salida));
    }

    [Fact]
    public async Task Si_el_servicio_de_donde_salio_la_base_sigue_vivo_con_su_tienda_el_paquete_no_sirve()
    {
        var tienda = await TiendaAsync(servicio);
        PaqueteDePrueba.En(Usb, ("tienda", tienda));
        _red.EsElServicio(PaqueteDePrueba.IpDelOrigen + ":5080", servicio.Fabrica.Server.CreateHandler());

        var (codigo, dice) = await RevisarAsync(new PcDePrueba(_carpeta.Ruta));

        Assert.Equal(2, codigo);
        Assert.Equal("mudanza-paquete", dice["clave"]);
        Assert.StartsWith("CAJA2 (192.0.2.77) volvió a trabajar con su base", dice["error"]);
        Assert.Equal(tienda, dice["tienda"]);
    }

    [Fact]
    public async Task Sobre_el_servicio_de_esta_pc_con_su_tienda_no_se_trae_sin_confirmarlo_y_con_la_nota_ya_estaba()
    {
        var tienda = await TiendaAsync(servicio);
        Assert.NotEqual("", tienda);
        PaqueteDePrueba.En(Usb, ("tienda", tienda));
        var pc = new PcDePrueba(_carpeta.Ruta).ConServicio();
        _red.EsElServicio(RedDePrueba.EstaPc, servicio.Fabrica.Server.CreateHandler());

        var (codigo, dice) = await RevisarAsync(pc);

        Assert.Equal(2, codigo);
        Assert.Equal("mudanza", dice["clave"]);
        Assert.StartsWith($"Esta PC ya tiene una tienda ({tienda})", dice["error"]);

        // Confirmándolo, se aparta la de aquí.
        (codigo, dice) = await RevisarAsync(pc, ("mudanza-encima", "si"));
        Assert.True(codigo == 0 && dice["aparta-base"] == "si" && dice["ya-estaba"] == "no", dice["error"]);

        // Y si es la del paquete, que ya se puso aquí (la nota de instalar-pc.ps1): no se trae otra vez.
        pc.Archivo("mudanza-puesta.txt", $"paquete={PaqueteDePrueba.Id}\r\nequipo=CAJA2\r\n");
        (codigo, dice) = await RevisarAsync(pc);
        Assert.True(codigo == 0 && dice["ya-estaba"] == "si" && dice["ya-configurada"] == "no" && dice["aparta-base"] == "no", dice["error"]);

        // Con su configuración también escrita aquí (la otra nota), esa no se vuelve a poner: también se puede contestar «No» a eleventa.
        pc.Archivo("mudanza-configurada.txt", $"paquete={PaqueteDePrueba.Id}\r\n");
        (codigo, dice) = await RevisarAsync(pc, ("eleventa", "no"));
        Assert.True(codigo == 0 && dice["ya-estaba"] == "si" && dice["ya-configurada"] == "si" && dice["aparta-base"] == "no", dice["error"]);
    }

    /// <summary>La base como la deja el servicio recién instalado en una PC nueva (sin sucursal ni usuarios): dice «tiendaNueva», y ahí sí se trae.</summary>
    [Fact]
    public async Task Sobre_el_servicio_recien_instalado_sin_tienda_se_puede_traer()
    {
        await using var nuevo = await ServicioDePrueba.CrearSinTiendaAsync();
        PaqueteDePrueba.En(Usb);
        var pc = new PcDePrueba(_carpeta.Ruta).ConServicio();
        _red.EsElServicio(RedDePrueba.EstaPc, nuevo.Fabrica.Server.CreateHandler());

        var (codigo, dice) = await RevisarAsync(pc);

        Assert.True(codigo == 0 && dice["bien"] == "si" && dice["ya-estaba"] == "no" && dice["aparta-base"] == "no", dice["error"]);
        // Con las notas de este paquete tampoco «ya estaba»: la base que contesta no tiene tienda, y se trae con su configuración.
        pc.Archivo("mudanza-puesta.txt", $"paquete={PaqueteDePrueba.Id}\r\n");
        pc.Archivo("mudanza-configurada.txt", $"paquete={PaqueteDePrueba.Id}\r\n");
        (codigo, dice) = await RevisarAsync(pc);
        Assert.True(codigo == 0 && dice["ya-estaba"] == "no" && dice["ya-configurada"] == "no", dice["error"]);
    }
}
