using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Cómo sale el ticket (Configuración › Ticket, 2026-11-21), como «Al cobrar»: lo cambia quien configura el ticket, se guarda en
/// la base y llega a cada caja con el ticket de cada cobro (en línea) y con su copia, también con lo que cambió (sin conexión).
/// </summary>
public sealed class FormatoDelTicketApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Se_guarda_y_llega_con_el_ticket_de_cada_cobro_y_a_la_copia()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var (cajero, encargado) = (await Entrar("cajero", "1111"), await Entrar("encargado", "2222"));
        Task<HttpResponseMessage> Poner(SesionDto quien, FormatoTicket formato) =>
            http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/ticket/formato", new SolicitudFormatoTicket(quien.UsuarioId, formato));
        await servicio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var nuevo = new FormatoTicket(Letra: LetraTicket.B, MostrarCajero: false, NombresConMayusculas: false);
        try
        {
            Assert.Equal(FormatoTicket.Predeterminado, antes.FormatoDelTicket);
            Assert.Equal(HttpStatusCode.Forbidden, (await Poner(cajero, nuevo)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Poner(encargado, nuevo with { Tamano = (TamanoLetraTicket)5 })).StatusCode);

            Assert.Equal(nuevo, (await Leer<ConfiguracionTicketDto>(await Poner(encargado, nuevo))).Formato);
            Assert.Equal(nuevo, (await http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{caja.SucursalId}/ticket"))!.Formato);
            await servicio.CatalogoAlDiaAsync();

            var turnoRespuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
            var turno = turnoRespuesta.StatusCode == HttpStatusCode.NoContent
                ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
                : await Leer<TurnoDto>(turnoRespuesta);
            var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(null, null, 1000, "RECUERDO DE PRUEBA", 2000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)])));
            Assert.Equal(nuevo, ticket.Formato);
            Assert.Contains("RECUERDO DE PRUEBA", PapeleriaPos.Impresion.TicketTexto.Generar(ticket));

            var cambios = await CopiaPuestaAlDiaTests.CambiosAsync(http, caja.Id, antes);
            Assert.Equal(nuevo, cambios.Ticket!.Formato);
            var completa = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
            Assert.Equal(nuevo, completa.FormatoDelTicket);
            Assert.Equal(nuevo, CopiaAlDia.Aplicar(antes, cambios).FormatoDelTicket);
        }
        finally
        {
            await Leer<ConfiguracionTicketDto>(await Poner(encargado, FormatoTicket.Predeterminado));
        }
    }
}
