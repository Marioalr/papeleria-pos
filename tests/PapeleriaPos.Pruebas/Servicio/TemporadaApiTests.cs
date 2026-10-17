using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using static PapeleriaPos.Pruebas.Servicio.ServicioDeTemporadas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Empezar y terminar la temporada, su hoja, las tarjetas de precio y lo más preguntado por la API (entrega 13, parte 3): la
/// lista del año pasado con el precio de entonces (de su historial o de lo que se cobró), el costo y el sugerido solo para quien
/// ve la utilidad, los precios que se confirman juntos sin dejar el mayoreo igual o más caro, las tarjetas que hay que volver a
/// imprimir, el resumen al terminarla (con el conteo, y sin él en el piloto con eleventa) y las consultas del verificador.
/// </summary>
public sealed class TemporadaApiTests(ServicioDeTemporadas servicio) : IClassFixture<ServicioDeTemporadas>
{
    private sealed record Caja(HttpClient Http, Guid Sucursal, Guid CajaId, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> RechazoAsync(HttpResponseMessage r, HttpStatusCode codigo = HttpStatusCode.BadRequest)
    {
        Assert.Equal(codigo, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private static async Task<Caja> PrepararAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja.SucursalId, caja.Id, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private Task<Caja> CajaAsync() => PrepararAsync(servicio.Fabrica.CreateClient());

    /// <summary>Una temporada nueva con sus fechas y su «Mesa de la entrada» con esos productos, dada de alta por el dueño.</summary>
    private static Task<Guid> TemporadaAsync(Caja c, DiaDelAnio desde, DiaDelAnio hasta, params Guid[] productos) =>
        ServicioDeTemporadas.TemporadaAsync(c.Http, c.Dueno.UsuarioId, c.Sucursal, desde, hasta, productos);

    private static Task<TemporadaParaEmpezarDto> EmpezarAsync(Caja c, Guid temporada, SesionDto quien) =>
        c.Http.GetFromJsonAsync<TemporadaParaEmpezarDto>($"api/temporadas/{temporada}/empezar?sucursalId={c.Sucursal}&usuarioId={quien.UsuarioId}")!;

    [Fact]
    public async Task La_lista_del_anio_pasado_trae_el_precio_de_entonces_y_el_costo_y_el_sugerido_solo_para_quien_ve_la_utilidad()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        // Papel picado: $35 desde 2039; antes de Navidad 2039 subió a $40 y al terminar se rebajó a $20 para sacarlo.
        var papel = await servicio.ProductoAsync(departamento, "Papel picado navideño", 3500, El(2039, 10, 1), codigo: Codigo("PAP"));
        await servicio.CambiarPrecioAsync(papel, 4000, El(2039, 11, 1));
        await servicio.CambiarPrecioAsync(papel, 2000, El(2040, 1, 5));
        // La esfera no cambió desde que se dio de alta.
        var esfera = await servicio.ProductoAsync(departamento, "Esfera roja chica", 3000, El(2039, 10, 1));
        // La corona se dio de alta en 2040 (lo de antes venía de eleventa): su precio sale de lo que se cobró.
        var corona = await servicio.ProductoAsync(departamento, "Corona de pino", 2200, El(2040, 6, 1));
        // La nueva no estaba el año pasado.
        var nueva = await servicio.ProductoAsync(departamento, "Guirnalda nueva", 5000, El(2040, 6, 1));
        await using (var db = servicio.Base())
        {
            var venta = new VentaHistorica { SucursalId = c.Sucursal, FechaUtc = El(2039, 12, 10).UtcDateTime, FolioOrigen = $"H{Guid.NewGuid():N}"[..10], TotalCentavos = 7400 };
            venta.Lineas.AddRange(
            [
                new VentaHistoricaLinea { VarianteId = corona, Cantidad = Cantidad.Piezas(1), PrecioCentavos = 2500, ImporteCentavos = 2500 },
                new VentaHistoricaLinea { VarianteId = corona, Cantidad = Cantidad.Piezas(1), PrecioCentavos = 2500, ImporteCentavos = 2500 },
                new VentaHistoricaLinea { VarianteId = corona, Cantidad = Cantidad.Piezas(1), PrecioCentavos = 2400, ImporteCentavos = 2400 },
            ]);
            db.VentasHistoricas.Add(venta);
            // El costo de hoy: el de la última compra del papel ($16), y de la esfera el promedio de lo que hay ($25).
            var proveedor = new Proveedor { Nombre = $"Proveedor {Guid.NewGuid():N}"[..20] };
            db.Proveedores.Add(proveedor);
            db.ProveedoresVariantes.Add(new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = papel, UltimoCostoCentavos = 1600, UltimaCompraUtc = El(2040, 9, 1).UtcDateTime });
            db.FotosProducto.Add(new FotoProducto { VarianteId = esfera, Datos = [1, 2, 3], Huella = "0123456789abcdef", Ancho = 1, Alto = 1 });
            await db.SaveChangesAsync();
        }
        await using (var db = servicio.Base(El(2040, 9, 1)))
        {
            var inventario = new RegistroInventario(db);
            var variante = await db.Variantes.SingleAsync(v => v.Id == esfera);
            await inventario.RegistrarAsync(Kardex.Entrada(c.Sucursal, variante, TipoMovimiento.Compra, Cantidad.Piezas(4), 10_000, El(2040, 9, 1).UtcDateTime));
        }
        var temporada = await TemporadaAsync(c, Del20DeNoviembre, Al1DeEnero, papel, esfera, corona, nueva);

        var delEncargado = await EmpezarAsync(c, temporada, c.Encargado);
        var delDueno = await EmpezarAsync(c, temporada, c.Dueno);

        // La que sigue es la de 2040, con la lista de la de 2039.
        Assert.Equal((new DateOnly(2040, 11, 20), new DateOnly(2041, 1, 1)), (delEncargado.Inicio, delEncargado.Fin));
        Assert.Equal((new DateOnly(2039, 11, 20), new DateOnly(2040, 1, 1)), (delEncargado.InicioAnterior, delEncargado.FinAnterior));
        Assert.False(delEncargado.Corre);
        Assert.Null(delEncargado.PreciosConfirmados);
        Assert.Equal([papel, esfera, corona, nueva], delEncargado.Productos.Select(p => p.VarianteId));
        Assert.All(delEncargado.Productos, p => Assert.Equal("Mesa de la entrada", p.Lugar));
        // El de entonces: el papel a $40 (no los $20 de la rebaja), la esfera a $30, la corona a $25 (al que más se cobró).
        Assert.Equal([(4000L, "historial"), (3000L, "historial"), (2500L, "ventas")],
            delEncargado.Productos.Take(3).Select(p => (p.AnteriorCentavos!.Value, p.DeDonde!)));
        Assert.Null(delEncargado.Productos[3].AnteriorCentavos);
        Assert.Equal([2000L, 3000L, 2200L, 5000L], delEncargado.Productos.Select(p => p.PrecioCentavos!.Value));
        Assert.Equal([false, true, false, false], delEncargado.Productos.Select(p => p.TieneFoto));
        // El encargado no ve el costo ni lo que sale de él.
        Assert.False(delEncargado.VeCostos);
        Assert.All(delEncargado.Productos, p => Assert.True(p.CostoCentavos is null && p.MargenCentesimas is null && p.SugeridoCentavos is null));
        // El dueño sí: el papel con la última compra ($16) deja 20 % con $20; sugerido, los $40 del año pasado.
        Assert.True(delDueno.VeCostos);
        var papelDelDueno = delDueno.Productos[0];
        Assert.Equal((1600L, 2000, 3000, 4000L), (papelDelDueno.CostoCentavos!.Value, papelDelDueno.MargenCentesimas!.Value,
            papelDelDueno.MargenMinimoCentesimas!.Value, papelDelDueno.SugeridoCentavos!.Value));
        // La esfera, con el promedio de lo que hay ($25): con $30 deja 16.67 %; sugerido, el que deja el 30 %: $35.71 → $36.
        Assert.Equal((2500L, 1667, 3600L), (delDueno.Productos[1].CostoCentavos!.Value, delDueno.Productos[1].MargenCentesimas!.Value,
            delDueno.Productos[1].SugeridoCentavos!.Value));
        // Sin costo, sin sugerido.
        Assert.Null(delDueno.Productos[2].SugeridoCentavos);

        // La lista de temporadas la trae, con cuántos productos y cuántos sin foto.
        var temporadas = await c.Http.GetFromJsonAsync<TemporadasDto>($"api/temporadas?sucursalId={c.Sucursal}");
        var suya = temporadas!.Temporadas.Single(t => t.ExhibidorId == temporada);
        Assert.Equal((4, 3, new DateOnly(2040, 11, 20)), (suya.Productos, suya.SinFoto, suya.Inicio!.Value));
        // La que corre (Todos Santos, del 16 de septiembre al 5 de noviembre) sale antes que las que no corren.
        Assert.True(temporadas.Temporadas.TakeWhile(t => t.Corre).Any());
        Assert.All(temporadas.Temporadas.SkipWhile(t => t.Corre), t => Assert.False(t.Corre));
    }

    [Fact]
    public async Task Los_precios_se_confirman_juntos_sin_dejar_el_mayoreo_igual_o_mas_caro_y_dicen_que_tarjetas_reimprimir()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var vela = await servicio.ProductoAsync(departamento, "Vela de cera", 5000, El(2039, 10, 1), codigo: Codigo("VEL"), escalas: [(12, 4500)]);
        var copal = await servicio.ProductoAsync(departamento, "Copal en grano", 1000, El(2039, 10, 1), codigo: Codigo("COP"));
        var temporada = await TemporadaAsync(c, Del16DeSeptiembre, Al5DeNoviembre, vela, copal);
        // El copal ya tiene su tarjeta de precio, a $10.
        await Leer<TarjetasDto>(await c.Http.PostAsJsonAsync("api/tarjetas/impresas", new SolicitudTarjetasImpresas(c.Encargado.UsuarioId, c.Sucursal,
            [new TarjetaParaImprimirDto(copal, "Copal en grano", 1000, (await EmpezarAsync(c, temporada, c.Encargado)).Productos[1].Codigo!)])));

        // La vela a $44 quedaría abajo de su mayoreo ($45 desde 12): no se confirma nada, ni el copal.
        var abajo = await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios", new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal,
            [new PrecioDeTemporadaDto(vela, 5000, 4400), new PrecioDeTemporadaDto(copal, 1000, 1200)]));
        Assert.Contains("«Vela de cera» quedaría igual o más barato que su mayoreo (desde 12: $45.00)", await RechazoAsync(abajo));
        Assert.Equal((5000L, 1000L), (await servicio.MenudeoAsync(vela), await servicio.MenudeoAsync(copal)));
        // Igual al mayoreo, tampoco.
        Assert.Contains("mayoreo", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(vela, 5000, 4500)]))));
        // El cajero no confirma precios.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Cajero.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(copal, 1000, 1200)]))).StatusCode);

        var confirmados = await Leer<PreciosConfirmadosDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(vela, 5000, 5500), new PrecioDeTemporadaDto(copal, 1000, 1200)])));

        Assert.Equal((2, 1), (confirmados.Cambiaron, confirmados.ConTarjeta));
        Assert.Equal((5500L, 1200L), (await servicio.MenudeoAsync(vela), await servicio.MenudeoAsync(copal)));
        // Corre hoy: es la de 2040, confirmada por el encargado.
        Assert.True(confirmados.Temporada.Corre);
        Assert.Equal("Encargado de prueba confirmó los precios el 17/10/2040", confirmados.Temporada.PreciosConfirmados);
        Assert.Equal([5500L, 1200L], confirmados.Temporada.Productos.Select(p => p.PrecioCentavos!.Value));
        // La tarjeta del copal quedó por reimprimir.
        var tarjetas = await c.Http.GetFromJsonAsync<TarjetasDto>($"api/tarjetas?sucursalId={c.Sucursal}");
        var delCopal = Assert.Single(tarjetas!.PorReimprimir, t => t.VarianteId == copal);
        Assert.Equal((1000L, 1200L, "cambió el precio"), (delCopal.TarjetaCentavos!.Value, delCopal.PrecioCentavos!.Value, delCopal.PorQue!));
        // Una sola línea en la bitácora, con cuántos y cuáles; y el historial de cada precio.
        await using (var db = servicio.Base())
        {
            var renglon = await db.Bitacora.Where(b => b.Detalle.Contains("Confirmó los precios de la temporada")).OrderByDescending(b => b.FechaUtc).FirstAsync();
            Assert.Contains("2 productos, cambiaron 2: «Vela de cera» $50.00 → $55.00, «Copal en grano» $10.00 → $12.00", renglon.Detalle);
            Assert.Contains(await db.PreciosHistorial.Where(h => h.VarianteId == copal).ToListAsync(),
                h => h.PrecioAnteriorCentavos == 1000 && h.PrecioNuevoCentavos == 1200);
        }

        // Si la respuesta se perdió y se confirma otra vez lo mismo, no cambia nada ni falla.
        var otraVez = await Leer<PreciosConfirmadosDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(vela, 5000, 5500), new PrecioDeTemporadaDto(copal, 1000, 1200)])));
        Assert.Equal(0, otraVez.Cambiaron);
        // Con un precio de antes que ya no es el de hoy (otra caja lo cambió mientras), nada se confirma.
        Assert.Contains("el precio de «Copal en grano» cambió mientras tanto (ahora es $12.00)", await RechazoAsync(await c.Http.PostAsJsonAsync(
            $"api/temporadas/{temporada}/precios", new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(copal, 1000, 1300)]))));
        // Un producto que no está en la mesa, tampoco.
        Assert.Contains("ya no está en la mesa", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(Guid.NewGuid(), 1000, 1300)]))));
    }

    [Fact]
    public async Task Las_tarjetas_salen_con_codigo_y_precio_y_vuelven_a_reimprimir_si_cambia_el_precio_o_el_codigo()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var calculadora = await servicio.ProductoAsync(departamento, "Calculadora de tarjeta", 8500, El(2040, 1, 1), codigo: Codigo("CAL"));
        var sinCodigo = await servicio.ProductoAsync(departamento, "Compás sin código", 12000, El(2040, 1, 1));
        var sinPrecio = await servicio.ProductoAsync(departamento, "Agenda sin precio", 100, El(2040, 1, 1), codigo: Codigo("AGE"));
        await using (var db = servicio.Base())
        {
            db.Precios.RemoveRange(db.Precios.Where(p => p.VarianteId == sinPrecio));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.PostAsJsonAsync("api/tarjetas/preparar",
            new SolicitudPrepararTarjetas(c.Cajero.UsuarioId, c.Sucursal, [calculadora]))).StatusCode);
        var listas = await Leer<TarjetasParaImprimirDto>(await c.Http.PostAsJsonAsync("api/tarjetas/preparar",
            new SolicitudPrepararTarjetas(c.Encargado.UsuarioId, c.Sucursal, [sinCodigo, calculadora, sinPrecio])));

        // En el orden pedido; al que no tenía código se le dio uno interno; lo que no tiene precio no sale.
        Assert.Equal([sinCodigo, calculadora], listas.Tarjetas.Select(t => t.VarianteId));
        Assert.Equal((1, "Agenda sin precio"), (listas.CodigosNuevos, Assert.Single(listas.SinPrecio)));
        Assert.StartsWith("2", listas.Tarjetas[0].Codigo);
        Assert.Equal(13, listas.Tarjetas[0].Codigo.Length);
        Assert.Equal((12000L, 8500L), (listas.Tarjetas[0].PrecioCentavos, listas.Tarjetas[1].PrecioCentavos));

        var impresas = await Leer<TarjetasDto>(await c.Http.PostAsJsonAsync("api/tarjetas/impresas",
            new SolicitudTarjetasImpresas(c.Encargado.UsuarioId, c.Sucursal, listas.Tarjetas)));
        Assert.DoesNotContain(impresas.PorReimprimir, t => t.VarianteId == calculadora || t.VarianteId == sinCodigo);

        // Cambia el precio de la calculadora: su tarjeta sale por reimprimir; al imprimirla otra vez, ya no.
        await servicio.CambiarPrecioAsync(calculadora, 9000, ServicioDeTemporadas.Hoy);
        var porReimprimir = await c.Http.GetFromJsonAsync<TarjetasDto>($"api/tarjetas?sucursalId={c.Sucursal}");
        var suya = Assert.Single(porReimprimir!.PorReimprimir, t => t.VarianteId == calculadora);
        Assert.Equal((8500L, 9000L, "cambió el precio"), (suya.TarjetaCentavos!.Value, suya.PrecioCentavos!.Value, suya.PorQue!));
        Assert.Equal(new DateTime(2040, 10, 17, 12, 0, 0), suya.ImpresaLocal);
        var otraVez = await Leer<TarjetasDto>(await c.Http.PostAsJsonAsync("api/tarjetas/impresas", new SolicitudTarjetasImpresas(c.Encargado.UsuarioId, c.Sucursal,
            [new TarjetaParaImprimirDto(calculadora, "Calculadora de tarjeta", 9000, listas.Tarjetas[1].Codigo)])));
        Assert.DoesNotContain(otraVez.PorReimprimir, t => t.VarianteId == calculadora);

        // Si su código ya no es suyo (se quitó), escanear la tarjeta no daría el producto: también por reimprimir.
        await using (var db = servicio.Base())
        {
            db.CodigosBarras.RemoveRange(db.CodigosBarras.Where(x => x.Codigo == listas.Tarjetas[1].Codigo));
            await db.SaveChangesAsync();
        }
        Assert.Equal("su código cambió", (await c.Http.GetFromJsonAsync<TarjetasDto>($"api/tarjetas?sucursalId={c.Sucursal}"))!
            .PorReimprimir.Single(t => t.VarianteId == calculadora).PorQue);
        // «Ya no tiene»: se quita y deja de estar por reimprimir.
        var antes = otraVez.ConTarjeta;
        var sinElla = await Leer<TarjetasDto>(await c.Http.PostAsJsonAsync($"api/tarjetas/{calculadora}/quitar", new SolicitudQuitarTarjeta(c.Encargado.UsuarioId, c.Sucursal)));
        Assert.DoesNotContain(sinElla.PorReimprimir, t => t.VarianteId == calculadora);
        Assert.Equal(antes - 1, sinElla.ConTarjeta);
    }

    [Fact]
    public async Task El_verificador_cuenta_lo_que_se_pregunta_y_lo_mas_preguntado_es_lo_que_no_tiene_tarjeta()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var muchas = await servicio.ProductoAsync(departamento, "Memoria USB preguntada", 13900, El(2040, 1, 1), codigo: Codigo("USB"));
        var pocas = await servicio.ProductoAsync(departamento, "Compás preguntado", 12000, El(2040, 1, 1), codigo: Codigo("CMP"));
        var conTarjeta = await servicio.ProductoAsync(departamento, "Calculadora con tarjeta", 8500, El(2040, 1, 1), codigo: Codigo("CLT"));

        async Task Preguntar(Guid variante, int veces)
        {
            for (var i = 0; i < veces; i++)
                Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync("api/consultas", new SolicitudConsulta(c.Sucursal, variante, c.CajaId))).StatusCode);
        }
        await Preguntar(muchas, 3);
        await Preguntar(pocas, 1);
        await Preguntar(conTarjeta, 5);
        // Lo que ya no existe no cuenta (ni falla).
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync("api/consultas", new SolicitudConsulta(c.Sucursal, Guid.NewGuid()))).StatusCode);
        // Una de hace 100 días ya no cuenta.
        await using (var db = servicio.Base())
        {
            db.ConsultasDePrecio.Add(new ConsultaDePrecio { SucursalId = c.Sucursal, VarianteId = pocas, FechaUtc = ServicioDeTemporadas.Hoy.UtcDateTime.AddDays(-100) });
            await db.SaveChangesAsync();
        }
        await Leer<TarjetasDto>(await c.Http.PostAsJsonAsync("api/tarjetas/impresas", new SolicitudTarjetasImpresas(c.Encargado.UsuarioId, c.Sucursal,
            [new TarjetaParaImprimirDto(conTarjeta, "Calculadora con tarjeta", 8500, "CLT-PRUEBA")])));

        var preguntados = (await c.Http.GetFromJsonAsync<List<ProductoConTarjetaDto>>($"api/tarjetas/mas-preguntados?sucursalId={c.Sucursal}&maximo=200"))!
            .Where(p => p.VarianteId == muchas || p.VarianteId == pocas || p.VarianteId == conTarjeta).ToList();

        Assert.Equal([(muchas, 3), (pocas, 1)], preguntados.Select(p => (p.VarianteId, p.Veces)));
        Assert.All(preguntados, p => Assert.Null(p.TarjetaCentavos));
        // Cada consulta quedó con su caja y la hora del servicio.
        await using (var db = servicio.Base())
        {
            var suyas = await db.ConsultasDePrecio.Where(x => x.VarianteId == muchas).ToListAsync();
            Assert.All(suyas, x => Assert.Equal((c.CajaId, c.Sucursal, ServicioDeTemporadas.Hoy.UtcDateTime), (x.CajaId!.Value, x.SucursalId, x.FechaUtc)));
        }
    }

    [Fact]
    public async Task La_hoja_trae_cada_lugar_con_sus_productos_activos_su_foto_su_precio_y_un_codigo_para_cada_uno()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var conFoto = await servicio.ProductoAsync(departamento, "Calavera con foto", 3500, El(2040, 1, 1), codigo: Codigo("CAV"));
        var sinCodigo = await servicio.ProductoAsync(departamento, "Máscara sin código", 2800, El(2040, 1, 1));
        var apagado = await servicio.ProductoAsync(departamento, "Catrina desactivada", 2200, El(2040, 1, 1), codigo: Codigo("CAT"));
        await using (var db = servicio.Base())
        {
            db.FotosProducto.Add(new FotoProducto { VarianteId = conFoto, Datos = [0xFF, 0xD8, 0xFF], Huella = "fedcba9876543210", Ancho = 1, Alto = 1 });
            (await db.Variantes.SingleAsync(v => v.Id == apagado)).Activa = false;
            await db.SaveChangesAsync();
        }
        var temporada = await TemporadaAsync(c, Del16DeSeptiembre, Al5DeNoviembre, conFoto, sinCodigo, apagado);

        var hoja = await Leer<HojaDeTemporadaDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/hoja",
            new SolicitudHojaDeTemporada(c.Encargado.UsuarioId, c.Sucursal)));

        var lugar = Assert.Single(hoja.Lugares);
        Assert.Equal("Mesa de la entrada", lugar.Nombre);
        Assert.Equal(["Calavera con foto", "Máscara sin código"], lugar.Productos.Select(p => p.Nombre));
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF }), lugar.Productos[0].FotoBase64);
        Assert.Null(lugar.Productos[1].FotoBase64);
        Assert.Equal((3500L, 2800L), (lugar.Productos[0].PrecioCentavos!.Value, lugar.Productos[1].PrecioCentavos!.Value));
        Assert.Equal(1, hoja.CodigosNuevos);
        Assert.StartsWith("2", lugar.Productos[1].Codigo);
        Assert.Equal("del 16 de septiembre al 5 de noviembre", hoja.Fechas);
    }

    [Fact]
    public async Task Al_terminarla_queda_el_resumen_y_lo_contado_reemplaza_la_existencia_una_sola_vez()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var vela = await servicio.ProductoAsync(departamento, "Vela del resumen", 2000, El(2040, 1, 1), codigo: Codigo("VLR"));
        var copal = await servicio.ProductoAsync(departamento, "Copal que no se vendió", 1000, El(2040, 1, 1), codigo: Codigo("CPR"));
        // Antes de Todos Santos (16 de septiembre) había 20 velas; llegaron 10, se vendieron 8, se devolvió una y se mermó una.
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.InventarioInicial, 20, El(2040, 9, 1));
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.Compra, 10, El(2040, 9, 20));
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.Venta, 8, El(2040, 10, 1));
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.Devolucion, 1, El(2040, 10, 2));
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.Merma, 1, El(2040, 10, 5));
        await servicio.MovimientoAsync(c.Sucursal, copal, TipoMovimiento.InventarioInicial, 6, El(2040, 9, 1));
        var temporada = await TemporadaAsync(c, Del16DeSeptiembre, Al5DeNoviembre, vela, copal);

        var antes = await c.Http.GetFromJsonAsync<TemporadaParaTerminarDto>($"api/temporadas/{temporada}/terminar?sucursalId={c.Sucursal}");

        Assert.Equal((new DateOnly(2040, 9, 16), new DateOnly(2040, 11, 5), true), (antes!.Inicio, antes.Fin, antes.Corre));
        Assert.True(antes.SeCuenta);
        Assert.False(antes.AntesDelPos);
        Assert.Null(antes.Terminada);
        Assert.Equal([(30m, 7m, 22m), (6m, 0m, 6m)], antes.Productos.Select(p => (p.Inicial, p.Vendido, p.Quedo)));

        // El cajero no la termina; contar pide «Conteo completo de un departamento».
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Cajero.UsuarioId, c.Sucursal, []))).StatusCode);
        Assert.Contains("piezas enteras", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, [new ContadoDto(vela, 19_500)]))));

        // Sobraron 20 velas (el sistema decía 22); el copal no se contó y se queda como está.
        var terminada = await Leer<TemporadaParaTerminarDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, [new ContadoDto(vela, 20_000)])));

        Assert.Equal("Encargado de prueba la terminó y contó lo que sobró el 17/10/2040", terminada.Terminada);
        Assert.True(terminada.Contada);
        Assert.False(terminada.SeCuenta);
        Assert.Equal([(30m, 7m, 20m), (6m, 0m, 6m)], terminada.Productos.Select(p => (p.Inicial, p.Vendido, p.Quedo)));
        await using (var db = servicio.Base())
        {
            Assert.Equal(20m, (await db.Existencias.SingleAsync(e => e.SucursalId == c.Sucursal && e.VarianteId == vela)).Cantidad.EnUnidades);
            var conteo = await db.MovimientosInventario.SingleAsync(m => m.VarianteId == vela && m.DocumentoTipo == nameof(TemporadaDelAnio));
            Assert.Equal((TipoMovimiento.Conteo, -2m), (conteo.Tipo, conteo.Cantidad.EnUnidades));
            Assert.Contains(await db.Bitacora.Select(b => b.Detalle).ToListAsync(), d => d.StartsWith("Terminó la temporada", StringComparison.Ordinal)
                && d.Contains("2 productos, 1 se vendió; contó 1, 1 con diferencia"));
        }

        // Se termina una vez: lo guardado no cambia aunque después se venda.
        Assert.Contains("ya se terminó", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, []))));
        await servicio.MovimientoAsync(c.Sucursal, vela, TipoMovimiento.Venta, 5, ServicioDeTemporadas.Hoy);
        var guardada = await c.Http.GetFromJsonAsync<TemporadaParaTerminarDto>($"api/temporadas/{temporada}/terminar?sucursalId={c.Sucursal}");
        Assert.Equal([(30m, 7m, 20m), (6m, 0m, 6m)], guardada!.Productos.Select(p => (p.Inicial, p.Vendido, p.Quedo)));
        Assert.Equal(terminada.Terminada, guardada.Terminada);
    }

    [Fact]
    public async Task Una_temporada_que_empezo_antes_de_usar_el_pos_se_resume_desde_ese_dia()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        DateTime inicioDelPos;
        await using (var db = servicio.Base())
            inicioDelPos = (await db.MovimientosInventario.Where(m => m.SucursalId == c.Sucursal).OrderBy(m => m.FechaUtc).FirstAsync()).FechaUtc;
        var diaDelPos = DateOnly.FromDateTime(HoraMexico.ALocal(inicioDelPos));
        // Una temporada que empezó 20 días antes de que se empezara a usar este POS y termina 30 días después.
        var (desde, hasta) = (DiaDelAnio.De(diaDelPos.AddDays(-20)), DiaDelAnio.De(diaDelPos.AddDays(30)));
        var producto = await servicio.ProductoAsync(departamento, "Producto de la temporada del cambio", 1000, new DateTimeOffset(inicioDelPos.AddDays(1), TimeSpan.Zero));
        await servicio.MovimientoAsync(c.Sucursal, producto, TipoMovimiento.Compra, 10, new DateTimeOffset(inicioDelPos.AddDays(2), TimeSpan.Zero));
        await servicio.MovimientoAsync(c.Sucursal, producto, TipoMovimiento.Venta, 3, new DateTimeOffset(inicioDelPos.AddDays(5), TimeSpan.Zero));
        var temporada = await TemporadaAsync(c, desde, hasta, producto);
        var hoy = servicio.Reloj.Ahora;
        try
        {
            servicio.Reloj.Ahora = new DateTimeOffset(inicioDelPos.AddDays(10), TimeSpan.Zero);
            var resumen = await c.Http.GetFromJsonAsync<TemporadaParaTerminarDto>($"api/temporadas/{temporada}/terminar?sucursalId={c.Sucursal}");

            Assert.Equal((diaDelPos.AddDays(-20), diaDelPos), (resumen!.Inicio, resumen.Desde));
            Assert.False(resumen.AntesDelPos);
            Assert.True(resumen.SeCuenta);
            Assert.Equal((10m, 3m, 7m), (resumen.Productos[0].Inicial, resumen.Productos[0].Vendido, resumen.Productos[0].Quedo));
        }
        finally
        {
            servicio.Reloj.Ahora = hoy;
        }
    }

    [Fact]
    public async Task Una_temporada_de_antes_del_pos_no_tiene_resumen_y_sin_fechas_no_se_empieza()
    {
        var c = await CajaAsync();
        var departamento = await servicio.DepartamentoAsync();
        var producto = await servicio.ProductoAsync(departamento, "Producto de una temporada vieja", 1000, El(2040, 1, 1));
        // Del 1 al 2 de enero, preguntada en 2020: la última que empezó, la de 2020, terminó antes de cualquier movimiento.
        var vieja = await TemporadaAsync(c, new DiaDelAnio(1, 1), new DiaDelAnio(1, 2), producto);
        var hoy = servicio.Reloj.Ahora;
        try
        {
            servicio.Reloj.Ahora = new DateTimeOffset(2020, 3, 1, 18, 0, 0, TimeSpan.Zero);
            var antes = await c.Http.GetFromJsonAsync<TemporadaParaTerminarDto>($"api/temporadas/{vieja}/terminar?sucursalId={c.Sucursal}");
            Assert.True(antes!.AntesDelPos);
            Assert.False(antes.SeCuenta);
            Assert.Empty(antes.Productos);
            Assert.Contains("antes de que la tienda usara este POS", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{vieja}/terminar",
                new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, []))));
        }
        finally
        {
            servicio.Reloj.Ahora = hoy;
        }

        // Una sin fechas (como Primavera mientras no se definen) no se empieza ni se termina: lo dice.
        var nombre = $"Sin fechas {Guid.NewGuid():N}"[..20];
        var sinFechas = (await Leer<MostradorDto>(await c.Http.PostAsJsonAsync("api/mostrador",
            new SolicitudNuevoExhibidor(c.Dueno.UsuarioId, c.Sucursal, nombre, TipoExhibidor.Temporada)))).Exhibidores.Single(e => e.Nombre == nombre).Id;
        Assert.Contains("todavía no tiene fechas", await RechazoAsync(await c.Http.GetAsync(
            $"api/temporadas/{sinFechas}/empezar?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}")));
        // Un exhibidor no es una temporada.
        var mostrador = (await c.Http.GetFromJsonAsync<MostradorDto>("api/mostrador"))!.Exhibidores.First(e => e.Tipo == TipoExhibidor.Exhibidor).Id;
        Assert.Contains("es un exhibidor", await RechazoAsync(await c.Http.GetAsync($"api/temporadas/{mostrador}/terminar?sucursalId={c.Sucursal}")));
    }

    [Fact]
    public async Task En_el_piloto_lo_de_eleventa_se_confirma_como_esta_y_lo_que_sobro_no_se_cuenta_pero_el_resumen_si()
    {
        var piloto = servicio.Piloto();
        var c = await PrepararAsync(piloto.CreateClient());
        var departamento = await servicio.DepartamentoAsync();
        var deEleventa = await servicio.ProductoAsync(departamento, "Papel china de eleventa", 300, El(2040, 1, 1), codigo: Codigo("PCE"));
        var deAqui = await servicio.ProductoAsync(departamento, "Calcomanía de aquí", 1200, El(2040, 1, 1), codigo: Codigo("CDA"));
        await using (var db = servicio.Base())
        {
            db.MapeosImportacion.Add(new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = deEleventa });
            await db.SaveChangesAsync();
        }
        // Antes de la temporada había 50 pliegos; la caja 2 vendió 5 y la lectura de eleventa bajó 12 (lo que vendió la caja
        // principal) y subió 20 (lo que se compró allá).
        await servicio.MovimientoAsync(c.Sucursal, deEleventa, TipoMovimiento.InventarioInicial, 50, El(2040, 9, 1));
        await servicio.MovimientoAsync(c.Sucursal, deEleventa, TipoMovimiento.Venta, 5, El(2040, 9, 20));
        await servicio.MovimientoAsync(c.Sucursal, deEleventa, TipoMovimiento.Conteo, -12, El(2040, 9, 21), RegistroTemporadas.LecturaDeEleventa);
        await servicio.MovimientoAsync(c.Sucursal, deEleventa, TipoMovimiento.Conteo, 20, El(2040, 9, 22), RegistroTemporadas.LecturaDeEleventa);
        var temporada = await TemporadaAsync(c, Del16DeSeptiembre, Al5DeNoviembre, deEleventa, deAqui);

        var empezar = await EmpezarAsync(c, temporada, c.Encargado);
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, empezar.EnEleventa);
        Assert.Equal([true, false], empezar.Productos.Select(p => p.EnEleventa));
        // El de eleventa no cambia aquí: nada se confirma; como está, sí, y lo de aquí cambia.
        Assert.Contains("«Papel china de eleventa» es de eleventa", await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(deEleventa, 300, 350), new PrecioDeTemporadaDto(deAqui, 1200, 1500)]))));
        var confirmados = await Leer<PreciosConfirmadosDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/precios",
            new SolicitudConfirmarPrecios(c.Encargado.UsuarioId, c.Sucursal, [new PrecioDeTemporadaDto(deEleventa, 300, 300), new PrecioDeTemporadaDto(deAqui, 1200, 1500)])));
        Assert.Equal(1, confirmados.Cambiaron);
        Assert.Equal((300L, 1500L), (await servicio.MenudeoAsync(deEleventa), await servicio.MenudeoAsync(deAqui)));

        var terminar = await piloto.CreateClient().GetFromJsonAsync<TemporadaParaTerminarDto>($"api/temporadas/{temporada}/terminar?sucursalId={c.Sucursal}");
        Assert.False(terminar!.SeCuenta);
        Assert.Contains(TrastiendaEnElPiloto.InventarioEnEleventa, terminar.EnEleventa);
        // Lo vendido: lo de la caja 2 (5) y lo que bajó con la lectura (12); lo que subió con ella llegó.
        Assert.Equal((70m, 17m, 53m), (terminar.Productos[0].Inicial, terminar.Productos[0].Vendido, terminar.Productos[0].Quedo));
        // Contar no se hace en el piloto, aunque se mande.
        Assert.Contains(TrastiendaEnElPiloto.InventarioEnEleventa, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, [new ContadoDto(deEleventa, 50_000)]))));
        // Sin contar, sí se termina y queda el resumen.
        var terminada = await Leer<TemporadaParaTerminarDto>(await c.Http.PostAsJsonAsync($"api/temporadas/{temporada}/terminar",
            new SolicitudTerminarTemporada(c.Encargado.UsuarioId, c.Sucursal, [])));
        Assert.Equal("Encargado de prueba la terminó el 17/10/2040", terminada.Terminada);
        Assert.False(terminada.Contada);
    }

    [Fact]
    public void La_lectura_de_eleventa_anota_sus_ajustes_como_los_lee_el_resumen() =>
        Assert.Equal(SincronizacionEleventa.Documento, RegistroTemporadas.LecturaDeEleventa);
}
