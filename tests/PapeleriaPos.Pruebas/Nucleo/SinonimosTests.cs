using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class SinonimosTests
{
    [Theory]
    [InlineData(" Bolígrafo ", "BOLIGRAFO")]
    [InlineData("FOAMY", "FOAMY")]
    [InlineData("papel china", null)]
    [InlineData("x", null)]
    public void Cada_lado_es_una_sola_palabra_sin_acentos(string escrito, string? normal)
    {
        Assert.Equal(normal, Sinonimo.Normalizar(escrito));
    }

    [Fact]
    public void Vale_la_palabra_y_desde_cuatro_letras_su_principio()
    {
        var sinonimos = new SinonimosDeBusqueda([("BOLIGRAFO", "LAPICERO"), ("BOLIGRAFO", "PLUMA"), ("GOMA", "BORRADOR")]);

        Assert.Equal([("BOLIGRAFO", "LAPICERO"), ("BOLIGRAFO", "PLUMA")], sinonimos.De("BOLIGRAFO"));
        Assert.Equal(2, sinonimos.De("BOLI").Count);
        // Con menos de cuatro letras, solo la palabra entera: «bol» también es bolsa.
        Assert.Empty(sinonimos.De("BOL"));
        Assert.Equal([("GOMA", "BORRADOR")], sinonimos.De("GOMA"));
        Assert.Empty(SinonimosDeBusqueda.Ninguno.De("GOMA"));
    }
}
