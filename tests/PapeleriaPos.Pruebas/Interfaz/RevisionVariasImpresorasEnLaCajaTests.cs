using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Revisión independiente de cb5f83f: Configuración › Impresoras e impresiones (arreglado el 2026-11-08).</summary>
public sealed class RevisionVariasImpresorasEnLaCajaTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private ServiceProvider Servicios(CajaDto caja) => CajaConVariasImpresoras.Servicios(servicio, caja);

    /// <summary>
    /// La decisión: «darla de alta otra vez con su IP ocupada tampoco se deja». El servicio lo rechaza, DarDeAltaAsync lo guarda en ErrorDe(impresora-baja-2),
    /// pero eso solo se pinta en el bloque de las activas (ConfiguracionImpresora.razor:1059): en «Dadas de baja» (62-79) no sale nada: el botón no hace nada.
    /// </summary>
    [Fact]
    public async Task Si_no_se_puede_volver_a_dar_de_alta_lo_dice()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-1", 10, 5))));
        var baja = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "Baja", PerfilImpresora.WF6590, "ip-ocupada")));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PutAsJsonAsync($"{ruta}/{baja.Numero}", new SolicitudCambioImpresora(dueno.UsuarioId, Activa: false)));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "Nueva", PerfilImpresora.WF6590, "ip-ocupada")));

        await using var servicios = Servicios(caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });
        await p.EsperarAsync(t => t.Contains("Dadas de baja"), "la lista de las dadas de baja");

        await p.OprimirAsync(await p.BotonAsync("Volver a darla de alta"));

        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains("ya es de la impresora"), "por qué no se pudo", TimeSpan.FromSeconds(5));
    }

    /// <summary>Teclado primero: en lo demás de la Configuración Enter guarda el renglón; aquí no pasa nada y hay que ir con Tab hasta el botón.</summary>
    [Fact]
    public async Task Enter_en_los_campos_de_agregar_una_impresora_la_agrega()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-1-enter", 10, 5))));
        var antes = (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!.Count;

        await using var servicios = Servicios(caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });
        await p.EsperarElementoAsync("impresora-nueva-nombre");
        await p.EscribirAsync("impresora-nueva-nombre", $"Con Enter {Guid.NewGuid():N}"[..20]);

        var host = await p.PorIdAsync("impresora-nueva-host");
        Assert.True(host.Eventos.ContainsKey("onkeydown"), "«IP de la impresora nueva» no atiende el teclado: Enter no hace nada.");
        Assert.True(antes > 0);

        // Enter en la IP (vacía: no se lee) la agrega, y lo dice junto a «Agregar», no hasta abajo de la página.
        await p.TeclaAsync("impresora-nueva-host", "Enter");
        await CajaConVariasImpresoras.EsperarAsync(async () => (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!.Count == antes + 1, "la nueva");
        await p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains($"Agregada como la impresora {antes + 1}"), "lo que dice al agregarla");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Con dos o más, un índice arriba, y «Terminar la instalación» sigue llevando a «Cómo se lee» (#como-se-lee, el de la primera). La
    /// L5590: su ayuda del aviso no habla de la caja de mantenimiento que no tiene, y en sus costos se le puede poner una (la ficha de Epson
    /// lista la C12C934461) con su precio y lo que dura.
    /// </summary>
    [Fact]
    public async Task Con_varias_hay_indice_el_ancla_sigue_y_a_la_L5590_se_le_pone_su_caja_de_mantenimiento()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-1-indice", 10, 5))));
        var l5590 = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
            new SolicitudNuevaImpresora(dueno.UsuarioId, $"L5590 {Guid.NewGuid():N}"[..14], PerfilImpresora.L5590, null)));

        await using var servicios = Servicios(caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });
        var precio = $"costo-impresora-caja-{l5590.Numero}";
        await p.EsperarElementoAsync(precio);

        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Costos", texto);
        Assert.Equal("Cómo se lee", PantallaSinNavegador.Limpio((await p.PorIdAsync("como-se-lee")).Texto));
        Assert.Contains("Con la tinta en este porcentaje o menos", texto);
        Assert.Contains("Con la tinta o la caja de mantenimiento en este porcentaje o menos", texto);

        await p.EscribirAsync(precio, "189");
        await p.EscribirAsync($"costo-impresora-caja-caras-{l5590.Numero}", "50000");
        await p.TeclaAsync($"costo-impresora-caja-caras-{l5590.Numero}", "Enter");
        await CajaConVariasImpresoras.EsperarAsync(async () =>
            (await cliente.CostosImpresionAsync(caja.SucursalId, sesion.UsuarioId))!.Impresoras!.Single(i => i.Numero == l5590.Numero)
                is { CajaMantenimiento: 189, PaginasPorCaja: 50000 }, "su caja de mantenimiento");
        Assert.Empty(p.Errores);
    }
}

/// <summary>El editor de costos de #151 con la tabla de las botellas: un renglón, un guardado; lo que no sirve se queda marcado con su porqué.</summary>
public sealed class RevisionBotellasEnElEditorTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Una_botella_se_guarda_por_renglon_con_un_solo_historial_y_lo_que_no_sirve_se_queda_marcado()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-1-botellas", 10, 5))));
        var l5590 = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590", PerfilImpresora.L5590, null)));

        await using var servicios = CajaConVariasImpresoras.Servicios(servicio, caja);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });
        var precio = $"costo-impresora-{l5590.Numero}-0";
        var rinde = $"costo-impresora-rinde-{l5590.Numero}-0";
        await p.EsperarElementoAsync(precio);
        int Historial()
        {
            using var db = servicio.Base();
            return db.Set<HistorialCostosImpresion>().Count(h => h.ImpresoraId != null);
        }
        var antes = Historial();

        await p.EscribirAsync(precio, "259");
        await p.EscribirAsync(rinde, "4500");
        await p.TeclaAsync(rinde, "Enter");
        await CajaConVariasImpresoras.EsperarAsync(async () => (await cliente.CostosImpresionAsync(caja.SucursalId, sesion.UsuarioId))!.Impresoras!.Single().Tintas[0] is { Precio: 259, Rendimiento: 4500 },
            "el renglón guardado");
        Assert.Equal(antes + 1, Historial());

        await p.EscribirAsync(precio, "abc");
        await p.TeclaAsync(precio, "Enter");
        await p.EsperarAsync(t => t.Contains("El precio de la tinta negro de la L5590", StringComparison.Ordinal), "su porqué", TimeSpan.FromSeconds(5));
        Assert.Equal("abc", (await p.PorIdAsync(precio)).Valor);
        Assert.Equal(antes + 1, Historial());
        Assert.Empty(p.Errores);
    }
}

/// <summary>
/// Una caja nueva contra un servicio de antes: su ruta /impresoras no existe (404) y todo va por /impresora. Comprobación de lo que dice el código
/// (ClienteCaja.ImpresorasAsync, noEncontradoEsNulo; ConfiguracionImpresora._deAntes).
/// </summary>
public sealed class RevisionCajaNuevaContraServicioDeAntesTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    private sealed class SinLasRutasNuevas(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.Contains("/impresoras", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task La_configuracion_enseña_la_de_siempre_sin_agregar_ni_errores()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-de-antes", 10, 5))));
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var cliente = new HttpClient(new SinLasRutasNuevas(servicio.Fabrica.Server.CreateHandler())) { BaseAddress = new Uri("http://localhost/") };
        await using var servicios = new ServiceCollection().AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(cliente, conexion)).AddSingleton<IJSRuntime>(new PaginaQueContesta()).BuildServiceProvider();
        var sesion = await servicios.GetRequiredService<ClienteCaja>().EntrarAsync("dueno", "3333");
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = sesion });

        Assert.Equal("ip-de-antes", (await p.EsperarElementoAsync("impresora-host")).Valor);
        Assert.DoesNotContain("Agregar una impresora", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Empty(p.Errores);
    }
}
