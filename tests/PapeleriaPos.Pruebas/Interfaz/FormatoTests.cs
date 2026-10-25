using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

public class FormatoTests
{
    /// <summary>
    /// El dinero de la caja, con la regla de los números del POS (revisión del PR #138): el punto es el decimal y la coma solo
    /// separa los miles, de tres en tres. «1,250» y «1,250.50» son lo que eran; «12.5» es $12.50, y «1000.» y «1,000.» son mil
    /// (revisión de la coma: «1,000.» se rechazaba con el aviso de los centavos).
    /// </summary>
    [Theory]
    [InlineData("$1,234.50", 123450L)]
    [InlineData("200", 20000L)]
    [InlineData("1,250", 125000L)]
    [InlineData("1,250.50", 125050L)]
    [InlineData("12.5", 1250L)]
    [InlineData("1000.", 100000L)]
    [InlineData("1,000.", 100000L)]
    [InlineData("$1,000.", 100000L)]
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
    // Tres decimales tampoco (revisión de la coma, decidido y por revisar): «1.500» podría ser mil quinientos; antes era $1.50.
    [InlineData("1.500")]
    [InlineData("1.250")]
    [InlineData("12.505")]
    [InlineData("$1,250.505")]
    [InlineData("12,50.")]
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
    [InlineData("1,000.", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    // Tres decimales: si pueden ser miles, cómo escribirlos; si no, que los centavos van con dos.
    [InlineData("1.500", "Si son 1,500 pesos, escríbelo 1500 o 1,500; los centavos van con dos decimales.")]
    [InlineData("$12.505", "Si son 12,505 pesos, escríbelo 12505 o 12,505; los centavos van con dos decimales.")]
    [InlineData("1,250.505", "«1,250.505»: los centavos van con dos decimales, como 12.50.")]
    [InlineData("0.500", "«0.500»: los centavos van con dos decimales, como 12.50.")]
    public void La_coma_de_centavos_dice_como_escribirlo(string texto, string? mensaje) => Assert.Equal(mensaje, Formato.ProblemaDePesos(texto));

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

    /// <summary>Los enteros, sin punto ninguno (revisión de la coma): «1.000» era 1, «12.000» era 12 y «3.0» era 3.</summary>
    [Theory]
    [InlineData("12", 12L)]
    [InlineData("1,000", 1000L)]
    [InlineData("2.5", null)]
    [InlineData("2,5", null)]
    [InlineData("1.000", null)]
    [InlineData("12.000", null)]
    [InlineData("3.0", null)]
    [InlineData("12.", null)]
    [InlineData("$3", null)]
    public void Los_enteros_con_la_misma_regla(string texto, long? entero) => Assert.Equal(entero, Formato.LeerEntero(texto));

    [Theory]
    [InlineData("1.000", "Va entero, sin punto: si son 1,000, escríbelo 1000 o 1,000.")]
    [InlineData("12.000", "Va entero, sin punto: si son 12,000, escríbelo 12000 o 12,000.")]
    [InlineData("3.0", "«3.0» va entero, sin punto ni decimales.")]
    [InlineData("2.5", "«2.5» va entero, sin punto ni decimales.")]
    [InlineData("2,5", "Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).")]
    [InlineData("1,000", null)]
    [InlineData("12", null)]
    [InlineData("doce", null)]
    public void Un_entero_con_punto_dice_como_escribirlo(string texto, string? mensaje) => Assert.Equal(mensaje, Formato.ProblemaDeEntero(texto));

    /// <summary>Un porcentaje con «$» son pesos (revisión de la coma: Ctrl+D tomaba «$5» como 5 %).</summary>
    [Theory]
    [InlineData("$5", "Escribe el porcentaje sin «$»: «$5» son pesos.")]
    [InlineData(" $ 12.5 ", "Escribe el porcentaje sin «$»: «$ 12.5» son pesos.")]
    [InlineData("2,5", "Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).")]
    [InlineData("5", null)]
    [InlineData("$", null)]
    [InlineData("cinco", null)]
    public void Un_porcentaje_con_pesos_dice_como_escribirlo(string texto, string? mensaje)
    {
        Assert.Equal(mensaje, Formato.ProblemaDePorcentaje(texto));
        if (texto.Contains('$'))
            Assert.Null(Formato.LeerCantidad(texto));
    }

    /// <summary>
    /// El aviso de cómo se escribe un número (Etiquetas, Sugerido) quita solo el suyo al corregirlo (revisión de la coma): antes
    /// cualquier número bien escrito borraba el error que ya estaba.
    /// </summary>
    [Fact]
    public void El_aviso_de_como_se_escribe_solo_quita_el_suyo()
    {
        string? error = "«Copias» es un servicio: no lleva etiqueta.", anterior = null;
        Formato.AvisoDeEscritura(ref error, ref anterior, null);
        Assert.Equal("«Copias» es un servicio: no lleva etiqueta.", error);

        Formato.AvisoDeEscritura(ref error, ref anterior, Formato.ComaDeDecimales("2,5"));
        Assert.Equal("Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).", error);
        Formato.AvisoDeEscritura(ref error, ref anterior, null);
        Assert.Null(error);

        Formato.AvisoDeEscritura(ref error, ref anterior, Formato.ProblemaDeEntero("1.000"));
        error = "No se pudo guardar la hoja.";
        Formato.AvisoDeEscritura(ref error, ref anterior, null);
        Assert.Equal("No se pudo guardar la hoja.", error);
    }

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
