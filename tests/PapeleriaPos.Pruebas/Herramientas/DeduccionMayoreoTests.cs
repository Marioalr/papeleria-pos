using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Herramientas;

public class DeduccionMayoreoTests
{
    private static VentaConMayoreo Venta(double cantidad, int mes = 3, double ganancia = 10) => new(cantidad, new DateTime(2026, mes, 10), ganancia);

    [Fact]
    public void El_paquete_es_la_cantidad_que_mas_se_vende_con_F11_y_sus_multiplos_cuentan()
    {
        var r = DeduccionMayoreo.Deducir([Venta(12), Venta(12), Venta(12), Venta(24), Venta(36), Venta(5)]);

        Assert.Equal(Cantidad.Piezas(12), r.Paquete);
        Assert.Equal("12 ×3 · 5 ×1 · 24 ×1 · 36 ×1", r.Cantidades);
    }

    [Fact]
    public void No_cuentan_las_del_regreso_a_clases_ni_las_vendidas_al_costo()
    {
        // En agosto F11 dejaba al costo los productos estratégicos: esas no dicen el paquete.
        var r = DeduccionMayoreo.Deducir([Venta(1, mes: 8), Venta(1, mes: 8), Venta(2, mes: 8), Venta(1, ganancia: 0), Venta(10), Venta(10), Venta(10)]);

        Assert.Equal((Cantidad.Piezas(10), 3), (r.Paquete, r.Ventas));
    }

    [Theory]
    [InlineData(new double[] { 12, 12, 12, 24, 24, 24, 24 }, 12)] // El múltiplo que más se vendió no tapa al paquete.
    [InlineData(new double[] { 12, 12, 12, 24, 24, 24 }, 12)]
    [InlineData(new double[] { 12, 12, 24, 24, 24, 24, 24 }, 24)] // 12 se vendió muy pocas veces solo para confiar en él.
    [InlineData(new double[] { 2, 12, 12, 12, 12 }, 12)] // Un 2 suelto no hace que el mayoreo empiece en 2.
    public void El_paquete_junta_sus_multiplos_y_se_vendio_solo_lo_suficiente(double[] cantidades, int paquete)
    {
        var r = DeduccionMayoreo.Deducir(cantidades.Select(c => Venta(c)));

        Assert.Equal((Cantidad.Piezas(paquete), (string?)null), (r.Paquete, r.Motivo));
    }

    [Theory]
    [InlineData(new double[0], DeduccionMayoreo.SinVentas)]
    [InlineData(new double[] { 12, 12 }, DeduccionMayoreo.PocasVentas)]
    [InlineData(new double[] { 12, 12, 1, 1, 1 }, DeduccionMayoreo.PocasVentas)]
    [InlineData(new double[] { 10, 10, 10, 12, 12, 12 }, DeduccionMayoreo.Empate)]
    [InlineData(new double[] { 12, 12, 12, 5, 7, 9, 11 }, DeduccionMayoreo.SinPaquete)]
    [InlineData(new double[] { 1, 1, 1 }, DeduccionMayoreo.SinPaquete)]
    public void Sin_un_paquete_claro_va_a_revisar(double[] cantidades, string motivo)
    {
        var r = DeduccionMayoreo.Deducir(cantidades.Select(c => Venta(c)));

        Assert.Null(r.Paquete);
        Assert.Equal(motivo, r.Motivo);
    }
}
