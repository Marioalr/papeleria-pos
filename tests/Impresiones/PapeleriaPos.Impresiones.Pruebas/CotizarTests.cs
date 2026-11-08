using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Cotizar (2026-11-08, hito 3 de docs/impresion.md; el dueño: «cotizar daba un precio inferior al calculado en la impresión»): se cotiza con
/// el mismo cálculo, los mismos costos y los mismos ajustes que la impresión, la página se ajusta a la hoja como al imprimirla, la cotización
/// se guarda y se liga a lo que se imprime, y se mide qué tan preciso es cotizar en cada impresora.
/// </summary>
public sealed class CotizarTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static CostSettings Costos()
    {
        var cfg = new CostSettings();
        foreach (var p in cfg.Papers)
            p.PackPrice = p.PackSheets; // $1 por hoja
        return cfg;
    }

    private async Task GuardarCostosAsync()
    {
        await using var db = _base.Contexto();
        await _base.Registro(db).GuardarCostosAsync(PosDepruebaSucursal, Costos());
    }

    private static Guid PosDepruebaSucursal => PosDePrueba.Sucursal;

    /// <summary>Páginas carta medidas al cotizar: cobertura de la página (negro y algo de color) y su tamaño.</summary>
    private static List<CoberturaPaginaDto> Paginas(int n, double k = 0.08, double color = 0) =>
        Enumerable.Range(0, n).Select(_ => new CoberturaPaginaDto(color, color, color, k, k, 93.5, color > 0, 8.5, 11)).ToList();

    /// <summary>Lo mismo impreso, como lo mide el agente: la cobertura del área imprimible (la página carta ajustada a ella).</summary>
    private static TrabajoMedidoDto Impreso(CajaDelPos caja, string documento, DateTime cuando, int paginas, int? impresora = null, double k = 0.08,
        double color = 0, string calidad = "Standard", int copias = 1, bool duplex = false)
    {
        // Al ajustar la carta al área imprimible, la tinta de la página (k × 93.5 × 0.945 in²) cubre esa fracción del área imprimible.
        var area = (215.9 - 6) * (279.4 - 6) / (25.4 * 25.4);
        var escala = QuoteCalculator.PrintedArea(new PageCoverage(0, 0, 0, 0, 0, 0, false) { WidthIn = 8.5, HeightIn = 11 }, (215.9 - 6) / 25.4,
            (279.4 - 6) / 25.4) / area;
        return new TrabajoMedidoDto(Guid.NewGuid(), caja.Id, null, "CAJA2", "WF-6590", 7, documento, "cajero", cuando, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto(calidad, "Plain", "NorthAmericaLetter", 215.9, 279.4, color > 0, duplex, copias, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(color * escala, color * escala, color * escala, k * escala, k * escala, area,
                color > 0)).ToList(), true, null, null, cuando.AddSeconds(-20), Impresora: impresora);
    }

    private static SolicitudCotizacion Solicitud(IReadOnlyList<CoberturaPaginaDto> paginas, OpcionesCotizacionDto? opciones = null, Guid? id = null,
        string? documento = "Tarea de Juan.pdf", int? impresora = null, Guid? caja = null, bool parcial = false) =>
        new(PosDePrueba.Sucursal, null, paginas, opciones ?? new OpcionesCotizacionDto(Color: false), impresora, id ?? Guid.NewGuid(), documento,
            paginas.Count, caja, parcial);

    // ── El cálculo ──────────────────────────────────────────────────────────

    [Fact]
    public void La_pagina_se_ajusta_a_la_hoja_como_al_imprimirla()
    {
        double ancho = (215.9 - 6) / 25.4, alto = (279.4 - 6) / 25.4;
        PageCoverage Pagina(double w, double h) => new(0, 0, 0, 0.1, 0.1, w * h, false) { WidthIn = w, HeightIn = h };

        // Carta en carta: se reduce un poco, al área imprimible (como «Ajustar» en Acrobat o Edge).
        Assert.Equal(8.5 * 11 * 0.9722 * 0.9722, QuoteCalculator.PrintedArea(Pagina(8.5, 11), ancho, alto), 1);
        // A4 en carta: la reduce su alto. Antes se suponía que llenaba la carta (88.9 in²) y salía de más.
        Assert.Equal(8.27 * 11.69 * Math.Pow(alto / 11.69, 2), QuoteCalculator.PrintedArea(Pagina(8.27, 11.69), ancho, alto), 1);
        // Acostada: se gira, como lo hacen los programas.
        Assert.Equal(QuoteCalculator.PrintedArea(Pagina(8.5, 11), ancho, alto), QuoteCalculator.PrintedArea(Pagina(11, 8.5), ancho, alto), 6);
        // Sin su tamaño (lo de antes): el área imprimible completa.
        Assert.Equal(ancho * alto, QuoteCalculator.PrintedArea(new PageCoverage(0, 0, 0, 0.1, 0.1, 93.5, false), ancho, alto), 6);
    }

    [Fact]
    public void Cotizar_da_lo_mismo_que_imprimir_lo_mismo()
    {
        // Una página carta medida al cotizar y la misma página como la mide la impresión (ajustada al área imprimible): el mismo precio.
        var cfg = Costos();
        var cotizada = new PageCoverage(0.1, 0.1, 0.1, 0.2, 0.2, 93.5, true) { WidthIn = 8.5, HeightIn = 11 };
        var area = (215.9 - 6) * (279.4 - 6) / (25.4 * 25.4);
        var escala = QuoteCalculator.PrintedArea(cotizada, (215.9 - 6) / 25.4, (279.4 - 6) / 25.4) / area;
        var impresa = new PageCoverage(0.1 * escala, 0.1 * escala, 0.1 * escala, 0.2 * escala, 0.2 * escala, area, true);
        var opciones = new QuoteOptions { Quality = "StandardVivid", Copies = 3 };

        var q = QuoteCalculator.Calculate([cotizada, cotizada], opciones, cfg);
        var impreso = CostCalculator.Calculate(new JobAnalysis(QuoteCalculator.SettingsFor(opciones), [impresa, impresa], AnalysisLevel.Measured, null,
            null, TimeSpan.Zero), cfg);

        Assert.Equal(impreso.Total, q.Cost.Total, 6);
        Assert.Equal(impreso.Price, q.Price);
    }

    [Fact]
    public void La_correccion_solo_toca_la_tinta()
    {
        var cfg = Costos();
        var pagina = new PageCoverage(0.1, 0.1, 0.1, 0.2, 0.2, 93.5, true) { WidthIn = 8.5, HeightIn = 11 };

        var sin = QuoteCalculator.Calculate([pagina], new QuoteOptions(), cfg);
        var con = QuoteCalculator.Calculate([pagina], new QuoteOptions { CorrectionFactor = 1.2 }, cfg);

        Assert.Equal(sin.Cost.InkCost * 1.2m, con.Cost.InkCost, 6);
        Assert.Equal(sin.Cost.PaperCost, con.Cost.PaperCost);
        Assert.Equal(sin.Cost.Sides, con.Cost.Sides);
        // Sin corregir, para aprender la corrección con lo impreso.
        Assert.Equal(sin.Cost.InkCost, con.UncorrectedInkCost, 6);
    }

    // ── Reconocer lo cotizado ───────────────────────────────────────────────

    [Theory]
    [InlineData("Tarea.pdf", "Tarea.pdf", true)]
    [InlineData("Tarea.pdf", "Microsoft Edge - Tarea.pdf", true)]
    [InlineData("tarea.PDF", "C:\\Users\\caja\\Downloads\\Tarea.pdf", true)]
    [InlineData("Tarea.pdf", "Tarea", true)]
    [InlineData("Tarea.pdf", "Otra cosa.pdf", false)]
    [InlineData("", "Tarea.pdf", false)]
    public void El_mismo_documento_aunque_el_programa_le_cambie_el_nombre(string archivo, string documento, bool igual) =>
        Assert.Equal(igual, LoCotizado.MismoDocumento(archivo, documento));

    [Theory]
    [InlineData(1000, 1100, true)]
    [InlineData(1000, 1250, false)]
    [InlineData(500, 700, true)] // en uno chico, $2
    [InlineData(500, 701, false)]
    public void Lo_cotizado_se_cobra_si_lo_impreso_esta_cerca(long cotizado, long impreso, bool cerca) =>
        Assert.Equal(cerca, LoCotizado.DentroDeLaTolerancia(cotizado, impreso));

    [Fact]
    public void La_precision_sugiere_la_correccion_con_cinco_pares()
    {
        CotizacionImpresion Par(long cotizado, long impreso, decimal tintaCotizada, decimal tintaImpresa, bool mismos = true) => new()
        {
            Documento = "x", Tamano = "Carta", TipoPapel = "Papel normal", Calidad = "Standard", TrabajoId = Guid.NewGuid(), MismosAjustes = mismos,
            PrecioCentavos = cotizado, PrecioImpresoCentavos = impreso, TintaSinCorregir = tintaCotizada, TintaImpresa = tintaImpresa,
        };
        var cuatro = Enumerable.Range(0, 4).Select(_ => Par(1000, 1100, 1m, 1.12m)).ToList();

        var pocos = LoCotizado.Precision(cuatro);
        var cinco = LoCotizado.Precision([.. cuatro, Par(1000, 1100, 1m, 1.12m), Par(2000, 2000, 1m, 1m, mismos: false)]);

        Assert.Equal((4, 10.0, (double?)null), (pocos.Pares, pocos.DiferenciaPorcentaje, pocos.FactorSugerido));
        // El que se imprimió distinto no cuenta.
        Assert.Equal((5, 10.0, (double?)1.12), (cinco.Pares, cinco.DiferenciaPorcentaje, cinco.FactorSugerido));
    }

    // ── Guardar, ligar y cobrar lo cotizado ─────────────────────────────────

    [Fact]
    public async Task La_cotizacion_se_guarda_sola_y_lo_medido_mientras_sigue_no()
    {
        await GuardarCostosAsync();
        var id = Guid.NewGuid();
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);

        var parcial = await registro.CotizarAsync(Solicitud(Paginas(2), id: id, parcial: true), conCosto: false);
        Assert.Null(parcial.GuardadaId);
        Assert.Empty(await db.Set<CotizacionImpresion>().ToListAsync());

        var primera = await registro.CotizarAsync(Solicitud(Paginas(3), id: id, documento: @"C:\Users\caja\Downloads\Tarea de Juan.pdf"), conCosto: false);
        // Cambiar las copias la vuelve a guardar: la misma cotización.
        var segunda = await registro.CotizarAsync(Solicitud(Paginas(3), new OpcionesCotizacionDto(Color: false, Copias: 2), id), conCosto: false);

        var guardada = Assert.Single(await db.Set<CotizacionImpresion>().AsNoTracking().ToListAsync());
        Assert.Equal((id, id), (primera.GuardadaId, segunda.GuardadaId));
        Assert.Equal(("Tarea de Juan.pdf", 3, 2, 6, segunda.PrecioCentavos), (guardada.Documento, guardada.Paginas, guardada.Copias, guardada.Caras,
            guardada.PrecioCentavos));
        Assert.Equal(Ahora.AddDays(15), guardada.VenceUtc);
        Assert.Equal(HoraMexicoLocal(Ahora.AddDays(15)), segunda.ValeHastaLocal);
        var recientes = await new RegistroCotizaciones(db, _base.Pos).RecientesAsync(PosDePrueba.Sucursal);
        Assert.Equal(("Tarea de Juan.pdf", false), (Assert.Single(recientes).Documento, recientes[0].Usada));
    }

    private static DateTime HoraMexicoLocal(DateTime utc) => PapeleriaPos.Nucleo.HoraMexico.ALocal(utc);

    [Fact]
    public async Task Lo_impreso_igual_que_lo_cotizado_se_cobra_a_lo_cotizado_y_deja_el_par()
    {
        await GuardarCostosAsync();
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        var cotizacion = await registro.CotizarAsync(Solicitud(Paginas(4)), conCosto: false);

        // Se imprime una hora después con Edge, igual, pero la impresión dibuja el texto un poco más grueso (5 % más de tinta).
        _base.Pos.Hora.Ahora = _base.Pos.Hora.Ahora.AddHours(1);
        var trabajo = await registro.RegistrarAsync(Impreso(caja, "Microsoft Edge - Tarea de Juan.pdf", Ahora, 4, k: 0.084));

        Assert.Equal(cotizacion.GuardadaId, trabajo.CotizacionId);
        Assert.True(trabajo.CobraLoCotizado);
        Assert.Equal(cotizacion.PrecioCentavos, trabajo.PrecioCentavos);
        Assert.Equal(cotizacion.PrecioCentavos, trabajo.PrecioCotizadoCentavos);
        Assert.NotNull(trabajo.PrecioImpresoCentavos);
        var guardada = await db.Set<CotizacionImpresion>().AsNoTracking().SingleAsync();
        Assert.Equal((trabajo.Id, true), (guardada.TrabajoId, guardada.MismosAjustes));
        // La tinta de lo impreso contra la cotizada: 5 % más (de ahí sale la corrección).
        Assert.Equal(1.05, (double)(guardada.TintaImpresa!.Value / guardada.TintaSinCorregir), 2);

        // En F2 sale con lo cotizado, y el cajero puede cobrar lo impreso.
        var porCobrar = Assert.Single(await registro.PorCobrarAsync(PosDePrueba.Sucursal));
        Assert.Equal((trabajo.CotizacionId, true, trabajo.PrecioImpresoCentavos), (porCobrar.CotizacionId, porCobrar.CobraLoCotizado,
            porCobrar.PrecioImpresoCentavos));
        var impreso = await new RegistroCotizaciones(db, _base.Pos).CobrarLoCotizadoAsync(trabajo.Id, loCotizado: false);
        Assert.Equal((false, trabajo.PrecioImpresoCentavos!.Value), (impreso.CobraLoCotizado, impreso.PrecioCentavos));

        // Una cotización se usa una vez: otra impresión del mismo documento ya no la toma.
        var otra = await registro.RegistrarAsync(Impreso(caja, "Tarea de Juan.pdf", Ahora.AddMinutes(1), 4));
        Assert.Null(otra.CotizacionId);
    }

    [Fact]
    public async Task Lo_impreso_distinto_se_cobra_a_lo_impreso_y_el_cajero_elige()
    {
        await GuardarCostosAsync();
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        var cotizacion = await registro.CotizarAsync(Solicitud(Paginas(10)), conCosto: false);

        // Una página de más (el navegador agregó una) y en otra calidad: es lo cotizado, pero no igual.
        var trabajo = await registro.RegistrarAsync(Impreso(caja, "Tarea de Juan.pdf", Ahora.AddMinutes(10), 11, calidad: "HighQuality"));

        Assert.Equal(cotizacion.GuardadaId, trabajo.CotizacionId);
        Assert.False(trabajo.CobraLoCotizado);
        Assert.Equal(trabajo.PrecioImpresoCentavos, trabajo.PrecioCentavos);
        Assert.False((await db.Set<CotizacionImpresion>().AsNoTracking().SingleAsync()).MismosAjustes);
        var cotizado = await new RegistroCotizaciones(db, _base.Pos).CobrarLoCotizadoAsync(trabajo.Id, loCotizado: true);
        Assert.Equal(cotizacion.PrecioCentavos, cotizado.PrecioCentavos);
    }

    [Fact]
    public async Task No_se_liga_lo_que_no_es_lo_cotizado()
    {
        await GuardarCostosAsync();
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var registro = _base.Registro(db);
        await registro.CotizarAsync(Solicitud(Paginas(10)), conCosto: false);

        var otroDocumento = await registro.RegistrarAsync(Impreso(caja, "Recibo.pdf", Ahora.AddMinutes(1), 10));
        var otrasPaginas = await registro.RegistrarAsync(Impreso(caja, "Tarea de Juan.pdf", Ahora.AddMinutes(2), 3));
        var antes = await registro.RegistrarAsync(Impreso(caja, "Tarea de Juan.pdf", Ahora.AddMinutes(-30), 10));
        var vencida = await registro.RegistrarAsync(Impreso(caja, "Tarea de Juan.pdf", Ahora.AddDays(16), 10));

        Assert.All([otroDocumento, otrasPaginas, antes, vencida], t => Assert.Null(t.CotizacionId));
    }

    // ── Cotizar como se imprime ─────────────────────────────────────────────

    [Fact]
    public async Task Se_cotiza_con_lo_que_se_imprime_en_la_impresora_de_la_caja_y_su_correccion()
    {
        await GuardarCostosAsync();
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true);
        var segunda = await impresoras.AgregarAsync(PosDePrueba.Sucursal, "WF-6590 2", PerfilImpresora.WF6590, null);
        var registro = _base.Registro(db);
        // En la 2 se imprime casi todo en «Normal (colores vivos)» y doble cara; la caja imprime ahí.
        for (var i = 0; i < 3; i++)
            await registro.RegistrarAsync(Impreso(caja, $"Doc {i}.pdf", Ahora.AddMinutes(i), 4, segunda.Numero, calidad: "StandardVivid", duplex: true));
        await registro.RegistrarAsync(Impreso(caja, "Otro.pdf", Ahora.AddMinutes(5), 4, 1));
        await registro.RegistrarAsync(Impreso(caja, "Último.pdf", Ahora.AddMinutes(6), 4, segunda.Numero));

        var cotizaciones = new RegistroCotizaciones(db, _base.Pos);
        var (habituales, deLaCaja) = await cotizaciones.HabitualesAsync(PosDePrueba.Sucursal, caja.Id);

        Assert.Equal(segunda.Numero, deLaCaja);
        var deLa2 = habituales.Single(h => h.Impresora == segunda.Numero);
        Assert.Equal(("StandardVivid", true, "Plain", "Carta", 4), (deLa2.Opciones.Calidad, deLa2.Opciones.Duplex, deLa2.Opciones.TipoPapel,
            deLa2.Opciones.Tamano, deLa2.Trabajos));
        Assert.Equal(("Standard", 1), (habituales.Single(h => h.Impresora == 1).Opciones.Calidad, habituales.Single(h => h.Impresora == 1).Trabajos));

        // Sin elegir impresora, se cotiza en la de la caja; con su corrección, la tinta sube.
        var sinCorregir = await registro.CotizarAsync(Solicitud(Paginas(4, color: 0.1), new OpcionesCotizacionDto(), documento: null, caja: caja.Id),
            conCosto: true);
        await impresoras.CambiarAsync(segunda, null, null, null, factorCotizacion: 1.25);
        var corregida = await registro.CotizarAsync(Solicitud(Paginas(4, color: 0.1), new OpcionesCotizacionDto(), documento: null, caja: caja.Id),
            conCosto: true);
        Assert.Equal((segunda.Numero, 1.0), (sinCorregir.Impresora, sinCorregir.FactorCotizacion));
        Assert.Equal((segunda.Numero, 1.25), (corregida.Impresora, corregida.FactorCotizacion));
        Assert.True(corregida.Costo!.TintaCentavos > sinCorregir.Costo!.TintaCentavos);
        Assert.Equal(sinCorregir.Costo.PapelCentavos, corregida.Costo.PapelCentavos);
        await Assert.ThrowsAsync<ArgumentException>(() => impresoras.CambiarAsync(segunda, null, null, null, factorCotizacion: 3));
    }

    [Fact]
    public async Task La_impresora_dice_que_tan_preciso_es_cotizar_en_ella()
    {
        await GuardarCostosAsync();
        var caja = _base.Pos.NuevaCaja("Caja 2");
        await using var db = _base.Contexto();
        var impresoras = _base.Impresora(db);
        var primera = (await impresoras.PrimeraAsync(PosDePrueba.Sucursal, crear: true))!;
        var registro = _base.Registro(db);
        // Cinco documentos cotizados e impresos igual, con 10 % más de tinta al imprimir.
        for (var i = 0; i < 5; i++)
        {
            await registro.CotizarAsync(Solicitud(Paginas(20, k: 0.3, color: 0.2), new OpcionesCotizacionDto(), documento: $"Doc {i}.pdf", impresora: 1),
                conCosto: false);
            await registro.RegistrarAsync(Impreso(caja, $"Doc {i}.pdf", Ahora.AddMinutes(i + 1), 20, 1, k: 0.33, color: 0.22));
        }

        var dto = await impresoras.DtoAsync(primera, null);

        Assert.Equal(1.0, dto.FactorCotizacion);
        Assert.Equal(5, dto.PrecisionCotizar!.Pares);
        Assert.True(dto.PrecisionCotizar.DiferenciaPorcentaje > 0, $"{dto.PrecisionCotizar.DiferenciaPorcentaje}");
        Assert.Equal(1.1, dto.PrecisionCotizar.FactorSugerido!.Value, 2);
    }
}
