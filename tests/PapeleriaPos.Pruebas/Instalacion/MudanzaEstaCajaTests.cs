using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;
using ServicioDePrueba = PapeleriaPos.Pruebas.Servicio.ServicioDePrueba;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>probar --esta-caja si</c>: al terminar una mudanza, en la PC de donde salió la base, si la PC nueva reconoce la llave de la caja de
/// esta PC (docs/instalacion.md). Es la prueba de identidad de siempre, la que hace la caja antes de mandarle su llave a nadie; el
/// ayudante la tiene escrita aparte (no usa los proyectos de la caja), así que aquí se revisa contra el servicio de verdad en memoria y
/// contra el código de la caja: la llave pública que entrega es la que la caja de verdad da por buena, y cada pieza da lo mismo que
/// <see cref="Descubrimiento"/>. La llave de la caja nunca sale: ni a la salida, ni por la red.
/// </summary>
public sealed class MudanzaEstaCajaTests : IClassFixture<ServicioDePrueba>, IDisposable
{
    /// <summary>De documentación (TEST-NET): la PC nueva, que ya tiene la base, y esta, de donde salió. Nunca se les habla de verdad.</summary>
    private const string Principal = "192.0.2.50";

    private const string EstaPc = "192.0.2.77";

    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaTienda = [(IPAddress.Parse(Principal), 24)];

    /// <summary>
    /// Con el servicio de verdad en memoria y toda la suite corriendo a la par, una respuesta que ya está lista puede tardar: aquí se
    /// revisa lo que se entiende de ella, no lo que tarda.
    /// </summary>
    private static readonly PlazosDeLaMudanza Plazos = RespuestasDePrueba.ConPaciencia with { ALaPrincipal = TimeSpan.FromMinutes(2) };

    private readonly ServicioDePrueba _servicio;
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;
    private readonly RedDePrueba _red = new();

    public MudanzaEstaCajaTests(ServicioDePrueba servicio)
    {
        _servicio = servicio;
        _pc = new PcDePrueba(_carpeta.Ruta) { NombreDeLaPc = "CAJA2" };
        _pc.IpsDeLaTienda.Clear();
        _pc.IpsDeLaTienda.Add(EstaPc);
    }

    public void Dispose() => _carpeta.Dispose();

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private string Ruta => CajaJson.Ruta(_pc.Datos);

    // ── La tienda de la prueba ──

    /// <summary>
    /// La caja principal en la red de la tienda: sabe sus redes, y lo que le manda la PC de <paramref name="desde"/> le llega a la IP y al
    /// puerto a los que esa PC se conectó (en la red de la tienda no hay nada en medio). Con <paramref name="recibeEn"/>, le llega a esa
    /// otra dirección: alguien en medio le pasó la pregunta.
    /// </summary>
    private WebApplicationFactory<Program> EnLaTienda(string desde = EstaPc, string? recibeEn = null) =>
        _servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new ComoLlegaPorLaRed(desde, recibeEn));
            s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
        }));

    private sealed class ComoLlegaPorLaRed(string desde, string? recibeEn) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
        {
            app.Use((contexto, resto) =>
            {
                contexto.Connection.RemoteIpAddress = IPAddress.Parse(desde);
                // A donde se conectó quien pregunta: la dirección que pidió, si es una IP (por nombre no se sabe en cuál de sus IP cayó).
                var destino = recibeEn is null ? contexto.Request.Host : HostString.FromUriComponent(recibeEn);
                if (IPAddress.TryParse(destino.Host, out var ip))
                {
                    contexto.Connection.LocalIpAddress = ip;
                    contexto.Connection.LocalPort = destino.Port ?? 80;
                }
                return resto(contexto);
            });
            siguiente(app);
        };
    }

    /// <summary>El alta de una caja en la tienda, como la hace el dueño con su PIN; con <paramref name="caja"/>, esa misma caja otra vez.</summary>
    private async Task<AltaCajaDto> DarDeAltaAsync(WebApplicationFactory<Program> tienda, string serie, Guid? caja = null)
    {
        var sucursal = (await _servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;
        var solicitud = caja is null
            ? new SolicitudAltaCaja("dueno", "3333", sucursal, null, $"Caja {serie}", serie, "CAJA2")
            : new SolicitudAltaCaja("dueno", "3333", sucursal, caja, null, null, "OTRA-PC");
        var respuesta = await tienda.CreateClient().PostAsJsonAsync("api/alta", solicitud);
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<AltaCajaDto>())!;
    }

    /// <summary>La llave pública de otro servicio: en la PC de donde salió la base, la del servicio que tenía (pos.llave no se muda).</summary>
    private static string LlaveDeOtroServicio()
    {
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(otra.ExportSubjectPublicKeyInfo());
    }

    // ── caja.json, como lo deja la caja ──

    /// <summary>
    /// caja.json como lo guarda la caja al darse de alta con el servicio de su misma PC (ConfiguracionCaja.Guardar): el mismo JSON, con
    /// su llave cifrada para esta PC en base64 y la llave pública del servicio que la dio de alta.
    /// </summary>
    private void GuardarComoLaCaja(Guid? caja, string? llave, string? llaveDelServicio, string? llaveProtegida = null, string servicio = "http://localhost:5080/") =>
        new ConfiguracionDeLaCaja(servicio, caja, "",
            llaveProtegida ?? (llave is null ? null : Convert.ToBase64String(PcDePrueba.CifrarParaEstaPc(Encoding.UTF8.GetBytes(llave)))),
            LlaveDelServicio: llaveDelServicio, DireccionDeConfianza: servicio).Guardar(Ruta);

    /// <summary>
    /// La caja de verdad, abierta con el caja.json que hay (como arma su conexión la ventana de Windows): lo que dice su propia
    /// comprobación de la dirección guardada, la que hace antes de mandarle su llave. Su plazo es fijo (2 s): si con toda la suite a la
    /// par no alcanzó, se le pregunta otra vez.
    /// </summary>
    private async Task<(Comprobacion Dice, ConexionCaja Conexion)> LaCajaCompruebaAsync()
    {
        var guardada = ConfiguracionDeLaCaja.Leer(Ruta);
        var conexion = new ConexionCaja
        {
            Servicio = new Uri(guardada.Servicio),
            CajaId = guardada.CajaId,
            Llave = guardada.LlaveProtegida is null ? null : Encoding.UTF8.GetString(_pc.AbrirLoDeEstaPc(Convert.FromBase64String(guardada.LlaveProtegida))!),
            LlaveDelServicio = guardada.LlaveDelServicio,
            DireccionDeConfianza = new Uri(guardada.DireccionDeConfianza ?? guardada.Servicio),
            PideAlta = true,
        };
        var reencuentro = new ReencuentroDeLaPrincipal(conexion, new NadieMas(), new HttpClient(_red, disposeHandler: false), redes: () => RedDeLaTienda);
        var dice = await reencuentro.ComprobarGuardadaAsync();
        for (var intento = 0; intento < 20 && dice == Comprobacion.NoContesta; intento++)
            dice = await reencuentro.ComprobarGuardadaAsync();
        return (dice, conexion);
    }

    private sealed class NadieMas : IBuscadorDeLaPrincipal
    {
        public Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>([]);
    }

    // ── Como lo corre instalar-pc.ps1 ──

    private async Task<ArchivoDeClaves> ProbarAsync(string direccion = Principal)
    {
        var codigo = await Ayudante.CorrerAsync(["probar", "--direccion", direccion, "--esta-caja", "si", "--salida", Salida], _pc, _red, Plazos);

        Assert.Equal(0, codigo);
        var dice = ArchivoDeClaves.Leer(Salida);
        // Siempre lo mismo de «probar» y, al final, lo de la caja de esta PC.
        Assert.Equal(["tienda", "conoce", "conoce-motivo", "llave-del-servicio"], dice.Datos.Select(d => d.Clave).TakeLast(4));
        return dice;
    }

    /// <summary>Lo que va por la red de la prueba: cada petición con su dirección completa y sus encabezados.</summary>
    private sealed class Grabadora(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        public ConcurrentQueue<string> Pedidas { get; } = new();

        /// <summary>Lo que hace con cada respuesta de la prueba de identidad quien está en medio (o un servicio de otra versión).</summary>
        public Func<HttpResponseMessage, Task<HttpResponseMessage>>? AlContestarLaPrueba { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Pedidas.Enqueue($"{request.Method} {request.RequestUri} {string.Join(" ", request.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");
            var respuesta = await base.SendAsync(request, cancellationToken);
            return AlContestarLaPrueba is { } cambiar && request.RequestUri!.AbsolutePath == "/api/identidad" ? await cambiar(respuesta) : respuesta;
        }
    }

    /// <summary>La principal de la prueba, puesta en esa dirección de la red de la prueba.</summary>
    private Grabadora Conectar(WebApplicationFactory<Program> tienda, params string[] direcciones)
    {
        var grabadora = new Grabadora(tienda.Server.CreateHandler());
        foreach (var direccion in direcciones)
            _red.EsElServicio(direccion, grabadora);
        return grabadora;
    }

    // ── La que manda ──

    /// <summary>
    /// La caja de esta PC está dada de alta en la base que ya tiene la PC nueva: esa PC lo prueba (con la huella de la llave de esta
    /// caja, para la dirección a la que se le habló) y firma con su propia llave. La llave pública que entrega el ayudante es la que
    /// guarda la caja; y con caja.json ya convertido, la caja de verdad comprueba a esa PC y la da por buena, sin aprender nada nuevo.
    /// La dirección, como la escribe el dueño: sola (el 5080), con otro puerto o completa.
    /// </summary>
    [Theory]
    [InlineData("B", Principal, Principal + ":5080")]
    [InlineData("C", Principal + ":5090", Principal + ":5090")]
    [InlineData("D", "http://" + Principal + ":5080/", Principal + ":5080")]
    public async Task Con_su_alta_la_principal_prueba_que_la_conoce_y_la_caja_de_verdad_da_por_buena_la_llave_que_se_entrega(string serie, string direccion,
        string aDonde)
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, serie);
        // En esta PC la caja recuerda la llave del servicio que tenía aquí, que no es el de la PC nueva.
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio());
        var antes = File.ReadAllBytes(Ruta);
        var red = Conectar(tienda, aDonde);
        var estado = (await _servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;

        var dice = await ProbarAsync(direccion);

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("si", dice["tienda"]);
        Assert.NotEmpty(estado.Sucursales);
        Assert.Equal(string.Join("; ", estado.Sucursales), dice["sucursales"]);
        Assert.Equal("si", dice["conoce"]);
        Assert.Equal("", dice["conoce-motivo"]);
        // La llave pública del servicio de la PC nueva, tal cual la da al dar de alta (la que guarda la caja).
        Assert.Equal(alta.LlaveDelServicio, dice["llave-del-servicio"]);
        Assert.True(IdentidadDeLaPrincipal.EsLlaveDeServicio(dice["llave-del-servicio"]));
        // Probar no cambia nada en esta PC.
        Assert.Equal(antes, File.ReadAllBytes(Ruta));
        // Dos preguntas, las dos sin llave: el estado y la prueba, con el número de la caja y un reto nuevo.
        Assert.Equal(2, red.Pedidas.Count);
        Assert.StartsWith($"GET http://{aDonde}/api/estado", red.Pedidas.First());
        Assert.StartsWith($"GET http://{aDonde}/api/identidad?caja={alta.Caja.Id:D}&reto=", red.Pedidas.Last());
        NoSaleLaLlave(alta.Llave, red);

        // Con eso instalar-pc.ps1 deja caja.json apuntando allá (escribir --mudada-a), y la caja de verdad, al abrirla, la comprueba.
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        new ArchivoDeClaves().Poner("rol", "otra").Poner("mudanza", "terminar").Poner("principal", direccion).Poner("impresora", "").Poner("lector", "teclado")
            .Guardar(respuestas);
        Assert.Equal(0, await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--mudada-a", direccion, "--llave-del-servicio",
            dice["llave-del-servicio"]!, "--salida", Salida], _pc, RedDePrueba.SinNadie()));
        var (comprueba, conexion) = await LaCajaCompruebaAsync();
        Assert.Equal(Comprobacion.Es, comprueba);
        Assert.Equal(new Uri($"http://{aDonde}/"), conexion.Servicio);
        Assert.Equal(conexion.Servicio, conexion.DireccionDeConfianza);
        Assert.Equal(alta.LlaveDelServicio, conexion.LlaveDelServicio);
        NoSaleLaLlave(alta.Llave, red);
    }

    /// <summary>
    /// Para ver que la comprobación de arriba distingue: con la llave de OTRO servicio guardada (la que la caja traía de su PC, si no se
    /// cambiara al terminar la mudanza), la caja de verdad no da por buena a la PC nueva.
    /// </summary>
    [Fact]
    public async Task Con_la_llave_del_servicio_de_antes_la_caja_de_verdad_no_daria_por_buena_a_la_pc_nueva()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "E");
        Conectar(tienda, Principal + ":5080");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio(), servicio: $"http://{Principal}:5080/");

        Assert.Equal(Comprobacion.NoEs, (await LaCajaCompruebaAsync()).Dice);
        // El ayudante, en cambio, no compara con la llave que la caja traía: dice si la PC nueva conoce la llave de la caja.
        Assert.Equal("si", (await ProbarAsync())["conoce"]);
    }

    /// <summary>
    /// Cuando no se pudo saber (o si instalar-pc.ps1 no fija la llave), <c>escribir --mudada-a</c> deja vacía la llave del servicio: la
    /// caja de verdad comprueba a la PC nueva en su dirección de confianza y aprende su llave. Sin <c>--mudada-a</c> (el «escribir» de
    /// siempre, que solo cambia «Servicio») la caja seguiría recordando la del servicio de antes, y no daría por buena a la PC nueva.
    /// </summary>
    [Theory]
    [InlineData("M", true)]
    [InlineData("N", false)]
    public async Task Convertida_sin_fijar_la_llave_la_caja_de_verdad_aprende_la_de_la_pc_nueva(string serie, bool conMudadaA)
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, serie);
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio());
        Conectar(tienda, Principal + ":5080");
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        new ArchivoDeClaves().Poner("rol", "otra").Poner("mudanza", "terminar").Poner("principal", Principal).Poner("impresora", "").Poner("lector", "teclado")
            .Guardar(respuestas);
        string[] opciones = conMudadaA ? ["--mudada-a", Principal] : [];

        Assert.Equal(0, await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, .. opciones, "--salida", Salida], _pc, RedDePrueba.SinNadie()));

        var (comprueba, conexion) = await LaCajaCompruebaAsync();
        Assert.Equal(new Uri($"http://{Principal}:5080/"), conexion.Servicio);
        Assert.Equal(alta.Caja.Id, conexion.CajaId);
        Assert.Equal(alta.Llave, conexion.Llave);
        if (conMudadaA)
        {
            Assert.Equal(Comprobacion.Es, comprueba);
            Assert.Equal(alta.LlaveDelServicio, conexion.LlaveDelServicio);
        }
        else
            Assert.Equal(Comprobacion.NoEs, comprueba);
    }

    /// <summary>La llave de la caja y su huella (con ella se arma la prueba) no salen: ni a la salida ni por la red; y nada va a la pantalla.</summary>
    private void NoSaleLaLlave(string llave, Grabadora red)
    {
        var protegida = Convert.ToBase64String(PcDePrueba.CifrarParaEstaPc(Encoding.UTF8.GetBytes(llave)));
        foreach (var texto in red.Pedidas.Append(File.ReadAllText(Salida)))
        {
            Assert.DoesNotContain(llave, texto, StringComparison.Ordinal);
            Assert.DoesNotContain(LlaveCaja.Huella(llave), texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(protegida, texto, StringComparison.Ordinal);
            Assert.DoesNotContain(ClienteCaja.EncabezadoLlave, texto, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Console.", File.ReadAllText(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "src", "PapeleriaPos.Instalacion", "IdentidadDeLaPrincipal.cs")));
    }

    // ── Cuando no la conoce ──

    /// <summary>
    /// En la PC nueva se dio de alta OTRA PC como esta misma caja (en su pantalla del alta se eligió «Caja 2» en lugar de «Caja nueva»):
    /// la caja sigue en la base, con otra llave. La PC nueva contesta 200, pero su prueba no cuadra con la llave de esta caja.
    /// </summary>
    [Fact]
    public async Task Con_la_llave_reemplazada_por_otra_alta_de_la_misma_caja_no_la_conoce()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "F");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, alta.LlaveDelServicio);
        var otra = await DarDeAltaAsync(tienda, "F", alta.Caja.Id);
        Assert.Equal(alta.Caja.Id, otra.Caja.Id);
        Assert.NotEqual(alta.Llave, otra.Llave);
        var red = Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("si", dice["tienda"]);
        Assert.Equal("no", dice["conoce"]);
        Assert.Contains("no probó conocer la llave de esta caja", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(alta.Llave, red);
        // La caja de verdad, apuntada allá, tampoco la daría por buena.
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, null, servicio: $"http://{Principal}:5080/");
        Assert.Equal(Comprobacion.NoEs, (await LaCajaCompruebaAsync()).Dice);
    }

    /// <summary>La base de esa PC no trae el alta de esta caja (otra tienda, o todavía no se trajo la base de esta PC): 404.</summary>
    [Fact]
    public async Task Una_caja_que_la_principal_no_conoce_no()
    {
        await using var tienda = EnLaTienda();
        var llave = LlaveCaja.Nueva();
        GuardarComoLaCaja(Guid.NewGuid(), llave, LlaveDeOtroServicio());
        var red = Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no", dice["conoce"]);
        Assert.Contains("dice que no conoce a esta caja", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(llave, red);
    }

    /// <summary>La PC nueva todavía no tiene la base (el servicio recién instalado, sin tienda): lo dice «tienda», y a esta caja no la conoce.</summary>
    [Fact]
    public async Task Una_principal_que_todavia_no_tiene_la_base_dice_que_no_tiene_tienda()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        await using var tienda = nueva.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new ComoLlegaPorLaRed(EstaPc, null));
            s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
        }));
        GuardarComoLaCaja(Guid.NewGuid(), LlaveCaja.Nueva(), LlaveDeOtroServicio());
        Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no", dice["tienda"]);
        Assert.Equal("si", dice["tienda-nueva"]);
        Assert.Equal("", dice["sucursales"]);
        Assert.Equal("no", dice["conoce"]);
    }

    // ── Sin alta ──

    /// <summary>Sin alta no hay llave con qué probar nada: se dice, y a la PC nueva solo se le pregunta su estado.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("""{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "" }""")]
    [InlineData("""{ "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "LlaveProtegida": null }""")]
    [InlineData("""{ "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "LlaveProtegida": "  " }""")]
    [InlineData("""{ "Servicio": "http://localhost:5080/", "CajaId": null, "LlaveProtegida": "Y2lmcmFkby1wYXJhLWVzdGEtcGM6bGxhdmU=" }""")]
    public async Task Sin_alta_en_caja_json_dice_sin_alta_y_no_pide_la_prueba(string? cajaJson)
    {
        await using var tienda = EnLaTienda();
        if (cajaJson is not null)
            _pc.Archivo(Path.Combine("Caja", "caja.json"), cajaJson);
        var red = Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("si", dice["tienda"]);
        Assert.Equal("sin-alta", dice["conoce"]);
        Assert.StartsWith("La caja de esta PC no tiene alta", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        Assert.StartsWith($"GET http://{Principal}:5080/api/estado", Assert.Single(red.Pedidas));
        Assert.Equal(0, _pc.Abiertas);
    }

    /// <summary>También si la PC nueva no contesta: que no haya alta se sabe sin preguntarle a nadie.</summary>
    [Fact]
    public async Task Sin_alta_y_sin_que_conteste_nadie_sigue_siendo_sin_alta()
    {
        var dice = await ProbarAsync();

        Assert.Equal("no", dice["contesta"]);
        Assert.Equal("no", dice["tienda"]);
        Assert.Equal("sin-alta", dice["conoce"]);
    }

    /// <summary>La caja de una versión de antes guardaba su caja.json en C:\ProgramData\PapeleriaPos: es el que abre la caja si no hay otro.</summary>
    [Fact]
    public async Task El_alta_del_caja_json_de_la_carpeta_de_antes_tambien_cuenta()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "G");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, alta.LlaveDelServicio);
        File.Move(Ruta, CajaJson.RutaDeAntes(_pc.Datos));
        Conectar(tienda, Principal + ":5080");

        Assert.Equal("si", (await ProbarAsync())["conoce"]);
    }

    /// <summary>
    /// «Terminar» otra vez después de una corrida que ya le quitó el alta a esta caja (la PC nueva no reconocía su llave: <c>--alta
    /// otra-vez</c>) y que no llegó al final (Windows no terminó de quitar el servicio): sin alta en caja.json, pero no es que nunca la
    /// tuvo. Dice lo mismo que la primera vez, «no», para que instalar-pc.ps1 vuelva a decir que la caja se da de alta como la MISMA de
    /// antes (no «Caja nueva»: lo que cobró con su copia es de esa caja); y volver a quitarle el alta ya no cambia nada.
    /// </summary>
    [Fact]
    public async Task Repetir_terminar_despues_de_quitarle_el_alta_sigue_diciendo_que_no_la_conoce()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "P");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio());
        // En la PC nueva se dio de alta otra PC como esta misma caja: ya no reconoce su llave.
        await DarDeAltaAsync(tienda, "P", alta.Caja.Id);
        var red = Conectar(tienda, Principal + ":5080");
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        new ArchivoDeClaves().Poner("rol", "otra").Poner("mudanza", "terminar").Poner("principal", Principal).Poner("impresora", "").Poner("lector", "teclado")
            .Guardar(respuestas);
        string[] quitarleElAlta = ["escribir", "--respuestas", respuestas, "--mudada-a", Principal, "--alta", "otra-vez", "--salida", Salida];

        // La primera corrida: no la conoce, y con la USB que lo prueba (instalar-pc.ps1) se le quita el alta.
        Assert.Equal("no", (await ProbarAsync())["conoce"]);
        Assert.Equal(0, await Ayudante.CorrerAsync(quitarleElAlta, _pc, RedDePrueba.SinNadie()));
        Assert.Null(AltaDeLaCaja.DeEstaPc(_pc.Datos));
        var sinAlta = File.ReadAllBytes(Ruta);
        var conAlta = File.ReadAllBytes(Ruta + ".anterior");
        Assert.True(AltaDeLaCaja.SeQuitoAlTerminarLaMudanza(_pc.Datos));

        // La segunda: lo mismo que la primera, sin pedirle la prueba a nadie (no hay llave con qué).
        var pedidasAntes = red.Pedidas.Count;
        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("si", dice["tienda"]);
        Assert.Equal("no", dice["conoce"]);
        Assert.StartsWith("La caja de esta PC ya no tiene alta: se la quitó una corrida anterior de «Terminar la mudanza»", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        Assert.StartsWith($"GET http://{Principal}:5080/api/estado", Assert.Single(red.Pedidas.Skip(pedidasAntes)));
        NoSaleLaLlave(alta.Llave, red);
        // Y quitarle el alta otra vez no cambia nada: el alta de antes sigue en caja.json.anterior.
        Assert.Equal(0, await Ayudante.CorrerAsync(quitarleElAlta, _pc, RedDePrueba.SinNadie()));
        Assert.Equal("igual", ArchivoDeClaves.Leer(Salida)["caja-json"]);
        Assert.Equal(sinAlta, File.ReadAllBytes(Ruta));
        Assert.Equal(conAlta, File.ReadAllBytes(Ruta + ".anterior"));
    }

    /// <summary>
    /// Sin alta y sin esa historia sigue siendo «sin-alta»: un caja.json que no está convertido (le habla a esta PC), uno convertido sin
    /// caja.json.anterior, o uno cuyo anterior tampoco tenía alta.
    /// </summary>
    [Theory]
    [InlineData("sin-convertir")]
    [InlineData("sin-anterior")]
    [InlineData("anterior-sin-alta")]
    public async Task Sin_alta_y_sin_que_se_la_haya_quitado_una_corrida_anterior_es_sin_alta(string caso)
    {
        await using var tienda = EnLaTienda();
        Conectar(tienda, Principal + ":5080");
        var convertida = $"http://{Principal}:5080/";
        GuardarComoLaCaja(caso == "anterior-sin-alta" ? null : Guid.NewGuid(), caso == "anterior-sin-alta" ? null : LlaveCaja.Nueva(), LlaveDeOtroServicio());
        if (caso != "sin-anterior")
            File.Copy(Ruta, Ruta + ".anterior");
        GuardarComoLaCaja(null, null, null, servicio: caso == "sin-convertir" ? "http://localhost:5080/" : convertida);

        var dice = await ProbarAsync();

        Assert.Equal("sin-alta", dice["conoce"]);
        Assert.False(AltaDeLaCaja.SeQuitoAlTerminarLaMudanza(_pc.Datos));
    }

    // ── Cuando no se puede saber ──

    /// <summary>
    /// La PC nueva solo le prueba quién es a su red local: desde otra red contesta 403 (aunque su estado sí lo dice a cualquiera). No se
    /// sabe si conoce a esta caja.
    /// </summary>
    [Fact]
    public async Task Si_la_principal_contesta_403_porque_esta_pc_no_esta_en_su_red_no_se_sabe()
    {
        await using var tienda = EnLaTienda();
        await using var desdeOtraRed = EnLaTienda(desde: "198.51.100.7");
        var alta = await DarDeAltaAsync(tienda, "H");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, alta.LlaveDelServicio);
        var red = Conectar(desdeOtraRed, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("si", dice["tienda"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.Contains("contestó 403", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(alta.Llave, red);
    }

    [Fact]
    public async Task Si_la_principal_no_contesta_no_se_sabe()
    {
        var llave = LlaveCaja.Nueva();
        GuardarComoLaCaja(Guid.NewGuid(), llave, LlaveDeOtroServicio());

        var dice = await ProbarAsync();

        Assert.Equal("no", dice["contesta"]);
        Assert.Equal("no", dice["tienda"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.Equal("La caja principal no contestó: no se le pidió que probara quién es.", dice["conoce-motivo"]);
        Assert.Contains("no contestó", dice["error"]);
        // Ni se abrió la llave: no había a quién probársela.
        Assert.Equal(0, _pc.Abiertas);
        Assert.DoesNotContain(llave, File.ReadAllText(Salida));
    }

    /// <summary>Contesta su estado, pero la prueba no llega (se corta la red a la mitad) o falla allá: no se sabe.</summary>
    [Theory]
    [InlineData("IA", null)]
    [InlineData("IB", HttpStatusCode.InternalServerError)]
    [InlineData("IC", HttpStatusCode.ServiceUnavailable)]
    [InlineData("ID", HttpStatusCode.BadRequest)]
    public async Task Si_la_prueba_no_llega_o_falla_alla_no_se_sabe(string serie, HttpStatusCode? codigo)
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, serie);
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, alta.LlaveDelServicio);
        var red = Conectar(tienda, Principal + ":5080");
        red.AlContestarLaPrueba = _ => codigo is { } con ? Task.FromResult(new HttpResponseMessage(con)) : throw new HttpRequestException("Se cortó la red.");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.Contains(codigo is { } dijo ? $"contestó con el código {(int)dijo}" : "no contestó a la prueba de identidad", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(alta.Llave, red);
    }

    /// <summary>
    /// caja.json trae una llave que esta PC no puede abrir (el archivo viene de otra PC, o se reinstaló Windows) o que ni es base64: no
    /// se sabe, y no se le pide la prueba a nadie.
    /// </summary>
    [Theory]
    [InlineData("bGEtbGxhdmUtY2lmcmFkYS1lbi1vdHJhLXBj")]
    [InlineData("esto no es base64")]
    public async Task Si_la_llave_de_la_caja_no_se_deja_abrir_no_se_sabe(string llaveProtegida)
    {
        await using var tienda = EnLaTienda();
        GuardarComoLaCaja(Guid.NewGuid(), null, LlaveDeOtroServicio(), llaveProtegida);
        var red = Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.StartsWith("La llave de la caja de esta PC (LlaveProtegida, en caja.json) no se pudo abrir aquí", dice["conoce-motivo"]);
        Assert.Single(red.Pedidas);
        Assert.DoesNotContain(llaveProtegida, File.ReadAllText(Salida));
    }

    /// <summary>
    /// Donde no hay Windows que abra la llave (el ayudante corrido en otro sistema, o una PC cuyo Windows ya no la abre), tampoco se
    /// sabe: no truena ni dice que no la conoce.
    /// </summary>
    [Fact]
    public async Task Sin_windows_que_abra_la_llave_no_se_sabe()
    {
        GuardarComoLaCaja(Guid.NewGuid(), LlaveCaja.Nueva(), LlaveDeOtroServicio());
        _red.EsElServicio(Principal + ":5080", new Contesta("{}"));

        var codigo = await Ayudante.CorrerAsync(["probar", "--direccion", Principal, "--esta-caja", "si", "--salida", Salida],
            new EquipoSinWindows(_pc.Datos, _pc.Programa), _red, Plazos);

        Assert.Equal(0, codigo);
        var dice = ArchivoDeClaves.Leer(Salida);
        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.StartsWith("La llave de la caja de esta PC (LlaveProtegida, en caja.json) no se pudo abrir aquí", dice["conoce-motivo"]);
        Assert.Equal([Principal + ":5080"], _red.Preguntadas);
    }

    /// <summary>Un caja.json mal escrito (la caja tampoco abriría): no se sabe si tiene alta, y se dice sin su contenido.</summary>
    [Fact]
    public async Task Con_caja_json_mal_escrito_no_se_sabe()
    {
        await using var tienda = EnLaTienda();
        _pc.Archivo(Path.Combine("Caja", "caja.json"), """{ "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "LlaveProtegida": "c2VjcmV0bw==", """);
        Conectar(tienda, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.StartsWith("caja.json está mal escrito", dice["conoce-motivo"]);
        Assert.DoesNotContain("c2VjcmV0bw", File.ReadAllText(Salida));
    }

    // ── La dirección va en la prueba ──

    /// <summary>
    /// La prueba es para la dirección a la que se le habló: si quien contesta en esa dirección solo le pasa la pregunta a la caja
    /// principal de verdad (que la recibe en SU dirección), la prueba es para otra dirección y no cuadra.
    /// </summary>
    [Fact]
    public async Task La_prueba_hecha_para_otra_direccion_no_cuenta()
    {
        await using var tienda = EnLaTienda();
        await using var porOtroLado = EnLaTienda(recibeEn: "192.0.2.51:5080");
        var alta = await DarDeAltaAsync(tienda, "J");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, alta.LlaveDelServicio);
        Conectar(porOtroLado, Principal + ":5080");

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no", dice["conoce"]);
        Assert.Equal("", dice["llave-del-servicio"]);
    }

    /// <summary>
    /// Por nombre, la prueba se hace con la IP de ese nombre (como la caja, que le pregunta al DNS): «localhost» es 127.0.0.1 en
    /// cualquier PC, sin preguntarle a nadie. El estado se le pide por su nombre, como siempre.
    /// </summary>
    [Fact]
    public async Task Por_nombre_la_prueba_va_con_la_ip_de_ese_nombre()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "K");
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio());
        var red = Conectar(tienda, "localhost:5080", "127.0.0.1:5080");

        var dice = await ProbarAsync("localhost");

        Assert.Equal("si", dice["conoce"]);
        Assert.Equal(alta.LlaveDelServicio, dice["llave-del-servicio"]);
        Assert.StartsWith("GET http://localhost:5080/api/estado", red.Pedidas.First());
        Assert.StartsWith($"GET http://127.0.0.1:5080/api/identidad?caja={alta.Caja.Id:D}&reto=", red.Pedidas.Last());
    }

    // ── «si» es solo con las dos: la prueba y la firma ──

    /// <summary>
    /// La prueba cuadra (quien contesta conoce la huella de la llave de esta caja), pero la llave pública que presenta no viene con una
    /// firma suya que cuadre: otra llave en el camino, una firma de otra pregunta, o un servicio de antes, que no firmaba. Tampoco si la
    /// llave viene escrita de otra forma que como se guarda (con un espacio en medio se lee igual, pero la caja la compara letra por
    /// letra). No se fija ninguna llave: no se sabe (la caja aprende la de su caja principal al abrirla).
    /// </summary>
    [Theory]
    [InlineData("LA", "otra-llave")]
    [InlineData("LB", "otra-firma")]
    [InlineData("LC", "sin-llave-ni-firma")]
    [InlineData("LD", "sin-firma")]
    [InlineData("LE", "llave-con-un-espacio")]
    public async Task Con_la_prueba_pero_sin_una_firma_que_cuadre_no_se_fija_ninguna_llave(string serie, string caso)
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, serie);
        GuardarComoLaCaja(alta.Caja.Id, alta.Llave, LlaveDeOtroServicio());
        var red = Conectar(tienda, Principal + ":5080");
        // La firma de otra pregunta (otro reto): de ese servicio, pero no de esta.
        using var cliente = new HttpClient(tienda.Server.CreateHandler());
        var otraPrueba = (await cliente.GetFromJsonAsync<IdentidadServicioDto>(
            $"http://{Principal}:5080/api/identidad?caja={alta.Caja.Id}&reto={Descubrimiento.NuevoReto()}"))!;
        red.AlContestarLaPrueba = async respuesta =>
        {
            var identidad = JsonNode.Parse(await respuesta.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(["prueba", "llaveDelServicio", "firma"], identidad.Select(dato => dato.Key));
            switch (caso)
            {
                case "otra-llave":
                    identidad["llaveDelServicio"] = LlaveDeOtroServicio();
                    break;
                case "otra-firma":
                    identidad["firma"] = otraPrueba.Firma;
                    break;
                case "sin-llave-ni-firma":
                    identidad.Remove("llaveDelServicio");
                    identidad.Remove("firma");
                    break;
                case "llave-con-un-espacio":
                    identidad["llaveDelServicio"] = ((string)identidad["llaveDelServicio"]!).Insert(40, " ");
                    break;
                default:
                    identidad["firma"] = null;
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(identidad.ToJsonString(), Encoding.UTF8, "application/json") };
        };

        var dice = await ProbarAsync();

        Assert.Equal("no-se", dice["conoce"]);
        Assert.Contains("probó conocer la llave de esta caja, pero no presentó su propia llave con una firma que cuadre", dice["conoce-motivo"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(alta.Llave, red);
    }

    /// <summary>
    /// En esa dirección contesta algo que dice ser Papelería POS, pero a la prueba contesta cualquier cosa (o una prueba que no es de la
    /// llave de esta caja): no la conoce. La caja de verdad tampoco le mandaría su llave.
    /// </summary>
    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "prueba": null }""")]
    [InlineData("""{ "prueba": 12 }""")]
    [InlineData("""{ "prueba": "" }""")]
    [InlineData("""{ "prueba": "0000000000000000000000000000000000000000000000000000000000000000" }""")]
    [InlineData("""[ "prueba" ]""")]
    [InlineData("""esto no es JSON""")]
    [InlineData("")]
    public async Task Lo_que_contesta_sin_ser_la_prueba_de_esta_caja_es_que_no_la_conoce(string contesta)
    {
        var llave = LlaveCaja.Nueva();
        GuardarComoLaCaja(Guid.NewGuid(), llave, LlaveDeOtroServicio());
        var red = new Grabadora(new Contesta(contesta));
        _red.EsElServicio(Principal + ":5080", red);

        var dice = await ProbarAsync();

        Assert.Equal("si", dice["contesta"]);
        Assert.Equal("no", dice["conoce"]);
        Assert.Equal("", dice["llave-del-servicio"]);
        NoSaleLaLlave(llave, red);
    }

    /// <summary>Lo que contesta de más (la prueba es un renglón) ni se lee entero: no es la prueba.</summary>
    [Fact]
    public async Task Una_respuesta_enorme_a_la_prueba_no_se_carga()
    {
        GuardarComoLaCaja(Guid.NewGuid(), LlaveCaja.Nueva(), LlaveDeOtroServicio());
        _red.EsElServicio(Principal + ":5080", new Contesta($$"""{ "prueba": "{{new string('A', 100_000)}}" }"""));

        Assert.Equal("no", (await ProbarAsync())["conoce"]);
    }

    /// <summary>Otro programa en esa dirección, o una principal de mentiras: su estado dice Papelería POS con «Matriz» y a la prueba contesta eso.</summary>
    private sealed class Contesta(string aLaPrueba) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/identidad" ? aLaPrueba : RedDePrueba.Estado(["Centro", "Matriz"]), Encoding.UTF8,
                    "application/json"),
            });
    }

    // ── Sin --esta-caja, como hoy ──

    /// <summary>
    /// «probar» sin <c>--esta-caja</c> dice lo de siempre, con las sucursales separadas con coma; con ella, las mismas claves y las
    /// sucursales unidas con «; », como dice «tienda» la marca de la mudanza (instalar-pc.ps1 las compara).
    /// </summary>
    [Fact]
    public async Task Sin_esta_caja_probar_dice_lo_de_siempre()
    {
        _red.EsElServicio(Principal + ":5080", new Contesta("{}"));
        string[] deSiempre = ["contesta", "direccion", "version", "sucursales", "una-sola-caja", "tienda-nueva", "aviso", "texto", "error"];

        Assert.Equal(0, await Ayudante.CorrerAsync(["probar", "--direccion", Principal, "--salida", Salida], _pc, _red, Plazos));
        var sin = ArchivoDeClaves.Leer(Salida);
        Assert.Equal(0, await Ayudante.CorrerAsync(["probar", "--direccion", Principal, "--esta-caja", "no", "--salida", Salida], _pc, _red, Plazos));
        var conNo = ArchivoDeClaves.Leer(Salida);
        var con = await ProbarAsync();

        Assert.Equal(deSiempre, sin.Datos.Select(d => d.Clave));
        Assert.Equal("Centro, Matriz", sin["sucursales"]);
        Assert.Equal(sin.Datos, conNo.Datos);
        Assert.Equal([.. deSiempre, "tienda", "conoce", "conoce-motivo", "llave-del-servicio"], con.Datos.Select(d => d.Clave));
        Assert.Equal("Centro; Matriz", con["sucursales"]);
        Assert.Equal(sin.Datos.Where(d => d.Clave != "sucursales"), con.Datos.Take(deSiempre.Length).Where(d => d.Clave != "sucursales"));
    }

    /// <summary>Con una dirección que no se entiende no se le pregunta a nadie, y lo de la caja de esta PC se dice igual.</summary>
    [Fact]
    public async Task Con_una_direccion_que_no_se_entiende_tampoco_truena()
    {
        GuardarComoLaCaja(Guid.NewGuid(), LlaveCaja.Nueva(), LlaveDeOtroServicio());

        var dice = await ProbarAsync("la de enfrente");

        Assert.Equal("no", dice["contesta"]);
        Assert.Contains("va la IP de la caja principal", dice["error"]);
        Assert.Equal("no", dice["tienda"]);
        Assert.Equal("no-se", dice["conoce"]);
        Assert.Empty(_red.Preguntadas);
    }

    // ── Lo mismo que hace la caja, pieza por pieza ──

    /// <summary>
    /// Lo que el ayudante tiene escrito aparte da lo mismo que el código de la caja y del servicio (Descubrimiento, LlaveCaja): la huella
    /// de la llave, la dirección que entra en la prueba, la prueba, lo que se firma y cuándo una firma vale.
    /// </summary>
    [Fact]
    public void Cada_pieza_de_la_prueba_da_lo_mismo_que_la_de_la_caja()
    {
        Assert.Equal(Descubrimiento.RutaIdentidad, IdentidadDeLaPrincipal.RutaIdentidad);
        Assert.Equal(ReencuentroDeLaPrincipal.TopeDeLaRespuesta, IdentidadDeLaPrincipal.TopeDeLaRespuesta);
        using var servicio = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publica = Convert.ToBase64String(servicio.ExportSubjectPublicKeyInfo());
        for (var i = 0; i < 20; i++)
        {
            var llave = LlaveCaja.Nueva();
            var huella = LlaveCaja.Huella(llave);
            Assert.Equal(huella, IdentidadDeLaPrincipal.Huella(Encoding.UTF8.GetBytes(llave)));
            var caja = Guid.NewGuid();
            var reto = IdentidadDeLaPrincipal.NuevoReto();
            Assert.True(Descubrimiento.EsRetoValido(reto), reto);
            Assert.Equal(Descubrimiento.NuevoReto().Length, reto.Length);
            var direccion = $"192.0.2.{i + 1}:{5080 + i}";
            var prueba = Descubrimiento.Prueba(huella, caja, reto, direccion);
            Assert.Equal(prueba, IdentidadDeLaPrincipal.Prueba(huella, caja, reto, direccion));
            Assert.Equal(Descubrimiento.MensajeFirmado(caja, reto, direccion, prueba), IdentidadDeLaPrincipal.MensajeFirmado(caja, reto, direccion, prueba));
            // La firma, como la hace el servicio (LlaveDelServicio.Firmar).
            var mensaje = Descubrimiento.MensajeFirmado(caja, reto, direccion, prueba);
            var firma = Convert.ToBase64String(servicio.SignData(mensaje, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            foreach (var (conLlave, sobre, conFirma) in new (string?, byte[], string?)[]
                     {
                         (publica, mensaje, firma), (publica, Descubrimiento.MensajeFirmado(caja, reto, "192.0.2.200:5080", prueba), firma), (LlaveDeOtroServicio(), mensaje, firma),
                         (publica, mensaje, null), (null, mensaje, firma), (publica, mensaje, "no es base64"), ("no es una llave", mensaje, firma), (publica, mensaje, ""),
                         (new string('A', 1028), mensaje, firma), (publica, mensaje, new string('A', 1028)),
                     })
                Assert.Equal(Descubrimiento.FirmaValida(conLlave, sobre, conFirma), IdentidadDeLaPrincipal.FirmaValida(conLlave, sobre, conFirma));
            Assert.True(IdentidadDeLaPrincipal.FirmaValida(publica, mensaje, firma));
            foreach (var contesto in new[] { prueba, prueba.ToLowerInvariant(), prueba[..^1], prueba + "0", "", null, "ñ" + prueba[1..], Descubrimiento.Prueba(huella, caja, reto, "192.0.2.200:5080") })
                Assert.Equal(Descubrimiento.EsLaPrueba(contesto, prueba), IdentidadDeLaPrincipal.EsLaPrueba(contesto, prueba));
            Assert.True(IdentidadDeLaPrincipal.EsLaPrueba(prueba.ToLowerInvariant(), prueba));
        }
        // La dirección que entra en la prueba, de la dirección que queda en caja.json.
        foreach (var texto in new[]
                 {
                     "192.168.1.10", "192.168.1.10:5090", "http://192.168.1.10:5080/", "https://192.168.1.10", "http://192.168.1.10:80", "PRINCIPAL", "principal:5090",
                     "localhost", "127.0.0.1", "[::1]:5080", "[fe80::1]", "[::ffff:192.168.1.10]:5080", "010.1.2.3", "192.168.1",
                 })
        {
            var guardada = new Uri(BusquedaDeLaPrincipal.Direccion(texto)!.ToString());
            Assert.Equal(Descubrimiento.Direccion(guardada), IdentidadDeLaPrincipal.Direccion(guardada));
        }
        Assert.Equal("192.168.1.10:5080", IdentidadDeLaPrincipal.Direccion(BusquedaDeLaPrincipal.Direccion("192.168.1.10")!));
        Assert.Equal("192.168.1.10:5090", IdentidadDeLaPrincipal.Direccion(BusquedaDeLaPrincipal.Direccion("192.168.1.10:5090")!));
        Assert.Null(IdentidadDeLaPrincipal.Direccion(BusquedaDeLaPrincipal.Direccion("PRINCIPAL")!));
    }

    /// <summary>
    /// En Windows, la llave se abre como la guarda la caja: lo que cierra <c>ProtectedData.Protect(…, null, LocalMachine)</c>
    /// (ConfiguracionCaja.Guardar) lo abre el ayudante con crypt32, sin paquetes. Lo que no es de esta PC no se abre, sin tronar. En los
    /// demás sistemas no hay DPAPI: lo de arriba usa el de las pruebas.
    /// </summary>
    [Fact]
    public void En_windows_la_llave_se_abre_como_la_guarda_la_caja()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var equipo = new EquipoWindows();
        var llave = LlaveCaja.Nueva();
        var guardada = ProtectedData.Protect(Encoding.UTF8.GetBytes(llave), null, DataProtectionScope.LocalMachine);

        Assert.Equal(llave, Encoding.UTF8.GetString(equipo.AbrirLoDeEstaPc(guardada)!));
        Assert.Equal(LlaveCaja.Huella(llave), IdentidadDeLaPrincipal.Huella(equipo.AbrirLoDeEstaPc(guardada)));

        guardada[^1] ^= 0xFF;
        Assert.Null(equipo.AbrirLoDeEstaPc(guardada));
        Assert.Null(equipo.AbrirLoDeEstaPc([1, 2, 3]));
        Assert.Null(equipo.AbrirLoDeEstaPc([]));
        // Con otra entropía no es lo que guarda la caja: tampoco se abre.
        Assert.Null(equipo.AbrirLoDeEstaPc(ProtectedData.Protect(Encoding.UTF8.GetBytes(llave), "otra cosa"u8.ToArray(), DataProtectionScope.LocalMachine)));
    }
}
