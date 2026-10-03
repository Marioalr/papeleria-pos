using ClosedXML.Excel;

namespace PapeleriaPos.Herramientas.Diagnostico;

/// <summary>El diagnóstico en Excel: una hoja por análisis, cada una con su explicación y una tabla filtrable.</summary>
public static class LibroDiagnostico
{
    public const string HojaResumen = "Resumen";
    public const string HojaDineroParado = "Dinero parado";
    public const string HojaAbc = "80-20";
    public const string HojaDepartamentos = "Margen por departamento";
    public const string HojaRevisar = "Revisar catálogo";
    public const string HojaTemporada = "Temporada";

    private sealed record Columna<T>(string Titulo, Func<T, object?> Valor, Formato Formato = Formato.Texto, double Ancho = 14, bool Sumar = false);

    private static readonly XLColor Encabezado = XLColor.FromHtml("#1F4E78");

    public static void Guardar(Analisis analisis, string ruta, string origen)
    {
        using var libro = new XLWorkbook();
        Resumen(libro.AddWorksheet(HojaResumen), analisis, origen);
        DineroParado(libro.AddWorksheet(HojaDineroParado), analisis);
        Abc(libro.AddWorksheet(HojaAbc), analisis);
        Departamentos(libro.AddWorksheet(HojaDepartamentos), analisis);
        Revisar(libro.AddWorksheet(HojaRevisar), analisis);
        Temporada(libro.AddWorksheet(HojaTemporada), analisis);
        libro.SaveAs(ruta);
    }

    private static void Resumen(IXLWorksheet hoja, Analisis analisis, string origen)
    {
        Titulo(hoja, "Diagnóstico del negocio", $"Base: {origen}. Generado el {DateTime.Now:dd/MM/yyyy HH:mm}. Montos con IVA.");

        var fila = 4;
        foreach (var r in analisis.Resumen())
        {
            hoja.Cell(fila, 1).Value = r.Concepto;
            hoja.Cell(fila, 1).Style.Font.Bold = true;
            Valor(hoja.Cell(fila, 2), r.Valor, r.Formato);
            hoja.Cell(fila, 3).Value = r.Nota;
            hoja.Cell(fila, 3).Style.Font.FontColor = XLColor.Gray;
            fila++;
        }
        hoja.Column(1).Width = 40;
        hoja.Column(2).Width = 22;
        hoja.Column(3).Width = 75;
        hoja.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void DineroParado(IXLWorksheet hoja, Analisis analisis)
    {
        var filas = analisis.DineroParado();
        Titulo(hoja, $"Dinero parado: {filas.Sum(f => f.Valor):C0} en {filas.Count} variantes",
            $"Mercancía con existencia que no se ha vendido en los últimos {Analisis.DiasSinVenta} días, de mayor a menor valor. " +
            "Candidatos a promoción, devolución al proveedor o a dejar de comprar.");
        Tabla(hoja, "DineroParado", filas,
        [
            .. ColumnasArticulo<FilaDineroParado>(f => f.Articulo),
            new("Existencia", f => f.Existencia, Formato.Decimal, 11),
            new("Costo promedio", f => f.CostoPromedio, Formato.Dinero),
            new("Valor a costo", f => f.Valor, Formato.Dinero),
            new("Última venta", f => f.UltimaVenta, Formato.Fecha, 13),
            new("Días sin vender", f => f.DiasSinVender, Formato.Entero, 12),
            new("Situación", f => f.Situacion, Ancho: 30),
        ]);
    }

    private static void Abc(IXLWorksheet hoja, Analisis analisis)
    {
        var filas = analisis.Abc();
        var a = filas.Count(f => f.Clase == 'A');
        Titulo(hoja, $"80-20: {a} de {filas.Count} variantes ({(double)a / Math.Max(filas.Count, 1):P0}) hacen el 80 % de la venta",
            "A: hacen el 80 % de la venta, no deben faltar nunca. B: el siguiente 15 %. C: el último 5 %, revisar si vale la pena tenerlas.");
        Tabla(hoja, "Abc", filas,
        [
            new("#", f => f.Rango, Formato.Entero, 7),
            new("Clase", f => f.Clase.ToString(), Ancho: 8),
            .. ColumnasArticulo<FilaAbc>(f => f.Articulo),
            new("Unidades", f => f.Unidades, Formato.Decimal, 11),
            new("Venta", f => f.Venta, Formato.Dinero),
            new("Utilidad", f => f.Utilidad, Formato.Dinero),
            new("Margen", f => f.Margen, Formato.Porcentaje, 10),
            new("% de la venta", f => f.PorcentajeVenta, Formato.Porcentaje, 12),
            new("% acumulado", f => f.PorcentajeAcumulado, Formato.Porcentaje, 12),
        ]);
    }

    private static void Departamentos(IXLWorksheet hoja, Analisis analisis)
    {
        var filas = analisis.MargenPorDepartamento();
        Titulo(hoja, "Margen por departamento",
            "Utilidad bruta = venta − costo de lo vendido. Los cobros a terceros (recargas) van aparte: solo la comisión es ingreso.");
        Tabla(hoja, "Departamentos", filas,
        [
            new("Departamento", f => f.CobroTerceros ? $"{f.Departamento} (cobro a terceros)" : f.Departamento, Ancho: 34),
            new("Venta", f => f.Venta, Formato.Dinero, 16, Sumar: true),
            new("Costo", f => f.Costo, Formato.Dinero, 16, Sumar: true),
            new("Utilidad", f => f.Utilidad, Formato.Dinero, 16, Sumar: true),
            new("Margen", f => f.Margen, Formato.Porcentaje, 10),
            new("% de la venta propia", f => f.CobroTerceros ? null : f.PorcentajeVenta, Formato.Porcentaje, 18),
            new("Variantes vendidas", f => f.VariantesVendidas, Formato.Entero, 16),
        ]);
    }

    private static void Revisar(IXLWorksheet hoja, Analisis analisis)
    {
        var filas = analisis.RevisarCatalogo();
        Titulo(hoja, "Revisar catálogo",
            "Productos sin código (no se pueden escanear), con algún precio debajo del costo, o que parecen capturados dos veces. " +
            "Filtra por la columna «Problema».");
        Tabla(hoja, "Revisar", filas,
        [
            new("Problema", f => f.Problema, Ancho: 22),
            .. ColumnasArticulo<FilaRevisar>(f => f.Articulo),
            new("Precio", f => f.Precio, Formato.Dinero, 12),
            new("Costo", f => f.Costo, Formato.Dinero, 12),
            new("Detalle", f => f.Detalle, Ancho: 60),
        ]);
    }

    private static void Temporada(IXLWorksheet hoja, Analisis analisis)
    {
        Titulo(hoja, "Temporada", "Venta propia por mes. El regreso a clases se cuenta del 15 de julio al 15 de septiembre.");

        var meses = analisis.Meses();
        var ultimaFila = Tabla(hoja, "Meses", meses,
        [
            new("Mes", f => f.Mes.ToDateTime(TimeOnly.MinValue).ToString("MMM yyyy"), Ancho: 12),
            new("Tickets", f => f.Tickets, Formato.Entero, 10, Sumar: true),
            new("Venta", f => f.Venta, Formato.Dinero, 16, Sumar: true),
            new("% del periodo", f => f.PorcentajeVenta, Formato.Porcentaje, 13),
            new("Venta por día abierto", f => f.VentaPorDia, Formato.Dinero, 18),
        ], conTotal: true);
        if (meses.Count > 0)
            hoja.Range(5, 3, 4 + meses.Count, 3).AddConditionalFormat().DataBar(XLColor.FromHtml("#5B9BD5")).LowestValue().HighestValue();

        var productos = analisis.ProductosDeTemporada();
        var inicio = ultimaFila + 3;
        hoja.Cell(inicio, 1).Value = "Lo que mueve el regreso a clases";
        hoja.Cell(inicio, 1).Style.Font.Bold = true;
        hoja.Cell(inicio, 1).Style.Font.FontSize = 12;
        hoja.Cell(inicio + 1, 1).Value =
            "Lo que más vende en temporada y se vende al menos 1.5 veces más por día que el resto del año. Hay que tenerlo surtido antes de julio.";
        hoja.Cell(inicio + 1, 1).Style.Font.Italic = true;
        Tabla(hoja, "ProductosTemporada", productos,
        [
            .. ColumnasArticulo<FilaTemporada>(f => f.Articulo),
            new("Venta en temporada", f => f.VentaTemporada, Formato.Dinero, 18),
            new("Venta resto del año", f => f.VentaResto, Formato.Dinero, 18),
            new("% en temporada", f => f.PorcentajeEnTemporada, Formato.Porcentaje, 14),
            new("Veces más por día", f => f.Indice, Formato.Decimal, 16),
        ], fila: inicio + 3, congelar: false);
    }

    // ── Ayudantes ───────────────────────────────────────────────────────────────

    private static IEnumerable<Columna<T>> ColumnasArticulo<T>(Func<T, Articulo> articulo) =>
    [
        new("Departamento", f => articulo(f).Departamento, Ancho: 20),
        new("Producto", f => articulo(f).Producto, Ancho: 34),
        new("Marca", f => articulo(f).Marca, Ancho: 14),
        new("Variante", f => articulo(f).Variante, Ancho: 24),
        new("Código", f => articulo(f).Codigo, Ancho: 15),
    ];

    private static void Titulo(IXLWorksheet hoja, string titulo, string explicacion)
    {
        hoja.Cell(1, 1).Value = titulo;
        hoja.Cell(1, 1).Style.Font.Bold = true;
        hoja.Cell(1, 1).Style.Font.FontSize = 14;
        hoja.Cell(1, 1).Style.Font.FontColor = Encabezado;
        hoja.Cell(2, 1).Value = explicacion;
        hoja.Cell(2, 1).Style.Font.Italic = true;
    }

    /// <summary>Escribe una tabla de Excel con filtros y formato por columna. Devuelve la última fila usada.</summary>
    private static int Tabla<T>(IXLWorksheet hoja, string nombre, IReadOnlyList<T> filas, IReadOnlyList<Columna<T>> columnas,
        int fila = 4, bool conTotal = false, bool congelar = true)
    {
        for (var c = 0; c < columnas.Count; c++)
        {
            hoja.Cell(fila, c + 1).Value = columnas[c].Titulo;
            if (hoja.Column(c + 1).Width < columnas[c].Ancho)
                hoja.Column(c + 1).Width = columnas[c].Ancho;
        }

        if (filas.Count == 0)
        {
            hoja.Cell(fila + 1, 1).Value = "Nada que reportar.";
            return fila + 1;
        }

        for (var r = 0; r < filas.Count; r++)
            for (var c = 0; c < columnas.Count; c++)
                Valor(hoja.Cell(fila + 1 + r, c + 1), columnas[c].Valor(filas[r]), columnas[c].Formato);

        var ultima = fila + filas.Count;
        var tabla = hoja.Range(fila, 1, ultima, columnas.Count).CreateTable(nombre);
        tabla.Theme = XLTableTheme.TableStyleMedium2;

        if (conTotal)
        {
            tabla.ShowTotalsRow = true;
            tabla.Field(0).TotalsRowLabel = "Total";
            for (var c = 1; c < columnas.Count; c++)
            {
                if (columnas[c].Sumar)
                {
                    tabla.Field(c).TotalsRowFunction = XLTotalsRowFunction.Sum;
                    hoja.Cell(ultima + 1, c + 1).Style.NumberFormat.Format = FormatoExcel(columnas[c].Formato);
                }
            }
            ultima++;
        }

        if (congelar)
            hoja.SheetView.FreezeRows(fila);
        return ultima;
    }

    private static void Valor(IXLCell celda, object? valor, Formato formato)
    {
        celda.Value = valor switch
        {
            null => Blank.Value,
            string s => s,
            DateTime d => d,
            decimal m => m,
            int i => i,
            long l => l,
            double d => d,
            _ => valor.ToString(),
        };
        if (FormatoExcel(formato) is { } f)
            celda.Style.NumberFormat.Format = f;
    }

    private static string? FormatoExcel(Formato formato) => formato switch
    {
        Formato.Entero => "#,##0",
        Formato.Dinero => "$#,##0.00",
        Formato.Porcentaje => "0.0%",
        Formato.Fecha => "dd/mm/yyyy",
        Formato.Decimal => "#,##0.0##",
        _ => null,
    };
}
