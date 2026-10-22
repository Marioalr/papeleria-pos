using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>
/// El Excel de los productos (Productos › Productos en Excel, 2026-10-22; en eleventa, «Exportar» e «Importar» del catálogo): lo
/// arma la caja principal con los mismos títulos y el mismo encabezado que los Excel de los reportes, y lo lee de vuelta ya
/// cambiado. Lo que se exporta es exactamente lo que se importa: los títulos y cómo se lee cada celda son de
/// <see cref="ProductosEnExcel"/>.
/// <list type="bullet">
/// <item><b>«Productos»:</b> una fila por variante que se vende, con sus códigos, su nombre, departamento, marca y unidad, su
/// precio y su mayoreo (desde cuántas y a cuánto), su existencia, su mínimo y su máximo y si se vende; el costo y el margen al
/// final, solo para quien los ve. Los códigos van como texto, para que Excel no les quite los ceros.</item>
/// <item><b>«Presentaciones»:</b> las cajas y paquetes de esas variantes, con sus códigos y su precio propio (sin él, se cobran
/// por sus piezas).</item>
/// </list>
/// Al leer: se busca el renglón de los títulos (el encabezado de arriba no cuenta), una fórmula vale lo que calculó Excel, y un
/// renglón vacío no cuenta. Hasta <see cref="ProductosEnExcel.MaximoRenglones"/> por hoja y <see cref="ProductosEnExcel.BytesMaximos"/>.
/// </summary>
internal static class ExcelDeProductos
{
    private const string TablaProductos = "TablaProductos";
    private const string TablaPresentaciones = "TablaPresentaciones";

    /// <summary>Lo más que se descomprime al abrirlo: un .xlsx de 8 MB nunca trae tanto; más es un archivo hecho para tumbar el servicio.</summary>
    private const long BytesDescomprimidosMaximos = 256L * 1024 * 1024;

    /// <summary>Hasta dónde se busca el renglón de los títulos, y hasta qué columna.</summary>
    private const int FilasDeEncabezado = 40;

    private const int ColumnasMaximas = 120;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    // ── Armar ────────────────────────────────────────────────────────────────

    public static byte[] Armar(ProductosParaExcel p, QuienExporta quien)
    {
        using var libro = LibroExcel.Nuevo();
        static string T(CampoDeProducto campo) => ProductosEnExcel.Titulo(campo);

        var hoja = libro.AddWorksheet(ProductosEnExcel.HojaProductos);
        var cuantos = p.Variantes.Count == 1 ? "1 variante" : $"{p.Variantes.Count.ToString("#,##0", Mexico)} variantes";
        var fila = LibroExcel.Encabezado(hoja, quien, $"Productos: {cuantos}",
        [
            p.Filtros,
            p.Variantes.Count == 0 ? "Ningún producto con esos filtros." : null,
            "Una fila por variante que se vende, en el orden de la lista. El precio y el mayoreo van por pieza (o por metro, kilo o litro); el "
            + "mayoreo, desde cuántas. Las cajas y paquetes con sus códigos y su precio van en la hoja «Presentaciones».",
            "Para cambiar precios y datos: cámbialos aquí, guárdalo y en Productos › «Productos en Excel» elige «Importar de Excel»; primero se ve "
            + "qué cambia. Cada producto se reconoce por su código; un código nuevo es un producto nuevo. La existencia"
            + (p.ConCostos ? ", el costo y el margen no se importan." : " no se importa."),
            p.SinExportar == 0 ? null
                : $"Sin {(p.SinExportar == 1 ? "1 kit o cobro de terceros" : $"{p.SinExportar.ToString("#,##0", Mexico)} kits o cobros de terceros")} de la lista: "
                  + "esos se cambian en su ficha.",
        ]);
        var escalas = Math.Max(ProductosEnExcel.EscalasMinimas, p.Variantes.Select(v => v.Mayoreo.Count).DefaultIfEmpty(0).Max());
        var columnas = new List<Columna<VarianteParaExcel>>
        {
            new(T(CampoDeProducto.Codigo), v => v.Codigo, Ancho: 16),
            new(T(CampoDeProducto.OtrosCodigos), v => v.OtrosCodigos.Count == 0 ? null : string.Join(", ", v.OtrosCodigos), Ancho: 18),
            new(T(CampoDeProducto.Producto), v => v.Producto, Ancho: 38),
            new(T(CampoDeProducto.Variante), v => v.Variante.Length == 0 ? null : v.Variante, Ancho: 14),
            new(T(CampoDeProducto.Departamento), v => v.Departamento, Ancho: 18),
            new(T(CampoDeProducto.Marca), v => v.Marca, Ancho: 14),
            new(T(CampoDeProducto.Unidad), v => ProductosEnExcel.Unidad(v.Unidad), Ancho: 9),
            new(T(CampoDeProducto.Precio), v => v.PrecioCentavos, FormatoCelda.Dinero, 11),
        };
        for (var i = 0; i < escalas; i++)
        {
            var cual = i;
            columnas.Add(new(ProductosEnExcel.MayoreoDesde(i + 1), v => cual < v.Mayoreo.Count ? v.Mayoreo[cual].Desde : null, FormatoCelda.Cantidad, 10));
            columnas.Add(new(ProductosEnExcel.MayoreoPrecio(i + 1), v => cual < v.Mayoreo.Count ? v.Mayoreo[cual].PrecioCentavos : null, FormatoCelda.Dinero, 11));
        }
        columnas.AddRange(
        [
            new(T(CampoDeProducto.Existencia), v => v.Existencia, FormatoCelda.Cantidad, 10),
            new(T(CampoDeProducto.Minimo), v => v.Minimo, FormatoCelda.Cantidad, 9),
            new(T(CampoDeProducto.Maximo), v => v.Maximo, FormatoCelda.Cantidad, 9),
            new(T(CampoDeProducto.SeVende), v => v.SeVende, Ancho: 9),
        ]);
        // El costo y el margen, solo para quien los ve: a los demás ni siquiera les llegaron.
        if (p.ConCostos)
        {
            columnas.Add(new(T(CampoDeProducto.CostoPromedio), v => v.CostoPromedioCentavos, FormatoCelda.Dinero, 13));
            columnas.Add(new(T(CampoDeProducto.Margen), v => v.MargenCentesimas is { } m ? m / 10000m : null, FormatoCelda.Porcentaje, 9));
        }
        var ultima = LibroExcel.Tabla(hoja, fila, TablaProductos, columnas, p.Variantes, "");
        ComoTexto(hoja, fila, ultima, 1, 2);

        var deLasPresentaciones = libro.AddWorksheet(ProductosEnExcel.HojaPresentaciones);
        fila = LibroExcel.Encabezado(deLasPresentaciones, quien, "Presentaciones de esos productos",
        [
            p.Filtros,
            p.Presentaciones.Count == 0 ? "Ninguno de esos productos tiene presentaciones (cajas o paquetes)." : null,
            "Las cajas y paquetes de cada producto, con sus códigos y su precio propio. Sin precio propio, se cobran por sus piezas al precio de la hoja "
            + "«Productos». Se reconocen por el código del producto y el nombre de la presentación; una nueva se da de alta con lo que contiene.",
        ]);
        static string P(CampoDePresentacion campo) => ProductosEnExcel.Titulo(campo);
        var propias = p.Presentaciones.Select(x => x.Mayoreo.Count).DefaultIfEmpty(0).Max();
        var deCada = new List<Columna<PresentacionParaExcel>>
        {
            new(P(CampoDePresentacion.CodigoDelProducto), x => x.CodigoDelProducto, Ancho: 18),
            new(P(CampoDePresentacion.Producto), x => x.Producto, Ancho: 38),
            new(P(CampoDePresentacion.Presentacion), x => x.Presentacion, Ancho: 16),
            new(P(CampoDePresentacion.Contiene), x => x.Contiene, FormatoCelda.Cantidad, 10),
            new(P(CampoDePresentacion.Codigos), x => x.Codigos.Count == 0 ? null : string.Join(", ", x.Codigos), Ancho: 18),
            new(P(CampoDePresentacion.Precio), x => x.PrecioCentavos, FormatoCelda.Dinero, 11),
        };
        for (var i = 0; i < propias; i++)
        {
            var cual = i;
            deCada.Add(new(ProductosEnExcel.MayoreoDesde(i + 1), x => cual < x.Mayoreo.Count ? x.Mayoreo[cual].Desde : null, FormatoCelda.Cantidad, 10));
            deCada.Add(new(ProductosEnExcel.MayoreoPrecio(i + 1), x => cual < x.Mayoreo.Count ? x.Mayoreo[cual].PrecioCentavos : null, FormatoCelda.Dinero, 11));
        }
        ultima = LibroExcel.Tabla(deLasPresentaciones, fila, TablaPresentaciones, deCada, p.Presentaciones, "");
        ComoTexto(deLasPresentaciones, fila, ultima, 1, 5);
        return LibroExcel.Bytes(libro);
    }

    /// <summary>
    /// Las columnas de los códigos, como texto (también unos renglones abajo, donde se escribe uno nuevo): así Excel no les quita los
    /// ceros de adelante ni los escribe como 7.5E+12.
    /// </summary>
    private static void ComoTexto(IXLWorksheet hoja, int titulos, int ultima, params int[] columnas)
    {
        foreach (var columna in columnas)
            hoja.Range(titulos + 1, columna, Math.Max(ultima, titulos) + 200, columna).Style.NumberFormat.Format = "@";
    }

    // ── Leer ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// El Excel que se importa, ya leído. Si no se puede (no es un .xlsx, no trae la hoja de los productos, trae demasiados
    /// renglones), el error dice qué hacer.
    /// </summary>
    /// <param name="archivo">Cómo se llama, para los mensajes.</param>
    public static LibroDeProductos Leer(byte[] datos, string archivo)
    {
        var nombre = string.IsNullOrWhiteSpace(archivo) ? "el archivo" : $"«{archivo.Trim()}»";
        if (datos.Length == 0)
            throw new ArgumentException($"{Mayuscula(nombre)} está vacío.");
        if (datos.Length > ProductosEnExcel.BytesMaximos)
            throw new ArgumentException($"{Mayuscula(nombre)} pesa {Megas(datos.Length)}: se importan archivos de hasta {Megas(ProductosEnExcel.BytesMaximos)} "
                + "(unos 10,000 productos). Pártelo en dos, o exporta un departamento a la vez.");
        RevisarComprimido(datos, nombre);
        XLWorkbook libro;
        try
        {
            libro = new XLWorkbook(new MemoryStream(datos));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException($"No se pudo abrir {nombre} como libro de Excel: ábrelo en Excel y guárdalo como «Libro de Excel (*.xlsx)», sin "
                + "contraseña. Luego vuelve a importarlo.");
        }
        using (libro)
        {
            var hojas = libro.Worksheets.ToList();
            var productos = hojas.Where(h => Texto.Normalizar(h.Name) == Texto.Normalizar(ProductosEnExcel.HojaProductos)).Concat(hojas)
                .Select(h => (Hoja: h, Fila: FilaDeTitulos(h, t => ProductosEnExcel.CampoDe(t) == CampoDeProducto.Codigo)))
                .FirstOrDefault(x => x.Fila is not null);
            if (productos.Hoja is null)
                throw new ArgumentException($"{Mayuscula(nombre)} no es el Excel de los productos: ninguna hoja trae la columna «{ProductosEnExcel.Titulo(CampoDeProducto.Codigo)}». "
                    + "Exporta los productos con «Exportar a Excel» y cambia ese archivo.");
            var presentaciones = hojas.Where(h => h != productos.Hoja && Texto.Normalizar(h.Name) == Texto.Normalizar(ProductosEnExcel.HojaPresentaciones))
                .Select(h => (Hoja: h, Fila: FilaDeTitulos(h, t => ProductosEnExcel.CampoDePresentacionDe(t) == CampoDePresentacion.CodigoDelProducto)))
                .FirstOrDefault(x => x.Fila is not null);
            return new LibroDeProductos(
                LeerHoja(productos.Hoja, productos.Fila!.Value, ProductosEnExcel.CampoDe),
                presentaciones.Hoja is null ? null : LeerHoja(presentaciones.Hoja, presentaciones.Fila!.Value, ProductosEnExcel.CampoDePresentacionDe));
        }
    }

    /// <summary>El renglón de los títulos: el primero (de los de arriba) que trae el de esa columna.</summary>
    private static int? FilaDeTitulos(IXLWorksheet hoja, Func<string, bool> esElTitulo)
    {
        var ultima = Math.Min(hoja.LastRowUsed()?.RowNumber() ?? 0, FilasDeEncabezado);
        var columnas = Math.Min(hoja.LastColumnUsed()?.ColumnNumber() ?? 0, ColumnasMaximas);
        for (var fila = 1; fila <= ultima; fila++)
        {
            for (var columna = 1; columna <= columnas; columna++)
            {
                var celda = hoja.Cell(fila, columna);
                if (celda.CachedValue.IsText && esElTitulo(celda.CachedValue.GetText()))
                    return fila;
            }
        }
        return null;
    }

    private static HojaLeida<T> LeerHoja<T>(IXLWorksheet hoja, int filaTitulos, Func<string, T?> campoDe) where T : struct, Enum
    {
        var ultimaColumna = Math.Min(hoja.LastColumnUsed()?.ColumnNumber() ?? 0, ColumnasMaximas);
        var columnas = new Dictionary<T, int>();
        var desde = new Dictionary<int, int>();
        var precio = new Dictionary<int, int>();
        var sinUsar = new List<string>();
        for (var columna = 1; columna <= ultimaColumna; columna++)
        {
            var titulo = LecturaDeCeldas.Limpio(hoja.Cell(filaTitulos, columna).GetFormattedString(CultureInfo.InvariantCulture));
            if (titulo.Length == 0)
                continue;
            if (campoDe(titulo) is { } campo)
            {
                if (!columnas.TryAdd(campo, columna))
                    sinUsar.Add($"{titulo} (repetida)");
            }
            else if (ProductosEnExcel.MayoreoDe(titulo) is { } escala)
            {
                if (!(escala.Precio ? precio : desde).TryAdd(escala.Numero, columna))
                    sinUsar.Add($"{titulo} (repetida)");
            }
            else
                sinUsar.Add(titulo);
        }
        // Una escala solo cuenta con sus dos columnas; la que trae una sola no se usa.
        var escalas = desde.Keys.Intersect(precio.Keys).Order().ToList();
        foreach (var numero in desde.Keys.Union(precio.Keys).Except(escalas).Order())
            sinUsar.Add(desde.ContainsKey(numero) ? ProductosEnExcel.MayoreoDesde(numero) : ProductosEnExcel.MayoreoPrecio(numero));

        var renglones = new List<RenglonLeido<T>>();
        var ultimaFila = hoja.LastRowUsed()?.RowNumber() ?? 0;
        for (var fila = filaTitulos + 1; fila <= ultimaFila; fila++)
        {
            var celdas = new Dictionary<T, CeldaLeida>();
            foreach (var (campo, columna) in columnas)
            {
                var celda = Leer(hoja.Cell(fila, columna));
                if (!celda.EsVacia)
                    celdas[campo] = celda;
            }
            var mayoreo = new Dictionary<int, (CeldaLeida Desde, CeldaLeida Precio)>();
            foreach (var numero in escalas)
            {
                var par = (Leer(hoja.Cell(fila, desde[numero])), Leer(hoja.Cell(fila, precio[numero])));
                if (!par.Item1.EsVacia || !par.Item2.EsVacia)
                    mayoreo[numero] = par;
            }
            if (celdas.Count == 0 && mayoreo.Count == 0)
                continue;
            if (renglones.Count == ProductosEnExcel.MaximoRenglones)
                throw new ArgumentException($"La hoja «{hoja.Name}» trae más de {ProductosEnExcel.MaximoRenglones.ToString("#,##0", Mexico)} renglones: se importan "
                    + $"hasta {ProductosEnExcel.MaximoRenglones.ToString("#,##0", Mexico)} a la vez. Pártelo en dos archivos, o exporta un departamento a la vez.");
            renglones.Add(new RenglonLeido<T>(fila, celdas, mayoreo));
        }
        return new HojaLeida<T>(hoja.Name, columnas.Keys.ToHashSet(), escalas, renglones, sinUsar);
    }

    /// <summary>
    /// Lo de una celda: una fórmula vale lo que calculó Excel la última vez que se guardó (si no lo trae, se calcula aquí; si no se
    /// puede, se dice). Una fecha se lee como se ve: en un precio, no es un número.
    /// </summary>
    private static CeldaLeida Leer(IXLCell celda)
    {
        const string SinCalcular = "tiene una fórmula que no se pudo calcular: ábrelo en Excel, guárdalo y vuelve a importarlo.";
        XLCellValue valor;
        try
        {
            valor = celda.CachedValue;
            // Sin lo que calculó Excel (un archivo que armó otro programa): se calcula aquí. Si aquí da error (una función que
            // solo conoce Excel), no es que Excel lo tenga mal: se pide guardarlo en Excel.
            if (celda.HasFormula && valor.IsBlank)
            {
                valor = celda.Value;
                if (valor.IsError)
                    return CeldaLeida.DeError(SinCalcular);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CeldaLeida.DeError(SinCalcular);
        }
        return valor.Type switch
        {
            XLDataType.Blank => CeldaLeida.Vacia,
            XLDataType.Text => CeldaLeida.DeTexto(valor.GetText()),
            XLDataType.Number => CeldaLeida.DeNumero(valor.GetNumber()),
            XLDataType.Boolean => CeldaLeida.DeSiNo(valor.GetBoolean()),
            XLDataType.Error => CeldaLeida.DeError(Error(valor.GetError())),
            _ => CeldaLeida.DeTexto(celda.GetFormattedString(Mexico)),
        };
    }

    /// <summary>Cómo se ve el error en Excel.</summary>
    private static string Error(XLError error) => error switch
    {
        XLError.NullValue => "#NULL!",
        XLError.DivisionByZero => "#DIV/0!",
        XLError.IncompatibleValue => "#VALUE!",
        XLError.CellReference => "#REF!",
        XLError.NameNotRecognized => "#NAME?",
        XLError.NumberInvalid => "#NUM!",
        _ => "#N/A",
    };

    /// <summary>Que sea un .xlsx (un zip) y que no traiga más de lo que se espera al descomprimirlo.</summary>
    private static void RevisarComprimido(byte[] datos, string nombre)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(datos), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entrada in zip.Entries)
            {
                total += entrada.Length;
                if (total > BytesDescomprimidosMaximos)
                    throw new ArgumentException($"{Mayuscula(nombre)} trae demasiado adentro para ser el Excel de los productos.");
            }
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException($"{Mayuscula(nombre)} no es un libro de Excel (.xlsx): ábrelo en Excel y guárdalo como «Libro de Excel (*.xlsx)». "
                + "Luego vuelve a importarlo.");
        }
    }

    private static string Megas(long bytes) => $"{(bytes / 1024m / 1024m).ToString("0.#", Mexico)} MB";

    private static string Mayuscula(string texto) => texto.Length == 0 ? texto : char.ToUpper(texto[0], Mexico) + texto[1..];
}
