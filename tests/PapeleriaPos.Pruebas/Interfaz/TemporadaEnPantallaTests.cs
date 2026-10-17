using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo de Productos › Temporadas que no es la página (entrega 13, parte 3): qué precio queda en cada renglón, qué se marca en
/// rojo, qué se manda a confirmar y cómo se lee lo que se vendió.
/// </summary>
public class TemporadaEnPantallaTests
{
    private static ProductoDeTemporadaDto Producto(long? hoy = 5000, long piso = 0, bool activo = true, bool enEleventa = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Vela", null, "Mesa", activo, false, 4500, "historial", hoy, piso, null, enEleventa);

    [Fact]
    public void Un_renglon_toma_lo_escrito_y_si_no_el_precio_de_hoy()
    {
        var vela = Producto();

        Assert.Equal(5000, TemporadaEnPantalla.Precio(vela, null));
        Assert.Equal(5550, TemporadaEnPantalla.Precio(vela, "$55.50"));
        Assert.Null(TemporadaEnPantalla.Precio(vela, "cincuenta"));
        Assert.Null(TemporadaEnPantalla.Precio(vela, "0"));
    }

    [Fact]
    public void Se_marca_lo_que_no_es_un_precio_lo_que_queda_abajo_del_mayoreo_y_lo_que_no_se_cambia_aqui()
    {
        Assert.Null(TemporadaEnPantalla.Problema(Producto(), null));
        Assert.Equal("no es un precio", TemporadaEnPantalla.Problema(Producto(), "abc"));
        Assert.Equal("más barato que su mayoreo", TemporadaEnPantalla.Problema(Producto(piso: 4501), "45"));
        Assert.Null(TemporadaEnPantalla.Problema(Producto(piso: 4501), "45.01"));
        Assert.Equal("se cambia en eleventa", TemporadaEnPantalla.Problema(Producto(enEleventa: true), "60"));
        Assert.Equal("está desactivado", TemporadaEnPantalla.Problema(Producto(activo: false), "60"));
        // Escribir el mismo precio no es cambiarlo: no se marca.
        Assert.Null(TemporadaEnPantalla.Problema(Producto(enEleventa: true), "50.00"));
    }

    [Fact]
    public void Se_confirma_todo_lo_activo_con_precio_y_lo_de_eleventa_como_esta()
    {
        var vela = Producto();
        var deEleventa = Producto(hoy: 300, enEleventa: true);
        var apagado = Producto(activo: false);
        var sinPrecio = Producto(hoy: null);

        var precios = TemporadaEnPantalla.ParaConfirmar([vela, deEleventa, apagado, sinPrecio], new Dictionary<Guid, string> { [vela.VarianteId] = "55" });

        Assert.Equal([new PrecioDeTemporadaDto(vela.VarianteId, 5000, 5500), new PrecioDeTemporadaDto(deEleventa.VarianteId, 300, 300)], precios!);
        // Si uno está en rojo, nada.
        Assert.Null(TemporadaEnPantalla.ParaConfirmar([vela], new Dictionary<Guid, string> { [vela.VarianteId] = "abc" }));
    }

    [Fact]
    public void Lo_vendido_se_lee_como_se_acabo_no_se_vendio_o_que_parte()
    {
        ResumenDeProductoDto Resumen(decimal inicial, decimal vendido, decimal quedo) =>
            new(Guid.NewGuid(), "Vela", null, "Mesa", UnidadBase.Pieza, true, inicial, vendido, quedo);

        Assert.Equal("se acabó", TemporadaEnPantalla.ComoSeVendio(Resumen(12, 12, 0)));
        Assert.Equal("no se vendió", TemporadaEnPantalla.ComoSeVendio(Resumen(6, 0, 6)));
        Assert.Equal("75 %", TemporadaEnPantalla.ComoSeVendio(Resumen(20, 15, 5)));
        Assert.Equal("100 %", TemporadaEnPantalla.ComoSeVendio(Resumen(10, 12, 1)));
    }
}
