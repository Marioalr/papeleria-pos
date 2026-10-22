using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Las promociones con fechas (fase 3) en el cálculo del precio: cada tipo, sus fechas, sus días y la medianoche, y que no se
/// acumulan (se queda la que más le conviene al cliente, también contra el mayoreo). Es el único lugar donde se decide el
/// precio: la pantalla, el cobro sin conexión y el servicio llaman a esto mismo.
/// </summary>
public class PromocionesTests
{
    private static readonly Guid Matriz = Ids.Nuevo();

    /// <summary>Un miércoles.</summary>
    private static readonly DateOnly Hoy = new(2027, 8, 4);

    private readonly Variante _cuaderno = new() { UnidadBase = UnidadBase.Pieza };
    private readonly Presentacion _pieza;
    private readonly Presentacion _docena;

    public PromocionesTests()
    {
        _pieza = new Presentacion { VarianteId = _cuaderno.Id, Nombre = "Pieza", Factor = Cantidad.Piezas(1), EsBase = true };
        _docena = new Presentacion { VarianteId = _cuaderno.Id, Nombre = "Docena", Factor = Cantidad.Piezas(12) };
    }

    private Precio PorPieza(long centavos, int desde = 1) => new()
    {
        VarianteId = _cuaderno.Id, CantidadMinima = Cantidad.Piezas(desde), PrecioCentavos = centavos,
    };

    private static PromocionDeProducto Regla(TipoPromocion tipo, int lleva = 0, int paga = 0, int centesimas = 0, long? precio = null,
        DateOnly? desde = null, DateOnly? hasta = null, DiasDeLaSemana dias = DiasDeLaSemana.Todos, Guid? id = null, string nombre = "Regreso a clases") =>
        new(id ?? Ids.Nuevo(), nombre, tipo, lleva, paga, centesimas, precio, desde ?? Hoy.AddDays(-10), hasta ?? Hoy.AddDays(10), dias);

    private PrecioAplicado Calcular(Precio[] precios, Presentacion presentacion, decimal cantidad, params PromocionDeProducto[] promociones) =>
        CalculoPrecio.Calcular(precios, Matriz, presentacion, Cantidad.Unidades(cantidad), promociones: promociones, dia: Hoy)!;

    // ── Cada tipo ──

    [Fact]
    public void Lleva_tres_y_paga_dos_cuenta_los_grupos_completos()
    {
        var tresPorDos = Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2);
        Precio[] precios = [PorPieza(1000)];

        Assert.Null(Calcular(precios, _pieza, 2, tresPorDos).Promocion);
        Assert.Equal((2000L, 1000L), (Calcular(precios, _pieza, 3, tresPorDos).ImporteCentavos, Calcular(precios, _pieza, 3, tresPorDos).AhorroPromocionCentavos));
        Assert.Equal(4000, Calcular(precios, _pieza, 5, tresPorDos).ImporteCentavos);
        Assert.Equal(4000, Calcular(precios, _pieza, 6, tresPorDos).ImporteCentavos);
        // Con la promoción, la línea va a su menudeo: no es mayoreo.
        var aplicado = Calcular(precios, _pieza, 6, tresPorDos);
        Assert.Equal(1000, aplicado.Precio.PrecioCentavos);
        Assert.False(aplicado.EsMayoreo);
        Assert.Same(tresPorDos, aplicado.Promocion);
    }

    [Fact]
    public void El_segundo_a_mitad_de_precio_va_por_pares()
    {
        var segundo = Regla(TipoPromocion.SegundoAMitad);
        Precio[] precios = [PorPieza(1000)];

        Assert.Null(Calcular(precios, _pieza, 1, segundo).Promocion);
        Assert.Equal(1500, Calcular(precios, _pieza, 2, segundo).ImporteCentavos);
        Assert.Equal(2500, Calcular(precios, _pieza, 3, segundo).ImporteCentavos);
        Assert.Equal(3000, Calcular(precios, _pieza, 4, segundo).ImporteCentavos);
    }

    [Fact]
    public void El_porcentaje_va_sobre_el_menudeo_de_toda_la_cantidad()
    {
        var aplicado = Calcular([PorPieza(1250)], _pieza, 2, Regla(TipoPromocion.Porcentaje, centesimas: 1500));

        Assert.Equal(2125, aplicado.ImporteCentavos);
        Assert.Equal(375, aplicado.AhorroPromocionCentavos);
    }

    [Fact]
    public void El_precio_especial_es_por_pieza_tambien_en_el_paquete()
    {
        var alCosto = Regla(TipoPromocion.PrecioEspecial, precio: 730);
        Precio[] precios = [PorPieza(1250)];

        Assert.Equal(2190, Calcular(precios, _pieza, 3, alCosto).ImporteCentavos);
        var docena = Calcular(precios, _docena, 1, alCosto);
        Assert.Equal(12 * 730, docena.ImporteCentavos);
        // El precio de la línea es el de la docena a menudeo; lo que se ahorró es contra él.
        Assert.Equal(12 * 1250, docena.PrecioPorPresentacion(_docena));
        Assert.Equal(12 * (1250 - 730), docena.AhorroPromocionCentavos);
    }

    [Fact]
    public void Un_paquete_cuenta_sus_piezas_para_el_tres_por_dos_como_para_el_mayoreo()
    {
        // Una docena son 12 piezas: 4 grupos de 3, 4 van gratis.
        var aplicado = Calcular([PorPieza(800)], _docena, 1, Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2));

        Assert.Equal(8 * 800, aplicado.ImporteCentavos);
    }

    [Fact]
    public void A_granel_solo_cuentan_las_unidades_enteras()
    {
        var liston = new Variante { UnidadBase = UnidadBase.Metro };
        var metro = new Presentacion { VarianteId = liston.Id, Nombre = "Metro", Factor = Cantidad.Unidades(1), EsBase = true };
        Precio[] precios = [new Precio { VarianteId = liston.Id, CantidadMinima = Cantidad.Unidades(0.001m), PrecioCentavos = 600 }];

        var aplicado = CalculoPrecio.Calcular(precios, Matriz, metro, Cantidad.Unidades(3.5m),
            promociones: [Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2)], dia: Hoy)!;

        // 3.5 m son un grupo de 3: un metro gratis, se pagan 2.5.
        Assert.Equal(1500, aplicado.ImporteCentavos);
    }

    // ── Fechas, días y medianoche ──

    [Fact]
    public void Corre_entre_sus_fechas_y_en_sus_dias()
    {
        var finDeSemana = Regla(TipoPromocion.Porcentaje, centesimas: 1000, desde: new DateOnly(2027, 8, 1), hasta: new DateOnly(2027, 8, 31),
            dias: DiasDeLaSemana.Sabado | DiasDeLaSemana.Domingo);

        Assert.False(finDeSemana.AplicaEl(new DateOnly(2027, 7, 31)));
        Assert.True(finDeSemana.AplicaEl(new DateOnly(2027, 8, 1)));
        Assert.False(finDeSemana.AplicaEl(new DateOnly(2027, 8, 4)));
        Assert.True(finDeSemana.AplicaEl(new DateOnly(2027, 8, 7)));
        Assert.False(finDeSemana.AplicaEl(new DateOnly(2027, 9, 4)));
        Assert.Null(Calcular([PorPieza(1000)], _pieza, 1, finDeSemana).Promocion);
        Assert.Equal("sábado y domingo", Semana.Texto(DiasDeLaSemana.Sabado | DiasDeLaSemana.Domingo));
        Assert.Equal("lunes a viernes", Semana.Texto(DiasDeLaSemana.Lunes | DiasDeLaSemana.Martes | DiasDeLaSemana.Miercoles | DiasDeLaSemana.Jueves
            | DiasDeLaSemana.Viernes));
        Assert.Equal("lunes, miércoles y viernes", Semana.Texto(DiasDeLaSemana.Lunes | DiasDeLaSemana.Miercoles | DiasDeLaSemana.Viernes));
        Assert.Equal("todos los días", Semana.Texto(DiasDeLaSemana.Todos));
        Assert.Equal("del 1/08/2027 al 31/08/2027 · sábado y domingo", Promocion.FechasTexto(finDeSemana.Desde, finDeSemana.Hasta, finDeSemana.Dias));
    }

    /// <summary>
    /// La venta lee el día cada vez que calcula: una promoción que empieza a medianoche se aplica a lo que se escanea después,
    /// sin bajar nada; y la que termina, deja de aplicarse.
    /// </summary>
    [Fact]
    public void A_medianoche_empieza_una_y_termina_otra_sin_bajar_nada()
    {
        var dia = new DateOnly(2027, 8, 14);
        var empieza = Regla(TipoPromocion.Porcentaje, centesimas: 2000, desde: dia.AddDays(1), hasta: dia.AddDays(30), nombre: "Empieza mañana");
        var otro = new Variante { UnidadBase = UnidadBase.Pieza };
        var piezaOtro = new Presentacion { VarianteId = otro.Id, Nombre = "Pieza", Factor = Cantidad.Piezas(1), EsBase = true };
        var termina = new PromocionDeProducto(Ids.Nuevo(), "Termina hoy", TipoPromocion.Porcentaje, 0, 0, 1000, null, dia.AddDays(-30), dia, DiasDeLaSemana.Todos);
        var venta = new Venta
        {
            Serie = "A", SucursalId = Matriz,
            Promociones = new PromocionesDeLaVenta(v => v == _cuaderno.Id ? [empieza] : v == otro.Id ? [termina] : [], () => dia),
        };
        Precio[] delCuaderno = [PorPieza(1000)];
        Precio[] delOtro = [new Precio { VarianteId = otro.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 2000 }];

        var cuaderno = venta.AgregarProducto(new ProductoEnVenta(_cuaderno.Id, "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true), _pieza, Cantidad.Piezas(1), delCuaderno);
        var conOtro = venta.AgregarProducto(new ProductoEnVenta(otro.Id, "Mochila", Impuesto.Iva16, UnidadBase.Pieza, true), piezaOtro, Cantidad.Piezas(1), delOtro);
        Assert.Equal((1000L, 1800L), (cuaderno.ImporteCentavos, conOtro.ImporteCentavos));
        Assert.Equal("Termina hoy", conOtro.Promocion);

        dia = dia.AddDays(1);
        venta.CambiarCantidad(cuaderno, Cantidad.Piezas(1), _pieza, delCuaderno);
        venta.CambiarCantidad(conOtro, Cantidad.Piezas(1), piezaOtro, delOtro);

        Assert.Equal((800L, "Empieza mañana", 200L), (cuaderno.ImporteCentavos, cuaderno.Promocion, cuaderno.AhorroPromocionCentavos));
        Assert.Equal((2000L, (string?)null, 0L), (conOtro.ImporteCentavos, conOtro.Promocion, conOtro.AhorroPromocionCentavos));
    }

    // ── No se acumulan ──

    [Fact]
    public void Contra_el_mayoreo_se_queda_lo_que_mas_le_conviene_al_cliente()
    {
        Precio[] precios = [PorPieza(800), PorPieza(650, desde: 12)];
        var diezPorCiento = Regla(TipoPromocion.Porcentaje, centesimas: 1000);

        // 5: la promoción ($36) le conviene más que el menudeo ($40).
        var cinco = Calcular(precios, _pieza, 5, diezPorCiento);
        Assert.Equal((3600L, 400L), (cinco.ImporteCentavos, cinco.AhorroPromocionCentavos));
        // 12: el mayoreo ($78) le conviene más que la promoción ($86.40): no se juntan.
        var doce = Calcular(precios, _pieza, 12, diezPorCiento);
        Assert.Null(doce.Promocion);
        Assert.True(doce.EsMayoreo);
        Assert.Equal(7800, doce.ImporteCentavos);
        // Con el mayoreo siempre del cliente, igual: lo que más le conviene.
        var deCliente = CalculoPrecio.Calcular(precios, Matriz, _pieza, Cantidad.Piezas(2), mayoreoSiempre: true, promociones: [diezPorCiento], dia: Hoy)!;
        Assert.Equal(1300, deCliente.ImporteCentavos);
        Assert.Null(deCliente.Promocion);
    }

    [Fact]
    public void Entre_dos_promociones_se_queda_la_que_deja_menos_y_en_un_empate_siempre_la_misma()
    {
        Precio[] precios = [PorPieza(1000)];
        var tresPorDos = Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2, nombre: "3×2");
        var veinte = Regla(TipoPromocion.Porcentaje, centesimas: 2000, nombre: "20 %");

        Assert.Equal("3×2", Calcular(precios, _pieza, 3, tresPorDos, veinte).Promocion!.Nombre);
        Assert.Equal("20 %", Calcular(precios, _pieza, 2, tresPorDos, veinte).Promocion!.Nombre);

        // Dos que dejan lo mismo: la de menor Id, en el orden en que lleguen (la caja y el servicio eligen la misma).
        var a = Regla(TipoPromocion.Porcentaje, centesimas: 1000, id: Guid.Parse("00000000-0000-0000-0000-000000000001"), nombre: "A");
        var b = Regla(TipoPromocion.PrecioEspecial, precio: 900, id: Guid.Parse("00000000-0000-0000-0000-000000000002"), nombre: "B");
        Assert.Equal("A", Calcular(precios, _pieza, 1, a, b).Promocion!.Nombre);
        Assert.Equal("A", Calcular(precios, _pieza, 1, b, a).Promocion!.Nombre);
    }

    // ── La línea de la venta ──

    [Fact]
    public void La_linea_dice_la_promocion_y_el_descuento_va_encima_de_ella()
    {
        var tresPorDos = Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2);
        var venta = new Venta { Serie = "A", SucursalId = Matriz, Promociones = new PromocionesDeLaVenta(_ => [tresPorDos], () => Hoy) };
        Precio[] precios = [PorPieza(1000)];

        var linea = venta.AgregarProducto(new ProductoEnVenta(_cuaderno.Id, "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true), _pieza, Cantidad.Piezas(3), precios);

        Assert.Equal((1000L, 2000L, 1000L), (linea.PrecioCentavos, linea.ImporteCentavos, linea.AhorroPromocionCentavos));
        Assert.Equal((tresPorDos.PromocionId, "Regreso a clases"), (linea.PromocionId!.Value, linea.Promocion));
        Assert.Equal(linea.Cantidad.Importe(linea.PrecioCentavos) - linea.AhorroPromocionCentavos, linea.ImporteSinDescuentoCentavos);
        // Un descuento a mano va sobre el precio con la promoción.
        venta.DescontarLinea(linea, 1000, null, "Cliente frecuente");
        Assert.Equal((200L, 1800L), (linea.DescuentoCentavos, linea.ImporteCentavos));
        // Un precio cambiado a mano ya no lleva promoción.
        venta.CambiarPrecio(linea, 900, null);
        Assert.Equal((null, 0L), (linea.Promocion, linea.AhorroPromocionCentavos));
        Assert.Equal(2700 - 270, linea.ImporteCentavos);
    }

    /// <summary>
    /// Revisión del PR #132: el descuento fijo del cliente registrado no se acumula con la promoción. En cada renglón se queda lo
    /// que más le conviene al cliente: la promoción (y su descuento queda en pausa), o su precio con su descuento. Se vuelve a
    /// decidir al cambiar la cantidad y al quitarle el descuento.
    /// </summary>
    [Fact]
    public void El_descuento_del_cliente_no_se_acumula_con_la_promocion_se_queda_lo_que_mas_le_conviene()
    {
        var tresPorDos = Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2);
        var venta = new Venta { Serie = "A", SucursalId = Matriz, Promociones = new PromocionesDeLaVenta(_ => [tresPorDos], () => Hoy) };
        Precio[] precios = [PorPieza(1000)];
        var cuaderno = new ProductoEnVenta(_cuaderno.Id, "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true);
        var motivo = PapeleriaPos.Nucleo.Clientes.ClienteRegistrado.MotivoDescuento("Maestra Ana");

        // 3 con el 5 % del cliente: el 3×2 ($20) le conviene más que $30 con 5 % ($28.50). El descuento queda en pausa.
        var linea = venta.AgregarProducto(cuaderno, _pieza, Cantidad.Piezas(3), precios);
        venta.DescontarLinea(linea, 500, null, motivo);
        Assert.Equal((2000L, 0L, "Regreso a clases"), (linea.ImporteCentavos, linea.DescuentoCentavos, linea.Promocion));
        Assert.False(linea.TieneDescuento);
        Assert.True(linea.DescuentoDeCliente);
        Assert.Equal(0, venta.DescuentoARevisar(500));

        // Con 2 no hay 3×2: vuelve su 5 %.
        venta.CambiarCantidad(linea, Cantidad.Piezas(2), _pieza, precios);
        Assert.Equal((1900L, 100L, (string?)null), (linea.ImporteCentavos, linea.DescuentoCentavos, linea.Promocion));
        venta.CambiarCantidad(linea, Cantidad.Piezas(3), _pieza, precios);
        Assert.Equal((2000L, 0L), (linea.ImporteCentavos, linea.DescuentoCentavos));

        // Con 40 % le conviene más su descuento ($18) que el 3×2 ($20): sin la promoción.
        venta.DescontarLinea(linea, 4000, null, motivo);
        Assert.Equal((1800L, 1200L, (Guid?)null), (linea.ImporteCentavos, linea.DescuentoCentavos, linea.PromocionId));
        // Sin el descuento del cliente (sale del ticket), vuelve la promoción.
        venta.QuitarDescuento(linea);
        Assert.Equal((2000L, "Regreso a clases"), (linea.ImporteCentavos, linea.Promocion));

        // Un descuento a mano sí va encima de la promoción.
        venta.DescontarLinea(linea, 1000, null, "Pasta doblada");
        Assert.Equal((1800L, 200L, "Regreso a clases"), (linea.ImporteCentavos, linea.DescuentoCentavos, linea.Promocion));
    }

    [Fact]
    public void Lo_que_se_cobro_sin_conexion_entra_con_su_promocion_aunque_aqui_ya_no_este()
    {
        var venta = new Venta { Serie = "A", SucursalId = Matriz };
        Precio[] precios = [PorPieza(800), PorPieza(650, desde: 12)];
        var linea = venta.AgregarProducto(new ProductoEnVenta(_cuaderno.Id, "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true), _pieza, Cantidad.Piezas(12), precios);
        Assert.True(linea.TieneMayoreo);
        var promocion = Ids.Nuevo();

        venta.FijarPromocionCobrada(linea, 800, promocion, "Regreso a clases", 12 * 800 - 7000);

        Assert.Equal((800L, 7000L, promocion), (linea.PrecioCentavos, linea.ImporteCentavos, linea.PromocionId!.Value));
        Assert.False(linea.TieneMayoreo);
        Assert.False(linea.PrecioCambiado);
        Assert.Throws<ArgumentException>(() => venta.FijarPromocionCobrada(linea, 800, promocion, "Regreso a clases", 9600));
    }

    // ── Al devolver ──

    /// <summary>
    /// Revisión del PR #132: devolver de una en una las tres piezas de un 3×2 de $10 ($20) da $6.67, $6.66 y $6.67: suman lo
    /// cobrado, como el efectivo (antes, $20.01). Devolver las tres de una vez da $20, como siempre.
    /// </summary>
    [Fact]
    public void Devolver_de_una_en_una_las_piezas_de_un_tres_por_dos_suma_lo_cobrado()
    {
        var ahora = HoraMexico.AUtc(Hoy.ToDateTime(new TimeOnly(12, 0)));
        Venta Vendida()
        {
            var venta = new Venta { Serie = "A", SucursalId = Matriz, Promociones = new PromocionesDeLaVenta(_ => [Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2)], () => Hoy) };
            var linea = venta.AgregarProducto(new ProductoEnVenta(_cuaderno.Id, "Cuaderno", Impuesto.Iva16, UnidadBase.Pieza, true), _pieza, Cantidad.Piezas(3),
                [PorPieza(1000)]);
            linea.CostoCentavos = 1000;
            venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 2000)], ahora);
            return venta;
        }

        var deUnaEnUna = Vendida();
        var devoluciones = new List<Devolucion>();
        for (var i = 0; i < 3; i++)
        {
            var d = CalculoDevolucion.Armar(deUnaEnUna, [new LineaPorDevolver(deUnaEnUna.Lineas[0].Id, Cantidad.Piezas(1))], Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(),
                "No era", ahora, devoluciones);
            CalculoDevolucion.Aplicar(deUnaEnUna, d);
            devoluciones.Add(d);
        }
        var todas = Vendida();
        var completa = CalculoDevolucion.Armar(todas, [new LineaPorDevolver(todas.Lineas[0].Id, Cantidad.Piezas(3))], Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(),
            "No era", ahora);

        Assert.Equal([667L, 666L, 667L], devoluciones.Select(d => d.TotalCentavos));
        Assert.Equal((2000L, 2000L, 1000L), (devoluciones.Sum(d => d.TotalCentavos), devoluciones.Sum(d => d.ReembolsoEfectivoCentavos),
            devoluciones.Sum(d => d.Lineas.Sum(l => l.CostoCentavos))));
        Assert.Equal((2000L, 1000L), (completa.TotalCentavos, completa.Lineas[0].CostoCentavos));
    }

    // ── Al darla de alta ──

    [Fact]
    public void Se_revisa_lo_de_su_tipo_sus_fechas_sus_dias_y_a_que_toca()
    {
        Promocion Nueva(Action<Promocion> como)
        {
            var p = new Promocion { Nombre = "  Regreso   a clases ", Tipo = TipoPromocion.LlevaNPagaM, Lleva = 3, Paga = 2, Desde = Hoy, Hasta = Hoy.AddDays(5) };
            p.Alcance.Add(new AlcancePromocion { VarianteId = Ids.Nuevo() });
            como(p);
            return p;
        }

        var buena = Nueva(_ => { });
        buena.Validar();
        Assert.Equal("Regreso a clases", buena.Nombre);
        Assert.Throws<ArgumentException>(() => Nueva(p => p.Paga = 3).Validar());
        Assert.Throws<ArgumentException>(() => Nueva(p => (p.Tipo, p.Centesimas) = (TipoPromocion.Porcentaje, 10000)).Validar());
        Assert.Throws<ArgumentException>(() => Nueva(p => p.Hasta = Hoy.AddDays(-1)).Validar());
        Assert.Throws<ArgumentException>(() => Nueva(p => p.Dias = DiasDeLaSemana.Ninguno).Validar());
        Assert.Throws<ArgumentException>(() => Nueva(p => p.Alcance.Clear()).Validar());
        // Un precio especial es el de cada producto: no un departamento, y cada uno con su precio.
        Assert.Throws<ArgumentException>(() => Nueva(p => (p.Tipo, p.Alcance[0]) = (TipoPromocion.PrecioEspecial, new AlcancePromocion { DepartamentoId = Ids.Nuevo() }))
            .Validar());
        Assert.Throws<ArgumentException>(() => Nueva(p => p.Tipo = TipoPromocion.PrecioEspecial).Validar());
    }

    [Fact]
    public void Su_precio_mas_bajo_es_con_el_grupo_completo()
    {
        decimal? MasBajo(PromocionDeProducto regla, long porPieza) => CalculoPrecio.PrecioMasBajoCon(regla, [PorPieza(porPieza)], [_pieza, _docena], Matriz);

        Assert.Equal(600m, MasBajo(Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2), 900));
        Assert.Equal(750m, MasBajo(Regla(TipoPromocion.SegundoAMitad), 1000));
        Assert.Equal(850m, MasBajo(Regla(TipoPromocion.Porcentaje, centesimas: 1500), 1000));
        Assert.Equal(730m, MasBajo(Regla(TipoPromocion.PrecioEspecial, precio: 730), 1250));
        // Un precio especial más alto que el menudeo no lo baja (la venta no lo aplica): no lo deja debajo de nada.
        Assert.Null(MasBajo(Regla(TipoPromocion.PrecioEspecial, precio: 1300), 1250));
    }

    /// <summary>
    /// Revisión del PR #132: la docena con precio propio ($120, $10 la pieza) de un cuaderno de $12 baja con un 3×2 a $6.67 la
    /// pieza (tres docenas por $240), no a los $8 de la pieza. Contra eso se revisa el costo.
    /// </summary>
    [Fact]
    public void Su_precio_mas_bajo_cuenta_la_presentacion_con_precio_propio_como_la_venta()
    {
        Precio Docena(long centavos) => new() { VarianteId = _cuaderno.Id, PresentacionId = _docena.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = centavos };
        var tresPorDos = Regla(TipoPromocion.LlevaNPagaM, lleva: 3, paga: 2);
        Precio[] precios = [PorPieza(1200), Docena(12000)];

        Assert.Equal(666.6667m, Math.Round(CalculoPrecio.PrecioMasBajoCon(tresPorDos, precios, [_pieza, _docena], Matriz)!.Value, 4));
        Assert.Equal(24000, Calcular(precios, _docena, 3, tresPorDos).ImporteCentavos);
        // Una docena con precio propio más caro que sus piezas no la usa la venta (la cobra por sus piezas): cuenta la pieza.
        Assert.Equal(800m, CalculoPrecio.PrecioMasBajoCon(tresPorDos, [PorPieza(1200), Docena(15000)], [_pieza, _docena], Matriz));
        // El precio especial es por pieza en cualquier presentación.
        Assert.Equal(700m, CalculoPrecio.PrecioMasBajoCon(Regla(TipoPromocion.PrecioEspecial, precio: 700), precios, [_pieza, _docena], Matriz));
    }

    [Fact]
    public void Las_de_una_copia_se_buscan_por_producto_y_su_huella_cambia_con_ellas()
    {
        var cuaderno = Ids.Nuevo();
        PromocionDto[] lista =
        [
            new(Ids.Nuevo(), "Al costo", TipoPromocion.PrecioEspecial, 0, 0, 0, Hoy, Hoy.AddDays(20), DiasDeLaSemana.Todos, [new VarianteEnPromocionDto(cuaderno, 730)]),
        ];

        var porProducto = new PromocionesPorProducto(lista);

        Assert.Equal(730, Assert.Single(porProducto.De(cuaderno)).PrecioCentavos);
        Assert.Empty(porProducto.De(Ids.Nuevo()));
        Assert.Empty(porProducto.DeHoy(cuaderno, Hoy.AddDays(21)));
        Assert.Equal(PromocionDto.Huella(lista), PromocionDto.Huella([lista[0] with { }]));
        Assert.NotEqual(PromocionDto.Huella(lista), PromocionDto.Huella([lista[0] with { Hasta = Hoy.AddDays(21) }]));
        Assert.NotEqual(PromocionDto.Huella(lista), PromocionDto.Huella([]));
    }
}
