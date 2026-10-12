using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class ErrorDeDedoTests
{
    [Theory]
    [InlineData("MANIAL", "MANILA")] // dos volteadas
    [InlineData("PUMTO", "PUNTO")] // una cambiada
    [InlineData("LAPZ", "LAPIZ")] // una de menos
    [InlineData("SOBRES", "SOBRE")] // una de más
    [InlineData("CUADRENO", "CUADERNO")]
    public void Una_letra_de_diferencia_es_un_error_de_dedo(string escrito, string palabra)
    {
        Assert.True(ErrorDeDedo.DifierenEnUna(escrito, palabra));
    }

    [Theory]
    [InlineData("MANILA", "MALINA")] // dos cambiadas que no están juntas
    [InlineData("PLUMA", "PL")]
    [InlineData("ABCD", "WXYZ")]
    public void Dos_o_mas_letras_ya_no(string escrito, string palabra)
    {
        Assert.False(ErrorDeDedo.DifierenEnUna(escrito, palabra));
    }

    [Fact]
    public void Se_compara_con_el_principio_de_cada_palabra_y_solo_con_cuatro_letras_o_mas()
    {
        string[] palabras = ["SOBRE", "MANILA", "CARTA", "7501234567890"];

        Assert.True(ErrorDeDedo.Coincide("MANIAL", palabras));
        Assert.True(ErrorDeDedo.Coincide("CRATA", palabras));
        // El principio, como siempre.
        Assert.True(ErrorDeDedo.Coincide("MAN", palabras));
        // Con menos de cuatro letras sobran los parecidos, y los números no tienen errores de dedo.
        Assert.False(ErrorDeDedo.Coincide("MNA", palabras));
        Assert.False(ErrorDeDedo.Coincide("7502", palabras));
    }
}
