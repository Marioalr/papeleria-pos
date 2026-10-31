using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La instalación desde cero en la caja (2026-10-31), sin navegador y contra el servicio de verdad: en la PC del servicio, con una base
/// nueva, la caja crea la tienda en tres pasos y el dueño queda adentro; en otra PC dice que se termine en la caja principal. Después,
/// «Terminar la instalación» dice lo que falta y los costos de las impresiones se cambian en la Configuración.
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

    private static ServiceProvider Servicios(ConexionCaja conexion, HttpClient http, IImpresora? impresora = null) =>
        new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(http, conexion))
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .BuildServiceProvider();

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

    [Fact]
    public async Task Terminar_la_instalacion_dice_lo_que_falta_y_al_terminarla_sale_de_la_configuracion()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var impresora = new ImpresoraQueImprime();
        await using var servicios = Servicios(conexion, servicio.Fabrica.CreateClient(), impresora);
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

            await lista.OprimirAsync(await lista.BotonAsync("Imprimir uno de prueba"));
            await lista.EsperarAsync(t => t.Contains("Salió el ticket de prueba"), "el ticket de prueba");
            Assert.Equal(1, impresora.Impresos);
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
            await p.EscribirAsync("costo-cartucho-1", "1,150");
            await p.SalirAsync("costo-cartucho-1");
            await p.EsperarAsync(t => !t.Contains("Cargando"), "lo guardado");
            Assert.Equal("1,150.00", (await p.PorIdAsync("costo-cartucho-1")).Valor);
            // Lo que no sirve se dice y no se guarda.
            await p.EscribirAsync("costo-rinde-1", "0");
            await p.SalirAsync("costo-rinde-1");
            Assert.Contains("Lo que rinde el cartucho cian va de 1 a 1,000,000 páginas (lo dice su caja).", await p.TextoAsync());
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
