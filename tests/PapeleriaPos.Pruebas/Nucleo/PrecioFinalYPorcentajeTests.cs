using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Lo que pidió el dueño el 2026-11-22, en el ticket: el «Precio final» más alto sube el precio (Venta.SubirPrecio, como Ctrl+E de antes;
/// no es un descuento) y el porcentaje junto a cada descuento en pesos (PorcentajeDelDescuento: un decimal, sin «.0»).
/// </summary>
public class PrecioFinalYPorcentajeTests
{
    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma punto fino azul", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _pieza;
    private readonly List<Precio> _precios;

    public PrecioFinalYPorcentajeTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _precios = [new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 }];
    }

    private static Venta NuevaVenta() => new() { SucursalId = Ids.Nuevo(), Serie = "A", CreadaUtc = DateTime.UtcNow };

    // ── Subir el precio ─────────────────────────────────────────────────────

    [Fact]
    public void Subir_el_precio_deja_el_renglon_en_lo_escrito_como_precio_cambiado_y_sin_descuento()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);
        venta.DescontarLinea(linea, 1000, null, "Cliente frecuente");
        var autorizacion = Ids.Nuevo();

        // Tres a $10.00 son $30.00: que queden en $40.00 es $13.33 cada una (al centavo), $39.99 el renglón.
        var precio = venta.SubirPrecio(linea, 4000, autorizacion);

        Assert.Equal(1333, precio);
        Assert.Equal(1333, linea.PrecioCentavos);
        Assert.Equal(1000, linea.PrecioOriginalCentavos);
        Assert.True(linea.PrecioCambiado);
        Assert.Equal(autorizacion, linea.AutorizacionPrecioId);
        Assert.False(linea.TieneDescuento);
        Assert.Null(linea.DescuentoCentesimas);
        Assert.Equal(3999, linea.ImporteCentavos);
        Assert.Equal(3999, venta.TotalCentavos);
        Assert.Equal(0, venta.DescuentoCentavos);
    }

    [Fact]
    public void Una_pieza_queda_justo_en_lo_escrito()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(1), _precios);

        Assert.Equal(1250, venta.SubirPrecio(linea, 1250, null));
        Assert.Equal(1250, venta.TotalCentavos);
    }

    [Fact]
    public void No_se_sube_el_articulo_comun_ni_a_lo_mismo_o_menos()
    {
        var venta = NuevaVenta();
        var comun = venta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        var pluma = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(1), _precios);

        Assert.Equal(Venta.ComunNoSube, Assert.Throws<InvalidOperationException>(() => venta.SubirPrecio(comun, 6000, null)).Message);
        Assert.Throws<ArgumentException>(() => venta.SubirPrecio(pluma, 1000, null));
        Assert.Throws<ArgumentException>(() => venta.SubirPrecio(pluma, 900, null));
        Assert.False(pluma.PrecioCambiado);
        Assert.Equal(6000, venta.TotalCentavos);
    }

    // ── El porcentaje del descuento ─────────────────────────────────────────

    [Theory]
    [InlineData(500, 5000, "10 %")]
    [InlineData(500, 6000, "8.3 %")]
    [InlineData(100, 300, "33.3 %")]
    [InlineData(125, 1000, "12.5 %")]
    [InlineData(1, 2000, "0.1 %")]
    [InlineData(2000, 2000, "100 %")]
    [InlineData(1995, 2000, "99.8 %")]
    [InlineData(0, 2000, "")]
    [InlineData(100, 0, "")]
    public void El_porcentaje_va_con_un_decimal_y_sin_punto_cero(long descuento, long sinDescuento, string texto) =>
        Assert.Equal(texto, PorcentajeDelDescuento.Texto(descuento, sinDescuento));

    [Fact]
    public void El_de_un_renglon_es_el_escrito_o_el_que_sale_del_monto_y_el_del_ticket_va_sobre_todo_el_total()
    {
        var venta = NuevaVenta();
        var pluma = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);
        var comun = venta.AgregarArticuloComun("Recuerdo", 6000, Cantidad.Piezas(1));
        venta.FijarImpresiones(1000, 200);

        venta.DescontarLinea(pluma, 1250, null, "Cliente frecuente");     // 12.5 % de $30.00: $3.75
        venta.DescontarLinea(comun, null, 500, "Detalle");                 // $5.00 de $60.00: 8.3 %

        Assert.Equal("12.5 %", PorcentajeDelDescuento.DeLinea(pluma));
        Assert.Equal("8.3 %", PorcentajeDelDescuento.DeLinea(comun));
        // $8.75 de los $100.00 del ticket sin descuentos (con las impresiones): 8.8 %.
        Assert.Equal(875, venta.DescuentoCentavos);
        Assert.Equal("8.8 %", PorcentajeDelDescuento.DelTicket(venta));
    }

    [Fact]
    public void El_ticket_armado_lleva_el_porcentaje_tambien_del_descuento_en_pesos()
    {
        var venta = NuevaVenta();
        var comun = venta.AgregarArticuloComun("Recuerdo", 6000, Cantidad.Piezas(1));
        venta.DescontarLinea(comun, null, 500, "Detalle");
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 6000)], DateTime.UtcNow);

        var ticket = ArmadoTicket.Desde(venta, "Caja 2", "Ana", _ => null, ["PAPELERÍA DE PRUEBA"], ["Gracias"], null);

        Assert.Equal("8.3 %", Assert.Single(ticket.Lineas).Descuento);
    }
}
