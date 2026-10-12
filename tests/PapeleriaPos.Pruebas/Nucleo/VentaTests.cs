using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

public class VentaTests
{
    private static readonly DateTime Ahora = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Matriz = Ids.Nuevo();

    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma punto fino azul", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly ProductoEnVenta _liston = new(Ids.Nuevo(), "Listón rojo", Impuesto.Iva16, UnidadBase.Metro, true);
    private readonly Presentacion _pieza;
    private readonly Presentacion _caja12;
    private readonly Presentacion _metro;
    private readonly List<Precio> _precios;

    public VentaTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _caja12 = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Caja 12", Factor = Cantidad.Piezas(12) };
        _metro = new Presentacion { VarianteId = _liston.VarianteId, Nombre = "Metro", Factor = Cantidad.Unidades(1), EsBase = true };
        _precios =
        [
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 },
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(12), PrecioCentavos = 800 },
            new() { VarianteId = _liston.VarianteId, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = 750 },
        ];
    }

    private Venta NuevaVenta() => new() { SucursalId = Matriz, Serie = "A", CreadaUtc = Ahora };

    [Fact]
    public void Agregar_el_mismo_producto_suma_cantidad_y_alcanza_el_mayoreo()
    {
        var venta = NuevaVenta();

        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios);
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(2), _precios);

        Assert.Single(venta.Lineas);
        Assert.Equal(Cantidad.Piezas(12), linea.Cantidad);
        Assert.Equal(800, linea.PrecioCentavos);
        Assert.Equal(9600, venta.TotalCentavos);
    }

    [Fact]
    public void La_linea_guarda_desde_cuanto_fue_mayoreo_y_lo_que_se_ahorro()
    {
        var venta = NuevaVenta();

        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(12), _precios);
        var caja = venta.AgregarProducto(_pluma, _caja12, Cantidad.Piezas(1), _precios);
        var poquitas = NuevaVenta().AgregarProducto(_pluma, _pieza, Cantidad.Piezas(11), _precios);

        // 12 a $8 en vez de $10: ahorra $24. La caja de 12 cuenta como 12 piezas.
        Assert.Equal((Cantidad.Piezas(12), 2400L), (linea.MayoreoDesde!.Value, linea.AhorroMayoreoCentavos));
        Assert.Equal((Cantidad.Piezas(12), 2400L), (caja.MayoreoDesde!.Value, caja.AhorroMayoreoCentavos));
        Assert.Equal((false, 0L), (poquitas.TieneMayoreo, poquitas.AhorroMayoreoCentavos));
    }

    [Fact]
    public void Con_el_precio_cambiado_ya_no_es_mayoreo()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(12), _precios);

        venta.CambiarPrecio(linea, 700, null);

        Assert.Equal((false, 0L), (linea.TieneMayoreo, linea.AhorroMayoreoCentavos));
    }

    [Fact]
    public void Las_escalas_de_un_producto_salen_en_orden_y_de_la_sucursal_si_tiene()
    {
        var escalas = CalculoPrecio.Escalas(_precios, Matriz, _pieza);

        Assert.False(escalas.DePresentacion);
        Assert.Equal([1000L, 800L], escalas.Escalas.Select(e => e.PrecioCentavos));
    }

    [Fact]
    public void Una_caja_de_12_sale_a_precio_de_mayoreo_y_descuenta_12_piezas()
    {
        var venta = NuevaVenta();

        var linea = venta.AgregarProducto(_pluma, _caja12, Cantidad.Piezas(1), _precios);

        Assert.Equal(Cantidad.Piezas(12), linea.CantidadBase);
        Assert.Equal(9600, linea.PrecioCentavos);
        Assert.Equal(9600, linea.ImporteCentavos);
    }

    [Fact]
    public void Por_metro_admite_fracciones_y_por_pieza_no()
    {
        var venta = NuevaVenta();

        var liston = venta.AgregarProducto(_liston, _metro, Cantidad.Unidades(1.5m), _precios);
        Assert.Equal(1125, liston.ImporteCentavos);

        var fraccion = Assert.Throws<ArgumentException>(() => venta.AgregarProducto(_pluma, _pieza, Cantidad.Unidades(0.5m), _precios));
        // Así lo ve el cajero: sin « (Parameter 'cantidad')» al final.
        Assert.Equal("Este producto se vende por pieza: no admite fracciones.", fraccion.Message);
        Assert.Single(venta.Lineas);
    }

    [Fact]
    public void Sin_precio_no_se_agrega()
    {
        var venta = NuevaVenta();
        var sinPrecio = new ProductoEnVenta(Ids.Nuevo(), "Sin precio", Impuesto.Iva16, UnidadBase.Pieza, true);

        Assert.Throws<InvalidOperationException>(() =>
            venta.AgregarProducto(sinPrecio, new Presentacion { VarianteId = sinPrecio.VarianteId, Nombre = "Pieza" }, Cantidad.Piezas(1), _precios));
        Assert.Empty(venta.Lineas);
    }

    [Fact]
    public void El_iva_se_desglosa_del_precio_que_ya_lo_incluye()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(1), _precios);   // $10.00 con IVA 16 %
        var exento = new ProductoEnVenta(Ids.Nuevo(), "Libro", Impuesto.Iva0, UnidadBase.Pieza, true);
        venta.AgregarProducto(exento, new Presentacion { VarianteId = exento.VarianteId, Nombre = "Pieza" }, Cantidad.Piezas(1),
            [new Precio { VarianteId = exento.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 5000 }]);

        Assert.Equal(6000, venta.TotalCentavos);
        Assert.Equal(138, venta.IvaCentavos); // 1000 − round(1000 / 1.16) = 1000 − 862
    }

    [Fact]
    public void Articulo_comun_va_sin_variante_y_sin_inventario()
    {
        var venta = NuevaVenta();

        var linea = venta.AgregarArticuloComun("Recuerdo", 2500, Cantidad.Piezas(2));

        Assert.Null(linea.VarianteId);
        Assert.False(linea.LlevaInventario);
        Assert.Equal(5000, venta.TotalCentavos);
    }

    [Fact]
    public void Quitar_una_linea_recalcula_el_total()
    {
        var venta = NuevaVenta();
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(2), _precios);
        venta.AgregarArticuloComun("Varios", 500, Cantidad.Piezas(1));

        venta.QuitarLinea(linea);

        Assert.Equal(500, venta.TotalCentavos);
    }

    [Fact]
    public void Pagar_en_efectivo_da_cambio_y_asigna_folio()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);

        venta.Pagar(123, [new CobroSolicitado(FormaPago.Efectivo, 5000)], Ahora);

        Assert.Equal(EstadoVenta.Pagada, venta.Estado);
        Assert.Equal("A-000123", venta.FolioTexto);
        var efectivo = Assert.Single(venta.Cobros);
        Assert.Equal((3000L, 5000L, 2000L), (efectivo.MontoCentavos, efectivo.RecibidoCentavos, efectivo.CambioCentavos));
        Assert.Throws<InvalidOperationException>(() => venta.AgregarArticuloComun("Otro", 100, Cantidad.Piezas(1)));
    }

    [Fact]
    public void Cobro_mixto_y_la_transferencia_queda_por_verificar()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios); // $100

        venta.Pagar(1, [new CobroSolicitado(FormaPago.Transferencia, 6000, "SPEI 123"), new CobroSolicitado(FormaPago.Efectivo, 5000)], Ahora);

        var transferencia = venta.Cobros.Single(c => c.Forma == FormaPago.Transferencia);
        Assert.Equal(Verificacion.PorVerificar, transferencia.Verificacion);
        var efectivo = venta.Cobros.Single(c => c.Forma == FormaPago.Efectivo);
        Assert.Equal((4000L, 1000L), (efectivo.MontoCentavos, efectivo.CambioCentavos));
    }

    [Fact]
    public void Pago_completo_con_tarjeta_no_lleva_efectivo()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(1), _precios);

        venta.Pagar(1, [new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 55")], Ahora);

        Assert.False(venta.LlevaEfectivo);
        Assert.Equal(0, venta.CambioCentavos);
    }

    [Theory]
    [InlineData(FormaPago.Efectivo, 2000)]   // falta dinero
    [InlineData(FormaPago.Tarjeta, 4000)]    // la tarjeta no da cambio
    public void Cobros_que_no_cuadran_se_rechazan_sin_cambiar_nada(FormaPago forma, long monto)
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);

        Assert.Throws<InvalidOperationException>(() => venta.Pagar(1, [new CobroSolicitado(forma, monto)], Ahora));
        Assert.Equal(EstadoVenta.EnCaptura, venta.Estado);
        Assert.Null(venta.Folio);
        Assert.Empty(venta.Cobros);
    }

    [Theory]
    [InlineData(13700, 20000, RevisionDelCambio.Normal)]        // $137 con $200
    [InlineData(4500, 50000, RevisionDelCambio.Normal)]         // con un billete de $500: $455 de cambio
    [InlineData(4500, 100000, RevisionDelCambio.PorConfirmar)]  // con uno de $1,000: $955, se confirma
    [InlineData(13700, 200000, RevisionDelCambio.PorConfirmar)] // «2000» por «200»: $1,863, se confirma
    [InlineData(100, 200100, RevisionDelCambio.PorConfirmar)]   // justo dos billetes de $1,000 de cambio
    [InlineData(100, 200101, RevisionDelCambio.NoEsDinero)]     // un centavo más
    [InlineData(13700, 1370000, RevisionDelCambio.NoEsDinero)]  // «13700» por «137.00»
    [InlineData(13700, 750123456789000, RevisionDelCambio.NoEsDinero)] // el código 7501234567890 en «Pagó con»
    [InlineData(13700, 10000, RevisionDelCambio.Normal)]        // falta dinero: eso se revisa aparte
    public void El_cambio_dice_si_es_dinero(long aCobrar, long recibido, RevisionDelCambio esperado) =>
        Assert.Equal(esperado, TopeDeCambio.Revisar(aCobrar, recibido));

    [Fact]
    public void Un_codigo_de_barras_en_pago_con_no_se_cobra_ni_cambia_nada()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios); // $30

        var error = Assert.Throws<InvalidOperationException>(() =>
            venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 750123456789000)], Ahora));

        Assert.StartsWith("«Pagó con» $7,501,234,567,890.00 no puede ser", error.Message);
        Assert.Equal(EstadoVenta.EnCaptura, venta.Estado);
        Assert.Null(venta.Folio);
        Assert.Empty(venta.Cobros);
        Assert.Throws<InvalidOperationException>(() => venta.ValidarCobros([new CobroSolicitado(FormaPago.Efectivo, 750123456789000)]));
    }

    [Fact]
    public void Hasta_dos_mil_pesos_de_cambio_se_cobran_y_con_tarjeta_el_tope_es_el_efectivo()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios); // $100
        var mixta = NuevaVenta();
        mixta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios);

        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 210000)], Ahora);
        // Con $60 en tarjeta, el efectivo cubre $40: $2,041 darían $2,001 de cambio.
        Assert.Throws<InvalidOperationException>(() =>
            mixta.Pagar(2, [new CobroSolicitado(FormaPago.Tarjeta, 6000, "Aut 1"), new CobroSolicitado(FormaPago.Efectivo, 204100)], Ahora));

        Assert.Equal(200000, venta.CambioCentavos);
        Assert.Equal(EstadoVenta.EnCaptura, mixta.Estado);
    }

    [Fact]
    public void Lo_cobrado_sin_conexion_no_se_rechaza_por_el_cambio()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);

        venta.Pagar(7, [new CobroSolicitado(FormaPago.Efectivo, 500000)], Ahora, topeDeCambio: false);

        Assert.Equal(EstadoVenta.Pagada, venta.Estado);
        Assert.Equal(497000, venta.CambioCentavos);
    }

    [Fact]
    public void Numero_de_articulos_cuenta_piezas_y_una_por_linea_a_granel()
    {
        var venta = NuevaVenta();
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);
        venta.AgregarProducto(_liston, _metro, Cantidad.Unidades(1.5m), _precios);

        Assert.Equal(4m, venta.NumeroDeArticulos);
    }
}
