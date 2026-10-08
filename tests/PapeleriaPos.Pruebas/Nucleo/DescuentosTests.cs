using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

public class DescuentosTests
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma punto fino azul", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly ProductoEnVenta _cuaderno = new(Ids.Nuevo(), "Cuaderno profesional", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _piezaPluma;
    private readonly Presentacion _piezaCuaderno;
    private readonly List<Precio> _precios;

    public DescuentosTests()
    {
        _piezaPluma = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _piezaCuaderno = new Presentacion { VarianteId = _cuaderno.VarianteId, Nombre = "Pieza", EsBase = true };
        _precios =
        [
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 },
            new() { VarianteId = _cuaderno.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 4500 },
        ];
    }

    private Venta NuevaVenta() => new() { SucursalId = Matriz, Serie = "A", CreadaUtc = DateTime.UtcNow };

    [Fact]
    public void El_porcentaje_se_vuelve_a_aplicar_si_cambia_la_cantidad()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _piezaPluma, Cantidad.Piezas(3), _precios);

        venta.DescontarLinea(linea, 1000, null, "Cliente frecuente");
        Assert.Equal((300, 2700), (linea.DescuentoCentavos, linea.ImporteCentavos));
        venta.CambiarCantidad(linea, Cantidad.Piezas(5), _piezaPluma, _precios);

        Assert.Equal(500, linea.DescuentoCentavos);
        Assert.Equal(4500, linea.ImporteCentavos);
        Assert.Equal(1000, linea.PrecioCentavos);          // el precio unitario no cambia: el descuento va aparte
        Assert.Equal(4500, venta.TotalCentavos);
        Assert.Equal(621, venta.IvaCentavos);              // el IVA sale del importe con descuento
    }

    [Fact]
    public void Un_monto_en_todo_el_ticket_se_reparte_sin_perder_centavos_y_sin_tocar_las_impresiones()
    {
        var venta = NuevaVenta();
        var pluma = venta.AgregarProducto(_pluma, _piezaPluma, Cantidad.Piezas(1), _precios);
        var cuaderno = venta.AgregarProducto(_cuaderno, _piezaCuaderno, Cantidad.Piezas(2), _precios);
        var impresiones = venta.FijarImpresiones(1500, 300)!;

        venta.DescontarTicket(null, 1001, "Redondeo para el cliente");

        Assert.Equal(1001, venta.DescuentoCentavos);
        Assert.Equal(1001, pluma.DescuentoCentavos + cuaderno.DescuentoCentavos);
        Assert.True(cuaderno.DescuentoCentavos > pluma.DescuentoCentavos);
        Assert.Equal(0, impresiones.DescuentoCentavos);
        Assert.Equal(10000 + 1500 - 1001, venta.TotalCentavos);
        Assert.Throws<InvalidOperationException>(() => venta.DescontarLinea(impresiones, 1000, null, "No"));
    }

    [Fact]
    public void Lleva_motivo_y_no_pasa_del_importe()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _piezaPluma, Cantidad.Piezas(1), _precios);

        Assert.Throws<ArgumentException>(() => venta.DescontarLinea(linea, 1000, null, " "));
        Assert.Throws<ArgumentException>(() => venta.DescontarLinea(linea, null, 1001, "Demasiado"));
        Assert.Equal("El porcentaje va de más de 0 a 100 %.",
            Assert.Throws<ArgumentException>(() => venta.DescontarLinea(linea, 10001, null, "Más de 100 %")).Message);
        Assert.Equal(1000, venta.TotalCentavos);
    }

    [Fact]
    public void Lo_que_se_revisa_es_el_descuento_mas_alto_sin_contar_el_del_cliente_especial()
    {
        var venta = NuevaVenta();
        var pluma = venta.AgregarProducto(_pluma, _piezaPluma, Cantidad.Piezas(3), _precios);
        var cuaderno = venta.AgregarProducto(_cuaderno, _piezaCuaderno, Cantidad.Piezas(1), _precios);

        venta.DescontarTicket(1500, null, "Cliente especial: Escuela");
        Assert.Equal(0, venta.DescuentoARevisar(preaprobado: 1500));
        Assert.Equal(1500, venta.DescuentoARevisar(preaprobado: null));

        venta.DescontarLinea(cuaderno, null, 2250, "Tiene la pasta doblada");   // 50 % en monto
        Assert.Equal(5000, venta.DescuentoARevisar(preaprobado: 1500));

        venta.QuitarDescuentos();
        Assert.Equal(7500, venta.TotalCentavos);
        Assert.False(pluma.TieneDescuento);
    }
}
