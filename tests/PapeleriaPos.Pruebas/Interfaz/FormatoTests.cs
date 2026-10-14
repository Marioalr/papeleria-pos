using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

public class FormatoTests
{
    [Theory]
    [InlineData("$1,234.50", 123450L)]
    [InlineData("200", 20000L)]
    [InlineData("7501234567890", 750123456789000L)] // un EAN-13 cabe
    public void Lee_pesos_a_centavos(string texto, long centavos) => Assert.Equal(centavos, Formato.LeerPesos(texto));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    // Un código largo del lector en un campo de dinero no cabe en centavos: antes tumbaba la pantalla al dibujarla.
    [InlineData("12345678901234567890")]
    [InlineData("1234567890123456789012345678")]
    public void Lo_que_no_es_una_cantidad_que_quepa_es_null(string texto) => Assert.Null(Formato.LeerPesos(texto));
}
