using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Qué se hace con el ticket al cobrar (Configuración › Ticket › «Al cobrar»): lo que dice el ajuste, salvo lo fiado, que
/// siempre se imprime (el cliente se lleva «SU CUENTA»: lo que debe y hasta cuándo paga).
/// </summary>
public class AlCobrarTests
{
    private static TicketDto Ticket(TicketAlCobrar alCobrar, params CobroTicketDto[] cobros) => new(
        "A-000123", new DateTime(2026, 10, 17, 12, 0, 0), "Caja 1", "Ana", [new LineaTicketDto("1", "Cuaderno profesional", 4500, 4500)],
        1, 4500, 621, cobros.Length > 0 ? cobros : [new CobroTicketDto(FormaPago.Efectivo, 4500, 5000, 500, null)], [], [], AlCobrar: alCobrar);

    [Theory]
    [InlineData(TicketAlCobrar.Imprimir)]
    [InlineData(TicketAlCobrar.Ofrecer)]
    [InlineData(TicketAlCobrar.NoImprimir)]
    public void Sin_fiado_se_hace_lo_que_dice_el_ajuste(TicketAlCobrar alCobrar)
    {
        Assert.Equal(alCobrar, Ticket(alCobrar).LoQueSeHaceAlCobrar);
        Assert.False(Ticket(alCobrar).SiempreSeImprime);
        // Tarjeta y transferencia, igual: el comprobante es el de la terminal o el del banco.
        Assert.Equal(alCobrar, Ticket(alCobrar, new CobroTicketDto(FormaPago.Tarjeta, 4500, 0, 0, "123"), new CobroTicketDto(FormaPago.Transferencia, 0, 0, 0, null))
            .LoQueSeHaceAlCobrar);
    }

    [Theory]
    [InlineData(TicketAlCobrar.Imprimir)]
    [InlineData(TicketAlCobrar.Ofrecer)]
    [InlineData(TicketAlCobrar.NoImprimir)]
    public void Lo_fiado_siempre_se_imprime(TicketAlCobrar alCobrar)
    {
        // Una parte a crédito y otra en efectivo; y todo con su saldo a favor (también va a su cuenta).
        var fiado = Ticket(alCobrar, new CobroTicketDto(FormaPago.Credito, 3000, 0, 0, null), new CobroTicketDto(FormaPago.Efectivo, 1500, 2000, 500, null))
            with { Credito = new CreditoTicketDto(3000, 38000, new DateOnly(2026, 11, 16)) };
        var aFavor = Ticket(alCobrar, new CobroTicketDto(FormaPago.Credito, 4500, 0, 0, null)) with { Credito = new CreditoTicketDto(4500, -500, null) };

        Assert.Equal(TicketAlCobrar.Imprimir, fiado.LoQueSeHaceAlCobrar);
        Assert.Equal(TicketAlCobrar.Imprimir, aFavor.LoQueSeHaceAlCobrar);
        Assert.True(fiado.SiempreSeImprime && aFavor.SiempreSeImprime);
    }

    /// <summary>
    /// «No imprimirlo» cobrado sin la caja principal (con la copia de la caja) se ofrece, como «Ofrecerlo»: sin ella no hay
    /// reimpresión si el cliente lo pide. Las otras dos, igual que en línea; y lo fiado sale.
    /// </summary>
    [Fact]
    public void Sin_conexion_no_imprimirlo_se_ofrece()
    {
        Assert.Equal(TicketAlCobrar.Ofrecer, (Ticket(TicketAlCobrar.NoImprimir) with { EnContingencia = true }).LoQueSeHaceAlCobrar);
        Assert.Equal(TicketAlCobrar.Ofrecer, (Ticket(TicketAlCobrar.Ofrecer) with { EnContingencia = true }).LoQueSeHaceAlCobrar);
        Assert.Equal(TicketAlCobrar.Imprimir, (Ticket(TicketAlCobrar.Imprimir) with { EnContingencia = true }).LoQueSeHaceAlCobrar);
        var fiado = Ticket(TicketAlCobrar.NoImprimir, new CobroTicketDto(FormaPago.Credito, 4500, 0, 0, null))
            with { Credito = new CreditoTicketDto(4500, 4500, null, Estimado: true), EnContingencia = true };
        Assert.Equal(TicketAlCobrar.Imprimir, fiado.LoQueSeHaceAlCobrar);
    }

    /// <summary>Van como número en la copia y en el ticket, y como texto en la base: el orden no cambia.</summary>
    [Fact]
    public void Las_opciones_y_como_se_llaman()
    {
        Assert.Equal([TicketAlCobrar.Imprimir, TicketAlCobrar.Ofrecer, TicketAlCobrar.NoImprimir], Enum.GetValues<TicketAlCobrar>());
        Assert.Equal(0, (int)TicketAlCobrar.Imprimir);
        Assert.Equal(["Imprimir el ticket", "Ofrecerlo", "No imprimirlo"], Enum.GetValues<TicketAlCobrar>().Select(ConfiguracionTicket.Nombre));
        // Un ticket o una copia de antes, sin el ajuste: se imprime.
        Assert.Equal(TicketAlCobrar.Imprimir, System.Text.Json.JsonSerializer.Deserialize<TicketLocalDto>("""{"encabezado":[],"pie":[]}""",
            System.Text.Json.JsonSerializerOptions.Web)!.AlCobrar);

        var configuracion = new ConfiguracionTicket();
        Assert.Equal(TicketAlCobrar.Imprimir, configuracion.AlCobrar);
        configuracion.CambiarAlCobrar(TicketAlCobrar.Ofrecer);
        Assert.Equal(TicketAlCobrar.Ofrecer, configuracion.AlCobrar);
        Assert.Throws<ArgumentException>(() => configuracion.CambiarAlCobrar((TicketAlCobrar)7));
        Assert.Equal(TicketAlCobrar.Ofrecer, configuracion.AlCobrar);
    }
}
