using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// «Al cobrar» (Configuración › Ticket): imprimir el ticket, ofrecerlo o no imprimirlo, en todas las cajas de la sucursal. Lo
/// cambia quien configura el ticket, con su sesión, y queda en la bitácora; llega a cada caja con el ticket de cada cobro (en
/// línea) y con su copia, también con lo que cambió (sin conexión). Lo fiado se imprime siempre.
/// </summary>
public sealed class AlCobrarApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Caja> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private static Task<HttpResponseMessage> PonerAsync(Caja c, SesionDto quien, TicketAlCobrar alCobrar) =>
        c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/ticket/al-cobrar", new SolicitudAlCobrar(quien.UsuarioId, alCobrar));

    private static async Task<TicketAlCobrar> GuardadoAsync(Caja c) =>
        (await c.Http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{c.Datos.SucursalId}/ticket"))!.AlCobrar;

    private static async Task<TurnoDto> TurnoAsync(Caja c)
    {
        var respuesta = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
    }

    /// <summary>Las demás pruebas de esta clase comparten el servicio: se deja como siempre.</summary>
    private static async Task RegresarAsync(Caja c) => await Leer<ConfiguracionTicketDto>(await PonerAsync(c, c.Dueno, TicketAlCobrar.Imprimir));

    /// <summary>
    /// Con la sesión de quien entró (en Administración no se pide autorización: o puede o no puede, dueño 2026-10-15): el
    /// encargado, que configura el ticket, sí; el cajero no, y se le dice quién. Lo que cambió queda en la bitácora; lo mismo
    /// otra vez, no. Lo que no es una de las tres opciones no se guarda.
    /// </summary>
    [Fact]
    public async Task Lo_cambia_quien_configura_el_ticket_y_queda_en_la_bitacora()
    {
        var c = await CajaAsync();
        try
        {
            // Por omisión, lo de siempre: se imprime.
            Assert.Equal(TicketAlCobrar.Imprimir, await GuardadoAsync(c));

            var porCajero = await PonerAsync(c, c.Cajero, TicketAlCobrar.NoImprimir);
            Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
            Assert.Equal("Esto lo hace quien tiene el permiso «Configurar el ticket»: Encargado de prueba o Dueño de prueba.",
                (await porCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
            Assert.Equal(TicketAlCobrar.Imprimir, await GuardadoAsync(c));

            Assert.Equal(TicketAlCobrar.Ofrecer, (await Leer<ConfiguracionTicketDto>(await PonerAsync(c, c.Encargado, TicketAlCobrar.Ofrecer))).AlCobrar);
            Assert.Equal(TicketAlCobrar.Ofrecer, await GuardadoAsync(c));
            await Leer<ConfiguracionTicketDto>(await PonerAsync(c, c.Encargado, TicketAlCobrar.Ofrecer));

            var otra = await PonerAsync(c, c.Encargado, (TicketAlCobrar)7);
            Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);
            Assert.Equal("Al cobrar se imprime el ticket, se ofrece o no se imprime.", (await otra.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
            Assert.Equal(TicketAlCobrar.Ofrecer, await GuardadoAsync(c));

            await using var db = servicio.Base();
            var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTicket).ToListAsync();
            Assert.Single(bitacora, b => b.UsuarioId == c.Encargado.UsuarioId && b.CajaId == c.Datos.Id
                && b.Detalle == "Cambió qué se hace con el ticket al cobrar: «Ofrecerlo»");
            Assert.Contains(bitacora, b => b.UsuarioId == c.Cajero.UsuarioId && b.Resultado == ResultadoBitacora.Negado);
        }
        finally
        {
            await RegresarAsync(c);
        }
    }

    /// <summary>
    /// En línea, el ticket de cada cobro trae lo que dice la sucursal en ese momento: la caja decide con él, sin preguntar
    /// nada más al cobrar. Sin conexión, lo que trae su copia: con lo que cambió (solo el ticket) queda igual que la completa.
    /// </summary>
    [Fact]
    public async Task Llega_con_el_ticket_de_cada_cobro_y_a_la_copia_de_cada_caja()
    {
        var c = await CajaAsync();
        await servicio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        Assert.Equal(TicketAlCobrar.Imprimir, antes.AlCobrar);
        try
        {
            await Leer<ConfiguracionTicketDto>(await PonerAsync(c, c.Dueno, TicketAlCobrar.NoImprimir));
            await servicio.CatalogoAlDiaAsync();

            var turno = await TurnoAsync(c);
            var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
                [new LineaSolicitada(null, null, 1000, "Recuerdo", 2000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)])));
            Assert.Equal((TicketAlCobrar.NoImprimir, TicketAlCobrar.NoImprimir), (ticket.AlCobrar, ticket.LoQueSeHaceAlCobrar));

            var cambios = await CopiaPuestaAlDiaTests.CambiosAsync(c.Http, c.Datos.Id, antes);
            Assert.Null(cambios.PaqueteCompleto);
            Assert.Equal(TicketAlCobrar.NoImprimir, cambios.Ticket!.AlCobrar);
            Assert.Empty(cambios.Productos);
            var completa = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
            var aplicada = CopiaAlDia.Aplicar(antes, cambios);
            Assert.Equal((TicketAlCobrar.NoImprimir, TicketAlCobrar.NoImprimir), (completa.AlCobrar, aplicada.AlCobrar));
            Assert.Equal(completa.Encabezado, aplicada.Encabezado);
            Assert.Equal(completa.Pie, aplicada.Pie);

            // Lo que cambia otra cosa del ticket (el pie) no lo pierde.
            var configuracion = (await c.Http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{c.Datos.SucursalId}/ticket"))!;
            await Leer<ConfiguracionTicketDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/ticket/textos",
                new SolicitudTextosTicket(c.Dueno.UsuarioId, configuracion.Encabezado, ["Gracias por su compra"])));
            await servicio.CatalogoAlDiaAsync();
            var otraVez = CopiaAlDia.Aplicar(aplicada, await CopiaPuestaAlDiaTests.CambiosAsync(c.Http, c.Datos.Id, aplicada));
            Assert.Equal(TicketAlCobrar.NoImprimir, otraVez.AlCobrar);
            Assert.Equal(["Gracias por su compra"], otraVez.Pie);
            await Leer<ConfiguracionTicketDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/ticket/textos",
                new SolicitudTextosTicket(c.Dueno.UsuarioId, configuracion.Encabezado, configuracion.Pie)));
        }
        finally
        {
            await RegresarAsync(c);
        }
    }

    /// <summary>
    /// Lo fiado sale aunque «Al cobrar» diga «No imprimirlo»: el ticket trae «SU CUENTA» y el cliente se lo lleva. El ajuste
    /// llega igual con el ticket; lo que se hace al cobrar, no.
    /// </summary>
    [Fact]
    public async Task Lo_fiado_se_imprime_aunque_no_se_imprima_al_cobrar()
    {
        var c = await CajaAsync();
        try
        {
            await Leer<ConfiguracionTicketDto>(await PonerAsync(c, c.Dueno, TicketAlCobrar.NoImprimir));
            var cliente = await Leer<ClienteRegistradoDto>(await c.Http.PostAsJsonAsync("api/clientes",
                new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, new ClienteRegistradoDto(Guid.Empty, "Al cobrar: Arq. Fiado", 0, null, true))));
            await Leer<ClienteRegistradoDto>(await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
                new SolicitudCreditoCliente(c.Dueno.UsuarioId, c.Datos.SucursalId, 100000, 30)));
            var turno = await TurnoAsync(c);

            var fiado = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
                [new LineaSolicitada(null, null, 1000, "Mercancía", 5000)], [new CobroSolicitado(FormaPago.Credito, 5000)], Ids.Nuevo(), ClienteId: cliente.Id)));
            var deContado = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
                [new LineaSolicitada(null, null, 1000, "Mercancía", 5000)], [new CobroSolicitado(FormaPago.Efectivo, 5000)], Ids.Nuevo(), ClienteId: cliente.Id)));

            Assert.NotNull(fiado.Credito);
            Assert.Equal((TicketAlCobrar.NoImprimir, TicketAlCobrar.Imprimir), (fiado.AlCobrar, fiado.LoQueSeHaceAlCobrar));
            Assert.Equal(TicketAlCobrar.NoImprimir, deContado.LoQueSeHaceAlCobrar);
        }
        finally
        {
            await RegresarAsync(c);
        }
    }
}
