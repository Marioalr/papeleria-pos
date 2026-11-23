using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// El porcentaje de los descuentos en el ticket impreso (dueño, 2026-11-22): el de cada renglón junto a «Descuento» y el de todo el
/// ticket en «Descuentos: -$X (N %)», sobre el total sin descuentos. Con «Lo que se ahorró» apagado (Configuración › Ticket, PR #189),
/// ninguno sale, como antes; los demás ajustes no cambian.
/// </summary>
public class TicketConPorcentajeTests
{
    /// <summary>$100.00 sin descuentos: $5.00 de descuento en pesos a la goma ($60.00) y 10 % a la pluma ($40.00).</summary>
    private static TicketDto Ticket(FormatoTicket? formato = null) => new(
        "A-000124", new DateTime(2026, 11, 22, 10, 5, 0), "Caja 2", "Ana",
        [
            new LineaTicketDto("1", "GOMA GRANDE", 6000, 5500, 500, "8.3 %"),
            new LineaTicketDto("4", "PLUMA AZUL", 1000, 3600, 400, "10 %"),
        ],
        5, 9100, 1255, [new CobroTicketDto(FormaPago.Efectivo, 9100, 10000, 900, null)],
        ["PAPELERÍA DE PRUEBA"], ["Gracias por su compra"], Formato: formato);

    private static string[] Renglones(TicketDto t) => TicketTexto.Generar(t).Split(Environment.NewLine);

    [Fact]
    public void Cada_descuento_lleva_su_porcentaje_y_el_ticket_el_suyo_sobre_el_total()
    {
        var renglones = Renglones(Ticket());

        Assert.Contains(renglones, r => r.Contains("Descuento 8.3 %") && r.EndsWith("-$5.00"));
        Assert.Contains(renglones, r => r.Contains("Descuento 10 %") && r.EndsWith("-$4.00"));
        // $9.00 de $100.00: 9 %, sin «.0».
        Assert.Contains(renglones, r => r.EndsWith("Descuentos: -$9.00 (9 %)"));
        Assert.Equal("Descuentos: -$9.00 (9 %)", DisenoTicket.DescuentosDelTicket(900, 9100));
        Assert.All(renglones, r => Assert.True(r.Length <= DisenoTicket.Columnas, $"«{r}» mide {r.Length}"));
    }

    [Fact]
    public void Sin_lo_que_se_ahorro_no_sale_ningun_porcentaje()
    {
        var renglones = Renglones(Ticket(new FormatoTicket(MostrarAhorro: false)));

        Assert.DoesNotContain(renglones, r => r.Contains("Descuento") || r.Contains('%'));
        Assert.Contains(renglones, r => r.EndsWith("Total: $91.00"));
    }

    [Fact]
    public void Sin_descuentos_no_sale_la_linea_del_ticket()
    {
        var sin = Ticket() with { Lineas = [new LineaTicketDto("1", "GOMA GRANDE", 6000, 6000)], TotalCentavos = 6000 };

        Assert.DoesNotContain(Renglones(sin), r => r.Contains("Descuentos"));
    }

    [Fact]
    public void Con_la_letra_b_y_la_letra_alta_tambien_sale()
    {
        var renglones = Renglones(Ticket(new FormatoTicket(Letra: LetraTicket.B, Tamano: TamanoLetraTicket.Alta)));

        Assert.Contains(renglones, r => r.EndsWith("Descuentos: -$9.00 (9 %)"));
        Assert.All(renglones, r => Assert.True(r.Length <= 64, $"«{r}» mide {r.Length}"));
    }
}
