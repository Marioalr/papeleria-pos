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

    private static FilaLinea Linea(int ticket, string codigo, double cantidad, double precio, double? total, double ganancia, string nombre = "x") =>
        new(ticket, codigo, nombre, cantidad, total, precio, ganancia, 2, false);

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
    public void El_mayoreo_manual_no_se_importa_pero_las_promociones_por_cantidad_si()
    {
        var r = ImportadorEleventa.Importar(Datos(
            [Producto(1, "A1", "Pegamento 35g", precio: 22, mayoreo: 20), Producto(2, "B2", "Lápiz", precio: 6)],
            promociones: [new("B2", 12, 5)]));

        Assert.Equal(1, r.Avisos[ImportadorEleventa.AvisoMayoreo]);
        var escala = r.Precios.Single(p => p.CantidadMinima == Cantidad.Piezas(12));
        Assert.Equal((VarianteDe(r, "B2").Id, 500L), (escala.VarianteId, escala.PrecioCentavos));
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
