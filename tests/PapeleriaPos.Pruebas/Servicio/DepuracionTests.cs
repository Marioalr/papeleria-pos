using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

public class DepuracionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        return (http, caja, cajero, encargado);
    }

    /// <summary>Un producto con su pieza, una caja de 12, su código, su precio y existencia (y opcionalmente su ID de eleventa).</summary>
    private async Task<(Guid Variante, string Codigo)> ProductoAsync(Guid sucursalId, string nombre, int existencia, long costoPieza, UnidadBase unidad = UnidadBase.Pieza,
        string? eleventa = null)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var codigo = $"DEP{Guid.NewGuid():N}"[..16];
        var producto = new Producto { Nombre = nombre, DepartamentoId = await db.Departamentos.Select(d => d.Id).FirstAsync() };
        var variante = new Variante { ProductoId = producto.Id, UnidadBase = unidad };
        var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
        var caja12 = new Presentacion { VarianteId = variante.Id, Nombre = "Caja", Factor = Cantidad.Unidades(12) };
        db.AddRange(producto, variante, pieza, caja12, new CodigoBarras { Codigo = codigo, PresentacionId = pieza.Id },
            new Precio { VarianteId = variante.Id, PrecioCentavos = costoPieza * 2 });
        if (eleventa is not null)
            db.MapeosImportacion.Add(new MapeoImportacion { Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = eleventa, IdNuevo = variante.Id });
        await db.SaveChangesAsync();
        if (existencia > 0)
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(sucursalId, variante, TipoMovimiento.InventarioInicial, Cantidad.Unidades(existencia),
                existencia * costoPieza, DateTime.UtcNow, "Inicial de prueba"));
        return (variante.Id, codigo);
    }

    private async Task<T> ConBaseAsync<T>(Func<PosDbContext, Task<T>> consulta)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return await consulta(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    [Fact]
    public async Task Juntar_pasa_codigos_existencia_y_costo_y_deja_la_historia()
    {
        var (http, caja, cajero, encargado) = await PrepararAsync();
        var (repetido, codigoRepetido) = await ProductoAsync(caja.SucursalId, "Goma blanca", 5, 300);
        var (bueno, _) = await ProductoAsync(caja.SucursalId, "Goma Pelikan blanca", 3, 400);

        var porCajero = await http.PostAsJsonAsync("api/catalogo/depuracion/juntar", new SolicitudJuntar(cajero.UsuarioId, caja.SucursalId, repetido, bueno));
        (await http.PostAsJsonAsync("api/catalogo/depuracion/juntar", new SolicitudJuntar(encargado.UsuarioId, caja.SucursalId, repetido, bueno))).EnsureSuccessStatusCode();
        var escaneado = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{codigoRepetido}?sucursalId={caja.SucursalId}"));
        var decisiones = await http.GetFromJsonAsync<List<DecisionDepuracionDto>>("api/catalogo/depuracion");

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal(bueno, escaneado.VarianteId);
        var (origen, destino, existenciaOrigen, existenciaDestino, cajas) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == repetido),
            await db.Variantes.AsNoTracking().Include(v => v.Presentaciones).ThenInclude(p => p.Codigos).SingleAsync(v => v.Id == bueno),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == repetido),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == bueno),
            await db.MovimientosInventario.AsNoTracking().CountAsync(m => m.VarianteId == repetido)));
        Assert.False(origen.Activa);
        Assert.Equal(bueno, origen.JuntadaEnId);
        Assert.Equal(Cantidad.Cero, existenciaOrigen.Cantidad);
        Assert.Equal(0, existenciaOrigen.CostoCentavos);
        Assert.Equal(Cantidad.Unidades(8), existenciaDestino.Cantidad);
        Assert.Equal(5 * 300 + 3 * 400, existenciaDestino.CostoCentavos);
        Assert.Equal(2, cajas);                                         // el inicial y el ajuste: la historia se queda
        Assert.Equal(2, destino.Presentaciones.Single(p => p.EsBase).Codigos.Count);
        Assert.Single(destino.Presentaciones, p => !p.EsBase);           // la caja de 12 ya existía: no se duplica
        Assert.Contains(decisiones!, d => d.Tipo == TipoDecisionDepuracion.Juntar && d.Descripcion == "«Goma blanca» se juntó con «Goma Pelikan blanca»");
    }

    [Fact]
    public async Task Cambiar_la_unidad_convierte_la_existencia_o_la_deja_para_contar()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (liston, _) = await ProductoAsync(caja.SucursalId, "Listón de raso rojo", 2, 5000);
        var (micas, _) = await ProductoAsync(caja.SucursalId, "Micas tamaño carta", 4, 1000);

        (await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, liston, UnidadBase.Metro, 50_000, 350))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, micas, UnidadBase.Kilogramo, null, 2000))).EnsureSuccessStatusCode();
        var otraVez = await http.PostAsJsonAsync("api/catalogo/depuracion/unidad",
            new SolicitudCambiarUnidad(encargado.UsuarioId, caja.SucursalId, liston, UnidadBase.Metro, null, 350));

        var (variante, existencia, precios, sinConvertir) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().Include(v => v.Presentaciones).SingleAsync(v => v.Id == liston),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == liston),
            await db.Precios.AsNoTracking().Where(p => p.VarianteId == liston).ToListAsync(),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == micas)));
        Assert.Equal(UnidadBase.Metro, variante.UnidadBase);
        Assert.Equal("Metro", variante.Presentaciones.Single(p => p.EsBase).Nombre);
        Assert.Equal(Cantidad.Unidades(600), variante.Presentaciones.Single(p => !p.EsBase).Factor);   // la caja de 12 = 600 m
        Assert.Equal(Cantidad.Unidades(100), existencia.Cantidad);
        Assert.Equal(2 * 5000, existencia.CostoCentavos);                                                  // el valor no cambia
        Assert.Equal(350, Assert.Single(precios).PrecioCentavos);
        Assert.Equal(Cantidad.Cero, sinConvertir.Cantidad);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
    }

    [Fact]
    public async Task Dar_de_baja_deja_la_existencia_en_cero_y_lo_saca_de_la_venta()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (viejo, codigo) = await ProductoAsync(caja.SucursalId, "Disquete 3.5", 7, 500);

        (await http.PostAsJsonAsync("api/catalogo/depuracion/baja", new SolicitudBaja(encargado.UsuarioId, caja.SucursalId, viejo))).EnsureSuccessStatusCode();
        var escaneo = await http.GetAsync($"api/productos/codigo/{codigo}?sucursalId={caja.SucursalId}");

        var (producto, existencia) = await ConBaseAsync(async db => (
            await db.Productos.AsNoTracking().SingleAsync(p => p.Variantes.Any(v => v.Id == viejo)),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == viejo)));
        Assert.False(producto.Activo);
        Assert.Equal(Cantidad.Cero, existencia.Cantidad);
        Assert.Equal(HttpStatusCode.NotFound, escaneo.StatusCode);
    }

    [Fact]
    public async Task Las_decisiones_se_repiten_en_otra_importacion_por_su_id_de_eleventa()
    {
        var (_, caja, _, _) = await PrepararAsync();
        var (a, _) = await ProductoAsync(caja.SucursalId, "Lápiz amarillo", 2, 200, eleventa: "90001");
        var (b, _) = await ProductoAsync(caja.SucursalId, "Lápiz Mirado amarillo", 1, 250, eleventa: "90002");
        var (c, _) = await ProductoAsync(caja.SucursalId, "Pluma fuente vieja", 1, 900, eleventa: "90003");
        // Así llegan de la otra base: con los IDs de eleventa, no con los de aquí.
        var decisiones = new List<DecisionDepuracion>
        {
            new() { Tipo = TipoDecisionDepuracion.Juntar, OrigenEleventa = "90001", DestinoEleventa = "90002", Descripcion = "lápices", FechaUtc = DateTime.UtcNow.AddMinutes(-2) },
            new() { Tipo = TipoDecisionDepuracion.DarDeBaja, OrigenEleventa = "90003", Descripcion = "pluma", FechaUtc = DateTime.UtcNow.AddMinutes(-1) },
            new() { Tipo = TipoDecisionDepuracion.DarDeBaja, OrigenEleventa = "99999", Descripcion = "no existe", FechaUtc = DateTime.UtcNow },
        };

        var (aplicadas, omitidas) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));
        var (otraVez, _) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));

        Assert.Equal(2, aplicadas);
        Assert.Equal("no existe: no viene en esta importación.", Assert.Single(omitidas));
        Assert.Equal(0, otraVez);
        var (juntada, baja, existencia) = await ConBaseAsync(async db => (
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == a),
            await db.Variantes.AsNoTracking().SingleAsync(v => v.Id == c),
            await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == b)));
        Assert.Equal(b, juntada.JuntadaEnId);
        Assert.False(baja.Activa);
        Assert.Equal(Cantidad.Unidades(3), existencia.Cantidad);
    }

    [Fact]
    public async Task El_mayoreo_de_eleventa_se_captura_en_F3_y_se_repite_en_la_importacion_final()
    {
        var (http, caja, cajero, encargado) = await PrepararAsync();
        var (hojas, _) = await ProductoAsync(caja.SucursalId, "Hoja de color", 100, 30, eleventa: "91001");
        var (vieja, _) = await ProductoAsync(caja.SucursalId, "Pluma de temporada", 10, 500, eleventa: "91002");
        await ConBaseAsync(async db =>
        {
            db.MayoreoPorRevisar.AddRange(
                new MayoreoPorRevisar { VarianteId = hojas, PrecioMayoreoCentavos = 45, MenudeoCentavos = 60, VentasConMayoreo = 2, Cantidades = "100 ×2",
                    Motivo = "Pocas ventas", Sugerida = Cantidad.Piezas(100) },
                new MayoreoPorRevisar { VarianteId = vieja, PrecioMayoreoCentavos = 500, MenudeoCentavos = 1000, Motivo = "Al costo" });
            return await db.SaveChangesAsync();
        });

        var lista = await Leer<List<MayoreoPorRevisarDto>>(await http.GetAsync("api/catalogo/depuracion/mayoreo"));
        var delCajero = await http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo", new SolicitudMayoreo(cajero.UsuarioId, caja.SucursalId, hojas, 100_000, 45));
        var caro = await http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo", new SolicitudMayoreo(encargado.UsuarioId, caja.SucursalId, hojas, 100_000, 60));
        Assert.True((await http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo", new SolicitudMayoreo(encargado.UsuarioId, caja.SucursalId, hojas, 100_000, 45)))
            .IsSuccessStatusCode);
        Assert.True((await http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo", new SolicitudMayoreo(encargado.UsuarioId, caja.SucursalId, vieja, null, null)))
            .IsSuccessStatusCode);
        var despues = await Leer<List<MayoreoPorRevisarDto>>(await http.GetAsync("api/catalogo/depuracion/mayoreo"));

        Assert.Contains(lista, m => m.VarianteId == hojas && m.SugeridaMilesimas == 100_000 && m.Cantidades == "100 ×2");
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, caro.StatusCode);
        Assert.DoesNotContain(despues, m => m.VarianteId == hojas || m.VarianteId == vieja);
        var escala = await ConBaseAsync(db => db.Precios.AsNoTracking().SingleAsync(p => p.VarianteId == hojas && p.CantidadMinima == Cantidad.Piezas(100)));
        Assert.Equal(45, escala.PrecioCentavos);

        // En la importación final (otra base) se repiten, por el ID de eleventa: aquí se simula quitando la escala.
        var decisiones = await ConBaseAsync(db => db.DecisionesDepuracion.AsNoTracking()
            .Where(d => d.VarianteId == hojas || d.VarianteId == vieja).ToListAsync());
        await ConBaseAsync(async db =>
        {
            db.Precios.Remove(await db.Precios.SingleAsync(p => p.VarianteId == hojas && p.CantidadMinima == Cantidad.Piezas(100)));
            db.DecisionesDepuracion.RemoveRange(db.DecisionesDepuracion.Where(d => d.VarianteId == hojas || d.VarianteId == vieja));
            return await db.SaveChangesAsync();
        });
        var (aplicadas, _) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));

        Assert.Equal([TipoDecisionDepuracion.EscalaMayoreo, TipoDecisionDepuracion.SinMayoreo], decisiones.OrderBy(d => d.FechaUtc).Select(d => d.Tipo));
        Assert.Equal(2, aplicadas);
        Assert.Equal(45, (await ConBaseAsync(db => db.Precios.AsNoTracking().SingleAsync(p => p.VarianteId == hojas && p.CantidadMinima == Cantidad.Piezas(100))))
            .PrecioCentavos);
    }

    /// <summary>Las escalas base del producto, «desde:precio» de menor a mayor: "1:700 12:450".</summary>
    private Task<string> EscalasAsync(Guid variante) => ConBaseAsync(async db => string.Join(" ",
        (await db.Precios.AsNoTracking().Where(p => p.VarianteId == variante && p.PresentacionId == null && p.SucursalId == null).ToListAsync())
            .OrderBy(p => p.CantidadMinima).Select(p => $"{p.CantidadMinima.Milesimas / 1000}:{p.PrecioCentavos}")));

    private Task<int> AgregarAsync(Guid variante, params (int Desde, long Precio)[] escalas) => ConBaseAsync(async db =>
    {
        db.Precios.AddRange(escalas.Select(e => new Precio { VarianteId = variante, CantidadMinima = Cantidad.Piezas(e.Desde), PrecioCentavos = e.Precio }));
        return await db.SaveChangesAsync();
    });

    /// <summary>El mayoreo que dejó la importación: por revisar, o ya deducido desde <paramref name="deducido"/>.</summary>
    private Task<int> MayoreoDeEleventaAsync(Guid variante, long mayoreo, int? deducido = null) => ConBaseAsync(async db =>
    {
        var m = new MayoreoPorRevisar { VarianteId = variante, PrecioMayoreoCentavos = mayoreo, MenudeoCentavos = 800, Motivo = "Pocas ventas" };
        if (deducido is { } desde)
            m.Resolver("Deducido", DateTime.UtcNow, Cantidad.Piezas(desde));
        db.MayoreoPorRevisar.Add(m);
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task El_mayoreo_de_F3_no_hace_subir_el_precio_con_la_cantidad_ni_tapa_una_promocion()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (pluma, _) = await ProductoAsync(caja.SucursalId, "Pluma de gel", 0, 350); // menudeo $7
        await AgregarAsync(pluma, (3, 500), (24, 400)); // promociones de eleventa
        await MayoreoDeEleventaAsync(pluma, 600);
        Task<HttpResponseMessage> Decidir(long? desde, long? precio) => http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo",
            new SolicitudMayoreo(encargado.UsuarioId, caja.SucursalId, pluma, desde * 1000, precio));

        var masCaro = await Decidir(12, 600); // desde 3 ya sale a $5
        var otraPromocion = await Decidir(24, 450);
        var subiria = await Decidir(12, 380); // desde 24 sale a $4: 23 piezas costarían más que 24
        var bien = await Decidir(12, 450);
        var conEscala = await EscalasAsync(pluma);
        // Otra decisión cambia la escala de este mayoreo (no suma otra), y «sin mayoreo» la quita; las promociones se quedan.
        var otra = await Decidir(6, 480);
        var conOtra = await EscalasAsync(pluma);
        var sin = await Decidir(null, null);

        Assert.All(new[] { masCaro, otraPromocion, subiria }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("ya hay otro precio", (await otraPromocion.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.All(new[] { bien, otra, sin }, r => Assert.True(r.IsSuccessStatusCode));
        Assert.Equal("1:700 3:500 12:450 24:400", conEscala);
        Assert.Equal("1:700 3:500 6:480 24:400", conOtra);
        Assert.Equal("1:700 3:500 24:400", await EscalasAsync(pluma));
    }

    [Fact]
    public async Task Al_repetir_vale_la_ultima_decision_y_cambia_o_quita_la_escala_que_dedujo_la_importacion_final()
    {
        var (_, caja, _, _) = await PrepararAsync();
        // La importación final dedujo 12 a $6.50 y 12 a $6.80; en la depuración se había decidido otra cosa.
        var (sinMayoreo, _) = await ProductoAsync(caja.SucursalId, "Pegamento", 0, 400, eleventa: "92001");
        var (desdeDiez, _) = await ProductoAsync(caja.SucursalId, "Corrector", 0, 400, eleventa: "92002");
        var (dosVeces, _) = await ProductoAsync(caja.SucursalId, "Marcatextos", 0, 400, eleventa: "92003");
        await AgregarAsync(sinMayoreo, (12, 650));
        await MayoreoDeEleventaAsync(sinMayoreo, 650, deducido: 12);
        await AgregarAsync(desdeDiez, (12, 680));
        await MayoreoDeEleventaAsync(desdeDiez, 680, deducido: 12);
        await MayoreoDeEleventaAsync(dosVeces, 650);
        var hace = DateTime.UtcNow.AddHours(-1);
        var decisiones = new List<DecisionDepuracion>
        {
            new() { Tipo = TipoDecisionDepuracion.SinMayoreo, OrigenEleventa = "92001", Descripcion = "pegamento", FechaUtc = hace },
            new() { Tipo = TipoDecisionDepuracion.EscalaMayoreo, OrigenEleventa = "92002", Descripcion = "corrector", FechaUtc = hace.AddMinutes(1),
                FactorMilesimas = 10_000, PrecioCentavos = 650 },
            new() { Tipo = TipoDecisionDepuracion.EscalaMayoreo, OrigenEleventa = "92003", Descripcion = "marcatextos 120", FechaUtc = hace.AddMinutes(2),
                FactorMilesimas = 120_000, PrecioCentavos = 650 },
            new() { Tipo = TipoDecisionDepuracion.EscalaMayoreo, OrigenEleventa = "92003", Descripcion = "marcatextos 12", FechaUtc = hace.AddMinutes(3),
                FactorMilesimas = 12_000, PrecioCentavos = 650 },
        };

        var (aplicadas, omitidas) = await ConBaseAsync(db => new RegistroDepuracion(db).RepetirAsync(decisiones));

        Assert.Equal(3, aplicadas);
        Assert.Equal("marcatextos 120: después se decidió otra cosa.", Assert.Single(omitidas));
        Assert.Equal("1:800", await EscalasAsync(sinMayoreo));
        Assert.Equal("1:800 10:650", await EscalasAsync(desdeDiez));
        Assert.Equal("1:800 12:650", await EscalasAsync(dosVeces));
    }

    [Fact]
    public async Task Lo_juntado_dado_de_baja_o_con_otra_unidad_sale_de_la_revision_del_mayoreo()
    {
        var (http, caja, _, encargado) = await PrepararAsync();
        var (origen, _) = await ProductoAsync(caja.SucursalId, "Lápiz dup", 0, 350);
        var (destino, _) = await ProductoAsync(caja.SucursalId, "Lápiz dup", 0, 350);
        var (baja, _) = await ProductoAsync(caja.SucursalId, "Borrador viejo", 0, 350);
        var (liston, _) = await ProductoAsync(caja.SucursalId, "Listón", 0, 100, UnidadBase.Metro);
        foreach (var v in new[] { origen, baja, liston })
            await MayoreoDeEleventaAsync(v, 600);

        await ConBaseAsync(async db =>
        {
            var depuracion = new RegistroDepuracion(db);
            await depuracion.JuntarAsync(null, caja.SucursalId, origen, destino);
            await depuracion.DarDeBajaAsync(null, caja.SucursalId, baja);
            await depuracion.CambiarUnidadAsync(null, caja.SucursalId, liston, UnidadBase.Pieza, 10, 15000);
            return 0;
        });
        var lista = await Leer<List<MayoreoPorRevisarDto>>(await http.GetAsync("api/catalogo/depuracion/mayoreo"));
        var alJuntado = await http.PostAsJsonAsync("api/catalogo/depuracion/mayoreo", new SolicitudMayoreo(encargado.UsuarioId, caja.SucursalId, origen, 12_000, 600));

        Assert.DoesNotContain(lista, m => m.VarianteId == origen || m.VarianteId == baja || m.VarianteId == liston);
        Assert.False(alJuntado.IsSuccessStatusCode);
        Assert.Equal("1:700", await EscalasAsync(origen));
        var resoluciones = await ConBaseAsync(db => db.MayoreoPorRevisar.AsNoTracking()
            .Where(m => m.VarianteId == origen || m.VarianteId == baja || m.VarianteId == liston).Select(m => m.Resolucion).ToListAsync());
        Assert.All(resoluciones, Assert.NotNull);
    }
}
