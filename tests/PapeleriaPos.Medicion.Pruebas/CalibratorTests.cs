// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Medicion.Pruebas;

public class CalibratorTests
{
    static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly CostSettings Cfg = new();
    // Con los valores por defecto un cartucho negro XXL equivale a 46,750 in²: 1 punto = 467.5 in².
    const double KPoint = 467.5;

    static PrinterStatus S(int minute, int k, long life, long mono, long color = 1000, int? box = 50, int c = 80) => new()
    {
        TakenUtc = T0.AddMinutes(minute), InkK = k, InkC = c, InkM = 80, InkY = 80, Maintenance = box,
        LifeCount = life, MonoCount = mono, ColorCount = color,
    };

    static CalibrationJob J(int minute, double inkK, int sides = 1, int colorSides = 0, double inkC = 0) =>
        new(T0.AddMinutes(minute), sides, colorSides, inkC, 0, 0, inkK);

    static List<CalibrationJob> Jobs(int from, int count, double inkK) =>
        Enumerable.Range(from, count).Select(m => J(m, inkK)).ToList();

    static ChannelCalibration Black(CalibrationResult r) => r.Channels.Single(c => c.Channel == InkChannel.Black);

    [Fact]
    public void Accurate_model_keeps_factor_at_one()
    {
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500), S(100, 50, 1010, 510)], Jobs(1, 10, KPoint), Cfg);
        var k = Black(r);
        Assert.Equal(10, k.ObservedPoints);
        Assert.Equal(10, k.EstimatedPoints, 3);
        Assert.Equal(1.0, k.Ratio);
        Assert.Equal(1.0, k.Factor);
        Assert.Equal(CalibrationQuality.Reliable, k.Quality);
        Assert.Equal(0, r.ExternalMonoPages);
    }

    [Fact]
    public void Printer_using_more_ink_raises_the_factor_gradually()
    {
        // Bajó 12 puntos cuando se estimaron 10: real/estimado = 1.2, con peso 12/20.
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500), S(100, 48, 1010, 510)], Jobs(1, 10, KPoint), Cfg);
        var k = Black(r);
        Assert.Equal(1.2, k.Ratio!.Value, 3);
        Assert.Equal(1.12, k.Factor, 3);
    }

    [Fact]
    public void Cartridge_replacement_is_excluded()
    {
        var statuses = new[] { S(0, 50, 1000, 500), S(100, 40, 1010, 510), S(200, 100, 1010, 510), S(300, 90, 1020, 520) };
        var jobs = Jobs(1, 10, KPoint).Concat(Jobs(201, 10, KPoint)).ToList();
        var r = Calibrator.Calibrate(statuses, jobs, Cfg);
        Assert.Equal(20, Black(r).ObservedPoints);
        Assert.Equal(1.0, Black(r).Ratio);
        var change = Assert.Single(r.Changes);
        Assert.Equal("Negro", change.Supply);
        Assert.Equal((40, 100), (change.From, change.To));
    }

    [Fact]
    public void Pages_from_other_sources_are_estimated()
    {
        // La impresora contó 20 páginas B/N; desde esta PC solo 10 (las otras 10 son copias en el equipo).
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500), S(100, 40, 1020, 520)], Jobs(1, 10, KPoint), Cfg);
        Assert.Equal(10, r.ExternalMonoPages);
        Assert.Equal(10, r.OurSides);
        Assert.Equal(20, Black(r).EstimatedPoints, 3);
        Assert.Equal(1.0, Black(r).Ratio);
    }

    [Fact]
    public void Maintenance_box_life_is_measured_in_pages()
    {
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500, box: 50), S(100, 60, 5000, 4500, box: 40)], [], Cfg);
        Assert.Equal(10, r.MaintenanceObservedPoints);
        Assert.Equal(40000, r.MaintenancePagesPerBox);
        Assert.Equal(CalibrationQuality.Reliable, r.MaintenanceQuality);
    }

    [Fact]
    public void Little_data_does_not_change_prices()
    {
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500), S(100, 59, 1002, 502)], Jobs(1, 2, KPoint), Cfg);
        Assert.Equal(CalibrationQuality.Collecting, Black(r).Quality);
        Assert.Equal(1.0, Black(r).Factor);
        Assert.Null(r.MaintenancePagesPerBox);
    }

    [Fact]
    public void Single_reading_has_no_data()
    {
        var r = Calibrator.Calibrate([S(0, 60, 1000, 500)], [], Cfg);
        Assert.All(r.Channels, c => Assert.Equal(CalibrationQuality.NoData, c.Quality));
    }

    [Fact]
    public void Calibration_changes_cost_but_not_the_model_estimate()
    {
        var cfg = new CostSettings
        {
            CleaningFactor = 1.0,
            Black = new InkCartridge { Name = "Negro", Price = 1000, IsoYield = 10000 },
            Calibration = new CalibrationSettings { K = 1.5 },
        };
        var page = new PageCoverage(0, 0, 0, 0.05, 0.05, 93.5, false);
        var a = new JobAnalysis(new JobSettings(), [page], AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var cost = CostCalculator.Calculate(a, cfg);
        Assert.Equal(0.15m, Math.Round(cost.InkCostK, 6));   // 0.10 × 1.5
        Assert.Equal(0.05 * 93.5, cost.InkK, 6);              // la estimación queda sin calibrar
        Assert.Equal(1.5, cost.CalK);

        cfg.Calibration.Enabled = false;
        Assert.Equal(0.10m, Math.Round(CostCalculator.Calculate(a, cfg).InkCostK, 6));
    }

    [Fact]
    public void Color_sides_are_counted_per_page()
    {
        var color = new PageCoverage(0.1, 0, 0, 0, 0.1, 88.9, true);
        var mono = new PageCoverage(0, 0, 0, 0.05, 0.05, 88.9, false);
        var a = new JobAnalysis(new JobSettings { Copies = 2 }, [color, mono, mono], AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var c = CostCalculator.Calculate(a, new CostSettings());
        Assert.Equal(6, c.Sides);
        Assert.Equal(2, c.ColorSides);

        var bw = a with { Settings = new JobSettings { Copies = 2, Color = false } };
        Assert.Equal(0, CostCalculator.Calculate(bw, new CostSettings()).ColorSides);
    }
}
