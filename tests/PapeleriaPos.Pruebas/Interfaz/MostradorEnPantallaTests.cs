using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que se señala en la pantalla (Ctrl+M, entrega 13), sin la página: qué sale primero, a dónde llevan las flechas, cuánto
/// agrega lo escrito en el cuadro y las iniciales mientras no hay foto.
/// </summary>
public class MostradorEnPantallaTests
{
    private static ExhibidorDto Exhibidor(string nombre) => new(Guid.NewGuid(), nombre, TipoExhibidor.Exhibidor, null, null, []);

    private static ExhibidorDto Temporada(string nombre, DiaDelAnio? desde, DiaDelAnio? hasta) => new(Guid.NewGuid(), nombre, TipoExhibidor.Temporada, desde, hasta, []);

    [Fact]
    public void Primero_la_temporada_que_corre_y_luego_los_exhibidores_la_que_no_corre_solo_al_acomodar()
    {
        var mostrador = Exhibidor("Mostrador");
        var pared = Exhibidor("La pared");
        var patrias = Temporada("15 de septiembre", new DiaDelAnio(9, 1), new DiaDelAnio(9, 16));
        var todosSantos = Temporada("Todos Santos", new DiaDelAnio(9, 16), new DiaDelAnio(11, 5));
        var navidad = Temporada("Navidad", new DiaDelAnio(11, 20), new DiaDelAnio(1, 1));
        var primavera = Temporada("Primavera", null, null);
        // Como llegan de la caja principal: primero los exhibidores y luego las temporadas.
        var todos = new[] { mostrador, pared, patrias, todosSantos, navidad, primavera };

        Assert.Equal(["Todos Santos", "Mostrador", "La pared"], MostradorEnPantalla.EnOrden(todos, new DateOnly(2026, 10, 17)).Select(e => e.Nombre));
        Assert.Equal(["15 de septiembre", "Todos Santos", "Mostrador", "La pared"],
            MostradorEnPantalla.EnOrden(todos, new DateOnly(2026, 9, 16)).Select(e => e.Nombre));
        Assert.Equal(["Navidad", "Mostrador", "La pared"], MostradorEnPantalla.EnOrden(todos, new DateOnly(2027, 1, 1)).Select(e => e.Nombre));
        Assert.Equal(["Mostrador", "La pared"], MostradorEnPantalla.EnOrden(todos, new DateOnly(2027, 6, 1)).Select(e => e.Nombre));
        Assert.Equal(["Todos Santos", "Mostrador", "La pared", "15 de septiembre", "Navidad", "Primavera"],
            MostradorEnPantalla.EnOrden(todos, new DateOnly(2026, 10, 17), conLasQueNoCorren: true).Select(e => e.Nombre));
        Assert.Equal(["Todos Santos"], MostradorEnPantalla.TemporadasQueCorren(todos, new DateOnly(2026, 11, 5)).Select(e => e.Nombre));
        Assert.Empty(MostradorEnPantalla.TemporadasQueCorren(todos, new DateOnly(2026, 11, 6)));
        Assert.Empty(MostradorEnPantalla.TemporadasQueCorren(null, new DateOnly(2026, 11, 6)));
    }

    /// <summary>
    /// Dos repisas de 4 en 4: la de arriba con 6 piezas (dos renglones, el segundo de 2) y la de abajo con 3. Las piezas se
    /// cuentan de corrido: 0 a 5 arriba, 6 a 8 abajo.
    /// </summary>
    [Theory]
    [InlineData(0, "ArrowRight", 1)]
    [InlineData(5, "ArrowRight", 6)]
    [InlineData(8, "ArrowRight", 8)]
    [InlineData(0, "ArrowLeft", 0)]
    [InlineData(6, "ArrowLeft", 5)]
    [InlineData(1, "ArrowDown", 5)]
    [InlineData(3, "ArrowDown", 5)]
    [InlineData(5, "ArrowDown", 7)]
    [InlineData(4, "ArrowDown", 6)]
    [InlineData(7, "ArrowDown", 7)]
    [InlineData(5, "ArrowUp", 1)]
    [InlineData(6, "ArrowUp", 4)]
    [InlineData(8, "ArrowUp", 5)]
    [InlineData(0, "ArrowUp", 0)]
    public void Las_flechas_van_a_la_pieza_de_al_lado_de_arriba_o_de_abajo(int actual, string tecla, int esperada) =>
        Assert.Equal(esperada, MostradorEnPantalla.Siguiente([6, 3], 4, actual, tecla));

    [Fact]
    public void Un_lugar_vacio_se_brinca_y_sin_piezas_no_hay_a_donde_ir()
    {
        Assert.Equal(2, MostradorEnPantalla.Siguiente([2, 0, 3], 4, 0, "ArrowDown"));
        Assert.Equal(0, MostradorEnPantalla.Siguiente([2, 0, 3], 4, 2, "ArrowUp"));
        Assert.Equal(0, MostradorEnPantalla.Siguiente([0, 0], 4, 0, "ArrowDown"));
        Assert.Equal((2, 2), MostradorEnPantalla.Lugar([2, 0, 3], 4));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("3", 3)]
    [InlineData(" 12 ", 12)]
    [InlineData("2.5", 2.5)]
    [InlineData("3*", 3)]
    [InlineData("9999", 9999)]
    // La coma solo separa los miles, como en «5*código» de la venta (revisión del PR #138): «1,000» son mil, no uno.
    [InlineData("1,000", 1000)]
    [InlineData("9,999", 9999)]
    [InlineData("1,250.5*", 1250.5)]
    public void Lo_escrito_antes_es_la_cantidad(string texto, double cantidad)
    {
        Assert.Equal((decimal)cantidad, MostradorEnPantalla.Cantidad(texto));
        Assert.True(MostradorEnPantalla.EsCantidad(texto));
    }

    /// <summary>
    /// Un código largo (también el que se quedó en el cuadro al tocar una pieza: serían billones), un nombre, «3*código» o algo
    /// que no es un número de hasta cuatro cifras no son una cantidad: con Enter van como siempre, un escaneo, y al tocar una
    /// pieza no se agregan.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("7501234567890")]
    [InlineData("12345")]
    [InlineData("10000")]
    [InlineData("1.2345")]
    [InlineData("1e3")]
    [InlineData("cua pro")]
    [InlineData("3*7501234567890")]
    [InlineData("-2")]
    // Una coma que podría ser de decimales no se adivina (antes aquí era 2.5 y en la venta, 25).
    [InlineData("1,5")]
    [InlineData("2,5*")]
    [InlineData("12,50")]
    [InlineData("10,000")]
    public void Lo_que_no_es_una_cantidad_va_como_siempre(string texto)
    {
        Assert.False(MostradorEnPantalla.EsCantidad(texto));
        Assert.Null(MostradorEnPantalla.Cantidad(texto));
    }

    [Theory]
    [InlineData("Calculadora básica 8 dígitos", "CB")]
    [InlineData("Juego de geometría metálico 4 piezas", "JG")]
    [InlineData("Memoria USB 32 GB", "MU")]
    [InlineData("Ñandú de peluche", "ÑP")]
    [InlineData("Agenda", "AG")]
    [InlineData("", "?")]
    public void Sin_foto_las_iniciales(string nombre, string iniciales) => Assert.Equal(iniciales, MostradorEnPantalla.Iniciales(nombre));
}
