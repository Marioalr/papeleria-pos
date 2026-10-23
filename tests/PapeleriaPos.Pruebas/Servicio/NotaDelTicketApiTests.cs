using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La nota del ticket (F4 al cobrar, como en eleventa; 2026-10-22): se guarda con la venta, sale en su ticket y en sus
/// reimpresiones, y en «Ventas del día». Lo que se cobró sin conexión llega con la suya en la bandeja.
/// </summary>
public sealed class NotaDelTicketApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Venta(HttpClient Http, CajaDto Caja, SesionDto Cajero, TurnoDto Turno, ProductoVentaDto Producto, long Precio);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Venta> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var paquete = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        var producto = paquete.Productos.First(p => p.Precios.Any(x => x.PresentacionId == null));
        var precio = producto.Precios.Where(x => x.PresentacionId == null).MinBy(x => x.CantidadMinimaMilesimas)!.PrecioCentavos;
        return new Venta(http, caja, cajero, turno, producto, precio);
    }

    private static Task<HttpResponseMessage> CobrarAsync(Venta v, string? nota) =>
        v.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(v.Caja.Id, v.Turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(v.Producto.VarianteId, v.Producto.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 100000)],
            Nota: nota));

    [Fact]
    public async Task La_nota_se_guarda_con_la_venta_y_sale_en_el_ticket_en_la_reimpresion_y_en_las_ventas_del_dia()
    {
        var v = await PrepararAsync();

        var ticket = await Leer<TicketDto>(await CobrarAsync(v, "  Se entrega en la escuela,\n salón 3-B "));

        Assert.Equal("Se entrega en la escuela, salón 3-B", ticket.Nota);
        Assert.Contains("Nota: Se entrega en la escuela, salón 3-B", TicketTexto.Generar(DisenoTicket.Armar(ticket)));
        await using (var db = servicio.Base())
            Assert.Equal("Se entrega en la escuela, salón 3-B", (await db.Ventas.AsNoTracking().SingleAsync(x => x.Id == ticket.VentaId)).Nota);

        var reimpreso = await Leer<TicketDto>(await v.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/reimpresion",
            new SolicitudReimpresion(v.Cajero.UsuarioId, v.Caja.Id, "Lo pidió el cliente")));
        Assert.Equal(ticket.Nota, reimpreso.Nota);
        Assert.Equal(ticket.Nota, (await v.Http.GetFromJsonAsync<TicketDto>($"api/ventas/{ticket.VentaId}/ticket"))!.Nota);

        var delDia = (await v.Http.GetFromJsonAsync<VentasDeUnDiaDto>($"api/cajas/{v.Caja.Id}/ventas?usuarioId={v.Cajero.UsuarioId}&caja={v.Caja.Id}"))!;
        Assert.Equal(ticket.Nota, delDia.Ventas.Single(x => x.Id == ticket.VentaId).Nota);

        // Sin nota, como siempre.
        var sinNota = await Leer<TicketDto>(await CobrarAsync(v, "   "));
        Assert.Null(sinNota.Nota);
        Assert.DoesNotContain("Nota:", TicketTexto.Generar(DisenoTicket.Armar(sinNota)));
    }

    [Fact]
    public async Task Una_nota_que_no_cabe_en_dos_renglones_no_se_cobra()
    {
        var v = await PrepararAsync();
        await using var antes = servicio.Base();
        var ventas = await antes.Ventas.CountAsync();

        var respuesta = await CobrarAsync(v, new string('a', NotaDelTicket.Maximo + 1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal($"La nota del ticket va hasta {NotaDelTicket.Maximo} letras (dos renglones del ticket): tiene {NotaDelTicket.Maximo + 1}.",
            (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        await using var despues = servicio.Base();
        Assert.Equal(ventas, await despues.Ventas.CountAsync());
    }

    [Fact]
    public async Task Lo_cobrado_sin_conexion_llega_con_su_nota()
    {
        var v = await PrepararAsync();
        var paquete = (await v.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{v.Caja.Id}/paquete"))!;
        var subida = new VentaContingenciaDto(Guid.NewGuid(), v.Caja.Id, v.Turno.Id, v.Cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(v.Producto.VarianteId, v.Producto.Presentacion.Id, v.Producto.Descripcion, 1000, v.Precio)],
            [new CobroSolicitado(FormaPago.Efectivo, v.Precio)], Nota: "Para la maestra Lupita");

        Assert.True((await v.Http.PostAsJsonAsync("api/bandeja/ventas", subida)).IsSuccessStatusCode);

        Assert.Equal("Para la maestra Lupita", (await v.Http.GetFromJsonAsync<TicketDto>($"api/ventas/{subida.Id}/ticket"))!.Nota);
    }
}
