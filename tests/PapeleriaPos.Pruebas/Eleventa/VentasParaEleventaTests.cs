using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>Cómo pasa una venta de la caja nueva a lo que entiende eleventa (docs/migracion-eleventa.md, «Mandar cada venta»).</summary>
public class VentasParaEleventaTests
{
    private static readonly DateTime Cobrada = new(2026, 3, 9, 17, 30, 0, DateTimeKind.Utc);

    private readonly Turno _turno = new() { FondoCentavos = 50000, AbiertoUtc = new DateTime(2026, 3, 9, 14, 0, 0, DateTimeKind.Utc) };
    private readonly ProductoEnVenta _cuaderno = new(Ids.Nuevo(), "Cuaderno profesional", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly ProductoEnVenta _liston = new(Ids.Nuevo(), "Listón rojo", Impuesto.Iva16, UnidadBase.Metro, true);
    private readonly ProductoEnVenta _nuevo = new(Ids.Nuevo(), "Dado de alta aquí", Impuesto.Iva16, UnidadBase.Pieza, true);

    private Venta Venta()
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "B", CreadaUtc = Cobrada.AddMinutes(-3) };
        Agregar(venta, _cuaderno, Cantidad.Piezas(2), 3000);
        Agregar(venta, _liston, Cantidad.Unidades(1.5m), 850);
        Agregar(venta, _nuevo, Cantidad.Piezas(1), 1200);
        venta.AgregarArticuloComun("Recuerdo", 1500, Cantidad.Piezas(1));
        return venta;
    }

    private static void Agregar(Venta venta, ProductoEnVenta producto, Cantidad cantidad, long precio) =>
        venta.AgregarProducto(producto, new Presentacion { VarianteId = producto.VarianteId, Nombre = "Base", EsBase = true }, cantidad,
            [new Precio { VarianteId = producto.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = precio }]);

    private Dictionary<Guid, int> DeEleventa() => new() { [_cuaderno.VarianteId] = 10, [_liston.VarianteId] = 11 };

    [Fact]
    public void Lo_que_vino_de_eleventa_lleva_su_producto_y_lo_demas_va_como_comun()
    {
        var venta = Venta();
        venta.Pagar(7, [new CobroSolicitado(FormaPago.Efectivo, 15000)], Cobrada, redondear: false);

        var r = VentasParaEleventa.Armar(venta, _turno, DeEleventa());

        Assert.Null(r.PorRevisar);
        var armada = r.Venta!;
        Assert.Equal([10, 11, null, null], armada.Articulos.Select(a => a.ProductoId));
        Assert.Equal(["Cuaderno profesional", "Listón rojo", "Dado de alta aquí", "Recuerdo"], armada.Articulos.Select(a => a.Nombre));
        Assert.Equal([2m, 1.5m, 1m, 1m], armada.Articulos.Select(a => a.Cantidad));
        Assert.Equal([60m, 12.75m, 12m, 15m], armada.Articulos.Select(a => a.Importe));
        Assert.Equal((99.75m, 150m), (armada.Total, armada.PagoCon));
        // La hora de la tienda, como la guarda eleventa; el turno, por si allá no hay uno abierto.
        Assert.Equal((new DateTime(2026, 3, 9, 11, 27, 0), new DateTime(2026, 3, 9, 11, 30, 0)), (armada.CreadaLocal, armada.CobradaLocal));
        Assert.Equal(new TurnoParaEleventa(new DateTime(2026, 3, 9, 8, 0, 0), 500m), armada.Turno);
    }

    /// <summary>
    /// Lo cobrado con una escala de mayoreo va marcado, con lo que se ahorró contra el menudeo: en eleventa es el F11
    /// (<c>USA_MAYOREO</c> y el <c>TOTAL_AHORRADO</c> del ticket). Lo de menudeo, sin (2026-10-24).
    /// </summary>
    [Fact]
    public void Lo_cobrado_con_mayoreo_va_marcado_con_lo_que_se_ahorro()
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "B", CreadaUtc = Cobrada.AddMinutes(-3) };
        venta.AgregarProducto(_cuaderno, new Presentacion { VarianteId = _cuaderno.VarianteId, Nombre = "Base", EsBase = true }, Cantidad.Piezas(12),
        [
            new Precio { VarianteId = _cuaderno.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 3000 },
            new Precio { VarianteId = _cuaderno.VarianteId, CantidadMinima = Cantidad.Piezas(12), PrecioCentavos = 2750 },
        ]);
        Agregar(venta, _liston, Cantidad.Unidades(1.5m), 850);
        venta.Pagar(7, [new CobroSolicitado(FormaPago.Efectivo, 40000)], Cobrada, redondear: false);

        var armada = VentasParaEleventa.Armar(venta, _turno, DeEleventa()).Venta!;

        Assert.Equal([(true, 30m, 330m), (false, 0m, 12.75m)], armada.Articulos.Select(a => (a.ConMayoreo, a.AhorroMayoreo, a.Importe)));
    }

    [Fact]
    public void El_redondeo_del_efectivo_va_en_la_linea_mas_grande_para_que_cuadre_con_la_caja()
    {
        var venta = Venta();
        venta.Pagar(7, [new CobroSolicitado(FormaPago.Efectivo, 10000)], Cobrada);

        var armada = VentasParaEleventa.Armar(venta, _turno, DeEleventa()).Venta!;

        Assert.Equal(100m, venta.TotalCobradoCentavos / 100m);
        Assert.Equal([60.25m, 12.75m, 12m, 15m], armada.Articulos.Select(a => a.Importe));
        Assert.Equal(100m, armada.Total);
    }

    [Fact]
    public void Lo_que_no_fue_en_efectivo_o_se_cancelo_queda_por_revisar()
    {
        var conTarjeta = Venta();
        conTarjeta.Pagar(7, [new CobroSolicitado(FormaPago.Tarjeta, 9975, "4821")], Cobrada);
        var cancelada = Venta();
        cancelada.Pagar(8, [new CobroSolicitado(FormaPago.Efectivo, 10000)], Cobrada);
        cancelada.Estado = EstadoVenta.Cancelada;

        var tarjeta = VentasParaEleventa.Armar(conTarjeta, _turno, DeEleventa());
        var deCancelada = VentasParaEleventa.Armar(cancelada, _turno, DeEleventa());

        Assert.Null(tarjeta.Venta);
        Assert.Contains("tarjeta", tarjeta.PorRevisar);
        Assert.Null(deCancelada.Venta);
        Assert.Contains("canceló", deCancelada.PorRevisar);
    }

    [Fact]
    public void Una_entrada_o_salida_va_con_su_motivo_y_su_hora_y_la_que_llego_despues_del_corte_queda_por_revisar()
    {
        var salida = new MovimientoCaja
        {
            TurnoId = _turno.Id, Tipo = TipoMovimientoCaja.Salida, MontoCentavos = 12550, Motivo = "Garrafón de agua", FechaUtc = Cobrada.AddTicks(1234567),
        };
        var tarde = new MovimientoCaja
        {
            TurnoId = _turno.Id, Tipo = TipoMovimientoCaja.Entrada, MontoCentavos = 20000, Motivo = "Cambio del banco", FechaUtc = Cobrada,
            EnContingencia = true, FueraDeCorteUtc = Cobrada.AddHours(2),
        };

        var r = VentasParaEleventa.ArmarMovimiento(salida, _turno);
        var fuera = VentasParaEleventa.ArmarMovimiento(tarde, _turno);

        Assert.Null(r.PorRevisar);
        Assert.Equal(new MovimientoParaEleventa(false, 125.5m, "Garrafón de agua", new DateTime(2026, 3, 9, 11, 30, 0).AddTicks(1234567),
            new TurnoParaEleventa(new DateTime(2026, 3, 9, 8, 0, 0), 500m)), r.Movimiento);
        // En eleventa su turno ya se cerró: mandarla abriría otro.
        Assert.Null(fuera.Movimiento);
        Assert.Contains("corte", fuera.PorRevisar);
    }
}
