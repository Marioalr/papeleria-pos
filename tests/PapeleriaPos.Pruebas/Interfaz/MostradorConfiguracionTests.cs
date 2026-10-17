using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Configuración › Mostrador y temporadas (entrega 13), sin navegador, contra el servicio de prueba: quien edita el catálogo
/// da de alta una temporada, le escribe sus fechas como las diría («15/7 al 31/8»), le agrega lugares y los renombra; todo se
/// guarda solo, sin «Guardar».
/// </summary>
public sealed class MostradorConfiguracionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private async Task<(PantallaSinNavegador Pantalla, HttpClient Http)> AbrirAsync(string usuario, string pin)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        return (pantalla, http);
    }

    private static async Task<MostradorDto> MostradorAsync(HttpClient http) => (await http.GetFromJsonAsync<MostradorDto>("api/mostrador"))!;

    [Fact]
    public async Task Quien_edita_el_catalogo_da_de_alta_una_temporada_con_sus_fechas_y_sus_lugares_y_se_guarda_sola()
    {
        var (p, http) = await AbrirAsync("encargado", "2222");
        await using var abierta = p;
        var antes = await MostradorAsync(http);
        var todosSantos = antes.Exhibidores.Single(e => e.Nombre == "Todos Santos");
        await p.OprimirAsync(await p.ElementoAsync(e => e.Id == "cfg-mostrador", "el grupo"));
        // Los nombres van en sus campos: se espera a que estén.
        await p.EsperarElementoAsync($"mo-fechas-{todosSantos.Id}");
        await p.EsperarAsync(t => !t.Contains("Cargando…"), "los exhibidores y las temporadas");
        Assert.Equal("Todos Santos", (await p.PorIdAsync($"mo-nombre-{todosSantos.Id}")).Valor);
        var texto = await p.TextoAsync();
        // Las de la migración, con sus fechas como se dicen, y el mostrador con sus repisas.
        Assert.Equal("del 16 de septiembre al 5 de noviembre", (await p.PorIdAsync($"mo-fechas-{todosSantos.Id}")).Valor);
        Assert.Equal("", (await p.PorIdAsync($"mo-fechas-{antes.Exhibidores.Single(e => e.Nombre == "Primavera").Id}")).Valor);
        Assert.Contains("Sin fechas", texto);
        Assert.Equal("Repisa de arriba", (await p.PorIdAsync($"mo-lugar-{antes.Exhibidores[0].Lugares[0].Id}")).Valor);

        // Una nueva, con Enter.
        await p.EscribirAsync("mo-nuevo-Temporada", "Regreso a clases");
        await p.TeclaAsync("mo-nuevo-Temporada", "Enter");
        var regreso = (await MostradorAsync(http)).Exhibidores.Single(e => e.Nombre == "Regreso a clases");
        Assert.Equal(TipoExhibidor.Temporada, regreso.Tipo);
        Assert.Equal("", (await p.PorIdAsync("mo-nuevo-Temporada")).Valor);

        // Sus fechas, como se dicen; una que no existe dice por qué y no se guarda.
        await p.EscribirAsync($"mo-fechas-{regreso.Id}", "31/4 al 5/5");
        await p.SalirAsync($"mo-fechas-{regreso.Id}");
        Assert.Contains("El 31/4 no existe", await p.TextoAsync());
        await p.EscribirAsync($"mo-fechas-{regreso.Id}", "15/7 al 31/8");
        await p.SalirAsync($"mo-fechas-{regreso.Id}");
        regreso = (await MostradorAsync(http)).Exhibidores.Single(e => e.Id == regreso.Id);
        Assert.Equal((new DiaDelAnio(7, 15), new DiaDelAnio(8, 31)), (regreso.Desde, regreso.Hasta));
        Assert.Equal("del 15 de julio al 31 de agosto", (await p.PorIdAsync($"mo-fechas-{regreso.Id}")).Valor);

        // Un lugar (con Enter) y su nombre nuevo.
        await p.EscribirAsync($"mo-lugar-nuevo-{regreso.Id}", "Mesa de la entrada");
        await p.TeclaAsync($"mo-lugar-nuevo-{regreso.Id}", "Enter");
        var mesa = Assert.Single((await MostradorAsync(http)).Exhibidores.Single(e => e.Id == regreso.Id).Lugares);
        await p.EscribirAsync($"mo-lugar-{mesa.Id}", "El aparador");
        await p.SalirAsync($"mo-lugar-{mesa.Id}");
        Assert.Equal("El aparador", Assert.Single((await MostradorAsync(http)).Exhibidores.Single(e => e.Id == regreso.Id).Lugares).Nombre);
        Assert.Contains("Guardado a las", await p.TextoAsync());

        // Al buscar «temporada» se encuentra, con su grupo.
        await p.EscribirAsync("cfg-busca", "temporada");
        Assert.Contains("Mostrador y temporadas · Lo que se vende unas semanas al año", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }
}
