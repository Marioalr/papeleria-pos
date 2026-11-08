using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

public class DevolucionTests
{
    private static readonly DateTime Ahora = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Usuario = Ids.Nuevo();

    private readonly Turno _turno = new() { FondoCentavos = 50000 };
    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _pieza;
    private readonly List<Precio> _precios;

    public DevolucionTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _precios = [new Precio { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 }];
    }

    private Venta Vendida(int piezas, params CobroSolicitado[] cobros)
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A" };
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(piezas), _precios);
        linea.CostoCentavos = piezas * 400;
        venta.Pagar(1, cobros.Length > 0 ? cobros : [new CobroSolicitado(FormaPago.Efectivo, piezas * 1000)], Ahora);
        return venta;
    }

    [Fact]
    public void Devolver_parte_de_una_linea_reparte_importe_y_costo()
    {
        var venta = Vendida(3);
        var linea = venta.Lineas.Single();

        var devolucion = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "No era", Ahora.AddDays(2));
        CalculoDevolucion.Aplicar(venta, devolucion);

        Assert.Equal((1000L, 400L), (devolucion.Lineas.Single().ImporteCentavos, devolucion.Lineas.Single().CostoCentavos));
        Assert.Equal(1000, devolucion.ReembolsoEfectivoCentavos);
        Assert.Equal(Cantidad.Piezas(2), linea.CantidadPorDevolver);
    }

    /// <summary>
    /// La regla de la caja cuando lo que ya salió de la línea se sabe en dinero y pudo no salir con ella (un ticket de eleventa,
    /// que redondea a su modo; 2026-10-24): lo acumulado con esto menos lo que de verdad ya salió, sin pasarse de lo que le
    /// queda ni bajar de cero. De tres por $20.00.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 0, 667)]
    [InlineData(1, 1, 667, 666)]
    [InlineData(1, 1, 666, 667)]
    [InlineData(2, 1, 1333, 667)]
    [InlineData(2, 1, 1340, 660)]
    [InlineData(1, 1, 1400, 0)]
    [InlineData(0, 3, 0, 2000)]
    public void Con_lo_que_de_verdad_ya_salio_suma_lo_que_le_toca_y_completa_la_linea(int antes, int parte, long yaSalio, long esperado)
    {
        Assert.Equal(esperado, CalculoDevolucion.ParteDe(2000, Cantidad.Piezas(antes), Cantidad.Piezas(parte), Cantidad.Piezas(3), yaSalio));
    }

    [Fact]
    public void Con_lo_que_salio_por_la_misma_regla_es_la_de_la_caja()
    {
        long yaSalio = 0;
        for (var antes = 0; antes < 3; antes++)
        {
            var caja = CalculoDevolucion.ParteDe(2000, Cantidad.Piezas(antes), Cantidad.Piezas(1), Cantidad.Piezas(3));
            Assert.Equal(caja, CalculoDevolucion.ParteDe(2000, Cantidad.Piezas(antes), Cantidad.Piezas(1), Cantidad.Piezas(3), yaSalio));
            yaSalio += caja;
        }
        Assert.Equal(2000, yaSalio);
    }

    [Fact]
    public void No_se_devuelve_dos_veces_ni_despues_de_7_dias()
    {
        var venta = Vendida(2);
        var linea = venta.Lineas.Single();
        CalculoDevolucion.Aplicar(venta, CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(2))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora));

        Assert.Throws<InvalidOperationException>(() =>
            CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora));
        var otra = Vendida(1);
        Assert.Throws<InvalidOperationException>(() =>
            CalculoDevolucion.Armar(otra, [new LineaPorDevolver(otra.Lineas.Single().Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora.AddDays(8)));
    }

    [Fact]
    public void Cancelar_solo_en_su_turno_y_con_tarjeta_no_sale_efectivo()
    {
        var conTarjeta = Vendida(5, new CobroSolicitado(FormaPago.Tarjeta, 3000, "Aut 1"), new CobroSolicitado(FormaPago.Efectivo, 2000));

        Assert.Throws<InvalidOperationException>(() => CalculoDevolucion.ArmarCancelacion(conTarjeta, Ids.Nuevo(), Usuario, "x", Ahora));
        var cancelacion = CalculoDevolucion.ArmarCancelacion(conTarjeta, _turno.Id, Usuario, "Se arrepintió", Ahora);
        CalculoDevolucion.Aplicar(conTarjeta, cancelacion);

        Assert.True(cancelacion.EsCancelacion);
        Assert.Equal((5000L, 2000L), (cancelacion.TotalCentavos, cancelacion.ReembolsoEfectivoCentavos));
        Assert.Equal(EstadoVenta.Cancelada, conTarjeta.Estado);
    }

    [Fact]
    public void El_corte_resta_reembolsos_y_no_cuenta_ventas_canceladas()
    {
        var cancelada = Vendida(2);
        var cancelacion = CalculoDevolucion.ArmarCancelacion(cancelada, _turno.Id, Usuario, "x", Ahora);
        CalculoDevolucion.Aplicar(cancelada, cancelacion);
        var vigente = Vendida(3);
        var devolucion = CalculoDevolucion.Armar(vigente, [new LineaPorDevolver(vigente.Lineas.Single().Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora);

        var corte = CalculoCorte.Calcular(_turno, [cancelada, vigente], [], TipoCorte.X, Usuario, Ahora, devoluciones: [cancelacion, devolucion]);

        Assert.Equal((1, 1), (corte.Ventas, corte.Cancelaciones));
        Assert.Equal(5000, corte.EfectivoVentasCentavos);
        Assert.Equal(3000, corte.DevolucionesCentavos);
        Assert.Equal(50000 + 5000 - 3000, corte.EsperadoCentavos);
    }

    [Fact]
    public void El_precio_cambiado_se_conserva_al_cambiar_la_cantidad()
    {
        var venta = new Venta { Serie = "A" };
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(2), _precios);
        var autorizacion = Ids.Nuevo();

        venta.CambiarPrecio(linea, 800, autorizacion);
        venta.CambiarCantidad(linea, Cantidad.Piezas(3), _pieza, _precios);

        Assert.Equal((1000L, 800L, 2400L), (linea.PrecioOriginalCentavos!.Value, linea.PrecioCentavos, linea.ImporteCentavos));
        Assert.Equal(autorizacion, linea.AutorizacionPrecioId);
        Assert.Throws<InvalidOperationException>(() => venta.CambiarPrecio(venta.AgregarArticuloComun("Varios", 500, Cantidad.Piezas(1)), 100, null));
    }
}
