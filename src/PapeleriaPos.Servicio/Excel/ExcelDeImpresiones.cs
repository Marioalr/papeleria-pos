using ClosedXML.Excel;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// El Excel de «Impresiones por período» (2026-11-20), como el reporte de Control Print: una hoja con los totales y cada tipo, una
/// por cada tabla de la pantalla (por día, por quien atendió, por PC, por impresora, las no vendidas y las hojas que faltaron) y
/// una con cada impresión de esos días. El costo, la utilidad y la pérdida, solo si quien lo exporta los ve (como en la pantalla).
/// </summary>
internal static class ExcelDeImpresiones
{
    public const string HojaResumen = "Resumen";
    public const string HojaPorDia = "Por día";
    public const string HojaPorAtendio = "Por quién atendió";
    public const string HojaPorPc = "Por PC";
    public const string HojaPorImpresora = "Por impresora";
    public const string HojaNoVendidas = "No vendidas";
    public const string HojaFaltaron = "Hojas que faltaron";
    public const string HojaImpresiones = "Impresiones";

    public static byte[] Armar(ImpresionesPorPeriodoDto d, QuienExporta quien, CancellationToken ct = default)
    {
        using var libro = LibroExcel.Nuevo();
        var titulo = $"Impresiones por período: {TextosDeReportes.Periodo(d.Desde, d.Hasta, d.Hoy, conAnio: true)}";
        List<string?> notas =
        [
            d.Piloto ? TextosDeImpresionesPorPeriodo.Piloto : null,
            TextosDeImpresionesPorPeriodo.ComoCuenta,
            d.VeCosto ? TextosDeImpresionesPorPeriodo.ComoCuentaElCosto : null,
        ];
        Resumenes(libro.AddWorksheet(HojaResumen), "TablaPorTipo", "Tipo", d.PorTipo, d, quien, $"{titulo}, por tipo", notas, conTotal: true);
        Resumenes(libro.AddWorksheet(HojaPorDia), "TablaPorDia", null, d.Dias, d, quien, $"{titulo}, por día", notas, conTotal: true);
        Resumenes(libro.AddWorksheet(HojaPorAtendio), "TablaPorAtendio", "Atendió", d.PorAtendio, d, quien, $"{titulo}, por quién atendió", notas);
        Resumenes(libro.AddWorksheet(HojaPorPc), "TablaPorPc", "PC", d.PorPc, d, quien, $"{titulo}, por PC", notas);
        Resumenes(libro.AddWorksheet(HojaPorImpresora), "TablaPorImpresora", "Impresora", d.PorImpresora, d, quien, $"{titulo}, por impresora", notas);
        ct.ThrowIfCancellationRequested();
        NoVendidas(libro.AddWorksheet(HojaNoVendidas), d, quien, titulo, notas);
        Faltaron(libro.AddWorksheet(HojaFaltaron), d, quien, titulo, notas);
        ct.ThrowIfCancellationRequested();
        Impresiones(libro.AddWorksheet(HojaImpresiones), d, quien, titulo, notas);
        ct.ThrowIfCancellationRequested();
        return LibroExcel.Bytes(libro);
    }

    /// <summary>Las columnas de cada grupo: lo que se imprimió, su cobro y, con el permiso, su costo y su utilidad.</summary>
    public static List<Columna<ResumenImpresionesDto>> Columnas(string? grupo, bool veCosto)
    {
        var columnas = new List<Columna<ResumenImpresionesDto>>
        {
            grupo is null ? new("Día", r => r.Fecha, FormatoCelda.Fecha, 13) : new(grupo, r => r.Nombre, Ancho: 26),
            new("Impresiones", r => r.Trabajos, FormatoCelda.Entero, 12),
            new("Caras", r => r.Caras, FormatoCelda.Entero, 9),
            new("En color", r => r.CarasColor, FormatoCelda.Entero, 9),
            new("Hojas", r => r.Hojas, FormatoCelda.Entero, 9),
            new("Cobradas", r => r.Cobradas, FormatoCelda.Entero, 10),
            new("Cobrado", r => r.CobradoCentavos, FormatoCelda.Dinero, 13),
            new("Por cobrar", r => r.PorCobrar, FormatoCelda.Entero, 10),
            new("Su precio", r => r.PorCobrarCentavos, FormatoCelda.Dinero, 13),
            new("No vendidas", r => r.NoVendidas, FormatoCelda.Entero, 11),
            new("Sin cobrar", r => r.NoVendidasCentavos, FormatoCelda.Dinero, 13),
            new("En trámite", r => r.EnTramite, FormatoCelda.Entero, 10),
        };
        if (veCosto)
            columnas.AddRange(
            [
                new("Costo", r => r.CostoCentavos, FormatoCelda.Dinero, 13),
                new("Costo de lo cobrado", r => r.CostoCobradoCentavos, FormatoCelda.Dinero, 15),
                new("Utilidad", r => r.UtilidadCentavos, FormatoCelda.Dinero, 13),
                new("Pérdida", r => r.PerdidaCentavos, FormatoCelda.Dinero, 13),
            ]);
        return columnas;
    }

    private static void Resumenes(IXLWorksheet hoja, string tabla, string? grupo, IReadOnlyList<ResumenImpresionesDto> renglones, ImpresionesPorPeriodoDto d,
        QuienExporta quien, string titulo, List<string?> notas, bool conTotal = false)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, titulo, notas);
        var columnas = Columnas(grupo, d.VeCosto);
        var ultima = LibroExcel.Tabla(hoja, fila, tabla, columnas, renglones, "No se imprimió nada en esos días.");
        if (!conTotal || renglones.Count == 0)
            return;
        // El total, debajo de cada columna (fuera de la tabla: al ordenarla o filtrarla se queda abajo).
        var total = ultima + 1;
        hoja.Cell(total, 1).Value = "Total";
        for (var c = 1; c < columnas.Count; c++)
            LibroExcel.Valor(hoja.Cell(total, c + 1), columnas[c].Valor(d.Totales), columnas[c].Formato);
        hoja.Row(total).Style.Font.SetBold();
    }

    private static void NoVendidas(IXLWorksheet hoja, ImpresionesPorPeriodoDto d, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"{titulo}, las no vendidas por motivo", notas);
        var columnas = new List<Columna<NoVendidasPorMotivoDto>>
        {
            new("Motivo", n => TextosDeImpresionesPorPeriodo.Motivo(n.Motivo), Ancho: 24),
            new("Cuántas", n => n.Cuantas, FormatoCelda.Entero, 10),
            new("Caras", n => n.Caras, FormatoCelda.Entero, 9),
            new("Sin cobrar", n => n.PrecioCentavos, FormatoCelda.Dinero, 13),
        };
        if (d.VeCosto)
            columnas.Add(new("Pérdida", n => n.CostoCentavos, FormatoCelda.Dinero, 13));
        LibroExcel.Tabla(hoja, fila, "TablaNoVendidas", columnas, d.NoVendidas, "No hubo no vendidas en esos días.");
    }

    private static void Faltaron(IXLWorksheet hoja, ImpresionesPorPeriodoDto d, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"{titulo}, las hojas que faltaron",
            [.. notas, "La impresora contó menos caras de las que se mandaron: un atasco o una cancelación en el panel."]);
        LibroExcel.Tabla(hoja, fila, "TablaFaltaron",
        [
            new("Fecha y hora", f => f.FechaLocal, FormatoCelda.FechaYHora, 17),
            new("Documento", f => f.Documento, Ancho: 36),
            new("PC", f => f.Pc, Ancho: 14),
            new("Impresora", f => f.Impresora, Ancho: 16),
            new("Atendió", f => f.Atendio, Ancho: 18),
            new("Caras mandadas", f => f.Caras, FormatoCelda.Entero, 11),
            new("Caras que salieron", f => f.CarasContadas, FormatoCelda.Entero, 11),
            new("Cobro", f => TextosDeImpresionesPorPeriodo.Cobro(f.Cobro), Ancho: 13),
            new("Solo lo que salió", f => f.SoloLoQueSalio ? "Sí" : "No", Ancho: 11),
            new("Precio", f => f.PrecioCentavos, FormatoCelda.Dinero, 12),
        ], d.Faltaron, "No faltaron hojas en esos días.");
    }

    private static void Impresiones(IXLWorksheet hoja, ImpresionesPorPeriodoDto d, QuienExporta quien, string titulo, List<string?> notas)
    {
        var fila = LibroExcel.Encabezado(hoja, quien, $"{titulo}, cada impresión", notas);
        var columnas = new List<Columna<TrabajoDelPeriodoDto>>
        {
            new("Fecha y hora", t => t.FechaLocal, FormatoCelda.FechaYHora, 17),
            new("Tipo", t => TextosDeImpresionesPorPeriodo.Tipo(t.Tipo), Ancho: 18),
            new("Documento", t => t.Documento, Ancho: 36),
            new("PC", t => t.Pc, Ancho: 14),
            new("Impresora", t => t.Impresora, Ancho: 16),
            new("Atendió", t => t.Atendio, Ancho: 18),
            new("Páginas", t => t.Paginas, FormatoCelda.Entero, 9),
            new("Copias", t => t.Copias, FormatoCelda.Entero, 9),
            new("Caras", t => t.Caras, FormatoCelda.Entero, 9),
            new("En color", t => t.CarasColor, FormatoCelda.Entero, 9),
            new("Hojas", t => t.Hojas, FormatoCelda.Entero, 9),
            new("Contó la impresora", t => t.CarasContadas, FormatoCelda.Entero, 11),
            new("Tamaño", t => t.Tamano, Ancho: 10),
            new("Papel", t => t.TipoPapel, Ancho: 14),
            new("Cobro", t => TextosDeImpresionesPorPeriodo.Cobro(t.Cobro), Ancho: 13),
            new("Motivo", t => t.Motivo is { } m ? TextosDeImpresionesPorPeriodo.Motivo(m) : null, Ancho: 18),
            new("Precio", t => t.PrecioCentavos, FormatoCelda.Dinero, 12),
        };
        if (d.VeCosto)
            columnas.Add(new("Costo", t => t.CostoCentavos, FormatoCelda.Dinero, 12));
        LibroExcel.Tabla(hoja, fila, "TablaImpresiones", columnas, d.Trabajos, "No se imprimió nada en esos días.");
    }
}
