using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El cobro con la versión de la copia de la caja (entrega 5): la caja escanea contra su copia del catálogo y el cobro lleva
/// su versión; si el precio o el mayoreo de algo del ticket cambió después, el servicio no cobra y contesta con los precios
/// de ahora. Una caja de antes no manda versión y se cobra como siempre.
/// </summary>
public sealed class CobroConLaVersionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno, PaqueteContingenciaDto Copia);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>El cajero con su turno y la copia de la caja recién bajada.</summary>
    private async Task<Caja> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var copia = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        return new Caja(http, caja, cajero, dueno, turno, copia);
    }

    /// <summary>Productos de la copia que se venden por pieza con un solo precio (sin mayoreo); de cada prueba, otros.</summary>
    private static List<ProductoVentaDto> SinMayoreo(Caja c) =>
        c.Copia.Productos.Where(p => p is { Unidad: UnidadBase.Pieza, Presentacion.EsBase: true } && p.Precios.Count == 1
                                     && p.Precios[0] is { PresentacionId: null, SucursalId: null, CantidadMinimaMilesimas: 1000 })
            .OrderBy(p => p.VarianteId).ToList();

    private static Task<HttpResponseMessage> CobrarAsync(Caja c, ProductoVentaDto producto, int piezas, long? version, Guid? ventaId = null,
        long? versionDeLaLinea = null) =>
        c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, piezas * 1000, VersionCatalogo: versionDeLaLinea)],
            [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)], VentaId: ventaId ?? Guid.NewGuid(), VersionCatalogo: version));

    private static async Task CambiarPrecioAsync(Caja c, ProductoVentaDto producto, long centavos) =>
        Assert.True((await c.Http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Dueno.UsuarioId, c.Datos.SucursalId, producto.VarianteId, centavos))).IsSuccessStatusCode);

    [Fact]
    public async Task Con_la_version_de_una_copia_vieja_no_cobra_y_contesta_con_los_precios_de_ahora()
    {
        var c = await PrepararAsync();
        var producto = SinMayoreo(c)[0];
        var antes = producto.Precios[0].PrecioCentavos;
        await CambiarPrecioAsync(c, producto, antes + 300);
        var ventaId = Guid.NewGuid();

        var respuesta = await CobrarAsync(c, producto, 2, c.Copia.Version, ventaId);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        var cambiados = JsonSerializer.Deserialize<PreciosCambiadosDto>(cuerpo, JsonSerializerOptions.Web)!;
        Assert.Equal(PreciosCambiadosDto.CodigoDelError, cambiados.Codigo);
        Assert.Equal($"Cambió el precio de «{producto.Descripcion}» en la caja principal: revisa el total y cobra otra vez.", cambiados.Mensaje);
        var alDia = Assert.Single(cambiados.Productos);
        Assert.Equal(producto.VarianteId, alDia.VarianteId);
        Assert.Equal(antes + 300, alDia.Precios.Single().PrecioCentavos);
        Assert.True(cambiados.VersionCatalogo > c.Copia.Version);
        Assert.Equal(cambiados.VersionCatalogo, alDia.Version);
        // Se lee también como cualquier error del servicio.
        Assert.Equal((cambiados.Mensaje, PreciosCambiadosDto.CodigoDelError),
            JsonSerializer.Deserialize<ErrorDto>(cuerpo, JsonSerializerOptions.Web) is { } error ? (error.Mensaje, error.Codigo) : default);
        // No se cobró: la venta no existe.
        Assert.Equal(HttpStatusCode.NotFound, (await c.Http.GetAsync($"api/ventas/{ventaId}/ticket")).StatusCode);

        // Con los precios de ahora (la versión que dijo el servicio), sí.
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, producto, 2, cambiados.VersionCatalogo, ventaId));
        Assert.Equal(2 * (antes + 300), ticket.TotalCentavos);
    }

    [Fact]
    public async Task Lo_que_cambio_fuera_del_ticket_no_detiene_el_cobro()
    {
        var c = await PrepararAsync();
        var (vendido, otro) = (SinMayoreo(c)[1], SinMayoreo(c)[2]);
        await CambiarPrecioAsync(c, otro, otro.Precios[0].PrecioCentavos + 500);

        var ticket = await Leer<TicketDto>(await CobrarAsync(c, vendido, 3, c.Copia.Version));

        Assert.Equal(3 * vendido.Precios[0].PrecioCentavos, ticket.TotalCentavos);
    }

    /// <summary>
    /// Una escala de mayoreo que se quitó no deja ningún precio con versión nueva: solo el historial de precios, con la versión
    /// con que se quitó (por eso la migración). El cajero vio el precio de mayoreo; el servicio ya no lo tiene.
    /// </summary>
    [Fact]
    public async Task Una_escala_de_mayoreo_que_se_quito_tambien_detiene_el_cobro()
    {
        var c = await PrepararAsync();
        var producto = c.Copia.Productos.Where(p => p is { Unidad: UnidadBase.Pieza, Presentacion.EsBase: true }
                                                    && p.Precios.Count(x => x is { PresentacionId: null, SucursalId: null }) >= 2)
            .OrderBy(p => p.VarianteId).First();
        var escala = producto.Precios.Where(x => x is { PresentacionId: null, SucursalId: null }).MaxBy(x => x.CantidadMinimaMilesimas)!;
        await using (var db = servicio.Base())
        {
            db.Precios.Remove(await db.Precios.SingleAsync(p => p.Id == escala.Id));
            await db.SaveChangesAsync();
        }

        var respuesta = await CobrarAsync(c, producto, (int)(escala.CantidadMinimaMilesimas / 1000), c.Copia.Version);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var alDia = Assert.Single((await respuesta.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!.Productos);
        Assert.DoesNotContain(alDia.Precios, p => p.Id == escala.Id);
        await using (var db = servicio.Base())
        {
            // Lo que queda de sus precios es de antes de la copia: lo dijo el historial.
            Assert.All(await db.Precios.AsNoTracking().Where(p => p.VarianteId == producto.VarianteId).ToListAsync(),
                p => Assert.True(p.Version <= c.Copia.Version));
            Assert.True(await db.PreciosHistorial.AsNoTracking()
                .AnyAsync(h => h.PrecioId == escala.Id && h.PrecioNuevoCentavos == null && h.VersionCatalogo > c.Copia.Version));
        }
    }

    [Fact]
    public async Task Una_caja_de_antes_sin_version_cobra_como_siempre()
    {
        var c = await PrepararAsync();
        var producto = SinMayoreo(c)[3];
        var nuevo = producto.Precios[0].PrecioCentavos + 700;
        await CambiarPrecioAsync(c, producto, nuevo);

        var ticket = await Leer<TicketDto>(await CobrarAsync(c, producto, 1, version: null));

        Assert.Equal(nuevo, ticket.TotalCentavos);
    }

    /// <summary>Un producto que llegó de la caja principal después de la copia trae su propia versión: con ella se revisa.</summary>
    [Fact]
    public async Task La_version_de_cada_linea_manda_sobre_la_del_ticket()
    {
        var c = await PrepararAsync();
        var producto = SinMayoreo(c)[4];
        await CambiarPrecioAsync(c, producto, producto.Precios[0].PrecioCentavos + 100);
        var deLaPrincipal = await Leer<ProductoVentaDto>(await c.Http.GetAsync($"api/productos/{producto.VarianteId}?sucursalId={c.Datos.SucursalId}"));

        var ticket = await Leer<TicketDto>(await CobrarAsync(c, deLaPrincipal, 1, c.Copia.Version, versionDeLaLinea: deLaPrincipal.Version));

        Assert.True(deLaPrincipal.Version > c.Copia.Version);
        Assert.Equal(producto.Precios[0].PrecioCentavos + 100, ticket.TotalCentavos);
    }

    /// <summary>
    /// Una versión más nueva que la del servicio: su base cambió entera (la prueba empezó de cero). No se sabe qué vio el
    /// cajero: se contesta con los precios de ahora.
    /// </summary>
    [Fact]
    public async Task Una_version_mas_nueva_que_la_del_servicio_no_se_cobra_sin_revisar()
    {
        var c = await PrepararAsync();
        var producto = SinMayoreo(c)[5];

        var respuesta = await CobrarAsync(c, producto, 1, c.Copia.Version + 1000);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(producto.VarianteId, Assert.Single((await respuesta.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!.Productos).VarianteId);
    }
}
