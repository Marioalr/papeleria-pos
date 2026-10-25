using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

public class FormatoTests
{
    /// <summary>
    /// El dinero de la caja, con la regla de los números del POS (revisión del PR #138): el punto es el decimal y la coma solo
    /// separa los miles, de tres en tres. «1,250» y «1,250.50» son lo que eran; «12.5» es $12.50 y «1.250», $1.25.
    /// </summary>
    [Theory]
    [InlineData("$1,234.50", 123450L)]
    [InlineData("200", 20000L)]
    [InlineData("1,250", 125000L)]
    [InlineData("1,250.50", 125050L)]
    [InlineData("12.5", 1250L)]
    [InlineData("1.250", 125L)]
    [InlineData("12.505", 1251L)]
    [InlineData(" $ 12.50 ", 1250L)]
    [InlineData("1,234,567.89", 123456789L)]
    [InlineData("7501234567890", 750123456789000L)] // un EAN-13 cabe
    public void Lee_pesos_a_centavos(string texto, long centavos) => Assert.Equal(centavos, Formato.LeerPesos(texto));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    // Una coma que podría ser de decimales no se adivina: antes «12,50» era $1,250.00.
    [InlineData("12,50")]
    [InlineData("1,5")]
    [InlineData("0,50")]
    [InlineData("1,2345")]
    [InlineData("1,23,456")]
    [InlineData("1.234,50")]
    // Tampoco los espacios por dentro.
    [InlineData("12 50")]
    // Un código largo del lector en un campo de dinero no cabe en centavos: antes tumbaba la pantalla al dibujarla.
    [InlineData("12345678901234567890")]
    [InlineData("1234567890123456789012345678")]
    public void Lo_que_no_es_una_cantidad_que_quepa_es_null(string texto) => Assert.Null(Formato.LeerPesos(texto));

    /// <summary>Lo que se rechaza por la coma dice cómo escribirlo, con lo escrito ya corregido si se puede; lo demás, nada.</summary>
    [Theory]
    [InlineData("12,50", "Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).")]
    [InlineData("$12,5", "Los centavos van con punto: $12.5 (la coma solo separa los miles: 1,250.00).")]
    [InlineData("1.234,50", "Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).")]
    [InlineData("1,250", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void La_coma_de_centavos_dice_como_escribirlo(string texto, string? mensaje) => Assert.Equal(mensaje, Formato.ComaDeCentavos(texto));

    [Fact]
    public void La_coma_de_decimales_dice_como_escribirlo() =>
        Assert.Equal("Los decimales van con punto: 1.5 (la coma solo separa los miles: 1,000).", Formato.ComaDeDecimales("1,5"));

    /// <summary>
    /// Las cantidades y los porcentajes, con la misma regla (revisión del PR #138): «2,5», «1,5» y «12,50» no se adivinan (antes,
    /// según la pantalla, 25, 15 y 1250, o 2.5, 1.5 y 12.5); «1,000» son mil (antes, en el mostrador, uno). Lo que trae «$» es
    /// dinero, no cuántos. Los enteros, sin decimales.
    /// </summary>
    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("1,000", 1000.0)]
    [InlineData("1,250.5", 1250.5)]
    [InlineData("0.125", 0.125)]
    [InlineData("-3", -3.0)]
    [InlineData("2,5", null)]
    [InlineData("1,5", null)]
    [InlineData("12,50", null)]
    [InlineData("$5", null)]
    [InlineData("2 5", null)]
    [InlineData("tres", null)]
    public void Las_cantidades_con_la_misma_regla(string texto, double? cantidad) =>
        Assert.Equal(cantidad is { } c ? (decimal)c : null, Formato.LeerCantidad(texto));

    [Theory]
    [InlineData("12", 12L)]
    [InlineData("1,000", 1000L)]
    [InlineData("2.5", null)]
    [InlineData("2,5", null)]
    public void Los_enteros_con_la_misma_regla(string texto, long? entero) => Assert.Equal(entero, Formato.LeerEntero(texto));

    /// <summary>
    /// La importación de los productos en Excel no cambia: lee con la misma regla, y ahí sí se toleran los espacios de los miles
    /// («1 234»), que en la caja no se adivinan.
    /// </summary>
    [Fact]
    public void El_Excel_lee_igual_que_antes()
    {
        Assert.Equal(1234m, PapeleriaPos.Nucleo.Catalogo.LecturaDeCeldas.NumeroDeTexto("1 234", out _));
        Assert.Null(Formato.LeerNumero("1 234", out _));
        Assert.Null(PapeleriaPos.Nucleo.Catalogo.LecturaDeCeldas.NumeroDeTexto("12,50", out var coma));
        Assert.True(coma);
        Assert.Equal(1250.5m, PapeleriaPos.Nucleo.Catalogo.LecturaDeCeldas.NumeroDeTexto("$1,250.50", out _));
    }
}
