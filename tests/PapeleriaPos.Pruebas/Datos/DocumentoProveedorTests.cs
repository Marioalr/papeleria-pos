using System.Text;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Nucleo.Inventario;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PapeleriaPos.Pruebas.Datos;

public class DocumentoProveedorTests
{
    /// <summary>Una factura CFDI 4.0 mínima: los conceptos van tal cual, con su IVA trasladado.</summary>
    public static byte[] Factura(string uuid, string rfc = "DEC010101AB1", string tipo = "I", string moneda = "MXN", string? tipoCambio = null,
        params string[] conceptos) =>
        Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <cfdi:Comprobante xmlns:cfdi="http://www.sat.gob.mx/cfd/4" xmlns:tfd="http://www.sat.gob.mx/TimbreFiscalDigital" Version="4.0"
                Serie="F" Folio="1234" Fecha="2026-10-07T10:00:00" SubTotal="100.00" Moneda="{moneda}" {(tipoCambio is null ? "" : $"TipoCambio=\"{tipoCambio}\"")}
                Total="116.00" TipoDeComprobante="{tipo}">
              <cfdi:Emisor Rfc="{rfc}" Nombre="DISTRIBUIDORA ESCOLAR DEL CENTRO" RegimenFiscal="601" />
              <cfdi:Receptor Rfc="XAXX010101000" Nombre="PAPELERIA" UsoCFDI="G01" />
              <cfdi:Conceptos>{string.Concat(conceptos)}</cfdi:Conceptos>
              <cfdi:Complemento><tfd:TimbreFiscalDigital Version="1.1" UUID="{uuid}" /></cfdi:Complemento>
            </cfdi:Comprobante>
            """);

    public static string Concepto(string? clave, string descripcion, string cantidad, string unidad, string importe, string? iva, string? descuento = null) =>
        $"""
        <cfdi:Concepto ClaveProdServ="44121701" {(clave is null ? "" : $"NoIdentificacion=\"{clave}\"")} Cantidad="{cantidad}" ClaveUnidad="H87" Unidad="{unidad}"
            Descripcion="{descripcion}" ValorUnitario="1" Importe="{importe}" {(descuento is null ? "" : $"Descuento=\"{descuento}\"")} ObjetoImp="02">
          {(iva is null ? "" : $"""<cfdi:Impuestos><cfdi:Traslados><cfdi:Traslado Base="{importe}" Impuesto="002" TipoFactor="Tasa" TasaOCuota="0.160000" Importe="{iva}" /></cfdi:Traslados></cfdi:Impuestos>""")}
        </cfdi:Concepto>
        """;

    /// <summary>Un PDF con texto en posiciones dadas (renglón por renglón, de arriba abajo).</summary>
    public static byte[] Pdf(params (double Y, (double X, string Texto)[] Celdas)[] renglones)
    {
        var builder = new PdfDocumentBuilder();
        var fuente = builder.AddStandard14Font(Standard14Font.Helvetica);
        var pagina = builder.AddPage(PageSize.Letter);
        foreach (var (y, celdas) in renglones)
            foreach (var (x, texto) in celdas)
                pagina.AddText(texto, 10, new PdfPoint(x, y), fuente);
        return builder.Build();
    }

    [Fact]
    public void La_factura_trae_cada_concepto_con_lo_que_se_paga_incluido_el_iva()
    {
        var xml = Factura("aaaa1111-2222-3333-4444-555566667777", conceptos:
        [
            Concepto("7508669136854", "PLUMA BIC AZUL CAJA C/12", "3", "CAJA", "232.758621", "37.241379"),
            Concepto("DEC-500", "CUADERNO PROFESIONAL 100 HOJAS", "10", "PIEZA", "200.00", "28.80", descuento: "20.00"),
            Concepto(null, "LIBRO SIN IVA", "2.5", "PIEZA", "50.00", null),
        ]);

        var leido = LectorDocumentos.Leer(xml);

        Assert.Equal(TipoDocumentoProveedor.Xml, leido.Tipo);
        Assert.Equal(("F1234", "AAAA1111-2222-3333-4444-555566667777", "DEC010101AB1"), (leido.Folio, leido.Uuid, leido.RfcEmisor));
        Assert.Equal(
        [
            new LineaLeida("7508669136854", "PLUMA BIC AZUL CAJA C/12", "CAJA", 3m, 27000),
            new LineaLeida("DEC-500", "CUADERNO PROFESIONAL 100 HOJAS", "PIEZA", 10m, 20880),
            new LineaLeida(null, "LIBRO SIN IVA", "PIEZA", 2.5m, 5000),
        ], leido.Lineas);
        Assert.Empty(leido.Avisos);
    }

    [Fact]
    public void Una_factura_en_dolares_se_pasa_a_pesos_y_una_nota_de_credito_no_se_toma()
    {
        var dolares = LectorDocumentos.Leer(Factura("u-1", moneda: "USD", tipoCambio: "18.50", conceptos: [Concepto("A1", "TINTA", "1", "PIEZA", "10.00", "1.60")]));
        var credito = Assert.Throws<InvalidOperationException>(() => LectorDocumentos.Leer(Factura("u-2", tipo: "E", conceptos: [Concepto("A1", "TINTA", "1", "PIEZA", "10.00", null)])));
        var conDtd = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><x>&a;</x>""");

        Assert.Equal(21460, Assert.Single(dolares.Lineas).ImporteCentavos);
        Assert.Contains(dolares.Avisos, a => a.Contains("USD"));
        Assert.Contains("nota de crédito", credito.Message);
        Assert.Throws<InvalidOperationException>(() => LectorDocumentos.Leer(conDtd));
        Assert.Throws<InvalidOperationException>(() => LectorDocumentos.Leer("hola"u8.ToArray()));
    }

    [Fact]
    public void Del_pdf_se_toman_los_renglones_que_cuadran_y_el_iva_se_reparte()
    {
        var pdf = Pdf(
            (720, [(40, "DISTRIBUIDORA ESCOLAR DEL CENTRO"), (400, "NOTA DE VENTA 5512")]),
            (690, [(40, "CLAVE"), (130, "DESCRIPCION"), (330, "CANT"), (380, "UNIDAD"), (440, "P.UNIT"), (510, "IMPORTE")]),
            (670, [(40, "DEC-PFA12"), (130, "PLUMA PUNTO FINO AZUL C/12"), (330, "3"), (380, "CAJA"), (440, "75.00"), (510, "225.00")]),
            (655, [(40, "7503505687545"), (130, "PLUMA PUNTO FINO NEGRO"), (330, "5"), (380, "PZA"), (440, "6.90"), (510, "34.50")]),
            (640, [(40, "CU-100"), (130, "CUADERNO 100 HOJAS RAYA"), (330, "12"), (380, "PZA"), (440, "1,050.00"), (510, "$12,600.00")]),
            (600, [(440, "SUBTOTAL"), (510, "12,859.50")]),
            (585, [(440, "IVA 16%"), (510, "2,057.52")]),
            (570, [(440, "TOTAL"), (510, "$14,917.02")]));

        var leido = LectorDocumentos.Leer(pdf);

        Assert.Equal(TipoDocumentoProveedor.Pdf, leido.Tipo);
        Assert.Equal(1491702, leido.TotalCentavos);
        Assert.Equal(["DEC-PFA12", "7503505687545", "CU-100"], leido.Lineas.Select(l => l.Codigo));
        Assert.Equal(["PLUMA PUNTO FINO AZUL C/12", "PLUMA PUNTO FINO NEGRO", "CUADERNO 100 HOJAS RAYA"], leido.Lineas.Select(l => l.Descripcion));
        Assert.Equal([3m, 5m, 12m], leido.Lineas.Select(l => l.Cantidad));
        Assert.Equal(["CAJA", "PZA", "PZA"], leido.Lineas.Select(l => l.Unidad));
        // Los precios venían sin IVA: cada renglón sube 16 % y entre todos dan el total.
        Assert.Equal([26100, 4002, 1461600], leido.Lineas.Select(l => l.ImporteCentavos));
        Assert.Equal(1491702, leido.Lineas.Sum(l => l.ImporteCentavos));
        Assert.Contains(leido.Avisos, a => a.Contains("repartió en proporción"));
    }

    [Fact]
    public void Una_nota_con_la_cantidad_primero_y_sin_claves_tambien_se_lee()
    {
        var pdf = Pdf(
            (700, [(40, "CANT"), (90, "DESCRIPCION"), (400, "PRECIO"), (480, "IMPORTE")]),
            (680, [(40, "2"), (90, "FOLDER CARTA CREMA"), (400, "$ 3.50"), (480, "$ 7.00")]),
            (665, [(40, "1"), (90, "ENGRAPADORA"), (400, "85.00"), (480, "85.00")]),
            (640, [(400, "TOTAL:"), (480, "92.00")]));

        var leido = LectorDocumentos.Leer(pdf);

        Assert.Equal(
        [
            new LineaLeida(null, "FOLDER CARTA CREMA", null, 2m, 700),
            new LineaLeida(null, "ENGRAPADORA", null, 1m, 8500),
        ], leido.Lineas);
        Assert.DoesNotContain(leido.Avisos, a => a.Contains("repartió"));
    }

    [Fact]
    public void Un_pdf_sin_texto_pide_capturar_a_mano()
    {
        var vacio = Pdf();

        var error = Assert.Throws<InvalidOperationException>(() => LectorDocumentos.Leer(vacio));

        Assert.Contains("captura la nota a mano", error.Message);
    }
}
