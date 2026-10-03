using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class TextoTests
{
    [Theory]
    [InlineData("Bic", "BIC")]
    [InlineData("  pelikán  ", "PELIKAN")]
    [InlineData("Baco   Ñandú", "BACO ÑANDU")]
    public void Normalizar_ignora_mayusculas_acentos_y_espacios(string texto, string esperado)
    {
        Assert.Equal(esperado, Texto.Normalizar(texto));
    }

    [Fact]
    public void La_marca_guarda_su_nombre_normalizado()
    {
        var marca = new Marca { Nombre = " Pelikán " };

        Assert.Equal("Pelikán", marca.Nombre);
        Assert.Equal("PELIKAN", marca.NombreNormalizado);
    }
}
