using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La instalación desde cero en la caja (2026-10-31), sin navegador y contra el servicio de verdad: en la PC del servicio, con una base
/// nueva, la caja crea la tienda en tres pasos y el dueño queda adentro; en otra PC dice que se termine en la caja principal, y una caja
/// que ya estaba dada de alta no crea otra tienda. Después, «Terminar la instalación» dice lo que falta y los costos de las impresiones
/// se cambian en la Configuración, un renglón a la vez. Con lo de la revisión independiente del 2026-10-31.
/// </summary>
public sealed class TiendaNuevaEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    /// <summary>Una impresora de tickets que imprime todo al momento.</summary>
    private sealed class ImpresoraQueImprime : IImpresora
    {
        public int Impresos { get; private set; }

        public bool MuestraVistaPrevia => false;

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            Impresos++;
            return Task.CompletedTask;
        }
    }

    /// <summary>En «esta PC» (localhost) no hay servicio: la caja está en otra PC de la red.</summary>
    private sealed class SinServicioEnEstaPc : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.IsLoopback ? throw new HttpRequestException("En esta PC no hay servicio.") : base.SendAsync(request, ct);
    }

    /// <summary>El servicio de esta PC todavía no arranca hasta que se diga (justo después de instalar, o al prender la PC).</summary>
    private sealed class ServicioQueArranca : DelegatingHandler
    {
        private int _intentos;

        public volatile bool Arranco;

        public int Intentos => Volatile.Read(ref _intentos);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _intentos);
            return Arranco ? base.SendAsync(request, ct) : throw new HttpRequestException("Todavía no arranca.");
        }
    }

    /// <summary>La franja de la prueba recarga la página con esto; aquí no hace nada.</summary>
    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    private static ServiceProvider Servicios(ConexionCaja conexion, HttpClient http, IImpresora? impresora = null, PaginaQueContesta? pagina = null) =>
        new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(http, conexion))
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina ?? new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .AddSingleton<NavigationManager>(new NavegacionDePrueba())
            .BuildServiceProvider();

    /// <summary>A dónde mandó el cursor la pantalla la última vez (lo que le pidió a la página).</summary>
    private static string? UltimoFoco(PaginaQueContesta pagina) =>
        pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?[0] as string;

    [Fact]
    public async Task En_la_pc_del_servicio_la_caja_crea_la_tienda_en_tres_pasos_y_el_dueno_queda_adentro()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        await using var servicios = Servicios(conexion, nueva.Fabrica.CreateClient());
        SesionDto? entro = null;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfigurarCaja>(new()
        {
            [nameof(ConfigurarCaja.AlCrearTienda)] = EventCallback.Factory.Create<SesionDto>(this, s => entro = s),
        });

        // 1. La tienda: con lo que se propone.
        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal"), "el primer paso");
        Assert.Equal(TiendaNueva.SucursalPorOmision, (await p.PorIdAsync("tn-sucursal")).Valor);
        await p.OprimirAsync(await p.BotonAsync("Siguiente"));
        // 2. La cuenta del dueño: el usuario sigue al nombre, y los dos PIN tienen que ser iguales.
        await p.EsperarAsync(t => t.Contains("Tu nombre"), "el segundo paso");
        await p.EscribirAsync("tn-nombre", "Mario Álvarez");
        Assert.Equal("mario", (await p.PorIdAsync("tn-usuario")).Valor);
        await p.EscribirAsync("tn-pin", "4321");
        await p.EscribirAsync("tn-pin2", "4312");
        await p.TeclaAsync("tn-pin2", "Enter");
        Assert.Contains("Los dos PIN no son iguales: escríbelo otra vez.", await p.TextoAsync());
        Assert.Equal("", (await p.PorIdAsync("tn-pin")).Valor);
        await p.EscribirAsync("tn-pin", "4321");
        await p.EscribirAsync("tn-pin2", "4321");
        await p.TeclaAsync("tn-pin2", "Enter");
        // 3. Esta caja: «Caja 1», serie A; con Enter se crea todo.
        await p.EsperarAsync(t => t.Contains("Serie de folios"), "el tercer paso");
        Assert.Contains("A-000001", await p.TextoAsync());
        await p.TeclaAsync("tn-serie", "Enter");

        await VentaConCopia.EsperarAsync(() => entro is not null, "el dueño adentro");
        Assert.Equal("Mario Álvarez", entro!.Nombre);
        Assert.NotNull(conexion.CajaId);
        Assert.NotNull(conexion.Llave);
        Assert.NotNull(conexion.LlaveDelServicio);
        Assert.False((await nueva.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.TiendaNueva);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_otra_pc_la_caja_dice_que_se_termine_primero_en_la_caja_principal()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://192.168.1.10:5080/"), PideAlta = true };
        await using var servicios = Servicios(conexion, nueva.Fabrica.CreateDefaultClient(new SinServicioEnEstaPc()));
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        await p.AbrirAsync<ConfigurarCaja>([]);

        await p.EsperarAsync(t => t.Contains(TiendaNueva.TerminaEnLaPrincipal, StringComparison.Ordinal), "que se termine en la caja principal");
        Assert.DoesNotContain("Nombre de la sucursal", await p.TextoAsync());
        Assert.NotNull(await p.BotonAsync("Ya se terminó: volver a probar"));
        Assert.Null(conexion.CajaId);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// La caja de la PC del servicio ya estaba dada de alta (caja.json con su caja y su llave) y el servicio arranca con una base vacía
    /// (pos.db movida o cambiada al actualizar). No ofrece crear otra tienda ni toca su alta: se detiene y dice qué pasa (revisión
    /// independiente del 2026-10-31; la prueba de la revisión esperaba «Sin conexión»: ahora la tarjeta dice que amaneció sin tienda).
    /// </summary>
    [Fact]
    public async Task Una_caja_dada_de_alta_no_ofrece_crear_otra_tienda_si_su_base_amanece_vacia()
    {
        await using var vacia = await ServicioDePrueba.CrearSinTiendaAsync();
        var cajaDeAntes = Guid.NewGuid();
        var conexion = new ConexionCaja
        {
            Servicio = new Uri("http://localhost:5080/"), PideAlta = true, CajaId = cajaDeAntes, Llave = "la-llave-de-la-caja-de-antes",
        };
        await using var servicios = Servicios(conexion, vacia.Fabrica.CreateClient());
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        await p.AbrirAsync<PantallaCaja>([]);

        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal") || t.Contains(TiendaNueva.TituloSinTienda), "lo que dice la caja");
        var texto = await p.TextoAsync();
        Assert.False(texto.Contains("Nombre de la sucursal", StringComparison.Ordinal), $"Ofrece «Tienda nueva» a una caja ya dada de alta:\n{texto}");
        Assert.Contains(TiendaNueva.SinTiendaEnLaPrincipal, texto);
        Assert.Equal(cajaDeAntes, conexion.CajaId);
        Assert.Equal("la-llave-de-la-caja-de-antes", conexion.Llave);
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo mismo si esa caja se vuelve a dar de alta («Cambiar la caja principal o dar de alta de nuevo»): no crea otra tienda.</summary>
    [Fact]
    public async Task Una_caja_dada_de_alta_tampoco_crea_la_tienda_al_darse_de_alta_otra_vez()
    {
        await using var vacia = await ServicioDePrueba.CrearSinTiendaAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true, CajaId = Guid.NewGuid(), Llave = "llave" };
        await using var servicios = Servicios(conexion, vacia.Fabrica.CreateClient());
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        await p.AbrirAsync<ConfigurarCaja>([]);

        await p.EsperarAsync(t => t.Contains(TiendaNueva.SinTiendaEnLaPrincipal, StringComparison.Ordinal), "que amaneció sin tienda");
        Assert.DoesNotContain("Nombre de la sucursal", await p.TextoAsync());
        Assert.True((await vacia.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.TiendaNueva);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Recién instalado (o al prender la PC), el servicio de esta PC tarda en contestar: la caja lo sigue buscando un rato, diciéndolo,
    /// en lugar de mandar a escribir la dirección (revisión independiente del 2026-10-31).
    /// </summary>
    [Fact]
    public async Task En_la_pc_del_servicio_espera_a_que_arranque_su_servicio()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        var arranque = new ServicioQueArranca();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        await using var servicios = Servicios(conexion, nueva.Fabrica.CreateDefaultClient(arranque));
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        // Abrirla termina cuando termina de cargar: aquí, cuando el servicio contesta.
        var abierta = p.AbrirAsync<ConfigurarCaja>(new() { [nameof(ConfigurarCaja.EntreIntentos)] = TimeSpan.FromMilliseconds(50) });

        await p.EsperarAsync(t => t.Contains("Buscando el servicio de esta PC"), "que lo está buscando");
        await VentaConCopia.EsperarAsync(() => arranque.Intentos >= 3, "otros intentos");
        Assert.DoesNotContain("Dirección de la caja principal", await p.TextoAsync());
        arranque.Arranco = true;
        await abierta;
        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal"), "la tienda nueva");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Si pasado el rato el servicio de esta PC no contesta, la dirección ya dice «localhost» y no se manda a buscar en la red (con «La
    /// única caja» no encontraría nada): se dice qué revisar, con el cursor en la dirección.
    /// </summary>
    [Fact]
    public async Task Si_el_servicio_de_esta_pc_no_contesta_no_manda_a_buscarlo_en_la_red()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        var pagina = new PaginaQueContesta();
        await using var servicios = Servicios(conexion, servicio.Fabrica.CreateDefaultClient(new SinServicioEnEstaPc()), pagina: pagina);
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        await p.AbrirAsync<ConfigurarCaja>(new()
        {
            [nameof(ConfigurarCaja.EsperaDelServicio)] = TimeSpan.FromMilliseconds(200),
            [nameof(ConfigurarCaja.EntreIntentos)] = TimeSpan.FromMilliseconds(50),
        });

        await p.EsperarAsync(t => t.Contains("El servicio de esta PC no contesta todavía"), "lo que hay que revisar");
        Assert.Equal("localhost", (await p.PorIdAsync("direccion")).Valor);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Buscar en la red");
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#direccion", "el cursor en la dirección");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// «La única caja»: desde otra PC el servicio ya lo dice en /api/estado, así que la caja lo dice antes de pedir el usuario y el PIN
    /// del dueño, que viajarían por la red para nada (revisión independiente del 2026-10-31).
    /// </summary>
    [Fact]
    public async Task Con_la_unica_caja_otra_pc_lo_dice_antes_de_pedir_el_pin()
    {
        await using var unica = await ServicioDePrueba.CrearAsync(("Instalacion:UnaSolaCaja", "true"));
        var desdeLaRed = unica.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaRed>()))
            .CreateDefaultClient(new SinServicioEnEstaPc());
        var conexion = new ConexionCaja { Servicio = new Uri("http://192.168.1.10:5080/"), PideAlta = true };
        await using var servicios = Servicios(conexion, desdeLaRed);
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;

        await p.AbrirAsync<ConfigurarCaja>([]);

        await p.EsperarAsync(t => t.Contains("Conectado a"), "la caja principal");
        var (texto, elementos) = await p.LeerAsync();
        Assert.True(texto.Contains("La única caja", StringComparison.Ordinal) && !elementos.Any(e => e.Atributos.GetValueOrDefault("type") is "password"),
            $"Pide el PIN antes de decir que es «La única caja»:\n{texto}");
        Assert.Contains(TiendaNueva.UnaSolaCaja, texto);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Los dos PIN no coinciden: se borran los dos para escribirlos otra vez, completos, y el cursor va al primero (revisión
    /// independiente del 2026-10-31).
    /// </summary>
    [Fact]
    public async Task Si_los_dos_pin_no_coinciden_el_cursor_va_al_primero()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        var pagina = new PaginaQueContesta();
        await using var servicios = Servicios(conexion, nueva.Fabrica.CreateClient(), pagina: pagina);
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfigurarCaja>(new()
        {
            [nameof(ConfigurarCaja.AlCrearTienda)] = EventCallback.Factory.Create<SesionDto>(this, _ => { }),
        });
        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal"), "el primer paso");
        await p.OprimirAsync(await p.BotonAsync("Siguiente"));
        await p.EsperarAsync(t => t.Contains("Tu nombre"), "el segundo paso");
        await p.EscribirAsync("tn-nombre", "Mario Álvarez");
        await p.EscribirAsync("tn-pin", "4321");
        await p.EscribirAsync("tn-pin2", "4312");

        await p.TeclaAsync("tn-pin2", "Enter");

        await p.EsperarAsync(t => t.Contains("Los dos PIN no son iguales"), "el aviso");
        Assert.Equal(("", ""), ((await p.PorIdAsync("tn-pin")).Valor ?? "", (await p.PorIdAsync("tn-pin2")).Valor ?? ""));
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#tn-pin", "el cursor en el primer PIN");
    }

    /// <summary>
    /// Con el teclado (revisión independiente del 2026-10-31): Enter pasa al campo que sigue (en «Tu nombre», al usuario, sin decir
    /// todavía que falta el PIN) y, en el último, al paso que sigue; Esc regresa al paso de antes, como «Volver».
    /// </summary>
    [Fact]
    public async Task Con_el_teclado_Enter_pasa_al_campo_que_sigue_y_Esc_regresa_un_paso()
    {
        await using var nueva = await ServicioDePrueba.CrearSinTiendaAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        var pagina = new PaginaQueContesta();
        await using var servicios = Servicios(conexion, nueva.Fabrica.CreateClient(), pagina: pagina);
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfigurarCaja>([]);
        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal"), "el primer paso");
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#tn-sucursal", "el cursor en la sucursal");

        await p.TeclaAsync("tn-sucursal", "Enter");
        await p.EsperarAsync(t => t.Contains("Tu nombre"), "el segundo paso");
        await p.EscribirAsync("tn-nombre", "Mario Álvarez");
        await p.TeclaAsync("tn-nombre", "Enter");

        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#tn-usuario", "el cursor en el usuario");
        Assert.DoesNotContain("El PIN es de", await p.TextoAsync());
        await p.TeclaAsync("tn-usuario", "Enter");
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#tn-pin", "el cursor en el PIN");

        var paso = await p.ElementoAsync(e => e.Clase == "entrada-main", "el paso");
        await p.DispararAsync(paso, "onkeydown", new KeyboardEventArgs { Key = "Escape" });

        await p.EsperarAsync(t => t.Contains("Nombre de la sucursal"), "el primer paso otra vez");
        await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#tn-sucursal", "el cursor en la sucursal otra vez");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Terminar_la_instalacion_dice_lo_que_falta_y_al_terminarla_sale_de_la_configuracion()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var impresora = new ImpresoraQueImprime();
        var pagina = new PaginaQueContesta();
        await using var servicios = Servicios(conexion, servicio.Fabrica.CreateClient(), impresora, pagina);
        var dueno = await servicios.GetRequiredService<ClienteCaja>().EntrarAsync("dueno", "3333");
        var instalacion = servicios.GetRequiredService<InstalacionDeLaCaja>();

        await using (var lista = new PantallaSinNavegador(servicios))
        {
            await lista.AbrirAsync<TerminarInstalacion>(new() { [nameof(TerminarInstalacion.Caja)] = caja, [nameof(TerminarInstalacion.Sesion)] = dueno });
            await lista.EsperarAsync(t => t.Contains("Revísalos: faltan Papel Carta normal"), "los costos que faltan");
            var texto = await lista.TextoAsync();
            Assert.Contains("Imprime uno de prueba: si sale completo y se lee bien, está lista.", texto);
            Assert.Contains("Para conectar otra caja, corre el instalador en esa PC y elige «Otra caja»", texto);
            // Sin eleventa ni Control Print configurados, no salen.
            Assert.DoesNotContain("Control Print", texto);
            Assert.DoesNotContain("eleventa", texto);

            // Con la impresora por probar, el cursor no se va al lector; ya que imprimió, sí: es lo que sigue.
            Assert.DoesNotContain(pagina.Llamadas, l => l.Funcion == "enfocar" && l.Argumentos?[0] as string == "#inst-lector");
            await lista.OprimirAsync(await lista.BotonAsync("Imprimir uno de prueba"));
            await lista.EsperarAsync(t => t.Contains("Salió el ticket de prueba"), "el ticket de prueba");
            Assert.Equal(1, impresora.Impresos);
            await VentaConCopia.EsperarAsync(() => UltimoFoco(pagina) == "#inst-lector", "el cursor en el cuadro del lector");
            await lista.EscribirAsync("inst-lector", "7501234567890");
            await lista.TeclaAsync("inst-lector", "Enter");
            await lista.EsperarAsync(t => t.Contains("Leyó «7501234567890»"), "lo que leyó el lector");
            Assert.Empty(lista.Errores);
        }

        // En una caja instalada antes de esto (la caja 2 del piloto) no sale en la Configuración: empieza al dar de alta la caja o al
        // crear la tienda.
        await using (var deAntes = new PantallaSinNavegador(Servicios(conexion, servicio.Fabrica.CreateClient())))
        {
            await deAntes.AbrirAsync<Configuracion>(new() { [nameof(Configuracion.Caja)] = caja, [nameof(Configuracion.Sesion)] = dueno });
            await deAntes.EsperarAsync(t => t.Contains("Acerca de") && !t.Contains("Cargando…"), "la Configuración");
            Assert.DoesNotContain((await deAntes.LeerAsync()).Elementos, e => e.Id == "cfg-instalacion");
        }
        await instalacion.EmpezarAsync();
        Assert.True(instalacion.Pendiente);

        await using var configuracion = new PantallaSinNavegador(servicios);
        await configuracion.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = dueno,
            [nameof(Configuracion.GrupoInicial)] = Configuracion.GrupoInstalacion,
        });
        await configuracion.EsperarAsync(t => t.Contains("Ya terminé"), "Terminar la instalación");
        Assert.NotNull(await configuracion.PorIdAsync("cfg-instalacion"));

        await configuracion.OprimirAsync(await configuracion.BotonAsync("Ya terminé"));

        Assert.True(instalacion.Terminada);
        await configuracion.EsperarAsync(t => !t.Contains("Ya terminé"), "que se quite");
        Assert.DoesNotContain((await configuracion.LeerAsync()).Elementos, e => e.Id == "cfg-instalacion");
        Assert.Empty(configuracion.Errores);
    }

    /// <summary>«Imprimir uno de prueba» con doble clic (o Enter repetido): sale un ticket de prueba, no dos (revisión independiente).</summary>
    [Fact]
    public async Task Imprimir_uno_de_prueba_con_doble_clic_imprime_uno()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var http = propio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var impresora = new ImpresoraQueImprime();
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient(), impresora);
        var dueno = await servicios.GetRequiredService<ClienteCaja>().EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios) { AcuseDelDibujo = TimeSpan.FromMilliseconds(150) };
        await p.AbrirAsync<TerminarInstalacion>(new() { [nameof(TerminarInstalacion.Caja)] = caja, [nameof(TerminarInstalacion.Sesion)] = dueno });
        var boton = await p.BotonAsync("Imprimir uno de prueba");

        await Task.WhenAll(p.OprimirAsync(boton), p.OprimirAsync(boton));

        await p.EsperarAsync(t => t.Contains("Salió el ticket de prueba"), "el ticket de prueba");
        await Task.Delay(300);
        Assert.Equal(1, impresora.Impresos);
    }

    [Fact]
    public async Task Los_costos_de_las_impresiones_se_cambian_en_la_configuracion_y_quien_no_los_ve_no_los_ve()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = Servicios(conexion, servicio.Fabrica.CreateClient());
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        var cajero = await cliente.EntrarAsync("cajero", "1111");

        await using (var p = new PantallaSinNavegador(servicios))
        {
            await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });
            var precio = await p.EsperarElementoAsync("costo-cartucho-1");
            Assert.Equal("1,200.00", precio.Valor);
            // Al salir del renglón, se guarda.
            await p.EscribirAsync("costo-cartucho-1", "1,150");
            await p.SalirAsync("costo-cartucho-1");
            await VentaConCopia.EsperarAsync(() => p.PorIdAsync("costo-cartucho-1").Result.Valor == "1,150.00", "lo guardado");

            // El precio y lo que rinde del mismo cartucho, de un campo al otro: se guardan juntos, una sola vez (revisión del 2026-10-31).
            int historialAntes, bitacoraAntes;
            await using (var db = servicio.Base())
                (historialAntes, bitacoraAntes) = (await db.Set<HistorialCostosImpresion>().CountAsync(),
                    await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.ConfigurarImpresora));
            await p.DispararAsync(await p.PorIdAsync("costo-cartucho-2"), "onfocus", new FocusEventArgs { Type = "focus" });
            await p.EscribirAsync("costo-cartucho-2", "1,100");
            await p.DispararAsync(await p.PorIdAsync("costo-cartucho-2"), "onblur", new FocusEventArgs { Type = "blur" });
            await p.DispararAsync(await p.PorIdAsync("costo-rinde-2"), "onfocus", new FocusEventArgs { Type = "focus" });
            await p.EscribirAsync("costo-rinde-2", "6,500");
            await Task.Delay(400);
            Assert.Equal(1200m, (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos.Cartuchos[2].Precio);
            await p.SalirAsync("costo-rinde-2");
            var limite = DateTime.UtcNow.AddSeconds(20);
            CartuchoCostoDto magenta;
            while ((magenta = (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos.Cartuchos[2]).Rendimiento != 6500
                && DateTime.UtcNow < limite)
                await Task.Delay(20);
            Assert.Equal(new CartuchoCostoDto("Magenta", 1100, 6500), magenta);
            await using (var db = servicio.Base())
            {
                Assert.Equal(historialAntes + 1, await db.Set<HistorialCostosImpresion>().CountAsync());
                var bitacora = await db.Bitacora.Where(b => b.Accion == AccionControlada.ConfigurarImpresora).OrderBy(b => b.FechaUtc).Select(b => b.Detalle).ToListAsync();
                Assert.Equal(bitacoraAntes + 1, bitacora.Count);
                Assert.Equal("Cambió los costos de las impresiones: el precio del cartucho magenta y lo que rinde el cartucho magenta", bitacora[^1]);
            }

            // Sin salir del renglón: tras la pausa se guarda, aunque el cursor se quede en el otro campo sin escribir nada.
            await p.DispararAsync(await p.PorIdAsync("costo-cartucho-3"), "onfocus", new FocusEventArgs { Type = "focus" });
            await p.EscribirAsync("costo-cartucho-3", "1,050");
            await p.DispararAsync(await p.PorIdAsync("costo-cartucho-3"), "onblur", new FocusEventArgs { Type = "blur" });
            await p.DispararAsync(await p.PorIdAsync("costo-rinde-3"), "onfocus", new FocusEventArgs { Type = "focus" });
            limite = DateTime.UtcNow.AddSeconds(20);
            CartuchoCostoDto amarillo;
            while ((amarillo = (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos.Cartuchos[3]).Precio != 1050
                && DateTime.UtcNow < limite)
                await Task.Delay(50);
            Assert.Equal(new CartuchoCostoDto("Amarillo", 1050, 7000), amarillo);
            await p.DispararAsync(await p.PorIdAsync("costo-rinde-3"), "onblur", new FocusEventArgs { Type = "blur" });

            // Lo que no sirve se dice, se queda escrito para corregirlo y no se guarda.
            await p.EscribirAsync("costo-rinde-1", "0");
            await p.SalirAsync("costo-rinde-1");
            await p.EsperarAsync(t => t.Contains("Lo que rinde el cartucho cian va de 1 a 1,000,000 páginas (lo dice su caja)."), "por qué no se guardó");
            Assert.Equal("0", (await p.PorIdAsync("costo-rinde-1")).Valor);
            // El margen del papel: vacío, el mismo que lo demás.
            Assert.Equal("", (await p.PorIdAsync("costo-margen-papel")).Valor);
            Assert.Empty(p.Errores);
        }
        var guardados = (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!;
        Assert.Equal(new CartuchoCostoDto("Cian", 1150, 7000), guardados.Costos.Cartuchos[1]);

        await using (var p = new PantallaSinNavegador(servicios))
        {
            await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = cajero });
            await p.EsperarAsync(t => !t.Contains("Cargando"), "la impresora");
            Assert.DoesNotContain("Costos de las impresiones", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }
    }
}
