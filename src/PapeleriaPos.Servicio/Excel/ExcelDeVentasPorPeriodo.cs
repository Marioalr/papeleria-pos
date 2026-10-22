using ClosedXML.Excel;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// El Excel de «Ventas por período» (entrega 9, parte 2), como el de eleventa: todos los tickets de esos días (no solo la página
/// que se ve), cada uno con su folio, la fecha y la hora, la caja, quién cobró, cómo se pagó, el total y si se canceló o se
/// devolvió, con los totales al final; y en otra hoja cada día, con sus formas de pago. Sin costos: no los lleva la pantalla.
/// </summary>
internal static class ExcelDeVentasPorPeriodo
{
    public const string HojaTickets = "Tickets";
    public const string HojaPorDia = "Por día";

    public static byte[] Armar(VentasPorPeriodoDto d, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        var titulo = $"Ventas por período: {TextosDeReportes.Periodo(d.Desde, d.Hasta, d.Hoy, conAnio: true)}";
        var notas = Notas(d);
        Tickets(libro.AddWorksheet(HojaTickets), d, quien, titulo, notas);
        PorDia(libro.AddWorksheet(HojaPorDia), d, quien, titulo, notas);
        return LibroExcel.Bytes(libro);
    }

    /// <summary>De qué caja, el piloto, lo que no cuenta y lo que hay del historial de eleventa en esos días.</summary>
    public static List<string?> Notas(VentasPorPeriodoDto d) =>
    [
        d.CajaId is { } caja ? $"De la caja: {d.Cajas.FirstOrDefault(c => c.Id == caja)?.Nombre ?? "—"}." : "De todas las cajas.",
        d.Piloto ? PilotoConEleventa.VentasPorPeriodoDeLaCaja2 : null,
        "Los tickets cancelados se listan, pero no cuentan; lo devuelto en esos días (también de tickets de antes) se resta de lo vendido, "
        + "como en el corte del día y en los reportes.",
        d.DelHistorialTickets > 0
            ? $"Además, el historial de eleventa tiene {Formato.Numero(d.DelHistorialTickets)} tickets ({Formato.Dinero(d.DelHistorialCentavos)}) de "
              + "toda la tienda en esos días: cuentan en los reportes, pero no se listan aquí porque no traen caja, cajero ni forma de pago."
            : null,
    ];

    private static void Tickets(IXLWorksheet hoja, VentasPorPeriodoDto d, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, titulo, notas);
        var columnas = new List<Columna<VentaDeUnDiaDto>> { new("Folio", v => v.Folio, Ancho: 12) };
        // En el piloto, el folio que salió en el ticket es el de eleventa.
        if (d.Ventas.Any(v => v.FolioEleventa is not null))
            columnas.Add(new("Folio de eleventa", v => v.FolioEleventa, FormatoCelda.Entero, 12));
        columnas.AddRange(
        [
            new("Fecha y hora", v => v.FechaLocal, FormatoCelda.FechaYHora, 17),
            new("Caja", v => v.Caja, Ancho: 14),
            new("Cajero", v => v.Cajero, Ancho: 20),
            new("Forma de pago", v => TextosDeReportes.FormasDePago(v.Formas), Ancho: 22),
            new("Total", v => v.TotalCentavos, FormatoCelda.Dinero, 13),
            new("Cancelada o devuelta", v => TextosDeReportes.Estado(v), Ancho: 20),
            new("Devuelto", v => v.DevueltoCentavos > 0 ? v.DevueltoCentavos : null, FormatoCelda.Dinero, 12),
        ]);
        var ultima = LibroExcel.Tabla(hoja, fila, "TablaTickets", columnas, d.Ventas, "No hubo tickets en esos días.");

        // Los totales de todos esos días, al final: el dinero bajo «Total» y lo que es, a su izquierda.
        var t = d.Totales;
        var columnaTotal = columnas.FindIndex(c => c.Titulo == "Total") + 1;
        var renglones = new List<(string, object?, FormatoCelda, bool)>
        {
            ("Tickets cobrados", t.Tickets, FormatoCelda.Entero, false),
            ("Vendido", t.VendidoCentavos, FormatoCelda.Dinero, true),
            (t.Devoluciones == 1 ? "Devuelto (1 devolución)" : $"Devuelto ({t.Devoluciones} devoluciones)", -t.DevueltoCentavos, FormatoCelda.Dinero, false),
            ("Venta neta", t.VentaCentavos, FormatoCelda.Dinero, true),
            (t.Cancelados == 1 ? "1 ticket cancelado (no cuenta)" : $"{t.Cancelados} tickets cancelados (no cuentan)", t.CanceladosCentavos, FormatoCelda.Dinero,
                false),
        };
        renglones.AddRange(t.Formas.Select(f => ($"{TextosDeReportes.Forma(f.Forma)}{(f.Forma == FormaPago.Efectivo ? " (sin el redondeo al peso)" : "")}",
            (object?)f.Centavos, FormatoCelda.Dinero, false)));
        LibroExcel.Totales(hoja, ultima + 2, "Totales del periodo", columnaTotal, renglones);
    }

    /// <summary>Cada día de esos días (también los que no tuvieron nada), con sus formas de pago, y el total al final.</summary>
    private static void PorDia(IXLWorksheet hoja, VentasPorPeriodoDto d, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"{titulo}, por día",
            [.. notas, "Cada forma de pago es lo cobrado así (el efectivo, sin el redondeo al peso): suman lo vendido."]);
        var conAlgo = d.Dias.Where(x => x.Fecha is not null).ToDictionary(x => x.Fecha!.Value);
        var dias = Enumerable.Range(0, d.Hasta.DayNumber - d.Desde.DayNumber + 1).Select(n => d.Desde.AddDays(n))
            .Select(dia => conAlgo.GetValueOrDefault(dia) ?? new ResumenDeVentasDto(dia, 0, 0, 0, 0, 0, 0, []))
            .ToList();
        // Las formas que se usaron en esos días; el efectivo, siempre.
        var formas = d.Totales.Formas.Select(f => f.Forma).Append(FormaPago.Efectivo).Distinct().Order().ToList();
        var columnas = new List<Columna<ResumenDeVentasDto>>
        {
            new("Día", x => x.Fecha, FormatoCelda.Fecha, 13),
            new("Tickets", x => x.Tickets, FormatoCelda.Entero, 9),
            new("Vendido", x => x.VendidoCentavos, FormatoCelda.Dinero, 14),
        };
        columnas.AddRange(formas.Select(forma =>
            new Columna<ResumenDeVentasDto>(TextosDeReportes.Forma(forma), x => x.Formas.FirstOrDefault(f => f.Forma == forma)?.Centavos ?? 0L, FormatoCelda.Dinero, 15)));
        columnas.AddRange(
        [
            new("Devoluciones", x => x.Devoluciones, FormatoCelda.Entero, 13),
            new("Devuelto", x => x.DevueltoCentavos, FormatoCelda.Dinero, 13),
            new("Venta neta", x => x.VentaCentavos, FormatoCelda.Dinero, 14),
            new("Cancelados", x => x.Cancelados, FormatoCelda.Entero, 11),
            new("Lo cancelado", x => x.CanceladosCentavos, FormatoCelda.Dinero, 13),
        ]);
        var ultima = LibroExcel.Tabla(hoja, fila, "TablaPorDia", columnas, dias, "No hubo nada en esos días.");

        // El total, debajo de cada columna (fuera de la tabla: al ordenarla o filtrarla se queda abajo).
        var total = ultima + 1;
        hoja.Cell(total, 1).Value = "Total";
        for (var c = 1; c < columnas.Count; c++)
            LibroExcel.Valor(hoja.Cell(total, c + 1), columnas[c].Valor(d.Totales), columnas[c].Formato);
        hoja.Row(total).Style.Font.SetBold();
    }
}
