using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// Las tarjetas de la cola de Control Print en F2 (con los textos de Control Print), sus lotes, lo que la caja recuerda que cobró
/// (allá siguen «por cobrar») y el precio de lo que imprime la caja 2 con los costos de Control Print.
/// </summary>
public sealed class TarjetasControlPrintTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static TrabajoControlPrint Leido(JobRecordCp registro) =>
        JsonSerializer.Deserialize<TrabajoControlPrint>(JsonSerializer.Serialize(registro, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }), ProtocoloControlPrint.Json)!;

    [Fact]
    public void La_tarjeta_dice_lo_mismo_que_en_Control_Print()
    {
        var detectado = new DateTime(2026, 10, 28, 16, 5, 0, DateTimeKind.Utc);
        var registro = PorCobrarDePrueba.Trabajo(1234, detectado, precio: 15, documento: "Tesis capítulo 2.docx");
        (registro.Pages, registro.Copies, registro.Color, registro.InkC, registro.Duplex, registro.Quality, registro.PaperSize) =
            (3, 2, true, 0.2, true, "HighQuality", "Oficio");
        (registro.Label, registro.OwnerName, registro.SourcePc) = ("Juan Pérez", "Ana", "DISENO");

        var tarjeta = TarjetaControlPrint.De(Leido(registro), conCosto: false);

        Assert.Equal(1234, tarjeta.Id);
        Assert.Equal("Juan Pérez", tarjeta.Titulo);
        Assert.Equal("3 págs × 2 copias · Color · Oficio · Papel normal · Alta · Dúplex", tarjeta.Resumen);
        Assert.Equal(new DateTime(2026, 10, 28, 10, 5, 0), tarjeta.HoraLocal);
        Assert.Equal(("DISENO", "Ana", 1500L), (tarjeta.Pc, tarjeta.Atendio, tarjeta.PrecioCentavos));
        Assert.Null(tarjeta.CostoCentavos);
        Assert.Equal(112L, TarjetaControlPrint.De(Leido(registro), conCosto: true).CostoCentavos);
        Assert.Null(tarjeta.Especial);
        Assert.Null(tarjeta.RevisarHojas);

        // En modo color sin tinta de color es B/N (como en Control Print), y sin etiqueta el título es el documento.
        (registro.InkC, registro.Label, registro.Copies, registro.Quality, registro.Duplex) = (0, null, 1, "Standard", false);
        var bn = TarjetaControlPrint.De(Leido(registro), conCosto: false);
        Assert.Equal("Tesis capítulo 2.docx", bn.Titulo);
        Assert.Equal("3 págs · B/N · Oficio · Papel normal", bn.Resumen);
    }

    [Fact]
    public void Sus_chips_especial_cotizado_copias_y_revisar_hojas()
    {
        var registro = PorCobrarDePrueba.Trabajo(7, PorCobrarDePrueba.Hace(3), precio: 10);
        (registro.PriceOverride, registro.PriceKind, registro.SpecialName) = (8, PriceKind.Special, "Escuela Juárez");
        var especial = TarjetaControlPrint.De(Leido(registro), conCosto: false);
        Assert.Equal(("Escuela Juárez", 800L), (especial.Especial, especial.PrecioCentavos));

        (registro.PriceKind, registro.SpecialName) = (PriceKind.Quoted, null);
        Assert.True(TarjetaControlPrint.De(Leido(registro), conCosto: false).Cotizado);

        registro.Kind = JobKind.PanelCopy;
        Assert.True(TarjetaControlPrint.De(Leido(registro), conCosto: false).CopiasEnImpresora);

        // La impresora contó menos hojas y nadie lo ha resuelto: «Revisar hojas», con lo que contó.
        (registro.Kind, registro.PriceKind, registro.PriceOverride, registro.Sides, registro.CountedSides) = (JobKind.Print, PriceKind.Calculated, null, 4, 1);
        Assert.Equal("La impresora contó 1 de 4 hojas: ¿atasco o cancelación en la impresora?", TarjetaControlPrint.De(Leido(registro), false).RevisarHojas);
        registro.CountedSides = 0;
        Assert.StartsWith("La impresora no contó ninguna de las 4 hojas", TarjetaControlPrint.De(Leido(registro), false).RevisarHojas);
        // Ya resuelto con «solo lo que salió»: no.
        registro.PriceKind = PriceKind.Partial;
        Assert.Null(TarjetaControlPrint.De(Leido(registro), false).RevisarHojas);
    }

    [Fact]
    public void Su_version_cambia_con_lo_que_se_ve_y_no_con_el_costo()
    {
        var registro = PorCobrarDePrueba.Trabajo(5, PorCobrarDePrueba.Hace(3), precio: 10);
        var una = TarjetaControlPrint.Version([TarjetaControlPrint.De(Leido(registro), conCosto: false)]);

        Assert.Equal(una, TarjetaControlPrint.Version([TarjetaControlPrint.De(Leido(registro), conCosto: true)]));
        registro.Price = 12;
        Assert.NotEqual(una, TarjetaControlPrint.Version([TarjetaControlPrint.De(Leido(registro), conCosto: false)]));
        Assert.NotEqual(una, TarjetaControlPrint.Version([TarjetaControlPrint.De(Leido(PorCobrarDePrueba.Trabajo(5, PorCobrarDePrueba.Hace(3), precio: 10)), false)], "no contesta"));
        Assert.True(una > 0);
    }

    [Fact]
    public void Los_lotes_juntan_lo_mandado_a_dos_minutos_o_menos_como_en_Control_Print()
    {
        var base0 = new DateTime(2026, 10, 28, 16, 0, 0, DateTimeKind.Utc);
        var trabajos = new (int Id, double Minuto, long Precio)[] { (1, 0, 200), (2, 1.5, 400), (3, 3.5, 300), (4, 10, 500), (5, 12.1, 200) }
            .Select(t => (t.Id, Utc: base0.AddMinutes(t.Minuto), t.Precio)).ToList();

        var lotes = LotesDeImpresiones.Agrupar(trabajos, t => t.Utc, t => t.Precio);

        // Del más nuevo al más viejo: el 5 solo (2.1 min después del 4), el 4 solo, y 3-2-1 (a 2 min o menos uno del otro).
        Assert.Equal([[5], [4], [3, 2, 1]], lotes.Select(l => l.Trabajos.Select(t => t.Id).ToArray()));
        Assert.Equal([200L, 500, 900], lotes.Select(l => l.TotalCentavos));
        var ahora = HoraMexico.ALocal(base0);
        Assert.Equal("Hoy 10:00–10:03", lotes[2].Cuando(ahora));
        Assert.Equal("Hoy 10:12", lotes[0].Cuando(ahora));
        // Otro día: con su día y su fecha (el nombre del día, como lo escriba Windows).
        Assert.EndsWith(" 28/10 10:00–10:03", lotes[2].Cuando(ahora.AddDays(1)));
        Assert.DoesNotContain("Hoy", lotes[2].Cuando(ahora.AddDays(1)));
        Assert.Empty(LotesDeImpresiones.Agrupar(Array.Empty<int>(), _ => DateTime.UtcNow, _ => 0));
    }

    // ── Lo que la caja recuerda que cobró ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_caja_recuerda_lo_que_cobro_de_Control_Print_y_lo_suelta_al_cancelar()
    {
        var venta = Guid.NewGuid();
        var detectado = new DateTime(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc);
        await using (var db = _base.Contexto())
        {
            _base.EnLaVenta(db).CobrarControlPrint(PosDePrueba.Sucursal,
                [new TrabajoControlPrintParaCobrar(1234, detectado, "Tesis.pdf", "PRINCIPAL", 1500, 320), new TrabajoControlPrintParaCobrar(1235, detectado.AddMinutes(1), "Acta.pdf", "DISENO", 200, 40)],
                venta, detectado.AddMinutes(5));
            await db.SaveChangesAsync();
        }

        await using (var db = _base.Contexto())
        {
            var cobrados = await _base.EnLaVenta(db).CobradosAsync(PosDePrueba.Sucursal, [1234, 1235, 99]);
            Assert.Equal([1234L, 1235], cobrados.Select(c => c.TrabajoId).Order());
            Assert.All(cobrados, c => Assert.Equal(venta, c.VentaId));
            Assert.Equal((1500L, 320L), (cobrados.Single(c => c.TrabajoId == 1234).PrecioCentavos, cobrados.Single(c => c.TrabajoId == 1234).CostoCentavos));
            // Otra sucursal no los ve.
            Assert.Empty(await _base.EnLaVenta(db).CobradosAsync(Guid.NewGuid(), [1234]));

            // El mismo trabajo no queda dos veces cobrado sin soltarse (dos tickets que lo llevan a la vez).
            _base.EnLaVenta(db).CobrarControlPrint(PosDePrueba.Sucursal, [new TrabajoControlPrintParaCobrar(1234, detectado, "Tesis.pdf", "PRINCIPAL", 1500, 320)],
                Guid.NewGuid(), detectado.AddMinutes(6));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Se cancela la venta: quedan liberados (con su historia) y ya no cuentan como cobrados aquí.
        await using (var db = _base.Contexto())
        {
            await _base.EnLaVenta(db).LiberarAsync(venta);
            await db.SaveChangesAsync();
        }
        await using (var db = _base.Contexto())
        {
            Assert.Empty(await _base.EnLaVenta(db).CobradosAsync(PosDePrueba.Sucursal, [1234, 1235]));
            var historia = await _base.EnLaVenta(db).DeControlPrintEnLaVentaAsync(venta);
            Assert.Equal(2, historia.Count);
            Assert.All(historia, c => Assert.Equal(_base.Pos.Hora.GetUtcNow().UtcDateTime, c.LiberadoUtc));

            // Y se puede volver a cobrar en otra venta.
            _base.EnLaVenta(db).CobrarControlPrint(PosDePrueba.Sucursal, [new TrabajoControlPrintParaCobrar(1234, detectado, "Tesis.pdf", "PRINCIPAL", 1500, 320)],
                Guid.NewGuid(), detectado.AddMinutes(9));
            await db.SaveChangesAsync();
            Assert.Single(await _base.EnLaVenta(db).CobradosAsync(PosDePrueba.Sucursal, [1234]));
        }
    }

    // ── Lo que imprime la caja 2, con los costos de Control Print ───────────────────────────────────────────────

    [Fact]
    public async Task Lo_que_imprime_la_caja_2_se_cobra_con_los_costos_de_Control_Print()
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        TrabajoMedidoDto Medido() => new(Guid.NewGuid(), caja.Id, null, "CAJA2", "WF-6590 Series(Red)", 3, "Recibo.pdf", "cajera", _base.Pos.Hora.GetUtcNow().UtcDateTime,
            EstadoTrabajo.Impreso, new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null);
        // Los de Control Print: un mínimo de $7 (los de la sucursal, los de siempre).
        var deControlPrint = new CostSettings { MinimumPrice = 7, RoundTo = 1 };

        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        var conLosDeLaSucursal = await registro.RegistrarAsync(Medido());
        var conLosDeControlPrint = await registro.RegistrarAsync(Medido(), deControlPrint);

        Assert.NotEqual(700, conLosDeLaSucursal.PrecioCentavos);
        Assert.Equal(700, conLosDeControlPrint.PrecioCentavos);

        // Y quedan como los de la sucursal, solo si cambiaron (para cuando Control Print no conteste al arrancar).
        Assert.True(await registro.GuardarCostosSiCambiaronAsync(PosDePrueba.Sucursal, deControlPrint));
        Assert.False(await registro.GuardarCostosSiCambiaronAsync(PosDePrueba.Sucursal, deControlPrint));
        Assert.Equal(7, (await registro.CostosAsync(PosDePrueba.Sucursal)).MinimumPrice);
        var (cuantos, firma) = await registro.CuantosPorCobrarAsync(PosDePrueba.Sucursal);
        Assert.Equal(2, cuantos);
        await registro.NoVendidaAsync(conLosDeLaSucursal.Id, new SolicitudNoVendida(Guid.NewGuid(), MotivoNoVendida.SalioMal, null));
        Assert.NotEqual(firma, (await registro.CuantosPorCobrarAsync(PosDePrueba.Sucursal)).Firma);
    }

    /// <summary>
    /// Los costos que se reemplazan se quedan en su historial (2026-10-29): un Control Print reinstalado (o un error al escribirlos) no
    /// se lleva los de antes, ni la calibración de la tinta que va en ellos. Si no cambian, no se anota nada.
    /// </summary>
    [Fact]
    public async Task Los_costos_que_se_reemplazan_quedan_en_su_historial()
    {
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        var calibrados = new CostSettings { MinimumPrice = 3, RoundTo = 0.5m, MarginPercent = 90 };
        await registro.GuardarCostosAsync(PosDePrueba.Sucursal, calibrados, origen: "Configuración");
        Assert.Empty(await registro.HistorialCostosAsync(PosDePrueba.Sucursal));

        Assert.True(await registro.GuardarCostosSiCambiaronAsync(PosDePrueba.Sucursal, new CostSettings { MinimumPrice = 5, RoundTo = 1 }));
        Assert.False(await registro.GuardarCostosSiCambiaronAsync(PosDePrueba.Sucursal, new CostSettings { MinimumPrice = 5, RoundTo = 1 }));

        var antes = Assert.Single(await registro.HistorialCostosAsync(PosDePrueba.Sucursal));
        Assert.Equal("Control Print", antes.PorLosDe);
        var deAntes = System.Text.Json.JsonSerializer.Deserialize<CostSettings>(antes.CostosJson, CostSettings.Json)!;
        Assert.Equal((3m, 0.5m, 90m), (deAntes.MinimumPrice, deAntes.RoundTo, deAntes.MarginPercent));
        Assert.Equal(5m, (await registro.CostosAsync(PosDePrueba.Sucursal)).MinimumPrice);
    }
}
