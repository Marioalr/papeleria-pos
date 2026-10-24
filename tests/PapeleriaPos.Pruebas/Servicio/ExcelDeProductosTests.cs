using ClosedXML.Excel;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Cómo lee la caja principal el Excel de los productos que se importa (Productos › Productos en Excel), con archivos hechos a
/// mano: el renglón de los títulos donde esté (el encabezado de arriba no cuenta), los títulos como se escriban, lo que no se
/// usa, las escalas que traen sus dos columnas, los renglones vacíos, las fórmulas (con lo que calculó Excel o sin ello) y lo que
/// no es un Excel.
/// </summary>
public sealed class ExcelDeProductosTests
{
    private static byte[] Bytes(XLWorkbook libro, bool calcular = false)
    {
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo, new SaveOptions { EvaluateFormulasBeforeSaving = calcular });
        return flujo.ToArray();
    }

    [Fact]
    public void Busca_los_titulos_como_se_escriban_y_dice_lo_que_no_se_usa()
    {
        using var libro = new XLWorkbook();
        var hoja = libro.AddWorksheet("Mis productos");
        hoja.Cell(1, 1).Value = "Papelería de prueba";
        hoja.Cell(2, 1).Value = "Lo que cambié";
        hoja.Cell(4, 2).Value = "CODIGO";
        hoja.Cell(4, 3).Value = " precio ";
        hoja.Cell(4, 4).Value = "Notas";
        hoja.Cell(4, 5).Value = "Mayoreo 1: desde";
        hoja.Cell(4, 6).Value = "Mayoreo 1: precio";
        hoja.Cell(4, 7).Value = "Mayoreo 2: desde";
        hoja.Cell(4, 8).Value = "Precio";
        hoja.Cell(5, 2).Value = "A1";
        hoja.Cell(5, 3).Value = 12.5;
        hoja.Cell(5, 5).Value = 12;
        hoja.Cell(5, 6).Value = 11;
        // Un renglón vacío no cuenta; uno con solo mayoreo, sí.
        hoja.Cell(7, 5).Value = 24;

        var leido = ExcelDeProductos.Leer(Bytes(libro), "Cambios.xlsx");

        Assert.Null(leido.Presentaciones);
        var productos = leido.Productos;
        Assert.Equal("Mis productos", productos.Nombre);
        Assert.Equal(new HashSet<CampoDeProducto> { CampoDeProducto.Codigo, CampoDeProducto.Precio }, productos.Columnas);
        Assert.Equal([1], productos.Mayoreo);
        Assert.Equal(["Notas", "Precio (repetida)", "Mayoreo 2: desde"], productos.SinUsar);
        Assert.Equal([5, 7], productos.Renglones.Select(r => r.Fila));
        var renglon = productos.Renglones[0];
        Assert.Equal(("A1", 12.5), (renglon[CampoDeProducto.Codigo].Texto, renglon[CampoDeProducto.Precio].Numero));
        Assert.Equal((12d, 11d), (renglon.Mayoreo[1].Desde.Numero, renglon.Mayoreo[1].Precio.Numero));
        Assert.True(renglon[CampoDeProducto.Producto].EsVacia);
        Assert.True(productos.Renglones[1].Mayoreo[1].Precio.EsVacia);
    }

    /// <summary>
    /// El mismo .xlsx sin lo que calcularon sus fórmulas (como lo deja un programa que no calcula): sin el valor de cada celda con
    /// fórmula, y sin su tipo.
    /// </summary>
    private static byte[] SinLoCalculado(byte[] datos)
    {
        using var flujo = new MemoryStream();
        flujo.Write(datos);
        using (var zip = new System.IO.Compression.ZipArchive(flujo, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var entrada in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)).ToList())
            {
                string xml;
                using (var lector = new StreamReader(entrada.Open()))
                    xml = lector.ReadToEnd();
                // Con o sin el prefijo del espacio de nombres («x:c», «x:f», «x:v»).
                xml = System.Text.RegularExpressions.Regex.Replace(xml,
                    @"(<(?:x:)?c [^>]*?) t=""(?:e|str|n|b)""([^>]*>)(<(?:x:)?f>[^<]*</(?:x:)?f>)<(?:x:)?v>[^<]*</(?:x:)?v>", "$1$2$3");
                xml = System.Text.RegularExpressions.Regex.Replace(xml, @"(<(?:x:)?f>[^<]*</(?:x:)?f>)<(?:x:)?v>[^<]*</(?:x:)?v>", "$1");
                var nombre = entrada.FullName;
                entrada.Delete();
                using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open());
                escritor.Write(xml);
            }
        }
        return flujo.ToArray();
    }

    [Fact]
    public void Una_formula_vale_lo_que_calculo_Excel_y_si_no_lo_trae_se_calcula()
    {
        using var libro = new XLWorkbook();
        var hoja = libro.AddWorksheet(ProductosEnExcel.HojaProductos);
        hoja.Cell(1, 1).Value = "Código";
        hoja.Cell(1, 2).Value = "Precio";
        foreach (var (fila, formula) in new[] { (2, "10*1.1"), (3, "WEBSERVICE(\"http://localhost/precio\")"), (4, "1/0") })
        {
            hoja.Cell(fila, 1).Value = $"F{fila}";
            hoja.Cell(fila, 2).FormulaA1 = formula;
        }
        hoja.Cell(5, 1).Value = "F5";
        hoja.Cell(5, 2).Value = XLError.NoValueAvailable;
        hoja.Cell(6, 1).Value = "F6";
        hoja.Cell(6, 2).Value = new DateTime(2026, 10, 22);

        // Sin lo que calculó Excel (un archivo de otro programa): se calcula aquí. Lo que solo sabe calcular Excel queda como error
        // (el mensaje pide guardarlo otra vez en Excel, LecturaDeCeldasTests).
        var guardado = Bytes(libro, calcular: true);
        var sinCalcular = ExcelDeProductos.Leer(SinLoCalculado(guardado), "Fórmulas.xlsx").Productos.Renglones.Select(r => r[CampoDeProducto.Precio]).ToList();
        Assert.Equal(11, sinCalcular[0].Numero, 6);
        Assert.Equal(TipoCelda.Error, sinCalcular[1].Tipo);
        // Un error que ya trae la celda (como lo guarda Excel): se dice cuál.
        Assert.Equal((TipoCelda.Error, "#N/A"), (sinCalcular[3].Tipo, sinCalcular[3].Texto));
        // Una fecha en un precio se lee como se ve: no es un número.
        Assert.Equal(TipoCelda.Texto, sinCalcular[4].Tipo);

        // Con lo que calculó al guardarlo: eso vale (sin volver a calcular).
        var calculado = ExcelDeProductos.Leer(guardado, "Fórmulas.xlsx").Productos.Renglones.Select(r => r[CampoDeProducto.Precio]).ToList();
        Assert.Equal(11, calculado[0].Numero, 6);
        Assert.Equal((TipoCelda.Error, "#DIV/0!"), (calculado[2].Tipo, calculado[2].Texto));
    }

    [Fact]
    public void Lee_la_hoja_de_las_presentaciones_si_la_trae()
    {
        using var libro = new XLWorkbook();
        var productos = libro.AddWorksheet("Productos");
        productos.Cell(1, 1).Value = "Código";
        var presentaciones = libro.AddWorksheet("presentaciones");
        presentaciones.Cell(3, 1).Value = "Código del producto";
        presentaciones.Cell(3, 2).Value = "Presentación";
        presentaciones.Cell(3, 3).Value = "Contiene";
        presentaciones.Cell(4, 1).Value = "A1";
        presentaciones.Cell(4, 2).Value = "Caja 12";
        presentaciones.Cell(4, 3).Value = 12;

        var leido = ExcelDeProductos.Leer(Bytes(libro), "Con cajas.xlsx");

        Assert.Empty(leido.Productos.Renglones);
        var renglon = Assert.Single(leido.Presentaciones!.Renglones);
        Assert.Equal(("A1", "Caja 12", 12d), (renglon[CampoDePresentacion.CodigoDelProducto].Texto, renglon[CampoDePresentacion.Presentacion].Texto,
            renglon[CampoDePresentacion.Contiene].Numero));
    }

    [Fact]
    public void Lo_que_no_es_un_Excel_se_dice_con_lo_que_hay_que_hacer()
    {
        var noEsExcel = Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer("hola"u8.ToArray(), "Productos.csv"));
        Assert.Equal("«Productos.csv» no es un libro de Excel (.xlsx): ábrelo en Excel y guárdalo como «Libro de Excel (*.xlsx)». Luego vuelve a importarlo.",
            noEsExcel.Message);
        Assert.Equal("«Vacío.xlsx» está vacío.", Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer([], "Vacío.xlsx")).Message);
        Assert.StartsWith("«Grande.xlsx» pesa 8.1 MB: se importan archivos de hasta 8 MB",
            Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(new byte[ProductosEnExcel.BytesMaximos + 100_000], "Grande.xlsx")).Message);
        // Un zip que no es un libro de Excel.
        using var zip = new MemoryStream();
        using (var archivo = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            archivo.CreateEntry("hola.txt");
        Assert.StartsWith("No se pudo abrir «Otro.xlsx» como libro de Excel",
            Assert.Throws<ArgumentException>(() => ExcelDeProductos.Leer(zip.ToArray(), "Otro.xlsx")).Message);
    }
}
