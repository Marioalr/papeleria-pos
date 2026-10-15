// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows.Pruebas;

public class AnalysisTests
{
    static JobAnalysis Analyze(string spool)
    {
        using var s = File.OpenRead(Fixture.Path(spool));
        return JobAnalyzer.Analyze(s, new JobSettings(), 1, new AnalysisOptions());
    }

    [Theory]
    [InlineData("black.spl", 1)]
    [InlineData("blank.spl", 1)]
    [InlineData("text.spl", 1)]
    [InlineData("mixed3.spl", 3)]
    public void Reads_every_page_of_the_emf_spool(string spool, int pages)
    {
        var a = Analyze(spool);
        Assert.Equal(AnalysisLevel.Measured, a.Level);
        Assert.Equal(pages, a.Pages.Count);
    }

    [Fact]
    public void Reads_document_name_from_spool_header()
    {
        using var s = File.OpenRead(Fixture.Path("black.spl"));
        Assert.Equal("CPLab-black", new EmfSpoolReader(s).DocumentName);
    }

    [Fact]
    public void Full_black_page_is_100_percent_black()
    {
        var p = Analyze("black.spl").Pages[0];
        Assert.InRange(p.K, 0.995, 1.0);
        Assert.Equal(0, p.C + p.M + p.Y, 3);
        Assert.False(p.HasColor);
        // Área imprimible carta que reporta el driver: 210 × 273 mm.
        Assert.InRange(p.AreaSqIn, 88.5, 89.3);
    }

    [Fact]
    public void Full_cyan_page_uses_only_cyan()
    {
        var p = Analyze("cyan.spl").Pages[0];
        Assert.InRange(p.C, 0.995, 1.0);
        Assert.Equal(0, p.M + p.Y + p.K, 3);
        Assert.True(p.HasColor);
    }

    [Fact]
    public void Mid_gray_goes_to_black_channel()
    {
        var p = Analyze("gray50.spl").Pages[0];
        Assert.InRange(p.K, 0.49, 0.51);
        Assert.Equal(0, p.C + p.M + p.Y, 3);
    }

    [Fact]
    public void One_inch_square_measures_one_square_inch()
    {
        var p = Analyze("square1in.spl").Pages[0];
        Assert.InRange(p.K * p.AreaSqIn, 0.97, 1.03);
    }

    [Fact]
    public void Text_page_matches_what_the_driver_sends()
    {
        // Referencia: el driver manda para esta página una imagen de 600 dpi con 8.19 % de cobertura de gris.
        var p = Analyze("text.spl").Pages[0];
        Assert.InRange(p.K, 0.0819 * 0.97, 0.0819 * 1.03);
        Assert.False(p.HasColor);
    }

    [Fact]
    public void Color_content_printed_in_black_and_white_matches_driver_gray()
    {
        // Referencia: impresa en B/N, el driver manda esta página (bandas rojo/verde/azul) con 66.36 % de gris.
        var p = Analyze("rgb.spl").Pages[0];
        Assert.True(p.HasColor);
        Assert.InRange(p.Gray, 0.6636 * 0.98, 0.6636 * 1.02);

        var bw = new JobAnalysis(new JobSettings { Color = false }, [p], AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var cost = CostCalculator.Calculate(bw, new CostSettings());
        Assert.Equal(0, cost.InkC + cost.InkM + cost.InkY);
        Assert.True(cost.InkK > 0);
    }

    [Fact]
    public void Mixed_document_is_measured_per_page()
    {
        var pages = Analyze("mixed3.spl").Pages;
        Assert.True(pages[0].IsBlank);
        Assert.InRange(pages[1].K, 0.995, 1.0);
        Assert.InRange(pages[2].K, 0.079, 0.085);
    }

    [Fact]
    public void Produces_png_thumbnail_of_first_page()
    {
        var thumb = Analyze("text.spl").Thumbnail;
        Assert.NotNull(thumb);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, thumb![..4]);
    }

    [Fact]
    public void Thumbnail_skips_blank_first_page()
    {
        var a = Analyze("mixed3.spl");
        Assert.Equal(2, a.ThumbnailPage);
        Assert.Equal(1, Analyze("text.spl").ThumbnailPage);
    }

    [Fact]
    public void Non_emf_data_falls_back_to_page_estimate()
    {
        using var raw = new MemoryStream("\x1b\x01@EJL 1284.4\n"u8.ToArray());
        var a = JobAnalyzer.Analyze(raw, new JobSettings { Color = false }, 4, new AnalysisOptions());
        Assert.Equal(AnalysisLevel.Estimated, a.Level);
        Assert.Equal(4, a.Pages.Count);
        Assert.Equal(0.05, a.Pages[0].K);
    }

    [Theory]
    [InlineData("ticket-standard.xml", "Standard", "Plain", "NorthAmericaLetter", true, false, 1)]
    [InlineData("ticket-draft.xml", "Draft", "Plain", "NorthAmericaLetter", true, false, 1)]
    [InlineData("ticket-glossy.xml", "Standard", "PhotographicHighGloss", "NorthAmericaLetter", true, false, 1)]
    [InlineData("ticket-mono.xml", "Standard", "Plain", "NorthAmericaLetter", false, false, 1)]
    [InlineData("ticket-duplex.xml", "Standard", "Plain", "NorthAmericaLetter", true, true, 1)]
    [InlineData("ticket-copies2.xml", "Standard", "Plain", "NorthAmericaLetter", true, false, 2)]
    [InlineData("ticket-oficio.xml", "Standard", "Plain", "NorthAmericaLegal", true, false, 1)]
    public void Parses_print_ticket(string file, string quality, string media, string size, bool color, bool duplex, int copies)
    {
        var s = PrintTicketReader.Parse(Fixture.Text(file));
        Assert.Equal(quality, s.Quality);
        Assert.Equal(media, s.MediaType);
        Assert.Equal(size, s.MediaSizeName);
        Assert.Equal(color, s.Color);
        Assert.Equal(duplex, s.Duplex);
        Assert.Equal(copies, s.Copies);
        Assert.Equal(1, s.PagesPerSheet);
        Assert.True(s.FromPrintTicket);
    }

    [Fact]
    public void Print_ticket_gives_exact_paper_dimensions()
    {
        var letter = PrintTicketReader.Parse(Fixture.Text("ticket-standard.xml"));
        Assert.Equal(215.9, letter.MediaWidthMm, 1);
        Assert.Equal(279.4, letter.MediaHeightMm, 1);
        var legal = PrintTicketReader.Parse(Fixture.Text("ticket-oficio.xml"));
        Assert.Equal(355.6, legal.MediaHeightMm, 1);
    }
}
