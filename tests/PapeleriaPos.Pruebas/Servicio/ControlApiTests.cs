using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

public class ControlApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
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

    [Fact]
    public async Task La_caja_pregunta_permisos_y_el_encargado_autoriza()
    {
        var (http, caja, cajero, _) = await PrepararAsync();

        var decision = await (await http.PostAsJsonAsync("api/permisos", new SolicitudPermiso(cajero.UsuarioId, caja.SucursalId, AccionControlada.CancelarTicket)))
            .Content.ReadFromJsonAsync<DecisionDto>();
        var negada = await http.PostAsJsonAsync("api/autorizaciones",
            new SolicitudAutorizacion(AccionControlada.CancelarTicket, caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "0000", 1000, "Cancelar"));
        var autorizada = await http.PostAsJsonAsync("api/autorizaciones",
            new SolicitudAutorizacion(AccionControlada.CancelarTicket, caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 1000, "Cancelar"));

        Assert.Equal(NivelPermiso.RequiereAutorizacion, decision!.Nivel);
        Assert.Equal(HttpStatusCode.BadRequest, negada.StatusCode);
        Assert.Equal("Encargado de prueba", (await autorizada.Content.ReadFromJsonAsync<AutorizacionDto>())!.Autorizo);
    }

    [Fact]
    public async Task La_salida_de_efectivo_regresa_su_comprobante_y_el_aviso_de_retiro_aparece_al_limite()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var porCajero = await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/configuracion", new SolicitudConfiguracionSucursal("encargado", "2222", 100));
        Assert.Equal(HttpStatusCode.BadRequest, porCajero.StatusCode); // solo el dueño
        Assert.True((await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/configuracion", new SolicitudConfiguracionSucursal("dueno", "3333", 100))).IsSuccessStatusCode);

        var entrada = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos", new SolicitudMovimiento(TipoMovimientoCaja.Entrada, 500, "Cambio", cajero.UsuarioId));
        Assert.True(entrada.IsSuccessStatusCode, await entrada.Content.ReadAsStringAsync());
        var efectivo = await http.GetFromJsonAsync<EfectivoCajaDto>($"api/turnos/{turno.Id}/efectivo");
        Assert.True(efectivo!.PideRetiro);

        var sinMonto = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos", new SolicitudMovimiento(TipoMovimientoCaja.Salida, 0, "Garrafón", cajero.UsuarioId));
        Assert.Equal(HttpStatusCode.BadRequest, sinMonto.StatusCode);
        Assert.Equal("El monto debe ser mayor a cero.", (await sinMonto.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje); // sin « (Parameter …)»

        var salida = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos", new SolicitudMovimiento(TipoMovimientoCaja.Salida, 300, "Garrafón", cajero.UsuarioId));
        var comprobante = (await salida.Content.ReadFromJsonAsync<ComprobanteMovimientoDto>())!;
        Assert.Equal((300L, "Garrafón"), (comprobante.MontoCentavos, comprobante.Motivo));
        var texto = TicketTexto.Generar(DisenoComprobante.Movimiento(comprobante));
        Assert.Contains("SALIDA DE EFECTIVO", texto);
        Assert.Contains("Firma de quien recibe el efectivo", texto);

        Assert.True((await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/configuracion", new SolicitudConfiguracionSucursal("dueno", "3333", null))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Reimprimir_pide_motivo_y_queda_registrado()
    {
        var (http, caja, cajero, turno) = await PrepararAsync();
        var ticket = (await (await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 1000)], [new CobroSolicitado(FormaPago.Efectivo, 1000)]))).Content.ReadFromJsonAsync<TicketDto>())!;

        var sinMotivo = await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/reimpresion", new SolicitudReimpresion(cajero.UsuarioId, caja.Id, null));
        var conMotivo = await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/reimpresion", new SolicitudReimpresion(cajero.UsuarioId, caja.Id, "El cliente la perdió"));

        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal(ticket.Folio, (await conMotivo.Content.ReadFromJsonAsync<TicketDto>())!.Folio);
    }

    [Fact]
    public async Task La_caja_solo_reporta_quitar_productos_a_la_bitacora()
    {
        var (http, caja, cajero, _) = await PrepararAsync();

        var quitar = await http.PostAsJsonAsync("api/bitacora", new SolicitudBitacora(cajero.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.QuitarProducto, "Quitó una goma", 500));
        var falsa = await http.PostAsJsonAsync("api/bitacora", new SolicitudBitacora(cajero.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.CorteZ, "Inventado"));

        Assert.True(quitar.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, falsa.StatusCode);
    }

    [Fact]
    public async Task La_caja_pide_en_una_consulta_todo_lo_que_puede_quien_entro()
    {
        var (http, caja, cajero, _) = await PrepararAsync();

        var permisos = (await http.GetFromJsonAsync<List<PermisoDeSesion>>($"api/usuarios/{cajero.UsuarioId}/permisos?sucursalId={caja.SucursalId}"))!
            .ToDictionary(p => p.Accion);

        Assert.Equal(NivelPermiso.RequiereAutorizacion, permisos[AccionControlada.CancelarTicket].Nivel);
        Assert.Contains("Encargado de prueba", permisos[AccionControlada.CancelarTicket].Autorizan);
        Assert.DoesNotContain("Cajero de prueba", permisos[AccionControlada.CancelarTicket].Autorizan);
        Assert.Contains("Dueño de prueba", permisos[AccionControlada.CambiarPrecio].Autorizan);
        Assert.DoesNotContain("Encargado de prueba", permisos[AccionControlada.CambiarPrecio].Autorizan);
        Assert.Equal(NivelPermiso.NoPermitido, permisos[AccionControlada.CorteZ].Nivel);
    }

    [Fact]
    public async Task El_dueno_cambia_las_reglas_y_el_cambio_se_aplica()
    {
        var (http, caja, cajero, _) = await PrepararAsync();
        var url = $"api/sucursales/{caja.SucursalId}/reglas";
        var reglas = (await http.GetFromJsonAsync<List<ReglaDto>>(url))!;
        var reimprimir = reglas.Single(r => r.Accion == AccionControlada.Reimprimir && r.Rol == Rol.Cajero);
        Assert.Equal(NivelPermiso.Permitido, reimprimir.Nivel);

        var porEncargado = await http.PutAsJsonAsync(url, new SolicitudReglas("encargado", "2222", [reimprimir with { Nivel = NivelPermiso.NoPermitido }]));
        var porDueno = await http.PutAsJsonAsync(url, new SolicitudReglas("dueno", "3333", [reimprimir with { Nivel = NivelPermiso.NoPermitido }]));
        var decision = await (await http.PostAsJsonAsync("api/permisos", new SolicitudPermiso(cajero.UsuarioId, caja.SucursalId, AccionControlada.Reimprimir)))
            .Content.ReadFromJsonAsync<DecisionDto>();

        Assert.Equal(HttpStatusCode.BadRequest, porEncargado.StatusCode);
        Assert.True(porDueno.IsSuccessStatusCode, await porDueno.Content.ReadAsStringAsync());
        Assert.Equal(NivelPermiso.NoPermitido, decision!.Nivel);

        await http.PutAsJsonAsync(url, new SolicitudReglas("dueno", "3333", [reimprimir]));
    }
}
