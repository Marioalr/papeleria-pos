using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que el servicio les da a las páginas de Productos, Inventario y Clientes (entrega 8): la lista de Productos que abre
/// llena, sus filtros, lo pendiente de las tareas del inventario y quién puede dar crédito. Solo leen.
/// </summary>
public class PaginasApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        return (http, caja, cajero, encargado, dueno);
    }

    private static Task<HttpResponseMessage> ListaAsync(HttpClient http, CajaDto caja, SesionDto quien, string extra = "") =>
        http.GetAsync($"api/catalogo/lista?sucursalId={caja.SucursalId}&usuarioId={quien.UsuarioId}{extra}");

    /// <summary>Lo vendido en 90 días de cada producto (todas sus variantes), como lo cuenta la búsqueda de la venta.</summary>
    private async Task<Dictionary<Guid, int>> VendidosPorProductoAsync()
    {
        await using var db = servicio.Base();
        var desde = DateTime.UtcNow.AddDays(-90);
        var historicas = await (from l in db.VentasHistoricasLineas
                                join v in db.VentasHistoricas on l.VentaHistoricaId equals v.Id
                                where !v.Cancelada && v.FechaUtc >= desde
                                select l.VarianteId).ToListAsync();
        var delPos = await (from l in db.VentasLineas
                            join v in db.Ventas on l.VentaId equals v.Id
                            where v.Estado == EstadoVenta.Pagada && v.CreadaUtc >= desde && l.VarianteId != null
                            select l.VarianteId!.Value).ToListAsync();
        var productoDe = await db.Variantes.ToDictionaryAsync(v => v.Id, v => v.ProductoId);
        return historicas.Concat(delPos).GroupBy(v => productoDe[v]).ToDictionary(g => g.Key, g => g.Count());
    }

    [Fact]
    public async Task La_lista_de_productos_abre_llena_con_lo_mas_vendido_primero_y_dice_cuantos_hay()
    {
        var (http, caja, _, encargado, _) = await PrepararAsync();
        var vendidos = await VendidosPorProductoAsync();

        var lista = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado));
        var diez = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado, "&maximo=10"));

        // Sin buscar nada: llena, los primeros 50, y cuántos hay en total.
        Assert.Equal(50, lista.Productos.Count);
        Assert.True(lista.Total > 50, $"Hay {lista.Total}");
        Assert.True(lista.PorLoMasVendido);
        // Lo más vendido primero, y con lo mismo vendido, por nombre.
        var veces = lista.Productos.Select(p => vendidos.GetValueOrDefault(p.ProductoId)).ToList();
        Assert.Equal(veces.OrderByDescending(v => v), veces);
        Assert.True(veces[0] > 0);
        Assert.Equal(lista.Productos.Take(10).Select(p => p.ProductoId), diez.Productos.Select(p => p.ProductoId));
        Assert.Equal(lista.Total, diez.Total);
        // Sin «Ver costos, utilidad y margen», el margen no viaja.
        Assert.All(lista.Productos, p => Assert.Null(p.MargenCentesimas));
    }

    [Fact]
    public async Task Con_texto_busca_en_el_nombre_la_marca_el_departamento_y_el_codigo_y_el_codigo_exacto_va_primero()
    {
        var (http, caja, _, encargado, _) = await PrepararAsync();
        var conCodigo = (await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado))).Productos.First(p => p.Codigo is not null);

        var porCodigo = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado, $"&q={Uri.EscapeDataString(conCodigo.Codigo!)}"));
        var porDepartamento = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado,
            $"&q={Uri.EscapeDataString(conCodigo.Departamento)}&maximo=500"));

        Assert.Equal(conCodigo.ProductoId, porCodigo.Productos[0].ProductoId);
        // El nombre del departamento también encuentra lo suyo, como en el prototipo.
        Assert.Contains(porDepartamento.Productos, p => p.ProductoId == conCodigo.ProductoId);
        Assert.True(porDepartamento.Productos.Count(p => p.Departamento == conCodigo.Departamento) > porDepartamento.Total / 2);
    }

    [Fact]
    public async Task Los_filtros_por_completar_y_poca_existencia()
    {
        var (http, caja, cajero, encargado, _) = await PrepararAsync();
        var departamento = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var codigo = $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        await Leer<ProductoVentaDto>(await http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(cajero.UsuarioId, caja.SucursalId, codigo, "Alta desde la caja para las páginas", 1250, departamento)));

        var porCompletar = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado, "&filtro=PorCompletar"));
        var poca = await Leer<ListaProductosDto>(await ListaAsync(http, caja, encargado, "&filtro=PocaExistencia&maximo=500"));

        Assert.True(porCompletar.PorCompletar >= 1);
        Assert.Equal(porCompletar.PorCompletar, porCompletar.Total);
        Assert.Contains(porCompletar.Productos, p => p.Codigo == codigo);
        Assert.All(porCompletar.Productos, p => Assert.True(p.PorCompletar));
        // Poca existencia: lo que lleva inventario y tiene 3 o menos (el producto recién dado de alta, en cero, también).
        Assert.NotEmpty(poca.Productos);
        Assert.All(poca.Productos, p =>
        {
            Assert.False(p.Servicio);
            Assert.True(p.Existencia <= PocaExistencia.Hasta, $"{p.Nombre}: {p.Existencia}");
        });
        Assert.Contains(poca.Productos, p => p.Codigo == codigo);
    }

    [Fact]
    public async Task El_margen_bajo_solo_para_quien_ve_la_utilidad_y_con_el_minimo_de_la_configuracion()
    {
        var (http, caja, _, encargado, dueno) = await PrepararAsync();
        var margenes = (await http.GetFromJsonAsync<MargenesDto>($"api/sucursales/{caja.SucursalId}/margenes"))!;

        // Sin «Ver costos, utilidad y margen»: el servicio no lo da.
        var sinPermiso = await ListaAsync(http, caja, encargado, "&filtro=MargenBajo");
        Assert.Equal(HttpStatusCode.Forbidden, sinPermiso.StatusCode);
        Assert.Contains("Ver costos, utilidad y margen", (await sinPermiso.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);

        try
        {
            // Con el mínimo en 99 %, todo lo que tiene costo deja menos; en 0 %, nada (en los datos de prueba todos ganan).
            await Leer<MargenesDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/margenes",
                new SolicitudMargenes(dueno.UsuarioId, margenes with { MargenMinimoCentesimas = 9900, Departamentos = [] })));
            var todos = await Leer<ListaProductosDto>(await ListaAsync(http, caja, dueno, "&filtro=MargenBajo"));
            Assert.True(todos.Total > 0);
            Assert.All(todos.Productos, p =>
            {
                Assert.NotNull(p.MargenCentesimas);
                Assert.True(p.MargenCentesimas < p.MargenMinimoCentesimas);
            });

            await Leer<MargenesDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/margenes",
                new SolicitudMargenes(dueno.UsuarioId, margenes with { MargenMinimoCentesimas = 0, Departamentos = [] })));
            Assert.Equal(0, (await Leer<ListaProductosDto>(await ListaAsync(http, caja, dueno, "&filtro=MargenBajo"))).Total);
            // Quien ve la utilidad la recibe también en la lista de siempre.
            Assert.Contains((await Leer<ListaProductosDto>(await ListaAsync(http, caja, dueno))).Productos, p => p.MargenCentesimas is not null);
        }
        finally
        {
            await Leer<MargenesDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/margenes",
                new SolicitudMargenes(dueno.UsuarioId, margenes)));
        }
    }

    [Fact]
    public async Task Lo_pendiente_del_inventario_cuenta_las_compras_las_ordenes_y_los_conteos()
    {
        var (http, caja, cajero, encargado, _) = await PrepararAsync();
        async Task<PendientesInventarioDto> PendientesAsync() =>
            (await http.GetFromJsonAsync<PendientesInventarioDto>($"api/sucursales/{caja.SucursalId}/inventario/pendientes"))!;
        var antes = await PendientesAsync();
        var proveedor = (await http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.First();
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=cuaderno&sucursalId={caja.SucursalId}"))!.First();
        var conPresentacion = (await http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{producto.VarianteId}?sucursalId={caja.SucursalId}"))!;

        // Una compra que se está contando y una orden vacía (se arma): la orden todavía no viene en camino.
        await Leer<CompraDto>(await http.PostAsJsonAsync("api/compras", new SolicitudAbrirCompra(cajero.UsuarioId, caja.SucursalId, proveedor.Id!.Value, null)));
        var orden = await Leer<OrdenDto>(await http.PostAsJsonAsync("api/ordenes", new SolicitudAbrirOrden(encargado.UsuarioId, caja.SucursalId, proveedor.Id!.Value, null)));
        var conCompraYOrden = await PendientesAsync();
        Assert.Equal(antes.ComprasContando + 1, conCompraYOrden.ComprasContando);
        Assert.Equal(antes.OrdenesAbiertas + 1, conCompraYOrden.OrdenesAbiertas);
        Assert.Equal(antes.OrdenesEnCamino, conCompraYOrden.OrdenesEnCamino);

        // Con un producto pedido, ya se espera.
        await Leer<OrdenDto>(await http.PostAsJsonAsync($"api/ordenes/{orden.Id}/renglones",
            new SolicitudPedido(encargado.UsuarioId, conPresentacion.VarianteId, conPresentacion.Presentacion.Id, 2000)));
        Assert.Equal(antes.OrdenesEnCamino + 1, (await PendientesAsync()).OrdenesEnCamino);
    }

    [Fact]
    public async Task Quien_puede_dar_credito_va_por_su_nombre()
    {
        var (http, caja, _, _, _) = await PrepararAsync();

        var quienes = await Leer<List<QuienAutoriza>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/quienes-pueden?accion=DarCredito"));
        var mal = await http.GetAsync($"api/sucursales/{caja.SucursalId}/quienes-pueden?accion=Volar");

        // Al empezar, solo el dueño da crédito (las reglas de antes, con los permisos por usuario).
        Assert.Equal(["Dueño de prueba"], quienes.Select(q => q.Nombre));
        Assert.Equal(HttpStatusCode.BadRequest, mal.StatusCode);
    }
}

/// <summary>
/// Los últimos movimientos de un producto, para su detalle en Productos: aparte, porque vende, y la lista de arriba ordena
/// por lo vendido.
/// </summary>
public class MovimientosDeProductoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Los_ultimos_movimientos_cuadran_con_el_kardex_y_dicen_su_folio()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var buscado = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=cuaderno&sucursalId={caja.SucursalId}"))!
            .First(r => r.Existencia > 5);
        var producto = (await http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{buscado.VarianteId}?sucursalId={caja.SucursalId}"))!;
        // Dos ventas: las dos salidas quedan arriba, con su folio.
        var folios = new List<string>();
        foreach (var piezas in new[] { 1000, 2000 })
            folios.Add((await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, piezas)], [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)])))).Folio);

        var kardex = (await http.GetFromJsonAsync<List<MovimientoKardexDto>>($"api/catalogo/variantes/{producto.VarianteId}/kardex?sucursalId={caja.SucursalId}"))!;
        var ultimos = (await http.GetFromJsonAsync<List<MovimientoProductoDto>>(
            $"api/catalogo/variantes/{producto.VarianteId}/movimientos?sucursalId={caja.SucursalId}&maximo=3"))!;

        Assert.Equal(Math.Min(3, kardex.Count), ultimos.Count);
        // Lo que quedó después de cada uno, contado hacia atrás desde la existencia, es lo mismo que dice el kardex completo.
        Assert.Equal(kardex.Take(ultimos.Count).Select(k => (k.Tipo, k.Cantidad, k.Existencia)), ultimos.Select(u => (u.Tipo, u.Cantidad, u.Existencia)));
        Assert.Equal([TipoMovimiento.Venta, TipoMovimiento.Venta], ultimos.Take(2).Select(u => u.Tipo));
        Assert.Equal([-2m, -1m], ultimos.Take(2).Select(u => u.Cantidad));
        Assert.Equal(folios.AsEnumerable().Reverse(), ultimos.Take(2).Select(u => u.Documento));
        // De un producto sin movimientos (o que no existe): nada.
        Assert.Empty((await http.GetFromJsonAsync<List<MovimientoProductoDto>>($"api/catalogo/variantes/{Guid.NewGuid()}/movimientos?sucursalId={caja.SucursalId}"))!);
    }
}
