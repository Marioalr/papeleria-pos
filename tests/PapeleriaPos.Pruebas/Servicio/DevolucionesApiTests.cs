using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

public class DevolucionesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, TurnoDto Turno)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await respuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        return (http, caja, cajero, turno);
    }

    private static async Task<TicketDto> VenderAsync(HttpClient http, CajaDto caja, SesionDto cajero, TurnoDto turno, long monto, FormaPago forma = FormaPago.Efectivo) =>
        (await (await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", monto)], [new CobroSolicitado(forma, monto, forma == FormaPago.Efectivo ? null : "REF-1")])))
            .Content.ReadFromJsonAsync<TicketDto>())!;

    [Fact]
    public async Task Se_busca_por_folio_y_cancelar_pide_autorizacion_con_codigo()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = await VenderAsync(http, caja, cajero, turno, 2500);

        var venta = await http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{caja.SucursalId}/ventas/folio/{ticket.Folio}");
        var sinAutorizacion = await http.PostAsJsonAsync($"api/ventas/{venta!.Id}/cancelacion", new SolicitudCancelacion(cajero.UsuarioId, caja.Id, "Se equivocó"));
        var autorizacion = (await (await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.CancelarTicket, caja.SucursalId, caja.Id,
            cajero.UsuarioId, "encargado", "2222", 2500, $"Cancelar {ticket.Folio}"))).Content.ReadFromJsonAsync<AutorizacionDto>())!;
        var conAutorizacion = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/cancelacion", new SolicitudCancelacion(cajero.UsuarioId, caja.Id, "Se equivocó", autorizacion.Id));

        Assert.True(venta.PuedeCancelar);
        Assert.Equal(HttpStatusCode.Forbidden, sinAutorizacion.StatusCode);
        Assert.Equal("requiere-autorizacion", (await sinAutorizacion.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        var comprobante = (await conAutorizacion.Content.ReadFromJsonAsync<ComprobanteDevolucionDto>())!;
        Assert.Equal((true, 2500L, "Encargado de prueba"), (comprobante.EsCancelacion, comprobante.ReembolsoEfectivoCentavos, comprobante.Autorizo));
        Assert.Contains("CANCELACIÓN", TicketTexto.Generar(DisenoComprobante.Devolucion(comprobante)));
        var despues = await http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{caja.SucursalId}/ventas/folio/{ticket.Folio}");
        Assert.Equal(EstadoVenta.Cancelada, despues!.Estado);
    }

    private async Task<VentaParaDevolverDto> VentaDeAsync(HttpClient http, CajaDto caja, string folio) =>
        (await http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{caja.SucursalId}/ventas/folio/{folio}"))!;

    /// <summary>Una venta de tres por su común, en efectivo: de ella se devuelve una parte.</summary>
    private static async Task<TicketDto> VenderTresAsync(HttpClient http, CajaDto caja, SesionDto cajero, TurnoDto turno) =>
        (await (await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 3000, "Recuerdo", 1200)], [new CobroSolicitado(FormaPago.Efectivo, 3600)])))
            .Content.ReadFromJsonAsync<TicketDto>())!;

    /// <summary>
    /// Se pierde la respuesta y se oprime otra vez «Devolver lo marcado»: la misma solicitud, con el mismo Id, regresa el mismo
    /// comprobante. Una sola devolución y un solo reembolso en el corte. Antes se registraba otra.
    /// </summary>
    [Fact]
    public async Task La_misma_devolucion_repetida_se_hace_una_vez_y_regresa_su_comprobante()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = await VenderTresAsync(http, caja, cajero, turno);
        var venta = await VentaDeAsync(http, caja, ticket.Folio);
        var solicitud = new SolicitudDevolucion(cajero.UsuarioId, caja.Id, "Defecto", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)],
            null, Guid.NewGuid());
        var antes = await EsperadoAsync(http, turno, cajero);

        var primera = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud);
        var segunda = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (primera.StatusCode, segunda.StatusCode));
        var (uno, dos) = ((await primera.Content.ReadFromJsonAsync<ComprobanteDevolucionDto>())!, (await segunda.Content.ReadFromJsonAsync<ComprobanteDevolucionDto>())!);
        Assert.Equal((uno.Folio, uno.TotalCentavos, uno.ReembolsoEfectivoCentavos, uno.FechaLocal), (dos.Folio, dos.TotalCentavos, dos.ReembolsoEfectivoCentavos, dos.FechaLocal));
        Assert.Equal(1000, (await VentaDeAsync(http, caja, ticket.Folio)).Lineas.Single().CantidadDevueltaMilesimas);
        Assert.Equal(antes - uno.ReembolsoEfectivoCentavos, await EsperadoAsync(http, turno, cajero));

        // Otra devolución, con su Id, sí es otra.
        var otra = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud with { Id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, otra.StatusCode);
        Assert.Equal(2000, (await VentaDeAsync(http, caja, ticket.Folio)).Lineas.Single().CantidadDevueltaMilesimas);
    }

    /// <summary>Devolverlo todo y cancelar, repetidos: regresan su comprobante. Antes la segunda salía como error, aunque sí quedó.</summary>
    [Fact]
    public async Task Devolver_todo_y_cancelar_repetidos_regresan_su_comprobante()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var deTodo = await VentaDeAsync(http, caja, (await VenderTresAsync(http, caja, cajero, turno)).Folio);
        var devolverTodo = new SolicitudDevolucion(dueno.UsuarioId, caja.Id, "Defecto", [new LineaDevolucionSolicitada(deTodo.Lineas.Single().LineaId, 3000)], null, Guid.NewGuid());
        var cancelada = await VentaDeAsync(http, caja, (await VenderTresAsync(http, caja, cajero, turno)).Folio);
        var cancelar = new SolicitudCancelacion(dueno.UsuarioId, caja.Id, "Se equivocó", null, Guid.NewGuid());

        foreach (var _ in new[] { 1, 2 })
        {
            var devuelta = await http.PostAsJsonAsync($"api/ventas/{deTodo.Id}/devoluciones", devolverTodo);
            Assert.Equal(HttpStatusCode.OK, devuelta.StatusCode);
            Assert.Equal(3600, (await devuelta.Content.ReadFromJsonAsync<ComprobanteDevolucionDto>())!.TotalCentavos);
            var cancelacion = await http.PostAsJsonAsync($"api/ventas/{cancelada.Id}/cancelacion", cancelar);
            Assert.Equal(HttpStatusCode.OK, cancelacion.StatusCode);
            Assert.True((await cancelacion.Content.ReadFromJsonAsync<ComprobanteDevolucionDto>())!.EsCancelacion);
        }
    }

    /// <summary>
    /// Se perdió la respuesta y la cajera cambió lo escrito sin volver a buscar el ticket: con el mismo Id ya no se contesta como si
    /// se hubiera hecho lo nuevo. El error dice lo que de verdad quedó y lo que hay que entregar. Antes la ventana se cerraba sin error.
    /// </summary>
    [Fact]
    public async Task El_mismo_Id_con_otra_cosa_dice_lo_que_ya_quedo()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var ticket = await VenderTresAsync(http, caja, cajero, turno);
        var venta = await VentaDeAsync(http, caja, ticket.Folio);
        var linea = venta.Lineas.Single().LineaId;
        var solicitud = new SolicitudDevolucion(dueno.UsuarioId, caja.Id, "Defecto", [new LineaDevolucionSolicitada(linea, 1000)], null, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud)).StatusCode);

        var respuestas = new[]
        {
            await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud with { Lineas = [new LineaDevolucionSolicitada(linea, 2000)] }),
            await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones", solicitud with { Lineas = [new LineaDevolucionSolicitada(linea, 1000, RegresaAlInventario: false)] }),
            await http.PostAsJsonAsync($"api/ventas/{venta.Id}/cancelacion", new SolicitudCancelacion(dueno.UsuarioId, caja.Id, "Defecto", null, solicitud.Id)),
        };

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
            Assert.Equal("Ya quedó registrada una devolución de este ticket por $12.00: entrega $12.00 en efectivo. Vuelve a buscarlo para ver lo que quedó.",
                (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        }
        var despues = await VentaDeAsync(http, caja, ticket.Folio);
        Assert.Equal((EstadoVenta.Pagada, 1000L), (despues.Estado, despues.Lineas.Single().CantidadDevueltaMilesimas));

        // Lo mismo que se pidió sigue regresando su comprobante, aunque venga por partes.
        var repetida = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones",
            solicitud with { Lineas = [new LineaDevolucionSolicitada(linea, 400), new LineaDevolucionSolicitada(linea, 600)] });
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
    }

    /// <summary>Al revés: ya se canceló, y con el mismo Id se pide devolver una parte.</summary>
    [Fact]
    public async Task El_mismo_Id_de_una_cancelacion_no_devuelve_una_parte()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var venta = await VentaDeAsync(http, caja, (await VenderTresAsync(http, caja, cajero, turno)).Folio);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"api/ventas/{venta.Id}/cancelacion", new SolicitudCancelacion(dueno.UsuarioId, caja.Id, "Se equivocó", null, id))).StatusCode);

        var respuesta = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/devoluciones",
            new SolicitudDevolucion(dueno.UsuarioId, caja.Id, "Defecto", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)], null, id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Contains("la cancelación de este ticket por $36.00: entrega $36.00", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    /// <summary>Lo que el corte espera en efectivo en ese turno.</summary>
    private static async Task<long> EsperadoAsync(HttpClient http, TurnoDto turno, SesionDto usuario) =>
        (await (await http.PostAsync($"api/turnos/{turno.Id}/corte-x?usuarioId={usuario.UsuarioId}", null)).Content.ReadFromJsonAsync<CorteDto>())!.EsperadoCentavos;

    /// <summary>Se canceló el ticket: su transferencia ya no está por verificar. Antes el encargado revisaba en el banco algo cancelado.</summary>
    [Fact]
    public async Task La_transferencia_de_un_ticket_cancelado_ya_no_se_lista()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = await VenderAsync(http, caja, cajero, turno, 4100, FormaPago.Transferencia);
        var venta = await VentaDeAsync(http, caja, ticket.Folio);
        Assert.Contains(await http.GetFromJsonAsync<List<TransferenciaDto>>($"api/sucursales/{caja.SucursalId}/transferencias") ?? [], t => t.Folio == ticket.Folio);
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;

        var cancelada = await http.PostAsJsonAsync($"api/ventas/{venta.Id}/cancelacion", new SolicitudCancelacion(dueno.UsuarioId, caja.Id, "Se equivocó"));

        Assert.Equal(HttpStatusCode.OK, cancelada.StatusCode);
        Assert.DoesNotContain(await http.GetFromJsonAsync<List<TransferenciaDto>>($"api/sucursales/{caja.SucursalId}/transferencias") ?? [], t => t.Folio == ticket.Folio);
    }

    [Fact]
    public async Task Las_transferencias_aparecen_por_verificar_y_el_encargado_las_verifica()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = await VenderAsync(http, caja, cajero, turno, 4000, FormaPago.Transferencia);

        var lista = await http.GetFromJsonAsync<List<TransferenciaDto>>($"api/sucursales/{caja.SucursalId}/transferencias");
        var transferencia = lista!.Single(t => t.Folio == ticket.Folio);
        var encargado = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var verificada = await http.PostAsJsonAsync($"api/cobros/{transferencia.CobroId}/verificacion", new SolicitudVerificacion(encargado.UsuarioId, true));

        Assert.Equal(Verificacion.PorVerificar, transferencia.Verificacion);
        Assert.True(verificada.IsSuccessStatusCode, await verificada.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task La_bitacora_la_consulta_el_encargado_no_el_cajero()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        await VenderAsync(http, caja, cajero, turno, 1000);
        var hoy = DateOnly.FromDateTime(DateTime.Now);

        // Con la sesión, sin volver a escribir el PIN (la Configuración nueva, entrega 7).
        var encargado = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))).Content.ReadFromJsonAsync<SesionDto>())!;
        string Ruta(SesionDto quien) => $"api/cajas/{caja.Id}/bitacora?usuarioId={quien.UsuarioId}&desde={hoy.AddDays(-1):yyyy-MM-dd}&hasta={hoy.AddDays(1):yyyy-MM-dd}";
        var porCajero = await http.GetAsync(Ruta(cajero));
        var registros = await http.GetFromJsonAsync<List<BitacoraDto>>(Ruta(encargado));

        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal("Esto lo hace quien tiene el permiso «Consultar la bitácora»: Encargado de prueba o Dueño de prueba.",
            (await porCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Contains(registros!, r => r.Accion == AccionControlada.ArticuloComun && r.Usuario == "Cajero de prueba");
    }
}
