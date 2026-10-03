using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class CalculoPrecioTests
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private static readonly Guid Sucursal2 = Ids.Nuevo();

    private readonly Variante _pluma = new() { UnidadBase = UnidadBase.Pieza };
    private readonly Presentacion _pieza;
    private readonly Presentacion _docena;

    public CalculoPrecioTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.Id, Nombre = "Pieza", Factor = Cantidad.Piezas(1), EsBase = true };
        _docena = new Presentacion { VarianteId = _pluma.Id, Nombre = "Docena", Factor = Cantidad.Piezas(12) };
    }

    private Precio PorPieza(long centavos, int desde = 1, Guid? sucursal = null) => new()
    {
        VarianteId = _pluma.Id, SucursalId = sucursal, CantidadMinima = Cantidad.Piezas(desde), PrecioCentavos = centavos,
    };

    [Fact]
    public void Menudeo_cobra_precio_por_pieza()
    {
        var aplicado = CalculoPrecio.Calcular([PorPieza(800)], Matriz, _pieza, Cantidad.Piezas(3));

        Assert.Equal(2400, aplicado!.ImporteCentavos);
    }

    [Fact]
    public void El_paquete_sin_precio_propio_cuesta_sus_piezas_y_cuenta_para_mayoreo()
    {
        Precio[] precios = [PorPieza(800), PorPieza(650, desde: 12)];

        var aplicado = CalculoPrecio.Calcular(precios, Matriz, _docena, Cantidad.Piezas(1));

        Assert.Equal(650, aplicado!.Precio.PrecioCentavos);
        Assert.Equal(12 * 650, aplicado.ImporteCentavos);
    }

    [Fact]
    public void Se_usa_la_escala_mas_alta_que_alcanza()
    {
        Precio[] precios = [PorPieza(800), PorPieza(700, desde: 10), PorPieza(600, desde: 50)];

        Assert.Equal(800, CalculoPrecio.Calcular(precios, Matriz, _pieza, Cantidad.Piezas(9))!.Precio.PrecioCentavos);
        Assert.Equal(700, CalculoPrecio.Calcular(precios, Matriz, _pieza, Cantidad.Piezas(10))!.Precio.PrecioCentavos);
        Assert.Equal(600, CalculoPrecio.Calcular(precios, Matriz, _pieza, Cantidad.Piezas(80))!.Precio.PrecioCentavos);
    }

    [Fact]
    public void El_precio_propio_de_la_presentacion_gana()
    {
        var propio = new Precio
        {
            VarianteId = _pluma.Id, PresentacionId = _docena.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 7000,
        };

        var aplicado = CalculoPrecio.Calcular([PorPieza(800), propio], Matriz, _docena, Cantidad.Piezas(2));

        Assert.Same(propio, aplicado!.Precio);
        Assert.Equal(14000, aplicado.ImporteCentavos);
    }

    [Fact]
    public void El_precio_de_la_sucursal_gana_sobre_el_base()
    {
        Precio[] precios = [PorPieza(800), PorPieza(900, sucursal: Sucursal2)];

        Assert.Equal(900, CalculoPrecio.Calcular(precios, Sucursal2, _pieza, Cantidad.Piezas(1))!.ImporteCentavos);
        Assert.Equal(800, CalculoPrecio.Calcular(precios, Matriz, _pieza, Cantidad.Piezas(1))!.ImporteCentavos);
    }

    [Fact]
    public void Las_escalas_base_no_se_mezclan_con_las_de_la_sucursal()
    {
        Precio[] precios = [PorPieza(800), PorPieza(600, desde: 12), PorPieza(900, sucursal: Sucursal2)];

        var aplicado = CalculoPrecio.Calcular(precios, Sucursal2, _pieza, Cantidad.Piezas(12));

        Assert.Equal(900, aplicado!.Precio.PrecioCentavos);
    }

    [Fact]
    public void Por_metro_con_fracciones_por_debajo_de_la_escala_minima()
    {
        var liston = new Variante { UnidadBase = UnidadBase.Metro };
        var metro = new Presentacion { VarianteId = liston.Id, Nombre = "Metro", Factor = Cantidad.Unidades(1), EsBase = true };
        var precio = new Precio { VarianteId = liston.Id, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = 750 };

        Assert.Equal(1125, CalculoPrecio.Calcular([precio], Matriz, metro, Cantidad.Unidades(1.5m))!.ImporteCentavos);
        Assert.Equal(375, CalculoPrecio.Calcular([precio], Matriz, metro, Cantidad.Unidades(0.5m))!.ImporteCentavos);
    }

    [Fact]
    public void Sin_precio_devuelve_null()
    {
        Assert.Null(CalculoPrecio.Calcular([], Matriz, _pieza, Cantidad.Piezas(1)));
    }
}
