using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Nucleo;

public class KardexTests
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private static readonly DateTime Ahora = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    private readonly Variante _goma = new() { UnidadBase = UnidadBase.Pieza };
    private readonly Existencia _existencia;

    public KardexTests()
    {
        _existencia = new Existencia { SucursalId = Matriz, VarianteId = _goma.Id };
    }

    private void Registrar(MovimientoInventario movimiento) => _existencia.Aplicar(movimiento);

    private MovimientoInventario Compra(int piezas, long costo) =>
        Kardex.Entrada(Matriz, _goma, TipoMovimiento.Compra, Cantidad.Piezas(piezas), costo, Ahora);

    private MovimientoInventario Venta(int piezas, long ultimoCosto = 0) =>
        Kardex.Salida(Matriz, _goma, TipoMovimiento.Venta, Cantidad.Piezas(piezas), _existencia, ultimoCosto, Ahora);

    [Fact]
    public void La_existencia_y_el_valor_son_la_suma_de_movimientos()
    {
        Registrar(Compra(10, 1000));
        Registrar(Compra(10, 1400));

        Assert.Equal(Cantidad.Piezas(20), _existencia.Cantidad);
        Assert.Equal(2400, _existencia.CostoCentavos);
        Assert.Equal(120m, _existencia.CostoPromedio);
    }

    [Fact]
    public void La_salida_lleva_el_costo_promedio_con_signo_negativo()
    {
        Registrar(Compra(10, 1000));
        Registrar(Compra(10, 1400));

        var venta = Venta(5);

        Assert.Equal(Cantidad.Piezas(-5), venta.Cantidad);
        Assert.Equal(-600, venta.CostoCentavos);
    }

    [Fact]
    public void Costos_con_fracciones_de_centavo_no_pierden_valor()
    {
        // Caja de 144 por $100: cada pieza cuesta 69.44 centavos.
        Registrar(Compra(144, 10000));

        for (var i = 0; i < 144; i++)
            Registrar(Venta(1));

        Assert.Equal(Cantidad.Cero, _existencia.Cantidad);
        Assert.Equal(0, _existencia.CostoCentavos);
    }

    [Fact]
    public void Sin_existencia_se_vende_al_ultimo_costo_conocido()
    {
        var venta = Venta(2, ultimoCosto: 350);

        Assert.Equal(-700, venta.CostoCentavos);
    }

    [Fact]
    public void Se_permite_vender_sin_existencia()
    {
        Registrar(Compra(1, 100));
        Registrar(Venta(3, ultimoCosto: 100));

        Assert.Equal(Cantidad.Piezas(-2), _existencia.Cantidad);
    }

    [Fact]
    public void Las_piezas_no_admiten_fracciones_pero_el_metro_si()
    {
        Assert.Throws<ArgumentException>(() =>
            Kardex.Entrada(Matriz, _goma, TipoMovimiento.Compra, Cantidad.Unidades(1.5m), 100, Ahora));

        var liston = new Variante { UnidadBase = UnidadBase.Metro };
        var entrada = Kardex.Entrada(Matriz, liston, TipoMovimiento.Compra, Cantidad.Unidades(1.5m), 100, Ahora);
        Assert.Equal(1500, entrada.Cantidad.Milesimas);
    }

    [Fact]
    public void Ajustes_y_mermas_requieren_motivo()
    {
        Registrar(Compra(5, 500));

        Assert.Throws<ArgumentException>(() =>
            Kardex.Salida(Matriz, _goma, TipoMovimiento.Merma, Cantidad.Piezas(1), _existencia, 0, Ahora));

        var merma = Kardex.Salida(Matriz, _goma, TipoMovimiento.Merma, Cantidad.Piezas(1), _existencia, 0, Ahora, "Rota");
        Assert.Equal("Rota", merma.Motivo);
    }

    [Theory]
    [InlineData(TipoMovimiento.Venta)]
    [InlineData(TipoMovimiento.Merma)]
    public void Un_tipo_de_salida_no_puede_ser_entrada(TipoMovimiento tipo)
    {
        Assert.Throws<ArgumentException>(() =>
            Kardex.Entrada(Matriz, _goma, tipo, Cantidad.Piezas(1), 100, Ahora, "motivo"));
    }

    [Fact]
    public void La_cantidad_debe_ser_positiva_y_la_fecha_en_utc()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Kardex.Entrada(Matriz, _goma, TipoMovimiento.Compra, Cantidad.Piezas(-1), 100, Ahora));
        Assert.Throws<ArgumentException>(() =>
            Kardex.Entrada(Matriz, _goma, TipoMovimiento.Compra, Cantidad.Piezas(1), 100, DateTime.Now));
    }

    [Fact]
    public void Reconstruir_desde_el_kardex_da_lo_mismo()
    {
        var movimientos = new List<MovimientoInventario>();
        void Agregar(MovimientoInventario m)
        {
            movimientos.Add(m);
            Registrar(m);
        }

        Agregar(Compra(144, 10000));
        Agregar(Venta(7));
        Agregar(Compra(20, 1500));
        Agregar(Venta(30));

        var reconstruida = Assert.Single(Existencia.Reconstruir(movimientos));
        Assert.Equal(_existencia.Cantidad, reconstruida.Cantidad);
        Assert.Equal(_existencia.CostoCentavos, reconstruida.CostoCentavos);
    }
}
