using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class CantidadTests
{
    [Fact]
    public void Las_unidades_se_guardan_en_milesimas()
    {
        Assert.Equal(1000, Cantidad.Piezas(1).Milesimas);
        Assert.Equal(1500, Cantidad.Unidades(1.5m).Milesimas);
        Assert.Equal(250, Cantidad.Unidades(0.25m).Milesimas);
        Assert.Equal(1.5m, Cantidad.Unidades(1.5m).EnUnidades);
    }

    [Fact]
    public void Mas_de_tres_decimales_no_se_aceptan()
    {
        Assert.Throws<ArgumentException>(() => Cantidad.Unidades(0.0005m));
    }

    [Fact]
    public void Paquetes_por_factor_dan_unidades_base()
    {
        var docena = Cantidad.Piezas(12);

        Assert.Equal(Cantidad.Piezas(18), Cantidad.Unidades(1.5m).Por(docena));
        Assert.Equal(Cantidad.Piezas(36), Cantidad.Piezas(3).Por(docena));
    }

    [Theory]
    [InlineData(1.5, 750, 1125)]   // 1.5 m a $7.50 = $11.25
    [InlineData(0.333, 1000, 333)] // 0.333 kg a $10.00 = $3.33
    [InlineData(0.125, 4, 1)]      // 0.5 centavos se redondea hacia arriba
    [InlineData(-2, 1550, -3100)]  // devolución
    public void El_importe_se_redondea_al_centavo(decimal cantidad, long precio, long esperado)
    {
        Assert.Equal(esperado, Cantidad.Unidades(cantidad).Importe(precio));
    }

    [Fact]
    public void Solo_las_piezas_completas_son_enteras()
    {
        Assert.True(Cantidad.Piezas(3).EsEntera);
        Assert.False(Cantidad.Unidades(2.5m).EsEntera);
    }

    [Fact]
    public void Se_muestra_sin_ceros_de_mas()
    {
        Assert.Equal("1.5", Cantidad.Unidades(1.5m).ToString());
        Assert.Equal("12", Cantidad.Piezas(12).ToString());
    }
}
