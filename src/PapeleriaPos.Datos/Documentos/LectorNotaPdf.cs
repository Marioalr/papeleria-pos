using System.Globalization;
using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Inventario;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace PapeleriaPos.Datos.Documentos;

/// <summary>
/// El PDF de una nota o factura. Cada proveedor tiene su formato, así que se lee el texto por renglones y se toma como
/// producto el que termina en precio unitario e importe y tiene una cantidad que cuadra con ellos (cantidad × precio =
/// importe). La clave es la primera palabra si parece clave; lo demás es la descripción. Si el total de la nota no es la
/// suma de los renglones (precios sin IVA, descuento general), la diferencia se reparte en proporción.
/// </summary>
public static partial class LectorNotaPdf
{
    /// <summary>Palabras de los renglones de totales: no son productos.</summary>
    private static readonly HashSet<string> Totales =
        ["SUBTOTAL", "SUB-TOTAL", "TOTAL", "IVA", "I.V.A.", "I.V.A", "DESCUENTO", "DESCUENTOS", "IMPUESTO", "IMPUESTOS", "SALDO", "ANTICIPO", "CAMBIO",
         "PAGO", "PAGOS", "RETENCION", "IEPS", "LETRA"];

    private static readonly HashSet<string> Unidades =
        ["PZA", "PZ", "PZS", "PIEZA", "PIEZAS", "CAJA", "CAJAS", "CJA", "CJ", "PAQ", "PAQUETE", "PQT", "KG", "KGS", "KILO", "MT", "MTS", "METRO", "M", "ROLLO",
         "RLL", "JGO", "JUEGO", "BLS", "BOLSA", "H87", "XBX", "KGM", "MTR", "LT", "LTO", "LITRO", "SET", "DOC", "DOCENA", "BLISTER", "GRUESA", "CTO",
         "CIENTO", "UND", "UNIDAD", "PAR", "E48", "ACT"];

    public static DocumentoLeido Leer(byte[] contenido)
    {
        List<List<string>> renglones;
        try
        {
            using var pdf = PdfDocument.Open(contenido);
            renglones = pdf.GetPages().SelectMany(Renglones).ToList();
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new InvalidOperationException("El PDF tiene contraseña: quítasela o captura la nota a mano.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("El PDF está dañado o no se pudo leer.");
        }
        if (renglones.Count == 0)
            throw new InvalidOperationException("El PDF no trae texto (parece una imagen escaneada): captura la nota a mano.");

        var lineas = new List<LineaLeida>();
        decimal? total = null;
        foreach (var palabras in renglones)
        {
            if (palabras.Any(p => Totales.Contains(Normal(p).TrimEnd(':'))))
            {
                if (palabras.Any(p => Normal(p).TrimEnd(':') == "TOTAL") && palabras.Select(Numero).LastOrDefault(n => n is not null) is { } t)
                    total = t;
                continue;
            }
            if (Producto(palabras) is { } linea)
                lineas.Add(linea);
        }
        if (lineas.Count == 0)
            throw new InvalidOperationException("No encontré los productos en el PDF (cantidad, precio e importe): captura la nota a mano.");

        var avisos = new List<string> { "Se leyó del PDF: revisa que cada renglón tenga su cantidad e importe." };
        var suma = lineas.Sum(l => l.ImporteCentavos);
        if (total is { } pesos && LectorDocumentos.Centavos(pesos) is var totalCentavos and > 0
            && Math.Abs(totalCentavos - suma) > Math.Max(100, suma / 200))
        {
            lineas = Repartir(lineas, totalCentavos);
            avisos.Add($"Los renglones suman {Dinero(suma)} y la nota dice total {Dinero(totalCentavos)} (IVA o descuento): la diferencia se "
                + "repartió en proporción en cada renglón.");
        }
        return new DocumentoLeido(TipoDocumentoProveedor.Pdf, null, null, null, null, total is { } x ? LectorDocumentos.Centavos(x) : null, lineas, avisos);
    }

    /// <summary>Las palabras de cada renglón de la página, de arriba abajo y de izquierda a derecha.</summary>
    private static IEnumerable<List<string>> Renglones(Page pagina)
    {
        var palabras = pagina.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();
        var actual = new List<Word>();
        foreach (var palabra in palabras)
        {
            if (actual.Count > 0 && Math.Abs(actual[0].BoundingBox.Bottom - palabra.BoundingBox.Bottom) > Math.Max(2, palabra.BoundingBox.Height / 2))
            {
                yield return actual.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text).ToList();
                actual = [];
            }
            actual.Add(palabra);
        }
        if (actual.Count > 0)
            yield return actual.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text).ToList();
    }

    /// <summary>
    /// Un renglón es producto si termina en dos números (precio unitario e importe) y tiene una cantidad que cuadra:
    /// la anterior a ellos, la del principio o cualquier otra del renglón, en ese orden.
    /// </summary>
    private static LineaLeida? Producto(List<string> palabras)
    {
        var finales = new List<(int Indice, decimal Valor)>();
        for (var i = palabras.Count - 1; i >= 0; i--)
        {
            if (palabras[i] == "$")
                continue;
            if (Numero(palabras[i]) is not { } n)
                break;
            finales.Insert(0, (i, n));
        }
        if (finales.Count < 2)
            return null;
        var (iImporte, importe) = finales[^1];
        var (iPrecio, precio) = finales[^2];
        if (importe <= 0 || precio <= 0)
            return null;

        var candidatos = new List<(int Indice, decimal Valor)>();
        if (finales.Count >= 3)
            candidatos.Add(finales[^3]);
        candidatos.AddRange(palabras.Select((p, i) => (i, Numero(p))).Where(x => x.Item2 is > 0 && x.i < finales[0].Indice).Select(x => (x.i, x.Item2!.Value)));
        var tolerancia = Math.Max(0.05m, importe * 0.005m);
        var cuadra = candidatos.Where(c => Math.Abs(c.Valor * precio - importe) <= tolerancia).ToList();
        // Sin cantidad que cuadre, si el precio es el importe se toma una pieza (notas sin columna de cantidad).
        var (iCantidad, valorCantidad) = cuadra.Count > 0 ? cuadra[0] : Math.Abs(precio - importe) <= tolerancia ? (-1, 1m) : (-2, 0m);
        if (valorCantidad <= 0 || decimal.Round(valorCantidad, 3) != valorCantidad)
            return null;

        var resto = palabras.Select((p, i) => (p, i)).Where(x => x.i != iImporte && x.i != iPrecio && x.i != iCantidad && x.p != "$").ToList();
        string? unidad = null;
        if (resto.FirstOrDefault(x => Unidades.Contains(Normal(x.p).TrimEnd('.')) && (iCantidad < 0 || Math.Abs(x.i - iCantidad) == 1 || x.i == iPrecio - 1)) is { p: not null } u)
        {
            unidad = u.p;
            resto.Remove(u);
        }
        string? codigo = null;
        if (resto.Count > 1 && Clave().IsMatch(Normal(resto[0].p)) && resto[0].p.Any(char.IsAsciiDigit))
        {
            codigo = resto[0].p;
            resto.RemoveAt(0);
        }
        var descripcion = string.Join(" ", resto.Select(x => x.p)).Trim();
        if (descripcion.Length == 0 || !descripcion.Any(char.IsLetter))
            return null;
        return new LineaLeida(codigo, descripcion, unidad, valorCantidad, LectorDocumentos.Centavos(importe));
    }

    /// <summary>Lleva los importes al total en proporción; el último renglón se queda con el redondeo.</summary>
    private static List<LineaLeida> Repartir(List<LineaLeida> lineas, long total)
    {
        var suma = lineas.Sum(l => l.ImporteCentavos);
        var repartidas = lineas.Select(l => l with { ImporteCentavos = Redondeo.Dividir((Int128)l.ImporteCentavos * total, suma) }).ToList();
        var diferencia = total - repartidas.Sum(l => l.ImporteCentavos);
        repartidas[^1] = repartidas[^1] with { ImporteCentavos = repartidas[^1].ImporteCentavos + diferencia };
        return repartidas;
    }

    /// <summary>"1,234.50", "$270.00" o "3": como número. Lo demás (claves con letras, fechas), nada.</summary>
    private static decimal? Numero(string palabra)
    {
        var limpio = palabra.TrimStart('$').TrimEnd(',', ';');
        if (!Cifra().IsMatch(limpio))
            return null;
        return decimal.TryParse(limpio.Replace(",", ""), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static string Normal(string palabra) => palabra.ToUpperInvariant();

    private static string Dinero(long centavos) => (centavos / 100m).ToString("$#,##0.00", CultureInfo.GetCultureInfo("es-MX"));

    [GeneratedRegex(@"^-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$")]
    private static partial Regex Cifra();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-./_]{1,24}$")]
    private static partial Regex Clave();
}
