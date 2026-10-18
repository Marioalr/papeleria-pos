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

    /// <summary>Lo escrito con el de hoy que se veía: el de la lista ($50.00), salvo que se diga otro.</summary>
    private static PrecioEscrito Escrito(string texto, long? antes = 5000) => new(texto, antes);

    [Fact]
    public void Un_renglon_toma_lo_escrito_y_si_no_el_precio_de_hoy()
    {
        var vela = Producto();

        Assert.Equal(5000, TemporadaEnPantalla.Precio(vela, null));
        Assert.Equal(5550, TemporadaEnPantalla.Precio(vela, Escrito("$55.50")));
        Assert.Null(TemporadaEnPantalla.Precio(vela, Escrito("cincuenta")));
        Assert.Null(TemporadaEnPantalla.Precio(vela, Escrito("0")));
    }

    [Fact]
    public void Se_marca_lo_que_no_es_un_precio_lo_que_queda_abajo_del_mayoreo_y_lo_que_no_se_cambia_aqui()
    {
        Assert.Null(TemporadaEnPantalla.Problema(Producto(), null));
        Assert.Equal("no es un precio", TemporadaEnPantalla.Problema(Producto(), Escrito("abc")));
        Assert.Equal("más barato que su mayoreo", TemporadaEnPantalla.Problema(Producto(piso: 4501), Escrito("45")));
        Assert.Null(TemporadaEnPantalla.Problema(Producto(piso: 4501), Escrito("45.01")));
        Assert.Equal("se cambia en eleventa", TemporadaEnPantalla.Problema(Producto(enEleventa: true), Escrito("60")));
        Assert.Equal("está desactivado", TemporadaEnPantalla.Problema(Producto(activo: false), Escrito("60")));
        // Escribir el mismo precio no es cambiarlo: no se marca.
        Assert.Null(TemporadaEnPantalla.Problema(Producto(enEleventa: true), Escrito("50.00")));
    }

    [Fact]
    public void Se_confirma_todo_lo_activo_con_precio_y_lo_de_eleventa_como_esta()
    {
        var vela = Producto();
        var deEleventa = Producto(hoy: 300, enEleventa: true);
        var apagado = Producto(activo: false);
        var sinPrecio = Producto(hoy: null);

        var precios = TemporadaEnPantalla.ParaConfirmar([vela, deEleventa, apagado, sinPrecio],
            new Dictionary<Guid, PrecioEscrito> { [vela.VarianteId] = Escrito("55") });

        Assert.Equal([new PrecioDeTemporadaDto(vela.VarianteId, 5000, 5500), new PrecioDeTemporadaDto(deEleventa.VarianteId, 300, 300)], precios!);
        // Si uno está en rojo, nada.
        Assert.Null(TemporadaEnPantalla.ParaConfirmar([vela], new Dictionary<Guid, PrecioEscrito> { [vela.VarianteId] = Escrito("abc") }));
    }

    /// <summary>
    /// Lo escrito recuerda el de hoy que se veía (revisión de la entrega 13): se escribió $25 cuando era $20; otra caja lo pone a $30
    /// y la lista se vuelve a leer. El renglón lo marca, no se manda, y si se mandara iría con los $20 de antes: la caja principal
    /// no pisaría los $30 sin que se vieran.
    /// </summary>
    [Fact]
    public void Si_el_de_hoy_cambio_despues_de_escribirlo_se_marca_y_va_con_el_de_antes()
    {
        var antes = Producto(hoy: 2000);
        var despues = antes with { PrecioCentavos = 3000 };
        var escrito = new PrecioEscrito("25", 2000);

        Assert.Null(TemporadaEnPantalla.Problema(antes, escrito));
        Assert.Equal("el de hoy cambió (era $20.00): escríbelo otra vez", TemporadaEnPantalla.Problema(despues, escrito));
        Assert.Null(TemporadaEnPantalla.ParaConfirmar([despues], new Dictionary<Guid, PrecioEscrito> { [despues.VarianteId] = escrito }));
        Assert.Equal([new PrecioDeTemporadaDto(antes.VarianteId, 2000, 2500)],
            TemporadaEnPantalla.ParaConfirmar([antes], new Dictionary<Guid, PrecioEscrito> { [antes.VarianteId] = escrito })!);
        // Escribirlo otra vez, con los $30 a la vista, ya no se marca.
        Assert.Null(TemporadaEnPantalla.Problema(despues, new PrecioEscrito("25", 3000)));
        // Si quedó igual al de hoy, no hay nada que pisar.
        Assert.Null(TemporadaEnPantalla.Problema(despues, new PrecioEscrito("30", 2000)));
    }

    /// <summary>Lo contado se lee sin tronar (revisión de la entrega 13): más de tres decimales no es una cantidad.</summary>
    [Theory]
    [InlineData("12", true, 12_000L)]
    [InlineData(" 2,5 ", true, 2_500L)]
    [InlineData("0.125", true, 125L)]
    [InlineData("", true, null)]
    [InlineData("1.2345", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("doce", false, null)]
    [InlineData("2000000", false, null)]
    public void Lo_contado_se_lee_en_milesimas_sin_mas_de_tres_decimales(string texto, bool esCantidad, long? milesimas)
    {
        Assert.Equal(esCantidad, TemporadaEnPantalla.LeerCantidad(texto, out var leidas));
        Assert.Equal(milesimas, leidas);
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
