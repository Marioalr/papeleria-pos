using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>Filas inventadas con las rarezas que tiene la base real de eleventa.</summary>
public class ImportadorEleventaTests
{
    private static readonly DateTime Vendido = new(2026, 8, 20, 12, 30, 0);

    private static DatosEleventa Datos(
        IEnumerable<FilaProducto>? productos = null, IEnumerable<FilaExistencia>? existencias = null,
        IEnumerable<FilaTicket>? tickets = null, IEnumerable<FilaLinea>? lineas = null, IEnumerable<FilaPromocion>? promociones = null) => new()
    {
        Departamentos = [new(2, "papeleria", true), new(13, "Impresiones", true)],
        Productos = productos?.ToList() ?? [],
        Existencias = existencias?.ToList() ?? [],
        Tickets = tickets?.ToList() ?? [],
        Lineas = lineas?.ToList() ?? [],
        Promociones = promociones?.ToList() ?? [],
    };

    private static FilaProducto Producto(int id, string codigo, string descripcion, char tipo = 'U', int medida = 1, double costo = 10,
        double precio = 15, double mayoreo = 15, int? departamento = 2, bool eliminado = false) =>
        new(id, codigo, descripcion, tipo, medida, costo, precio, mayoreo, departamento, eliminado);

    private static FilaLinea Linea(int ticket, string codigo, double cantidad, double precio, double? total, double ganancia, string nombre = "x",
        bool mayoreo = false) =>
        new(ticket, codigo, nombre, cantidad, total, precio, ganancia, 2, mayoreo);

    /// <summary>Ventas con F11 del producto, una por ticket: (cantidad, mes) fuera o dentro del regreso a clases.</summary>
    private static (List<FilaTicket> Tickets, List<FilaLinea> Lineas) ConF11(string codigo, double precio, params (double Cantidad, int Mes)[] ventas)
    {
        var tickets = ventas.Select((v, i) => new FilaTicket(500 + i, 500 + i, new DateTime(2026, v.Mes, 5, 12, 0, 0), v.Cantidad * precio, false)).ToList();
        var lineas = ventas.Select((v, i) => Linea(500 + i, codigo, v.Cantidad, precio, v.Cantidad * precio, v.Cantidad * 2, mayoreo: true)).ToList();
        return (tickets, lineas);
    }

    private static Variante VarianteDe(ResultadoImportacion r, string codigo)
    {
        var presentacion = r.Codigos.Single(c => c.Codigo == codigo).PresentacionId;
        return r.Variantes.Single(v => v.Id == r.Presentaciones.Single(p => p.Id == presentacion).VarianteId);
    }

    [Fact]
    public void Cada_producto_queda_con_una_variante_su_codigo_y_su_precio()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "7501035102446", "Cinta Adh Trans Janel", precio: 15)]));

        var producto = Assert.Single(r.Productos);
        Assert.Equal("Cinta Adh Trans Janel", producto.Nombre);
        Assert.Equal("papeleria", r.Departamentos.Single(d => d.Id == producto.DepartamentoId).Nombre);
        var variante = Assert.Single(r.Variantes);
        Assert.Equal(UnidadBase.Pieza, variante.UnidadBase);
        Assert.Equal(OrigenCodigo.Fabrica, Assert.Single(r.Codigos).Origen);
        Assert.Equal(1500, Assert.Single(r.Precios).PrecioCentavos);
    }

    [Fact]
    public void Los_codigos_cortos_son_internos()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "IMP", "Impresiones")]));

        Assert.Equal(OrigenCodigo.Interno, Assert.Single(r.Codigos).Origen);
    }

    [Fact]
    public void Los_EAN_13_que_empiezan_con_2_son_internos_como_en_el_catalogo()
    {
        // El rango de uso interno de las tiendas: 2000000000015 es el primer código interno que da el POS.
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "2000000000015", "Separadores de colores")]));

        Assert.Equal(OrigenCodigo.Interno, Assert.Single(r.Codigos).Origen);
    }

    [Theory]
    [InlineData('D', 3, "Contac Autoadh Trans", UnidadBase.Metro)]
    [InlineData('D', 4, "Liston 2.0 V/Colores X Metro", UnidadBase.Metro)]
    [InlineData('D', 4, "Papel Kraft X mt", UnidadBase.Metro)]
    // Con la medida KG (desactivada en la tienda, sin báscula): pieza a granel, y su unidad se decide en la depuración (revisión del
    // PR #139; antes, kilo).
    [InlineData('D', 4, "Hoja Albanene Carta", UnidadBase.PiezaAGranel)]
    [InlineData('D', 5, "Alcohol", UnidadBase.Litro)]
    [InlineData('U', 1, "Goma M40 Pelikan", UnidadBase.Pieza)]
    // A granel con la medida PIEZA (las impresiones): fracciones sin «kg» (2026-10-24; antes, kilos y el aviso de KG).
    [InlineData('D', 1, "Impresiones", UnidadBase.PiezaAGranel)]
    public void La_unidad_sale_del_tipo_de_venta_y_la_medida(char tipo, int medida, string descripcion, UnidadBase esperada)
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "A1", descripcion, tipo, medida)]));

        Assert.Equal(esperada, Assert.Single(r.Variantes).UnidadBase);
        // El aviso de la medida KG, solo con esa medida (si el nombre dice metro, es metro y no hay nada que decidir).
        Assert.Equal(tipo == 'D' && medida == 4 && esperada == UnidadBase.PiezaAGranel, r.Avisos.ContainsKey(ReglasEleventa.AvisoMedidaKg));
        // La presentación base se llama como su unidad; la de la pieza a granel, «Pieza» (así se ve al recibir una compra).
        Assert.Equal(esperada == UnidadBase.PiezaAGranel ? "Pieza" : esperada.ToString(), Assert.Single(r.Presentaciones).Nombre);
    }

    /// <summary>
    /// La pieza a granel de eleventa (2026-10-24): las impresiones (TVENTA 'D' con la medida PIEZA) se venden con fracciones y el
    /// ticket las dice sin unidad («0.2»), no «0.2 kg».
    /// </summary>
    [Fact]
    public void Las_impresiones_de_eleventa_se_venden_con_fracciones_y_el_ticket_no_dice_kg()
    {
        var avisos = new List<string>();
        var unidad = ReglasEleventa.Unidad(Producto(1, "IMP", "Impresiones", 'D', 1), conFracciones: false, avisos.Add);
        var impresiones = new ProductoEnVenta(Guid.NewGuid(), "Impresiones", Impuesto.Iva16, unidad, true);
        var venta = new PapeleriaPos.Nucleo.Ventas.Venta { Serie = "A" };
        var linea = venta.AgregarProducto(impresiones, new Presentacion { VarianteId = impresiones.VarianteId, Nombre = "Pieza", EsBase = true },
            Cantidad.Unidades(0.2m), [new Precio { VarianteId = impresiones.VarianteId, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = 1000 }]);

        Assert.Equal((UnidadBase.PiezaAGranel, true), (unidad, unidad.AdmiteFracciones()));
        Assert.Empty(avisos);
        Assert.Equal(("0.2", 200L), (ArmadoTicket.CantidadTexto(linea, esBase: true), linea.ImporteCentavos));
        Assert.Equal("piezas a granel", ProductosEnExcel.Unidad(unidad));
    }

    [Fact]
    public void Un_producto_por_pieza_vendido_con_fracciones_se_importa_a_granel()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "MICA", "Mica Termica Por Partes")],
            tickets: [new(10, 1, Vendido, 20, false)],
            lineas: [Linea(10, "MICA", 0.5, 40, 20, 5)]));

        Assert.True(Assert.Single(r.Variantes).UnidadBase.AdmiteFracciones());
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoFraccionEnPieza]);
    }

    [Fact]
    public void Las_ñ_y_acentos_se_conservan()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "A1", "Diccionario Lengua Española Básico")]));

        Assert.Equal("Diccionario Lengua Española Básico", Assert.Single(r.Productos).Nombre);
    }

    [Fact]
    public void Las_lineas_viejas_sin_total_usan_precio_por_cantidad_y_el_costo_sale_de_la_ganancia_por_pieza()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "N10", "Nylon C10 X Metro", 'D', 3), Producto(2, "IMP", "Impresiones", 'D', 1)],
            tickets: [new(10, 7, Vendido, 128, false)],
            lineas: [Linea(10, "N10", 2, 60, 120, 20.04), Linea(10, "IMP", 0.8, 10, null, 8)]));

        var venta = Assert.Single(r.Ventas);
        Assert.Equal("7", venta.FolioOrigen);
        Assert.Equal(12800, venta.TotalCentavos);
        Assert.Equal(HoraMexico.AUtc(Vendido), venta.FechaUtc);
        // eleventa guarda la ganancia de cada pieza: 2 metros que ganan $20.04 cada uno cuestan $79.92.
        var nylon = venta.Lineas.Single(l => l.Cantidad == Cantidad.Unidades(2));
        Assert.Equal((12000L, 7992L, 6000L), (nylon.ImporteCentavos, nylon.CostoCentavos, nylon.PrecioCentavos));
        // 0.8 a $10 que gana $8: cada impresión cuesta $0.20.
        var impresiones = venta.Lineas.Single(l => l.Cantidad == Cantidad.Unidades(0.8m));
        Assert.Equal((800L, 160L), (impresiones.ImporteCentavos, impresiones.CostoCentavos));
    }

    /// <summary>
    /// Las líneas viejas de la tienda (todas las de antes del 2024-10-05) no traen TOTAL_ARTICULO ni PRECIO_FINAL: su precio está
    /// en PRECIO_USADO. Antes entraban en $0, con el costo en negativo, y el ticket «sin desglose» (2026-10-24).
    /// </summary>
    [Fact]
    public void Las_lineas_viejas_sin_precio_final_toman_su_precio_usado()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "A1", "Goma")], tickets: [new(10, 7, Vendido, 30, false)],
            lineas: [new FilaLinea(10, "A1", "Goma", 3, TotalArticulo: null, PrecioFinal: null, Ganancia: 4, 2, false, PrecioUsado: 10)]));

        var linea = Assert.Single(Assert.Single(r.Ventas).Lineas);
        Assert.Equal((3000L, 1000L, 1800L), (linea.ImporteCentavos, linea.PrecioCentavos, linea.CostoCentavos));
        Assert.DoesNotContain(ImportadorEleventa.AvisoTotalSinDesglose, r.Avisos.Keys);
    }

    /// <summary>
    /// Los artículos comunes de eleventa (un código COMUN-… por cada uno) van todos a un solo producto inactivo, en el departamento
    /// de los comunes de eleventa; lo que no tiene departamento, al «- Sin Departamento -» de eleventa, sin crear otro (2026-10-24:
    /// antes, un producto por cada común y otro «Sin departamento»).
    /// </summary>
    [Fact]
    public void Los_comunes_van_a_un_solo_producto_y_lo_sin_departamento_al_de_eleventa()
    {
        var r = ImportadorEleventa.Importar(new DatosEleventa
        {
            Departamentos = [new(1, "- Sin Departamento -", true), new(2, "Papelería", true), new(9, EnvioEleventa.DepartamentoComunes, true)],
            Productos = [Producto(1, "A1", "Goma"), Producto(2, "B2", "Regla", departamento: null)],
            Tickets = [new(10, 1, Vendido, 30, false), new(11, 2, Vendido.AddMinutes(5), 12, false)],
            Lineas = [Linea(10, "COMUN-4821-1", 1, 15, 15, 15, "Recuerdo") with { DepartamentoId = 9 }, Linea(10, "A1", 1, 15, 15, 5),
                Linea(11, "COMUN-77-2", 1, 12, 12, 12, "Tarjeta") with { DepartamentoId = 9 }],
        });

        var comunes = r.Ventas.SelectMany(v => v.Lineas).Where(l => l.EsArticuloComun).Select(l => l.VarianteId).Distinct().ToList();
        var producto = r.Productos.Single(p => p.Id == r.Variantes.Single(v => v.Id == Assert.Single(comunes)).ProductoId);
        Assert.Equal((ImportadorEleventa.ArticuloComun, false, EnvioEleventa.DepartamentoComunes),
            (producto.Nombre, producto.Activo, r.Departamentos.Single(d => d.Id == producto.DepartamentoId).Nombre));
        // La regla sin departamento, en el de eleventa; no hay otro.
        var regla = r.Productos.Single(p => p.Nombre == "Regla");
        Assert.Equal("- Sin Departamento -", r.Departamentos.Single(d => d.Id == regla.DepartamentoId).Nombre);
        Assert.Equal(3, r.Departamentos.Count);
        Assert.DoesNotContain(ImportadorEleventa.AvisoCodigoInexistente, r.Avisos.Keys);
    }

    /// <summary>
    /// La existencia es FLOAT en eleventa: −0.0000012 de algo a granel es cero (no se avisa como negativa), como en la lectura
    /// (2026-10-24). La negativa de verdad sí se avisa.
    /// </summary>
    [Fact]
    public void Un_cero_con_ruido_del_float_no_es_existencia_negativa()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "LIS", "Listón X Metro", 'D', 3), Producto(2, "GOM", "Goma")],
            existencias: [new(1, -1.19e-06), new(2, -2)]));

        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoExistenciaNegativa]);
        Assert.Empty(r.Movimientos);
    }

    [Fact]
    public void Tickets_cancelados_se_marcan_y_los_abiertos_no_se_importan()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Goma")],
            tickets: [new(10, 1, Vendido, 15, true), new(11, null, null, 0, false)],
            lineas: [Linea(10, "A1", 1, 15, 15, 5)]));

        Assert.True(Assert.Single(r.Ventas).Cancelada);
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoTicketAbierto]);
    }

    [Fact]
    public void Lo_vendido_con_un_codigo_que_ya_no_existe_queda_como_producto_inactivo()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Goma")],
            tickets: [new(10, 1, Vendido, 30, false), new(11, 2, Vendido, 15, false)],
            lineas: [Linea(10, "VIEJO", 2, 15, 30, 10, "Producto que se borró"), Linea(11, "viejo", 1, 15, 15, 5, "Producto que se borró")]));

        var inactivo = Assert.Single(r.Productos, p => !p.Activo);
        Assert.Equal("Producto que se borró", inactivo.Nombre);
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoCodigoInexistente]);
        Assert.Equal(2, r.Ventas.SelectMany(v => v.Lineas).Count(l => l.VarianteId == VarianteDe(r, "VIEJO").Id));
    }

    [Fact]
    public void Las_existencias_entran_como_inventario_inicial_al_costo_de_eleventa()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Goma", costo: 2.7), Producto(2, "LISTON", "Liston X Metro", 'D', 3, costo: 1.5), Producto(3, "NEG", "Negativo")],
            existencias: [new(1, 75), new(2, 12.5), new(3, -2)],
            tickets: [new(10, 1, Vendido, 15, false)],
            lineas: [Linea(10, "A1", 1, 15, 15, 5)]));

        Assert.Equal(2, r.Movimientos.Count);
        Assert.All(r.Movimientos, m => Assert.Equal(TipoMovimiento.InventarioInicial, m.Tipo));
        Assert.Equal(20250L, r.Movimientos.Single(m => m.VarianteId == VarianteDe(r, "A1").Id).CostoCentavos);
        Assert.Equal(Cantidad.Unidades(12.5m), r.Movimientos.Single(m => m.VarianteId == VarianteDe(r, "LISTON").Id).Cantidad);
        Assert.All(r.Movimientos, m => Assert.True(m.FechaUtc > HoraMexico.AUtc(Vendido)));
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoExistenciaNegativa]);
    }

    [Fact]
    public void El_mayoreo_de_F11_pasa_a_escala_desde_el_paquete_que_mas_se_vendio()
    {
        // Hojas a $0.50 y $0.40 de mayoreo: con F11 se vendían paquetes de 100 (y uno de 200).
        var (tickets, lineas) = ConF11("HOJA", 0.4, (100, 3), (100, 4), (200, 5), (100, 10), (3, 8));
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "HOJA", "Hoja blanca", costo: 0.2, precio: 0.5, mayoreo: 0.4), Producto(2, "B2", "Lápiz", precio: 6)],
            tickets: tickets, lineas: lineas, promociones: [new("B2", 12, 5), new("B2", 12, 5)]));

        var escala = r.Precios.Single(p => p.VarianteId == VarianteDe(r, "HOJA").Id && p.CantidadMinima == Cantidad.Piezas(100));
        Assert.Equal(40, escala.PrecioCentavos);
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoMayoreoDeducido]);
        // La promoción repetida entra una vez (repetida tumbaría el guardado).
        Assert.Single(r.Precios, p => p.VarianteId == VarianteDe(r, "B2").Id && p.CantidadMinima == Cantidad.Piezas(12));
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoPromocionRepetida]);
        // Lo deducido queda anotado y resuelto: una decisión de la depuración lo cambia al repetirse.
        var deducido = Assert.Single(r.MayoreoPorRevisar);
        Assert.Equal((DeduccionMayoreo.Deducido, Cantidad.Piezas(100), true), (deducido.Motivo, deducido.Escala, deducido.ResueltoUtc is not null));
    }

    [Fact]
    public void Una_promocion_que_se_redondea_a_una_pieza_no_choca_con_el_menudeo()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "B1", "Goma", precio: 10)], promociones: [new("B1", 1.0004, 9)]));

        Assert.Equal(1000L, Assert.Single(r.Precios).PrecioCentavos);
    }

    [Fact]
    public void Con_un_codigo_repetido_el_mayoreo_va_a_revisar_porque_sus_ventas_no_se_separan()
    {
        var (tickets, lineas) = ConF11("A1", 8, (12, 3), (12, 4), (12, 5), (12, 6));
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Lápiz viejo", costo: 5, precio: 10), Producto(2, "A1", "Lápiz nuevo", costo: 5, precio: 10, mayoreo: 8)],
            tickets: tickets, lineas: lineas));

        var nuevo = r.Mapeos.Single(m => m.Entidad == nameof(Variante) && m.IdOrigen == "2").IdNuevo;
        var revisar = Assert.Single(r.MayoreoPorRevisar);
        Assert.Equal((nuevo, ImportadorEleventa.MotivoCodigoRepetido, (Cantidad?)null, (DateTime?)null),
            (revisar.VarianteId, revisar.Motivo, revisar.Sugerida, revisar.ResueltoUtc));
        Assert.DoesNotContain(r.Precios, p => p.CantidadMinima > Cantidad.Piezas(1));
    }

    [Fact]
    public void No_se_sugiere_una_escala_que_choca_con_las_promociones()
    {
        // Choca: desde 3 ya sale a $5, y el mayoreo de $6 desde 12 haría subir el precio. Pocas: desde 12 ya hay promoción.
        var (t1, l1) = ConF11("CHOCA", 6, (12, 3), (12, 4), (12, 5));
        var (t2, l2) = ConF11("POCAS", 6, (12, 6), (12, 7));
        t2 = t2.Select(t => t with { Id = t.Id + 100 }).ToList();
        l2 = l2.Select(l => l with { TicketId = l.TicketId + 100 }).ToList();
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "CHOCA", "Pluma", costo: 3, precio: 7, mayoreo: 6), Producto(2, "POCAS", "Lápiz", costo: 3, precio: 7, mayoreo: 6)],
            tickets: [.. t1, .. t2], lineas: [.. l1, .. l2], promociones: [new("CHOCA", 3, 5), new("POCAS", 12, 5)]));

        var choca = r.MayoreoPorRevisar.Single(m => m.VarianteId == VarianteDe(r, "CHOCA").Id);
        var pocas = r.MayoreoPorRevisar.Single(m => m.VarianteId == VarianteDe(r, "POCAS").Id);
        Assert.Equal((ImportadorEleventa.MotivoChoca, (Cantidad?)null), (choca.Motivo, choca.Sugerida));
        Assert.Equal((DeduccionMayoreo.PocasVentas, (Cantidad?)null), (pocas.Motivo, pocas.Sugerida));
    }

    [Fact]
    public void Lo_que_no_se_puede_deducir_queda_por_revisar_con_lo_que_se_sabe()
    {
        var (tickets, lineas) = ConF11("POCAS", 18, (12, 3), (12, 4));
        var r = ImportadorEleventa.Importar(Datos(
            [
                Producto(1, "POCAS", "Pegamento 35g", costo: 12, precio: 22, mayoreo: 18),
                Producto(2, "COSTO", "Mochila", costo: 300, precio: 400, mayoreo: 300),
                Producto(3, "LISTON", "Listón", 'D', 3, costo: 2, precio: 5, mayoreo: 4),
                Producto(4, "IGUAL", "Clip", costo: 0.1, precio: 0.3, mayoreo: 0.3),
            ],
            tickets: tickets, lineas: lineas));

        var pocas = r.MayoreoPorRevisar.Single(m => m.VarianteId == VarianteDe(r, "POCAS").Id);
        Assert.Equal((DeduccionMayoreo.PocasVentas, 2, "12 ×2", 1800L), (pocas.Motivo, pocas.VentasConMayoreo, pocas.Cantidades, pocas.PrecioMayoreoCentavos));
        Assert.Equal(Cantidad.Piezas(12), pocas.Sugerida);
        Assert.Equal(ImportadorEleventa.MotivoAlCosto, r.MayoreoPorRevisar.Single(m => m.VarianteId == VarianteDe(r, "COSTO").Id).Motivo);
        Assert.Equal(ImportadorEleventa.MotivoGranel, r.MayoreoPorRevisar.Single(m => m.VarianteId == VarianteDe(r, "LISTON").Id).Motivo);
        // Mayoreo igual al menudeo: no hay mayoreo que revisar.
        Assert.DoesNotContain(r.MayoreoPorRevisar, m => m.VarianteId == VarianteDe(r, "IGUAL").Id);
        Assert.Equal(3, r.Avisos[ImportadorEleventa.AvisoMayoreoPorRevisar]);
        Assert.Single(r.Precios, p => p.VarianteId == VarianteDe(r, "POCAS").Id);
    }

    [Fact]
    public void Si_el_paquete_es_el_de_una_promocion_se_queda_la_promocion()
    {
        var (tickets, lineas) = ConF11("B2", 5, (12, 3), (12, 4), (12, 5));
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "B2", "Lápiz", costo: 3, precio: 6, mayoreo: 5.5)], tickets: tickets, lineas: lineas,
            promociones: [new("B2", 12, 5)]));

        Assert.Equal(500L, r.Precios.Single(p => p.CantidadMinima == Cantidad.Piezas(12)).PrecioCentavos);
        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoMayoreoYaEstaba]);
        // Queda anotado, ya resuelto y sin escala propia: la lectura del piloto sabe qué mayoreo tenía y no lo toma por nuevo.
        var anotado = Assert.Single(r.MayoreoPorRevisar);
        Assert.Equal((550L, true, (Cantidad?)null), (anotado.PrecioMayoreoCentavos, anotado.ResueltoUtc is not null, anotado.Escala));
    }

    /// <summary>
    /// El mínimo y el máximo de «Productos bajos en inventario» (la tienda los usa: base del 2026-10-24, casi siempre iguales):
    /// cada variante con los de su producto, en su unidad y con la conversión de la existencia; 0 es sin mínimo. Lo que la
    /// ficha no aceptaría no se toma y se avisa; un máximo menor que el mínimo se deja fuera.
    /// </summary>
    [Fact]
    public void El_minimo_y_el_maximo_de_eleventa_llegan_a_cada_variante_en_su_unidad()
    {
        var r = ImportadorEleventa.Importar(Datos(
        [
            Producto(1, "PLU", "Pluma") with { InventarioMinimo = 10, InventarioMaximo = 10 },
            Producto(2, "GOM", "Goma") with { InventarioMinimo = 0, InventarioMaximo = 0 },
            Producto(3, "LIS", "Listón X Metro", 'D', 3) with { InventarioMinimo = 2.5, InventarioMaximo = 2.5 },
            Producto(4, "SAC", "Sacapuntas") with { InventarioMinimo = 5, InventarioMaximo = 0 },
            Producto(5, "COR", "Corrector") with { InventarioMinimo = 0, InventarioMaximo = 12 },
            Producto(6, "CUA", "Cuaderno") with { InventarioMinimo = 1.5, InventarioMaximo = 1.5 },
            Producto(7, "BOR", "Borrador") with { InventarioMinimo = 8, InventarioMaximo = 3 },
            Producto(8, "CLI", "Clip") with { InventarioMinimo = 2_000_000, InventarioMaximo = 2_000_000 },
            Producto(9, "FOL", "Folder", eliminado: true) with { InventarioMinimo = 4, InventarioMaximo = 4 },
            Producto(10, "NEG", "Regla") with { InventarioMinimo = -3, InventarioMaximo = -3 },
        ], existencias: [new(1, 4), new(3, 7.5)]));

        // «mínimo/máximo», en su unidad; «—», sin.
        string De(string codigo) => $"{VarianteDe(r, codigo).InventarioMinimo?.ToString() ?? "—"}/{VarianteDe(r, codigo).InventarioMaximo?.ToString() ?? "—"}";
        Assert.Equal("10/10", De("PLU"));
        Assert.Equal("—/—", De("GOM"));
        Assert.Equal(("2.5/2.5", UnidadBase.Metro), (De("LIS"), VarianteDe(r, "LIS").UnidadBase));
        Assert.Equal("5/—", De("SAC"));
        Assert.Equal("—/12", De("COR"));
        // Media pieza no se puede pedir: no se toma (se avisa), como un máximo de dos millones.
        Assert.Equal("—/—", De("CUA"));
        Assert.Equal("—/—", De("CLI"));
        // El máximo menor que el mínimo se deja fuera: queda el mínimo.
        Assert.Equal("8/—", De("BOR"));
        // Lo borrado en eleventa entra inactivo, con lo suyo.
        Assert.Equal("4/4", De("FOL"));
        // Lo negativo tampoco es un mínimo, como el cero.
        Assert.Equal("—/—", De("NEG"));
        Assert.Equal(1, r.Avisos[ReglasEleventa.AvisoMinimoConFracciones]);
        Assert.Equal(1, r.Avisos[ReglasEleventa.AvisoMinimoDemasiadoGrande]);
        Assert.Equal(1, r.Avisos[ReglasEleventa.AvisoMaximoDebajoDelMinimo]);
        // Todo lo que llegó lo aceptaría la ficha (F3), también el mínimo igual al máximo.
        Assert.All(r.Variantes, v => Variante.RevisarMinimoYMaximo(v.UnidadBase, v.InventarioMinimo, v.InventarioMaximo, "x"));
    }

    [Fact]
    public async Task El_minimo_y_el_maximo_se_guardan_con_la_importacion()
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "PLU", "Pluma") with { InventarioMinimo = 10, InventarioMaximo = 10 }, Producto(2, "GOM", "Goma")]));
        await using var baseEnMemoria = await BaseEnMemoria.CrearAsync();

        await using (var db = baseEnMemoria.Contexto())
            await GuardadoLote.GuardarAsync(r, db);

        await using var lectura = baseEnMemoria.Contexto();
        var guardadas = await lectura.Variantes.ToDictionaryAsync(v => v.Id);
        var (pluma, goma) = (guardadas[VarianteDe(r, "PLU").Id], guardadas[VarianteDe(r, "GOM").Id]);
        Assert.Equal(((long?)10_000, (long?)10_000), (pluma.InventarioMinimo?.Milesimas, pluma.InventarioMaximo?.Milesimas));
        Assert.Equal(((long?)null, (long?)null), (goma.InventarioMinimo?.Milesimas, goma.InventarioMaximo?.Milesimas));
    }

    [Fact]
    public async Task El_mayoreo_por_revisar_se_guarda_con_la_importacion()
    {
        var (tickets, lineas) = ConF11("POCAS", 18, (12, 3));
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "POCAS", "Pegamento 35g", costo: 12, precio: 22, mayoreo: 18)], tickets: tickets, lineas: lineas));
        await using var baseEnMemoria = await BaseEnMemoria.CrearAsync();

        await using (var db = baseEnMemoria.Contexto())
            await GuardadoLote.GuardarAsync(r, db);

        await using var lectura = baseEnMemoria.Contexto();
        var guardado = await lectura.MayoreoPorRevisar.SingleAsync();
        Assert.Equal((VarianteDe(r, "POCAS").Id, 1800L), (guardado.VarianteId, guardado.PrecioMayoreoCentavos));
    }

    [Fact]
    public async Task Se_guarda_y_el_diagnostico_corre_sobre_lo_importado()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Goma", costo: 2.7), Producto(2, "B2", "Goma", costo: 2.7), Producto(3, "IMP", "Impresiones", 'D', 1, costo: 0, departamento: 13)],
            existencias: [new(1, 10), new(2, 5)],
            tickets: [new(10, 1, Vendido, 23, false)],
            lineas: [Linea(10, "A1", 1, 15, 15, 5), Linea(10, "IMP", 0.8, 10, null, 8)]));
        await using var baseEnMemoria = await BaseEnMemoria.CrearAsync();

        await using (var db = baseEnMemoria.Contexto())
            await GuardadoLote.GuardarAsync(r, db);

        await using var lectura = baseEnMemoria.Contexto();
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
        Assert.Equal(r.Mapeos.Count, await lectura.MapeosImportacion.CountAsync());
        var analisis = new Analisis(await DatosDiagnostico.CargarAsync(lectura));
        Assert.Equal(2, analisis.RevisarCatalogo().Count(f => f.Problema == Problemas.Duplicado));
        Assert.Equal(23m, analisis.MargenPorDepartamento().Sum(d => d.Venta));
    }
}
