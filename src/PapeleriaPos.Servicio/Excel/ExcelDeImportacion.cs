using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// Lo que cambió una importación de Excel (Productos › Productos en Excel), completo, como se guardó con su renglón de la bitácora:
/// se pide desde Configuración › Bitácora. Una hoja con cada dato que cambió (el producto, lo de antes y lo que quedó, y si había
/// cambiado después de exportar el Excel) y otra con los productos nuevos. Lo ve quien tiene «Consultar la bitácora».
/// </summary>
internal static class ExcelDeImportacion
{
    public const string HojaCambios = "Lo que cambió";
    public const string HojaNuevos = "Productos nuevos";

    /// <summary>Cada dato que cambió, en su renglón.</summary>
    private sealed record Dato(ProductoQueCambiaDto Producto, CambioDeDatoDto Cambio);

    /// <param name="renglon">Lo que dice su renglón de la bitácora.</param>
    /// <param name="cuando">Cuándo se aplicó (hora de la tienda).</param>
    public static byte[] Armar(DetalleDeImportacionDto d, QuienExporta quien, string renglon, DateTime cuando)
    {
        using var libro = LibroExcel.Nuevo();
        var hoja = libro.AddWorksheet(HojaCambios);
        var aplicada = $"Se aplicó el {cuando.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} a las {cuando.ToString("HH:mm", CultureInfo.InvariantCulture)}.";
        var fila = LibroExcel.Encabezado(hoja, quien, $"Importación de «{d.Archivo}»",
        [
            renglon,
            aplicada,
            "Cada dato que cambió, con lo que tenía antes y lo que quedó. «Había cambiado» dice lo que alguien cambió después de exportar ese Excel "
            + "(se aplicó de todos modos)." + (d.SinAplicar > 0 ? $" Otros {d.SinAplicar} que cambiaron después de exportarlo se quedaron como estaban." : ""),
        ]);
        var datos = d.Cambiaron.SelectMany(p => p.Cambios.Select(c => new Dato(p, c))).ToList();
        LibroExcel.Tabla(hoja, fila, "TablaCambios",
        [
            new Columna<Dato>("Producto", x => x.Producto.Producto, Ancho: 38),
            new("Código", x => x.Producto.Codigo, Ancho: 16),
            new("Renglón", x => x.Producto.Hoja == ProductosEnExcel.HojaProductos ? $"{x.Producto.Renglon}" : $"{x.Producto.Renglon} de {x.Producto.Hoja}", Ancho: 16),
            new("Qué", x => x.Cambio.Dato, Ancho: 22),
            new("Antes", x => x.Cambio.Antes, Ancho: 24),
            new("Quedó", x => x.Cambio.Despues, Ancho: 24),
            new("Había cambiado", x => x.Producto.CambioDespues is { } antes ? string.Join("; ", antes) : null, Ancho: 30),
        ], datos, "No cambió ningún producto que ya existía.");

        var nuevos = libro.AddWorksheet(HojaNuevos);
        fila = LibroExcel.Encabezado(nuevos, quien, $"Productos nuevos de «{d.Archivo}»",
        [
            aplicada,
            d.DepartamentosNuevos.Count == 0 ? null : $"Departamentos nuevos: {string.Join(", ", d.DepartamentosNuevos)}.",
            d.MarcasNuevas.Count == 0 ? null : $"Marcas nuevas: {string.Join(", ", d.MarcasNuevas)}.",
        ]);
        LibroExcel.Tabla(nuevos, fila, "TablaNuevos",
        [
            new Columna<ProductoNuevoDeExcelDto>("Código", x => x.Codigo, Ancho: 16),
            new("Producto", x => x.Producto, Ancho: 38),
            new("Departamento", x => x.Departamento, Ancho: 18),
            new("Precio", x => x.PrecioCentavos, FormatoCelda.Dinero, 11),
            new("Lo demás", x => x.Detalle, Ancho: 40),
            new("Renglón", x => x.Renglon, FormatoCelda.Entero, 9),
        ], d.Nuevos, "No se dio de alta ningún producto.");
        return LibroExcel.Bytes(libro);
    }
}
