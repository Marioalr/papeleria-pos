using ClosedXML.Excel;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// Los Excel de los reportes del inventario (Inventario › Movimientos, Bajos en inventario y Lo que vale el inventario; entrega 9,
/// parte 2, «A Excel, como eleventa»): lo que se ve en cada uno, con sus filtros, sin las páginas. El costo de cada movimiento
/// solo va para quien tiene «Ver costos, utilidad y margen»; lo que vale el inventario es todo costo, así que solo lo exporta
/// quien lo ve.
/// </summary>
internal static class ExcelDelInventario
{
    public const string HojaMovimientos = "Movimientos";
    public const string HojaBajos = "Bajos en inventario";
    public const string HojaDepartamentos = "Por departamento";
    public const string HojaProductos = "Productos";

    /// <param name="filtros">Lo que se eligió además de los días, ya en palabras: «Tipo: Venta», «Producto: «pluma»»…</param>
    public static byte[] Movimientos(MovimientosInventarioDto m, FiltroMovimientosDto filtro, IReadOnlyList<string> filtros, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        var hoja = libro.AddWorksheet(HojaMovimientos);
        var dias = TextosDeReportes.Periodo(filtro.Desde, filtro.Hasta, DateOnly.FromDateTime(quien.CuandoLocal), conAnio: true);
        var fila = LibroExcel.Encabezado(hoja, quien, $"Movimientos del inventario: {dias}",
        [
            filtros.Count == 0 ? "Todos los movimientos de esos días." : $"Solo {string.Join(" · ", filtros)}.",
            "Del más reciente al más viejo. «Quedó» es la existencia después de cada uno."
            + (m.ConCostos ? " El costo va con el signo del movimiento." : ""),
        ]);
        var columnas = new List<Columna<MovimientoInventarioDto>>
        {
            new("Fecha y hora", x => x.FechaLocal, FormatoCelda.FechaYHora, 17),
            new("Movimiento", x => Textos.Clase(x.Clase), Ancho: 18),
            new("Documento", x => x.Documento, Ancho: 26),
            new("Motivo", x => x.Motivo, Ancho: 26),
            new("Producto", x => x.Producto, Ancho: 38),
            new("Código", x => x.Codigo, Ancho: 16),
            new("Departamento", x => x.Departamento, Ancho: 18),
            new("Cantidad", x => x.Cantidad, FormatoCelda.Cantidad, 11),
            new("Quedó", x => x.Existencia, FormatoCelda.Cantidad, 10),
            new("Unidad", x => ExcelDeReportes.Unidad(x.Unidad), Ancho: 9),
            new("Quién", x => x.Quien, Ancho: 18),
        };
        // El costo, solo para quien ve costos: a los demás ni siquiera les llegó.
        if (m.ConCostos)
            columnas.Add(new("Costo", x => x.CostoCentavos, FormatoCelda.Dinero, 13));
        LibroExcel.Tabla(hoja, fila, "TablaMovimientos", columnas, m.Movimientos, "No hubo movimientos con esos filtros en esos días.");
        return LibroExcel.Bytes(libro);
    }

    /// <param name="filtros">Lo que se eligió, ya en palabras: «Departamento: Escritura», «Solo con mínimo propio»…</param>
    public static byte[] Bajos(BajosInventarioDto b, IReadOnlyList<string> filtros, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        var hoja = libro.AddWorksheet(HojaBajos);
        var fila = LibroExcel.Encabezado(hoja, quien, "Bajos en inventario: lo que está en su mínimo o debajo",
        [
            filtros.Count == 0 ? "Todos los departamentos." : $"Solo {string.Join(" · ", filtros)}.",
            $"Por departamento y por nombre. Sin mínimo propio cuenta {PocaExistencia.Hasta} o menos. «Pedir» llega a su máximo con lo que ya viene "
            + "en órdenes de compra; sin máximo, es lo que pide el sugerido de compra (en la presentación de «Pedir en»), y al menos lo que falta "
            + "para llegar a su mínimo.",
        ]);
        LibroExcel.Tabla(hoja, fila, "TablaBajos",
        [
            new Columna<BajoInventarioDto>("Departamento", x => x.Departamento, Ancho: 18),
            new("Producto", x => x.Nombre, Ancho: 38),
            new("Código", x => x.Codigo, Ancho: 16),
            new("Hay", x => x.Existencia, FormatoCelda.Cantidad, 9),
            new("Mínimo", x => x.Minimo, FormatoCelda.Cantidad, 9),
            new("Mínimo propio", x => x.MinimoPropio, Ancho: 14),
            new("Máximo", x => x.Maximo, FormatoCelda.Cantidad, 9),
            new("Máximo del sugerido", x => x.Maximo is null ? x.MaximoSugerido : null, FormatoCelda.Cantidad, 19),
            new("En camino", x => x.EnCamino > 0 ? x.EnCamino : null, FormatoCelda.Cantidad, 11),
            new("Pedir", x => x.Pedir, FormatoCelda.Cantidad, 9),
            new("Pedir en", x => x.PedirEn, Ancho: 16),
            new("Unidad", x => ExcelDeReportes.Unidad(x.Unidad), Ancho: 9),
        ], b.Productos, "Nada está en su mínimo o debajo con esos filtros.");
        return LibroExcel.Bytes(libro);
    }

    /// <param name="productosDe">De qué son los productos: «Todos los productos» o «Productos de «Cuadernos»», con su filtro.</param>
    public static byte[] Valor(ValorInventarioDto v, string productosDe, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        var titulo = $"Lo que vale el inventario a costo, a las {v.FechaLocal:HH:mm} del {v.FechaLocal:dd/MM/yyyy}";
        var nota = "Cada producto vale su costo promedio (el de sus compras, el que usa el POS) por su existencia. Lo que quedó en negativo no "
            + "resta, y lo que tiene existencia sin costo no suma. Un producto que ya no se vende cuenta mientras tenga existencia.";

        var hoja = libro.AddWorksheet(HojaDepartamentos);
        var fila = LibroExcel.Encabezado(hoja, quien, titulo, [nota]);
        var total = v.Total.ValorCentavos;
        var columnas = new List<Columna<ValorGrupoDto>>
        {
            new("Departamento", g => g.Nombre, Ancho: 26),
            new("Vale", g => g.ValorCentavos, FormatoCelda.Dinero, 15),
            new("Parte", g => LibroExcel.Parte(g.ValorCentavos, total), FormatoCelda.Porcentaje, 9),
            new("Con existencia", g => g.ConExistencia, FormatoCelda.Entero, 14),
            new("En cero", g => g.EnCero, FormatoCelda.Entero, 9),
            new("En negativo", g => g.Negativa, FormatoCelda.Entero, 12),
            new("Sin costo", g => g.SinCosto, FormatoCelda.Entero, 10),
        };
        var ultima = LibroExcel.Tabla(hoja, fila, "TablaPorDepartamento", columnas, v.Departamentos, "Nada que contar en el inventario.");
        // Toda la tienda, abajo.
        var suma = ultima + 1;
        for (var c = 0; c < columnas.Count; c++)
            LibroExcel.Valor(hoja.Cell(suma, c + 1), columnas[c].Valor(v.Total), columnas[c].Formato);
        hoja.Row(suma).Style.Font.SetBold();

        var productos = libro.AddWorksheet(HojaProductos);
        fila = LibroExcel.Encabezado(productos, quien, titulo, [productosDe, nota]);
        LibroExcel.Tabla(productos, fila, "TablaProductos",
        [
            new Columna<ProductoValorDto>("Producto", p => p.Nombre, Ancho: 38),
            new("Código", p => p.Codigo, Ancho: 16),
            new("Departamento", p => p.Departamento, Ancho: 18),
            new("Existencia", p => p.Existencia, FormatoCelda.Cantidad, 11),
            new("Unidad", p => ExcelDeReportes.Unidad(p.Unidad), Ancho: 9),
            new("Costo promedio", p => p.CostoPromedioCentavos, FormatoCelda.Dinero, 15),
            new("Vale", p => p.ValorCentavos, FormatoCelda.Dinero, 15),
            new("Se vende", p => p.Activo, Ancho: 9),
        ], v.Productos, "Ningún producto con ese filtro.");
        return LibroExcel.Bytes(libro);
    }
}
