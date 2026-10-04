// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Medicion.Pruebas;

public class CostCalculatorTests
{
    // Configuración sencilla para verificar la aritmética: sin factores extra.
    static CostSettings Simple() => new()
    {
        MarginPercent = 0,
        CleaningFactor = 1.0,
        Black = new InkCartridge { Name = "Negro", Price = 1000, IsoYield = 10000 },
        Cyan = new InkCartridge { Name = "Cian", Price = 700, IsoYield = 7000 },
        Magenta = new InkCartridge { Name = "Magenta", Price = 700, IsoYield = 7000 },
        Yellow = new InkCartridge { Name = "Amarillo", Price = 700, IsoYield = 7000 },
        Papers = [new PaperPrice { Size = PaperSize.Carta, Group = "Normal", PackPrice = 125m, PackSheets = 500 }],
        Maintenance = new MaintenanceBox { BoxPrice = 400, PagesPerBox = 40000 },
        Power = new PowerSettings { Watts = 0 },
    };

    static JobAnalysis Job(JobSettings s, params PageCoverage[] pages) =>
        new(s, pages, AnalysisLevel.Measured, null, null, TimeSpan.Zero);

    static readonly PageCoverage IsoBlackPage = new(0, 0, 0, 0.05, 0.05, 93.5, false);

    [Fact]
    public void Iso_page_costs_cartridge_price_divided_by_yield()
    {
        var cost = CostCalculator.Calculate(Job(new JobSettings(), IsoBlackPage), Simple());
        Assert.Equal(0.10m, Math.Round(cost.InkCostK, 6)); // 1000 / 10000
        Assert.Equal(0, cost.InkCostC);
    }

    [Fact]
    public void Iso_color_page_costs_each_color_by_its_yield()
    {
        var page = new PageCoverage(0.05, 0.05, 0.05, 0.05, 0.1, 93.5, true);
        var cost = CostCalculator.Calculate(Job(new JobSettings(), page), Simple());
        Assert.Equal(0.10m, Math.Round(cost.InkCostC, 6)); // 700 / 7000
        Assert.Equal(0.40m, Math.Round(cost.InkCost, 6));
    }

    [Fact]
    public void Copies_duplex_and_sheets_are_counted()
    {
        var s = new JobSettings { Duplex = true, Copies = 2 };
        var cost = CostCalculator.Calculate(Job(s, IsoBlackPage, IsoBlackPage, IsoBlackPage), Simple());
        Assert.Equal(6, cost.Sides);
        Assert.Equal(4, cost.Sheets);                         // 2 hojas por copia
        Assert.Equal(1.00m, cost.PaperCost);                  // 4 × 0.25
        Assert.Equal(0.06m, Math.Round(cost.MaintenanceCost, 6)); // 6 × 400/40000
        Assert.Equal(0.60m, Math.Round(cost.InkCostK, 6));
    }

    [Fact]
    public void Pages_per_sheet_reduce_sides_and_ink()
    {
        var s = new JobSettings { PagesPerSheet = 2 };
        var cost = CostCalculator.Calculate(Job(s, IsoBlackPage, IsoBlackPage, IsoBlackPage), Simple());
        Assert.Equal(2, cost.Sides);
        Assert.Equal(0.15m, Math.Round(cost.InkCostK, 6));    // 3 páginas a la mitad de tamaño
    }

    [Fact]
    public void Monochrome_prints_color_content_with_black_only()
    {
        var red = new PageCoverage(0, 1, 1, 0, 0.7, 93.5, true);
        var cost = CostCalculator.Calculate(Job(new JobSettings { Color = false }, red), Simple());
        Assert.Equal(0, cost.InkC + cost.InkM + cost.InkY);
        Assert.Equal(0.7 * 93.5, cost.InkK, 6);
    }

    [Fact]
    public void Quality_media_and_cleaning_factors_multiply_ink()
    {
        var cfg = Simple();
        cfg.CleaningFactor = 1.1;
        var s = new JobSettings { Quality = "HighQuality", MediaType = "PhotographicHighGloss" };
        var cost = CostCalculator.Calculate(Job(s, IsoBlackPage), cfg);
        Assert.Equal(1.3 * 1.5 * 1.1, cost.InkFactor, 6);
        Assert.Equal("Fotográfico brillante", cost.PaperGroup);
    }

    [Theory]
    [InlineData(10.00, 0, 10)]
    [InlineData(10.01, 0, 11)]
    [InlineData(5.00, 100, 10)]
    [InlineData(3.20, 50, 5)]
    [InlineData(0.30, 100, 1)]
    [InlineData(0, 100, 0)]
    public void Price_rounds_up_to_whole_peso(decimal cost, decimal margin, decimal expected) =>
        Assert.Equal(expected, CostCalculator.PriceFor(cost, margin, 1m));

    [Fact]
    public void Price_does_not_jump_on_tiny_rounding_errors() =>
        Assert.Equal(12m, CostCalculator.PriceFor(6.0000000001m, 100, 1m));

    [Theory]
    [InlineData(215.9, 279.4, PaperSize.Carta)]
    [InlineData(279.4, 215.9, PaperSize.Carta)]
    [InlineData(210, 297, PaperSize.A4)]
    [InlineData(215.9, 355.6, PaperSize.Oficio)]   // Legal
    [InlineData(216, 341, PaperSize.Oficio)]       // México-Oficio
    [InlineData(216, 330, PaperSize.Oficio)]       // Folio
    [InlineData(101.6, 152.4, PaperSize.Otro)]     // 4×6
    public void Classifies_paper_by_dimensions(double w, double h, PaperSize expected) =>
        Assert.Equal(expected, CostCalculator.Classify(w, h));

    [Fact]
    public void Missing_size_price_is_scaled_from_letter()
    {
        var s = new JobSettings { MediaWidthMm = 215.9, MediaHeightMm = 355.6 }; // Legal, sin precio propio
        var cost = CostCalculator.Calculate(Job(s, IsoBlackPage), Simple());
        Assert.Equal(PaperSize.Oficio, cost.PaperSize);
        Assert.Equal(Math.Round(0.25m * (decimal)(355.6 / 279.4), 4), cost.PaperCost);
        Assert.Contains(cost.Warnings, w => w.Contains("se estimó desde carta"));
    }

    [Fact]
    public void Warns_when_cartridge_prices_are_missing()
    {
        var cfg = new CostSettings();
        cfg.Black.Price = 0;
        var cost = CostCalculator.Calculate(Job(new JobSettings(), IsoBlackPage), cfg);
        Assert.Contains(cost.Warnings, w => w.Contains("Cartucho Negro"));
    }

    [Fact]
    public void Paper_is_priced_by_pack_and_defaults_match_the_business()
    {
        var pack = new PaperPrice { PackPrice = 120m, PackSheets = 500 };
        Assert.Equal(0.24m, pack.PricePerSheet);
        Assert.Equal(0m, new PaperPrice { PackPrice = 10m, PackSheets = 0 }.PricePerSheet);

        var d = new CostSettings();
        Assert.All(new[] { d.Black, d.Cyan, d.Magenta, d.Yellow }, c => Assert.Equal(1200m, c.Price));
        Assert.Equal(850m, d.Maintenance.BoxPrice);
        Assert.Equal(3m, d.Power.PricePerKwh);
        Assert.Equal(100m, d.MarginPercent);
        // Solo falta capturar el papel.
        Assert.All(d.MissingPrices(), m => Assert.StartsWith("Papel", m));
    }

    [Fact]
    public void Extras_apply_per_page_sheet_and_job()
    {
        var cfg = Simple();
        cfg.Extras =
        [
            new ExtraCost { Name = "Página", Amount = 0.10m, Per = ExtraPer.Page },
            new ExtraCost { Name = "Hoja", Amount = 0.20m, Per = ExtraPer.Sheet },
            new ExtraCost { Name = "Trabajo", Amount = 1m, Per = ExtraPer.Job },
        ];
        var s = new JobSettings { Duplex = true };
        var cost = CostCalculator.Calculate(Job(s, IsoBlackPage, IsoBlackPage), cfg);
        Assert.Equal(0.10m * 2 + 0.20m * 1 + 1m, cost.ExtrasCost);
    }
}
