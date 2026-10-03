using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class NombresParecidosTests
{
    [Theory]
    [InlineData("Pluma punto fino Bic Azul", "PLUMA PUNTO FINO BIC AZUL")]
    [InlineData("Pluma punto fino Bic Azul", "Pluma Bic azul punto fino")]
    [InlineData("Pluma punto fino Bic Azul", "Pluma p. fino Bic Azul")]
    [InlineData("Cuaderno profesional raya Scribe 100 hojas · Lisa", "Cuad. profesional raya Scribe 100 hojas Lisa")]
    [InlineData("Lápiz adhesivo Pritt 20 g", "LAPIZ ADHESIVO PRITT 20 G")]
    public void Son_el_mismo(string a, string b)
    {
        Assert.True(NombresParecidos.SonElMismo(a, b));
    }

    [Theory]
    [InlineData("Pluma punto fino Bic Azul", "Pluma punto fino Bic Negro")]
    [InlineData("Pluma punto fino Bic Azul", "Pluma punto fino Pelikan Azul")]
    [InlineData("Caja 1", "Caja 12")]
    [InlineData("Cuaderno 100 hojas", "Cuaderno 200 hojas")]
    [InlineData("Peña", "Pena")]
    [InlineData("Pila Duracell AA", "Pila Duracell AAA")]
    [InlineData("Pluma azul", "Pluma azul fino")]
    public void No_son_el_mismo(string a, string b)
    {
        Assert.False(NombresParecidos.SonElMismo(a, b));
    }

    [Fact]
    public void Agrupa_solo_los_que_son_el_mismo()
    {
        string[] nombres = ["Pluma punto fino Bic Azul", "Goma Pelikan", "PLUMA BIC AZUL P. FINO", "Pluma punto fino Bic Negro", "GOMA PELIKAN"];

        var grupos = NombresParecidos.Agrupar(nombres, n => n);

        Assert.Equal(2, grupos.Count);
        Assert.Contains(grupos, g => g.Order(StringComparer.Ordinal).SequenceEqual(["PLUMA BIC AZUL P. FINO", "Pluma punto fino Bic Azul"]));
        Assert.Contains(grupos, g => g.Order(StringComparer.Ordinal).SequenceEqual(["GOMA PELIKAN", "Goma Pelikan"]));
    }
}
