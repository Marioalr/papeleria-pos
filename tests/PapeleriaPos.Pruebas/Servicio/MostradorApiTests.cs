using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que se señala (entrega 13) por la API: lo leen todas las cajas, lo cambia quien tiene «Productos, precios y etiquetas»
/// (el cajero de prueba no; el encargado y el dueño, sí), y la copia de cada caja lo trae siempre (el formato 3).
/// </summary>
public sealed class MostradorApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja)> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        return (http, (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First());
    }

    private static async Task<SesionDto> EntrarAsync(HttpClient http, string usuario, string pin) =>
        await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));

    [Fact]
    public async Task Lo_cambia_quien_tiene_productos_precios_y_etiquetas_y_lo_lee_cualquier_caja()
    {
        var (http, caja) = await CajaAsync();
        var cajero = await EntrarAsync(http, "cajero", "1111");
        var encargado = await EntrarAsync(http, "encargado", "2222");
        var leido = await Leer<MostradorDto>(await http.GetAsync("api/mostrador"));
        var principal = leido.Exhibidores.First(e => e.Tipo == TipoExhibidor.Exhibidor);

        var delCajero = new[]
        {
            await http.PostAsJsonAsync("api/mostrador", new SolicitudNuevoExhibidor(cajero.UsuarioId, caja.SucursalId, "La pared", TipoExhibidor.Exhibidor)),
            await http.PutAsJsonAsync($"api/mostrador/{principal.Id}", new SolicitudCambiarExhibidor(cajero.UsuarioId, caja.SucursalId, "Vitrina")),
            await http.PostAsJsonAsync($"api/mostrador/{principal.Id}/lugares", new SolicitudLugar(cajero.UsuarioId, caja.SucursalId, "Gancho")),
            await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(cajero.UsuarioId, caja.SucursalId, [new LugarConProductosDto(principal.Lugares[0].Id, [])])),
        };
        var pared = await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador",
            new SolicitudNuevoExhibidor(encargado.UsuarioId, caja.SucursalId, "La pared", TipoExhibidor.Exhibidor)));
        var sinFechas = await http.PutAsJsonAsync($"api/mostrador/{pared.Exhibidores.Single(e => e.Nombre == "La pared").Id}",
            new SolicitudCambiarExhibidor(encargado.UsuarioId, caja.SucursalId, Fechas: new FechasTemporadaDto(new DiaDelAnio(1, 1), new DiaDelAnio(2, 1))));

        Assert.All(delCajero, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Contains("Productos, precios y etiquetas", await delCajero[0].Content.ReadAsStringAsync());
        // Los exhibidores primero (el principal, luego el nuevo) y después las temporadas.
        Assert.Equal(["Mostrador", "La pared"], pared.Exhibidores.Where(e => e.Tipo == TipoExhibidor.Exhibidor).Select(e => e.Nombre));
        Assert.Equal(TipoExhibidor.Temporada, pared.Exhibidores[^1].Tipo);
        Assert.Equal(HttpStatusCode.BadRequest, sinFechas.StatusCode);
        Assert.Contains("Solo las temporadas tienen fechas", await sinFechas.Content.ReadAsStringAsync());
        // La quita para no estorbar a las demás pruebas de esta clase.
        await Leer<MostradorDto>(await http.PostAsJsonAsync($"api/mostrador/{pared.Exhibidores.Single(e => e.Nombre == "La pared").Id}/quitar",
            new SolicitudMostrador(encargado.UsuarioId, caja.SucursalId)));
    }

    /// <summary>
    /// La copia completa trae los exhibidores y las temporadas con lo acomodado, en el formato 3; y una caja de este formato
    /// con su copia los encuentra sin preguntar (los de Ctrl+M y el chip salen de ahí).
    /// </summary>
    [Fact]
    public async Task La_copia_de_la_caja_los_trae_siempre()
    {
        var (http, caja) = await CajaAsync();
        var dueno = await EntrarAsync(http, "dueno", "3333");
        var copia = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var producto = copia.Productos.First(p => p.Presentacion.EsBase);
        var mostrador = copia.Exhibidores!.First(e => e.Nombre == "Mostrador");
        await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(dueno.UsuarioId, caja.SucursalId,
            [new LugarConProductosDto(mostrador.Lugares[1].Id, [producto.VarianteId])])));

        var despues = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));

        Assert.Equal(3, copia.Formato);
        Assert.Equal(CopiaAlDia.Formato, copia.Formato);
        Assert.Equal(["Mostrador", "15 de septiembre", "Todos Santos", "Navidad", "Primavera"], copia.Exhibidores!.Select(e => e.Nombre).Take(5));
        Assert.Equal([producto.VarianteId], despues.Exhibidores!.First(e => e.Nombre == "Mostrador").Lugares[1].Productos);
    }
}
