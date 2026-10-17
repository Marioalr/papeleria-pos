using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El chip de la temporada (entrega 13) con un reloj de prueba y las temporadas que deja la migración (las del dueño): sale
/// mientras corre, uno por cada una (el 16 de septiembre, dos), abre Ctrl+M en la suya y se oculta solo al terminar.
/// </summary>
public sealed class ChipsDeTemporadaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    /// <summary>Mediodía de ese día en la tienda.</summary>
    private static DateTimeOffset EnLaTienda(int anio, int mes, int dia) => new(anio, mes, dia, 12, 0, 0, TimeSpan.FromHours(-6));

    private static async Task<List<ElementoDibujado>> ChipsAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Clase?.Contains("vn-chip-temporada", StringComparison.Ordinal) == true).ToList();

    [Fact]
    public async Task Sale_mientras_corre_y_se_oculta_sola_al_terminar()
    {
        var reloj = new RelojFijo(EnLaTienda(2026, 11, 5));
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<TimeProvider>(reloj)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        Guid? abierta = null;
        var alAbrir = new EventCallback<Guid>(null, (Action<Guid>)(id => abierta = id));
        var chips = await pantalla.AbrirAsync<ChipsDeTemporada>(new() { [nameof(ChipsDeTemporada.AlAbrir)] = alAbrir });
        // Lo que hace la venta al volver a pintarse (cada escaneo, o el minuto que revisa el chip).
        Task Pintar(bool visible = true) => pantalla.Dispatcher.InvokeAsync(() => chips.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(ChipsDeTemporada.AlAbrir)] = alAbrir,
            [nameof(ChipsDeTemporada.Visible)] = visible,
        })));
        var todosSantos = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<MostradorDto>("api/mostrador"))!.Exhibidores.Single(e => e.Nombre == "Todos Santos");

        // El 5 de noviembre todavía corre Todos Santos (su último día): su chip abre Ctrl+M en ella.
        await pantalla.EsperarAsync(t => t.Contains("Todos Santos", StringComparison.Ordinal), "el chip");
        var deHoy = await ChipsAsync(pantalla);
        Assert.Equal(["Todos Santos Ctrl+M"], deHoy.Select(c => PantallaSinNavegador.Limpio(c.Texto)));
        await pantalla.OprimirAsync(deHoy[0]);
        Assert.Equal(todosSantos.Id, abierta);

        // Al día siguiente se oculta sola.
        reloj.Ahora = EnLaTienda(2026, 11, 6);
        await Pintar();
        Assert.Empty(await ChipsAsync(pantalla));

        // El 16 de septiembre corren dos; en Año Nuevo, Navidad (cruza el fin de año); en las páginas, ninguno.
        reloj.Ahora = EnLaTienda(2027, 9, 16);
        await Pintar();
        Assert.Equal(["15 de septiembre Ctrl+M", "Todos Santos Ctrl+M"], (await ChipsAsync(pantalla)).Select(c => PantallaSinNavegador.Limpio(c.Texto)));
        reloj.Ahora = EnLaTienda(2027, 1, 1);
        await Pintar();
        Assert.Equal(["Navidad Ctrl+M"], (await ChipsAsync(pantalla)).Select(c => PantallaSinNavegador.Limpio(c.Texto)));
        await Pintar(visible: false);
        Assert.Empty(await ChipsAsync(pantalla));
        Assert.Empty(pantalla.Errores);
    }
}
