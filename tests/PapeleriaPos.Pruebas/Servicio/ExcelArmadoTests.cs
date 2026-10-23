using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los Excel armados directo, sin el servicio (la revisión del PR #134): las cantidades enteras sin el punto al final, lo que
/// parece fórmula o trae caracteres raros escrito como texto (y el archivo, válido), «Por día» del primer al último día con algo,
/// la columna que suma lo vendido en los tickets y lo que dicen las notas de una caja.
/// </summary>
public sealed class ExcelArmadoTests
{
    private static QuienExporta Quien => new("Matriz", "Luis", new DateTime(2026, 10, 23, 12, 0, 0));

    private static BajoInventarioDto Bajo(string nombre, string departamento = "Escritura", decimal hay = 5m, UnidadBase unidad = UnidadBase.Pieza) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, "750100", Guid.NewGuid(), departamento, unidad, hay, 10m, true, 12m, null, 0m, 7m, "Pieza");

    private static byte[] Bajos(params BajoInventarioDto[] productos) =>
        ExcelDelInventario.Bajos(new BajosInventarioDto(productos, productos.Length, productos.Length, 0, 0, Math.Max(productos.Length, 1)), [], Quien);

    private static void Valido(byte[] datos)
    {
        using var documento = SpreadsheetDocument.Open(new MemoryStream(datos), false);
        var errores = new OpenXmlValidator().Validate(documento).Take(5).Select(e => $"{e.Description} ({e.Path?.XPath})").ToList();
        Assert.True(errores.Count == 0, string.Join('\n', errores));
    }

    [Fact]
    public void Una_cantidad_entera_se_lee_sin_el_punto_y_una_con_fraccion_con_sus_decimales()
    {
        var datos = Bajos(Bajo("Pluma", hay: 5m), Bajo("Cable", hay: 1.5m, unidad: UnidadBase.Metro), Bajo("Hojas", hay: 2500m));

        using var libro = new XLWorkbook(new MemoryStream(datos));
        var renglones = libro.Worksheet(ExcelDelInventario.HojaBajos).Tables.Single().DataRange.Rows().ToList();
        var hay = renglones.Select(r => r.Cells().First(c => c.Address.ColumnNumber == 4)).ToList();
        // Antes «#,##0.###» dejaba «5.».
        Assert.Equal(["5", "1.5", "2,500"], hay.Select(c => c.GetFormattedString(CultureInfo.InvariantCulture)));
        Assert.Equal(["#,##0", LibroExcel.CantidadConFraccion, "#,##0"], hay.Select(c => c.Style.NumberFormat.Format));
        Valido(datos);
    }

    [Fact]
    public void Lo_que_parece_formula_se_escribe_como_texto()
    {
        string[] nombres = ["=1+1", "+SUM(1,2)", "-2+3", "@SUM(1,1)", "=HYPERLINK(\"http://ejemplo.invalid\",\"clic\")", "=cmd|' /C calc'!A0"];
        var datos = Bajos(nombres.Select(n => Bajo(n, departamento: n)).ToArray());

        using var libro = new XLWorkbook(new MemoryStream(datos));
        var hoja = libro.Worksheet(ExcelDelInventario.HojaBajos);
        foreach (var nombre in nombres)
        {
            var celdas = hoja.CellsUsed().Where(c => c.Value.IsText && c.GetText() == nombre).ToList();
            Assert.Equal(2, celdas.Count);
            Assert.All(celdas, c => Assert.False(c.HasFormula));
        }
        // Ninguna hoja lleva una fórmula.
        using var zip = new ZipArchive(new MemoryStream(datos));
        foreach (var parte in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
        {
            using var lector = new StreamReader(parte.Open());
            Assert.DoesNotMatch(new Regex("<(\\w+:)?f[ >]"), lector.ReadToEnd());
        }
    }

    [Fact]
    public void Los_caracteres_raros_de_un_nombre_no_echan_a_perder_el_archivo()
    {
        foreach (var nombre in new[] { "Pluma\u001Bazul", "Pluma\u0001", "Pluma\u0000x", "Pluma\u0007x", "Pluma￾x", "Pluma\uD83Dx", "Pluma _x0041_ azul", "Pluma\tazul" })
        {
            var datos = Bajos(Bajo(nombre));
            Valido(datos);
            using var libro = new XLWorkbook(new MemoryStream(datos));
            Assert.StartsWith("Pluma", libro.Worksheet(ExcelDelInventario.HojaBajos).Tables.Single().DataRange.FirstRow().Cell(2).GetText());
        }
    }

    private static ResumenDeVentasDto Dia(DateOnly dia, long vendido) =>
        new(dia, 1, vendido, 0, 0, 0, 0, [new FormaPagoReporteDto(FormaPago.Efectivo, vendido)]);

    private static VentaDeUnDiaDto Ticket(string folio, DateTime cuando, long total, bool cancelada = false, long devuelto = 0) =>
        new(Guid.NewGuid(), folio, null, cuando, Guid.Empty, "Caja 1", Guid.Empty, "Ana", total, 1, [FormaPago.Efectivo], cancelada, devuelto, false, false);

    private static readonly Guid Caja1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Caja2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Del 1 de enero de 2000 a hoy, con algo solo el 19 y el 21 de octubre de 2026.</summary>
    private static VentasPorPeriodoDto DeAnios(Guid? caja = null)
    {
        var hoy = new DateOnly(2026, 10, 23);
        var dias = new List<ResumenDeVentasDto> { Dia(new DateOnly(2026, 10, 19), 4000), Dia(new DateOnly(2026, 10, 21), 2500) };
        var totales = new ResumenDeVentasDto(null, 2, 6500, 1, 1000, 1, 1200, [new FormaPagoReporteDto(FormaPago.Efectivo, 6500)]);
        var tickets = new List<VentaDeUnDiaDto>
        {
            Ticket("A-000001", new DateTime(2026, 10, 19, 10, 0, 0), 4000, devuelto: 1000),
            Ticket("A-000002", new DateTime(2026, 10, 21, 11, 0, 0), 1200, cancelada: true),
            Ticket("A-000003", new DateTime(2026, 10, 21, 12, 0, 0), 2500),
        };
        return new VentasPorPeriodoDto(new DateOnly(2000, 1, 1), hoy, hoy, caja, [new OpcionFiltroDto(Caja1, "Caja 1"), new OpcionFiltroDto(Caja2, "Caja 2")],
            tickets, tickets.Count, 0, tickets.Count, totales, dias, 0, 0, false);
    }

    [Fact]
    public void Por_dia_va_del_primer_al_ultimo_dia_con_algo()
    {
        using var libro = new XLWorkbook(new MemoryStream(ExcelDeVentasPorPeriodo.Armar(DeAnios(), Quien)));

        // No un renglón por cada día desde el año 2000: del 19 al 21, también el 20 (sin nada).
        var dias = libro.Worksheet(ExcelDeVentasPorPeriodo.HojaPorDia).Tables.Single().DataRange.Rows().Select(r => r.Cell(1).GetDateTime()).ToList();
        Assert.Equal([new DateTime(2026, 10, 19), new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)], dias);

        // Sin nada en esos días, lo dice y no hay renglón de total.
        var vacio = DeAnios() with { Dias = [], Ventas = [], Total = 0, Totales = new ResumenDeVentasDto(null, 0, 0, 0, 0, 0, 0, []) };
        using var sinNada = new XLWorkbook(new MemoryStream(ExcelDeVentasPorPeriodo.Armar(vacio, Quien)));
        var hoja = sinNada.Worksheet(ExcelDeVentasPorPeriodo.HojaPorDia);
        Assert.Empty(hoja.Tables);
        Assert.Contains(hoja.CellsUsed(), c => c.GetString() == "No hubo nada en esos días.");
        Assert.DoesNotContain(hoja.CellsUsed(), c => c.GetString() == "Total");
    }

    [Fact]
    public void La_columna_cuenta_en_lo_vendido_suma_lo_vendido_y_lo_devuelto_dice_que_es_de_cualquier_dia()
    {
        using var libro = new XLWorkbook(new MemoryStream(ExcelDeVentasPorPeriodo.Armar(DeAnios(), Quien)));
        var hoja = libro.Worksheet(ExcelDeVentasPorPeriodo.HojaTickets);
        var tabla = hoja.Tables.Single();
        var titulos = tabla.Fields.Select(f => f.Name).ToList();
        var cuenta = titulos.IndexOf(ExcelDeVentasPorPeriodo.CuentaEnLoVendido) + 1;

        // El cancelado, en cero: la autosuma da lo vendido de abajo.
        var valores = tabla.DataRange.Rows().Select(r => (decimal)r.Cell(cuenta).GetDouble()).ToList();
        Assert.Equal([40m, 0m, 25m], valores);
        var vendido = hoja.CellsUsed().Single(c => c.GetString() == "Vendido" && c.Address.RowNumber > tabla.RangeAddress.LastAddress.RowNumber);
        Assert.Equal(65m, (decimal)vendido.CellRight().GetDouble());
        Assert.Equal(cuenta, vendido.CellRight().Address.ColumnNumber);
        // Lo devuelto de cada ticket es de cuando sea; lo de abajo, lo devuelto esos días: lo dicen el título y la nota.
        Assert.Contains(ExcelDeVentasPorPeriodo.DevueltoCualquierDia, titulos);
        Assert.Contains(hoja.CellsUsed(), c => c.GetString().StartsWith($"«{ExcelDeVentasPorPeriodo.CuentaEnLoVendido}» es el total", StringComparison.Ordinal));
    }

    [Fact]
    public void De_una_caja_las_notas_no_dicen_que_es_como_el_corte_del_dia()
    {
        var todas = DeAnios();
        var deUna = DeAnios(Caja2);

        using var libroDeTodas = new XLWorkbook(new MemoryStream(ExcelDeVentasPorPeriodo.Armar(todas, Quien)));
        using var libroDeUna = new XLWorkbook(new MemoryStream(ExcelDeVentasPorPeriodo.Armar(deUna, Quien)));

        var notasDeUna = string.Join('\n', libroDeUna.Worksheet(ExcelDeVentasPorPeriodo.HojaTickets).CellsUsed().Select(c => c.GetString()));
        Assert.Contains("De la caja: Caja 2.", notasDeUna);
        Assert.Contains("son los de los tickets de Caja 2 que se hicieron esos días, en esa caja o en otra", notasDeUna);
        Assert.DoesNotContain("corte del día", notasDeUna);
        Assert.Contains(libroDeTodas.Worksheet(ExcelDeVentasPorPeriodo.HojaTickets).CellsUsed(), c => c.GetString().Contains("como en el corte del día"));
    }
}
