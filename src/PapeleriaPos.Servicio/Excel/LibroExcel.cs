using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Servicio.Excel;

/// <summary>Cómo se lee lo de una celda.</summary>
internal enum FormatoCelda
{
    Texto,

    /// <summary>«1,234».</summary>
    Entero,

    /// <summary>En pesos, con centavos: «$1,234.50» (el valor va en centavos y se escribe en pesos).</summary>
    Dinero,

    /// <summary>«32.5 %» (el valor va como fracción: 0.325).</summary>
    Porcentaje,

    /// <summary>«22/10/2026».</summary>
    Fecha,

    /// <summary>«22/10/2026 14:05».</summary>
    FechaYHora,

    /// <summary>
    /// Piezas, metros, kilos: «1,234» o, con fracción, «1,234.5». Cada celda con su formato: el de Excel con decimales
    /// opcionales («#,##0.###») deja el punto en las enteras («5.»; revisión del PR #134).
    /// </summary>
    Cantidad,
}

/// <summary>Una columna de una tabla: su título, de dónde sale lo de cada renglón, cómo se lee y qué tan ancha va.</summary>
internal sealed record Columna<T>(string Titulo, Func<T, object?> Valor, FormatoCelda Formato = FormatoCelda.Texto, double Ancho = 14);

/// <summary>De dónde es el Excel y quién lo pidió: va arriba de cada hoja.</summary>
/// <param name="Tienda">El nombre de la tienda (Configuración › Tienda; sin él, el de la sucursal).</param>
/// <param name="Persona">Quien entró a la caja y lo exportó.</param>
/// <param name="CuandoLocal">Cuándo (hora de la tienda).</param>
internal sealed record QuienExporta(string Tienda, string Persona, DateTime CuandoLocal)
{
    public static async Task<QuienExporta> DeAsync(PosDbContext db, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        var tienda = (await new RegistroConfiguracion(db).LeerAsync(sucursalId, ct)).NombreTienda
            ?? await db.Sucursales.AsNoTracking().Where(s => s.Id == sucursalId).Select(s => s.Nombre).SingleOrDefaultAsync(ct) ?? "La tienda";
        var persona = await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => u.Nombre).SingleOrDefaultAsync(ct) ?? "—";
        return new QuienExporta(tienda, persona, HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
    }

    /// <summary>«Lo exportó Dueño de prueba el 22/10/2026 a las 14:05.»</summary>
    public string Texto => $"Lo exportó {Persona} el {CuandoLocal.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} a las "
        + $"{CuandoLocal.ToString("HH:mm", CultureInfo.InvariantCulture)}.";
}

/// <summary>
/// Los Excel de los reportes (entrega 9, parte 2, 2026-10-22), con ClosedXML, como el diagnóstico de PapeleriaPos.Herramientas:
/// cada hoja empieza con la tienda, qué es y de qué días, quién lo exportó y cuándo, y sus notas; luego una tabla de Excel (con
/// filtros) con el renglón de los títulos fijo al bajar, el dinero en pesos con centavos, las fechas como fechas y un ancho que
/// se lee. Lo que solo ve quien tiene «Ver costos, utilidad y margen» ni siquiera va en el archivo para los demás: cada reporte
/// arma sus columnas con lo que le mandó el servicio.
/// </summary>
internal static class LibroExcel
{
    private static readonly XLColor Azul = XLColor.FromHtml("#1F4E78");
    private static readonly XLColor Gris = XLColor.FromHtml("#595959");

    public static string FormatoDe(FormatoCelda formato) => formato switch
    {
        FormatoCelda.Entero => "#,##0",
        FormatoCelda.Dinero => "$#,##0.00;-$#,##0.00",
        FormatoCelda.Porcentaje => "0.0%",
        FormatoCelda.Fecha => "dd/mm/yyyy",
        FormatoCelda.FechaYHora => "dd/mm/yyyy hh:mm",
        FormatoCelda.Cantidad => "#,##0",
        _ => "@",
    };

    /// <summary>Una cantidad con fracción (un metro y medio, 0.250 kilos): hasta milésimas.</summary>
    public const string CantidadConFraccion = "#,##0.###";

    /// <summary>Si la cantidad trae fracción: esas celdas llevan <see cref="CantidadConFraccion"/>; las enteras, sin punto.</summary>
    private static bool ConFraccion(object? valor) => valor switch
    {
        decimal m => m != decimal.Truncate(m),
        double x => x != Math.Truncate(x),
        _ => false,
    };

    /// <summary>Un libro nuevo, a nombre del POS.</summary>
    public static XLWorkbook Nuevo()
    {
        var libro = new XLWorkbook();
        libro.Properties.Author = "Papelería POS";
        return libro;
    }

    /// <summary>El libro ya armado, como bytes de un .xlsx.</summary>
    public static byte[] Bytes(XLWorkbook libro)
    {
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        return flujo.ToArray();
    }

    /// <summary>
    /// Arriba de la hoja: la tienda, el título (qué es y de qué días), quién lo exportó y cuándo, y las notas (los filtros, el
    /// piloto, de dónde sale cada cosa). Regresa la fila donde sigue, después de un renglón en blanco.
    /// </summary>
    public static int Encabezado(IXLWorksheet hoja, QuienExporta quien, string titulo, IEnumerable<string?>? notas = null)
    {
        hoja.Cell(1, 1).Value = quien.Tienda;
        hoja.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(Azul);
        hoja.Cell(2, 1).Value = titulo;
        hoja.Cell(2, 1).Style.Font.SetBold().Font.SetFontSize(12);
        var fila = 3;
        foreach (var texto in new[] { quien.Texto }.Concat(notas ?? []).OfType<string>().Where(n => n.Length > 0))
        {
            hoja.Cell(fila, 1).Value = texto;
            hoja.Cell(fila, 1).Style.Font.SetItalic().Font.SetFontColor(Gris);
            fila++;
        }
        return fila + 1;
    }

    /// <summary>
    /// Una tabla de Excel desde <paramref name="fila"/> (su nombre, distinto al de la hoja: «TablaTickets»): los títulos (ese
    /// renglón y los de arriba se quedan fijos al bajar, si
    /// <paramref name="congelar"/>), cada renglón con su formato y el ancho de cada columna. Sin renglones, lo dice
    /// <paramref name="vacia"/>. Regresa la última fila que usó.
    /// </summary>
    public static int Tabla<T>(IXLWorksheet hoja, int fila, string nombre, IReadOnlyList<Columna<T>> columnas, IReadOnlyList<T> renglones, string vacia,
        bool congelar = true)
    {
        for (var c = 0; c < columnas.Count; c++)
        {
            hoja.Cell(fila, c + 1).Value = columnas[c].Titulo;
            if (hoja.Column(c + 1).Width < columnas[c].Ancho)
                hoja.Column(c + 1).Width = columnas[c].Ancho;
        }
        if (congelar)
            hoja.SheetView.FreezeRows(fila);
        if (renglones.Count == 0)
        {
            var titulos = hoja.Range(fila, 1, fila, columnas.Count);
            titulos.Style.Font.SetBold().Fill.SetBackgroundColor(Azul).Font.SetFontColor(XLColor.White);
            hoja.Cell(fila + 1, 1).Value = vacia;
            hoja.Cell(fila + 1, 1).Style.Font.SetItalic().Font.SetFontColor(Gris);
            return fila + 1;
        }
        // Cada valor sin su formato, y el formato después, de toda la columna de una vez: con miles de renglones, darle su
        // formato a cada celda tardaba segundos. Solo las cantidades con fracción (pocas: casi todo es por pieza) llevan el suyo.
        var conFraccion = new List<IXLCell>();
        for (var r = 0; r < renglones.Count; r++)
            for (var c = 0; c < columnas.Count; c++)
            {
                var valor = columnas[c].Valor(renglones[r]);
                var celda = hoja.Cell(fila + 1 + r, c + 1);
                celda.Value = Celda(valor, columnas[c].Formato);
                if (columnas[c].Formato == FormatoCelda.Cantidad && ConFraccion(valor))
                    conFraccion.Add(celda);
            }
        var ultima = fila + renglones.Count;
        for (var c = 0; c < columnas.Count; c++)
            if (columnas[c].Formato != FormatoCelda.Texto)
                hoja.Range(fila + 1, c + 1, ultima, c + 1).Style.NumberFormat.Format = FormatoDe(columnas[c].Formato);
        foreach (var celda in conFraccion)
            celda.Style.NumberFormat.Format = CantidadConFraccion;
        var tabla = hoja.Range(fila, 1, ultima, columnas.Count).CreateTable(nombre);
        tabla.Theme = XLTableTheme.TableStyleMedium2;
        return ultima;
    }

    /// <summary>
    /// Unos renglones de «concepto y su valor» debajo de una tabla (los totales del periodo), con un título en negritas: el valor
    /// en <paramref name="columnaValor"/> y el concepto justo a su izquierda, alineado a la derecha (se lee completo aunque sea
    /// largo: las celdas de su izquierda van vacías). Regresa la última fila.
    /// </summary>
    public static int Totales(IXLWorksheet hoja, int fila, string titulo, int columnaValor,
        IEnumerable<(string Concepto, object? Valor, FormatoCelda Formato, bool Negritas)> renglones)
    {
        var columnaConcepto = Math.Max(1, columnaValor - 1);
        hoja.Cell(fila, columnaConcepto).Value = titulo;
        hoja.Cell(fila, columnaConcepto).Style.Font.SetBold().Font.SetFontColor(Azul).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        foreach (var (concepto, valor, formato, negritas) in renglones)
        {
            fila++;
            hoja.Cell(fila, columnaConcepto).Value = concepto;
            hoja.Cell(fila, columnaConcepto).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            Valor(hoja.Cell(fila, columnaValor), valor, formato);
            if (negritas)
            {
                hoja.Cell(fila, columnaConcepto).Style.Font.SetBold();
                hoja.Cell(fila, columnaValor).Style.Font.SetBold();
            }
        }
        return fila;
    }

    /// <summary>Lo de una celda con su formato: el dinero llega en centavos y se escribe en pesos.</summary>
    public static void Valor(IXLCell celda, object? valor, FormatoCelda formato)
    {
        celda.Value = Celda(valor, formato);
        if (formato != FormatoCelda.Texto && valor is not (null or string))
            celda.Style.NumberFormat.Format = formato == FormatoCelda.Cantidad && ConFraccion(valor) ? CantidadConFraccion : FormatoDe(formato);
    }

    /// <summary>El valor como lo guarda Excel: el dinero, que llega en centavos, en pesos; las fechas, como fechas.</summary>
    private static XLCellValue Celda(object? valor, FormatoCelda formato) =>
        (valor, formato) switch
        {
            (null, _) => Blank.Value,
            (long centavos, FormatoCelda.Dinero) => centavos / 100m,
            (int centavos, FormatoCelda.Dinero) => centavos / 100m,
            (decimal centavos, FormatoCelda.Dinero) => centavos / 100m,
            (string texto, _) => texto,
            (DateTime fecha, _) => fecha,
            (DateOnly dia, _) => dia.ToDateTime(TimeOnly.MinValue),
            (decimal numero, _) => numero,
            (double numero, _) => numero,
            (int entero, _) => entero,
            (long entero, _) => entero,
            (bool si, _) => si ? "Sí" : "No",
            _ => valor.ToString(),
        };

    /// <summary>La parte de un total, como fracción (para el formato de porcentaje); vacía sin total.</summary>
    public static decimal? Parte(long valor, long total) => total > 0 ? Math.Round(valor / (decimal)total, 4) : null;

    /// <summary>Cuánto subió o bajó, como fracción; vacío sin con qué comparar.</summary>
    public static decimal? Cambio(decimal? actual, decimal? anterior) =>
        actual is { } a && anterior is { } b && b > 0 ? Math.Round((a - b) / b, 4) : null;
}
