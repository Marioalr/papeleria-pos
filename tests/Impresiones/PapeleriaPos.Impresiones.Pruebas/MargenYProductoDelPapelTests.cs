using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Cada papel con su margen y su producto del inventario (el dueño, 2026-11-16): el margen propio del papel manda sobre el del papel
/// general; sin él, el general. Una caja de antes, que no los conoce, no los borra al guardar.
/// </summary>
public class MargenYProductoDelPapelTests
{
    static CostSettings Costos(decimal? margenGeneral, decimal? margenDelPapel) => new()
    {
        MarginPercent = 0,
        Scaling = ScalingMode.None,
        PaperMarginPercent = margenGeneral,
        CleaningFactor = 1.0,
        RoundTo = 0,
        MinimumPrice = 0,
        Black = new InkCartridge { Name = "Negro", Price = 1000, IsoYield = 10000 },
        Papers = [new PaperPrice { Size = PaperSize.Carta, Group = "Normal", PackPrice = 100m, PackSheets = 100, MarginPercent = margenDelPapel }],
        Maintenance = new MaintenanceBox { BoxPrice = 0, PagesPerBox = 40000 },
        Power = new PowerSettings { Watts = 0 },
    };

    static CostBreakdown Calcular(CostSettings c) => CostCalculator.Calculate(
        new JobAnalysis(new JobSettings(), [new PageCoverage(0, 0, 0, 0.05, 0.05, 93.5, false)], AnalysisLevel.Measured, null, null, TimeSpan.Zero), c);

    [Fact]
    public void El_margen_propio_del_papel_manda_sobre_el_general()
    {
        // Lo demás sin margen; el papel ($1.00 la hoja) con el suyo.
        var sinMargen = Calcular(Costos(null, null));
        Assert.Equal(1.00m, sinMargen.PaperCost);
        Assert.Equal(sinMargen.Price + 0.60m, Calcular(Costos(60, null)).Price);
        var propio = Calcular(Costos(60, 20));
        Assert.Equal(sinMargen.Price + 0.20m, propio.Price);
        Assert.Equal(20, propio.PaperMarginPercent);
    }

    [Fact]
    public void El_papel_de_un_trabajo_se_encuentra_por_su_tamano_y_su_tipo()
    {
        var c = new CostSettings();
        c.Papers[0].VarianteId = Guid.NewGuid();
        Assert.Same(c.PaperFor(PaperSize.Carta, "Normal"), c.PaperOfJob("Carta", c.MediaFor("Plain").Name));
        Assert.Null(c.PaperOfJob("", ""));
    }

    [Fact]
    public void Guardar_con_lo_propio_lo_pone_y_una_caja_de_antes_no_lo_borra()
    {
        var c = new CostSettings();
        var variante = Guid.NewGuid();
        var dto = CostosEnLaConfiguracion.De(c);
        var papeles = dto.Papeles.Select((p, i) => i == 0 ? p with { Margen = 35, VarianteId = variante, Producto = "Hoja carta", ConLoPropio = true } : p).ToList();
        var despues = CostosEnLaConfiguracion.Aplicar(c, dto with { Papeles = papeles });
        Assert.Equal((35m, variante, "Hoja carta"), (despues.Papers[0].MarginPercent!.Value, despues.Papers[0].VarianteId!.Value, despues.Papers[0].Producto));

        var deAntes = CostosEnLaConfiguracion.De(despues);
        var sinLoPropio = deAntes.Papeles.Select(p => new PapelCostoDto(p.Tamano, p.Grupo, p.PrecioPaquete, p.HojasPaquete)).ToList();
        var igual = CostosEnLaConfiguracion.Aplicar(despues, deAntes with { Papeles = sinLoPropio });
        Assert.Equal(35m, igual.Papers[0].MarginPercent);
        Assert.Equal(variante, igual.Papers[0].VarianteId);
        Assert.Throws<ArgumentException>(() => CostosEnLaConfiguracion.Aplicar(c, dto with { Papeles = [papeles[0] with { Margen = -1 }] }));
    }
}
