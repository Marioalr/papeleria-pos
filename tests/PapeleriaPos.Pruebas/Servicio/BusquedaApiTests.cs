using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>La búsqueda de la pantalla nueva: por principios de palabra, lo elegido antes en esa caja y lo más vendido.</summary>
public class BusquedaApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<List<ResultadoBusquedaDto>> BuscarAsync(HttpClient http, string texto, Guid sucursalId, Guid? cajaId = null) =>
        (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>(
            $"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}{(cajaId is { } c ? $"&cajaId={c}" : "")}"))!;

    private async Task<(HttpClient Http, CajaDto Caja)> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        return (http, (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single());
    }

    [Fact]
    public async Task Cada_pedazo_es_el_principio_de_una_palabra_en_cualquier_orden()
    {
        var (http, caja) = await CajaAsync();

        var resultados = await BuscarAsync(http, "gel plu", caja.SucursalId);

        Assert.NotEmpty(resultados);
        Assert.All(resultados, r => Assert.StartsWith("Pluma de gel", r.Descripcion));
        // «unto» está dentro de «punto», pero no es el principio de ninguna palabra.
        Assert.Empty(await BuscarAsync(http, "unto fino", caja.SucursalId));
    }

    [Fact]
    public async Task Lo_que_eligio_la_caja_con_un_texto_sale_primero_ahi_y_solo_ahi()
    {
        var (http, caja) = await CajaAsync();
        var antes = await BuscarAsync(http, "pluma punto fino", caja.SucursalId, caja.Id);
        var elegido = antes[^1];

        var respuesta = await http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(caja.Id, " Pluma  PUNTO fino", antes.Count, elegido.VarianteId));

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.Equal(elegido.VarianteId, (await BuscarAsync(http, "pluma punto fino", caja.SucursalId, caja.Id))[0].VarianteId);
        // Otra caja (o sin caja) sigue con el orden de siempre.
        Assert.Equal(antes[0].VarianteId, (await BuscarAsync(http, "pluma punto fino", caja.SucursalId, Guid.NewGuid()))[0].VarianteId);
        Assert.Equal(antes[0].VarianteId, (await BuscarAsync(http, "pluma punto fino", caja.SucursalId))[0].VarianteId);

        // Se guarda en la base: después de reiniciar el servicio, se sigue acordando.
        servicio.Fabrica.Services.GetRequiredService<CatalogoEnMemoria>().Invalidar();
        Assert.Equal(elegido.VarianteId, (await BuscarAsync(http, "pluma punto fino", caja.SucursalId, caja.Id))[0].VarianteId);
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var guardada = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Busquedas.AsNoTracking()
            .SingleAsync(b => b.CajaId == caja.Id && b.VarianteId == elegido.VarianteId);
        Assert.Equal("PLUMA PUNTO FINO", guardada.Texto);
        Assert.Equal(antes.Count, guardada.Resultados);
    }

    [Fact]
    public async Task Lo_que_no_encontro_nada_se_anota_sin_ensenar_nada()
    {
        var (http, caja) = await CajaAsync();

        var respuesta = await http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(caja.Id, "plumero de avestruz", 0, null));

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var guardada = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Busquedas.AsNoTracking()
            .SingleAsync(b => b.Texto == "PLUMERO DE AVESTRUZ");
        Assert.Equal(0, guardada.Resultados);
        Assert.Null(guardada.VarianteId);
        // Una caja que no existe no deja rastro.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(Guid.NewGuid(), "cuaderno", 3, null))).StatusCode);
    }

    [Fact]
    public async Task Si_el_texto_no_distingue_sale_primero_lo_mas_vendido()
    {
        var (http, caja) = await CajaAsync();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var opciones = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        async Task<VarianteFichaDto> CrearAsync(string nombre) =>
            (await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId,
                new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Servicio, Impuesto.Iva16, null, true, false,
                    [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [])], [new PrecioFichaDto(null, 1000, 1500)])])))))
            .Variantes[0];
        await CrearAsync("Engargolado orden alfa");
        var beta = await CrearAsync("Engargolado orden beta");
        // Sin ventas, por nombre: alfa antes que beta.
        Assert.Equal(["Engargolado orden alfa", "Engargolado orden beta"], (await BuscarAsync(http, "engar orden", caja.SucursalId)).Select(r => r.Descripcion));

        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(beta.Id!.Value, beta.Presentaciones[0].Id!.Value, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)])));
        // Lo vendido se cuenta una vez al día; aquí, como si fuera otro día.
        servicio.Fabrica.Services.GetRequiredService<CatalogoEnMemoria>().Invalidar();

        Assert.Equal(["Engargolado orden beta", "Engargolado orden alfa"], (await BuscarAsync(http, "engar orden", caja.SucursalId)).Select(r => r.Descripcion));
    }
}
