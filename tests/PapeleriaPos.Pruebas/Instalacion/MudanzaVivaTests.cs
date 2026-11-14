using System.Net;
using System.Net.Sockets;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>mudanza-viva</c>: antes de cancelar una mudanza en la PC de donde salió la base (docs/instalacion.md), si otra PC ya trabaja con
/// esa tienda. Con «viva=si» no se cancela (serían dos bases vivas); con «viva=no» y código 0, sí. Lo de esta misma PC no cuenta, y lo
/// que no se pudo saber es un error, nunca un «no». La red es la de la prueba: nunca se pregunta en la red de verdad.
/// </summary>
public sealed class MudanzaVivaTests : IDisposable
{
    /// <summary>De documentación (TEST-NET): la PC a donde se llevó la base, y esta, de donde salió.</summary>
    private const string Nueva = "192.0.2.50";

    private const string EstaPc = "192.0.2.77";

    private static readonly PlazosDeLaMudanza Plazos = RespuestasDePrueba.SinEsperar with { ALaPrincipal = TimeSpan.FromSeconds(30), AlBuscar = TimeSpan.FromMilliseconds(40) };

    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;
    private readonly RedDePrueba _red = new();

    /// <summary>Lo que contesta a la pregunta por UDP en la red de la prueba, y cuánto se les esperó cada vez.</summary>
    private readonly List<Uri> _contestanEnLaRed = [];

    private readonly List<TimeSpan> _busquedas = [];

    public MudanzaVivaTests()
    {
        _pc = new PcDePrueba(_carpeta.Ruta) { NombreDeLaPc = "CAJA2" };
        _pc.IpsDeLaTienda.Clear();
        _pc.IpsDeLaTienda.Add(EstaPc);
    }

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    /// <summary>Como lo corre instalar-pc.ps1: por su línea de órdenes.</summary>
    private Task<(int Codigo, ArchivoDeClaves Dice)> CorrerAsync(params string[] opciones) =>
        CorrerAsync(espera =>
        {
            _busquedas.Add(espera);
            return Task.FromResult<IReadOnlyList<Uri>>(_contestanEnLaRed);
        }, opciones);

    private async Task<(int Codigo, ArchivoDeClaves Dice)> CorrerAsync(Func<TimeSpan, Task<IReadOnlyList<Uri>>> enLaRed, params string[] opciones)
    {
        var codigo = await Ayudante.CorrerAsync(["mudanza-viva", .. opciones, "--salida", Salida], _pc, _red, Plazos, enLaRed);
        var dice = ArchivoDeClaves.Leer(Salida);
        // Siempre las mismas claves.
        Assert.Equal(["viva", "donde", "equipo", "error"], dice.Datos.Select(d => d.Clave));
        return (codigo, dice);
    }

    private static string Resumen(ArchivoDeClaves dice) => $"viva={dice["viva"]} donde={dice["donde"]} equipo={dice["equipo"]}";

    // ── Nadie la tiene ──

    /// <summary>Lo normal al cancelar: la otra PC está apagada o nunca recibió la base. Se buscó en la red y nadie contestó.</summary>
    [Fact]
    public async Task Si_nadie_contesta_la_base_no_esta_viva_en_otra_pc()
    {
        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz");

        Assert.Equal(0, codigo);
        Assert.Equal(["no", "", "", ""], dice.Datos.Select(d => d.Valor));
        // Se preguntó en la red (una vez, con su espera), y a nadie más.
        Assert.Equal([Plazos.AlBuscar], _busquedas);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>Tampoco si la dirección que se dio (la de traida.txt) no contesta: esa PC está apagada.</summary>
    [Fact]
    public async Task Si_la_direccion_que_se_dio_no_contesta_no_esta_viva()
    {
        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", Nueva);

        Assert.Equal(0, codigo);
        Assert.Equal("viva=no donde= equipo=", Resumen(dice));
        Assert.Equal([Nueva + ":5080"], _red.Preguntadas);
    }

    // ── Otra PC ya la tiene ──

    /// <summary>
    /// La PC nueva ya trabaja con la base que se le llevó: contesta su estado con la misma tienda (sin distinguir mayúsculas ni
    /// espacios, como en todo lo de la mudanza). No se cancela.
    /// </summary>
    [Theory]
    [InlineData("Matriz", new[] { "Matriz" })]
    [InlineData("matriz ", new[] { " MATRIZ" })]
    [InlineData("Centro; Matriz", new[] { "Centro", "Matriz" })]
    [InlineData("centro;matriz", new[] { "Centro", "Matriz" })]
    public async Task Si_la_direccion_que_se_dio_contesta_con_esa_tienda_esta_viva(string tienda, string[] contesta)
    {
        _red.Contesta(Nueva + ":5080", RedDePrueba.Estado(contesta));

        var (codigo, dice) = await CorrerAsync("--tienda", tienda, "--direcciones", Nueva);

        Assert.Equal(0, codigo);
        Assert.Equal(["si", Nueva, "", ""], dice.Datos.Select(d => d.Valor));
    }

    /// <summary>
    /// Sin la USB a la mano (no se dieron direcciones), la PC nueva se encuentra en la red como la encuentra «buscar»: contestó a la
    /// pregunta por UDP, y su estado dice esa tienda.
    /// </summary>
    [Theory]
    [InlineData("http://192.0.2.50:5080/", "192.0.2.50")]
    [InlineData("http://192.0.2.50:5090/", "192.0.2.50:5090")]
    public async Task Si_la_que_contesta_en_la_red_tiene_esa_tienda_esta_viva(string encontrada, string donde)
    {
        _contestanEnLaRed.Add(new Uri(encontrada));
        _red.Contesta(new Uri(encontrada).Authority, RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz");

        Assert.Equal(0, codigo);
        Assert.Equal($"viva=si donde={donde} equipo=", Resumen(dice));
    }

    /// <summary>De varias, basta una: las demás pueden estar apagadas, ser otra tienda o contestar otra cosa.</summary>
    [Fact]
    public async Task Basta_con_que_una_de_las_que_contestan_tenga_esa_tienda()
    {
        _contestanEnLaRed.AddRange([new Uri("http://192.0.2.60:5080/"), new Uri("http://192.0.2.61:5080/"), new Uri("http://192.0.2.50:5080/")]);
        _red.Contesta("192.0.2.60:5080", RedDePrueba.Estado(["Otra papelería"]));
        _red.Contesta("192.0.2.50:5080", RedDePrueba.Estado(["Matriz"]));
        _red.Contesta("192.0.2.62:5080", "esto no es JSON");

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", "192.0.2.62;192.0.2.63");

        Assert.Equal(0, codigo);
        Assert.Equal("viva=si donde=192.0.2.50 equipo=", Resumen(dice));
        // A cada una se le preguntó una vez.
        Assert.Equal(["192.0.2.50:5080", "192.0.2.60:5080", "192.0.2.61:5080", "192.0.2.62:5080", "192.0.2.63:5080"], _red.Preguntadas.Order());
    }

    /// <summary>La que se dio y además contestó en la red es la misma PC: se le pregunta una sola vez.</summary>
    [Fact]
    public async Task A_la_que_se_dio_y_ademas_contesto_en_la_red_se_le_pregunta_una_vez()
    {
        _contestanEnLaRed.AddRange([new Uri($"http://{Nueva}:5080/"), new Uri($"http://{Nueva}:5080/")]);
        _red.Contesta(Nueva + ":5080", RedDePrueba.Estado(["Matriz"]));

        var (_, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", $"{Nueva};{Nueva}");

        Assert.Equal("si", dice["viva"]);
        Assert.Equal([Nueva + ":5080"], _red.Preguntadas);
    }

    /// <summary>Las direcciones dadas se preguntan en el puerto que se diga (el del servicio de allá, si no es el 5080).</summary>
    [Fact]
    public async Task Las_direcciones_dadas_se_preguntan_en_su_puerto()
    {
        _red.Contesta(Nueva + ":5090", RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", $"192.0.2.49;{Nueva}", "--puerto", "5090");

        Assert.Equal(0, codigo);
        Assert.Equal($"viva=si donde={Nueva}:5090 equipo=", Resumen(dice));
        Assert.Equal(["192.0.2.49:5090", Nueva + ":5090"], _red.Preguntadas.Order());
    }

    // ── Lo que no es esa tienda viva en otra PC ──

    /// <summary>En esa dirección contesta otra cosa: otra tienda, una base todavía sin tienda u otro programa. No es la base de esta PC.</summary>
    [Theory]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Otra tienda"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Matriz", "Bodega"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": [], "tiendaNueva": true }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Matriz"], "tiendaNueva": true }""")]
    [InlineData("""{ "servicio": "Otra cosa", "sucursales": ["Matriz"] }""")]
    [InlineData("""{ "servicio": "Papelería POS", "sucursales": ["Otra"], "sucursales": ["Otra más"] }""")]
    [InlineData("esto no es JSON")]
    public async Task Lo_que_no_es_esa_tienda_viva_no_impide_cancelar(string contesta)
    {
        _contestanEnLaRed.Add(new Uri("http://192.0.2.60:5080/"));
        _red.Contesta(Nueva + ":5080", contesta);
        _red.Contesta("192.0.2.60:5080", contesta);

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", Nueva);

        Assert.Equal(0, codigo);
        Assert.Equal("viva=no donde= equipo=", Resumen(dice));
        Assert.Equal("", dice["error"]);
    }

    /// <summary>
    /// Lo de esta misma PC no cuenta: su propio servicio (si contestara, o el de una prueba en otro puerto) es justo el que se quiere
    /// volver a arrancar. Ni por una de sus IP (también las de otras tarjetas), ni por 127.x, ni dada ni encontrada: ni se le pregunta.
    /// </summary>
    [Fact]
    public async Task Lo_que_contesta_en_esta_misma_pc_no_cuenta()
    {
        _pc.IpsDeOtrasTarjetas.Add("10.99.0.1");
        foreach (var propia in new[] { EstaPc, "10.99.0.1", "127.0.0.1", "127.0.0.2" })
        {
            _red.Contesta(propia + ":5080", RedDePrueba.Estado(["Matriz"]));
            _red.Contesta(propia + ":5090", RedDePrueba.Estado(["Matriz"]));
            _contestanEnLaRed.Add(new Uri($"http://{propia}:5090/"));
        }

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", $"{EstaPc};10.99.0.1;127.0.0.1;127.0.0.2");

        Assert.Equal(0, codigo);
        Assert.Equal("viva=no donde= equipo=", Resumen(dice));
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>
    /// De punta a punta por UDP, con lo que contesta el servicio de verdad (Descubrimiento) en un puerto de esta PC: la pregunta sale y
    /// la respuesta se entiende; pero quien contesta desde esta misma PC (127.0.0.1) no es «otra PC», y ni se le pregunta su estado.
    /// </summary>
    [Fact]
    public async Task La_busqueda_de_verdad_encuentra_al_que_contesta_y_si_es_esta_pc_no_cuenta()
    {
        using var servicio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var puerto = ((IPEndPoint)servicio.Client.LocalEndPoint!).Port;
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var contestando = Task.Run(async () =>
        {
            var pregunta = await servicio.ReceiveAsync(limite.Token);
            Assert.True(Descubrimiento.EsPregunta(pregunta.Buffer));
            await servicio.SendAsync(Descubrimiento.ArmarRespuesta(5080), pregunta.RemoteEndPoint, limite.Token);
        });
        IReadOnlyList<Uri> encontradas = [];
        _red.Contesta("127.0.0.1:5080", RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await CorrerAsync(async _ => encontradas = await BusquedaDeLaPrincipal.BuscarAsync(TimeSpan.FromSeconds(3), [new IPEndPoint(IPAddress.Loopback, puerto)]),
            "--tienda", "Matriz");

        await contestando;
        Assert.Equal([new Uri("http://127.0.0.1:5080/")], encontradas);
        Assert.Equal(0, codigo);
        Assert.Equal("viva=no donde= equipo=", Resumen(dice));
        Assert.Empty(_red.Preguntadas);
    }

    // ── Cómo se llama esa PC ──

    /// <summary>
    /// El estado de un servicio no dice cómo se llama su PC, pero sí el traida.txt que esa PC dejó en el paquete: si la carpeta que
    /// dice la marca de esta PC está a la mano, es de este paquete y trae esa IP, se dice su nombre.
    /// </summary>
    [Theory]
    [InlineData("este", Nueva, "PRINCIPAL")]
    [InlineData("otro-paquete", Nueva, "")]
    [InlineData("este", "192.0.2.60", "")]
    [InlineData("sin-usb", Nueva, "")]
    [InlineData("nombre-raro", Nueva, "")]
    public async Task Con_el_paquete_a_la_mano_dice_como_se_llama_la_pc_que_la_tiene(string caso, string viva, string equipo)
    {
        _pc.DejarLaMarca(Usb);
        if (caso != "sin-usb")
        {
            PaqueteDePrueba.En(Usb).DejarTraida(caso == "otro-paquete" ? "11111111111111111111111111111111" : PaqueteDePrueba.Id);
            if (caso == "nombre-raro")
                File.WriteAllText(Path.Combine(Usb, "traida.txt"), $"paquete={PaqueteDePrueba.Id}\r\nequipo=PRINCIPAL (apágala y borra todo)\r\nips={Nueva}\r\n");
        }
        _red.Contesta(viva + ":5080", RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", viva);

        Assert.Equal(0, codigo);
        Assert.Equal($"viva=si donde={viva} equipo={equipo}", Resumen(dice));
    }

    // ── Lo que no se pudo saber no es un «no» ──

    /// <summary>
    /// Sin el nombre de la tienda no hay con qué comparar: nadie contestaría «la misma», y parecería que nadie la tiene. Es un error, y
    /// no se le pregunta a nadie.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; ")]
    public async Task Sin_el_nombre_de_la_tienda_no_dice_que_no(string tienda)
    {
        _red.Contesta(Nueva + ":5080", RedDePrueba.Estado([]));

        var (codigo, dice) = await CorrerAsync("--tienda", tienda, "--direcciones", Nueva);

        Assert.Equal(2, codigo);
        Assert.Equal("viva= donde= equipo=", Resumen(dice));
        Assert.Equal("Falta el nombre de la tienda («--tienda»). No se sabe si la base ya está viva en otra PC.", dice["error"]);
        Assert.Empty(_red.Preguntadas);
        Assert.Empty(_busquedas);
    }

    /// <summary>Una dirección que se dio y no es una IP no se salta (no preguntarle sería decir «no» sin saber): es un error.</summary>
    [Theory]
    [InlineData("192.0.2.50;PRINCIPAL")]
    [InlineData("principal.ejemplo.com")]
    [InlineData("192.0.2.50:5080")]
    [InlineData("300.1.1.1")]
    [InlineData("::1")]
    [InlineData("192.0.2.1;192.0.2.2;192.0.2.3;192.0.2.4;192.0.2.5;192.0.2.6;192.0.2.7;192.0.2.8;192.0.2.9;192.0.2.10;192.0.2.11;192.0.2.12;192.0.2.13;192.0.2.14;192.0.2.15;192.0.2.16;192.0.2.17")]
    public async Task Una_direccion_que_no_se_entiende_no_se_salta(string direcciones)
    {
        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", direcciones);

        Assert.Equal(2, codigo);
        Assert.Equal("", dice["viva"]);
        Assert.StartsWith($"Las direcciones no se entienden (está «{direcciones}»)", dice["error"]);
        Assert.EndsWith("No se sabe si la base ya está viva en otra PC.", dice["error"]);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>Con separadores de más, espacios o repetidas, sí se entienden (como las «ips» de traida.txt).</summary>
    [Fact]
    public async Task Las_direcciones_se_entienden_con_espacios_comas_y_repetidas()
    {
        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", $" {Nueva} ; 192.0.2.51,{Nueva};; ");

        Assert.Equal(0, codigo);
        Assert.Equal("no", dice["viva"]);
        Assert.Equal(["192.0.2.50:5080", "192.0.2.51:5080"], _red.Preguntadas.Order());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("el de siempre")]
    [InlineData("")]
    public async Task Un_puerto_que_no_se_entiende_no_dice_que_no(string puerto)
    {
        var (codigo, dice) = await CorrerAsync("--tienda", "Matriz", "--direcciones", Nueva, "--puerto", puerto);

        Assert.Equal(2, codigo);
        Assert.Equal("", dice["viva"]);
        Assert.StartsWith("El puerto no se entiende", dice["error"]);
        Assert.Empty(_red.Preguntadas);
    }

    /// <summary>
    /// Si ni se pudo preguntar en la red (Windows no dejó abrir el puerto), no se sabe quién más está: no dice que no. Salvo que una de
    /// las direcciones dadas ya haya contestado con esa tienda: entonces sí se sabe que está viva.
    /// </summary>
    [Theory]
    [InlineData(false, 2, "viva= donde= equipo=")]
    [InlineData(true, 0, "viva=si donde=192.0.2.50 equipo=")]
    public async Task Si_no_se_pudo_buscar_en_la_red_no_dice_que_no(bool contestaLaDada, int codigoEsperado, string resumen)
    {
        if (contestaLaDada)
            _red.Contesta(Nueva + ":5080", RedDePrueba.Estado(["Matriz"]));

        var (codigo, dice) = await CorrerAsync(_ => throw new SocketException((int)SocketError.AccessDenied), "--tienda", "Matriz", "--direcciones", Nueva);

        Assert.Equal(codigoEsperado, codigo);
        Assert.Equal(resumen, Resumen(dice));
        if (!contestaLaDada)
        {
            Assert.StartsWith("No se pudo buscar en la red (", dice["error"]);
            Assert.EndsWith("No se sabe si la base ya está viva en otra PC.", dice["error"]);
        }
    }

    // ── Sin poder preguntarle a nadie tampoco es un «no» ──

    /// <summary>
    /// Esta PC sin red (el cable suelto, el Wi-Fi caído, o una IP 169.254 que Windows se pone sola): las preguntas no salen de ella, y que
    /// nadie conteste no dice nada de la PC nueva. Con un «no» se cancelaría con la base viva allá (justo cuando «Terminar» falló porque
    /// la principal «no contesta»). Si aun así una de las dadas contesta con esa tienda, eso sí se sabe: está viva.
    /// </summary>
    [Theory]
    [InlineData(false, false, 2, "viva= donde= equipo=")]
    [InlineData(true, false, 2, "viva= donde= equipo=")]
    [InlineData(true, true, 0, "viva=si donde=192.0.2.50 equipo=")]
    public async Task Sin_red_en_esta_pc_no_dice_que_no(bool conDireccion, bool contestaLaDada, int codigoEsperado, string resumen)
    {
        _pc.IpsDeLaTienda.Clear();
        if (contestaLaDada)
            _red.Contesta(Nueva + ":5080", RedDePrueba.Estado(["Matriz"]));
        string[] opciones = conDireccion ? ["--tienda", "Matriz", "--direcciones", Nueva] : ["--tienda", "Matriz"];

        var (codigo, dice) = await CorrerAsync(opciones);

        Assert.Equal(codigoEsperado, codigo);
        Assert.Equal(resumen, Resumen(dice));
        if (codigoEsperado == 2)
        {
            Assert.StartsWith("Esta PC no está conectada a la red (ninguna tarjeta de cable o Wi-Fi tiene IP)", dice["error"]);
            Assert.EndsWith("No se sabe si la base ya está viva en otra PC.", dice["error"]);
        }
    }

    /// <summary>
    /// La pregunta de verdad por UDP, cuando no sale a ningún lado (aquí, a 0.0.0.0 en el puerto 0: ningún sistema la deja salir de la
    /// PC). «buscar» sigue como siempre, con una lista vacía; para mudanza-viva no es que nadie contestó, es que no se pudo buscar.
    /// </summary>
    [Fact]
    public async Task Si_la_pregunta_por_udp_no_sale_a_ningun_lado_no_dice_que_no()
    {
        IPEndPoint[] aNingunLado = [new(IPAddress.Any, 0)];
        Assert.Empty(await BusquedaDeLaPrincipal.BuscarAsync(TimeSpan.FromMilliseconds(50), aNingunLado));
        await Assert.ThrowsAsync<SocketException>(() => BusquedaDeLaPrincipal.BuscarAsync(TimeSpan.FromMilliseconds(50), aNingunLado, exigirQueSalga: true));

        var (codigo, dice) = await CorrerAsync(espera => BaseVivaEnOtraPc.BuscarEnLaRedAsync(espera, aNingunLado), "--tienda", "Matriz");

        Assert.Equal(2, codigo);
        Assert.Equal("viva= donde= equipo=", Resumen(dice));
        Assert.StartsWith("No se pudo buscar en la red (", dice["error"]);
        Assert.EndsWith("No se sabe si la base ya está viva en otra PC.", dice["error"]);
    }

    /// <summary>
    /// Una PC que contestó en la red como Papelería POS (su servicio está corriendo) y después no contestó su estado: puede ser justo la
    /// que ya tiene la base. No se sabe qué tienda tiene, y eso no es un «no». (La que contesta otra cosa, otro programa en ese puerto, no
    /// cuenta: <see cref="Lo_que_no_es_esa_tienda_viva_no_impide_cancelar"/>.) También si era una de las dadas: se le preguntó una vez.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_que_contesto_en_la_red_y_no_dijo_su_tienda_no_es_un_no(bool tambienDada)
    {
        _contestanEnLaRed.Add(new Uri($"http://{Nueva}:5080/"));
        string[] opciones = tambienDada ? ["--tienda", "Matriz", "--direcciones", Nueva] : ["--tienda", "Matriz"];

        var (codigo, dice) = await CorrerAsync(opciones);

        Assert.Equal(2, codigo);
        Assert.Equal("viva= donde= equipo=", Resumen(dice));
        Assert.Equal($"La PC {Nueva} contestó en la red como Papelería POS, pero no dijo qué tienda tiene. No se sabe si la base ya está viva en otra PC.",
            dice["error"]);
        Assert.Equal([Nueva + ":5080"], _red.Preguntadas);
    }

    /// <summary>Sin <c>--tienda</c> la orden está mal dada: no deja una salida que se pueda tomar por un «no».</summary>
    [Fact]
    public async Task Sin_la_tienda_la_orden_esta_mal_dada()
    {
        Assert.Equal(1, await Ayudante.CorrerAsync(["mudanza-viva", "--direcciones", Nueva, "--salida", Salida], _pc, _red, Plazos, _ => throw new InvalidOperationException()));

        Assert.False(File.Exists(Salida));
        Assert.Empty(_red.Preguntadas);
    }
}
