// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows.Pruebas;

public class PageCostsTests
{
    static readonly CostSettings Cfg = new()
    {
        Papers = [new PaperPrice { Size = PaperSize.Carta, Group = "Normal", PackPrice = 120m, PackSheets = 500 }],
    };

    static PageCoverage Page(string spool)
    {
        using var s = File.OpenRead(Fixture.Path(spool));
        return JobAnalyzer.Analyze(s, new JobSettings(), 1, new AnalysisOptions()).Pages[0];
    }

    static CostBreakdown Cost(JobSettings s, params PageCoverage[] pages) =>
        CostCalculator.Calculate(new JobAnalysis(s, pages, AnalysisLevel.Measured, null, null, TimeSpan.Zero), Cfg);

    [Fact]
    public void Each_page_costs_what_it_would_cost_printed_alone()
    {
        // Documento de 2 hojas: una con el mínimo de tinta (cuadro de 1 in²) y otra completamente negra.
        var minimal = Page("square1in.spl");
        var full = Page("black.spl");
        var settings = new JobSettings();
        var job = Cost(settings, minimal, full);
        var pages = PageCosts.Compute(job, settings, [minimal, full]);

        Assert.Equal(Math.Round(Cost(settings, minimal).Total, 6), Math.Round(pages[0].Cost, 6));
        Assert.Equal(Math.Round(Cost(settings, full).Total, 6), Math.Round(pages[1].Cost, 6));
        Assert.True(pages[1].InkCost > pages[0].InkCost * 50);   // la hoja llena gasta muchísima más tinta
        Assert.Equal(pages[0].OtherCost, pages[1].OtherCost);    // papel, caja y luz iguales
        Assert.Equal(Math.Round(pages[1].Cost * 2, 6), Math.Round(pages[1].Price, 6)); // margen 100 %
    }

    [Fact]
    public void Pages_times_copies_add_up_to_the_job_cost()
    {
        using var s = File.OpenRead(Fixture.Path("mixed3.spl"));
        var settings = new JobSettings { Copies = 2 };
        var a = JobAnalyzer.Analyze(s, settings, 3, new AnalysisOptions());
        var cost = CostCalculator.Calculate(a, Cfg);
        var pages = PageCosts.Compute(cost, settings, a.Pages);

        Assert.Equal(Math.Round(cost.Total, 6), Math.Round(pages.Sum(p => p.Cost) * 2, 6));
        Assert.True(pages[0].Blank);
        Assert.Equal(0, pages[0].InkCost);
        Assert.True(pages[1].Cost > pages[2].Cost && pages[2].Cost > pages[0].Cost);
    }

    [Fact]
    public void Black_and_white_job_charges_color_pages_as_gray()
    {
        var color = new PageCoverage(0, 1, 1, 0, 0.7, 88.9, true);
        var settings = new JobSettings { Color = false };
        var cost = Cost(settings, color);
        var page = Assert.Single(PageCosts.Compute(cost, settings, [color]));
        Assert.False(page.Color);
        Assert.Equal(0.7, page.K);
        Assert.Equal(cost.Total, page.Cost);
    }
}
