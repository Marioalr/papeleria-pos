using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Impresion;

public class TicketTests
{
    private static TicketDto Ticket(params CobroTicketDto[] cobros) => new(
        "A-000123", new DateTime(2026, 10, 3, 12, 47, 0), "Caja 1", "Ana",
        [
            new LineaTicketDto("2", "Nylon C10 x metro", 6000, 12000),
            new LineaTicketDto("0.5 m", "Listón de raso 2.5 cm rojo con orilla dorada para moños grandes", 800, 400),
            new LineaTicketDto("1", "Cuaderno profesional Español", 4500, 4500),
        ],
        4, 16900, 2331,
        cobros.Length > 0 ? cobros : [new CobroTicketDto(FormaPago.Efectivo, 16900, 20000, 3100, null)],
        ["PAPELERÍA DE PRUEBA", "Calle 1"], ["Gracias por su compra"]);

    [Fact]
    public void El_descuento_sale_debajo_del_articulo_y_el_ahorro_antes_del_total()
    {
        var ticket = Ticket() with
        {
            Lineas = [new LineaTicketDto("2", "Nylon C10 x metro", 6000, 10800, 1200, "10 %"), new LineaTicketDto("1", "Goma", 500, 500)],
            TotalCentavos = 11300,
        };

        var renglones = TicketTexto.Generar(ticket).Split(Environment.NewLine);

        Assert.Contains(renglones, r => r.Contains("Nylon C10 x metro") && r.EndsWith("$120.00"));
        Assert.Contains(renglones, r => r.Contains("Descuento 10 %") && r.EndsWith("-$12.00"));
        Assert.Contains(renglones, r => r.Contains("Usted ahorró: $12.00"));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    [Fact]
    public void Con_redondeo_sale_el_total_exacto_el_redondeo_y_lo_que_se_paga()
    {
        var ticket = Ticket(new CobroTicketDto(FormaPago.Efectivo, 16800, 20000, 3200, null)) with { RedondeoCentavos = -100 };

        var renglones = TicketTexto.Generar(ticket).Split(Environment.NewLine);

        Assert.Contains(renglones, r => r.Contains("Total: $169.00"));
        Assert.Contains(renglones, r => r.Contains("Redondeo: -$1.00"));
        Assert.Contains(renglones, r => r.Contains("Total a pagar: $168.00"));
        Assert.Contains(renglones, r => r.Contains("Efectivo: $200.00  Cambio: $32.00"));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.DoesNotContain("Redondeo", TicketTexto.Generar(Ticket()));
    }

    [Fact]
    public void La_cancelacion_redondeada_cuadra_en_el_comprobante()
    {
        var comprobante = new ComprobanteDevolucionDto("A-000123", true, 5049, 3000, "Se arrepintió", new DateTime(2026, 10, 7, 12, 0, 0), "Caja 1", "Ana", null,
            [new LineaTicketDto("1", "Mochila", 5049, 5049)], ["PAPELERÍA"], RedondeoCentavos: -49, CobradoSinEfectivoCentavos: 2000);

        var texto = TicketTexto.Generar(DisenoComprobante.Devolucion(comprobante));

        // $50.49 = $30 en efectivo + $20 por la tarjeta + $0.49 de redondeo.
        Assert.Contains("Redondeo: -$0.49", texto);
        Assert.Contains("Efectivo devuelto: $30.00", texto);
        Assert.Contains("Se regresa por su medio de pago: $20.00", texto);
        Assert.All(texto.Split(Environment.NewLine), r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    [Fact]
    public void El_mayoreo_sale_debajo_del_articulo_y_cuenta_en_lo_que_se_ahorro()
    {
        var ticket = Ticket() with
        {
            Lineas = [new LineaTicketDto("12", "Pluma Bic azul", 800, 9600, 0, null, 2400, "Mayoreo desde 12"), new LineaTicketDto("1", "Goma", 500, 450, 50, "10 %")],
            TotalCentavos = 10050,
        };

        var renglones = TicketTexto.Generar(ticket).Split(Environment.NewLine);

        // El renglón lleva el importe a menudeo ($120) y abajo lo que se ahorró por el mayoreo.
        Assert.Contains(renglones, r => r.Contains("Pluma Bic azul") && r.EndsWith("$120.00"));
        Assert.Contains(renglones, r => r.Contains("Mayoreo desde 12") && r.EndsWith("-$24.00"));
        Assert.Contains(renglones, r => r.Contains("Usted ahorró: $24.50"));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    [Fact]
    public void El_texto_cabe_en_48_columnas_y_trae_lo_importante()
    {
        var texto = TicketTexto.Generar(Ticket());
        var renglones = texto.Split(Environment.NewLine);

        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.Contains("Folio A-000123", texto);
        Assert.Contains("Total: $169.00", texto);
        Assert.Contains("Efectivo: $200.00  Cambio: $31.00", texto);
        Assert.Contains("Incluye IVA: $23.31", texto);
        Assert.Contains("[QR A-000123]", texto);
        Assert.Contains("Español", texto);
    }

    [Fact]
    public void Las_descripciones_largas_siguen_en_el_renglon_de_abajo_sin_cortar_palabras()
    {
        var partes = DisenoTicket.Partir("Listón de raso 2.5 cm rojo con orilla dorada para moños grandes", 30);

        Assert.True(partes.Count > 1);
        Assert.All(partes, p => Assert.True(p.Length <= 30));
        Assert.Equal("Listón de raso 2.5 cm rojo con orilla dorada para moños grandes", string.Join(" ", partes));
    }

    [Fact]
    public void Tarjeta_y_transferencia_aparecen_con_su_referencia()
    {
        var texto = TicketTexto.Generar(Ticket(
            new CobroTicketDto(FormaPago.Tarjeta, 10000, 0, 0, "Aut 4411"),
            new CobroTicketDto(FormaPago.Transferencia, 6900, 0, 0, "SPEI")));

        Assert.Contains("Tarjeta: $100.00 (Aut 4411)", texto);
        Assert.Contains("Transferencia: $69.00", texto);
    }

    [Fact]
    public void Lo_fiado_sale_como_credito_y_no_cuenta_como_pago()
    {
        var ticket = Ticket(new CobroTicketDto(FormaPago.Efectivo, 10000, 10000, 0, null), new CobroTicketDto(FormaPago.Credito, 6900, 0, 0, null));

        var texto = TicketTexto.Generar(ticket);

        Assert.Contains("Crédito (a su cuenta): $69.00", texto);
        Assert.DoesNotContain("Transferencia", texto);
        Assert.Equal((10000L, 6900L, true), (ticket.PagoConCentavos, ticket.CreditoCentavos, ticket.AbreCajon));
        Assert.False(Ticket(new CobroTicketDto(FormaPago.Credito, 16900, 0, 0, null)).AbreCajon);
    }

    [Fact]
    public void Escpos_inicia_corta_y_abre_el_cajon_solo_si_se_pide()
    {
        var conCajon = TicketEscPos.Generar(Ticket(), abrirCajon: true);
        var sinCajon = TicketEscPos.Generar(Ticket(), abrirCajon: false);
        byte[] cajon = [0x1B, (byte)'p', 0, 25, 250];
        byte[] corte = [0x1D, (byte)'V', 66, 0];

        Assert.Equal([0x1B, (byte)'@'], conCajon[..2]);
        Assert.Equal(corte, conCajon[^4..]);
        Assert.True(Contiene(conCajon, cajon));
        Assert.False(Contiene(sinCajon, cajon));
    }

    [Fact]
    public void Escpos_usa_la_pagina_850_para_la_ñ_y_trae_el_qr()
    {
        var bytes = TicketEscPos.Generar(Ticket(), abrirCajon: false);

        Assert.True(Contiene(bytes, [0x1B, (byte)'t', 2]));
        Assert.True(Contiene(bytes, [(byte)'E', (byte)'s', (byte)'p', (byte)'a', 0xA4, (byte)'o', (byte)'l'])); // "Español"
        Assert.True(Contiene(bytes, "A-000123"u8.ToArray()));
        Assert.True(Contiene(bytes, [0x1D, (byte)'(', (byte)'k', 3, 0, 49, 81, 48]));
    }

    private static bool Contiene(byte[] datos, byte[] buscado)
    {
        for (var i = 0; i <= datos.Length - buscado.Length; i++)
        {
            if (datos.AsSpan(i, buscado.Length).SequenceEqual(buscado))
                return true;
        }
        return false;
    }
}
