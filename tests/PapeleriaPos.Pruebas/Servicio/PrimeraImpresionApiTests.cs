using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El ticket que no salió al cobrar se imprime sin pedir motivo (paridad con eleventa, 2026-10-22): con «Al cobrar» en
/// «Ofrecerlo» o «No imprimirlo», o si no salió de la impresora y se dejó con «No imprimirlo», su primera impresión no es una
/// reimpresión. Queda en la bitácora como primera impresión, una sola vez; después, y si se canceló o se devolvió algo, se
/// reimprime con su motivo, como siempre.
/// </summary>
public sealed class PrimeraImpresionApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno, ProductoVentaDto Producto);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Caja> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var cajero = await Entrar("cajero", "1111");
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var paquete = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        return new Caja(http, caja, cajero, await Entrar("dueno", "3333"), turno, paquete.Productos.First(p => p.Precios.Any(x => x.PresentacionId == null)));
    }

    private static async Task AlCobrarAsync(Caja c, TicketAlCobrar alCobrar) =>
        await Leer<ConfiguracionTicketDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/ticket/al-cobrar",
            new SolicitudAlCobrar(c.Dueno.UsuarioId, alCobrar)));

    private static async Task<TicketDto> CobrarAsync(Caja c) =>
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(c.Producto.VarianteId, c.Producto.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 100000)])));

    private static Task<HttpResponseMessage> PrimeraAsync(Caja c, Guid ventaId) =>
        c.Http.PostAsJsonAsync($"api/ventas/{ventaId}/primera-impresion", new SolicitudPrimeraImpresion(c.Cajero.UsuarioId, c.Datos.Id));

    private static async Task<VentaDeUnDiaDto> DelDiaAsync(Caja c, Guid ventaId) =>
        (await c.Http.GetFromJsonAsync<VentasDeUnDiaDto>($"api/cajas/{c.Datos.Id}/ventas?usuarioId={c.Cajero.UsuarioId}&caja={c.Datos.Id}"))!
            .Ventas.Single(v => v.Id == ventaId);

    private static async Task SeReimprimeConMotivoAsync(HttpResponseMessage respuesta, string dice)
    {
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var error = (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!;
        Assert.Equal(SolicitudPrimeraImpresion.CodigoConMotivo, error.Codigo);
        Assert.Contains(dice, error.Mensaje);
    }

    private async Task<List<string>> DetallesAsync(Guid ventaId)
    {
        await using var db = servicio.Base();
        return await db.Bitacora.AsNoTracking().Where(b => b.VentaId == ventaId).Select(b => b.Detalle).ToListAsync();
    }

    private async Task<List<string>> BitacoraAsync(Guid ventaId)
    {
        await using var db = servicio.Base();
        return await db.Bitacora.AsNoTracking().Where(b => b.VentaId == ventaId && b.Accion == AccionControlada.Reimprimir)
            .OrderBy(b => b.FechaUtc).Select(b => b.Detalle).ToListAsync();
    }

    [Fact]
    public async Task Sin_imprimir_al_cobrar_la_primera_sale_sin_motivo_una_vez_y_queda_en_la_bitacora()
    {
        var c = await CajaAsync();
        await AlCobrarAsync(c, TicketAlCobrar.NoImprimir);
        try
        {
            var ticket = await CobrarAsync(c);
            var id = ticket.VentaId!.Value;
            Assert.True((await DelDiaAsync(c, id)).SinImprimir);

            var impreso = await Leer<TicketDto>(await PrimeraAsync(c, id));

            Assert.Equal(ticket.Folio, impreso.Folio);
            Assert.Equal([$"Primera impresión del ticket {ticket.Folio}: no salió al cobrar (no es una reimpresión)"], await BitacoraAsync(id));
            Assert.False((await DelDiaAsync(c, id)).SinImprimir);
            // Ya salió: otra vez es una reimpresión, con su motivo.
            await SeReimprimeConMotivoAsync(await PrimeraAsync(c, id), "ya salió");
            Assert.Single(await BitacoraAsync(id));

            // «Ofrecerlo» tampoco sale al cobrar.
            await AlCobrarAsync(c, TicketAlCobrar.Ofrecer);
            Assert.True((await DelDiaAsync(c, (await CobrarAsync(c)).VentaId!.Value)).SinImprimir);
        }
        finally
        {
            await AlCobrarAsync(c, TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Con «Imprimir el ticket», salió al cobrar: imprimirlo es una reimpresión. Si la impresora no lo sacó y se dejó con «No
    /// imprimirlo», la caja lo dice y su primera impresión ya no pide motivo. Reimprimir con motivo también cuenta como que salió.
    /// </summary>
    [Fact]
    public async Task Lo_que_salio_al_cobrar_se_reimprime_con_motivo_salvo_que_se_haya_dejado_sin_imprimir()
    {
        var c = await CajaAsync();
        var ticket = await CobrarAsync(c);
        var id = ticket.VentaId!.Value;

        Assert.False((await DelDiaAsync(c, id)).SinImprimir);
        await SeReimprimeConMotivoAsync(await PrimeraAsync(c, id), "ya salió");

        var dejado = await c.Http.PostAsJsonAsync($"api/ventas/{id}/sin-imprimir", new SolicitudTicketSinImprimir(c.Cajero.UsuarioId, c.Datos.Id));
        Assert.Equal(HttpStatusCode.NoContent, dejado.StatusCode);
        Assert.True((await DelDiaAsync(c, id)).SinImprimir);

        // Queda en la bitácora que se dejó sin imprimir.
        Assert.Contains($"El ticket {ticket.Folio} no salió de la impresora y se dejó sin imprimir: su primera impresión no pedirá motivo",
            await DetallesAsync(id));

        // Si se reimprime con su motivo (abajo, «Reimprimir»), ya salió.
        var reimpreso = await Leer<TicketDto>(await c.Http.PostAsJsonAsync($"api/ventas/{id}/reimpresion",
            new SolicitudReimpresion(c.Cajero.UsuarioId, c.Datos.Id, "Lo pidió")));
        Assert.Equal(1, reimpreso.Impresiones);
        Assert.False((await DelDiaAsync(c, id)).SinImprimir);
        await SeReimprimeConMotivoAsync(await PrimeraAsync(c, id), "ya salió");

        // Un «No imprimirlo» de un trabajo de antes de esa reimpresión (revisión del PR #133) ya no la marca: después salió otro.
        var deAntes = await c.Http.PostAsJsonAsync($"api/ventas/{id}/sin-imprimir", new SolicitudTicketSinImprimir(c.Cajero.UsuarioId, c.Datos.Id, 0));
        Assert.Equal(HttpStatusCode.NoContent, deAntes.StatusCode);
        Assert.False((await DelDiaAsync(c, id)).SinImprimir);
        await SeReimprimeConMotivoAsync(await PrimeraAsync(c, id), "ya salió");
        // El de esa reimpresión (si no hubiera salido) sí.
        await c.Http.PostAsJsonAsync($"api/ventas/{id}/sin-imprimir", new SolicitudTicketSinImprimir(c.Cajero.UsuarioId, c.Datos.Id, reimpreso.Impresiones));
        Assert.True((await DelDiaAsync(c, id)).SinImprimir);
    }

    /// <summary>Lo cancelado no se vuelve a marcar sin imprimir: si lo piden, se reimprime completo, con su motivo.</summary>
    [Fact]
    public async Task Lo_cancelado_no_se_marca_sin_imprimir()
    {
        var c = await CajaAsync();
        var id = (await CobrarAsync(c)).VentaId!.Value;
        var encargado = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{id}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se equivocó de producto", null)));

        await c.Http.PostAsJsonAsync($"api/ventas/{id}/sin-imprimir", new SolicitudTicketSinImprimir(c.Cajero.UsuarioId, c.Datos.Id));

        Assert.False((await DelDiaAsync(c, id)).SinImprimir);
        await using var db = servicio.Base();
        Assert.False(await db.Ventas.AsNoTracking().Where(v => v.Id == id).Select(v => v.TicketSinImprimir).SingleAsync());
    }

    /// <summary>Lo cancelado (o con algo devuelto) saldría completo: no se ofrece sin motivo, y el servicio tampoco lo deja.</summary>
    [Fact]
    public async Task Lo_cancelado_no_sale_sin_motivo()
    {
        var c = await CajaAsync();
        await AlCobrarAsync(c, TicketAlCobrar.NoImprimir);
        try
        {
            var id = (await CobrarAsync(c)).VentaId!.Value;
            var encargado = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
            await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{id}/cancelacion",
                new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se equivocó de producto", null)));

            Assert.False((await DelDiaAsync(c, id)).SinImprimir);
            await SeReimprimeConMotivoAsync(await PrimeraAsync(c, id), "se canceló o se devolvió algo");
            Assert.Empty(await BitacoraAsync(id));
        }
        finally
        {
            await AlCobrarAsync(c, TicketAlCobrar.Imprimir);
        }
    }
}
