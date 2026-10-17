using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las llamadas de esta fábrica llegan a la caja principal (192.168.1.10:5080) desde otra PC (la caja 2, en 192.168.1.50, o la que
/// se diga): como en la red de la tienda, donde la caja se conecta directo a la IP del servicio.
/// </summary>
internal sealed class EnLaRedDeLaTienda(string remota = "192.168.1.50", string local = "192.168.1.10", int puerto = 5080) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
    {
        app.Use((contexto, resto) =>
        {
            contexto.Connection.RemoteIpAddress = IPAddress.Parse(remota);
            contexto.Connection.LocalIpAddress = IPAddress.Parse(local);
            contexto.Connection.LocalPort = puerto;
            return resto(contexto);
        });
        siguiente(app);
    };
}

/// <summary>
/// La red de la tienda en el POS, contra el servicio de verdad: la caja principal prueba quién es solo con la huella de la llave de
/// cada caja y solo a la red de la tienda; la caja se cambia sola a su caja principal en su IP nueva (y no a quien le pasa la
/// pregunta); y el servicio dice si hay internet, para la franja.
/// </summary>
public sealed class RedDeLaTiendaApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaTienda = [(IPAddress.Parse("192.168.1.10"), 24)];

    /// <summary>La caja principal en la red de la tienda: sabe sus redes y recibe lo que manda la PC de <paramref name="remota"/>.</summary>
    private WebApplicationFactory<Program> EnLaTienda(string remota = "192.168.1.50") =>
        servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new EnLaRedDeLaTienda(remota));
            s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
        }));

    private async Task<AltaCajaDto> DarDeAltaAsync(WebApplicationFactory<Program> fabrica, string serie)
    {
        var sucursal = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;
        var respuesta = await fabrica.CreateClient().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", sucursal, null, $"Caja {serie}", serie, $"PC-{serie}"));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<AltaCajaDto>())!;
    }

    [Fact]
    public async Task La_caja_principal_prueba_quien_es_con_la_huella_de_la_llave_de_esa_caja_sin_pedir_la_llave()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "R");
        var reto = Descubrimiento.NuevoReto();

        var respuesta = await tienda.CreateClient().GetAsync($"api/identidad?caja={alta.Caja.Id}&reto={reto}");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var identidad = (await respuesta.Content.ReadFromJsonAsync<IdentidadServicioDto>())!;
        Assert.True(Descubrimiento.EsLaPrueba(identidad.Prueba, Descubrimiento.Prueba(LlaveCaja.Huella(alta.Llave), alta.Caja.Id, reto, "192.168.1.10:5080")));
        // Ni la llave ni su huella salen en la respuesta.
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(LlaveCaja.Huella(alta.Llave), cuerpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(alta.Llave, cuerpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_prueba_nada_de_una_caja_que_no_dio_de_alta_ni_sin_reto_ni_fuera_de_la_tienda()
    {
        await using var tienda = EnLaTienda();
        await using var deAfuera = EnLaTienda(remota: "201.141.20.7");
        var alta = await DarDeAltaAsync(tienda, "S");
        var reto = Descubrimiento.NuevoReto();

        var otraCaja = await tienda.CreateClient().GetAsync($"api/identidad?caja={Guid.NewGuid()}&reto={reto}");
        var sinReto = await tienda.CreateClient().GetAsync($"api/identidad?caja={alta.Caja.Id}");
        var retoMalo = await tienda.CreateClient().GetAsync($"api/identidad?caja={alta.Caja.Id}&reto=corto");
        var afuera = await deAfuera.CreateClient().GetAsync($"api/identidad?caja={alta.Caja.Id}&reto={reto}");

        Assert.Equal(HttpStatusCode.NotFound, otraCaja.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sinReto.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, retoMalo.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, afuera.StatusCode);
        Assert.Equal("fuera-de-la-red", (await afuera.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
    }

    /// <summary>
    /// La caja guardó la IP de antes de la caja principal (192.168.1.99). En la red contestan dos: un equipo (192.168.1.66) que le
    /// pasa la pregunta a la caja principal de verdad, y la caja principal en su IP nueva (192.168.1.10). La prueba de la caja
    /// principal es para su dirección, así que la del que pasa la pregunta no coincide: la caja se cambia solo a la de verdad.
    /// </summary>
    [Fact]
    public async Task La_caja_se_cambia_a_su_caja_principal_y_no_al_que_le_pasa_la_pregunta()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "T");
        var guardadas = new List<Uri>();
        var conexion = new ConexionCaja
        {
            Servicio = new Uri("http://192.168.1.99:5080/"),
            CajaId = alta.Caja.Id,
            Llave = alta.Llave,
            PideAlta = true,
            AlGuardar = c =>
            {
                guardadas.Add(c.Servicio);
                return Task.CompletedTask;
            },
        };
        // El servicio de prueba contesta a cualquier dirección: el que pasa la pregunta y la caja principal llegan al mismo, que
        // firma con la dirección en que recibe (192.168.1.10:5080).
        var http = new HttpClient(tienda.Server.CreateHandler());
        var buscador = new Contestan(new Uri("http://192.168.1.66:5080/"), new Uri("http://192.168.1.10:5080/"));
        var reencuentro = new ReencuentroDeLaPrincipal(conexion, buscador, http);

        Assert.True(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(new Uri("http://192.168.1.10:5080/"), conexion.Servicio);
        Assert.Equal([new Uri("http://192.168.1.10:5080/")], guardadas);
    }

    /// <summary>
    /// De punta a punta: la caja con su contingencia deja de llegar a la IP guardada; a los 30 s la busca en la red, la encuentra,
    /// comprueba que es su caja principal, guarda la dirección nueva y el latido vuelve a estar bien, sin que nadie toque nada. La
    /// bitácora de red lo cuenta.
    /// </summary>
    [Fact]
    public async Task Si_le_cambia_la_ip_a_la_caja_principal_la_caja_la_encuentra_sola()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "U");
        using var carpeta = new CarpetaDePrueba();
        var baseLocal = Path.Combine(carpeta.Ruta, "caja-local.db");
        var reloj = RelojDeLaRed.Nuevo();
        var guardadas = new List<Uri>();
        var conexion = new ConexionCaja
        {
            Servicio = new Uri("http://192.168.1.99:5080/"),
            CajaId = alta.Caja.Id,
            Llave = alta.Llave,
            PideAlta = true,
            AlGuardar = c =>
            {
                lock (guardadas)
                    guardadas.Add(c.Servicio);
                return Task.CompletedTask;
            },
        };
        // La IP de antes ya no es de nadie: no se puede conectar a ella. La nueva llega al servicio.
        var red = new HttpClient(new SoloA("192.168.1.10", tienda.Server.CreateHandler()));
        var bitacora = new BitacoraDeRed(carpeta.Ruta, "red-caja", reloj);
        bitacora.Empezar("esta caja");
        var vigia = new VigiaDeLaRed(bitacora, new EquipoFalso(), new PruebasFalsas(), new OpcionesDelVigia(), reloj);
        var reencuentro = new ReencuentroDeLaPrincipal(conexion, new Contestan(new Uri("http://192.168.1.10:5080/")), red, reloj, bitacora);
        var contingencia = new Contingencia(new AlmacenLocal(baseLocal), red, conexion, reloj,
            redDeLaCaja: new RedDeLaCaja(conexion, bitacora, vigia, reencuentro));
        try
        {
            for (var i = 0; i < 6; i++)
            {
                await contingencia.LatidoAsync();
                Assert.Equal(EstadoRed.Caida, contingencia.EstadoDeLaRed);
                reloj.Avanzar(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(EstadoRed.Caida, contingencia.EstadoDeLaRed);

            // A los 30 s sin contestar: la busca, la encuentra y se cambia; el latido que sigue ya contesta.
            await contingencia.LatidoAsync();
            await reencuentro.UltimaBusqueda;
            await VentaConCopia.EsperarAsync(() => contingencia.EstadoDeLaRed == EstadoRed.Buena, "el latido a la dirección nueva");

            Assert.Equal(new Uri("http://192.168.1.10:5080/"), conexion.Servicio);
            Assert.Equal([new Uri("http://192.168.1.10:5080/")], guardadas);
            // El latido deja la red buena y después lo anota: se espera la línea, no se supone que ya está.
            await VentaConCopia.EsperarAsync(() => carpeta.Lineas().Any(l => l.StartsWith("Volvió «la caja principal» tras 30 s", StringComparison.Ordinal)),
                "«Volvió «la caja principal»» en la bitácora");
            var lineas = carpeta.Lineas();
            Assert.Contains(lineas, l => l.StartsWith("Se perdió «la caja principal»: 192.168.1.99:5080 no contesta", StringComparison.Ordinal));
            Assert.Contains(lineas, l => l.StartsWith("Se busca la caja principal en la red: 192.168.1.99:5080 no contesta desde hace 30 s", StringComparison.Ordinal));
            Assert.Contains(lineas, l => l.StartsWith("La caja principal cambió de dirección: 192.168.1.99:5080 → 192.168.1.10:5080.", StringComparison.Ordinal));
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// La caja abre sin la caja principal: la bitácora de red empieza antes del primer latido (que ya anota que no contesta) y, al
    /// cerrar la caja, termina. Así la caída queda en el resumen y no se pierde al empezar.
    /// </summary>
    [Fact]
    public async Task La_bitacora_empieza_antes_del_primer_latido_y_termina_al_cerrar()
    {
        using var carpeta = new CarpetaDePrueba();
        var conexion = new ConexionCaja { Servicio = new Uri("http://192.168.1.99:5080/"), CajaId = Guid.NewGuid(), Llave = LlaveCaja.Nueva(), PideAlta = true };
        var red = new HttpClient(new SoloA("192.168.1.10", servicio.Fabrica.Server.CreateHandler()));
        var bitacora = new BitacoraDeRed(carpeta.Ruta, "red-caja");
        // Un arranque lento de la bitácora (leer lo de días anteriores en un disco lento): aun así, el primer latido va después.
        var vigia = new VigiaDeLaRed(bitacora, new EquipoFalso(), new PruebasFalsas(), new OpcionesDelVigia
        {
            Quien = "esta caja",
            AlTerminar = "se cerró la caja",
            Encendida = () =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
                return TimeSpan.FromMinutes(5);
            },
        });
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(carpeta.Ruta, "caja-local.db")), red, conexion,
            redDeLaCaja: new RedDeLaCaja(conexion, bitacora, vigia));
        try
        {
            contingencia.Iniciar();
            await VentaConCopia.EsperarAsync(() => bitacora.Perdido("la caja principal"), "«Se perdió «la caja principal»» en la bitácora");
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }

        var lineas = carpeta.Lineas();
        Assert.StartsWith("Empieza la bitácora de red de esta caja", lineas[0]);
        Assert.Single(lineas, l => l.StartsWith("Empieza", StringComparison.Ordinal));
        Assert.Contains(lineas, l => l.StartsWith("Se perdió «la caja principal»: 192.168.1.99:5080 no contesta", StringComparison.Ordinal));
        Assert.Equal("Termina la bitácora de red: se cerró la caja.", lineas[^1]);
        Assert.Equal("la caja principal", Assert.Single(bitacora.Caidas()).Que);
    }

    /// <summary>
    /// El servicio revisa el internet (con la red de mentira: sin red de verdad) y lo dice en su estado; la caja lo junta con su
    /// latido y la franja dice «Sin internet: la venta sigue. No desconectes nada.»; si además no llega a la caja principal, «Sin
    /// red local» con «Volver a buscarla».
    /// </summary>
    [Fact]
    public async Task El_servicio_dice_si_hay_internet_y_la_franja_de_la_caja_lo_junta_con_su_latido()
    {
        using var carpeta = new CarpetaDePrueba();
        var pruebas = new PruebasFalsas().SinInternet();
        await using var conVigia = await ServicioDePrueba.CrearAsync(("Red:Vigilar", "true"), ("Red:Carpeta", carpeta.Ruta));
        await using var tienda = conVigia.Fabrica.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IPruebasDeRed>(pruebas);
            s.AddSingleton<IRedDelEquipo>(new EquipoFalso());
        }));
        var vigia = tienda.Services.GetRequiredService<VigiaDeLaRed>();
        await vigia.RevisarAsync();
        await vigia.RevisarAsync();

        var estado = await tienda.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado");
        var resumen = await tienda.CreateClient().GetFromJsonAsync<ResumenDeRedDto>("api/red");

        Assert.Equal(new RedDeLaPrincipalDto(false, true), estado!.Red! with { DesdeUtc = null });
        Assert.Equal("internet", Assert.Single(resumen!.Caidas).Que);
        Assert.Null(resumen.Caidas[0].Hasta);
        Assert.Equal(carpeta.Ruta, resumen.Carpeta);
        Assert.Contains(carpeta.Lineas("red-servicio"), l => l.StartsWith("Se perdió «internet»", StringComparison.Ordinal));

        // La caja 2 con su latido: la caja principal contesta y dice que no hay internet.
        var baseLocal = Path.Combine(carpeta.Ruta, "caja-local.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://192.168.1.10:5080/"), CajaId = (await tienda.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].Id };
        var redDePrueba = new RedDePrueba(tienda.Server.CreateHandler());
        var contingencia = new Contingencia(new AlmacenLocal(baseLocal), new HttpClient(redDePrueba), conexion);
        var servicios = new ServiceCollection().AddSingleton(conexion).AddSingleton(contingencia).BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        try
        {
            await contingencia.LatidoAsync();
            await pantalla.AbrirAsync<FranjaContingencia>([]);
            await pantalla.EsperarAsync(t => t.Contains(AvisosDeRed.SinInternet, StringComparison.Ordinal), "«Sin internet» en la franja");
            // El chip «Sin internet» de arriba (y la entrada) dicen lo mismo que la franja.
            Assert.Equal(AvisosDeRed.SinInternet, EstadoCaja.SinInternet(contingencia, conexion));

            redDePrueba.Caida = true;
            await contingencia.LatidoAsync();
            await pantalla.EsperarAsync(t => t.Contains(AvisosDeRed.SinRedLocal, StringComparison.Ordinal), "«Sin red local» en la franja");
            Assert.DoesNotContain(AvisosDeRed.SinInternet, await pantalla.TextoAsync(), StringComparison.Ordinal);
            // Sin llegar a la caja principal no se sabe del internet: el chip no lo dice.
            Assert.Null(EstadoCaja.SinInternet(contingencia, conexion));
        }
        finally
        {
            await ((IAsyncDisposable)pantalla).DisposeAsync();
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
            await servicios.DisposeAsync();
        }
    }

    /// <summary>Un buscador que encuentra siempre lo mismo.</summary>
    private sealed class Contestan(params Uri[] servicios) : IBuscadorDeLaPrincipal
    {
        public Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>(servicios);
    }

    /// <summary>Solo la dirección que se diga llega al servicio; a las demás no se puede conectar.</summary>
    private sealed class SoloA(string host, HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == host
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException(HttpRequestError.ConnectionError, "No contesta.");
    }
}
