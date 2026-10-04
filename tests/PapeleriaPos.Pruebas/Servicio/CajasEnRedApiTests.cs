using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Las llamadas de esta fábrica llegan "desde otra PC de la red", como las de la caja 2.</summary>
internal sealed class DesdeLaRed : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
    {
        app.Use((contexto, resto) =>
        {
            contexto.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
            return resto(contexto);
        });
        siguiente(app);
    };
}

public class CajasEnRedApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private HttpClient Local() => servicio.Fabrica.CreateClient();

    private HttpClient DesdeOtraPc(string? llave = null)
    {
        var http = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, DesdeLaRed>())).CreateClient();
        if (llave is not null)
            http.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, llave);
        return http;
    }

    private async Task<Guid> SucursalAsync() => (await Local().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;

    private async Task<AltaCajaDto> DarDeAltaAsync(string serie, Guid? cajaId = null)
    {
        var respuesta = await DesdeOtraPc().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", await SucursalAsync(), cajaId, $"Caja {serie}", serie, $"PC-{serie}"));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<AltaCajaDto>())!;
    }

    [Fact]
    public async Task Desde_la_red_sin_llave_solo_se_ve_si_el_servicio_esta_vivo()
    {
        var http = DesdeOtraPc();

        var estado = await http.GetFromJsonAsync<EstadoServicioDto>("api/estado");
        var cajas = await http.GetAsync("api/cajas");
        var conLlaveFalsa = await DesdeOtraPc("no-es-una-llave").GetAsync("api/cajas");

        Assert.Equal("Papelería POS", estado!.Servicio);
        Assert.NotEmpty(estado.Sucursales);
        Assert.Equal(HttpStatusCode.Unauthorized, cajas.StatusCode);
        Assert.Equal("caja-sin-alta", (await cajas.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.Equal(HttpStatusCode.Unauthorized, conLlaveFalsa.StatusCode);
    }

    [Fact]
    public async Task Solo_el_dueno_da_de_alta_una_caja()
    {
        var sucursal = await SucursalAsync();

        var encargado = await DesdeOtraPc().PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("encargado", "2222"));
        var alta = await DesdeOtraPc().PostAsJsonAsync("api/alta", new SolicitudAltaCaja("encargado", "2222", sucursal, null, "Caja X", "X", "PC-X"));
        var opciones = await DesdeOtraPc().PostAsJsonAsync("api/alta/opciones", new SolicitudCredenciales("dueno", "3333"));

        Assert.Equal(HttpStatusCode.BadRequest, encargado.StatusCode);
        Assert.Equal("Esto lo autoriza el dueño.", (await alta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Contains((await opciones.Content.ReadFromJsonAsync<List<SucursalAltaDto>>())!.Single().Cajas, c => c.Serie == "A");
    }

    [Fact]
    public async Task Con_su_llave_la_caja_nueva_vende_pero_solo_como_ella_misma()
    {
        var alta = await DarDeAltaAsync("B");
        var http = DesdeOtraPc(alta.Llave);
        var cajaA = (await Local().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "A");
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;

        var cajas = await http.GetFromJsonAsync<List<CajaDto>>("api/cajas");
        var turnoPropio = await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta.Caja.Id, cajero.UsuarioId, 50000));
        var turnoAjeno = await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(cajaA.Id, cajero.UsuarioId, 50000));
        var turnoDeA = await http.GetAsync($"api/cajas/{cajaA.Id}/turno");

        Assert.Equal("PC-B", cajas!.Single(c => c.Serie == "B").Equipo);
        Assert.True(turnoPropio.IsSuccessStatusCode, await turnoPropio.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, turnoAjeno.StatusCode);
        Assert.Equal("otra-caja", (await turnoAjeno.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.Equal(HttpStatusCode.Forbidden, turnoDeA.StatusCode);
    }

    [Fact]
    public async Task Al_quitar_la_pc_o_darla_de_alta_en_otra_su_llave_deja_de_servir()
    {
        var primera = await DarDeAltaAsync("C");
        var reemplazo = await DarDeAltaAsync("C", primera.Caja.Id);

        var vieja = await DesdeOtraPc(primera.Llave).GetAsync("api/cajas");
        var nueva = await DesdeOtraPc(reemplazo.Llave).GetAsync("api/cajas");
        var quitar = await Local().PostAsJsonAsync($"api/cajas/{primera.Caja.Id}/quitar-equipo", new SolicitudCredenciales("dueno", "3333"));
        var despues = await DesdeOtraPc(reemplazo.Llave).GetAsync("api/cajas");

        Assert.Equal(primera.Caja.Id, reemplazo.Caja.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, vieja.StatusCode);
        Assert.Equal(HttpStatusCode.OK, nueva.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, quitar.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
    }

    [Fact]
    public async Task La_serie_de_folios_no_se_repite()
    {
        var respuesta = await DesdeOtraPc().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", await SucursalAsync(), null, "Otra caja A", "A", "PC-Z"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("La serie A ya es de otra caja.", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }
}
