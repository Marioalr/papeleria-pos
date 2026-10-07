using System.Xml;
using System.Xml.Linq;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos.Documentos;

/// <summary>
/// El XML de una factura (CFDI 3.3 o 4.0). Cada concepto trae su clave del proveedor (NoIdentificacion), cantidad,
/// unidad e importe; el costo de cada renglón es lo que se paga: importe − descuento + impuestos trasladados (IVA, IEPS).
/// </summary>
public static class LectorCfdi
{
    private static readonly XNamespace Timbre = "http://www.sat.gob.mx/TimbreFiscalDigital";

    public static DocumentoLeido Leer(byte[] contenido)
    {
        XDocument xml;
        try
        {
            // Sin DTD: un XML que llega de fuera no expande entidades.
            using var lector = XmlReader.Create(new MemoryStream(contenido), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            xml = XDocument.Load(lector);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("El XML está dañado o no es una factura.");
        }
        var raiz = xml.Root;
        if (raiz is null || raiz.Name.LocalName != "Comprobante" || !raiz.Name.NamespaceName.StartsWith("http://www.sat.gob.mx/cfd/", StringComparison.Ordinal))
            throw new InvalidOperationException("El XML no es una factura (CFDI).");
        var cfd = raiz.Name.Namespace;
        if (Texto(raiz, "TipoDeComprobante") is { } tipo && tipo != "I")
            throw new InvalidOperationException(tipo == "E"
                ? "Es una nota de crédito, no una factura de compra."
                : $"Es un comprobante de tipo «{tipo}», no una factura de compra.");

        var avisos = new List<string>();
        var tipoCambio = 1m;
        if (Texto(raiz, "Moneda") is { } moneda && moneda is not "MXN" and not "XXX")
        {
            tipoCambio = Numero(raiz, "TipoCambio") ?? throw new InvalidOperationException($"La factura es en {moneda} y no trae el tipo de cambio.");
            avisos.Add($"La factura es en {moneda}: se pasó a pesos al tipo de cambio {tipoCambio}.");
        }

        var lineas = new List<LineaLeida>();
        foreach (var concepto in raiz.Elements(cfd + "Conceptos").Elements(cfd + "Concepto"))
        {
            var descripcion = Texto(concepto, "Descripcion") ?? "";
            var cantidad = Math.Round(Numero(concepto, "Cantidad") ?? 0, 3, MidpointRounding.AwayFromZero);
            if (cantidad <= 0)
            {
                avisos.Add($"«{descripcion}» no trae cantidad: no se tomó.");
                continue;
            }
            var traslados = concepto.Elements(cfd + "Impuestos").Elements(cfd + "Traslados").Elements(cfd + "Traslado").Sum(t => Numero(t, "Importe") ?? 0);
            var pesos = (Numero(concepto, "Importe") ?? 0) - (Numero(concepto, "Descuento") ?? 0) + traslados;
            lineas.Add(new LineaLeida(Texto(concepto, "NoIdentificacion"), descripcion, Texto(concepto, "Unidad") ?? Texto(concepto, "ClaveUnidad"), cantidad,
                LectorDocumentos.Centavos(pesos * tipoCambio)));
        }
        if (lineas.Count == 0)
            throw new InvalidOperationException("La factura no trae conceptos.");

        var emisor = raiz.Element(cfd + "Emisor");
        var folio = $"{Texto(raiz, "Serie")}{Texto(raiz, "Folio")}";
        return new DocumentoLeido(TipoDocumentoProveedor.Xml, folio.Length == 0 ? null : folio,
            raiz.Descendants(Timbre + "TimbreFiscalDigital").Select(t => Texto(t, "UUID")).FirstOrDefault(u => u is not null)?.ToUpperInvariant(),
            emisor is null ? null : Texto(emisor, "Rfc")?.ToUpperInvariant(), emisor is null ? null : Texto(emisor, "Nombre"),
            Numero(raiz, "Total") is { } total ? LectorDocumentos.Centavos(total * tipoCambio) : null, lineas, avisos);
    }

    private static string? Texto(XElement elemento, string atributo) =>
        elemento.Attribute(atributo)?.Value.Trim() is { Length: > 0 } valor ? valor : null;

    private static decimal? Numero(XElement elemento, string atributo) => LectorDocumentos.Decimal(Texto(elemento, atributo));
}
