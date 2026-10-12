using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>El crédito de los clientes registrados (fase 3, entrega 2, parte 2): fiar, abonar, la cuenta, las devoluciones y el corte.</summary>
public class CreditoTests
{
    private static readonly DateTime Ahora = new(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Cliente = Ids.Nuevo();
    private readonly Turno _turno = new() { FondoCentavos = 50000 };

    /// <summary>Un ticket con un artículo común de ese importe, del cliente.</summary>
    private Venta Ticket(long totalCentavos, int piezas = 1)
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A", ClienteId = Cliente };
        venta.AgregarArticuloComun("Mercancía", totalCentavos / piezas, Cantidad.Piezas(piezas));
        return venta;
    }

    private static CobroSolicitado Fiado(long centavos) => new(FormaPago.Credito, centavos);

    private static CobroSolicitado Efectivo(long centavos) => new(FormaPago.Efectivo, centavos);

    // ── Fiar ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Fiar_todo_no_redondea_ni_abre_el_cajon()
    {
        var venta = Ticket(13745);

        venta.Pagar(1, [Fiado(13745)], Ahora);

        Assert.Equal((13745L, 0L, false), (venta.CreditoCentavos, venta.RedondeoCentavos, venta.LlevaEfectivo));
        Assert.Equal(FormaPago.Credito, Assert.Single(venta.Cobros).Forma);
    }

    [Fact]
    public void Fiar_el_resto_deja_los_centavos_en_la_deuda()
    {
        var venta = Ticket(13745);

        venta.Pagar(1, [Efectivo(10000), Fiado(3745)], Ahora);

        var efectivo = venta.Cobros.Single(c => c.Forma == FormaPago.Efectivo);
        Assert.Equal((10000L, 0L, 0L, 3745L), (efectivo.MontoCentavos, efectivo.CambioCentavos, venta.RedondeoCentavos, venta.CreditoCentavos));
    }

    [Fact]
    public void Fiar_una_parte_redondea_solo_el_efectivo()
    {
        var venta = Ticket(13745);

        venta.Pagar(1, [Efectivo(12000), Fiado(2000)], Ahora);

        // Quedan $117.45 en efectivo: se cobran $117 (redondeo −$0.45) y de $120 se dan $3 de cambio.
        var efectivo = venta.Cobros.Single(c => c.Forma == FormaPago.Efectivo);
        Assert.Equal((11700L, -45L, 300L, 2000L), (efectivo.MontoCentavos, venta.RedondeoCentavos, efectivo.CambioCentavos, venta.CreditoCentavos));
    }

    [Fact]
    public void El_credito_pide_cliente_va_en_un_solo_cobro_y_no_pasa_del_total()
    {
        var sinCliente = Ticket(5000);
        sinCliente.ClienteId = null;

        Assert.Contains("cliente registrado", Assert.Throws<InvalidOperationException>(() => sinCliente.Pagar(1, [Fiado(5000)], Ahora)).Message);
        Assert.Throws<ArgumentException>(() => Ticket(5000).Pagar(1, [Fiado(2000), Fiado(3000)], Ahora));
        Assert.Contains("crédito no pueden pasar del total",
            Assert.Throws<InvalidOperationException>(() => Ticket(5000).Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 3000), Fiado(3000)], Ahora)).Message);
    }

    [Theory]
    [InlineData(50000, 45000, 0, 10000, 5000)]   // pasa por $50
    [InlineData(50000, 80000, 0, 10000, 10000)]  // ya debía más del límite: todo lo fiado pasa
    [InlineData(50000, 10000, 20000, 25000, 5000)] // la transferencia sin verificar no da crédito
    [InlineData(0, -10000, 0, 6000, 0)]          // sin crédito, usa lo que tiene a favor
    [InlineData(0, -10000, 0, 15000, 5000)]      // y no más
    public void Lo_que_pasa_del_limite(long limite, long debe, long porVerificar, long fia, long exceso) =>
        Assert.Equal(exceso, CuentaCredito.Exceso(debe, porVerificar, fia, limite));

    [Fact]
    public void Vence_el_ultimo_dia_del_plazo_en_hora_de_mexico()
    {
        // 8 de octubre a mediodía en México: con 30 días, el último día a tiempo es el 7 de noviembre.
        Assert.Equal(new DateOnly(2026, 11, 7), CuentaCredito.Vence(Ahora, 30));
        // A las 11 de la noche del 8 en México ya es 9 en UTC: cuenta el día de México.
        Assert.Equal(new DateOnly(2026, 11, 7), CuentaCredito.Vence(new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc), 30));
    }

    // ── Abonar ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(23745, null, 30000, 23745, 23700, -45, 6300, true)]    // liquidar en efectivo: al peso
    [InlineData(23745, 23700L, 23700, 23745, 23700, -45, 0, true)]    // teclear lo que es al peso también liquida
    [InlineData(23745, 10000L, 10000, 10000, 10000, 0, 0, false)]     // abono parcial en pesos
    [InlineData(30, null, 100, 30, 100, 70, 0, true)]                 // 30 centavos se liquidan con un peso
    [InlineData(23760, null, 23800, 23760, 23800, 40, 0, true)]
    public void Abono_en_efectivo(long saldo, long? monto, long recibido, long baja, long cobra, long redondeo, long cambio, bool liquida) =>
        Assert.Equal(new AbonoCalculado(baja, cobra, redondeo, cambio, liquida), CalculoAbono.Calcular(saldo, FormaPago.Efectivo, monto, recibido));

    [Fact]
    public void Abono_que_no_se_puede()
    {
        Assert.Contains("($100.00 o $101.00), o liquida con $237.00",
            Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, 10050, 20000)).Message);
        Assert.Contains("En efectivo se abona en pesos ($1.00), o liquida con $237.00",
            Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, 50, 20000)).Message);
        Assert.Contains("La tarjeta no da cambio: abona hasta $237.45",
            Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Tarjeta, 25000, 0)).Message);
        Assert.Contains("No debe nada", Assert.Throws<InvalidOperationException>(() => CalculoAbono.Calcular(0, FormaPago.Efectivo, 1000, 1000)).Message);
        Assert.Contains("Faltan", Assert.Throws<InvalidOperationException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, null, 20000)).Message);
        Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Credito, 1000, 0));
        Assert.Equal(new AbonoCalculado(23745, 23745, 0, 0, true), CalculoAbono.Calcular(23745, FormaPago.Tarjeta, null, 0));
    }

    [Fact]
    public void Un_codigo_de_barras_en_el_abono_no_es_dinero()
    {
        // 7501234567890 en «Pagó con»: billones de cambio. En «Abona»: liquidaría la cuenta cobrando solo lo exacto.
        Assert.StartsWith("«Pagó con» $7,501,234,567,890.00 no puede ser",
            Assert.Throws<InvalidOperationException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, null, 750123456789000)).Message);
        Assert.StartsWith("«Pagó con»", Assert.Throws<InvalidOperationException>(() => CalculoAbono.Calcular(50000, FormaPago.Efectivo, 10000, 750123456789000)).Message);
        Assert.StartsWith("«Abona» $7,501,234,567,890.00 no puede ser: debe $237.45",
            Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, 750123456789000, 23700)).Message);
        Assert.Throws<ArgumentException>(() => CalculoAbono.Exacto(23745, FormaPago.Efectivo, 750123456789000));
    }

    [Theory]
    [InlineData(23745, null, 223700, 200000)]   // liquida $237 con $2,237: justo dos billetes de $1,000 de cambio
    [InlineData(23745, 223700L, 23700, 0)]      // teclear hasta $2,000 de más en «Abona» todavía liquida
    [InlineData(50000, 10000L, 110000, 100000)] // abona $100 con un billete de $1,000 (la caja pide confirmarlo)
    public void Hasta_dos_mil_pesos_de_cambio_el_abono_se_cobra(long saldo, long? monto, long recibido, long cambio) =>
        Assert.Equal(cambio, CalculoAbono.Calcular(saldo, FormaPago.Efectivo, monto, recibido).CambioCentavos);

    [Fact]
    public void Un_centavo_mas_de_dos_mil_pesos_no_es_dinero()
    {
        Assert.Throws<InvalidOperationException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, null, 223701));
        Assert.Throws<ArgumentException>(() => CalculoAbono.Calcular(23745, FormaPago.Efectivo, 223701, 23700));
    }

    [Fact]
    public void Lo_exacto_es_lo_que_se_le_pide_sin_cambio()
    {
        Assert.Equal(new AbonoCalculado(23745, 23700, -45, 0, true), CalculoAbono.Exacto(23745, FormaPago.Efectivo, null));
        Assert.Equal(new AbonoCalculado(10000, 10000, 0, 0, false), CalculoAbono.Exacto(23745, FormaPago.Efectivo, 10000));
        Assert.Equal(new AbonoCalculado(23745, 23745, 0, 0, true), CalculoAbono.Exacto(23745, FormaPago.Tarjeta, null));
        Assert.Throws<ArgumentException>(() => CalculoAbono.Exacto(23745, FormaPago.Efectivo, 10050));
    }

    // ── La cuenta ────────────────────────────────────────────────────────────

    private static MovimientoCredito Movimiento(TipoMovimientoCredito tipo, long monto, int dia, Guid? venta = null, Guid? abono = null) => new()
    {
        Id = Ids.Nuevo(), ClienteId = Cliente, Tipo = tipo, MontoCentavos = monto, FechaUtc = new DateTime(2026, 10, dia, 18, 0, 0, DateTimeKind.Utc),
        Vence = monto > 0 ? CuentaCredito.Vence(new DateTime(2026, 10, dia, 18, 0, 0, DateTimeKind.Utc), 30) : null, VentaId = venta, AbonoId = abono,
    };

    [Fact]
    public void Lo_pagado_liquida_primero_lo_mas_viejo_y_dice_cuando()
    {
        var primero = Movimiento(TipoMovimientoCredito.Cargo, 10000, 1);
        var segundo = Movimiento(TipoMovimientoCredito.Cargo, 5000, 5);
        var abono = Movimiento(TipoMovimientoCredito.Abono, -12000, 10);

        var cuenta = CuentaCredito.Aplicar([abono, segundo, primero]);

        Assert.Equal((3000L, 0L), (cuenta.SaldoCentavos, cuenta.AFavorCentavos));
        Assert.Equal((0L, abono.FechaUtc), (cuenta.Cargos[0].PendienteCentavos, cuenta.Cargos[0].LiquidadoUtc));
        Assert.Equal((3000L, (DateTime?)null), (cuenta.Cargos[1].PendienteCentavos, cuenta.Cargos[1].LiquidadoUtc));
        Assert.Equal(0, cuenta.Vencido(new DateOnly(2026, 11, 4)));
        Assert.Equal(3000, cuenta.Vencido(new DateOnly(2026, 11, 5)));
        Assert.Equal(new DateOnly(2026, 11, 4), cuenta.VenceMasAntiguo);
    }

    [Fact]
    public void La_devolucion_va_primero_a_su_propia_venta()
    {
        var (ventaVieja, ventaNueva) = (Ids.Nuevo(), Ids.Nuevo());
        var cuenta = CuentaCredito.Aplicar([
            Movimiento(TipoMovimientoCredito.Cargo, 10000, 1, ventaVieja),
            Movimiento(TipoMovimientoCredito.Cargo, 5000, 5, ventaNueva),
            Movimiento(TipoMovimientoCredito.Devolucion, -3000, 6, ventaNueva),
        ]);

        Assert.Equal((10000L, 0L), (cuenta.Cargos[0].PendienteCentavos, cuenta.Cargos[0].DevueltoCentavos));
        Assert.Equal((2000L, 3000L), (cuenta.Cargos[1].PendienteCentavos, cuenta.Cargos[1].DevueltoCentavos));
    }

    [Fact]
    public void Un_abono_rechazado_o_cancelado_no_liquida_nada()
    {
        var (rechazado, cancelado) = (Ids.Nuevo(), Ids.Nuevo());
        var cuenta = CuentaCredito.Aplicar([
            Movimiento(TipoMovimientoCredito.Cargo, 10000, 1),
            Movimiento(TipoMovimientoCredito.Abono, -6000, 2, abono: rechazado),
            Movimiento(TipoMovimientoCredito.Abono, -4000, 3, abono: cancelado),
            Movimiento(TipoMovimientoCredito.AbonoRechazado, 6000, 4, abono: rechazado),
            Movimiento(TipoMovimientoCredito.AbonoCancelado, 4000, 4, abono: cancelado),
        ]);

        Assert.Equal((10000L, (DateTime?)null), (cuenta.Cargos.Single().PendienteCentavos, cuenta.Cargos.Single().LiquidadoUtc));
        Assert.Equal(10000, cuenta.SaldoCentavos);
    }

    [Fact]
    public void El_saldo_a_favor_se_lo_come_el_siguiente_cargo()
    {
        var cuenta = CuentaCredito.Aplicar([
            Movimiento(TipoMovimientoCredito.Ajuste, -5000, 1),
            Movimiento(TipoMovimientoCredito.Cargo, 8000, 2),
        ]);

        Assert.Equal((3000L, 0L, 3000L), (cuenta.SaldoCentavos, cuenta.AFavorCentavos, cuenta.Cargos.Single().PendienteCentavos));
    }

    [Fact]
    public void Siempre_lo_pendiente_menos_lo_a_favor_es_el_saldo()
    {
        var azar = new Random(20261008);
        for (var vuelta = 0; vuelta < 300; vuelta++)
        {
            var movimientos = new List<MovimientoCredito>();
            var ventas = new List<Guid>();
            for (var i = 0; i < azar.Next(1, 15); i++)
            {
                var dia = azar.Next(1, 28);
                switch (azar.Next(5))
                {
                    case 0 or 1:
                        var venta = Ids.Nuevo();
                        ventas.Add(venta);
                        movimientos.Add(Movimiento(TipoMovimientoCredito.Cargo, azar.Next(1, 50000), dia, venta));
                        break;
                    case 2:
                        movimientos.Add(Movimiento(TipoMovimientoCredito.Abono, -azar.Next(1, 50000), dia, abono: Ids.Nuevo()));
                        break;
                    case 3 when ventas.Count > 0:
                        movimientos.Add(Movimiento(TipoMovimientoCredito.Devolucion, -azar.Next(1, 20000), dia, ventas[azar.Next(ventas.Count)]));
                        break;
                    default:
                        movimientos.Add(Movimiento(TipoMovimientoCredito.Ajuste, azar.Next(-20000, 20000) is var a and not 0 ? a : 1, dia));
                        break;
                }
            }

            var cuenta = CuentaCredito.Aplicar(movimientos);

            Assert.Equal(movimientos.Sum(m => m.MontoCentavos), cuenta.SaldoCentavos);
            Assert.Equal(cuenta.SaldoCentavos, cuenta.Cargos.Sum(c => c.PendienteCentavos) - cuenta.AFavorCentavos);
            Assert.All(cuenta.Cargos, c => Assert.InRange(c.PendienteCentavos, 0, c.MontoCentavos));
        }
    }

    // ── Devoluciones de lo fiado ─────────────────────────────────────────────

    private Devolucion Devolver(Venta venta, int piezas, IEnumerable<Devolucion> anteriores)
    {
        var devolucion = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(piezas))], Ids.Nuevo(), _turno.Id,
            Ids.Nuevo(), "No le quedó", Ahora.AddDays(1), anteriores);
        CalculoDevolucion.Aplicar(venta, devolucion);
        return devolucion;
    }

    [Fact]
    public void Lo_devuelto_de_una_venta_fiada_baja_primero_lo_fiado()
    {
        // $200 en 20 piezas de $10: $100 en efectivo y $100 fiado.
        var venta = Ticket(20000, piezas: 20);
        venta.Pagar(1, [Efectivo(10000), Fiado(10000)], Ahora);

        var primera = Devolver(venta, 5, []);                  // $50: todo a la cuenta
        var segunda = Devolver(venta, 12, [primera]);          // $120: $50 a la cuenta y $70 en efectivo
        var tercera = Devolver(venta, 3, [primera, segunda]);  // lo último, $30, en efectivo

        Assert.Equal((5000L, 0L), (primera.AlCreditoCentavos, primera.ReembolsoEfectivoCentavos));
        Assert.Equal((5000L, 7000L), (segunda.AlCreditoCentavos, segunda.ReembolsoEfectivoCentavos));
        Assert.Equal((0L, 3000L), (tercera.AlCreditoCentavos, tercera.ReembolsoEfectivoCentavos));
    }

    [Fact]
    public void Devolver_todo_de_una_venta_fiada_con_redondeo_regresa_lo_cobrado()
    {
        // $137.45: $37 fiado y $100.45 en efectivo, que se cobraron como $100 (redondeo −$0.45).
        var venta = Ticket(13745);
        venta.Pagar(1, [Efectivo(10000), Fiado(3700)], Ahora);

        var toda = Devolver(venta, 1, []);

        Assert.Equal((-45L, 3700L, 10000L, -45L), (venta.RedondeoCentavos, toda.AlCreditoCentavos, toda.ReembolsoEfectivoCentavos, toda.RedondeoCentavos));
        // En el corte, el redondeo de la venta y el de la devolución se anulan.
        Assert.Equal(0, venta.RedondeoCentavos - toda.RedondeoCentavos);
    }

    [Fact]
    public void Cancelar_una_venta_fiada_quita_todo_lo_fiado_y_regresa_solo_el_efectivo()
    {
        var venta = Ticket(13745);
        venta.Pagar(1, [Efectivo(10000), Fiado(3700)], Ahora);

        var cancelacion = CalculoDevolucion.ArmarCancelacion(venta, _turno.Id, Ids.Nuevo(), "Se equivocó", Ahora);

        Assert.Equal((3700L, 10000L, -45L), (cancelacion.AlCreditoCentavos, cancelacion.ReembolsoEfectivoCentavos, cancelacion.RedondeoCentavos));
    }

    [Fact]
    public void Una_venta_toda_fiada_devuelta_en_partes_deja_la_cuenta_en_cero()
    {
        // 3 cuadernos de $12.55 con 10 % del cliente: $33.88, todo fiado. Cada pieza devuelta vale $11.29 (suman $33.87).
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A", ClienteId = Cliente };
        venta.AgregarArticuloComun("Cuaderno", 1255, Cantidad.Piezas(3));
        venta.DescontarTicket(1000, null, "Cliente: Escuela");
        venta.Pagar(1, [Fiado(venta.TotalCentavos)], Ahora);

        var partes = new List<Devolucion>();
        for (var i = 0; i < 3; i++)
            partes.Add(Devolver(venta, 1, partes));

        Assert.Equal(3388, venta.CreditoCentavos);
        Assert.Equal(venta.CreditoCentavos, partes.Sum(d => d.AlCreditoCentavos));
        Assert.All(partes, d => Assert.Equal((0L, 0L), (d.ReembolsoEfectivoCentavos, d.RedondeoCentavos)));
    }

    [Fact]
    public void Las_impresiones_fiadas_se_devuelven_a_la_cuenta_y_la_mercancia_en_pesos()
    {
        // Mercancía $50 + $40 e impresiones $10.25; se fían las impresiones y se pagan $90 en efectivo.
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A", ClienteId = Cliente };
        var cuaderno = venta.AgregarArticuloComun("Cuaderno", 5000, Cantidad.Piezas(1));
        var mochila = venta.AgregarArticuloComun("Lapicera", 4000, Cantidad.Piezas(1));
        venta.FijarImpresiones(1025, 300);
        var impresiones = venta.Lineas.Single(l => l.EsImpresiones);
        venta.Pagar(1, [Efectivo(9000), Fiado(1025)], Ahora);
        Devolucion Una(VentaLinea linea, List<Devolucion> antes)
        {
            var d = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, linea.Cantidad)], Ids.Nuevo(), _turno.Id, Ids.Nuevo(), "x", Ahora, antes);
            CalculoDevolucion.Aplicar(venta, d);
            antes.Add(d);
            return d;
        }

        var hechas = new List<Devolucion>();
        var primero = Una(cuaderno, hechas);
        var luego = Una(impresiones, hechas);
        var ultimo = Una(mochila, hechas);

        Assert.Equal((0L, 5000L), (primero.AlCreditoCentavos, primero.ReembolsoEfectivoCentavos));
        Assert.Equal((1025L, 0L), (luego.AlCreditoCentavos, luego.ReembolsoEfectivoCentavos));
        Assert.Equal((0L, 4000L), (ultimo.AlCreditoCentavos, ultimo.ReembolsoEfectivoCentavos));
    }

    [Fact]
    public void Una_venta_sin_credito_se_devuelve_igual_que_antes()
    {
        var venta = Ticket(13745);
        venta.Pagar(1, [Efectivo(13700)], Ahora);

        var toda = Devolver(venta, 1, []);

        Assert.Equal((0L, 13700L, -45L), (toda.AlCreditoCentavos, toda.ReembolsoEfectivoCentavos, toda.RedondeoCentavos));
    }

    // ── El corte ─────────────────────────────────────────────────────────────

    private Abono Abono(FormaPago forma, long monto, long redondeo = 0, bool cancelado = false, TipoAbono tipo = TipoAbono.Abono) => new()
    {
        TurnoId = _turno.Id, ClienteId = Cliente, Forma = forma, MontoCentavos = monto, RedondeoCentavos = redondeo,
        CanceladoUtc = cancelado ? Ahora : null, Tipo = tipo,
    };

    [Fact]
    public void En_el_corte_los_abonos_en_efectivo_entran_al_esperado_y_lo_fiado_va_aparte()
    {
        var fiada = Ticket(13745);
        fiada.Pagar(1, [Efectivo(10000), Fiado(3745)], Ahora);
        var cancelada = Ticket(5000);
        cancelada.Pagar(2, [Fiado(5000)], Ahora);
        CalculoDevolucion.Aplicar(cancelada, CalculoDevolucion.ArmarCancelacion(cancelada, _turno.Id, Ids.Nuevo(), "x", Ahora));
        Abono[] abonos =
        [
            Abono(FormaPago.Efectivo, 23745, redondeo: -45),
            Abono(FormaPago.Tarjeta, 5000),
            Abono(FormaPago.Transferencia, 2000),
            Abono(FormaPago.Efectivo, 9900, cancelado: true),
        ];

        var corte = CalculoCorte.Calcular(_turno, [fiada, cancelada], [], TipoCorte.X, Ids.Nuevo(), Ahora, abonos: abonos);

        Assert.Equal((3745L, 3, 23700L, 5000L, 2000L), (corte.CreditoCentavos, corte.Abonos, corte.AbonosEfectivoCentavos, corte.AbonosTarjetaCentavos,
            corte.AbonosTransferenciaCentavos));
        Assert.Equal(50000 + 10000 + 23700, corte.EsperadoCentavos);
        Assert.Equal(-45, corte.RedondeoCentavos);
    }

    [Fact]
    public void En_el_corte_el_saldo_devuelto_sale_de_lo_esperado_y_no_cuenta_como_abono()
    {
        Abono[] abonos =
        [
            Abono(FormaPago.Efectivo, 10000),
            // Tenía $123.45 a favor: se le entregan $123.00 y su cuenta queda en cero exacto.
            Abono(FormaPago.Efectivo, 12345, redondeo: -45, tipo: TipoAbono.SaldoDevuelto),
        ];

        var corte = CalculoCorte.Calcular(_turno, [], [], TipoCorte.X, Ids.Nuevo(), Ahora, abonos: abonos);

        Assert.Equal((1, 10000L, 12300L), (corte.Abonos, corte.AbonosEfectivoCentavos, corte.SaldoDevueltoCentavos));
        Assert.Equal(50000 + 10000 - 12300, corte.EsperadoCentavos);
        Assert.Equal(45, corte.RedondeoCentavos);
    }

    // ── Qué tan puntual paga (parte 3) ───────────────────────────────────────

    private static readonly DateOnly HoyPuntualidad = new(2026, 10, 8);

    /// <summary>Un movimiento a mediodía de ese día (de México); lo que sube la cuenta vence a 30 días.</summary>
    private static MovimientoCredito El(DateOnly dia, TipoMovimientoCredito tipo, long monto, Guid? venta = null)
    {
        var fecha = HoraMexico.AUtc(dia.ToDateTime(new TimeOnly(12, 0)));
        return new MovimientoCredito
        {
            Id = Ids.Nuevo(), ClienteId = Cliente, Tipo = tipo, MontoCentavos = monto, FechaUtc = fecha, Vence = monto > 0 ? CuentaCredito.Vence(fecha, 30) : null,
            VentaId = venta,
        };
    }

    [Fact]
    public void La_puntualidad_cuenta_lo_liquidado_a_tiempo_y_los_dias_que_tardo()
    {
        var puntualidad = CuentaCredito.Puntualidad([
            El(new DateOnly(2026, 8, 1), TipoMovimientoCredito.Cargo, 10000),
            El(new DateOnly(2026, 8, 10), TipoMovimientoCredito.Abono, -10000),
            // Vencía el 01/10 y la liquidó el 05/10: 34 días, tarde.
            El(new DateOnly(2026, 9, 1), TipoMovimientoCredito.Cargo, 20000),
            El(new DateOnly(2026, 10, 5), TipoMovimientoCredito.Abono, -20000),
            El(new DateOnly(2026, 9, 20), TipoMovimientoCredito.Cargo, 5000),
        ], HoyPuntualidad);

        Assert.Equal(new PuntualidadDto(3, 2, 1, 22, 0), puntualidad);
        Assert.Equal("Liquida en 22 días, en promedio · 1 de 2 a tiempo", puntualidad.Texto());
        Assert.Equal("1 de 2", puntualidad.Corto());
    }

    [Fact]
    public void La_puntualidad_no_cuenta_ajustes_ni_lo_devuelto_completo_pero_si_lo_viejo_que_debe()
    {
        var devuelta = Ids.Nuevo();
        var puntualidad = CuentaCredito.Puntualidad([
            // Liquidada hace más de seis meses: ya no cuenta.
            El(new DateOnly(2026, 1, 15), TipoMovimientoCredito.Cargo, 8000),
            El(new DateOnly(2026, 1, 20), TipoMovimientoCredito.Abono, -8000),
            // Vieja, pero la sigue debiendo y ya venció: sí cuenta.
            El(new DateOnly(2026, 2, 1), TipoMovimientoCredito.Cargo, 10000),
            El(new DateOnly(2026, 6, 1), TipoMovimientoCredito.Ajuste, 35000),
            El(new DateOnly(2026, 9, 1), TipoMovimientoCredito.Cargo, 6000, devuelta),
            El(new DateOnly(2026, 9, 2), TipoMovimientoCredito.Devolucion, -6000, devuelta),
        ], HoyPuntualidad);

        Assert.Equal(new PuntualidadDto(1, 0, 0, null, 1), puntualidad);
        Assert.Equal(("0 de 1 a tiempo · 1 compra vencida sin pagar", "0 de 1"), (puntualidad.Texto(), puntualidad.Corto()));
    }

    [Fact]
    public void La_puntualidad_no_cuenta_lo_devuelto_despues_de_abonar_ni_lo_que_pago_su_saldo_a_favor_ni_lo_perdonado()
    {
        var (devuelta, cubierta, perdonada) = (Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo());
        var puntualidad = CuentaCredito.Puntualidad([
            // Abonó $30 y luego devolvió todo: no fue una compra que pagó tarde.
            El(new DateOnly(2026, 8, 1), TipoMovimientoCredito.Cargo, 10000, devuelta),
            El(new DateOnly(2026, 8, 5), TipoMovimientoCredito.Abono, -3000),
            El(new DateOnly(2026, 9, 15), TipoMovimientoCredito.Devolucion, -10000, devuelta),
            // Lo pagó su saldo a favor al fiarla: no fue crédito.
            El(new DateOnly(2026, 9, 16), TipoMovimientoCredito.Cargo, 2000, cubierta),
            // Se la perdonaron con un ajuste: no la pagó.
            El(new DateOnly(2026, 9, 20), TipoMovimientoCredito.Cargo, 5000, perdonada),
            El(new DateOnly(2026, 10, 1), TipoMovimientoCredito.Ajuste, -5000),
        ], HoyPuntualidad);

        Assert.True(puntualidad.SinDatos, puntualidad.ToString());
    }

    [Theory]
    [InlineData(0, 0, 0, null, 0, "Sin compras a crédito en los últimos 6 meses.")]
    [InlineData(2, 0, 0, null, 0, "Todavía no liquida sus compras a crédito; ninguna ha vencido.")]
    [InlineData(1, 1, 1, 0, 0, "Liquida el mismo día, en promedio · 1 de 1 a tiempo")]
    [InlineData(4, 2, 2, 1, 2, "Liquida en 1 día, en promedio · 2 de 4 a tiempo · 2 compras vencidas sin pagar")]
    public void La_puntualidad_se_dice_en_una_linea(int compras, int liquidadas, int aTiempo, int? dias, int vencidas, string texto) =>
        Assert.Equal(texto, new PuntualidadDto(compras, liquidadas, aTiempo, dias, vencidas).Texto());

    // ── Devolver el saldo a favor (parte 3) ──────────────────────────────────

    [Theory]
    [InlineData(12345, 12300, -45)]
    [InlineData(12360, 12400, 40)]
    [InlineData(15000, 15000, 0)]
    public void Devolver_todo_el_saldo_a_favor_entrega_al_peso_y_deja_la_cuenta_en_cero(long aFavor, long entrega, long redondeo) =>
        Assert.Equal(new SaldoDevueltoCalculado(aFavor, entrega, redondeo, Todo: true), CalculoAbono.DevolverSaldo(aFavor, null));

    [Fact]
    public void Devolver_una_parte_del_saldo_a_favor_va_en_pesos_exactos()
    {
        Assert.Equal(new SaldoDevueltoCalculado(10000, 10000, 0, Todo: false), CalculoAbono.DevolverSaldo(15000, 10000));
        Assert.True(CalculoAbono.DevolverSaldo(12345, 12300).Todo);
    }

    [Theory]
    [InlineData(0, null, "No tiene saldo a favor que se pueda devolver.")]
    [InlineData(40, null, "Tiene $0.40 a favor: menos de un peso no se devuelve en efectivo.")]
    [InlineData(15000, 20000L, "Tiene $150.00 a favor: se le devuelven hasta $150.00.")]
    [InlineData(15000, 5050L, "En efectivo se devuelve en pesos, o todo con $150.00.")]
    public void Lo_que_no_se_puede_devolver_dice_por_que(long devolvible, long? monto, string mensaje) =>
        Assert.Equal(mensaje, Assert.ThrowsAny<Exception>(() => CalculoAbono.DevolverSaldo(devolvible, monto)).Message);

    [Fact]
    public void El_saldo_devuelto_se_come_lo_que_tenia_a_favor_y_no_cuenta_como_compra()
    {
        MovimientoCredito[] movimientos =
        [
            El(new DateOnly(2026, 10, 1), TipoMovimientoCredito.Ajuste, -15000),
            El(new DateOnly(2026, 10, 2), TipoMovimientoCredito.SaldoDevuelto, 15000),
        ];
        var cuenta = CuentaCredito.Aplicar(movimientos);

        Assert.Equal((0L, 0L, 0L), (cuenta.SaldoCentavos, cuenta.AFavorCentavos, cuenta.Cargos.Sum(c => c.PendienteCentavos)));
        Assert.True(CuentaCredito.Puntualidad(movimientos, HoyPuntualidad).SinDatos);
    }

    [Fact]
    public void Lo_que_viene_de_transferencias_por_verificar_no_se_devuelve()
    {
        var cuenta = new CreditoClienteDto(Cliente, -30000, 20000, 0, 30, []);

        Assert.Equal((30000L, 10000L), (cuenta.AFavorCentavos, cuenta.DevolvibleCentavos));
        Assert.Equal(0, (cuenta with { PorVerificarCentavos = 50000 }).DevolvibleCentavos);
    }

    // ── Reglas ───────────────────────────────────────────────────────────────

    [Fact]
    public void El_limite_lo_da_el_dueno_y_pasarlo_lo_autoriza_el_encargado()
    {
        var sucursal = Ids.Nuevo();
        DecisionPermiso Para(Rol rol, AccionControlada accion) => Permisos.Evaluar(Permisos.Iniciales, rol, sucursal, accion, 1000);

        Assert.Equal((NivelPermiso.NoPermitido, NivelPermiso.NoPermitido, NivelPermiso.Permitido),
            (Para(Rol.Cajero, AccionControlada.DarCredito).Nivel, Para(Rol.Encargado, AccionControlada.DarCredito).Nivel, Para(Rol.Dueno, AccionControlada.DarCredito).Nivel));
        Assert.Equal((NivelPermiso.RequiereAutorizacion, NivelPermiso.Permitido),
            (Para(Rol.Cajero, AccionControlada.ExcederLimiteCredito).Nivel, Para(Rol.Encargado, AccionControlada.ExcederLimiteCredito).Nivel));
        Assert.Equal(NivelPermiso.NoPermitido, Para(Rol.Encargado, AccionControlada.AjustarCredito).Nivel);
        Assert.True(Para(Rol.Encargado, AccionControlada.CancelarAbono).PideMotivo);
        Assert.False(Permisos.SoloDelDueno(AccionControlada.DarCredito));
        Assert.Equal((NivelPermiso.RequiereAutorizacion, NivelPermiso.Permitido),
            (Para(Rol.Cajero, AccionControlada.DevolverSaldoAFavor).Nivel, Para(Rol.Encargado, AccionControlada.DevolverSaldoAFavor).Nivel));
    }

    // ── Sin conexión: la cuenta que conoce la caja (parte 2.4) ──────────────

    private static CreditoClienteDto DelServicio(long saldo, params CargoPendienteDto[] pendientes) =>
        new(Guid.NewGuid(), saldo, 0, 50000, 30, pendientes);

    [Fact]
    public void Estimar_suma_lo_local_en_orden_y_lo_abonado_paga_lo_mas_viejo()
    {
        var cuenta = CuentaCredito.Estimar(DelServicio(10000, new CargoPendienteDto(new DateOnly(2026, 10, 1), 10000)),
            [new CreditoLocal(5000, new DateOnly(2026, 11, 7)), new CreditoLocal(-3000)]);

        Assert.Equal(12000, cuenta.SaldoCentavos);
        Assert.Equal([new CargoPendienteDto(new DateOnly(2026, 10, 1), 7000), new CargoPendienteDto(new DateOnly(2026, 11, 7), 5000)], cuenta.Pendientes);
        Assert.Equal(7000, cuenta.Vencido(new DateOnly(2026, 10, 8)));
        Assert.Equal(12000, cuenta.Vencido(new DateOnly(2026, 11, 8)));
        Assert.True(cuenta.Estimado);
        Assert.Equal(cuenta.SaldoCentavos, cuenta.Pendientes.Sum(p => p.PendienteCentavos) - cuenta.AFavorCentavos);
    }

    [Fact]
    public void Estimar_deja_a_favor_lo_que_sobra_y_lo_fiado_despues_se_lo_come()
    {
        var aFavor = CuentaCredito.Estimar(DelServicio(10000, new CargoPendienteDto(new DateOnly(2026, 10, 1), 10000)), [new CreditoLocal(-15000)]);
        var despues = CuentaCredito.Estimar(aFavor with { Estimado = false }, [new CreditoLocal(2000, new DateOnly(2026, 11, 7))]);
        var debeOtraVez = CuentaCredito.Estimar(aFavor with { Estimado = false }, [new CreditoLocal(8000, new DateOnly(2026, 11, 7))]);

        Assert.Equal((-5000L, 5000L), (aFavor.SaldoCentavos, aFavor.AFavorCentavos));
        Assert.Empty(aFavor.Pendientes);
        Assert.Equal((-3000L, 0), (despues.SaldoCentavos, despues.Pendientes.Count));
        Assert.Equal([new CargoPendienteDto(new DateOnly(2026, 11, 7), 3000)], debeOtraVez.Pendientes);
    }

    [Fact]
    public void Estimar_un_abono_por_transferencia_baja_el_saldo_pero_queda_por_verificar()
    {
        var cuenta = CuentaCredito.Estimar(DelServicio(50000, new CargoPendienteDto(new DateOnly(2026, 10, 1), 50000)),
            [new CreditoLocal(-20000, PorVerificar: true)]);

        Assert.Equal((30000L, 20000L, 0L), (cuenta.SaldoCentavos, cuenta.PorVerificarCentavos, cuenta.DisponibleCentavos));
        Assert.Equal(10000, cuenta.Exceso(10000));
    }

    [Fact]
    public void Sin_nada_propio_la_cuenta_es_la_del_servicio()
    {
        var delServicio = DelServicio(10000, new CargoPendienteDto(new DateOnly(2026, 10, 1), 10000));

        var cuenta = CuentaCredito.Estimar(delServicio, []);

        Assert.Equal((delServicio.SaldoCentavos, delServicio.Pendientes.Single()), (cuenta.SaldoCentavos, cuenta.Pendientes.Single()));
    }
}
