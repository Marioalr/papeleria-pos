using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
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

    [Theory]
    [InlineData('D', 3, "Contac Autoadh Trans", UnidadBase.Metro)]
    [InlineData('D', 4, "Liston 2.0 V/Colores X Metro", UnidadBase.Metro)]
    [InlineData('D', 4, "Papel Kraft X mt", UnidadBase.Metro)]
    [InlineData('D', 4, "Hoja Albanene Carta", UnidadBase.Kilogramo)]
    [InlineData('D', 5, "Alcohol", UnidadBase.Litro)]
    [InlineData('U', 1, "Goma M40 Pelikan", UnidadBase.Pieza)]
    public void La_unidad_sale_del_tipo_de_venta_y_la_medida(char tipo, int medida, string descripcion, UnidadBase esperada)
    {
        var r = ImportadorEleventa.Importar(Datos([Producto(1, "A1", descripcion, tipo, medida)]));

        Assert.Equal(esperada, Assert.Single(r.Variantes).UnidadBase);
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
    public void Las_lineas_viejas_sin_total_usan_precio_por_cantidad_y_el_costo_sale_de_la_ganancia()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "N10", "Nylon C10 X Metro", 'D', 3), Producto(2, "IMP", "Impresiones", 'D', 1)],
            tickets: [new(10, 7, Vendido, 128, false)],
            lineas: [Linea(10, "N10", 2, 60, 120, 20.04), Linea(10, "IMP", 0.8, 10, null, 8)]));

        var venta = Assert.Single(r.Ventas);
        Assert.Equal("7", venta.FolioOrigen);
        Assert.Equal(12800, venta.TotalCentavos);
        Assert.Equal(HoraMexico.AUtc(Vendido), venta.FechaUtc);
        var nylon = venta.Lineas.Single(l => l.Cantidad == Cantidad.Unidades(2));
        Assert.Equal((12000L, 9996L, 6000L), (nylon.ImporteCentavos, nylon.CostoCentavos, nylon.PrecioCentavos));
        var impresiones = venta.Lineas.Single(l => l.Cantidad == Cantidad.Unidades(0.8m));
        Assert.Equal((800L, 0L), (impresiones.ImporteCentavos, impresiones.CostoCentavos));
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
