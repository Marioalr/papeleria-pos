using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las curvas del margen en Configuración › Impresoras e impresiones (2026-11-08), sin navegador y contra el servicio de verdad: se ven
/// (con su gráfica y el probador) antes de elegirlas, cada punto se escribe con sus cantidades exactas y se guarda con Enter como los demás
/// renglones, se agregan y se quitan puntos, y al elegir «Por curvas» se cobra con ellas.
/// </summary>
public sealed class CurvasDelMargenEnLaCajaTests
{
    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    private static ServiceProvider Servicios(ConexionCaja conexion, HttpClient http) =>
        new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(http, conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .AddSingleton<NavigationManager>(new NavegacionDePrueba())
            .BuildServiceProvider();

    private static async Task<T> HastaQue<T>(Func<Task<T>> leer, Func<T, bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        T valor;
        while (!condicion(valor = await leer()) && DateTime.UtcNow < limite)
            await Task.Delay(50);
        return valor;
    }

    [Fact]
    public async Task Las_curvas_se_escriben_punto_por_punto_se_prueban_y_al_elegirlas_se_cobra_con_ellas()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var caja = (await propio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient());
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        async Task<CostosImpresionDto> Costos() => (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });

        // Antes de elegirlas ya se ven, con las sugeridas, su gráfica y el probador, y dicen que todavía no se cobra con ellas.
        await p.EsperarElementoAsync("curva-tinta-x-0");
        await p.EsperarAsync(t => t.Contains("Todavía no se cobra con ellas") && t.Contains("Margen de cada cara"), "las curvas y el probador");
        Assert.Equal("5", (await p.PorIdAsync("curva-tinta-x-0")).Valor);
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "polyline");

        // Un punto con sus cantidades exactas, con Enter.
        await p.DispararAsync(await p.PorIdAsync("curva-tinta-y-0"), "onfocus", new FocusEventArgs { Type = "focus" });
        await p.EscribirAsync("curva-tinta-y-0", "300");
        await p.TeclaAsync("curva-tinta-y-0", "Enter");
        var costos = await HastaQue(Costos, c => c.Curvas!.PorTinta[0].Y == 300);
        Assert.Equal(new PuntoCurvaDto(5, 300), costos.Curvas!.PorTinta[0]);
        Assert.Equal(MargenDeImpresiones.PorCostoDelTrabajo, costos.Margen);

        // Agregar un punto (al final, al doble) y quitar el primero de la curva por caras.
        var puntos = costos.Curvas.PorCaras.Count;
        await p.OprimirAsync(await p.PorIdAsync("curva-caras-agregar"));
        costos = await HastaQue(Costos, c => c.Curvas!.PorCaras.Count == puntos + 1);
        Assert.Equal(costos.Curvas!.PorCaras[^2].X * 2, costos.Curvas.PorCaras[^1].X);
        await p.OprimirAsync(await p.PorIdAsync("curva-caras-quitar-0"));
        costos = await HastaQue(Costos, c => c.Curvas!.PorCaras.Count == puntos);
        Assert.NotEqual(1, costos.Curvas!.PorCaras[0].X);

        // Una curva con un punto que no sirve se queda escrita, con su porqué.
        await p.DispararAsync(await p.PorIdAsync("curva-tinta-x-1"), "onfocus", new FocusEventArgs { Type = "focus" });
        await p.EscribirAsync("curva-tinta-x-1", "500");
        await p.TeclaAsync("curva-tinta-x-1", "Enter");
        await p.EsperarAsync(t => t.Contains("La tinta de un punto de la curva por tinta va de más de 0 a 400 %"), "el porqué del punto");
        await p.EscribirAsync("curva-tinta-x-1", "25");
        await p.TeclaAsync("curva-tinta-x-1", "Enter");
        await HastaQue(Costos, c => c.Curvas!.PorTinta.Any(x => x.X == 25));

        // Elegir «Por curvas».
        await p.OprimirAsync(await p.PorIdAsync($"costo-margen-{(int)MargenDeImpresiones.PorCurvas}"));
        costos = await HastaQue(Costos, c => c.Margen == MargenDeImpresiones.PorCurvas);
        Assert.Equal(MargenDeImpresiones.PorCurvas, costos.Margen);
        await p.EsperarAsync(t => t.Contains("Así se cobra: cada cara lleva el margen de su tinta"), "que ya se cobra con ellas");
        Assert.Empty(p.Errores);
    }
}
