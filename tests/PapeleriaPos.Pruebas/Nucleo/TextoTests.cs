using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class TextoTests
{
    [Theory]
    [InlineData("Bic", "BIC")]
    [InlineData("  pelikán  ", "PELIKAN")]
    [InlineData("Baco   Ñandú", "BACO ÑANDU")]
    // Los acentos sueltos que quedan en un nombre (2026-10-24): sin ellos, y sin dejar dos espacios.
    [InlineData("Lapiz¨ Azul", "LAPIZ AZUL")]
    [InlineData("Mu´ñeca  ´ grande", "MUÑECA GRANDE")]
    [InlineData("Cinta`  Canela^", "CINTA CANELA")]
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
