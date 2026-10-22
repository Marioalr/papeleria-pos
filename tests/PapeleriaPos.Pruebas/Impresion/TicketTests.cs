using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Clientes;
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

    /// <summary>
    /// La nota del ticket (F4 al cobrar, como en eleventa; 2026-10-22): después del cobro y antes del pie, en uno o dos renglones
    /// de 48, sin cortar palabras. Una palabra muy larga no la deja en tres: se corta en el renglón.
    /// </summary>
    [Fact]
    public void La_nota_sale_antes_del_pie_en_dos_renglones_a_lo_mas()
    {
        var nota = "Se entrega en la escuela Benito Juárez, salón 3-B, con la maestra Lupita";
        Assert.True(nota.Length <= NotaDelTicket.Maximo);

        var renglones = TicketTexto.Generar(Ticket() with { Nota = nota }).Split(Environment.NewLine);

        var primero = Array.FindIndex(renglones, r => r.StartsWith("Nota: Se entrega", StringComparison.Ordinal));
        Assert.True(primero > Array.FindIndex(renglones, r => r.Contains("Efectivo: $200.00")));
        Assert.True(primero < Array.FindIndex(renglones, r => r.Contains("Gracias por su compra")));
        Assert.Equal($"Nota: {nota}", $"{renglones[primero]} {renglones[primero + 1]}");
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.DoesNotContain("Nota:", TicketTexto.Generar(Ticket()));

        var larga = "Recoger " + new string('x', 45) + " en la sucursal";
        Assert.Equal(2, DisenoTicket.RenglonesDeLaNota(larga).Count);
        Assert.Equal($"Nota: {larga}", string.Concat(DisenoTicket.RenglonesDeLaNota(larga)).Replace("  ", " "));
    }

    /// <summary>Como se guarda la nota: sin espacios de más ni saltos de renglón, vacía es ninguna y hasta 80 letras.</summary>
    [Fact]
    public void La_nota_se_guarda_en_un_renglon_y_hasta_lo_que_cabe()
    {
        Assert.Equal("Entregar en la escuela", NotaDelTicket.Normalizar("  Entregar\n en   la escuela "));
        Assert.Null(NotaDelTicket.Normalizar("  \n "));
        Assert.Null(NotaDelTicket.Normalizar(null));
        Assert.Equal(80, NotaDelTicket.Normalizar(new string('a', 80))!.Length);
        Assert.Throws<ArgumentException>(() => NotaDelTicket.Normalizar(new string('a', 81)));
        // Lo que llega con la bandeja ya se cobró: se guarda hasta donde cabe.
        Assert.Equal(80, NotaDelTicket.Recortada(new string('a', 90))!.Length);

        var venta = new Venta { Serie = "A" };
        venta.PonerNota(" Para Lupita ");
        Assert.Equal("Para Lupita", venta.Nota);
        venta.PonerNota("");
        Assert.Null(venta.Nota);
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
        // Sin QR por ahora, como el de eleventa (2026-10-16): el folio se escribe para devolver.
        Assert.DoesNotContain("[QR", texto);
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
    public void El_saldo_devuelto_dice_lo_que_tenia_lo_que_se_le_entrega_y_lo_firma_el_cliente()
    {
        var devuelto = Abono(FormaPago.Efectivo, 12345, redondeo: -45) with
        {
            Folio = "DS-1A2B3C", Tipo = TipoAbono.SaldoDevuelto, SaldoAnteriorCentavos = -12345, SaldoCentavos = 0, Vence = null,
        };

        var texto = TicketTexto.Generar(DisenoComprobante.Abono(devuelto));
        var renglones = texto.Split(Environment.NewLine);
        var crudos = DisenoComprobante.Abono(devuelto).OfType<Renglon>().Select(r => r.Texto).ToList();
        var parte = TicketTexto.Generar(DisenoComprobante.Abono(devuelto with { MontoCentavos = 10000, RedondeoCentavos = 0, SaldoCentavos = -2345 }));

        Assert.Contains("SALDO A FAVOR DEVUELTO", texto);
        Assert.Contains("Comprobante DS-1A2B3C", texto);
        Assert.Contains(renglones, r => r.StartsWith("Tenía a favor") && r.EndsWith("$123.45"));
        Assert.Contains(renglones, r => r.StartsWith("Se le entregan en efectivo") && r.EndsWith("$123.00"));
        Assert.Contains("(redondeo al peso: -$0.45)", texto);
        Assert.Contains("Al corriente", texto);
        Assert.Contains("Firma del cliente", texto);
        Assert.DoesNotContain("ABONO", texto);
        Assert.Contains("Le quedan a favor: $23.45", parte);
        Assert.All(crudos, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    private static EstadoCuentaDto Estado() => new(Guid.NewGuid(), "Arquitecto Ramírez Hernández de la Constructora del Valle", "5522334455",
        new DateTime(2026, 10, 8, 17, 5, 0), new DateOnly(2026, 8, 29), 0,
        [
            new MovimientoCreditoDto(Guid.NewGuid(), new DateTime(2026, 8, 29, 12, 0, 0), TipoMovimientoCredito.Ajuste, "Ajuste: Saldo inicial de la libreta", null,
                35000, 35000, new DateOnly(2026, 9, 28), false),
            new MovimientoCreditoDto(Guid.NewGuid(), new DateTime(2026, 10, 1, 13, 0, 0), TipoMovimientoCredito.Cargo, "Compra a crédito", "A-000123",
                12000, 47000, new DateOnly(2026, 10, 31), false),
            new MovimientoCreditoDto(Guid.NewGuid(), new DateTime(2026, 10, 8, 17, 0, 0), TipoMovimientoCredito.Abono, "Abono", "AB-1A2B3C",
                -10000, 37000, null, false),
        ],
        37000, 25000, 0,
        [
            new PendienteEstadoDto(new DateOnly(2026, 8, 29), "Ajuste: Saldo inicial de la libreta", null, 35000, 25000, new DateOnly(2026, 9, 28)),
            new PendienteEstadoDto(new DateOnly(2026, 10, 1), "Compra a crédito", "A-000123", 12000, 12000, new DateOnly(2026, 10, 31)),
        ],
        100000, 30, ["PAPELERÍA DE PRUEBA", "Av. Juárez 10"]);

    [Fact]
    public void El_estado_de_cuenta_impreso_cabe_en_el_ticket_y_dice_lo_que_falta_pagar()
    {
        var texto = TicketTexto.Generar(DisenoComprobante.EstadoCuenta(Estado()));
        var renglones = texto.Split(Environment.NewLine);
        // Lo que se manda a la impresora (el texto de vista previa ya viene recortado a lo ancho).
        var grande = Estado() with
        {
            Movimientos = [.. Estado().Movimientos, new MovimientoCreditoDto(Guid.NewGuid(), new DateTime(2026, 10, 8, 18, 0, 0), TipoMovimientoCredito.Cargo,
                "Compra a crédito", "A-000124", 15_000_000, -15_000_000, null, false)],
        };
        var crudos = DisenoComprobante.EstadoCuenta(grande).OfType<Renglon>().Select(r => r.Texto).ToList();

        Assert.Contains("ESTADO DE CUENTA", texto);
        Assert.Contains("Av. Juárez 10", texto);
        Assert.Contains("Crédito de $1,000.00 a 30 días, sin recargos", texto);
        Assert.Contains(renglones, r => r.StartsWith("Saldo antes del 29/08/2026") && r.EndsWith("$0.00"));
        Assert.Contains(renglones, r => r.StartsWith("01/10 Compra a") && r.Contains("+$120.00") && r.EndsWith("$470.00"));
        Assert.Contains(renglones, r => r.StartsWith("08/10 Abono AB-1A2B3C") && r.Contains("-$100.00") && r.EndsWith("$370.00"));
        Assert.Contains("Debe: $370.00", texto);
        Assert.Contains("Vencido: $250.00", texto);
        Assert.Contains("(de $350.00) VENCIÓ EL 28/09/2026", texto);
        Assert.Contains("vence el 31/10/2026", texto);
        Assert.Contains("Sin recargos.", texto);
        Assert.All(crudos, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.Contains(crudos, r => r.Contains("+$150,000.00 -$150,000.00"));
        // Si empieza en otro año, cada fecha lo dice.
        Assert.Contains("29/08/25 Ajuste", TicketTexto.Generar(DisenoComprobante.EstadoCuenta(Estado() with { Desde = new DateOnly(2025, 8, 29),
            Movimientos = [Estado().Movimientos[0] with { FechaLocal = new DateTime(2025, 8, 29, 12, 0, 0) }] })));
    }

    [Fact]
    public void El_estado_de_cuenta_para_whatsapp_dice_lo_mismo_en_texto()
    {
        var texto = DisenoComprobante.EstadoCuentaTexto(Estado());
        var alCorriente = DisenoComprobante.EstadoCuentaTexto(Estado() with { SaldoCentavos = 0, VencidoCentavos = 0, Pendientes = [] });

        Assert.StartsWith("*PAPELERÍA DE PRUEBA*\nEstado de cuenta de *Arquitecto Ramírez", texto);
        Assert.Contains("Saldo antes del 29/08/2026: $0.00", texto);
        Assert.Contains("01/10 Compra a crédito A-000123: +$120.00 (saldo $470.00)", texto);
        Assert.Contains("*Debe: $370.00*", texto);
        Assert.Contains("- Ajuste: Saldo inicial de la libreta del 29/08/2026: $250.00 de $350.00, *venció el 28/09/2026*", texto);
        Assert.Contains("- Compra a crédito A-000123 del 01/10/2026: $120.00, vence el 31/10/2026", texto);
        Assert.Contains("*Al corriente*", alCorriente);
        Assert.DoesNotContain("falta pagar", alCorriente);
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
        Assert.DoesNotContain("Saldo a favor devuelto", texto);
        Assert.Contains(TicketTexto.Generar(DisenoComprobante.Corte(corte with { SaldoDevueltoCentavos = 12300 }, "Caja 1")).Split(Environment.NewLine),
            r => r.StartsWith("Saldo a favor devuelto") && r.EndsWith("-$123.00"));
    }

    /// <summary>
    /// Una entrada o salida cancelada (2026-10-22) ya no suma en el corte, y el corte dice cuántas se cancelaron junto a lo que
    /// sí cuenta, como la pantalla del corte.
    /// </summary>
    [Fact]
    public void El_corte_dice_cuantas_entradas_y_salidas_se_cancelaron()
    {
        var corte = new CorteDto(TipoCorte.Z, new DateTime(2026, 10, 22, 18, 0, 0), "Ana", "Luis", 3, 50000, 20000, 10000, 5000, 0, 0, 75000, 75000, 0,
            EntradasCanceladas: 1, SalidasCanceladas: 2);

        var renglones = TicketTexto.Generar(DisenoComprobante.Corte(corte, "Caja 1")).Split(Environment.NewLine);
        var sinCanceladas = TicketTexto.Generar(DisenoComprobante.Corte(corte with { EntradasCanceladas = 0, SalidasCanceladas = 0 }, "Caja 1"));

        Assert.Contains(renglones, r => r.StartsWith("Entradas (1 cancelada)") && r.EndsWith("$100.00"));
        Assert.Contains(renglones, r => r.StartsWith("Salidas (2 canceladas)") && r.EndsWith("-$50.00"));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
        Assert.DoesNotContain("cancelada", sinCanceladas);
        Assert.Equal(("Entradas", "Salidas (1 cancelada)"), (DisenoComprobante.ConCanceladas("Entradas", 0), DisenoComprobante.ConCanceladas("Salidas", 1)));
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
    public void Escpos_usa_la_pagina_850_para_la_ñ_y_el_ticket_no_trae_qr()
    {
        var bytes = TicketEscPos.Generar(Ticket(), abrirCajon: false);
        byte[] imprimirQr = [0x1D, (byte)'(', (byte)'k', 3, 0, 49, 81, 48];

        Assert.True(Contiene(bytes, [0x1B, (byte)'t', 2]));
        Assert.True(Contiene(bytes, [(byte)'E', (byte)'s', (byte)'p', (byte)'a', 0xA4, (byte)'o', (byte)'l'])); // "Español"
        Assert.True(Contiene(bytes, "A-000123"u8.ToArray()));
        Assert.False(Contiene(bytes, imprimirQr));
        // La impresora sigue sabiendo imprimir uno, para cuando se quiera.
        Assert.True(Contiene(TicketEscPos.Generar([new Qr("A-000123")], abrirCajon: false), imprimirQr));
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
