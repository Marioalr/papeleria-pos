using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Cuándo corre una temporada (entrega 13): sus fechas se repiten cada año, con los dos días incluidos, y la que termina el año
/// siguiente (Navidad, del 20 de noviembre al 1 de enero) cruza el fin de año. Lo mismo en la caja principal, en la copia de
/// cada caja y en la Configuración.
/// </summary>
public class TemporadasTests
{
    private static Exhibidor Temporada(int desdeDia, int desdeMes, int hastaDia, int hastaMes)
    {
        var temporada = new Exhibidor { Nombre = "De prueba", Tipo = TipoExhibidor.Temporada };
        temporada.PonerFechas(new DiaDelAnio(desdeMes, desdeDia), new DiaDelAnio(hastaMes, hastaDia));
        return temporada;
    }

    [Theory]
    [InlineData("2026-11-19", false)]
    [InlineData("2026-11-20", true)]
    [InlineData("2026-12-24", true)]
    [InlineData("2026-12-31", true)]
    [InlineData("2027-01-01", true)]
    [InlineData("2027-01-02", false)]
    [InlineData("2027-06-15", false)]
    public void Navidad_cruza_el_fin_de_anio(string fecha, bool corre)
    {
        var navidad = Temporada(20, 11, 1, 1);

        Assert.Equal(corre, navidad.CorreEl(DateOnly.Parse(fecha, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("2026-08-31", false, false)]
    [InlineData("2026-09-01", true, false)]
    [InlineData("2026-09-15", true, false)]
    [InlineData("2026-09-16", true, true)]
    [InlineData("2026-09-17", false, true)]
    [InlineData("2026-11-05", false, true)]
    [InlineData("2026-11-06", false, false)]
    public void Las_del_duenio_con_sus_dias_incluidos_y_dos_a_la_vez_el_16_de_septiembre(string fecha, bool patrias, bool todosSantos)
    {
        var dia = DateOnly.Parse(fecha, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(patrias, Temporada(1, 9, 16, 9).CorreEl(dia));
        Assert.Equal(todosSantos, Temporada(16, 9, 5, 11).CorreEl(dia));
    }

    [Fact]
    public void Sin_fechas_un_exhibidor_o_una_quitada_no_corren()
    {
        var primavera = new Exhibidor { Nombre = "Primavera", Tipo = TipoExhibidor.Temporada };
        var mostrador = new Exhibidor { Nombre = "Mostrador", Tipo = TipoExhibidor.Exhibidor };
        var quitada = Temporada(1, 1, 31, 12);
        quitada.Activo = false;
        var hoy = new DateOnly(2026, 10, 17);

        Assert.False(primavera.CorreEl(hoy));
        Assert.False(mostrador.CorreEl(hoy));
        Assert.False(quitada.CorreEl(hoy));
        Assert.True(Temporada(1, 1, 31, 12).CorreEl(hoy));
        // Un solo día.
        Assert.True(Temporada(30, 4, 30, 4).CorreEl(new DateOnly(2027, 4, 30)));
        Assert.False(Temporada(30, 4, 30, 4).CorreEl(new DateOnly(2027, 5, 1)));
    }

    [Fact]
    public void El_29_de_febrero_vale_y_en_un_anio_sin_el_no_llega()
    {
        var bisiesto = Temporada(29, 2, 1, 3);

        Assert.True(new DiaDelAnio(2, 29).EsValido);
        Assert.False(bisiesto.CorreEl(new DateOnly(2027, 2, 28)));
        Assert.True(bisiesto.CorreEl(new DateOnly(2027, 3, 1)));
        Assert.True(bisiesto.CorreEl(new DateOnly(2028, 2, 29)));
    }

    [Fact]
    public void Las_fechas_son_las_dos_o_ninguna_y_tienen_que_existir()
    {
        var temporada = new Exhibidor { Nombre = "Navidad", Tipo = TipoExhibidor.Temporada };

        Assert.Throws<ArgumentException>(() => temporada.PonerFechas(new DiaDelAnio(11, 20), null));
        Assert.Throws<ArgumentException>(() => temporada.PonerFechas(new DiaDelAnio(4, 31), new DiaDelAnio(5, 5)));
        Assert.Throws<ArgumentException>(() => temporada.PonerFechas(new DiaDelAnio(13, 1), new DiaDelAnio(1, 1)));
        Assert.Throws<ArgumentException>(() => new Exhibidor { Nombre = "Mostrador" }.PonerFechas(new DiaDelAnio(1, 1), new DiaDelAnio(2, 1)));
        temporada.PonerFechas(new DiaDelAnio(11, 20), new DiaDelAnio(1, 1));
        Assert.Equal((20, 11, 1, 1), (temporada.DesdeDia, temporada.DesdeMes, temporada.HastaDia, temporada.HastaMes));
        temporada.PonerFechas(null, null);
        Assert.Null(temporada.Desde);
        Assert.Null(temporada.HastaMes);
    }

    [Theory]
    [InlineData("del 16 de septiembre al 5 de noviembre", 16, 9, 5, 11)]
    [InlineData("16/9 al 5/11", 16, 9, 5, 11)]
    [InlineData("  Del 20 de Noviembre AL 1 de enero ", 20, 11, 1, 1)]
    [InlineData("20 nov - 1 ene", 20, 11, 1, 1)]
    [InlineData("1 sept a 16 sep", 1, 9, 16, 9)]
    [InlineData("16-09 hasta 05-11", 16, 9, 5, 11)]
    [InlineData("1 de setiembre al 16 de setiembre", 1, 9, 16, 9)]
    [InlineData("el 30 de abril", 30, 4, 30, 4)]
    public void Las_fechas_se_leen_como_se_escriben(string texto, int desdeDia, int desdeMes, int hastaDia, int hastaMes)
    {
        var (desde, hasta) = Temporadas.LeerFechas(texto);

        Assert.Equal(new DiaDelAnio(desdeMes, desdeDia), desde);
        Assert.Equal(new DiaDelAnio(hastaMes, hastaDia), hasta);
        // Y como se enseñan, se vuelven a leer igual.
        Assert.Equal((desde, hasta), Temporadas.LeerFechas(Temporadas.Fechas(desde, hasta)));
    }

    [Theory]
    [InlineData("16/9")]
    [InlineData("31/4 al 5/5")]
    [InlineData("16 de sept al 5 de nobiembre")]
    [InlineData("mañana al lunes")]
    public void Lo_que_no_se_entiende_dice_por_que(string texto) =>
        Assert.Throws<ArgumentException>(() => Temporadas.LeerFechas(texto));

    [Fact]
    public void Sin_texto_no_hay_fechas() => Assert.Equal((null, null), Temporadas.LeerFechas("  "));

    [Fact]
    public void Las_fechas_se_dicen_como_las_dijo_el_duenio()
    {
        Assert.Equal("del 16 de septiembre al 5 de noviembre", Temporadas.Fechas(new DiaDelAnio(9, 16), new DiaDelAnio(11, 5)));
        Assert.Equal("el 30 de abril", Temporadas.Fechas(new DiaDelAnio(4, 30), new DiaDelAnio(4, 30)));
        Assert.Null(Temporadas.Fechas(null, null));
    }

    /// <summary>El día de la tienda es el de la Ciudad de México: a las 23:00 del 16 sigue siendo el 16 aunque en UTC ya sea el 17.</summary>
    [Fact]
    public void Hoy_es_el_dia_de_la_tienda()
    {
        Assert.Equal(new DateOnly(2026, 10, 16), Temporadas.Hoy(new RelojFijo(new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero))));
        Assert.Equal(new DateOnly(2026, 10, 17), Temporadas.Hoy(new RelojFijo(new DateTimeOffset(2026, 10, 17, 7, 0, 0, TimeSpan.Zero))));
    }
}
