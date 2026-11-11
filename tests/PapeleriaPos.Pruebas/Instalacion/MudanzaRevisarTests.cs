using System.Text;
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
        // Si no se puede, ni se trae ni se aparta nada: «ya-estaba» y «aparta-base» solo valen con «bien=si».
        Assert.Equal("bien=no ya-estaba=no aparta-base=no", Resumen(dice));
        Assert.Equal(clave, dice["clave"]);
        Assert.False(string.IsNullOrWhiteSpace(dice["error"]));
        return dice["error"]!;
    }

    /// <summary>La nota que deja instalar-pc.ps1 junto a la base en cuanto la del paquete ya es la de esta PC.</summary>
    private void DejarLaNota(string paquete = PaqueteDePrueba.Id) => _pc.DejarLaNota(paquete);

    /// <summary>«bien», «ya-estaba» y «aparta-base», juntos: lo que instalar-pc.ps1 necesita para saber qué sigue.</summary>
    private static string Resumen(ArchivoDeClaves dice) => $"bien={dice["bien"]} ya-estaba={dice["ya-estaba"]} aparta-base={dice["aparta-base"]}";

    [Fact]
    public async Task En_una_pc_nueva_el_paquete_completo_se_puede_traer_y_revisarlo_no_cambia_nada()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        var resumenAntes = File.ReadAllBytes(paquete.RutaDelResumen);

        var (codigo, dice) = await RevisarAsync();

        Assert.Equal(0, codigo);
        // El identificador del paquete, dos veces: «paquete» y «paquete-id» (como se llama también en mudanza-ver).
        Assert.Equal(["bien", "ya-estaba", "aparta-base", "archivo", "equipo", "paquete", "paquete-id", "tienda", "error", "clave"], dice.Datos.Select(d => d.Clave));
        Assert.Equal(["si", "no", "no", PaqueteDePrueba.Archivo, "CAJA2", PaqueteDePrueba.Id, PaqueteDePrueba.Id, "Matriz", "", ""], dice.Datos.Select(d => d.Valor));
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
        Assert.Equal("bien=no ya-estaba=no aparta-base=no", Resumen(dice));
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
        Assert.Equal("bien=si ya-estaba=no aparta-base=no", Resumen(dice));
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
        Assert.Equal("bien=si ya-estaba=no aparta-base=si", Resumen(dice));
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
        Assert.Equal("bien=si ya-estaba=si aparta-base=no", Resumen(dice));
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
        Assert.Equal("bien=si ya-estaba=no aparta-base=no", Resumen(dice));
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
        Assert.Equal("bien=si ya-estaba=no aparta-base=si", Resumen(dice));
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
        Assert.Equal($"bien=si ya-estaba={yaEstaba} aparta-base={apartaBase}", Resumen(dice));
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
        Assert.Equal(["bien", "ya-estaba", "aparta-base", "archivo", "equipo", "paquete", "paquete-id", "tienda", "error", "clave"], dice.Datos.Select(d => d.Clave));
        Assert.Equal("bien=no ya-estaba=no aparta-base=no", Resumen(dice));
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
        Assert.True(codigo == 0 && dice["ya-estaba"] == "si" && dice["aparta-base"] == "no", dice["error"]);
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
        // Con la nota de este paquete tampoco «ya estaba»: la base que contesta no tiene tienda.
        pc.Archivo("mudanza-puesta.txt", $"paquete={PaqueteDePrueba.Id}\r\n");
        (codigo, dice) = await RevisarAsync(pc);
        Assert.True(codigo == 0 && dice["ya-estaba"] == "no", dice["error"]);
    }
}
