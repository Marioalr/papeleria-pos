using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>Cómo se escriben y se leen los folios (2026-11-22, <see cref="Folios"/>): sin letra, sin «-»; con ella, como siempre.</summary>
public class FoliosTests
{
    [Theory]
    [InlineData("", 4812, "4812")]
    [InlineData(null, 1, "1")]
    [InlineData("B", 123, "B-123")]
    [InlineData("CEL", 7, "CEL-7")]
    [InlineData("", 1234567, "1234567")]
    public void Se_escribe_sin_guion_si_no_lleva_letra(string? serie, int folio, string esperado) =>
        Assert.Equal(esperado, Folios.Texto(serie, folio));

    [Fact]
    public void Sin_folio_no_hay_texto() => Assert.Null(Folios.Texto("", (int?)null));

    [Theory]
    [InlineData("4812", null, 4812)]
    [InlineData("004812", null, 4812)]
    [InlineData(" 004812 ", null, 4812)]
    [InlineData("-004812", "", 4812)]
    [InlineData("-4812", "", 4812)]
    [InlineData("A-004812", "A", 4812)]
    [InlineData("a4812", "A", 4812)]
    [InlineData("cel-000007", "CEL", 7)]
    [InlineData("000000", null, 0)]
    public void Se_lee_con_y_sin_letra(string texto, string? serie, int numero) =>
        Assert.Equal(new FolioLeido(serie, numero), Folios.Leer(texto));

    [Theory]
    [InlineData("")]
    [InlineData("folio")]
    [InlineData("ABCD-1")]
    [InlineData("A--1")]
    [InlineData("12 34")]
    [InlineData("7501234567890123")]
    public void Lo_que_no_es_folio_no_se_lee(string texto) => Assert.Null(Folios.Leer(texto));

    [Theory]
    [InlineData("004812", "-4812")]
    [InlineData("4812", "-4812")]
    [InlineData("B-000123", "B-000123")]
    public void Inequivoco_le_pone_guion_al_sin_letra(string folio, string esperado) => Assert.Equal(esperado, Folios.Inequivoco(folio));

    [Fact]
    public void La_serie_de_la_principal_se_ve_como_sin_letra()
    {
        Assert.Equal("Sin letra", Folios.SerieParaVer(Folios.SinLetra));
        Assert.Equal("B", Folios.SerieParaVer("B"));
        Assert.Equal(Folios.SinLetra, Folios.SerieDe("004812"));
        Assert.Equal("B", Folios.SerieDe("B-000123"));
    }

    /// <summary>La primera caja (la principal) ya no pide serie: solo su nombre.</summary>
    [Fact]
    public void La_caja_de_la_tienda_nueva_no_pide_serie()
    {
        Assert.Null(TiendaNueva.RevisarCaja("Caja principal"));
        Assert.NotNull(TiendaNueva.RevisarCaja(""));
    }
}
