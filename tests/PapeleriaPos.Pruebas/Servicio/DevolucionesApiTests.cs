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

    [Fact]
    public async Task Las_transferencias_aparecen_por_verificar_y_el_encargado_las_verifica()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = await VenderAsync(http, caja, cajero, turno, 4000, FormaPago.Transferencia);

        var lista = await http.GetFromJsonAsync<List<TransferenciaDto>>($"api/sucursales/{caja.SucursalId}/transferencias");
        var transferencia = lista!.Single(t => t.Folio == ticket.Folio);
        var verificada = await http.PostAsJsonAsync($"api/cobros/{transferencia.CobroId}/verificacion", new SolicitudVerificacion("encargado", "2222", true));

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
