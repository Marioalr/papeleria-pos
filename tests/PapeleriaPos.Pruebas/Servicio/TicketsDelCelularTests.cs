using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Mandar el ticket del celular a una caja (docs/celular-como-caja.md, «Mandar el ticket a una caja»): el celular, con su llave,
/// ve las cajas y manda qué y cuánto; el servicio pone los precios con su catálogo y lo deja en el buzón de la caja, que lo
/// recoge con la venta abierta. Sin duplicados si se manda otra vez, y con un error claro si la caja no lo recoge.
/// </summary>
public sealed class TicketsDelCelularTests
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> Error(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.Equal(estado, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    private static FichaProductoDto Ficha(Guid departamento, string nombre, string codigo, long precio) =>
        new(null, nombre, departamento, "Marca de Prueba", TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
        [
            new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                [new PrecioFichaDto(null, 1000, precio)]),
        ]);

    /// <summary>La tienda: la caja 2 con su turno y su llave, y el celular dado de alta; un producto con su código y su precio.</summary>
    private sealed record Tienda(ServicioDePrueba Servicio, HttpClient Celular, Guid CelularId, HttpClient Caja2, CajaDto Caja2Dto, Guid SucursalId,
        string Codigo, ProductoVentaDto Producto, SesionDto Encargado) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Servicio.DisposeAsync();
    }

    private static async Task<Tienda> TiendaAsync(params (string, string)[] ajustes)
    {
        var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var principal = servicio.Fabrica.CreateClient();
        var caja1 = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var alta2 = await Leer<AltaCajaDto>(await principal.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja1.SucursalId, null, "Caja 2", "B", "PC-2")));
        var caja2 = servicio.Fabrica.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta2.Llave);
        await Leer<TurnoDto>(await caja2.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta2.Caja.Id, encargado.UsuarioId, 0)));

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja1.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);

        var departamento = (await principal.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        await Leer<FichaProductoDto>(await principal.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(encargado.UsuarioId, caja1.SucursalId, Ficha(departamento, "Corrector de cinta", "36000291452", 2350))));
        var producto = await Leer<ProductoVentaDto>(await celular.GetAsync($"api/productos/codigo/36000291452?sucursalId={caja1.SucursalId}"));
        return new Tienda(servicio, celular, alta.Caja.Id, caja2, alta2.Caja, caja1.SucursalId, "36000291452", producto, encargado);
    }

    /// <summary>La caja 2 con la venta abierta: pregunta por sus tickets cada 50 ms y guarda lo que llega, hasta que se le pide parar.</summary>
    private static async Task<List<TicketRecibidoDto>> CajaConLaVentaAbiertaAsync(Tienda t, CancellationToken ct, int esperan = 1)
    {
        var recibidos = new List<TicketRecibidoDto>();
        while (!ct.IsCancellationRequested)
        {
            var llegaron = await Leer<List<TicketRecibidoDto>>(
                await t.Caja2.GetAsync($"api/cajas/{t.Caja2Dto.Id}/recibidos?esperan={esperan}&atiende={t.Encargado.UsuarioId}", CancellationToken.None));
            foreach (var l in llegaron.Where(l => recibidos.All(r => r.Id != l.Id)))
                recibidos.Add(l);
            if (llegaron.Count > 0)
                Assert.True((await t.Caja2.PostAsJsonAsync($"api/cajas/{t.Caja2Dto.Id}/recibidos", new SolicitudRecibidos([.. llegaron.Select(l => l.Id)]))).IsSuccessStatusCode);
            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return recibidos;
    }

    [Fact]
    public async Task El_celular_lo_manda_la_caja_lo_recoge_con_los_precios_del_servicio_y_otra_vez_no_lo_duplica()
    {
        await using var t = await TiendaAsync();

        // Sin la venta abierta, la caja sale, pero no recibe.
        var cajas = await Leer<List<CajaParaMandarDto>>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/para-mandar"));
        var caja2 = Assert.Single(cajas, c => c.Id == t.Caja2Dto.Id);
        Assert.False(caja2.Recibe);
        Assert.DoesNotContain(cajas, c => c.Id == t.CelularId);
        var id = Guid.NewGuid();
        var solicitud = new SolicitudMandarTicket(id, t.Caja2Dto.Id,
            [new RenglonMandado(t.Producto.VarianteId, t.Producto.Presentacion.Id, null, 3000), new RenglonMandado(null, null, "0036000291452", 1000)]);
        var sinVenta = await Error(await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", solicitud), HttpStatusCode.Conflict);
        Assert.Equal("caja-no-recibe", sinVenta.Codigo);
        Assert.StartsWith("La Caja 2 no tiene la venta abierta", sinVenta.Mensaje);

        using var parar = new CancellationTokenSource();
        var caja = CajaConLaVentaAbiertaAsync(t, parar.Token);
        await VentaConCopiaEsperar(async () =>
            (await Leer<List<CajaParaMandarDto>>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/para-mandar"))).Single(c => c.Id == t.Caja2Dto.Id).Recibe);
        caja2 = (await Leer<List<CajaParaMandarDto>>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/para-mandar"))).Single(c => c.Id == t.Caja2Dto.Id);
        Assert.Equal(("Encargado", 1), (caja2.Atiende?.Split(' ')[0], caja2.Esperan));

        // Por variante y por código (con la regla del UPC): el mismo producto, una sola línea de 4, al precio del servicio.
        var mandado = await Leer<TicketMandadoDto>(await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", solicitud));
        Assert.True(mandado.Recibido);
        Assert.Equal(("Caja 2", 1, 4m, 4 * 2350L), (mandado.Caja, mandado.Renglones, mandado.Articulos, mandado.TotalCentavos));

        // Otra vez el mismo (la respuesta se perdió en el Wi-Fi): la misma respuesta, y la caja no lo recibe dos veces.
        var otraVez = await Leer<TicketMandadoDto>(await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", solicitud));
        Assert.Equal(mandado, otraVez);
        await Task.Delay(200);
        await parar.CancelAsync();
        var recibido = Assert.Single(await caja);
        Assert.Equal((id, "Celular de Luis", 4 * 2350L), (recibido.Id, recibido.De, recibido.TotalCentavos));
        // La caja arma el ticket con sus precios: las líneas no traen ninguno.
        var linea = Assert.Single(recibido.Lineas);
        Assert.Equal((t.Producto.VarianteId, (long?)null, 4000L), (linea.VarianteId!.Value, linea.PrecioCentavos, linea.CantidadMilesimas));
        Assert.Equal(2350, Assert.Single(recibido.Productos).Precios.Single().PrecioCentavos);
    }

    [Fact]
    public async Task Lo_que_no_se_puede_mandar_lo_dice_en_espanol()
    {
        await using var t = await TiendaAsync();
        using var parar = new CancellationTokenSource();
        var caja = CajaConLaVentaAbiertaAsync(t, parar.Token);
        await VentaConCopiaEsperar(async () =>
            (await Leer<List<CajaParaMandarDto>>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/para-mandar"))).Single(c => c.Id == t.Caja2Dto.Id).Recibe);
        async Task<ErrorDto> Mandar(SolicitudMandarTicket s, HttpClient? quien = null) =>
            (await (quien ?? t.Celular).PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", s)).Content.ReadFromJsonAsync<ErrorDto>().Result!;
        var bueno = new RenglonMandado(t.Producto.VarianteId, null, null, 1000);

        Assert.Equal("El ticket está vacío.", (await Mandar(new SolicitudMandarTicket(Guid.NewGuid(), t.Caja2Dto.Id, []))).Mensaje);
        Assert.Equal("«7501234567890» ya no está en el catálogo de la caja principal: quítalo del ticket.",
            (await Mandar(new SolicitudMandarTicket(Guid.NewGuid(), t.Caja2Dto.Id, [bueno, new RenglonMandado(null, null, "7501234567890", 1000)]))).Mensaje);
        Assert.Equal("Una cantidad del ticket no es válida: va de 1 a 9,999.",
            (await Mandar(new SolicitudMandarTicket(Guid.NewGuid(), t.Caja2Dto.Id, [bueno with { CantidadMilesimas = 0 }]))).Mensaje);
        Assert.Equal("Esa caja ya no está en la tienda: elige otra.",
            (await Mandar(new SolicitudMandarTicket(Guid.NewGuid(), t.CelularId, [bueno]))).Mensaje);
        // Sin la llave del celular (desde esta misma PC, sin alta), no.
        var sinLlave = t.Servicio.Fabrica.CreateClient();
        var r = await sinLlave.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", new SolicitudMandarTicket(Guid.NewGuid(), t.Caja2Dto.Id, [bueno]));
        Assert.Equal("sin-llave", (await Error(r, HttpStatusCode.Unauthorized)).Codigo);
        // Con la llave de otra caja, a nombre del celular: no es la suya.
        var ajeno = await t.Caja2.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", new SolicitudMandarTicket(Guid.NewGuid(), t.Caja2Dto.Id, [bueno]));
        Assert.Equal("otra-caja", (await Error(ajeno, HttpStatusCode.Forbidden)).Codigo);

        await parar.CancelAsync();
        Assert.Empty(await caja);
    }

    [Fact]
    public async Task Si_la_caja_deja_de_recogerlos_el_celular_lo_sabe_y_no_le_llega_despues()
    {
        await using var t = await TiendaAsync(("Celular:SegundosParaRecibir", "1"));
        // La caja preguntó una vez (tenía la venta abierta) y se fue: el celular la ve lista, pero nadie lo recoge.
        await Leer<List<TicketRecibidoDto>>(await t.Caja2.GetAsync($"api/cajas/{t.Caja2Dto.Id}/recibidos?esperan=0&atiende={t.Encargado.UsuarioId}"));
        var id = Guid.NewGuid();
        var r = await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar",
            new SolicitudMandarTicket(id, t.Caja2Dto.Id, [new RenglonMandado(t.Producto.VarianteId, null, null, 2000)]));
        var error = await Error(r, HttpStatusCode.Conflict);
        Assert.Equal(("caja-no-recibe", "La Caja 2 no lo recogió: revisa que tenga la venta abierta y vuelve a mandarlo."), (error.Codigo, error.Mensaje));
        // Se retiró: cuando la caja vuelve a preguntar, ya no está.
        Assert.Empty(await Leer<List<TicketRecibidoDto>>(await t.Caja2.GetAsync($"api/cajas/{t.Caja2Dto.Id}/recibidos")));
    }

    private static async Task VentaConCopiaEsperar(Func<Task<bool>> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException("La caja no quedó lista para recibir.");
            await Task.Delay(20);
        }
    }
}
