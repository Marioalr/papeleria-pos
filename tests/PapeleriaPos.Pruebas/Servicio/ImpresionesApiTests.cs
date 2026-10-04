using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

public class ImpresionesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private HttpClient Http() => servicio.Fabrica.CreateClient();

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que mandaría el agente: una hoja carta con 5 % de negro por página (la página ISO).</summary>
    private static TrabajoMedidoDto Trabajo(CajaDto caja, string documento, int paginas = 1, bool color = false) =>
        new(Guid.NewGuid(), caja.Id, null, "PC-PRUEBA", "ControlPrint Lab (WF-6590)", 7, documento, "cajero", DateTime.UtcNow, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, color, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => color
                ? new CoberturaPaginaDto(0.05, 0.05, 0.05, 0.05, 0.1, 93.5, true)
                : new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(),
            true, null, null);

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno)> PrepararAsync()
    {
        var http = Http();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return (http, caja, cajero, dueno, turno);
    }

    private static Task<List<ImpresionPorCobrarDto>> PorCobrarAsync(HttpClient http, CajaDto caja, SesionDto quien) =>
        http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>($"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={quien.UsuarioId}")!;

    [Fact]
    public async Task El_servicio_le_pone_precio_y_solo_el_dueno_ve_el_costo()
    {
        var (http, caja, cajero, dueno, _) = await PrepararAsync();
        var trabajo = Trabajo(caja, "Tarea de historia.docx", paginas: 3, color: true);

        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        var paraCajero = (await PorCobrarAsync(http, caja, cajero)).Where(t => t.Id == trabajo.Id).ToList();
        var paraDueno = (await PorCobrarAsync(http, caja, dueno)).Single(t => t.Id == trabajo.Id);

        var impresion = Assert.Single(paraCajero);
        Assert.Equal("Tarea de historia.docx", impresion.Documento);
        Assert.Equal(3, impresion.Paginas);
        Assert.True(impresion.PrecioCentavos >= 100 && impresion.PrecioCentavos % 100 == 0, "Precio redondeado a pesos completos.");
        Assert.Null(impresion.Costo);
        Assert.NotNull(paraDueno.Costo);
        Assert.True(paraDueno.Costo!.TotalCentavos < paraDueno.PrecioCentavos);
    }

    [Fact]
    public async Task Se_cobran_en_una_sola_linea_y_al_cancelar_vuelven_a_estar_por_cobrar()
    {
        var (http, caja, cajero, _, turno) = await PrepararAsync();
        var uno = Trabajo(caja, "Credencial.pdf");
        var dos = Trabajo(caja, "Mapa.pdf", color: true);
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", uno));
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", dos));
        var precios = (await PorCobrarAsync(http, caja, cajero)).Where(t => t.Id == uno.Id || t.Id == dos.Id).Sum(t => t.PrecioCentavos);

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas",
            new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 100000)], Impresiones: [uno.Id, dos.Id])));
        var otraVez = await http.PostAsJsonAsync("api/ventas",
            new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 100000)], Impresiones: [uno.Id]));
        var despues = await PorCobrarAsync(http, caja, cajero);

        var linea = Assert.Single(ticket.Lineas);
        Assert.Equal("Impresiones", linea.Descripcion);
        Assert.Equal(precios, ticket.TotalCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        Assert.DoesNotContain(despues, t => t.Id == uno.Id || t.Id == dos.Id);

        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<ComprobanteDevolucionDto>(await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, caja.Id, "El cliente ya no las quiso")));
        var liberadas = await PorCobrarAsync(http, caja, cajero);
        Assert.Contains(liberadas, t => t.Id == uno.Id);
        Assert.Contains(liberadas, t => t.Id == dos.Id);
    }

    [Fact]
    public async Task Una_no_vendida_lleva_motivo_y_sale_de_lo_que_falta_cobrar()
    {
        var (http, caja, cajero, _, _) = await PrepararAsync();
        var trabajo = Trabajo(caja, "Salió corrida.pdf");
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));

        var sinNota = await http.PostAsJsonAsync($"api/impresiones/{trabajo.Id}/no-vendida", new SolicitudNoVendida(cajero.UsuarioId, MotivoNoVendida.Otro, null));
        var conMotivo = await http.PostAsJsonAsync($"api/impresiones/{trabajo.Id}/no-vendida", new SolicitudNoVendida(cajero.UsuarioId, MotivoNoVendida.SalioMal, null));

        Assert.Equal(HttpStatusCode.BadRequest, sinNota.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, conMotivo.StatusCode);
        Assert.DoesNotContain(await PorCobrarAsync(http, caja, cajero), t => t.Id == trabajo.Id);
    }
}
