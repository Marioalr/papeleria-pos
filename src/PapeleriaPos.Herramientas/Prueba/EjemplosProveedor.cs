using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PapeleriaPos.Herramientas.Prueba;

/// <summary>
/// Una factura (XML) y una nota (PDF) de un proveedor de la base, con sus productos, para probar las compras sin
/// documentos reales. La factura trae códigos de barras (llegan ligados), una clave del proveedor (llega como «¿Es…?» y
/// se confirma una vez) y un producto que no está en el catálogo (llega precargado como nuevo); la nota repite esa clave
/// (ya llega ligada) y trae los precios sin IVA, como muchas notas.
/// </summary>
public static class EjemplosProveedor
{
    private static readonly CultureInfo Invariante = CultureInfo.InvariantCulture;

    private sealed record Producto(Guid VarianteId, string Nombre, string? Codigo, string? Clave, long CostoCentavos);

    public static async Task<List<string>> EscribirAsync(PosDbContext db, string carpeta, CancellationToken ct = default)
    {
        Directory.CreateDirectory(carpeta);
        var candidatos = await (from pv in db.ProveedoresVariantes
                                join v in db.Variantes on pv.VarianteId equals v.Id
                                join p in db.Productos on v.ProductoId equals p.Id
                                where v.Activa && p.Activo && p.Tipo == TipoProducto.Mercancia && v.UnidadBase == UnidadBase.Pieza
                                      && pv.UltimoCostoCentavos != null && pv.UltimoCostoCentavos > 0
                                select new { pv.ProveedorId, pv.VarianteId, pv.CodigoProveedor, Costo = pv.UltimoCostoCentavos!.Value, p.Nombre, v.Descripcion })
            .ToListAsync(ct);
        var codigos = (await db.CodigosBarras.AsNoTracking().Where(c => c.Presentacion!.EsBase && c.Origen == OrigenCodigo.Fabrica)
                .Select(c => new { c.Presentacion!.VarianteId, c.Codigo }).ToListAsync(ct))
            .GroupBy(c => c.VarianteId).ToDictionary(g => g.Key, g => g.First().Codigo);
        var elegido = candidatos.Where(c => codigos.ContainsKey(c.VarianteId)).GroupBy(c => c.ProveedorId).OrderByDescending(g => g.Count()).FirstOrDefault()
            ?? throw new InvalidOperationException("La base no tiene un proveedor con productos con código para los ejemplos.");
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == elegido.Key, ct);
        var productos = elegido.OrderBy(c => c.Nombre, StringComparer.Ordinal).ThenBy(c => c.Descripcion, StringComparer.Ordinal).Take(4)
            .Select(c => new Producto(c.VarianteId, string.IsNullOrWhiteSpace(c.Descripcion) ? c.Nombre : $"{c.Nombre} {c.Descripcion}",
                codigos[c.VarianteId], c.CodigoProveedor ?? $"PRV-{c.VarianteId.ToString()[..6].ToUpperInvariant()}", c.Costo))
            .ToList();
        if (productos.Count < 4)
            throw new InvalidOperationException("Hacen falta al menos cuatro productos del mismo proveedor para los ejemplos.");

        // Factura: tres con código de barras, uno con la clave del proveedor (se confirma la primera vez) y uno nuevo.
        var nuevo = new Producto(Guid.Empty, "Sobre de burbuja 12x18", NuevoCodigo(codigos.Values), null, 950);
        var factura = new (Producto P, int Cantidad, bool PorClave)[]
        {
            (productos[0], 12, false), (productos[1], 24, false), (productos[2], 6, false), (productos[3], 10, true), (nuevo, 8, false),
        };
        await File.WriteAllTextAsync(Path.Combine(carpeta, "Factura de ejemplo.xml"), Factura(proveedor.Nombre, factura), Encoding.UTF8, ct);

        // Nota: repite la clave que se ligó con la factura (ya llega ligada) y trae precios sin IVA.
        var nota = new (Producto P, int Cantidad, bool PorClave)[] { (productos[3], 5, true), (productos[0], 6, false), (productos[1], 12, true) };
        await File.WriteAllBytesAsync(Path.Combine(carpeta, "Nota de ejemplo.pdf"), Nota(proveedor.Nombre, nota), ct);

        await File.WriteAllTextAsync(Path.Combine(carpeta, "Léeme.txt"), Leeme(proveedor.Nombre, factura, nota), Encoding.UTF8, ct);
        return [$"Factura de ejemplo.xml, Nota de ejemplo.pdf y Léeme.txt de «{proveedor.Nombre}» en {Path.GetFullPath(carpeta)}"];
    }

    /// <summary>Un código de barras de fábrica que no está en la base, para el producto nuevo de la factura.</summary>
    private static string NuevoCodigo(IEnumerable<string> existentes)
    {
        var ocupados = existentes.ToHashSet();
        for (var i = 1; ; i++)
        {
            var codigo = Nucleo.Catalogo.Ean13.Completar($"750999{i:D6}");
            if (!ocupados.Contains(codigo))
                return codigo;
        }
    }

    private static decimal SinIva(long costoCentavos) => Math.Round(costoCentavos / 116m, 2);

    private static string Factura(string proveedor, IReadOnlyList<(Producto P, int Cantidad, bool PorClave)> renglones)
    {
        XNamespace cfdi = "http://www.sat.gob.mx/cfd/4";
        XNamespace tfd = "http://www.sat.gob.mx/TimbreFiscalDigital";
        var conceptos = renglones.Select(r =>
        {
            var unitario = SinIva(r.P.CostoCentavos);
            var importe = unitario * r.Cantidad;
            var iva = Math.Round(importe * 0.16m, 2);
            return (Importe: importe, Iva: iva, Xml: new XElement(cfdi + "Concepto",
                new XAttribute("ClaveProdServ", "44121700"), new XAttribute("NoIdentificacion", r.PorClave ? r.P.Clave! : r.P.Codigo!),
                new XAttribute("Cantidad", r.Cantidad), new XAttribute("ClaveUnidad", "H87"), new XAttribute("Unidad", "PIEZA"),
                new XAttribute("Descripcion", r.P.Nombre.ToUpperInvariant()), new XAttribute("ValorUnitario", unitario.ToString("0.00", Invariante)),
                new XAttribute("Importe", importe.ToString("0.00", Invariante)), new XAttribute("ObjetoImp", "02"),
                new XElement(cfdi + "Impuestos", new XElement(cfdi + "Traslados", new XElement(cfdi + "Traslado",
                    new XAttribute("Base", importe.ToString("0.00", Invariante)), new XAttribute("Impuesto", "002"), new XAttribute("TipoFactor", "Tasa"),
                    new XAttribute("TasaOCuota", "0.160000"), new XAttribute("Importe", iva.ToString("0.00", Invariante)))))));
        }).ToList();
        var subtotal = conceptos.Sum(c => c.Importe);
        var total = subtotal + conceptos.Sum(c => c.Iva);
        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(cfdi + "Comprobante",
            new XAttribute(XNamespace.Xmlns + "cfdi", cfdi), new XAttribute(XNamespace.Xmlns + "tfd", tfd), new XAttribute("Version", "4.0"),
            new XAttribute("Serie", "A"), new XAttribute("Folio", "5512"), new XAttribute("Fecha", "2026-10-01T10:00:00"),
            new XAttribute("SubTotal", subtotal.ToString("0.00", Invariante)), new XAttribute("Moneda", "MXN"),
            new XAttribute("Total", total.ToString("0.00", Invariante)), new XAttribute("TipoDeComprobante", "I"),
            new XElement(cfdi + "Emisor", new XAttribute("Rfc", "EJE010101AB1"), new XAttribute("Nombre", proveedor.ToUpperInvariant()),
                new XAttribute("RegimenFiscal", "601")),
            new XElement(cfdi + "Receptor", new XAttribute("Rfc", "XAXX010101000"), new XAttribute("Nombre", "PAPELERIA"), new XAttribute("UsoCFDI", "G01")),
            new XElement(cfdi + "Conceptos", conceptos.Select(c => c.Xml)),
            new XElement(cfdi + "Complemento", new XElement(tfd + "TimbreFiscalDigital", new XAttribute("Version", "1.1"),
                new XAttribute("UUID", "0E1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B")))));
        return xml.Declaration + Environment.NewLine + xml;
    }

    private static byte[] Nota(string proveedor, IReadOnlyList<(Producto P, int Cantidad, bool PorClave)> renglones)
    {
        var builder = new PdfDocumentBuilder();
        var fuente = builder.AddStandard14Font(Standard14Font.Helvetica);
        var negrita = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var pagina = builder.AddPage(PageSize.Letter);
        void Texto(double x, double y, string texto, bool fuerte = false) => pagina.AddText(Ascii(texto), 9, new PdfPoint(x, y), fuerte ? negrita : fuente);

        Texto(40, 740, proveedor.ToUpperInvariant(), fuerte: true);
        Texto(40, 726, "NOTA DE REMISION 8834");
        Texto(400, 726, "FECHA: 01/10/2026");
        var columnas = new[] { (40d, "CLAVE"), (140d, "DESCRIPCION"), (365d, "CANT"), (400d, "UNIDAD"), (450d, "P.UNIT"), (520d, "IMPORTE") };
        foreach (var (x, titulo) in columnas)
            Texto(x, 700, titulo, fuerte: true);
        var y = 682d;
        var subtotal = 0m;
        foreach (var (p, cantidad, porClave) in renglones)
        {
            var unitario = SinIva(p.CostoCentavos);
            var importe = unitario * cantidad;
            subtotal += importe;
            Texto(40, y, porClave ? p.Clave! : p.Codigo!);
            Texto(140, y, p.Nombre.Length > 38 ? p.Nombre[..38] : p.Nombre);
            Texto(365, y, cantidad.ToString(Invariante));
            Texto(400, y, "PZA");
            Texto(450, y, unitario.ToString("#,##0.00", Invariante));
            Texto(520, y, importe.ToString("#,##0.00", Invariante));
            y -= 16;
        }
        var iva = Math.Round(subtotal * 0.16m, 2);
        Texto(450, y - 20, "SUBTOTAL");
        Texto(520, y - 20, subtotal.ToString("#,##0.00", Invariante));
        Texto(450, y - 36, "IVA 16%");
        Texto(520, y - 36, iva.ToString("#,##0.00", Invariante));
        Texto(450, y - 52, "TOTAL", fuerte: true);
        Texto(520, y - 52, (subtotal + iva).ToString("$#,##0.00", Invariante), fuerte: true);
        return builder.Build();
    }

    private static string Leeme(string proveedor, IReadOnlyList<(Producto P, int Cantidad, bool PorClave)> factura,
        IReadOnlyList<(Producto P, int Cantidad, bool PorClave)> nota)
    {
        var t = new StringBuilder();
        t.AppendLine("PAPELERÍA POS · PRUEBA CON DATOS FICTICIOS");
        t.AppendLine();
        t.AppendLine("Usuarios de prueba (usuario / PIN):");
        foreach (var (usuario, nombre, _, pin) in PreparacionCaja.UsuariosDePrueba)
            t.AppendLine($"  {usuario} / {pin}  ({nombre})");
        t.AppendLine();
        t.AppendLine("Clientes registrados de prueba (CTRL+L en la venta, por nombre o teléfono):");
        foreach (var (nombre, telefono, _, descuento, mayoreo, porRevisar, credito) in PreparacionPrueba.ClientesDePrueba)
        {
            var privilegios = Nucleo.Clientes.ClienteRegistrado.Privilegios(descuento, mayoreo);
            t.AppendLine($"  {nombre}{(telefono.Length > 0 ? $" · tel. {telefono}" : "")}{(privilegios.Length > 0 ? $" · {privilegios}" : "")}"
                + (credito > 0 ? $" · crédito hasta {AlertasTelegram.Dinero(credito)} a 30 días" : "")
                + (nombre == PreparacionPrueba.ConDeuda
                    ? $" · debe {AlertasTelegram.Dinero(PreparacionPrueba.DeudaCentavos)} de hace {PreparacionPrueba.DiasDeLaDeuda} días (vencido)" : "")
                + (porRevisar ? " · venía de los clientes especiales: por revisar" : ""));
        }
        t.AppendLine("  Para fiar: elige al cliente con CTRL+L y en F12 usa «A crédito». Para abonar: CTRL+B.");
        t.AppendLine();
        t.AppendLine("La guía de la prueba está dentro de la caja: botón «Guía de la prueba» en la franja de arriba.");
        t.AppendLine();
        t.AppendLine($"Para las tareas de compras, el proveedor es «{proveedor}».");
        t.AppendLine();
        t.AppendLine("1) «Factura de ejemplo.xml»: como cajero, recibe esta mercancía (escanea o busca cada producto y cuenta):");
        foreach (var (p, cantidad, _) in factura)
            t.AppendLine($"     {cantidad,3} piezas de {p.Nombre}{(p.Codigo is { } c ? $" (código {c})" : "")}"
                + (p.VarianteId == Guid.Empty ? "  ← no está en el catálogo: al escribir su código, cuéntalo como nuevo" : ""));
        t.AppendLine("   Si quieres ver una diferencia, cuenta uno menos de alguno. Después, como encargado, carga el XML en la compra:");
        t.AppendLine("   revisa lo precargado (confirma el que dice «¿Es…?», corrige el producto nuevo), dalo de alta y cierra la compra.");
        t.AppendLine();
        t.AppendLine("2) «Nota de ejemplo.pdf»: en otra compra, recibe:");
        foreach (var (p, cantidad, _) in nota)
            t.AppendLine($"     {cantidad,3} piezas de {p.Nombre}{(p.Codigo is { } c ? $" (código {c})" : "")}");
        t.AppendLine("   Después, como encargado, carga el PDF: sus precios vienen sin IVA y el sistema reparte el IVA.");
        return t.ToString();
    }

    /// <summary>La fuente estándar del PDF no trae acentos ni ñ: como en muchas notas, va en mayúsculas sin acentos.</summary>
    private static string Ascii(string texto)
    {
        var sb = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c is >= ' ' and <= '~' ? c : ' ');
        }
        return sb.ToString();
    }
}
