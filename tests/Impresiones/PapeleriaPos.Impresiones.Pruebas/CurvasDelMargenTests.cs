using System.Text.Json;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Las curvas del margen (el dueño, 2026-11-08: «cuanta más tinta lleva una hoja menor es el margen, y cuantas más hojas se imprimen menor es el
/// margen»): cada cara con el margen de su tinta, por el factor de las caras del trabajo, con las cantidades exactas de cada punto y recta
/// entre ellos sobre el logaritmo de x.
/// </summary>
public sealed class CurvasDelMargenTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private const double Carta = 93.5;

    private static List<CurvePoint> Puntos(params (decimal X, decimal Y)[] puntos) => puntos.Select(p => new CurvePoint(p.X, p.Y)).ToList();

    /// <summary>Costos sencillos para hacer las cuentas a mano: sin papel, caja, luz ni depreciación; solo la tinta y el margen.</summary>
    private static CostSettings SoloTinta(List<CurvePoint> porTinta, List<CurvePoint> porCaras)
    {
        var c = new CostSettings
        {
            Scaling = ScalingMode.ByCurves, RoundTo = 0, MinimumPrice = 0, CleaningFactor = 1,
            Maintenance = new MaintenanceBox { BoxPrice = 0, PagesPerBox = 0 }, Power = new PowerSettings { Watts = 0 },
            Depreciation = new PrinterDepreciation { Enabled = false },
            Curves = new MarginCurves { ByCoverage = porTinta, BySides = porCaras },
        };
        // 1,000 pesos por 10,000 páginas al 5 % de 93.5 in²: un centavo por cada «in² al 1 %» exacto de cobertura.
        foreach (var canal in new[] { InkChannel.Black, InkChannel.Cyan, InkChannel.Magenta, InkChannel.Yellow })
            (c.Ink(canal).Price, c.Ink(canal).IsoYield) = (1000, 10000);
        return c;
    }

    private static JobAnalysis Trabajo(IEnumerable<PageCoverage> paginas, bool color = false, int copias = 1, int porHoja = 1, bool duplex = false) =>
        new(new JobSettings { Color = color, Copies = copias, PagesPerSheet = porHoja, Duplex = duplex }, paginas.ToList(), AnalysisLevel.Measured, null,
            null, TimeSpan.Zero);

    private static PageCoverage Negro(double fraccion) => new(0, 0, 0, fraccion, fraccion, Carta, false);

    // ── La curva ─────────────────────────────────────────────────────────────

    [Fact]
    public void Entre_dos_puntos_es_recta_sobre_el_logaritmo_y_fuera_se_queda_en_el_extremo()
    {
        var curva = Puntos((5, 300), (20, 180), (50, 110), (100, 70));

        Assert.Equal(300, MarginCurves.Evaluate(curva, 0));
        Assert.Equal(300, MarginCurves.Evaluate(curva, 1));
        Assert.Equal(300, MarginCurves.Evaluate(curva, 5));
        Assert.Equal(180, MarginCurves.Evaluate(curva, 20));
        Assert.Equal(70, MarginCurves.Evaluate(curva, 100));
        Assert.Equal(70, MarginCurves.Evaluate(curva, 400));
        // 10 es la mitad entre 5 y 20 en el logaritmo (√(5·20)): la mitad entre 300 y 180.
        Assert.Equal(240, MarginCurves.Evaluate(curva, 10));
        // Sobre x en línea recta no: 12.5 (la mitad «normal») ya va más abajo de la mitad.
        Assert.True(MarginCurves.Evaluate(curva, 12.5m) < 240);
        Assert.Null(MarginCurves.Evaluate([], 5));
        // En cualquier orden.
        Assert.Equal(240, MarginCurves.Evaluate(Puntos((20, 180), (5, 300)), 10));
    }

    [Fact]
    public void Las_sugeridas_dejan_lo_tipico_como_hoy_y_bajan_con_la_tinta_y_con_las_caras()
    {
        var curvas = new MarginCurves();
        Assert.Equal(100, MarginCurves.Evaluate(curvas.ByCoverage, 5));
        Assert.Equal(100, MarginCurves.Evaluate(curvas.ByCoverage, 25));
        Assert.True(MarginCurves.Evaluate(curvas.ByCoverage, 100) < MarginCurves.Evaluate(curvas.ByCoverage, 40));
        Assert.Equal(100, MarginCurves.Evaluate(curvas.BySides, 1));
        Assert.Equal(100, MarginCurves.Evaluate(curvas.BySides, 100));
        Assert.True(MarginCurves.Evaluate(curvas.BySides, 1000) < MarginCurves.Evaluate(curvas.BySides, 250));

        // Hojas de texto (5 % de negro) de 1 a 100 caras, con el papel a $0.20: el mismo precio que hoy (los tramos de fábrica).
        var hoy = new CostSettings();
        hoy.Papers.First(p => p is { Size: PaperSize.Carta, Group: "Normal" }).PackPrice = 100;
        var conCurvas = hoy.Clone();
        conCurvas.Scaling = ScalingMode.ByCurves;
        foreach (var caras in new[] { 1, 2, 5, 10, 20, 50, 100 })
        {
            var trabajo = Trabajo(Enumerable.Repeat(Negro(0.05), caras));
            Assert.Equal(CostCalculator.Calculate(trabajo, hoy).Price, CostCalculator.Calculate(trabajo, conCurvas).Price);
        }
    }

    // ── El precio ────────────────────────────────────────────────────────────

    [Fact]
    public void Cada_cara_lleva_el_margen_de_su_tinta_por_el_factor_de_las_caras_del_trabajo()
    {
        var c = SoloTinta(Puntos((5, 300), (20, 180), (50, 110), (100, 70)), Puntos((1, 100), (10, 85), (100, 65), (500, 55)));
        // Dos caras: una de texto (5 %) y una toda negra (100 %). Dos caras → factor entre 1 y 10.
        var trabajo = Trabajo([Negro(0.05), Negro(1.0)]);

        var r = CostCalculator.Calculate(trabajo, c);

        decimal factor = MarginCurves.Evaluate(c.Curves.BySides, 2)!.Value;
        Assert.Equal(factor, r.SidesFactor);
        Assert.Equal([300 * factor / 100, 70 * factor / 100], r.PageMargins!);
        var tinta = PageCosts.Compute(r, trabajo.Settings, trabajo.Pages).Select(p => p.InkCost).ToList();
        decimal esperado = tinta[0] * (1 + 3 * factor / 100) + tinta[1] * (1 + 0.7m * factor / 100);
        Assert.Equal(Math.Round(esperado, 2), r.Price);
        // La tinta promedio de sus caras y de dónde salió el margen.
        Assert.Equal(52.5m, r.CoveragePercent);
        Assert.StartsWith("curva: de 5 % a 100 % de tinta → de 300 % a 70 % × ", r.MarginTier);
        Assert.Contains("(2 caras)", r.MarginTier);
        // El margen del trabajo es el promedio que da el mismo precio (así cobra «solo lo que salió», que guarda uno solo).
        Assert.InRange(CostCalculator.PriceFor(r.Total, r.MarginPercent, 0) - r.Price, -0.02m, 0.02m);
        // Cada página con el margen de su cara: la suma de las páginas es el trabajo.
        Assert.Equal(r.Price, Math.Round(PageCosts.Compute(r, trabajo.Settings, trabajo.Pages).Sum(p => p.Price), 2));
    }

    [Fact]
    public void Una_cara_lleva_el_texto_de_su_curva_y_el_factor_lo_dicen_las_caras_de_todas_las_copias()
    {
        var c = SoloTinta(Puntos((5, 200), (50, 100)), Puntos((1, 100), (100, 50)));

        // 10 copias de una página: 10 caras → factor a la mitad del logaritmo entre 1 y 100: 75 %.
        var r = CostCalculator.Calculate(Trabajo([Negro(0.05)], copias: 10), c);

        Assert.Equal(75, r.SidesFactor);
        Assert.Equal(150, r.MarginPercent);
        Assert.Equal("curva: 5 % de tinta → 200 % × 75 % (10 caras)", r.MarginTier);
    }

    [Fact]
    public void Con_varias_paginas_por_hoja_la_tinta_de_la_cara_es_la_de_sus_paginas_reducidas_y_el_duplex_cuenta_dos_caras()
    {
        // Cuatro páginas al 10 % en una cara (4 por hoja): la cara lleva 10 %. Cinco: la segunda cara solo una página reducida, 2.5 %.
        Assert.Equal(10, CostCalculator.SideCoverage(Enumerable.Repeat(Negro(0.10), 4), false, 4));
        Assert.Equal(2.5m, CostCalculator.SideCoverage([Negro(0.10)], false, 4));
        // A color, la suma de los cuatro canales (puede pasar de 100 %).
        Assert.Equal(160, CostCalculator.SideCoverage([new PageCoverage(0.4, 0.4, 0.4, 0.4, 0.4, Carta, true)], true, 1));
        // En B/N cuenta el gris.
        Assert.Equal(30, CostCalculator.SideCoverage([new PageCoverage(0.4, 0.4, 0.4, 0.1, 0.3, Carta, true)], false, 1));

        var c = SoloTinta(Puntos((2.5m, 200), (10, 100)), Puntos((1, 100), (2, 50)));
        var r = CostCalculator.Calculate(Trabajo(Enumerable.Repeat(Negro(0.10), 5), porHoja: 4, duplex: true), c);
        // Dos caras (en una hoja a doble cara) → factor 50 %; la primera cara (10 %) 100 % × 50 %; la segunda (2.5 %) 200 % × 50 %.
        Assert.Equal((2, 1), (r.Sides, r.Sheets));
        Assert.Equal([50, 50, 50, 50, 100], r.PageMargins!);
    }

    [Fact]
    public void El_papel_con_margen_propio_los_extras_el_redondeo_y_el_minimo_siguen_valiendo()
    {
        var c = SoloTinta(Puntos((5, 100), (50, 100)), Puntos((1, 100), (2, 100)));
        c.Papers.First(p => p is { Size: PaperSize.Carta, Group: "Normal" }).PackPrice = 500; // $1 la hoja
        c.PaperMarginPercent = 20;
        c.Extras = [new ExtraCost { Name = "Engargolado", Amount = 10, Per = ExtraPer.Job }];

        var r = CostCalculator.Calculate(Trabajo([Negro(0.05)], copias: 2), c);

        // Tinta: 5 % de 93.5 in² con un centavo por in²·% → $0.0935… por cara; × 2 caras con 100 %; papel $2 con 20 %; extra $10 con el
        // margen promedio (100 %).
        decimal tinta = r.InkCost;
        Assert.Equal(Math.Round(tinta * 2 + 2 * 1.2m + 10 * 2, 2), r.Price);
        Assert.Equal(100, r.MarginPercent);

        c.RoundTo = 1;
        Assert.Equal(Math.Ceiling(tinta * 2 + 2.4m + 20), CostCalculator.Calculate(Trabajo([Negro(0.05)], copias: 2), c).Price);

        c.Extras = [];
        c.MinimumPrice = 50;
        var conMinimo = CostCalculator.Calculate(Trabajo([Negro(0.05)]), c);
        Assert.True(conMinimo.MinimumApplied);
        Assert.Equal(50, conMinimo.Price);
    }

    [Fact]
    public void El_precio_especial_nunca_sube_del_calculado_con_las_curvas()
    {
        var c = SoloTinta(Puntos((5, 20), (50, 10)), Puntos((1, 100), (2, 100)));
        c.Special.MarginPercent = 50;
        var cotizado = QuoteCalculator.Calculate([Negro(0.05)], new QuoteOptions { Color = false }, c);
        Assert.Equal(cotizado.Price, cotizado.SpecialPrice);
    }

    [Fact]
    public void Con_los_otros_modos_las_curvas_no_cambian_nada()
    {
        var c = new CostSettings();
        var trabajo = Trabajo(Enumerable.Repeat(Negro(0.05), 3));
        var antes = CostCalculator.Calculate(trabajo, c);
        c.Curves = new MarginCurves { ByCoverage = Puntos((1, 900), (2, 900)), BySides = Puntos((1, 200), (2, 200)) };
        var despues = CostCalculator.Calculate(trabajo, c);
        Assert.Equal((antes.Price, antes.MarginPercent, antes.MarginTier), (despues.Price, despues.MarginPercent, despues.MarginTier));
        Assert.Null(despues.PageMargins);
    }

    // ── Guardadas: compatibles con lo de antes y con Control Print ──────────

    [Fact]
    public void Los_costos_de_antes_y_los_de_Control_Print_sin_curvas_toman_las_sugeridas_y_no_cambian_de_modo()
    {
        var deAntes = JsonSerializer.Serialize(new CostSettings(), CostSettings.Json);
        var sinCurvas = System.Text.Json.Nodes.JsonNode.Parse(deAntes)!.AsObject();
        sinCurvas.Remove("Curves");
        var leidos = JsonSerializer.Deserialize<CostSettings>(sinCurvas.ToJsonString(), CostSettings.Json)!;
        Assert.Equal(ScalingMode.ByJobCost, leidos.Scaling);
        Assert.Equal(MarginCurves.DefaultByCoverage().Select(p => (p.X, p.Y)), leidos.Curves.ByCoverage.Select(p => (p.X, p.Y)));
        Assert.Equal(MarginCurves.DefaultBySides().Select(p => (p.X, p.Y)), leidos.Curves.BySides.Select(p => (p.X, p.Y)));

        // «Curves»: null (escrito a mano) tampoco truena.
        sinCurvas["Curves"] = null;
        Assert.NotEmpty(JsonSerializer.Deserialize<CostSettings>(sinCurvas.ToJsonString(), CostSettings.Json)!.Curves.ByCoverage);

        // Con las curvas elegidas, ida y vuelta (el modo como texto, como Control Print).
        var c = new CostSettings { Scaling = ScalingMode.ByCurves };
        c.Curves.ByCoverage[0].Y = 250;
        var json = JsonSerializer.Serialize(c, CostSettings.Json);
        Assert.Contains("\"ByCurves\"", json);
        var vuelta = JsonSerializer.Deserialize<CostSettings>(json, CostSettings.Json)!;
        Assert.Equal((ScalingMode.ByCurves, 250m), (vuelta.Scaling, vuelta.Curves.ByCoverage[0].Y));
    }

    [Fact]
    public void La_configuracion_de_Control_Print_se_importa_sin_curvas_y_quedan_las_sugeridas()
    {
        var archivo = Path.Combine(Path.GetTempPath(), $"cp-{Guid.NewGuid():N}.db");
        try
        {
            using (var conexion = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={archivo};Pooling=False"))
            {
                conexion.Open();
                using var comando = conexion.CreateCommand();
                comando.CommandText = """
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
                    INSERT INTO settings VALUES ('cost', '{ "MarginPercent": 90, "Scaling": "BySheetCost" }');
                    """;
                comando.ExecuteNonQuery();
            }
            var costos = ImportacionControlPrint.LeerCostos(archivo);
            Assert.Equal((90m, ScalingMode.BySheetCost), (costos.MarginPercent, costos.Scaling));
            Assert.Equal(MarginCurves.DefaultBySides().Count, costos.Curves.BySides.Count);
        }
        finally
        {
            File.Delete(archivo);
        }
    }

    // ── En la Configuración ──────────────────────────────────────────────────

    private static CurvasMargenDto Curvas(IReadOnlyList<PuntoCurvaDto>? porTinta = null, IReadOnlyList<PuntoCurvaDto>? porCaras = null) => new(
        porTinta ?? [new(5, 300), new(20, 180), new(50, 110), new(100, 70)],
        porCaras ?? [new(1, 100), new(10, 85), new(100, 65), new(500, 55)]);

    [Fact]
    public void En_la_Configuracion_se_eligen_las_curvas_con_sus_puntos_exactos_y_la_bitacora_dice_que_cambiaron()
    {
        var antes = new CostSettings();
        var dto = CostosEnLaConfiguracion.De(antes);
        Assert.Equal(MargenDeImpresiones.PorCostoDelTrabajo, dto.Margen);
        Assert.Equal(MarginCurves.DefaultByCoverage().Count, dto.Curvas!.PorTinta.Count);

        // En desorden: se guardan en orden de x.
        var pedidas = Curvas([new(50, 110), new(5, 300), new(100, 70), new(20, 180)]);
        var despues = CostosEnLaConfiguracion.Aplicar(antes, dto with { Margen = MargenDeImpresiones.PorCurvas, Curvas = pedidas });

        Assert.Equal(ScalingMode.ByCurves, despues.Scaling);
        Assert.Equal([5m, 20m, 50m, 100m], despues.Curves.ByCoverage.Select(p => p.X));
        Assert.Equal(MargenDeImpresiones.PorCurvas, CostosEnLaConfiguracion.De(despues).Margen);
        Assert.Equal(["cómo se calcula el margen", "la curva del margen por la tinta de cada cara", "la curva del margen por las caras del trabajo"],
            CostosEnLaConfiguracion.Cambios(antes, despues));

        // Una caja de antes no manda las curvas: se quedan como estaban.
        var sinCurvas = CostosEnLaConfiguracion.Aplicar(despues, CostosEnLaConfiguracion.De(despues) with { Curvas = null });
        Assert.Equal([5m, 20m, 50m, 100m], sinCurvas.Curves.ByCoverage.Select(p => p.X));
    }

    [Theory]
    [InlineData("un punto", "al menos 2 puntos")]
    [InlineData("repetido", "Dos puntos de la curva por la tinta de cada cara están en 20")]
    [InlineData("tinta cero", "La tinta de un punto")]
    [InlineData("tinta de más", "La tinta de un punto")]
    [InlineData("margen de más", "El margen del punto de 20 % de tinta va de 0 a 1,000 %")]
    [InlineData("caras con decimales", "enteras")]
    [InlineData("factor de más", "El factor del punto de 10 caras va de 0 a 200 %")]
    [InlineData("demasiados", "hasta 12 puntos")]
    public void Lo_que_no_sirve_en_una_curva_se_dice_sin_cambiar_nada(string caso, string mensaje)
    {
        var curvas = caso switch
        {
            "un punto" => Curvas(porTinta: [new(5, 300)]),
            "repetido" => Curvas(porTinta: [new(20, 300), new(20, 180)]),
            "tinta cero" => Curvas(porTinta: [new(0, 300), new(20, 180)]),
            "tinta de más" => Curvas(porTinta: [new(5, 300), new(401, 180)]),
            "margen de más" => Curvas(porTinta: [new(5, 300), new(20, 1001)]),
            "caras con decimales" => Curvas(porCaras: [new(1, 100), new(2.5m, 90)]),
            "factor de más" => Curvas(porCaras: [new(1, 100), new(10, 201)]),
            _ => Curvas(porTinta: Enumerable.Range(1, 13).Select(i => new PuntoCurvaDto(i, 100)).ToList()),
        };
        var antes = new CostSettings();
        var ex = Assert.Throws<ArgumentException>(() =>
            CostosEnLaConfiguracion.Aplicar(antes, CostosEnLaConfiguracion.De(antes) with { Curvas = curvas }));
        Assert.Contains(mensaje, ex.Message);
    }

    // ── Cada impresora con sus márgenes ──────────────────────────────────────

    private static SolicitudConsumiblesImpresora Botellas(decimal precio) =>
        new(Guid.NewGuid(), [new("Negro", precio, 4300), new("Cian", precio, 7300), new("Magenta", precio, 7300), new("Amarillo", precio, 7300)]);

    private static TrabajoMedidoDto Impreso(CajaDelPos caja, DateTime cuando, int? impresora = null, int paginas = 3, double negro = 0.05) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", "WF-6590", 7, "Tarea.pdf", "cajero", cuando, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, negro, negro, Carta, false)).ToList(), true, null, null,
            cuando.AddSeconds(-20), Impresora: impresora);

    [Fact]
    public async Task La_L5590_puede_tener_sus_margenes_y_valen_con_sus_botellas_completas()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var registro = _base.Registro(db);
        await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true);
        var l5590 = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "L5590", PerfilImpresora.L5590, null);
        var deLaSucursal = await registro.CostosAsync(PosDePrueba.Sucursal);
        var margenes = CostosEnLaConfiguracion.Margenes(deLaSucursal) with
        {
            Margen = MargenDeImpresiones.PorCurvas, Curvas = Curvas(porTinta: [new(5, 400), new(100, 400)], porCaras: [new(1, 100), new(1000, 100)]),
        };

        var cambios = await impresoras.GuardarMargenesAsync(l5590, new SolicitudMargenesImpresora(Guid.NewGuid(), margenes), deLaSucursal);
        Assert.Contains("los márgenes de la L5590 (ahora los suyos)", cambios);
        Assert.Contains("cómo se calcula el margen de la L5590", cambios);
        Assert.DoesNotContain(cambios, c => c.Contains("cartucho"));

        // Sin el precio de sus botellas cobra como una WF-6590: con los márgenes de la sucursal.
        var sinPrecios = await registro.RegistrarAsync(Impreso(caja, _base.Pos.Hora.Ahora.UtcDateTime, impresora: 2));
        Assert.Equal(100, sinPrecios.MargenPorcentaje);

        await impresoras.GuardarConsumiblesAsync(l5590, Botellas(300));
        var conPrecios = await registro.RegistrarAsync(Impreso(caja, _base.Pos.Hora.Ahora.UtcDateTime, impresora: 2));
        var deLa1 = await registro.RegistrarAsync(Impreso(caja, _base.Pos.Hora.Ahora.UtcDateTime, impresora: 1));
        Assert.Equal(400, conPrecios.MargenPorcentaje);
        Assert.Equal(100, deLa1.MargenPorcentaje);
        // De dónde salió, con su costo.
        Assert.Equal("curva: 5 % de tinta → 400 % × 100 % (3 caras)", RegistroImpresiones.Costo(conPrecios).Margen);
        Assert.StartsWith("mayoreo:", RegistroImpresiones.Costo(deLa1).Margen);
        Assert.Contains((await impresoras.ConsumiblesPropiosAsync(PosDePrueba.Sucursal)), p => p.Margenes?.Margen == MargenDeImpresiones.PorCurvas);

        // Volver a los de la sucursal.
        Assert.Equal(["los márgenes de la L5590 (vuelve a los de la sucursal)"],
            await impresoras.GuardarMargenesAsync(l5590, new SolicitudMargenesImpresora(Guid.NewGuid(), null), deLaSucursal));
        Assert.Equal(100, (await registro.RegistrarAsync(Impreso(caja, _base.Pos.Hora.Ahora.UtcDateTime, impresora: 2))).MargenPorcentaje);

        // Una WF-6590 usa los de la sucursal: no tiene los suyos.
        var primera = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            impresoras.GuardarMargenesAsync(primera, new SolicitudMargenesImpresora(Guid.NewGuid(), margenes), deLaSucursal));
    }

    // ── El probador y la comparación ─────────────────────────────────────────

    [Fact]
    public async Task El_probador_da_el_margen_de_cada_cara_el_factor_y_el_precio_junto_al_de_hoy()
    {
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);

        var prueba = await registro.ProbarCurvasAsync(PosDePrueba.Sucursal,
            new SolicitudProbarCurvas(Guid.NewGuid(), null, Curvas(), 10, 10, false, "Carta", "Plain"));

        Assert.Equal(240, prueba.MargenPorTinta);
        Assert.Equal(85, prueba.Factor);
        Assert.Equal(204, prueba.Margen);
        Assert.True(prueba.PrecioCentavos > prueba.PrecioHoyCentavos);
        Assert.Equal(100, prueba.MargenHoyPorcentaje);
        Assert.InRange(prueba.CostoCentavos - (prueba.TintaCentavos + prueba.PapelCentavos + prueba.OtrosCentavos), -1, 1);
        // Las curvas no se guardan ni se eligen.
        Assert.Equal(ScalingMode.ByJobCost, (await registro.CostosAsync(PosDePrueba.Sucursal)).Scaling);

        await Assert.ThrowsAsync<ArgumentException>(() => registro.ProbarCurvasAsync(PosDePrueba.Sucursal,
            new SolicitudProbarCurvas(Guid.NewGuid(), null, Curvas(), 150, 10, false, "Carta", "Plain")));
        await Assert.ThrowsAsync<ArgumentException>(() => registro.ProbarCurvasAsync(PosDePrueba.Sucursal,
            new SolicitudProbarCurvas(Guid.NewGuid(), null, Curvas(porTinta: [new(5, 100)]), 10, 10, true, "Carta", "Plain")));
    }

    [Fact]
    public async Task La_comparacion_vuelve_a_poner_precio_a_lo_impreso_en_los_ultimos_dias_con_las_curvas()
    {
        var caja = _base.Pos.NuevaCaja("Caja 1");
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        var ahora = _base.Pos.Hora.Ahora.UtcDateTime;
        var texto = await registro.RegistrarAsync(Impreso(caja, ahora.AddDays(-2), paginas: 10));
        var lleno = await registro.RegistrarAsync(Impreso(caja, ahora.AddDays(-1), paginas: 2, negro: 0.9));
        var copias = await registro.RegistrarCopiasAsync(new SolicitudCopias(Guid.NewGuid(), caja.Id, null, 5, false, "Carta", false, null, null));
        var viejo = await registro.RegistrarAsync(Impreso(caja, ahora.AddDays(-40)));
        var noVendida = await registro.RegistrarAsync(Impreso(caja, ahora.AddHours(-1)));
        await registro.NoVendidaAsync(noVendida.Id, new SolicitudNoVendida(Guid.NewGuid(), MotivoNoVendida.SalioMal, null));

        var comparacion = await registro.CompararCurvasAsync(PosDePrueba.Sucursal, new SolicitudCompararCurvas(Guid.NewGuid(), null, Curvas()));

        Assert.Equal(3, comparacion.Trabajos);
        Assert.Equal(texto.PrecioCentavos + lleno.PrecioCentavos + copias.PrecioCentavos, comparacion.CobradoCentavos);
        // Hoy, con los mismos costos, da lo que se cobró.
        Assert.Equal(comparacion.CobradoCentavos, comparacion.HoyCentavos);
        // Con estas curvas, el texto sube (300 % en vez de 100 %) y lo muy lleno baja (70 %).
        var deTexto = Assert.Single(comparacion.Mayores, d => d.FechaLocal == HoraMexico.ALocal(texto.ImpresoUtc));
        Assert.True(deTexto.ConCurvasCentavos > deTexto.HoyCentavos);
        var deLleno = Assert.Single(comparacion.Mayores, d => d.FechaLocal == HoraMexico.ALocal(lleno.ImpresoUtc));
        Assert.True(deLleno.ConCurvasCentavos < deLleno.HoyCentavos);
        Assert.Equal(90, deLleno.Tinta);
        Assert.StartsWith("curva: 90 % de tinta", deLleno.Margen);
        Assert.DoesNotContain(comparacion.Mayores, d => d.FechaLocal == HoraMexico.ALocal(viejo.ImpresoUtc));
        Assert.Equal(comparacion.Mayores.OrderByDescending(d => Math.Abs(d.ConCurvasCentavos - d.HoyCentavos)).ToList(), comparacion.Mayores);
    }
}
