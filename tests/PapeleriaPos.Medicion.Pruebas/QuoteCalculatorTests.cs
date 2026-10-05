// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Medicion.Pruebas;

public class QuoteCalculatorTests
{
    static readonly PageCoverage Text = new(0, 0, 0, 0.05, 0.05, 93.5, false);
    static readonly PageCoverage Photo = new(0.4, 0.4, 0.4, 0.1, 0.5, 80, true);

    static CostSettings Cfg()
    {
        var cfg = new CostSettings();
        foreach (var p in cfg.Papers) p.PackPrice = p.PackSheets; // $1 por hoja
        return cfg;
    }

    [Theory]
    [InlineData(null, "1,2,3,4,5,6,7,8,9,10")]
    [InlineData("", "1,2,3,4,5,6,7,8,9,10")]
    [InlineData("3", "3")]
    [InlineData("1-3, 5", "1,2,3,5")]
    [InlineData("8-", "8,9,10")]
    [InlineData("-2", "1,2")]
    [InlineData("5, 1–2, 2", "1,2,5")]
    [InlineData("9-20", "9,10")]
    public void Page_ranges(string? text, string expected) =>
        Assert.Equal(expected, string.Join(",", QuoteCalculator.ParsePageRange(text, 10)));

    [Theory]
    [InlineData("0", "no es un número")]
    [InlineData("a-3", "no es un número")]
    [InlineData("5-2", "mayor que la última")]
    [InlineData("11", "tiene 10 páginas")]
    [InlineData("-", "no es un rango")]
    public void Invalid_page_ranges_explain_why(string text, string message)
    {
        var ex = Assert.Throws<FormatException>(() => QuoteCalculator.ParsePageRange(text, 10));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Quote_prices_like_a_real_print()
    {
        var cfg = Cfg();
        var doc = new[] { Photo, Text, Text, Text };
        var q = QuoteCalculator.Calculate(doc, new QuoteOptions { Copies = 2, Duplex = true }, cfg);

        Assert.Equal(8, q.Cost.Sides);
        Assert.Equal(4, q.Cost.Sheets);                   // dúplex: el papel se cobra por hoja
        Assert.Equal(4m, q.Cost.PaperCost);
        Assert.Equal(2, q.Cost.ColorSides);               // la portada en cada copia
        // Mismo resultado que el calculador de impresiones con la hoja carta.
        var area = (215.9 - 6) * (279.4 - 6) / (25.4 * 25.4);
        var same = CostCalculator.Calculate(new JobAnalysis(q.Settings, doc.Select(p => p with { AreaSqIn = area }).ToList(),
            AnalysisLevel.Measured, null, null, TimeSpan.Zero), cfg);
        Assert.Equal(same.Price, q.Price);
        Assert.Equal(4, q.PageCosts.Count);
        Assert.True(q.SpecialPrice <= q.Price);
    }

    [Fact]
    public void Options_change_the_price()
    {
        var cfg = Cfg();
        var doc = Enumerable.Repeat(Photo, 10).ToList();
        decimal color = QuoteCalculator.Calculate(doc, new QuoteOptions(), cfg).Price;
        decimal bw = QuoteCalculator.Calculate(doc, new QuoteOptions { Color = false }, cfg).Price;
        decimal glossy = QuoteCalculator.Calculate(doc, new QuoteOptions { MediaType = "PhotographicHighGloss" }, cfg).Price;
        decimal oficio = QuoteCalculator.Calculate(doc, new QuoteOptions { Size = PaperSize.Oficio }, cfg).Price;
        Assert.True(bw < color);
        Assert.True(glossy > color);                      // más tinta en papel fotográfico
        Assert.True(oficio > color);                      // más área impresa

        var some = QuoteCalculator.Calculate(doc, new QuoteOptions { PageRange = "1-3" }, cfg);
        Assert.Equal([1, 2, 3], some.Pages);
        Assert.Equal(3, some.Cost.Sides);

        var nup = QuoteCalculator.Calculate(doc, new QuoteOptions { PagesPerSheet = 2 }, cfg);
        Assert.Equal(5, nup.Cost.Sheets);
        Assert.Equal(PaperSize.Oficio, QuoteCalculator.Calculate(doc, new QuoteOptions { Size = PaperSize.Oficio }, cfg).Cost.PaperSize);
        Assert.Equal(PaperSize.A4, QuoteCalculator.Calculate(doc, new QuoteOptions { Size = PaperSize.A4 }, cfg).Cost.PaperSize);
    }
}
