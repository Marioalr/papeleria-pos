using System.Globalization;
using System.Text;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>Un concepto de la factura ficticia: la clave del proveedor (8 cifras, como las de un mayorista), lo que cobra y cómo.</summary>
public sealed record ConceptoDePrueba(
    string Clave, string Descripcion, decimal Cantidad, decimal ValorUnitario, decimal Descuento = 0, string Unidad = "Pieza", string ClaveUnidad = "H87",
    string ClaveProdServ = "14111514");

/// <summary>
/// Una factura CFDI 4.0 ficticia con la forma de las que manda un mayorista de papelería (la de ejemplo del dueño, 2026-11-27, que no
/// se copia: son datos reales): empieza con el BOM, trae sello y certificado, el emisor y un receptor genérico, cada concepto con su
/// clave del SAT, su clave de unidad, su IVA trasladado, los impuestos del comprobante, el timbre y una adenda del proveedor con su
/// propio espacio de nombres. Los importes cuadran como en el SAT: importe, descuento, IVA al 16 % redondeado por concepto y el total.
/// </summary>
public static class CfdiDePrueba
{
    public const string Rfc = "PGO050505AB1";
    public const string Nombre = "PAPELERA DEL GOLFO";

    public static byte[] Factura(string uuid, IEnumerable<ConceptoDePrueba> conceptos, string rfc = Rfc, string nombre = Nombre, string serie = "PGF",
        string folio = "20431", string tipo = "I", bool conTimbre = true)
    {
        static string N(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);
        var lista = conceptos.ToList();
        var xml = new StringBuilder();
        decimal subtotal = 0, descuentos = 0, iva = 0;
        foreach (var c in lista)
        {
            var importe = Math.Round(c.Cantidad * c.ValorUnitario, 2, MidpointRounding.AwayFromZero);
            var base_ = importe - c.Descuento;
            var traslado = Math.Round(base_ * 0.16m, 2, MidpointRounding.AwayFromZero);
            (subtotal, descuentos, iva) = (subtotal + importe, descuentos + c.Descuento, iva + traslado);
            xml.Append($"""
                <cfdi:Concepto ClaveProdServ="{c.ClaveProdServ}" NoIdentificacion="{c.Clave}" Cantidad="{c.Cantidad.ToString(CultureInfo.InvariantCulture)}" ClaveUnidad="{c.ClaveUnidad}" Unidad="{c.Unidad}" Descripcion="{c.Descripcion}" ValorUnitario="{N(c.ValorUnitario)}" Importe="{N(importe)}"{(c.Descuento > 0 ? $" Descuento=\"{N(c.Descuento)}\"" : "")} ObjetoImp="02">
                  <cfdi:Impuestos><cfdi:Traslados><cfdi:Traslado Base="{N(base_)}" Impuesto="002" TipoFactor="Tasa" TasaOCuota="0.160000" Importe="{N(traslado)}" /></cfdi:Traslados></cfdi:Impuestos>
                </cfdi:Concepto>
                """);
        }
        var total = subtotal - descuentos + iva;
        var texto = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <cfdi:Comprobante xmlns:cfdi="http://www.sat.gob.mx/cfd/4" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd" Version="4.0" Serie="{serie}" Folio="{folio}" Fecha="2026-11-25T17:33:39" Sello="U2VsbG9EZVBydWViYQ==" FormaPago="99" NoCertificado="00001000000000000001" Certificado="Q2VydGlmaWNhZG9EZVBydWViYQ==" CondicionesDePago="CONTADO" SubTotal="{N(subtotal)}"{(descuentos > 0 ? $" Descuento=\"{N(descuentos)}\"" : "")} Moneda="MXN" Total="{N(total)}" TipoDeComprobante="{tipo}" Exportacion="01" MetodoPago="PPD" LugarExpedicion="72000">
              <cfdi:Emisor Rfc="{rfc}" Nombre="{nombre}" RegimenFiscal="601" />
              <cfdi:Receptor Rfc="XAXX010101000" Nombre="PUBLICO EN GENERAL" DomicilioFiscalReceptor="72000" RegimenFiscalReceptor="616" UsoCFDI="S01" />
              <cfdi:Conceptos>{xml}</cfdi:Conceptos>
              <cfdi:Impuestos TotalImpuestosTrasladados="{N(iva)}"><cfdi:Traslados><cfdi:Traslado Base="{N(subtotal - descuentos)}" Impuesto="002" TipoFactor="Tasa" TasaOCuota="0.160000" Importe="{N(iva)}" /></cfdi:Traslados></cfdi:Impuestos>
              <cfdi:Complemento>{(conTimbre ? $"""<tfd:TimbreFiscalDigital xmlns:tfd="http://www.sat.gob.mx/TimbreFiscalDigital" Version="1.1" UUID="{uuid}" FechaTimbrado="2026-11-26T13:11:55" RfcProvCertif="AAA010101AAA" SelloCFD="U2VsbG8=" NoCertificadoSAT="00001000000000000002" SelloSAT="U2VsbG8=" />""" : "")}</cfdi:Complemento>
              <cfdi:Addenda><prov:ADENDA xmlns:prov="http://example.invalid/adenda-del-proveedor"><prov:ImporteLetra importe="(con letra)" /><prov:Cliente numero="0001" /></prov:ADENDA></cfdi:Addenda>
            </cfdi:Comprobante>
            """;
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(texto)];
    }
}
