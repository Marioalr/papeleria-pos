using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Cambiar precios de varios a la vez (Productos › Cambiar precios), como «Actualizar varios» de eleventa: los de un
/// departamento, una marca, lo que coincide con un texto o los marcados; un porcentaje o una cantidad, o un precio fijo, con
/// el mayoreo en la misma proporción y el redondeo. Primero la vista, que no cambia nada; al aplicar, todo en una vez, con un
/// renglón en la bitácora, el historial de cada precio y una sola versión del catálogo. El margen solo para quien ve la
/// utilidad; sin «Productos, precios y etiquetas», nada. En el piloto con eleventa, el menudeo y las promociones de lo de
/// eleventa no cambian aquí, y su mayoreo de aquí y lo dado de alta aquí, sí.
/// </summary>
public sealed class PreciosDeVariosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, Guid Sucursal, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que dice el servicio al rechazar: un 400 con su mensaje.</summary>
    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static async Task<Caja> PrepararAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> EntrarAsync(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja.SucursalId, await EntrarAsync("cajero", "1111"), await EntrarAsync("encargado", "2222"), await EntrarAsync("dueno", "3333"));
    }

    private Task<Caja> PrepararAsync() => PrepararAsync(servicio.Fabrica.CreateClient());

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Un departamento solo para la prueba: así lo que se cambia es solo lo suyo.</summary>
    private static async Task<(Guid Id, string Nombre)> DepartamentoAsync(Caja c)
    {
        var nombre = $"Precios {Ficha()}";
        var lista = await Leer<DepartamentosDto>(await c.Http.PostAsJsonAsync("api/catalogo/departamentos",
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Sucursal, nombre)));
        return (lista.Departamentos.Single(d => d.Nombre == nombre).Id, nombre);
    }

    /// <summary>
    /// Un producto con su menudeo y, si se dan, sus escalas de mayoreo, una «Caja 12» con precio propio u otro paquete con el
    /// suyo.
    /// </summary>
    private static async Task<FichaProductoDto> ProductoAsync(
        Caja c, string nombre, Guid departamento, long menudeo, string? marca = null, (long Desde, long Precio)[]? escalas = null, long? caja12 = null,
        (string Nombre, long Piezas, long Precio)? paquete = null)
    {
        var presentaciones = new List<PresentacionFichaDto> { new(null, "Pieza", 1000, true, [new CodigoFichaDto($"PV{Guid.NewGuid():N}"[..16])]) };
        var precios = new List<PrecioFichaDto> { new(null, 1000, menudeo) };
        precios.AddRange((escalas ?? []).Select(e => new PrecioFichaDto(null, e.Desde * 1000, e.Precio)));
        if (caja12 is { } caja)
        {
            presentaciones.Add(new PresentacionFichaDto(null, "Caja 12", 12000, false, []));
            precios.Add(new PrecioFichaDto("Caja 12", 1000, caja));
        }
        if (paquete is { } otro)
        {
            presentaciones.Add(new PresentacionFichaDto(null, otro.Nombre, otro.Piezas * 1000, false, []));
            precios.Add(new PrecioFichaDto(otro.Nombre, 1000, otro.Precio));
        }
        return await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, marca, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, precios)]))));
    }

    private static async Task<FichaProductoDto> FichaAsync(Caja c, FichaProductoDto producto) =>
        await Leer<FichaProductoDto>(await c.Http.GetAsync($"api/catalogo/productos/{producto.Id}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));

    /// <summary>Sus precios como quedaron: (paquete, desde cuántas, precio), el menudeo primero.</summary>
    private static async Task<List<(string? Paquete, long Desde, long Precio)>> PreciosAsync(Caja c, FichaProductoDto producto) =>
        (await FichaAsync(c, producto)).Variantes[0].Precios.Select(p => (p.Presentacion, p.CantidadMinimaMilesimas / 1000, p.PrecioCentavos)).ToList();

    private static async Task<long> MenudeoAsync(Caja c, FichaProductoDto producto) => (await PreciosAsync(c, producto)).First(p => p.Paquete is null).Precio;

    private static SolicitudPreciosDeVarios Pedido(
        Caja c, SesionDto quien, CualesProductos cuales, ComoCambiarPrecios como, long valor, Guid? departamento = null, string? marca = null,
        string? texto = null, IReadOnlyList<Guid>? productos = null, bool conMayoreo = true, RedondeoDePrecios redondeo = RedondeoDePrecios.CincuentaCentavos) =>
        new(quien.UsuarioId, c.Sucursal, cuales, departamento, marca, texto, productos, como, valor, conMayoreo, redondeo);

    private static Task<HttpResponseMessage> VistaAsync(Caja c, SolicitudPreciosDeVarios pedido) =>
        c.Http.PostAsJsonAsync("api/catalogo/precios-de-varios/vista", pedido);

    private static Task<HttpResponseMessage> AplicarAsync(Caja c, SolicitudPreciosDeVarios pedido) =>
        c.Http.PostAsJsonAsync("api/catalogo/precios-de-varios", pedido);

    /// <summary>Lo que se vio, aplicado: con su huella y un Id nuevo.</summary>
    private static async Task<PreciosDeVariosAplicadosDto> VerYAplicarAsync(Caja c, SolicitudPreciosDeVarios pedido)
    {
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, pedido));
        return await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, pedido with { Huella = vista.Huella, Id = Guid.NewGuid() }));
    }

    private async Task<long> VersionDelCatalogoAsync()
    {
        await using var db = servicio.Base();
        return await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).SingleAsync();
    }

    [Fact]
    public async Task Un_porcentaje_cambia_el_menudeo_y_el_mayoreo_en_la_misma_proporcion_todo_de_una_vez()
    {
        var c = await PrepararAsync();
        var (departamento, nombreDepartamento) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, 1250, escalas: [(12, 1100)], caja12: 13000);
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, 2000);
        var pedido = Pedido(c, c.Encargado, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento);

        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, pedido));

        Assert.Equal($"el departamento «{nombreDepartamento}»", vista.Cuales);
        Assert.Equal("subir 10 %, con el mayoreo en la misma proporción, redondeado a los 50 centavos más cercanos", vista.Regla);
        Assert.Empty(vista.NoCambian);
        Assert.Null(vista.EnEleventa);
        // $12.50 + 10 % es $13.75: a los 50 centavos, $14.00; la escala ($12.10) y la caja ($143.00), igual.
        var deCuaderno = vista.Cambian.Single(x => x.ProductoId == cuaderno.Id);
        Assert.Equal(($"Cuaderno {ficha}", 1250L, 1400L, false), (deCuaderno.Producto, deCuaderno.AntesCentavos, deCuaderno.DespuesCentavos, deCuaderno.MenudeoEnEleventa));
        Assert.Equal([("desde 12", 1100L, 1200L), ("Caja 12", 13000L, 14300L)], deCuaderno.Mayoreo.Select(m => (m.Cual, m.AntesCentavos, m.DespuesCentavos)));
        var deLapiz = vista.Cambian.Single(x => x.ProductoId == lapiz.Id);
        Assert.Equal((2000L, 2200L), (deLapiz.AntesCentavos, deLapiz.DespuesCentavos));
        Assert.Empty(deLapiz.Mayoreo);
        // El encargado no ve la utilidad: sin margen.
        Assert.False(vista.ConMargen);
        Assert.All(vista.Cambian, x => Assert.Equal((null, null, null), (x.MargenAntesCentesimas, x.MargenDespuesCentesimas, x.MargenMinimoCentesimas)));
        // Ver no cambia nada.
        Assert.Equal(1250, await MenudeoAsync(c, cuaderno));

        var antes = await VersionDelCatalogoAsync();
        var id = Guid.NewGuid();
        var hecho = await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, pedido with { Huella = vista.Huella, Id = id }));

        Assert.Equal((2, 4, false), (hecho.Productos, hecho.Precios, hecho.YaSeHabiaAplicado));
        Assert.Equal([(null, 1L, 1400L), (null, 12L, 1200L), ("Caja 12", 1L, 14300L)], await PreciosAsync(c, cuaderno));
        Assert.Equal(2200, await MenudeoAsync(c, lapiz));
        await using var db = servicio.Base();
        // Una sola versión del catálogo para todo (las cajas y el cobro con la versión de su copia se enteran), y el historial
        // de cada precio, como cuando se cambia en la ficha.
        var version = await VersionDelCatalogoAsync();
        Assert.Equal(antes + 1, version);
        var variantes = new[] { cuaderno.Variantes[0].Id!.Value, lapiz.Variantes[0].Id!.Value };
        Assert.All(await db.Precios.Where(p => variantes.Contains(p.VarianteId)).ToListAsync(), p => Assert.Equal(version, p.Version));
        var historial = await db.PreciosHistorial.Where(h => variantes.Contains(h.VarianteId) && h.PrecioAnteriorCentavos != null).ToListAsync();
        Assert.Equal([(1100L, 1200L), (1250L, 1400L), (2000L, 2200L), (13000L, 14300L)],
            historial.Select(h => (h.PrecioAnteriorCentavos!.Value, h.PrecioNuevoCentavos!.Value)).Order());
        // Un solo renglón en la bitácora: cuántos, cuáles y la regla, a nombre de quien lo hizo.
        var renglon = await db.Bitacora.SingleAsync(b => b.Id == id);
        Assert.Equal((AccionControlada.EditarCatalogo, c.Encargado.UsuarioId, ResultadoBitacora.Hecho), (renglon.Accion, renglon.UsuarioId, renglon.Resultado));
        Assert.Equal($"Cambió el precio de 2 productos (el departamento «{nombreDepartamento}»): subir 10 %, con el mayoreo en la misma proporción, "
            + "redondeado a los 50 centavos más cercanos", renglon.Detalle);
        Assert.Equal(renglon.Detalle, hecho.Bitacora);
        Assert.False(await db.Bitacora.AnyAsync(b => b.Detalle.Contains(ficha) && b.Id != id && b.Detalle.StartsWith("Precio de")));
    }

    [Fact]
    public async Task Una_cantidad_sin_el_mayoreo_y_un_precio_fijo_con_el_mayoreo_a_los_de_una_marca()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var marca = $"Marca {ficha}";
        var pluma = await ProductoAsync(c, $"Pluma {ficha}", departamento, 1250, marca, escalas: [(12, 1000)]);
        var plumon = await ProductoAsync(c, $"Plumón {ficha}", departamento, 3000, marca);
        var sinMarca = await ProductoAsync(c, $"Pluma sin marca {ficha}", departamento, 3000);

        // $1.00 menos, sin el mayoreo y sin redondear; la marca, como se escriba.
        var menos = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Marca, ComoCambiarPrecios.BajarMonto, 100,
            marca: $"  {marca.ToUpperInvariant()} ", conMayoreo: false, redondeo: RedondeoDePrecios.SinRedondear)));

        Assert.Equal($"la marca «{marca}»", menos.Cuales);
        Assert.Equal("bajar $1.00, sin cambiar el mayoreo, sin redondear", menos.Regla);
        Assert.Equal([(pluma.Id!.Value, 1250L, 1150L, 0), (plumon.Id!.Value, 3000L, 2900L, 0)],
            menos.Cambian.Select(x => (x.ProductoId, x.AntesCentavos, x.DespuesCentavos, x.Mayoreo.Count)).OrderBy(x => x.AntesCentavos));
        Assert.DoesNotContain(menos.Cambian, x => x.ProductoId == sinMarca.Id);
        await VerYAplicarAsync(c, Pedido(c, c.Encargado, CualesProductos.Marca, ComoCambiarPrecios.BajarMonto, 100, marca: marca, conMayoreo: false,
            redondeo: RedondeoDePrecios.SinRedondear));
        Assert.Equal([(null, 1L, 1150L), (null, 12L, 1000L)], await PreciosAsync(c, pluma));
        Assert.Equal(2900, await MenudeoAsync(c, plumon));
        Assert.Equal(3000, await MenudeoAsync(c, sinMarca));

        // Un precio fijo de $15.00, con el mayoreo y al peso: el menudeo queda tal cual; la escala, en su proporción
        // ($10.00 × 15 / 11.50 = $13.04, al peso $13.00).
        var fijo = Pedido(c, c.Encargado, CualesProductos.Marca, ComoCambiarPrecios.PrecioFijo, 1500, marca: marca, redondeo: RedondeoDePrecios.UnPeso);
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, fijo));
        Assert.Equal("ponerlos a $15.00, con el mayoreo en la misma proporción, redondeado al peso más cercano", vista.Regla);
        var hecho = await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, fijo with { Huella = vista.Huella, Id = Guid.NewGuid() }));
        Assert.Equal((2, 3), (hecho.Productos, hecho.Precios));
        Assert.Equal([(null, 1L, 1500L), (null, 12L, 1300L)], await PreciosAsync(c, pluma));
        Assert.Equal(1500, await MenudeoAsync(c, plumon));
    }

    [Fact]
    public async Task Lo_que_coincide_con_un_texto_y_los_marcados_activos()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var azul = await ProductoAsync(c, $"Zafiro {ficha} azul", departamento, 1000);
        var rojo = await ProductoAsync(c, $"Zafiro {ficha} rojo", departamento, 1000);
        var rubi = await ProductoAsync(c, $"Rubí {ficha}", departamento, 1000);
        var inactivo = await ProductoAsync(c, $"Zafiro {ficha} inactivo", departamento, 1000);
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal, (await FichaAsync(c, inactivo)) with { Activo = false })));

        // Cada palabra, sin acentos ni mayúsculas y en cualquier orden, como la búsqueda de la lista; solo los activos.
        var busqueda = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Busqueda,
            ComoCambiarPrecios.SubirPorcentaje, 500, texto: $" {ficha} ZAFIRO ")));
        Assert.Equal($"lo que coincide con «{ficha} ZAFIRO»", busqueda.Cuales);
        Assert.Equal(new[] { azul.Id!.Value, rojo.Id!.Value }.Order(), busqueda.Cambian.Select(x => x.ProductoId).Order());
        Assert.All(busqueda.Cambian, x => Assert.Equal(1050, x.DespuesCentavos));
        // También por su código.
        var codigo = rubi.Variantes[0].Presentaciones.Single(p => p.EsBase).Codigos.Single().Codigo;
        var porCodigo = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Busqueda,
            ComoCambiarPrecios.SubirPorcentaje, 500, texto: codigo.ToLowerInvariant())));
        Assert.Equal([rubi.Id!.Value], porCodigo.Cambian.Select(x => x.ProductoId));

        // Los marcados en la lista: el inactivo no entra.
        var marcados = Pedido(c, c.Encargado, CualesProductos.Marcados, ComoCambiarPrecios.SubirMonto, 50,
            productos: [rojo.Id!.Value, rubi.Id!.Value, inactivo.Id!.Value], redondeo: RedondeoDePrecios.SinRedondear);
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, marcados));
        Assert.Equal("los marcados en la lista", vista.Cuales);
        Assert.Equal(new[] { rojo.Id!.Value, rubi.Id!.Value }.Order(), vista.Cambian.Select(x => x.ProductoId).Order());
        Assert.Empty(vista.NoCambian);
        await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, marcados with { Huella = vista.Huella, Id = Guid.NewGuid() }));
        Assert.Equal((1000L, 1050L, 1050L, 1000L), (await MenudeoAsync(c, azul), await MenudeoAsync(c, rojo), await MenudeoAsync(c, rubi), await MenudeoAsync(c, inactivo)));
    }

    [Fact]
    public async Task El_redondeo_no_queda_al_reves_y_lo_que_quedaria_en_cero_o_no_tiene_precio_no_cambia()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var goma = await ProductoAsync(c, $"Goma {ficha}", departamento, 500);
        var clip = await ProductoAsync(c, $"Clip {ficha}", departamento, 510);
        var tijera = await ProductoAsync(c, $"Tijera {ficha}", departamento, 5000);
        var sinPrecio = await ProductoAsync(c, $"Sin precio {ficha}", departamento, 700);
        await using (var db = servicio.Base())
        {
            db.Precios.RemoveRange(db.Precios.Where(p => p.VarianteId == sinPrecio.Variantes[0].Id));
            await db.SaveChangesAsync();
        }

        // 2 % más: $5.00 y $5.10 se quedan como estaban (el más cercano a los 50 centavos bajaría el de $5.10); $50.00, a $51.00.
        var subir = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Departamento,
            ComoCambiarPrecios.SubirPorcentaje, 200, departamento)));
        Assert.Equal([(tijera.Id!.Value, 5000L, 5100L)], subir.Cambian.Select(x => (x.ProductoId, x.AntesCentavos, x.DespuesCentavos)));
        Assert.Equal([(clip.Id!.Value, PorQueNoCambia.QuedaIgual), (goma.Id!.Value, PorQueNoCambia.QuedaIgual), (sinPrecio.Id!.Value, PorQueNoCambia.SinPrecio)],
            subir.NoCambian.Select(x => (x.ProductoId, x.PorQue)));

        // $6.00 menos: lo de $5.00 quedaría en $0 o menos y no cambia; lo demás, sí.
        var bajar = Pedido(c, c.Encargado, CualesProductos.Departamento, ComoCambiarPrecios.BajarMonto, 600, departamento);
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, bajar));
        Assert.Equal([(tijera.Id!.Value, 4400L)], vista.Cambian.Select(x => (x.ProductoId, x.DespuesCentavos)));
        Assert.Equal([(clip.Id!.Value, PorQueNoCambia.QuedariaEnCero), (goma.Id!.Value, PorQueNoCambia.QuedariaEnCero), (sinPrecio.Id!.Value, PorQueNoCambia.SinPrecio)],
            vista.NoCambian.Select(x => (x.ProductoId, x.PorQue)));
        await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, bajar with { Huella = vista.Huella, Id = Guid.NewGuid() }));
        Assert.Equal((500L, 510L, 4400L), (await MenudeoAsync(c, goma), await MenudeoAsync(c, clip), await MenudeoAsync(c, tijera)));
    }

    [Fact]
    public async Task El_margen_y_el_aviso_de_debajo_del_minimo_solo_para_quien_ve_la_utilidad()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var carpeta = await ProductoAsync(c, $"Carpeta {ficha}", departamento, 1000);
        var folder = await ProductoAsync(c, $"Folder {ficha}", departamento, 1000);
        var sobre = await ProductoAsync(c, $"Sobre {ficha}", departamento, 1000);
        await using (var db = servicio.Base())
        {
            // El mínimo del departamento, 30 %; y su costo: diez de cada uno, a $8.00 y a $4.00 (el sobre sin costo todavía).
            (await db.Departamentos.SingleAsync(d => d.Id == departamento)).MargenMinimoCentesimas = 3000;
            await db.SaveChangesAsync();
            var inventario = new RegistroInventario(db);
            foreach (var (producto, costo) in new[] { (carpeta, 8000L), (folder, 4000L) })
            {
                var variante = await db.Variantes.SingleAsync(v => v.Id == producto.Variantes[0].Id);
                await inventario.RegistrarAsync(Kardex.Entrada(c.Sucursal, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(10), costo, DateTime.UtcNow));
            }
        }
        SolicitudPreciosDeVarios Subir(SesionDto quien) =>
            Pedido(c, quien, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento);

        var delDueno = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Subir(c.Dueno)));
        var delEncargado = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Subir(c.Encargado)));

        Assert.True(delDueno.ConMargen);
        // $10.00 a $11.00: con costo de $8.00, de 20 % a 27.3 %, debajo del mínimo de 30 %; con $4.00, de 60 % a 63.6 %.
        Assert.Equal([(carpeta.Id!.Value, (int?)2000, (int?)2727, (int?)3000), (folder.Id!.Value, 6000, 6364, 3000), (sobre.Id!.Value, null, null, null)],
            delDueno.Cambian.Select(x => (x.ProductoId, x.MargenAntesCentesimas, x.MargenDespuesCentesimas, x.MargenMinimoCentesimas)));
        Assert.False(delEncargado.ConMargen);
        Assert.All(delEncargado.Cambian, x => Assert.Equal((null, null, null), (x.MargenAntesCentesimas, x.MargenDespuesCentesimas, x.MargenMinimoCentesimas)));
        Assert.Equal(delDueno.Huella, delEncargado.Huella);
    }

    [Fact]
    public async Task Sin_productos_precios_y_etiquetas_no_se_ve_ni_se_aplica()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var goma = await ProductoAsync(c, $"Goma {Ficha()}", departamento, 1000);
        var pedido = Pedido(c, c.Cajero, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento);
        var id = Guid.NewGuid();

        var vista = await VistaAsync(c, pedido);
        var aplicar = await AplicarAsync(c, pedido with { Huella = "lo que sea", Id = id });

        Assert.Equal(HttpStatusCode.Forbidden, vista.StatusCode);
        Assert.Contains("«Productos, precios y etiquetas»", (await vista.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(HttpStatusCode.Forbidden, aplicar.StatusCode);
        Assert.Equal(1000, await MenudeoAsync(c, goma));
        await using var db = servicio.Base();
        Assert.False(await db.Bitacora.AnyAsync(b => b.Id == id));
        Assert.True(await db.Bitacora.AnyAsync(b => b.UsuarioId == c.Cajero.UsuarioId && b.Accion == AccionControlada.EditarCatalogo
            && b.Resultado == ResultadoBitacora.Negado));
    }

    /// <summary>
    /// Un lápiz como los que llegan de eleventa: menudeo de $15.00, la promoción de eleventa desde 12 a $12.00 y una escala de
    /// aquí desde 24 a $11.00, con su ID de eleventa (y la promoción, como la anota la lectura).
    /// </summary>
    private async Task<FichaProductoDto> DeEleventaAsync(Caja c, string nombre, Guid departamento)
    {
        var ficha = await ProductoAsync(c, nombre, departamento, 1500, escalas: [(12, 1200), (24, 1100)]);
        var variante = ficha.Variantes[0].Id!.Value;
        var idEleventa = Random.Shared.Next(100_000, 999_999).ToString();
        await using var db = servicio.Base();
        var promocion = await db.Precios.SingleAsync(p => p.VarianteId == variante && p.CantidadMinima == Cantidad.Unidades(12));
        db.MapeosImportacion.AddRange(
            new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = idEleventa, IdNuevo = variante },
            new MapeoImportacion { Origen = "eleventa", Entidad = "Promocion", IdOrigen = $"{idEleventa}@12000", IdNuevo = promocion.Id });
        await db.SaveChangesAsync();
        return ficha;
    }

    [Fact]
    public async Task En_el_piloto_el_menudeo_y_las_promociones_de_eleventa_no_cambian_y_su_mayoreo_de_aqui_y_lo_de_aqui_si()
    {
        var normal = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(normal);
        var ficha = Ficha();
        var deEleventa = await DeEleventaAsync(normal, $"Lápiz de eleventa {ficha}", departamento);
        var deAqui = await ProductoAsync(normal, $"Lápiz de aquí {ficha}", departamento, 1500, escalas: [(24, 1100)]);
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(piloto.CreateClient());
        SolicitudPreciosDeVarios Subir(SesionDto quien, bool conMayoreo) =>
            Pedido(c, quien, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento, conMayoreo: conMayoreo);

        // Sin el mayoreo, lo de eleventa no cambia: su precio se cambia allá. Lo dado de alta aquí, sí.
        var sinMayoreo = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Subir(c.Encargado, conMayoreo: false)));
        Assert.Equal([(deAqui.Id!.Value, 1500L, 1650L)], sinMayoreo.Cambian.Select(x => (x.ProductoId, x.AntesCentavos, x.DespuesCentavos)));
        Assert.Equal([(deEleventa.Id!.Value, PorQueNoCambia.EnEleventa)], sinMayoreo.NoCambian.Select(x => (x.ProductoId, x.PorQue)));
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, sinMayoreo.EnEleventa);

        // Con el mayoreo, de lo de eleventa cambia solo su escala de aquí, en la proporción que tendría su menudeo, pero más
        // barata que la promoción de eleventa desde 12 ($12.00), que se queda: $12.10 no cabe, así que al paso de abajo, $11.50.
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Subir(c.Encargado, conMayoreo: true)));
        var suyo = vista.Cambian.Single(x => x.ProductoId == deEleventa.Id);
        Assert.Equal((1500L, 1500L, true, true), (suyo.AntesCentavos, suyo.DespuesCentavos, suyo.MenudeoEnEleventa, suyo.MayoreoAjustado));
        Assert.Equal([("desde 24", 1100L, 1150L)], suyo.Mayoreo.Select(m => (m.Cual, m.AntesCentavos, m.DespuesCentavos)));
        Assert.Equal((1650L, false), vista.Cambian.Where(x => x.ProductoId == deAqui.Id).Select(x => (x.DespuesCentavos, x.MenudeoEnEleventa)).Single());
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, vista.EnEleventa);
        // El permiso va primero: el cajero oye lo de siempre.
        Assert.Equal(HttpStatusCode.Forbidden, (await VistaAsync(c, Subir(c.Cajero, conMayoreo: true))).StatusCode);

        var hecho = await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, Subir(c.Encargado, conMayoreo: true) with { Huella = vista.Huella, Id = Guid.NewGuid() }));
        Assert.Equal((2, 3), (hecho.Productos, hecho.Precios));
        Assert.Equal([(null, 1L, 1500L), (null, 12L, 1200L), (null, 24L, 1150L)], await PreciosAsync(c, deEleventa));
        Assert.Equal([(null, 1L, 1650L), (null, 24L, 1200L)], await PreciosAsync(c, deAqui));

        // Fuera del piloto, el mismo producto cambia completo.
        var fuera = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(normal, Subir(normal.Encargado, conMayoreo: true)));
        var completo = fuera.Cambian.Single(x => x.ProductoId == deEleventa.Id);
        Assert.Equal((1650L, false), (completo.DespuesCentavos, completo.MenudeoEnEleventa));
        Assert.Equal([("desde 12", 1200L, 1300L), ("desde 24", 1150L, 1250L)], completo.Mayoreo.Select(m => (m.Cual, m.AntesCentavos, m.DespuesCentavos)));
        Assert.Null(fuera.EnEleventa);
    }

    [Fact]
    public async Task Si_algo_cambio_desde_la_vista_no_se_aplica_y_pedirlo_otra_vez_no_lo_aplica_dos_veces()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var regla = await ProductoAsync(c, $"Regla {ficha}", departamento, 1000);
        var compas = await ProductoAsync(c, $"Compás {ficha}", departamento, 2000);
        var pedido = Pedido(c, c.Encargado, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento);
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, pedido));

        // Mientras, alguien cambia el precio del compás en su ficha: lo que se vio ya no es lo que quedaría.
        var delCompas = await FichaAsync(c, compas);
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            delCompas with { Variantes = [delCompas.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, 2100)] }] })));
        Assert.Equal(RegistroPreciosDeVarios.CambioDesdeLaVista,
            await RechazoAsync(await AplicarAsync(c, pedido with { Huella = vista.Huella, Id = Guid.NewGuid() })));
        Assert.Equal((1000L, 2100L), (await MenudeoAsync(c, regla), await MenudeoAsync(c, compas)));
        // Sin la vista, tampoco.
        Assert.Equal("Primero ve cómo quedan.", await RechazoAsync(await AplicarAsync(c, pedido with { Id = Guid.NewGuid() })));

        // Con la vista de ahora sí; y si la respuesta se perdió y se pide otra vez con el mismo Id, no se aplica dos veces.
        var otra = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, pedido));
        var id = Guid.NewGuid();
        var hecho = await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, pedido with { Huella = otra.Huella, Id = id }));
        var otraVez = await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, pedido with { Huella = otra.Huella, Id = id }));

        Assert.Equal((2, false), (hecho.Productos, hecho.YaSeHabiaAplicado));
        Assert.True(otraVez.YaSeHabiaAplicado);
        Assert.Equal(hecho.Bitacora, otraVez.Bitacora);
        Assert.Equal((1100L, 2300L), (await MenudeoAsync(c, regla), await MenudeoAsync(c, compas)));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Bitacora.CountAsync(b => b.Id == id));
    }

    /// <summary>Lo que cobra la caja ahora (con sus precios de la base) por tantas piezas, o tantos paquetes de ese nombre.</summary>
    private async Task<long> CobroAsync(Caja c, FichaProductoDto producto, int cuantos, string? paquete = null)
    {
        await using var db = servicio.Base();
        var variante = producto.Variantes[0].Id!.Value;
        var precios = await db.Precios.AsNoTracking().Where(p => p.VarianteId == variante).ToListAsync();
        var presentacion = await db.Presentaciones.AsNoTracking()
            .SingleAsync(p => p.VarianteId == variante && (paquete == null ? p.EsBase : p.Nombre == paquete));
        return CalculoPrecio.Calcular(precios, c.Sucursal, presentacion, Cantidad.Piezas(cuantos))!.ImporteCentavos;
    }

    [Fact]
    public async Task El_mayoreo_nunca_queda_igual_o_mas_caro_que_el_menudeo()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        // Un precio fijo de $10.90 con el mayoreo, al peso: $20.00 con desde 12 a $19.30.
        var fijo = await ProductoAsync(c, $"Fijo {ficha}", departamento, 2000, escalas: [(12, 1930)]);
        // Bajar 20 % sin el mayoreo: $10.00 con desde 12 a $9.00; y uno sin mayoreo, que sí cambia.
        var conEscala = await ProductoAsync(c, $"Con escala {ficha}", departamento, 1000, escalas: [(12, 900)]);
        var sinEscala = await ProductoAsync(c, $"Sin escala {ficha}", departamento, 1000);
        // Subir 2 % a los 50 centavos: $10.00 con desde 12 a $9.80 y el par a $19.90.
        var poco = await ProductoAsync(c, $"Poco {ficha}", departamento, 1000, escalas: [(12, 980)], paquete: ("Par", 2, 1990));
        SolicitudPreciosDeVarios Marcados(FichaProductoDto[] productos, ComoCambiarPrecios como, long valor, bool conMayoreo, RedondeoDePrecios redondeo) =>
            Pedido(c, c.Encargado, CualesProductos.Marcados, como, valor, productos: productos.Select(p => p.Id!.Value).ToList(), conMayoreo: conMayoreo,
                redondeo: redondeo);

        // El precio fijo: la escala, que al peso quedaría en $11.00 (más que el menudeo de $10.90), queda al peso de abajo.
        var aFijo = Marcados([fijo], ComoCambiarPrecios.PrecioFijo, 1090, conMayoreo: true, RedondeoDePrecios.UnPeso);
        var vistaFijo = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, aFijo));
        var suyo = vistaFijo.Cambian.Single();
        Assert.Equal((2000L, 1090L, true), (suyo.AntesCentavos, suyo.DespuesCentavos, suyo.MayoreoAjustado));
        Assert.Equal([("desde 12", 1930L, 1000L)], suyo.Mayoreo.Select(m => (m.Cual, m.AntesCentavos, m.DespuesCentavos)));
        await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, aFijo with { Huella = vistaFijo.Huella, Id = Guid.NewGuid() }));
        Assert.Equal([(null, 1L, 1090L), (null, 12L, 1000L)], await PreciosAsync(c, fijo));
        Assert.Equal((11 * 1090L, 12 * 1000L), (await CobroAsync(c, fijo, 11), await CobroAsync(c, fijo, 12)));

        // Sin el mayoreo, el que tiene escala no cambia (su escala quedaría más cara) y lo dice; el otro, sí.
        var bajar = Marcados([conEscala, sinEscala], ComoCambiarPrecios.BajarPorcentaje, 2000, conMayoreo: false, RedondeoDePrecios.CincuentaCentavos);
        var vistaBajar = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, bajar));
        Assert.Equal([(sinEscala.Id!.Value, 800L)], vistaBajar.Cambian.Select(x => (x.ProductoId, x.DespuesCentavos)));
        Assert.Equal([(conEscala.Id!.Value, PorQueNoCambia.MayoreoMasCaro)], vistaBajar.NoCambian.Select(x => (x.ProductoId, x.PorQue)));
        await Leer<PreciosDeVariosAplicadosDto>(await AplicarAsync(c, bajar with { Huella = vistaBajar.Huella, Id = Guid.NewGuid() }));
        Assert.Equal([(null, 1L, 1000L), (null, 12L, 900L)], await PreciosAsync(c, conEscala));
        Assert.Equal(800, await MenudeoAsync(c, sinEscala));

        // Subir poco: el menudeo se queda en $10.00, y la escala y el par no se emparejan con él (ni pasan): todo queda igual.
        var subir = Marcados([poco], ComoCambiarPrecios.SubirPorcentaje, 200, conMayoreo: true, RedondeoDePrecios.CincuentaCentavos);
        var vistaSubir = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, subir));
        Assert.Empty(vistaSubir.Cambian);
        Assert.Equal([(poco.Id!.Value, PorQueNoCambia.QuedaIgual)], vistaSubir.NoCambian.Select(x => (x.ProductoId, x.PorQue)));
        Assert.Equal([(null, 1L, 1000L), (null, 12L, 980L), ("Par", 1L, 1990L)], await PreciosAsync(c, poco));
        Assert.Equal((12 * 980L, 1990L), (await CobroAsync(c, poco, 12), await CobroAsync(c, poco, 1, "Par")));
    }

    [Fact]
    public async Task En_el_piloto_el_mayoreo_de_aqui_no_pasa_de_la_promocion_de_eleventa()
    {
        var normal = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(normal);
        // Menudeo de eleventa a $15.00, su promoción desde 12 a $14.50 y la escala de aquí desde 24 a $14.00.
        var lapiz = await ProductoAsync(normal, $"Lápiz {Ficha()}", departamento, 1500, escalas: [(12, 1450), (24, 1400)]);
        var variante = lapiz.Variantes[0].Id!.Value;
        var idEleventa = Random.Shared.Next(100_000, 999_999).ToString();
        await using (var db = servicio.Base())
        {
            var promocion = await db.Precios.SingleAsync(p => p.VarianteId == variante && p.CantidadMinima == Cantidad.Unidades(12));
            db.MapeosImportacion.AddRange(
                new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = idEleventa, IdNuevo = variante },
                new MapeoImportacion { Origen = "eleventa", Entidad = "Promocion", IdOrigen = $"{idEleventa}@12000", IdNuevo = promocion.Id });
            await db.SaveChangesAsync();
        }
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(piloto.CreateClient());

        // Subir 10 %, con el mayoreo y a los 50 centavos (lo de siempre): desde 24 no sube a $15.50 (más que la promoción y que
        // el menudeo de eleventa, que se quedan): se queda, y el producto dice que su precio se cambia allá.
        var vista = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c,
            Pedido(c, c.Encargado, CualesProductos.Departamento, ComoCambiarPrecios.SubirPorcentaje, 1000, departamento)));

        Assert.Empty(vista.Cambian);
        Assert.Equal([(lapiz.Id!.Value, PorQueNoCambia.EnEleventa)], vista.NoCambian.Select(x => (x.ProductoId, x.PorQue)));
        Assert.Equal([(null, 1L, 1500L), (null, 12L, 1450L), (null, 24L, 1400L)], await PreciosAsync(c, lapiz));
        Assert.Equal(24 * 1400L, await CobroAsync(c, lapiz, 24));
    }

    [Fact]
    public async Task Lo_que_coincide_con_el_departamento_de_la_lista_son_solo_los_suyos()
    {
        var c = await PrepararAsync();
        var (escolar, nombreEscolar) = await DepartamentoAsync(c);
        var (oficina, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var deEscolar = await ProductoAsync(c, $"Folder {ficha}", escolar, 1000);
        var deOficina = await ProductoAsync(c, $"Folder {ficha}", oficina, 1000);

        var todos = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Busqueda,
            ComoCambiarPrecios.SubirPorcentaje, 1000, texto: ficha)));
        var soloEscolar = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Pedido(c, c.Encargado, CualesProductos.Busqueda,
            ComoCambiarPrecios.SubirPorcentaje, 1000, escolar, texto: ficha)));

        Assert.Equal(new[] { deEscolar.Id!.Value, deOficina.Id!.Value }.Order(), todos.Cambian.Select(x => x.ProductoId).Order());
        Assert.Equal([deEscolar.Id!.Value], soloEscolar.Cambian.Select(x => x.ProductoId));
        Assert.Equal($"lo que coincide con «{ficha}» en «{nombreEscolar}»", soloEscolar.Cuales);
    }

    [Fact]
    public async Task Lo_que_no_tiene_sentido_dice_que_corregir()
    {
        var c = await PrepararAsync();
        var (departamento, _) = await DepartamentoAsync(c);
        async Task<string> VistaRechazadaAsync(SolicitudPreciosDeVarios pedido) => await RechazoAsync(await VistaAsync(c, pedido));
        SolicitudPreciosDeVarios Subir(CualesProductos cuales, Guid? depto = null, string? marca = null, string? texto = null, IReadOnlyList<Guid>? productos = null) =>
            Pedido(c, c.Encargado, cuales, ComoCambiarPrecios.SubirPorcentaje, 1000, depto, marca, texto, productos);

        Assert.Equal("Escribe el porcentaje: más de cero.", await VistaRechazadaAsync(Subir(CualesProductos.Departamento, departamento) with { Valor = 0 }));
        Assert.Equal("No se puede bajar 100 % o más: los precios quedarían en $0.",
            await VistaRechazadaAsync(Subir(CualesProductos.Departamento, departamento) with { Como = ComoCambiarPrecios.BajarPorcentaje, Valor = 10_000 }));
        Assert.Equal("Escribe el precio: más de cero.",
            await VistaRechazadaAsync(Subir(CualesProductos.Departamento, departamento) with { Como = ComoCambiarPrecios.PrecioFijo, Valor = -500 }));
        Assert.Equal("Elige el departamento.", await VistaRechazadaAsync(Subir(CualesProductos.Departamento)));
        Assert.Equal("No hay una marca «Ninguna así».", await VistaRechazadaAsync(Subir(CualesProductos.Marca, marca: " Ninguna así ")));
        Assert.Equal("Escribe qué buscar: parte del nombre, la marca, el departamento o un código.", await VistaRechazadaAsync(Subir(CualesProductos.Busqueda, texto: "  ")));
        Assert.Equal("Marca en la lista los productos que vas a cambiar.", await VistaRechazadaAsync(Subir(CualesProductos.Marcados, productos: [])));
        Assert.Equal("Son 1,001 productos: se cambian hasta 1,000 a la vez. Elige un departamento, una marca o una búsqueda más chica.",
            await VistaRechazadaAsync(Subir(CualesProductos.Marcados, productos: Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToList())));

        // Un departamento sin productos: la vista sale vacía y no hay nada que aplicar.
        var vacia = await Leer<VistaPreciosDeVariosDto>(await VistaAsync(c, Subir(CualesProductos.Departamento, departamento)));
        Assert.Empty(vacia.Cambian);
        Assert.Empty(vacia.NoCambian);
        Assert.Equal("Con esa regla no cambia ningún precio.", await RechazoAsync(await AplicarAsync(c,
            Subir(CualesProductos.Departamento, departamento) with { Huella = vacia.Huella, Id = Guid.NewGuid() })));
    }
}
