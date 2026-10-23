using ClosedXML.Excel;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// El Excel de la pestaña Reportes (entrega 9, parte 2; en el prototipo, «un Excel con una hoja por tabla»): las cifras contra el
/// año pasado, la gráfica como tabla y cada desglose (por departamento, cajero, forma de pago y lo que más se vende), con las
/// mismas notas de la pantalla. Se arma con el mismo reporte que ve quien lo exporta (<see cref="ReporteVentasDto"/>): sin «Ver
/// costos, utilidad y margen» no trae la utilidad ni el costo, así que tampoco sus columnas ni sus renglones.
/// </summary>
internal static class ExcelDeReportes
{
    public const string HojaResumen = "Resumen";
    public const string HojaDepartamentos = "Departamentos";
    public const string HojaCajeros = "Cajeros";
    public const string HojaFormas = "Formas de pago";
    public const string HojaMasVendidos = "Lo más vendido";

    /// <summary>La hoja de la gráfica: «Por hora» (hoy), «Por día» (la semana y el mes) o «Por mes» (el año).</summary>
    public static string HojaDeLaSerie(PeriodoReporte periodo) => periodo switch
    {
        PeriodoReporte.Hoy => "Por hora",
        PeriodoReporte.Anio => "Por mes",
        _ => "Por día",
    };

    public static byte[] Armar(ReporteVentasDto r, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        var titulo = $"Reporte de ventas: {TextosDeReportes.Titulo(r)}";
        // Arriba de cada hoja: contra qué se compara y, en el piloto, que solo es lo de la caja 2.
        var notas = new List<string?> { TextosDeReportes.Comparacion(r), r.Piloto ? PilotoConEleventa.ReportesDeLaCaja2 : null };

        Resumen(libro.AddWorksheet(HojaResumen), r, quien, titulo, notas);
        Serie(libro.AddWorksheet(HojaDeLaSerie(r.Periodo)), r, quien, titulo, notas);
        Departamentos(libro.AddWorksheet(HojaDepartamentos), r, quien, titulo, notas);
        Cajeros(libro.AddWorksheet(HojaCajeros), r, quien, titulo, notas);
        Formas(libro.AddWorksheet(HojaFormas), r, quien, titulo, notas);
        MasVendidos(libro.AddWorksheet(HojaMasVendidos), r, quien, titulo, notas);
        return LibroExcel.Bytes(libro);
    }

    private sealed record Cifra(string Concepto, FormatoCelda Formato, decimal? Actual, decimal? Anterior);

    /// <summary>Las cifras de arriba de la pantalla, con lo que hay detrás de la venta neta; contra el año pasado, si lo hay.</summary>
    private static void Resumen(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, titulo, [.. notas, TextosDeReportes.VentaNeta(r.Actual)]);
        var (a, b) = (r.Actual, r.Anterior);
        var cifras = new List<Cifra>
        {
            new("Venta neta", FormatoCelda.Dinero, a.VentaCentavos, b?.VentaCentavos),
            new("Tickets", FormatoCelda.Entero, a.Tickets, b?.Tickets),
            new("Ticket promedio", FormatoCelda.Dinero, a.TicketPromedioCentavos, b?.TicketPromedioCentavos),
            new("Lo cobrado en los tickets que no se cancelaron", FormatoCelda.Dinero, a.VendidoCentavos, b?.VendidoCentavos),
            new("Lo devuelto", FormatoCelda.Dinero, a.DevueltoCentavos, b?.DevueltoCentavos),
            new("Devoluciones", FormatoCelda.Entero, a.Devoluciones, b?.Devoluciones),
            new("Cancelaciones hechas en el periodo", FormatoCelda.Entero, a.Cancelados, b?.Cancelados),
            new("Lo que sumaban esos tickets", FormatoCelda.Dinero, a.CanceladosCentavos, b?.CanceladosCentavos),
            new("Del historial de eleventa", FormatoCelda.Dinero, a.DelHistorialCentavos, b?.DelHistorialCentavos),
        };
        // La utilidad, solo si vino (quien lo exporta ve costos, utilidad y margen).
        if (r.ConUtilidad && a.Utilidad is { } u)
        {
            var antes = b?.Utilidad;
            cifras.Add(new("Utilidad bruta", FormatoCelda.Dinero, u.UtilidadCentavos, antes?.UtilidadCentavos));
            cifras.Add(new("Margen", FormatoCelda.Porcentaje, u.MargenCentesimas / 10000m, antes?.MargenCentesimas / 10000m));
            cifras.Add(new("Venta con costo", FormatoCelda.Dinero, u.VentaCentavos, antes?.VentaCentavos));
            cifras.Add(new("Costo de lo vendido", FormatoCelda.Dinero, u.CostoCentavos, antes?.CostoCentavos));
            if (u.ServiciosSinCostoCentavos != 0 || antes?.ServiciosSinCostoCentavos is not (null or 0))
                cifras.Add(new("Servicios sin costo (cuentan completos)", FormatoCelda.Dinero, u.ServiciosSinCostoCentavos, antes?.ServiciosSinCostoCentavos));
        }
        // Cada renglón con su formato: se escribe a mano, no por columnas.
        var titulos = b is null ? new[] { "Concepto", "Este periodo" } : ["Concepto", "Este periodo", "Año pasado", "Cambio"];
        for (var c = 0; c < titulos.Length; c++)
        {
            hoja.Cell(fila, c + 1).Value = titulos[c];
            hoja.Column(c + 1).Width = c == 0 ? 40 : 16;
        }
        for (var i = 0; i < cifras.Count; i++)
        {
            var renglon = fila + 1 + i;
            var cifra = cifras[i];
            hoja.Cell(renglon, 1).Value = cifra.Concepto;
            LibroExcel.Valor(hoja.Cell(renglon, 2), Centavos(cifra.Actual, cifra.Formato), cifra.Formato);
            if (b is null)
                continue;
            LibroExcel.Valor(hoja.Cell(renglon, 3), Centavos(cifra.Anterior, cifra.Formato), cifra.Formato);
            // El margen no se compara en porcentaje de sí mismo: su cambio va en puntos.
            if (cifra.Formato != FormatoCelda.Porcentaje)
                LibroExcel.Valor(hoja.Cell(renglon, 4), LibroExcel.Cambio(cifra.Actual, cifra.Anterior), FormatoCelda.Porcentaje);
        }
        var tabla = hoja.Range(fila, 1, fila + cifras.Count, titulos.Length).CreateTable("TablaCifras");
        tabla.Theme = XLTableTheme.TableStyleMedium2;
        tabla.ShowAutoFilter = false;
        hoja.SheetView.FreezeRows(fila);

        // Las mismas notas de la pantalla: de dónde sale cada cosa.
        var siguiente = fila + cifras.Count + 2;
        foreach (var nota in TextosDeReportes.Notas(r))
        {
            hoja.Cell(siguiente, 1).Value = nota;
            hoja.Cell(siguiente, 1).Style.Font.SetItalic();
            siguiente++;
        }
    }

    /// <summary>El dinero viaja en centavos; lo demás, como es.</summary>
    private static object? Centavos(decimal? valor, FormatoCelda formato) => valor is { } v && formato == FormatoCelda.Dinero ? (long)v : valor;

    /// <summary>La gráfica como tabla («Ver como tabla»): cada hora, día o mes de este año y el que le toca del año pasado.</summary>
    private static void Serie(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var porQue = r.Periodo switch
        {
            PeriodoReporte.Hoy => "por hora",
            PeriodoReporte.Anio => "por mes",
            _ => "por día",
        };
        var fila = LibroExcel.Encabezado(hoja, quien, $"{titulo}, {porQue} (la gráfica como tabla)", notas);
        var conAnterior = r.Anterior is not null;
        var columnas = new List<Columna<PuntoReporteDto>>
        {
            r.Periodo switch
            {
                PeriodoReporte.Hoy => new("Hora", p => TextosDeReportes.Detalle(r.Periodo, p.InicioLocal), Ancho: 18),
                PeriodoReporte.Anio => new("Mes", p => TextosDeReportes.Detalle(r.Periodo, p.InicioLocal), Ancho: 14),
                _ => new("Día", p => DateOnly.FromDateTime(p.InicioLocal), FormatoCelda.Fecha, 13),
            },
            new("Este año", p => p.ActualCentavos, FormatoCelda.Dinero, 15),
        };
        if (conAnterior)
        {
            columnas.Add(new("Año pasado", p => p.AnteriorCentavos, FormatoCelda.Dinero, 15));
            columnas.Add(new("Del año pasado", p => p.InicioAnteriorLocal is { } antes ? TextosDeReportes.DetalleAnterior(r.Periodo, antes, r.Desde) : null,
                Ancho: 18));
            columnas.Add(new("Cambio", p => LibroExcel.Cambio(p.ActualCentavos, p.AnteriorCentavos), FormatoCelda.Porcentaje, 10));
        }
        columnas.Add(new("Nota", p => p.EnCurso ? "va en curso" : p.ActualCentavos is null ? "todavía no llega" : null, Ancho: 16));
        LibroExcel.Tabla(hoja, fila, "TablaSerie", columnas, r.Serie, "No hay ventas en este periodo.");
    }

    private static void Departamentos(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"Por departamento · {titulo}",
        [
            .. notas,
            "Lo vendido menos lo devuelto en el periodo; al final, las impresiones, el artículo común y, si lo hay, lo del historial de "
            + "eleventa sin desglose: suman la venta neta.",
            TextosDeReportes.NotaSinDesglose(r),
        ]);
        var total = r.Departamentos.Where(d => d.VentaCentavos > 0).Sum(d => d.VentaCentavos);
        var columnas = new List<Columna<DepartamentoReporteDto>>
        {
            new("Departamento", d => d.Nombre, Ancho: 28),
            new("Venta", d => d.VentaCentavos, FormatoCelda.Dinero, 15),
            new("Parte", d => LibroExcel.Parte(Math.Max(d.VentaCentavos, 0), total), FormatoCelda.Porcentaje, 10),
        };
        if (r.Anterior is not null)
        {
            columnas.Add(new("Año pasado", d => d.AnteriorCentavos, FormatoCelda.Dinero, 15));
            columnas.Add(new("Cambio", d => LibroExcel.Cambio(d.VentaCentavos, d.AnteriorCentavos), FormatoCelda.Porcentaje, 10));
        }
        // El costo, la utilidad y el margen: solo para quien ve costos (a los demás el servicio ni se los mandó). El artículo común y
        // lo que el historial suma sin desglose no tienen costo: «sin costo», como en la pantalla.
        if (r.ConUtilidad)
        {
            static bool SinCosto(DepartamentoReporteDto d) => d.Clase is ClaseVenta.ArticuloComun or ClaseVenta.SinDesglose;
            columnas.Add(new("Costo", d => SinCosto(d) ? null : d.CostoCentavos, FormatoCelda.Dinero, 15));
            columnas.Add(new("Utilidad", d => SinCosto(d) ? null : d.UtilidadCentavos, FormatoCelda.Dinero, 15));
            columnas.Add(new("Margen", d => SinCosto(d) ? "sin costo" : (object?)(d.MargenCentesimas / 10000m), FormatoCelda.Porcentaje, 10));
        }
        LibroExcel.Tabla(hoja, fila, "TablaDepartamentos", columnas, r.Departamentos, "No hubo ventas en este periodo.");
    }

    private static void Cajeros(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"Por cajero · {titulo}",
        [
            .. notas,
            "Lo que cobró cada quien menos lo devuelto en el periodo de sus tickets; el historial de eleventa no trae el cajero.",
            r.Piloto
                ? "En el piloto el corte se hace en eleventa: aquí no hay diferencias de corte."
                : "Las diferencias son las de los cortes del turno hechos en el periodo, a nombre del cajero del turno (negativo: faltó).",
        ]);
        LibroExcel.Tabla(hoja, fila, "TablaCajeros",
        [
            new Columna<CajeroReporteDto>("Cajero", c => c.Nombre, Ancho: 24),
            new("Tickets", c => c.Tickets, FormatoCelda.Entero, 10),
            new("Venta", c => c.VentaCentavos, FormatoCelda.Dinero, 15),
            new("Cortes", c => c.Cortes, FormatoCelda.Entero, 9),
            new("Diferencias", c => c.Cortes == 0 ? null : c.DiferenciaCentavos, FormatoCelda.Dinero, 14),
            new("Faltó", c => c.Cortes == 0 ? null : c.FaltanteCentavos, FormatoCelda.Dinero, 13),
            new("Sobró", c => c.Cortes == 0 ? null : c.SobranteCentavos, FormatoCelda.Dinero, 13),
        ], r.Cajeros, "Nadie cobró aquí en este periodo.");
    }

    private static void Formas(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"Por forma de pago · {titulo}",
        [
            .. notas,
            "Lo cobrado de cada forma (el efectivo, sin el redondeo al peso) menos lo devuelto en la forma en que se regresó: suman la venta "
            + "neta de lo vendido aquí (el historial de eleventa no trae la forma de pago).",
        ]);
        var total = r.FormasDePago.Where(f => f.Centavos > 0).Sum(f => f.Centavos);
        LibroExcel.Tabla(hoja, fila, "TablaFormas",
        [
            new Columna<FormaPagoReporteDto>("Forma de pago", f => TextosDeReportes.Forma(f.Forma), Ancho: 20),
            new("Venta", f => f.Centavos, FormatoCelda.Dinero, 15),
            new("Parte", f => LibroExcel.Parte(Math.Max(f.Centavos, 0), total), FormatoCelda.Porcentaje, 10),
        ], r.FormasDePago, "Nada cobrado aquí en este periodo.");
    }

    private static void MasVendidos(IXLWorksheet hoja, ReporteVentasDto r, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"Lo que más se vende · {titulo}",
            [.. notas, "Por lo vendido menos lo devuelto, sin las impresiones ni el artículo común; lo juntado, en el producto en que quedó."]);
        var lugar = 0;
        LibroExcel.Tabla(hoja, fila, "TablaMasVendidos",
        [
            new Columna<ProductoReporteDto>("Lugar", _ => ++lugar, FormatoCelda.Entero, 8),
            new("Producto", p => p.Nombre, Ancho: 40),
            new("Cantidad", p => p.Cantidad, FormatoCelda.Cantidad, 11),
            new("Unidad", p => Unidad(p.Unidad), Ancho: 10),
            new("Venta", p => p.VentaCentavos, FormatoCelda.Dinero, 15),
        ], r.MasVendidos, "No se vendió ningún producto en este periodo.");
    }

    /// <summary>Cómo se lee la unidad base en el Excel.</summary>
    public static string Unidad(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metros",
        UnidadBase.Kilogramo => "kilos",
        UnidadBase.Litro => "litros",
        _ => "piezas",
    };
}
