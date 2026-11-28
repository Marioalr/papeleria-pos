using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// «Mandar los conteos a eleventa» en Configuración › Inventario (dueño, 2026-11-27; docs/propuestas/conteo-del-celular-a-eleventa.md,
/// entrega 3): solo se ve en el piloto con eleventa y con «Configuración de la tienda»; llega apagado; se prende y se apaga solo con el
/// teclado, se guarda al tocarlo y queda en la bitácora. Si el puente no escribe, prendido lo dice.
/// </summary>
public sealed class MandarConteosEnConfiguracionTests
{
    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<PantallaSinNavegador> ConfiguracionAsync(ServicioDePrueba servicio, CajaDto caja, SesionDto sesion, PaginaQueContesta pagina)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection();
        servicios.AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .AddSingleton<NavigationManager>(new NavegacionDePrueba());
        var pantalla = new PantallaSinNavegador(servicios.BuildServiceProvider());
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        return pantalla;
    }

    private static async Task<MandarConteosDto> ComoEstaAsync(HttpClient http, CajaDto caja) =>
        (await http.GetFromJsonAsync<MandarConteosDto>($"api/cajas/{caja.Id}/configuracion/mandar-conteos"))!;

    [Fact]
    public async Task En_el_piloto_el_interruptor_llega_apagado_y_se_prende_y_se_apaga_solo_con_el_teclado()
    {
        // El piloto con la lectura prendida y el puente sin escribir.
        await using var servicio = await ServicioDePrueba.CrearAsync(("Eleventa:Activo", "true"));
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var pagina = new PaginaQueContesta();
        await using var p = await ConfiguracionAsync(servicio, caja, dueno, pagina);
        await p.OprimirAsync(await p.PorIdAsync("cfg-inventario"));

        var renglon = await p.ElementoAsync(e => e.Clase?.Trim() == "vn-ajuste"
            && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Mandar los conteos a eleventa", StringComparison.Ordinal), "el renglón del interruptor");
        Assert.Contains("como un ajuste de inventario", renglon.Texto);
        Assert.Contains("Apagado: no se le escribe nada", renglon.Texto);
        foreach (var (opcion, elegida) in new[] { ("apagado", "true"), ("prendido", "false") })
        {
            var boton = await p.PorIdAsync($"aj-mandar-conteos-{opcion}");
            Assert.Equal("button", boton.Etiqueta);
            Assert.Equal("radio", boton.Atributos.GetValueOrDefault("role"));
            Assert.False(boton.Desactivado, $"«{opcion}» desactivado");
            Assert.Null(boton.Atributos.GetValueOrDefault("tabindex"));
            Assert.True(boton.Eventos.ContainsKey("onkeydown"), $"«{opcion}» sin flechas");
            Assert.Equal(elegida, boton.Atributos.GetValueOrDefault("aria-checked"));
        }
        Assert.Equal(new MandarConteosDto(EnElPiloto: true, Mandar: false, PuenteEscribe: false), await ComoEstaAsync(http, caja));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "aj-mandar-conteos-sin-puente");

        // → en «Apagado»: «Prendido», guardado, con el cursor en él. Y como el puente no escribe, lo dice.
        await p.TeclaAsync("aj-mandar-conteos-apagado", "ArrowRight");
        Assert.True((await ComoEstaAsync(http, caja)).Mandar);
        Assert.Equal("true", (await p.PorIdAsync("aj-mandar-conteos-prendido")).Atributos.GetValueOrDefault("aria-checked"));
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && l.Argumentos?[0] as string == "#aj-mandar-conteos-prendido");
        Assert.Contains("El puente no le está escribiendo a eleventa", (await p.PorIdAsync("aj-mandar-conteos-sin-puente")).Texto);
        // ← regresa; Fin e Inicio, a las orillas; Tab no elige; Enter o Espacio en el botón son su clic.
        await p.TeclaAsync("aj-mandar-conteos-prendido", "ArrowLeft");
        Assert.False((await ComoEstaAsync(http, caja)).Mandar);
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && l.Argumentos?[0] as string == "#aj-mandar-conteos-apagado");
        await p.TeclaAsync("aj-mandar-conteos-apagado", "End");
        Assert.True((await ComoEstaAsync(http, caja)).Mandar);
        await p.TeclaAsync("aj-mandar-conteos-prendido", "Home");
        Assert.False((await ComoEstaAsync(http, caja)).Mandar);
        await p.TeclaAsync("aj-mandar-conteos-apagado", "Tab");
        Assert.False((await ComoEstaAsync(http, caja)).Mandar);
        await p.OprimirAsync(await p.PorIdAsync("aj-mandar-conteos-prendido"));
        Assert.True((await ComoEstaAsync(http, caja)).Mandar);

        // El buscador de ajustes lo encuentra.
        await p.EscribirAsync("cfg-busca", "conteos");
        Assert.Contains("Mandar los conteos a eleventa", await p.TextoAsync());

        await using var db = servicio.Base();
        var cambios = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTienda && b.UsuarioId == dueno.UsuarioId).ToListAsync();
        Assert.Equal(3, cambios.Count(b => b.Detalle.StartsWith("Prendió «Mandar los conteos a eleventa»", StringComparison.Ordinal)));
        Assert.Equal(2, cambios.Count(b => b.Detalle.StartsWith("Apagó «Mandar los conteos a eleventa»", StringComparison.Ordinal)));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Fuera_del_piloto_o_sin_el_permiso_el_interruptor_no_aparece_ni_se_puede_prender()
    {
        // Sin eleventa: ni el dueño lo ve, y la ruta no lo prende.
        await using (var servicio = await ServicioDePrueba.CrearAsync())
        {
            var http = servicio.Fabrica.CreateClient();
            var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
            var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
            await using var p = await ConfiguracionAsync(servicio, caja, dueno, new PaginaQueContesta());
            await p.OprimirAsync(await p.PorIdAsync("cfg-inventario"));
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id?.StartsWith("aj-mandar-conteos", StringComparison.Ordinal) == true);
            Assert.DoesNotContain("Mandar los conteos a eleventa", await p.TextoAsync());
            Assert.False((await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/mandar-conteos", new SolicitudMandarConteos(dueno.UsuarioId, true))).IsSuccessStatusCode);
            Assert.Equal(new MandarConteosDto(EnElPiloto: false, Mandar: false, PuenteEscribe: false), await ComoEstaAsync(http, caja));
        }
        // En el piloto, sin «Configuración de la tienda» (el encargado): no lo ve.
        await using (var servicio = await ServicioDePrueba.CrearAsync(("Eleventa:Activo", "true")))
        {
            var http = servicio.Fabrica.CreateClient();
            var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
            var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
            await using var p = await ConfiguracionAsync(servicio, caja, encargado, new PaginaQueContesta());
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id?.StartsWith("aj-mandar-conteos", StringComparison.Ordinal) == true);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden,
                (await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/mandar-conteos", new SolicitudMandarConteos(encargado.UsuarioId, true))).StatusCode);
            Assert.False((await ComoEstaAsync(http, caja)).Mandar);
        }
    }
}
