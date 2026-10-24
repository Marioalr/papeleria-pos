using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los reportes del inventario como en eleventa (2026-10-17), desde el servicio: lo que vale a costo por departamento (solo con
/// «Ver costos, utilidad y margen»), los movimientos del kardex con sus filtros, su folio, quién y lo que quedó, por páginas, y
/// lo que está en su mínimo o debajo con cuánto pedir. También el mínimo y el máximo de la ficha y la regla de «Poca
/// existencia».
/// </summary>
public class ReportesInventarioApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Escenario(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public string Url => $"api/sucursales/{Caja.SucursalId}/inventario";
    }

    private async Task<Escenario> PrepararAsync(HttpClient? http = null)
    {
        http ??= servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        return new Escenario(http, caja,
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))));
    }

    /// <summary>
    /// Un producto nuevo de una pieza a $20, con su código, en el departamento que se diga (o el primero), y con
    /// <paramref name="existencia"/> piezas a <paramref name="costoPieza"/> como inventario inicial (como al importar).
    /// </summary>
    private async Task<FichaProductoDto> ProductoAsync(Escenario e, string nombre, int existencia, long costoPieza, Guid? departamento = null,
        MinimoYMaximoDto? minimoYMaximo = null)
    {
        var opciones = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var codigo = $"IR{Guid.NewGuid():N}"[..14];
        var ficha = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId,
            e.Caja.SucursalId, new FichaProductoDto(null, nombre, departamento ?? opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16,
                null, true, false,
                [
                    new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                        [new PrecioFichaDto(null, 1000, 2000)], MinimoYMaximo: minimoYMaximo),
                ]))));
        if (existencia != 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(e.Caja.SucursalId, variante, TipoMovimiento.InventarioInicial,
                Cantidad.Piezas(existencia), existencia * costoPieza, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        return ficha;
    }

    private async Task<TurnoDto> TurnoAsync(Escenario e)
    {
        var respuesta = await e.Http.GetAsync($"api/cajas/{e.Caja.Id}/turno");
        return respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await e.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(e.Caja.Id, e.Cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
    }

    /// <summary>El cajero vende <paramref name="piezas"/>: regresa el folio.</summary>
    private async Task<string> VenderAsync(Escenario e, FichaProductoDto producto, int piezas)
    {
        var turno = await TurnoAsync(e);
        var variante = producto.Variantes[0];
        var ticket = await Leer<TicketDto>(await e.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(e.Caja.Id, turno.Id, turno.CajeroId,
            [new LineaSolicitada(variante.Id, variante.Presentaciones[0].Id, piezas * 1000L)], [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)])));
        return ticket.Folio;
    }

    /// <summary>Ayer y hoy (en la hora de México): lo que se acaba de hacer queda adentro aunque sea medianoche.</summary>
    private static string Dias(string extra = "")
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        return $"&desde={hoy.AddDays(-1):yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}{extra}";
    }

    private static Task<HttpResponseMessage> MovimientosAsync(Escenario e, SesionDto quien, string filtros) =>
        e.Http.GetAsync($"{e.Url}/movimientos?usuarioId={quien.UsuarioId}{filtros}");

    // ── Lo que vale el inventario ───────────────────────────────────────────

    [Fact]
    public async Task Lo_que_vale_el_inventario_cuadra_con_las_existencias_por_departamento_y_solo_con_ver_costos()
    {
        var e = await PrepararAsync();
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[^1].Id;
        // En negativo (se vendió sin existencia), sin costo (existencia a $0) y en cero: los tres se cuentan.
        var negativo = await ProductoAsync(e, "Valor negativo de prueba", 0, 0, departamento);
        await VenderAsync(e, negativo, 2);
        var sinCosto = await ProductoAsync(e, "Valor sin costo de prueba", 5, 0, departamento);
        var enCero = await ProductoAsync(e, "Valor en cero de prueba", 0, 0, departamento);
        var caro = await ProductoAsync(e, "Valor caro de prueba", 3, 1_000_00, departamento);

        var delEncargado = await e.Http.GetAsync($"{e.Url}/valor?usuarioId={e.Encargado.UsuarioId}");
        var valor = await Leer<ValorInventarioDto>(await e.Http.GetAsync($"{e.Url}/valor?usuarioId={e.Dueno.UsuarioId}"));

        // Sin «Ver costos, utilidad y margen», el servicio no lo da.
        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
        Assert.Contains("«Ver costos, utilidad y margen»", (await delEncargado.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        // Lo mismo que dice la base: el valor del kardex de lo que tiene existencia y costo; lo negativo no resta.
        await using (var db = servicio.Base())
        {
            var mercancia = await (from v in db.Variantes join p in db.Productos on v.ProductoId equals p.Id
                                   where p.Tipo == TipoProducto.Mercancia
                                   select new { v.Id, p.DepartamentoId, Activo = v.Activa && p.Activo }).ToListAsync();
            var existencias = await db.Existencias.Where(x => x.SucursalId == e.Caja.SucursalId).ToDictionaryAsync(x => x.VarianteId);
            var renglones = mercancia.Select(v => (v.DepartamentoId, v.Activo, Existencia: existencias.GetValueOrDefault(v.Id)))
                .Select(r => (r.DepartamentoId, r.Activo, Cantidad: r.Existencia?.Cantidad.Milesimas ?? 0, Costo: r.Existencia?.CostoCentavos ?? 0))
                .Where(r => r.Activo || r.Cantidad != 0).ToList();
            Assert.Equal(renglones.Where(r => r.Cantidad > 0 && r.Costo > 0).Sum(r => r.Costo), valor.Total.ValorCentavos);
            Assert.Equal(renglones.Count(r => r.Cantidad > 0), valor.Total.ConExistencia);
            Assert.Equal(renglones.Count(r => r.Cantidad == 0), valor.Total.EnCero);
            Assert.Equal(renglones.Count(r => r.Cantidad < 0), valor.Total.Negativa);
            Assert.Equal(renglones.Count(r => r.Cantidad > 0 && r.Costo <= 0), valor.Total.SinCosto);
        }
        Assert.True(valor.Total.ValorCentavos > 0);
        Assert.Equal(valor.Total.ValorCentavos, valor.Departamentos.Sum(d => d.ValorCentavos));
        Assert.Equal(valor.Total.ConExistencia, valor.Departamentos.Sum(d => d.ConExistencia));
        Assert.Equal(valor.Total.Negativa, valor.Departamentos.Sum(d => d.Negativa));
        Assert.Equal(valor.Departamentos.Select(d => d.ValorCentavos).OrderByDescending(v => v), valor.Departamentos.Select(d => d.ValorCentavos));
        // Sin departamento, los productos de toda la tienda, de lo que más vale a lo que menos.
        Assert.Null(valor.DepartamentoId);
        Assert.Equal(valor.Productos.Select(p => p.ValorCentavos).OrderByDescending(v => v), valor.Productos.Select(p => p.ValorCentavos));
        Assert.Equal(valor.Total.ConExistencia, valor.TotalProductos);

        // De un departamento: lo suyo, con cada filtro, de 50 en 50 y sin repetirse.
        async Task<(ValorInventarioDto Primera, List<ProductoValorDto> Todos)> DeAsync(FiltroValor filtro)
        {
            var paginas = new List<ValorInventarioDto>();
            do
                paginas.Add(await Leer<ValorInventarioDto>(await e.Http.GetAsync(
                    $"{e.Url}/valor?usuarioId={e.Dueno.UsuarioId}&departamentoId={departamento}&filtro={filtro}&pagina={paginas.Count}")));
            while (paginas[^1].Productos.Count == paginas[^1].Tamano);
            return (paginas[0], paginas.SelectMany(p => p.Productos).ToList());
        }
        var (conExistencia, todosConExistencia) = await DeAsync(FiltroValor.ConExistencia);
        var (_, negativos) = await DeAsync(FiltroValor.Negativa);
        var (_, sinCostos) = await DeAsync(FiltroValor.SinCosto);
        var (_, enCeros) = await DeAsync(FiltroValor.EnCero);
        var grupo = conExistencia.Departamentos.Single(d => d.DepartamentoId == departamento);

        Assert.All(todosConExistencia, p => Assert.True(p.Existencia > 0));
        Assert.Equal(grupo.ConExistencia, conExistencia.TotalProductos);
        Assert.Equal(grupo.ConExistencia, todosConExistencia.Select(p => p.VarianteId).Distinct().Count());
        Assert.Equal(todosConExistencia.Select(p => p.ValorCentavos).OrderByDescending(v => v), todosConExistencia.Select(p => p.ValorCentavos));
        var elCaro = todosConExistencia.Single(p => p.VarianteId == caro.Variantes[0].Id);
        Assert.Equal((3m, 3_000_00L, 1_000_00m), (elCaro.Existencia, elCaro.ValorCentavos, elCaro.CostoPromedioCentavos!.Value));
        Assert.Equal(caro.Variantes[0].Presentaciones[0].Codigos[0].Codigo, elCaro.Codigo);
        Assert.Contains(negativos, p => p.VarianteId == negativo.Variantes[0].Id && p.Existencia == -2 && p.ValorCentavos == 0);
        Assert.Equal(grupo.Negativa, negativos.Count);
        Assert.Contains(sinCostos, p => p.VarianteId == sinCosto.Variantes[0].Id && p.Existencia == 5 && p.ValorCentavos == 0);
        Assert.Contains(enCeros, p => p.VarianteId == enCero.Variantes[0].Id && p.Existencia == 0);
        Assert.DoesNotContain(enCeros, p => p.VarianteId == negativo.Variantes[0].Id);
    }

    // ── Movimientos ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Los_movimientos_dicen_su_folio_quien_lo_hizo_lo_que_quedo_y_el_costo_solo_con_permiso()
    {
        var e = await PrepararAsync();
        var producto = await ProductoAsync(e, "Movimientos de prueba folio", 10, 1000);
        var variante = producto.Variantes[0].Id!.Value;
        var folio = await VenderAsync(e, producto, 3);
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, variante, 9000, "Apareció otra caja")));
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/mermas",
            new SolicitudMerma(e.Cajero.UsuarioId, e.Caja.SucursalId, variante, 1000, CausaMerma.Danado, "Se rompió")));
        var codigo = producto.Variantes[0].Presentaciones[0].Codigos[0].Codigo;

        var delCajero = await MovimientosAsync(e, e.Cajero, Dias());
        var delEncargado = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, Dias($"&q={codigo}")));
        var delDueno = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Dueno, Dias($"&q={codigo}")));
        var kardex = (await e.Http.GetFromJsonAsync<List<MovimientoKardexDto>>($"api/catalogo/variantes/{variante}/kardex?sucursalId={e.Caja.SucursalId}"))!;

        // El cajero no lleva el inventario: no los ve.
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Contains("Los movimientos del inventario", (await delCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        // Del más reciente al más viejo: la merma, el ajuste, la venta y lo que había.
        Assert.Equal(4, delEncargado.Total);
        Assert.Equal([ClaseMovimiento.Merma, ClaseMovimiento.Ajuste, ClaseMovimiento.Venta, ClaseMovimiento.InventarioInicial],
            delEncargado.Movimientos.Select(m => m.Clase));
        Assert.Equal([-1m, 2m, -3m, 10m], delEncargado.Movimientos.Select(m => m.Cantidad));
        // Lo que quedó después de cada uno, lo mismo que dice el kardex.
        Assert.Equal(kardex.Select(k => k.Existencia), delEncargado.Movimientos.Select(m => m.Existencia));
        Assert.Equal([8m, 9m, 7m, 10m], delEncargado.Movimientos.Select(m => m.Existencia));
        var (merma, ajuste, venta, inicial) = (delEncargado.Movimientos[0], delEncargado.Movimientos[1], delEncargado.Movimientos[2], delEncargado.Movimientos[3]);
        Assert.Equal((folio, "Cajero de prueba"), (venta.Documento, venta.Quien));
        Assert.Equal(("Encargado de prueba", "Apareció otra caja"), (ajuste.Quien, ajuste.Motivo));
        Assert.Equal(("Cajero de prueba", "Dañado: Se rompió"), (merma.Quien, merma.Motivo));
        Assert.Equal(("de la importación de eleventa", (string?)null), (inicial.Documento, inicial.Quien));
        Assert.All(delEncargado.Movimientos, m => Assert.Equal(("Movimientos de prueba folio", codigo), (m.Producto, m.Codigo)));
        // El costo, solo con «Ver costos, utilidad y margen».
        Assert.False(delEncargado.ConCostos);
        Assert.All(delEncargado.Movimientos, m => Assert.Null(m.CostoCentavos));
        Assert.True(delDueno.ConCostos);
        Assert.Equal([-1000L, 2000L, -3000L, 10000L], delDueno.Movimientos.Select(m => m.CostoCentavos!.Value));
    }

    [Fact]
    public async Task Los_movimientos_se_filtran_por_tipo_producto_departamento_y_quien()
    {
        var e = await PrepararAsync();
        var departamentos = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos;
        var uno = await ProductoAsync(e, "Filtros de prueba uno", 10, 500, departamentos[0].Id);
        var otro = await ProductoAsync(e, "Filtros de prueba otro", 10, 500, departamentos[^1].Id);
        await VenderAsync(e, uno, 1);
        await VenderAsync(e, otro, 2);
        await Leer<AjusteInventarioDto>(await e.Http.PostAsJsonAsync("api/inventario/ajustes",
            new SolicitudAjuste(e.Encargado.UsuarioId, e.Caja.SucursalId, otro.Variantes[0].Id!.Value, 5000, "Conté el anaquel")));

        async Task<List<MovimientoInventarioDto>> ConAsync(string filtros) =>
            (await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, Dias($"&q={Uri.EscapeDataString("filtros de prueba")}{filtros}"))))
            .Movimientos.ToList();

        var todos = await ConAsync("");
        var ventas = await ConAsync("&clase=Venta");
        var ajustes = await ConAsync("&clase=Ajuste");
        var iniciales = await ConAsync("&clase=InventarioInicial");
        var delOtroDepartamento = await ConAsync($"&departamentoId={departamentos[^1].Id}");
        var delCajero = await ConAsync($"&quienId={e.Cajero.UsuarioId}");
        var delEncargado = await ConAsync($"&quienId={e.Encargado.UsuarioId}");
        var porCodigo = (await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado,
            Dias($"&q={uno.Variantes[0].Presentaciones[0].Codigos[0].Codigo}")))).Movimientos;
        var otroDia = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, "&desde=2020-01-01&hasta=2020-01-31&q=filtros"));
        var alReves = await MovimientosAsync(e, e.Encargado, "&desde=2020-01-31&hasta=2020-01-01");

        Assert.Equal(5, todos.Count);
        Assert.Equal(2, ventas.Count);
        Assert.All(ventas, m => Assert.Equal(ClaseMovimiento.Venta, m.Clase));
        // Tenía 10, vendió 2 y se ajustó a 5.
        Assert.Equal(("Filtros de prueba otro", -3m), (ajustes.Single().Producto, ajustes.Single().Cantidad));
        Assert.Equal(2, iniciales.Count);
        Assert.Equal(3, delOtroDepartamento.Count);
        Assert.All(delOtroDepartamento, m => Assert.Equal("Filtros de prueba otro", m.Producto));
        // Quién: el cajero, por sus ventas; el encargado, por su ajuste. Lo importado no es de nadie.
        Assert.Equal([ClaseMovimiento.Venta, ClaseMovimiento.Venta], delCajero.Select(m => m.Clase));
        Assert.Equal(ClaseMovimiento.Ajuste, delEncargado.Single().Clase);
        Assert.Equal(["Filtros de prueba uno"], porCodigo.Select(m => m.Producto).Distinct());
        Assert.Equal(0, otroDia.Total);
        Assert.Equal(HttpStatusCode.BadRequest, alReves.StatusCode);
        // Los usuarios para el filtro «Quién», también para quien los consulta.
        var quienes = await Leer<List<OpcionCatalogoDto>>(await e.Http.GetAsync($"{e.Url}/movimientos/quienes?usuarioId={e.Encargado.UsuarioId}"));
        Assert.Contains(quienes, q => q.Id == e.Cajero.UsuarioId && q.Nombre == "Cajero de prueba");
    }

    [Fact]
    public async Task Los_movimientos_van_de_50_en_50_del_mas_reciente_al_mas_viejo_sin_repetirse()
    {
        var e = await PrepararAsync();
        // El inventario inicial de la papelería ficticia: todos al cierre del mismo día, a la misma hora.
        var dia = "&desde=2026-09-30&hasta=2026-09-30";
        await using var db = servicio.Base();
        var desde = HoraMexico.AUtc(new DateTime(2026, 9, 30));
        var esperados = await db.MovimientosInventario
            .CountAsync(m => m.SucursalId == e.Caja.SucursalId && m.FechaUtc >= desde && m.FechaUtc < desde.AddDays(1));

        var primera = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, dia));
        var segunda = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, dia + "&pagina=1"));
        var ultima = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, dia + $"&pagina={(esperados - 1) / 50}"));
        var despues = await Leer<MovimientosInventarioDto>(await MovimientosAsync(e, e.Encargado, dia + $"&pagina={(esperados - 1) / 50 + 1}"));

        Assert.True(esperados > 100, $"Hay {esperados}");
        Assert.Equal((esperados, 50, 0), (primera.Total, primera.Movimientos.Count, primera.Pagina));
        Assert.Equal(50, segunda.Movimientos.Count);
        Assert.Empty(primera.Movimientos.Select(m => m.Id).Intersect(segunda.Movimientos.Select(m => m.Id)));
        Assert.Equal(esperados - (esperados - 1) / 50 * 50, ultima.Movimientos.Count);
        Assert.Empty(despues.Movimientos);
        Assert.All(primera.Movimientos.Concat(segunda.Movimientos), m =>
        {
            Assert.Equal(ClaseMovimiento.InventarioInicial, m.Clase);
            // Lo primero que tuvo: quedó lo mismo que entró.
            Assert.Equal(m.Cantidad, m.Existencia);
        });
    }

    // ── El mínimo y el máximo ───────────────────────────────────────────────

    [Fact]
    public async Task El_minimo_y_el_maximo_se_guardan_en_la_ficha_suben_la_version_y_una_caja_de_antes_no_los_borra()
    {
        var e = await PrepararAsync();
        var ficha = await ProductoAsync(e, "Mínimo de prueba ficha", 4, 100);
        var variante = ficha.Variantes[0].Id!.Value;
        Assert.Equal(new MinimoYMaximoDto(null, null), ficha.Variantes[0].MinimoYMaximo);
        long version;
        await using (var db = servicio.Base())
            version = (await db.Variantes.SingleAsync(v => v.Id == variante)).Version;

        async Task<FichaProductoDto> GuardarAsync(MinimoYMaximoDto? minimoYMaximo) =>
            await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId,
                e.Caja.SucursalId, ficha with { Variantes = [ficha.Variantes[0] with { MinimoYMaximo = minimoYMaximo }] })));

        var conMinimo = await GuardarAsync(new MinimoYMaximoDto(5000, 20000));
        // Una caja de antes no los conoce: no los manda, y se quedan.
        var deAntes = await GuardarAsync(null);
        var leida = await e.Http.GetFromJsonAsync<FichaProductoDto>($"api/catalogo/productos/{ficha.Id}?sucursalId={e.Caja.SucursalId}&usuarioId={e.Encargado.UsuarioId}");
        var sinMinimo = await GuardarAsync(new MinimoYMaximoDto(null, null));

        Assert.Equal(new MinimoYMaximoDto(5000, 20000), conMinimo.Variantes[0].MinimoYMaximo);
        Assert.Equal(new MinimoYMaximoDto(5000, 20000), deAntes.Variantes[0].MinimoYMaximo);
        Assert.Equal(new MinimoYMaximoDto(5000, 20000), leida!.Variantes[0].MinimoYMaximo);
        Assert.Equal(new MinimoYMaximoDto(null, null), sinMinimo.Variantes[0].MinimoYMaximo);
        await using (var db = servicio.Base())
        {
            // Sube la versión del catálogo: las cajas bajan el catálogo otra vez.
            Assert.True((await db.Variantes.SingleAsync(v => v.Id == variante)).Version > version);
            var bitacora = await db.Bitacora.Where(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle.StartsWith("Mínimo y máximo de «Mínimo de prueba ficha»"))
                .OrderBy(b => b.FechaUtc).Select(b => b.Detalle).ToListAsync();
            Assert.Equal(
            [
                "Mínimo y máximo de «Mínimo de prueba ficha»: sin mínimo ni máximo → mínimo 5, máximo 20",
                "Mínimo y máximo de «Mínimo de prueba ficha»: mínimo 5, máximo 20 → sin mínimo ni máximo",
            ], bitacora);
        }
    }

    [Fact]
    public async Task El_minimo_y_el_maximo_se_revisan_antes_de_guardar()
    {
        var e = await PrepararAsync();
        var ficha = await ProductoAsync(e, "Mínimo de prueba revisado", 0, 0);

        async Task<string> ErrorAsync(MinimoYMaximoDto minimoYMaximo, UnidadBase unidad = UnidadBase.Pieza)
        {
            var r = await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId, e.Caja.SucursalId,
                ficha with { Variantes = [ficha.Variantes[0] with { MinimoYMaximo = minimoYMaximo, Unidad = unidad }] }));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
        }

        Assert.Equal("El mínimo de «Mínimo de prueba revisado» no puede ser negativo.", await ErrorAsync(new MinimoYMaximoDto(-1000, null)));
        Assert.Equal("El máximo de «Mínimo de prueba revisado» va mayor a cero.", await ErrorAsync(new MinimoYMaximoDto(null, 0)));
        Assert.Equal("El máximo de «Mínimo de prueba revisado» no puede ser menor que su mínimo.", await ErrorAsync(new MinimoYMaximoDto(10000, 5000)));
        Assert.Equal("El mínimo de «Mínimo de prueba revisado» va en piezas enteras.", await ErrorAsync(new MinimoYMaximoDto(2500, null)));
        Assert.Equal("El máximo de «Mínimo de prueba revisado» es demasiado grande.", await ErrorAsync(new MinimoYMaximoDto(null, 2_000_000_000)));
        // Por metro sí van fracciones; el mínimo en cero también vale (sale cuando se acaba).
        var porMetro = await Leer<FichaProductoDto>(await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(
            e.Encargado.UsuarioId, e.Caja.SucursalId,
            ficha with { Variantes = [ficha.Variantes[0] with { MinimoYMaximo = new MinimoYMaximoDto(0, 2500), Unidad = UnidadBase.Metro }] })));
        Assert.Equal(new MinimoYMaximoDto(0, 2500), porMetro.Variantes[0].MinimoYMaximo);
    }

    [Fact]
    public async Task Poca_existencia_usa_el_minimo_propio_y_sin_el_tres_o_menos()
    {
        var e = await PrepararAsync();
        var conMinimoAlto = await ProductoAsync(e, "Poca de prueba mínimo alto", 8, 100, minimoYMaximo: new MinimoYMaximoDto(10000, null));
        var conMinimoCero = await ProductoAsync(e, "Poca de prueba mínimo cero", 2, 100, minimoYMaximo: new MinimoYMaximoDto(0, null));
        var sinMinimo = await ProductoAsync(e, "Poca de prueba sin mínimo", 3, 100);
        var conMuchas = await ProductoAsync(e, "Poca de prueba con muchas", 4, 100);

        var poca = await Leer<ListaProductosDto>(await e.Http.GetAsync(
            $"api/catalogo/lista?sucursalId={e.Caja.SucursalId}&usuarioId={e.Encargado.UsuarioId}&q=poca%20de%20prueba&filtro=PocaExistencia&maximo=500"));

        Assert.Equal(["Poca de prueba mínimo alto", "Poca de prueba sin mínimo"], poca.Productos.Select(p => p.Nombre).Order());
        Assert.Equal((8m, 10m), (poca.Productos.Single(p => p.ProductoId == conMinimoAlto.Id).Existencia,
            poca.Productos.Single(p => p.ProductoId == conMinimoAlto.Id).Minimo!.Value));
        Assert.Null(poca.Productos.Single(p => p.ProductoId == sinMinimo.Id).Minimo);
        Assert.DoesNotContain(poca.Productos, p => p.ProductoId == conMinimoCero.Id || p.ProductoId == conMuchas.Id);
    }

    // ── Bajos en inventario ─────────────────────────────────────────────────

    [Fact]
    public async Task Bajos_en_inventario_lista_lo_que_esta_en_su_minimo_con_cuanto_pedir()
    {
        var e = await PrepararAsync();
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        // Con su mínimo y su máximo: pide lo que falta para el máximo, contando lo que viene en una orden abierta.
        var conMaximo = await ProductoAsync(e, "Bajos de prueba con máximo", 4, 100, departamento, new MinimoYMaximoDto(5000, 20000));
        // Sin mínimo ni máximo, con 2 piezas y 14 vendidas hace tres días: lo del sugerido.
        var delSugerido = await ProductoAsync(e, "Bajos de prueba del sugerido", 2, 100, departamento);
        // Con su mínimo, pero arriba de él: no sale. Con el mínimo en cero y sin existencia: sí.
        var arriba = await ProductoAsync(e, "Bajos de prueba arriba", 10, 100, departamento, new MinimoYMaximoDto(5000, null));
        var enCero = await ProductoAsync(e, "Bajos de prueba en cero", 0, 0, departamento, new MinimoYMaximoDto(0, null));
        await using (var db = servicio.Base())
        {
            var hace3Dias = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)).AddDays(-3);
            db.VentasHistoricas.Add(new VentaHistorica
            {
                SucursalId = e.Caja.SucursalId, FechaUtc = HoraMexico.AUtc(hace3Dias.ToDateTime(new TimeOnly(12, 0))), FolioOrigen = "BAJOS-1", TotalCentavos = 28000,
                Lineas = [new VentaHistoricaLinea { VarianteId = delSugerido.Variantes[0].Id!.Value, Cantidad = Cantidad.Piezas(14), PrecioCentavos = 2000, ImporteCentavos = 28000 }],
            });
            await db.SaveChangesAsync();
        }
        var proveedor = (await e.Http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.First();
        var orden = await Leer<OrdenDto>(await e.Http.PostAsJsonAsync("api/ordenes", new SolicitudAbrirOrden(e.Encargado.UsuarioId, e.Caja.SucursalId, proveedor.Id!.Value, null)));
        await Leer<OrdenDto>(await e.Http.PostAsJsonAsync($"api/ordenes/{orden.Id}/renglones",
            new SolicitudPedido(e.Encargado.UsuarioId, conMaximo.Variantes[0].Id!.Value, conMaximo.Variantes[0].Presentaciones[0].Id!.Value, 6000)));

        var delCajero = await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Cajero.UsuarioId}");
        var bajos = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q=bajos%20de%20prueba"));
        var conMinimoPropio = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q=bajos%20de%20prueba&conMinimo=true"));
        var todos = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}"));
        var segunda = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&pagina=1"));
        var otroDepartamento = await Leer<BajosInventarioDto>(await e.Http.GetAsync(
            $"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q=bajos%20de%20prueba&departamentoId={Guid.NewGuid()}"));
        var pendientes = await Leer<PendientesInventarioDto>(await e.Http.GetAsync($"{e.Url}/pendientes"));
        var sugerido = await Leer<SugeridoDto>(await e.Http.GetAsync($"api/sucursales/{e.Caja.SucursalId}/sugerido?usuarioId={e.Encargado.UsuarioId}"));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(["Bajos de prueba con máximo", "Bajos de prueba del sugerido", "Bajos de prueba en cero"], bajos.Productos.Select(p => p.Nombre));
        Assert.Equal((3, 3, 2), (bajos.Total, bajos.Todos, bajos.ConMinimoPropio));
        var maximo = bajos.Productos[0];
        Assert.Equal((4m, 5m, true, (decimal?)20m, 6m, (decimal?)10m, (string?)null),
            (maximo.Existencia, maximo.Minimo, maximo.MinimoPropio, maximo.Maximo, maximo.EnCamino, maximo.Pedir, maximo.PedirEn));
        var conSugerido = bajos.Productos[1];
        var renglon = sugerido.Proveedores.SelectMany(p => p.Renglones).Single(r => r.VarianteId == delSugerido.Variantes[0].Id);
        Assert.Equal((2m, (decimal)PocaExistencia.Hasta, false, (decimal?)null), (conSugerido.Existencia, conSugerido.Minimo, conSugerido.MinimoPropio, conSugerido.Maximo));
        Assert.Equal((renglon.Paquetes * renglon.Factor, renglon.Maximo), (conSugerido.Pedir!.Value, conSugerido.MaximoSugerido!.Value));
        Assert.True(conSugerido.Pedir > 0);
        Assert.Equal((0m, 0m, true), (bajos.Productos[2].Existencia, bajos.Productos[2].Minimo, bajos.Productos[2].MinimoPropio));
        Assert.DoesNotContain(bajos.Productos, p => p.VarianteId == arriba.Variantes[0].Id);
        Assert.Equal(["Bajos de prueba con máximo", "Bajos de prueba en cero"], conMinimoPropio.Productos.Select(p => p.Nombre));
        Assert.Equal((2, 3), (conMinimoPropio.Total, conMinimoPropio.Todos));
        Assert.Empty(otroDepartamento.Productos);
        // La tarjeta cuenta todos los que están en su mínimo o debajo, como la lista sin filtros, que va de 100 en 100.
        Assert.Equal(todos.Todos, pendientes.BajosEnInventario);
        Assert.True(todos.Todos >= 3);
        Assert.Equal((Math.Min(100, todos.Total), Math.Clamp(todos.Total - 100, 0, 100)), (todos.Productos.Count, segunda.Productos.Count));
        Assert.Empty(todos.Productos.Select(p => p.VarianteId).Intersect(segunda.Productos.Select(p => p.VarianteId)));
        Assert.Equal(todos.Productos.Select(p => (p.Departamento, p.Nombre)).OrderBy(x => x.Departamento, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Nombre, StringComparer.CurrentCultureIgnoreCase), todos.Productos.Select(p => (p.Departamento, p.Nombre)));
    }

    [Fact]
    public async Task Sin_maximo_pide_lo_del_sugerido_pero_al_menos_lo_que_falta_para_su_minimo()
    {
        var e = await PrepararAsync();
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        // Mínimo 8, hay 2 y viene 1 en una orden, sin ventas: el sugerido no pide nada; faltan 5 para su mínimo.
        var sinVentas = await ProductoAsync(e, "Mínimo sin máximo de prueba sin ventas", 2, 100, departamento, new MinimoYMaximoDto(8000, null));
        // Mínimo 30 y hay 1, con ventas: el sugerido pide menos de las 29 que faltan para su mínimo.
        var minimoAlto = await ProductoAsync(e, "Mínimo sin máximo de prueba alto", 1, 100, departamento, new MinimoYMaximoDto(30000, null));
        // Mínimo 3 y hay 1, con ventas: el sugerido pide más de las 2 que faltan; se queda lo del sugerido.
        var minimoBajo = await ProductoAsync(e, "Mínimo sin máximo de prueba bajo", 1, 100, departamento, new MinimoYMaximoDto(3000, null));
        // Justo en su mínimo y sin ventas: sale, pero no le falta nada para llegar.
        var justo = await ProductoAsync(e, "Mínimo sin máximo de prueba justo", 5, 100, departamento, new MinimoYMaximoDto(5000, null));
        await using (var db = servicio.Base())
        {
            var hace3Dias = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)).AddDays(-3);
            db.VentasHistoricas.Add(new VentaHistorica
            {
                SucursalId = e.Caja.SucursalId, FechaUtc = HoraMexico.AUtc(hace3Dias.ToDateTime(new TimeOnly(12, 0))), FolioOrigen = "MINIMO-1", TotalCentavos = 56000,
                Lineas =
                [
                    new VentaHistoricaLinea { VarianteId = minimoAlto.Variantes[0].Id!.Value, Cantidad = Cantidad.Piezas(14), PrecioCentavos = 2000, ImporteCentavos = 28000 },
                    new VentaHistoricaLinea { VarianteId = minimoBajo.Variantes[0].Id!.Value, Cantidad = Cantidad.Piezas(14), PrecioCentavos = 2000, ImporteCentavos = 28000 },
                ],
            });
            await db.SaveChangesAsync();
        }
        var proveedor = (await e.Http.GetFromJsonAsync<List<ProveedorDto>>("api/proveedores"))!.First();
        var orden = await Leer<OrdenDto>(await e.Http.PostAsJsonAsync("api/ordenes", new SolicitudAbrirOrden(e.Encargado.UsuarioId, e.Caja.SucursalId, proveedor.Id!.Value, null)));
        await Leer<OrdenDto>(await e.Http.PostAsJsonAsync($"api/ordenes/{orden.Id}/renglones",
            new SolicitudPedido(e.Encargado.UsuarioId, sinVentas.Variantes[0].Id!.Value, sinVentas.Variantes[0].Presentaciones[0].Id!.Value, 1000)));

        var bajos = await Leer<BajosInventarioDto>(await e.Http.GetAsync(
            $"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q={Uri.EscapeDataString("mínimo sin máximo de prueba")}"));
        var sugerido = await Leer<SugeridoDto>(await e.Http.GetAsync($"api/sucursales/{e.Caja.SucursalId}/sugerido?usuarioId={e.Encargado.UsuarioId}"));
        BajoInventarioDto De(FichaProductoDto producto) => bajos.Productos.Single(p => p.VarianteId == producto.Variantes[0].Id);
        decimal DelSugerido(FichaProductoDto producto) =>
            sugerido.Proveedores.SelectMany(p => p.Renglones).Where(r => r.VarianteId == producto.Variantes[0].Id).Sum(r => r.Paquetes * r.Factor);

        Assert.Equal(4, bajos.Total);
        Assert.Equal(0m, DelSugerido(sinVentas));
        Assert.Equal((1m, (decimal?)5m, (string?)null), (De(sinVentas).EnCamino, De(sinVentas).Pedir, De(sinVentas).PedirEn));
        Assert.InRange(DelSugerido(minimoAlto), 1m, 28m);
        Assert.Equal(29m, De(minimoAlto).Pedir);
        Assert.True(DelSugerido(minimoBajo) > 2, $"El sugerido pide {DelSugerido(minimoBajo)}");
        Assert.Equal(DelSugerido(minimoBajo), De(minimoBajo).Pedir);
        Assert.Equal((5m, 5m, true, (decimal?)null), (De(justo).Existencia, De(justo).Minimo, De(justo).MinimoPropio, De(justo).Pedir));
    }

    // ── En el piloto con eleventa ───────────────────────────────────────────

    /// <summary>
    /// En el piloto, el mínimo y el máximo de un producto de eleventa los pone la lectura (2026-10-24: el inventario se lleva
    /// allá) y no se cambian aquí; los de lo dado de alta aquí, sí. Los reportes solo leen: también van en el piloto.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_el_minimo_de_un_producto_de_eleventa_viene_de_eleventa_y_los_reportes_se_ven()
    {
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var e = await PrepararAsync(piloto.CreateClient());
        var ficha = await ProductoAsync(e, "Piloto de prueba mínimo", 2, 100);
        var deAqui = await ProductoAsync(e, "Piloto de prueba de aquí", 1, 100);
        await using (var db = servicio.Base())
        {
            db.MapeosImportacion.Add(new MapeoImportacion
            {
                Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(),
                IdNuevo = ficha.Variantes[0].Id!.Value,
            });
            // Como lo deja la lectura.
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            (variante.InventarioMinimo, variante.InventarioMaximo) = (Cantidad.Piezas(4), Cantidad.Piezas(12));
            await db.SaveChangesAsync();
        }
        async Task<HttpResponseMessage> GuardarAsync(FichaProductoDto f, MinimoYMaximoDto? minimoYMaximo) =>
            await e.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(e.Encargado.UsuarioId, e.Caja.SucursalId,
                f with { Variantes = [f.Variantes[0] with { MinimoYMaximo = minimoYMaximo }] }));
        var deEleventa = await e.Http.GetFromJsonAsync<FichaProductoDto>(
            $"api/catalogo/productos/{ficha.Id}?sucursalId={e.Caja.SucursalId}&usuarioId={e.Encargado.UsuarioId}");

        // Lo pone la lectura: no se cambia aquí, con la nota de lo demás de eleventa.
        Assert.True(deEleventa!.ProductoDeEleventa);
        Assert.Equal(new MinimoYMaximoDto(4000, 12000), deEleventa.Variantes[0].MinimoYMaximo);
        var rechazo = await GuardarAsync(deEleventa, new MinimoYMaximoDto(6000, 12000));
        Assert.Equal(HttpStatusCode.BadRequest, rechazo.StatusCode);
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, (await rechazo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        // Lo dado de alta aquí sí.
        Assert.Equal(new MinimoYMaximoDto(3000, 6000), (await Leer<FichaProductoDto>(await GuardarAsync(deAqui, new MinimoYMaximoDto(3000, 6000)))).Variantes[0].MinimoYMaximo);
        // Los reportes solo leen: también van en el piloto.
        var bajos = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q=piloto%20de%20prueba"));
        Assert.Equal([("Piloto de prueba de aquí", 3m, (decimal?)5m), ("Piloto de prueba mínimo", 4m, (decimal?)10m)],
            bajos.Productos.OrderBy(p => p.Nombre).Select(p => (p.Nombre, p.Minimo, p.Pedir)));
        Assert.True((await MovimientosAsync(e, e.Encargado, Dias())).IsSuccessStatusCode);
        Assert.True((await e.Http.GetAsync($"{e.Url}/valor?usuarioId={e.Dueno.UsuarioId}")).IsSuccessStatusCode);
    }

    /// <summary>
    /// Como los pone la tienda en eleventa (base del 2026-10-24): el mínimo igual al máximo. La ficha lo acepta; con eso o menos
    /// sale en «Bajos en inventario» y pide lo que falta para el máximo (en el mínimo justo, nada: ya llega a su máximo); arriba
    /// del mínimo no sale.
    /// </summary>
    [Fact]
    public async Task Con_el_minimo_igual_al_maximo_como_en_eleventa_sale_en_su_minimo_y_pide_hasta_el_maximo()
    {
        var e = await PrepararAsync();
        var departamento = (await e.Http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var igual = new MinimoYMaximoDto(5000, 5000);
        var debajo = await ProductoAsync(e, "Igual de prueba debajo", 2, 100, departamento, igual);
        var justo = await ProductoAsync(e, "Igual de prueba justo", 5, 100, departamento, igual);
        await ProductoAsync(e, "Igual de prueba arriba", 6, 100, departamento, igual);

        var bajos = await Leer<BajosInventarioDto>(await e.Http.GetAsync($"{e.Url}/bajos?usuarioId={e.Encargado.UsuarioId}&q=igual%20de%20prueba"));

        Assert.Equal(igual, debajo.Variantes[0].MinimoYMaximo);
        Assert.Equal([("Igual de prueba debajo", 2m, 5m, (decimal?)5m, (decimal?)3m), ("Igual de prueba justo", 5m, 5m, (decimal?)5m, (decimal?)null)],
            bajos.Productos.OrderBy(p => p.Nombre).Select(p => (p.Nombre, p.Existencia, p.Minimo, p.Maximo, p.Pedir)));
        Assert.All(bajos.Productos, p => Assert.True(p.MinimoPropio));
        Assert.Equal(justo.Id, bajos.Productos.Single(p => p.Pedir is null).ProductoId);
    }

    [Fact]
    public void La_lectura_de_eleventa_anota_lo_mismo_que_lee_Movimientos()
    {
        Assert.Equal(SincronizacionEleventa.Documento, OrigenDelMovimiento.LecturaEleventa);
        Assert.Equal(PapeleriaPos.Herramientas.Eleventa.ImportadorEleventa.Origen, OrigenDelMovimiento.ImportacionEleventa);
        Assert.Equal(PapeleriaPos.Herramientas.Ficticios.GeneradorFicticios.Origen, OrigenDelMovimiento.Ficticios);
        Assert.Equal(ClaseMovimiento.LecturaEleventa, RegistroReportesInventario.ClaseDe(TipoMovimiento.Conteo, OrigenDelMovimiento.LecturaEleventa));
        Assert.Equal(ClaseMovimiento.LecturaEleventa, RegistroReportesInventario.ClaseDe(TipoMovimiento.InventarioInicial, OrigenDelMovimiento.LecturaEleventa));
        Assert.Equal(ClaseMovimiento.Conteo, RegistroReportesInventario.ClaseDe(TipoMovimiento.Conteo, nameof(ConteoCiclico)));
        Assert.Equal(ClaseMovimiento.Paquete, RegistroReportesInventario.ClaseDe(TipoMovimiento.AperturaPaquete, nameof(AperturaPaquete)));
    }
}
