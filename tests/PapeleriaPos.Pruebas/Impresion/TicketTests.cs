using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
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
    public void Lo_fiado_trae_su_cuenta_con_el_saldo_y_hasta_cuando_paga()
    {
        var ticket = Ticket(new CobroTicketDto(FormaPago.Efectivo, 10000, 10000, 0, null), new CobroTicketDto(FormaPago.Credito, 6900, 0, 0, null)) with
        {
            Credito = new CreditoTicketDto(6900, 41900, new DateOnly(2026, 11, 2)),
        };

        var texto = TicketTexto.Generar(ticket);
        var renglones = texto.Split(Environment.NewLine);

        Assert.Contains("SU CUENTA", texto);
        Assert.Contains(renglones, r => r.StartsWith("Fiado en esta compra") && r.EndsWith("$69.00"));
        Assert.Contains(renglones, r => r.StartsWith("Saldo de su cuenta") && r.EndsWith("$419.00"));
        Assert.Contains("Paga esta compra antes del 02/11/2026", texto);
        Assert.DoesNotContain("según esta caja", texto);
        Assert.Contains("según esta caja", TicketTexto.Generar(ticket with { Credito = ticket.Credito! with { Estimado = true } }));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.DoesNotContain("SU CUENTA", TicketTexto.Generar(Ticket()));
    }

    private static ComprobanteAbonoDto Abono(FormaPago forma, long monto, long redondeo = 0, long recibido = 0, long cambio = 0) => new(
        Guid.NewGuid(), "AB-1A2B3C", new DateTime(2026, 10, 8, 17, 5, 0), "Caja 1", "Ana", Guid.NewGuid(),
        "Arquitecto Ramírez Hernández de la Constructora del Valle", forma, monto, redondeo, recibido, cambio,
        forma == FormaPago.Efectivo ? null : "SPEI 778", 55045, 55045 - monto, 55045 - monto > 0 ? new DateOnly(2026, 11, 7) : null, ["PAPELERÍA DE PRUEBA"]);

    [Fact]
    public void El_comprobante_del_abono_dice_lo_que_debia_lo_que_abono_y_lo_que_queda()
    {
        var texto = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Efectivo, 30000, recibido: 50000, cambio: 20000)));
        var renglones = texto.Split(Environment.NewLine);

        Assert.Contains("ABONO A SU CUENTA", texto);
        Assert.Contains("Abono AB-1A2B3C", texto);
        Assert.Contains(renglones, r => r.StartsWith("Debía") && r.EndsWith("$550.45"));
        Assert.Contains(renglones, r => r.StartsWith("Abono en efectivo") && r.EndsWith("$300.00"));
        Assert.Contains("Efectivo: $500.00  Cambio: $200.00", texto);
        Assert.Contains("Saldo: $250.45", texto);
        Assert.Contains("Paga antes del 07/11/2026", texto);
        Assert.Contains("Lo más antiguo venció el 28/09/2026",
            TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Efectivo, 30000) with { Vence = new DateOnly(2026, 9, 28) })));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    [Fact]
    public void Liquidar_en_efectivo_lleva_su_redondeo_y_la_transferencia_queda_por_verificar()
    {
        var liquida = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Efectivo, 55045, redondeo: -45, recibido: 60000, cambio: 5000)));
        var transferencia = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Transferencia, 20000)));
        var cancelado = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Tarjeta, 20000) with { Cancelado = true, MotivoCancelacion = "Era de otro" }));

        Assert.Contains("Redondeo: -$0.45", liquida);
        Assert.Contains("Saldo: $0.00", liquida);
        Assert.Contains("Al corriente", liquida);
        Assert.Contains("Referencia: SPEI 778", transferencia);
        Assert.Contains("La transferencia queda por verificar", transferencia);
        Assert.Contains("CANCELACIÓN DE ABONO", cancelado);
        Assert.Contains("Motivo: Era de otro", cancelado);
        Assert.DoesNotContain("Paga antes", cancelado);
    }

    [Fact]
    public void La_cancelacion_del_abono_dice_lo_que_se_regresa_y_lo_que_vuelve_a_deber()
    {
        // Debía $550.45, abonó $300 pagando con $500 (quedó en $250.45); al cancelarlo vuelve a deber los $550.45.
        var efectivo = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Efectivo, 30000, recibido: 50000, cambio: 20000) with
        {
            Cancelado = true, MotivoCancelacion = "Era de otro", SaldoTrasCancelarCentavos = 55045,
        }));
        var transferencia = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Transferencia, 20000) with { Cancelado = true }));

        Assert.Contains("Se le regresan en efectivo: $300.00", efectivo);
        Assert.Contains("Saldo: $550.45", efectivo);
        Assert.Contains("Vuelve a deber $300.00.", efectivo);
        Assert.DoesNotContain("Cambio", efectivo);
        Assert.DoesNotContain("$250.45", efectivo);
        Assert.Contains("Se regresa por su medio de pago: $200.00", transferencia);
        Assert.Contains("Saldo: $550.45", transferencia);
        Assert.DoesNotContain("por verificar", transferencia);
    }

    [Fact]
    public void La_referencia_larga_del_abono_sale_completa_en_varios_renglones()
    {
        var referencia = "SPEI BBVA clave de rastreo MBAN01002510080012345678";
        var renglones = TicketTexto.Generar(DisenoComprobante.Abono(Abono(FormaPago.Transferencia, 20000) with { Referencia = referencia }))
            .Split(Environment.NewLine);

        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.Contains("MBAN01002510080012345678", string.Join(" ", renglones.Select(r => r.Trim())));
    }

    [Fact]
    public void Si_lo_fiado_lo_cubre_su_saldo_a_favor_el_ticket_no_pide_pagar()
    {
        var ticket = Ticket(new CobroTicketDto(FormaPago.Credito, 6000, 0, 0, null)) with { Credito = new CreditoTicketDto(6000, -4000, new DateOnly(2026, 11, 7)) };

        var texto = TicketTexto.Generar(ticket);
        var alCorriente = TicketTexto.Generar(ticket with { Credito = ticket.Credito! with { SaldoCentavos = 0 } });

        Assert.Contains("De su saldo a favor", texto);
        Assert.Contains("Le quedan a favor", texto);
        Assert.Contains("$40.00", texto);
        Assert.DoesNotContain("-$40.00", texto);
        Assert.DoesNotContain("Paga esta compra", texto);
        Assert.Contains("Al corriente", alCorriente);
        Assert.DoesNotContain("Paga esta compra", alCorriente);
    }

    [Fact]
    public void Devolver_lo_fiado_dice_cuanto_baja_su_cuenta_y_como_queda()
    {
        var comprobante = new ComprobanteDevolucionDto("A-000123", false, 5000, 0, "No le quedó", new DateTime(2026, 10, 8, 12, 0, 0), "Caja 1", "Ana", null,
            [new LineaTicketDto("1", "Mochila", 5000, 5000)], ["PAPELERÍA"], AlCreditoCentavos: 5000, Cliente: "Arq. Ramírez", SaldoClienteCentavos: 30000);

        var texto = TicketTexto.Generar(DisenoComprobante.Devolucion(comprobante));
        var aFavor = TicketTexto.Generar(DisenoComprobante.Devolucion(comprobante with { SaldoClienteCentavos = -2000 }));

        Assert.Contains("Cliente: Arq. Ramírez", texto);
        Assert.Contains("Efectivo devuelto: $0.00", texto);
        Assert.Contains("Se descuenta de su cuenta: $50.00", texto);
        Assert.Contains("Saldo de su cuenta: $300.00", texto);
        Assert.Contains("Saldo a su favor: $20.00", aFavor);
    }

    [Fact]
    public void El_corte_lleva_los_abonos_por_forma_y_lo_fiado_aparte()
    {
        var corte = new CorteDto(TipoCorte.X, new DateTime(2026, 10, 8, 18, 0, 0), "Ana", "Ana", 3, 50000, 20000, 0, 0, 5000, 0, 100000, null, null,
            CreditoCentavos: 6900, Abonos: 3, AbonosEfectivoCentavos: 30000, AbonosTarjetaCentavos: 10000, AbonosTransferenciaCentavos: 2500);

        var texto = TicketTexto.Generar(DisenoComprobante.Corte(corte, "Caja 1"));
        var renglones = texto.Split(Environment.NewLine);
        var sinCredito = TicketTexto.Generar(DisenoComprobante.Corte(corte with
        {
            CreditoCentavos = 0, Abonos = 0, AbonosEfectivoCentavos = 0, AbonosTarjetaCentavos = 0, AbonosTransferenciaCentavos = 0,
        }, "Caja 1"));

        Assert.Contains(renglones, r => r.StartsWith("Abonos de clientes en efectivo") && r.EndsWith("$300.00"));
        Assert.Contains("Abonos de clientes: 3", texto);
        Assert.Contains(renglones, r => r.StartsWith("Abonos con tarjeta") && r.EndsWith("$100.00"));
        Assert.Contains(renglones, r => r.StartsWith("Abonos por transferencia") && r.EndsWith("$25.00"));
        Assert.Contains(renglones, r => r.StartsWith("Fiado (ventas a crédito)") && r.EndsWith("$69.00"));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.DoesNotContain("Abonos", sinCredito);
        Assert.DoesNotContain("Fiado", sinCredito);
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
