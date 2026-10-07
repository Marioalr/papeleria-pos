using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>El cobro en efectivo se redondea al peso (fase 3, entrega 1): las ventas, los reembolsos y el corte.</summary>
public class RedondeoTests
{
    private static readonly DateTime Ahora = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Usuario = Ids.Nuevo();

    private readonly Turno _turno = new() { FondoCentavos = 50000 };
    private readonly ProductoEnVenta _goma = new(Ids.Nuevo(), "Goma", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly ProductoEnVenta _lapiz = new(Ids.Nuevo(), "Lápiz", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _piezaGoma;
    private readonly Presentacion _piezaLapiz;

    public RedondeoTests()
    {
        _piezaGoma = new Presentacion { VarianteId = _goma.VarianteId, Nombre = "Pieza", EsBase = true };
        _piezaLapiz = new Presentacion { VarianteId = _lapiz.VarianteId, Nombre = "Pieza", EsBase = true };
    }

    /// <summary>Una venta con una goma y un lápiz a esos precios (todavía sin cobrar).</summary>
    private Venta Venta(long goma, long lapiz = 0)
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A" };
        venta.AgregarProducto(_goma, _piezaGoma, Cantidad.Piezas(1), [new Precio { VarianteId = _goma.VarianteId, PrecioCentavos = goma }]);
        if (lapiz > 0)
            venta.AgregarProducto(_lapiz, _piezaLapiz, Cantidad.Piezas(1), [new Precio { VarianteId = _lapiz.VarianteId, PrecioCentavos = lapiz }]);
        return venta;
    }

    [Theory]
    [InlineData(3749, 3700)]
    [InlineData(3750, 3800)]
    [InlineData(3700, 3700)]
    [InlineData(49, 0)]
    [InlineData(50, 100)]
    public void Al_peso_mas_cercano_los_50_centavos_suben(long centavos, long redondeado) => Assert.Equal(redondeado, Redondeo.AlPeso(centavos));

    [Fact]
    public void El_efectivo_se_cobra_al_peso_y_el_total_queda_exacto()
    {
        var abajo = Venta(3749);
        var arriba = Venta(3750);

        abajo.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 5000)], Ahora);
        var faltan = Assert.Throws<InvalidOperationException>(() => arriba.ValidarCobros([new CobroSolicitado(FormaPago.Efectivo, 3750)]));
        arriba.Pagar(2, [new CobroSolicitado(FormaPago.Efectivo, 3800)], Ahora);

        var efectivo = abajo.Cobros.Single();
        Assert.Equal((3749L, -49L, 3700L), (abajo.TotalCentavos, abajo.RedondeoCentavos, abajo.TotalCobradoCentavos));
        Assert.Equal((3700L, 5000L, 1300L), (efectivo.MontoCentavos, efectivo.RecibidoCentavos, efectivo.CambioCentavos));
        Assert.Contains("0.50", faltan.Message);
        Assert.Equal((50L, 3800L, 0L), (arriba.RedondeoCentavos, arriba.Cobros.Single().MontoCentavos, arriba.CambioCentavos));
        // El IVA se desglosa del total exacto, no del redondeado.
        Assert.Equal(3749 - Redondeo.Dividir(3749 * 100L, 116), abajo.IvaCentavos);
    }

    [Fact]
    public void Con_tarjeta_solo_se_redondea_lo_que_falta_en_efectivo()
    {
        var mixta = Venta(5049);
        var todoTarjeta = Venta(5049);

        mixta.Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 2000, "123"), new CobroSolicitado(FormaPago.Efectivo, 3000)], Ahora);
        todoTarjeta.Pagar(2, [new CobroSolicitado(FormaPago.Tarjeta, 5049, "456")], Ahora);

        Assert.Equal(2000, mixta.Cobros.Single(c => c.Forma == FormaPago.Tarjeta).MontoCentavos);
        Assert.Equal(3000, mixta.Cobros.Single(c => c.Forma == FormaPago.Efectivo).MontoCentavos);
        Assert.Equal(-49, mixta.RedondeoCentavos);
        Assert.Equal((0L, false), (todoTarjeta.RedondeoCentavos, todoTarjeta.LlevaEfectivo));
    }

    [Fact]
    public void Lo_que_cuesta_menos_de_50_centavos_se_cobra_a_un_peso()
    {
        var clip = Venta(30);

        Assert.Equal(new EfectivoACobrar(30, 100), clip.Efectivo(0));
        clip.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 100)], Ahora);
        Assert.Equal(70, clip.RedondeoCentavos);
    }

    [Fact]
    public void Las_impresiones_no_se_redondean()
    {
        var venta = Venta(1025);
        venta.FijarImpresiones(450, 120);

        // La goma se redondea ($10.25 → $10) y las impresiones van exactas ($4.50).
        Assert.Equal(new EfectivoACobrar(1475, 1450), venta.Efectivo(0));
        Assert.Equal(new EfectivoACobrar(1475, 1475), venta.Efectivo(0, redondear: false));
    }

    [Fact]
    public void Con_impresiones_el_efectivo_cubre_primero_la_mercancia()
    {
        // Impresiones $10.00 y una goma de $20.49.
        var venta = Venta(2049);
        venta.FijarImpresiones(1000, 300);

        Assert.Equal(new EfectivoACobrar(1049, 1000), venta.Efectivo(2000));
        // Lo que falta después de la tarjeta son centavos de la goma: se cobra el peso mínimo.
        Assert.Equal(new EfectivoACobrar(49, 100), venta.Efectivo(3000));
    }

    [Fact]
    public void Una_goma_de_30_centavos_con_impresiones_no_paga_el_peso_minimo()
    {
        var venta = Venta(30);
        venta.FijarImpresiones(1000, 300);

        Assert.Equal(new EfectivoACobrar(1030, 1000), venta.Efectivo(0));
    }

    [Fact]
    public void Lo_pagado_con_tarjeta_se_regresa_en_pesos()
    {
        var venta = Venta(3749);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 3749, "123")], Ahora);

        var devolucion = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id,
            Usuario, "x", Ahora);

        Assert.Equal((3700L, -49L), (devolucion.ReembolsoEfectivoCentavos, devolucion.RedondeoCentavos));
    }

    [Fact]
    public void Tres_piezas_pagadas_con_tarjeta_devueltas_una_por_una_salen_en_pesos()
    {
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A" };
        var linea = venta.AgregarProducto(_goma, _piezaGoma, Cantidad.Piezas(3), [new Precio { VarianteId = _goma.VarianteId, PrecioCentavos = 3333 }]);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 9999, "123")], Ahora);

        var anteriores = new List<Devolucion>();
        for (var i = 0; i < 3; i++)
        {
            var d = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora, anteriores);
            CalculoDevolucion.Aplicar(venta, d);
            anteriores.Add(d);
        }

        Assert.Equal([3300L, 3400L, 3300L], anteriores.Select(d => d.ReembolsoEfectivoCentavos));
        Assert.All(anteriores, d => Assert.Equal(0, d.ReembolsoEfectivoCentavos % 100));
    }

    [Fact]
    public void Lo_que_se_cobro_sin_conexion_antes_del_redondeo_se_cobra_exacto()
    {
        var venta = Venta(1250);

        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 1250)], Ahora, redondear: false);

        Assert.Equal((0L, 1250L), (venta.RedondeoCentavos, venta.Cobros.Single().MontoCentavos));
    }

    [Fact]
    public void Las_devoluciones_regresan_pesos_y_al_final_lo_que_se_pago()
    {
        // $10.25 + $27.24 = $37.49, pagado en efectivo: $37.
        var venta = Venta(1025, 2724);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 3700)], Ahora);
        var goma = venta.Lineas.Single(l => l.Descripcion == "Goma");
        var lapiz = venta.Lineas.Single(l => l.Descripcion == "Lápiz");

        var primera = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(goma.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "No era", Ahora);
        CalculoDevolucion.Aplicar(venta, primera);
        var segunda = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(lapiz.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "Tampoco", Ahora,
            [primera]);
        CalculoDevolucion.Aplicar(venta, segunda);
        var corte = CalculoCorte.Calcular(_turno, [venta], [], TipoCorte.X, Usuario, Ahora, devoluciones: [primera, segunda]);

        Assert.Equal((1025L, 1000L, -25L), (primera.TotalCentavos, primera.ReembolsoEfectivoCentavos, primera.RedondeoCentavos));
        Assert.Equal((2724L, 2700L, -24L), (segunda.TotalCentavos, segunda.ReembolsoEfectivoCentavos, segunda.RedondeoCentavos));
        // Se regresó exactamente lo que se cobró, y en el corte el redondeo se anula.
        Assert.Equal(venta.TotalCobradoCentavos, primera.ReembolsoEfectivoCentavos + segunda.ReembolsoEfectivoCentavos);
        Assert.Equal((0L, 50000L), (corte.RedondeoCentavos, corte.EsperadoCentavos));
    }

    [Fact]
    public void Partir_la_devolucion_no_gana_nada()
    {
        // Dos gomas de 50 centavos, cobradas en $1.
        var venta = new Venta { TurnoId = _turno.Id, Serie = "A" };
        var linea = venta.AgregarProducto(_goma, _piezaGoma, Cantidad.Piezas(2), [new Precio { VarianteId = _goma.VarianteId, PrecioCentavos = 50 }]);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 100)], Ahora);

        var una = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora);
        CalculoDevolucion.Aplicar(venta, una);
        var otra = CalculoDevolucion.Armar(venta, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(1))], Ids.Nuevo(), _turno.Id, Usuario, "x", Ahora, [una]);

        Assert.Equal(100, una.ReembolsoEfectivoCentavos + otra.ReembolsoEfectivoCentavos);
    }

    [Fact]
    public void Al_cancelar_se_regresa_lo_cobrado_y_el_redondeo_se_anula()
    {
        var cancelada = Venta(3750);
        cancelada.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 5000)], Ahora);
        var vigente = Venta(3749);
        vigente.Pagar(2, [new CobroSolicitado(FormaPago.Efectivo, 3700)], Ahora);

        var cancelacion = CalculoDevolucion.ArmarCancelacion(cancelada, _turno.Id, Usuario, "Se arrepintió", Ahora);
        CalculoDevolucion.Aplicar(cancelada, cancelacion);
        var corte = CalculoCorte.Calcular(_turno, [cancelada, vigente], [], TipoCorte.X, Usuario, Ahora, devoluciones: [cancelacion]);

        Assert.Equal((3800L, 50L), (cancelacion.ReembolsoEfectivoCentavos, cancelacion.RedondeoCentavos));
        // Entraron $38 + $37, salieron $38: el esperado es lo que de verdad hay en el cajón.
        Assert.Equal(50000 + 3800 + 3700 - 3800, corte.EsperadoCentavos);
        Assert.Equal(-49, corte.RedondeoCentavos);
    }

    [Fact]
    public void La_cancelacion_con_tarjeta_regresa_en_efectivo_solo_lo_que_entro_en_efectivo()
    {
        var venta = Venta(5049);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 2000, "123"), new CobroSolicitado(FormaPago.Efectivo, 3000)], Ahora);

        var cancelacion = CalculoDevolucion.ArmarCancelacion(venta, _turno.Id, Usuario, "x", Ahora);

        Assert.Equal((5049L, 3000L, -49L), (cancelacion.TotalCentavos, cancelacion.ReembolsoEfectivoCentavos, cancelacion.RedondeoCentavos));
    }
}
