using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Servicio;

public class ConfiguracionTicketApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static string LogoBase64()
    {
        using var mapa = new SKBitmap(300, 100);
        using (var lienzo = new SKCanvas(mapa))
        {
            lienzo.Clear(SKColors.White);
            using var negro = new SKPaint { Color = SKColors.Black };
            lienzo.DrawRect(0, 0, 150, 100, negro);
        }
        using var imagen = SKImage.FromBitmap(mapa);
        return Convert.ToBase64String(imagen.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    private static async Task<SesionDto> EntrarAsync(HttpClient http, string usuario, string pin) =>
        (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;

    /// <summary>
    /// Con la sesión de quien entró a la caja (la Configuración nueva, entrega 7): el encargado (con «Configurar el ticket»)
    /// sin volver a escribir su PIN; el cajero no, y se le dice quién.
    /// </summary>
    [Fact]
    public async Task El_encargado_cambia_textos_y_logo_y_el_ticket_los_usa()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var url = $"api/cajas/{caja.Id}/configuracion/ticket";
        var cajero = await EntrarAsync(http, "cajero", "1111");
        var encargado = await EntrarAsync(http, "encargado", "2222");

        var antes = await http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{caja.SucursalId}/ticket");
        Assert.Equal("PAPELERÍA", antes!.Encabezado[0]); // lo de appsettings mientras no se guarde nada

        var porCajero = await http.PutAsJsonAsync($"{url}/textos", new SolicitudTextosTicket(cajero.UsuarioId, ["X"], []));
        Assert.Equal(HttpStatusCode.Forbidden, porCajero.StatusCode);
        Assert.Equal("Esto lo hace quien tiene el permiso «Configurar el ticket»: Encargado de prueba o Dueño de prueba.",
            (await porCajero.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);

        var textos = await http.PutAsJsonAsync($"{url}/textos", new SolicitudTextosTicket(encargado.UsuarioId, ["Papelería La Prueba", "Calle 5"], ["¡Gracias!"]));
        Assert.True(textos.IsSuccessStatusCode, await textos.Content.ReadAsStringAsync());
        var conLogo = await (await http.PutAsJsonAsync($"{url}/logo", new SolicitudLogo(encargado.UsuarioId, LogoBase64()))).Content.ReadFromJsonAsync<ConfiguracionTicketDto>();
        Assert.Equal((300, 100), (conLogo!.Logo!.Ancho, conLogo.Logo.Alto));
        Assert.NotNull(conLogo.LogoPngBase64);

        // En la bitácora: lo que cambió, a nombre del encargado; y el intento del cajero, negado.
        await using (var db = servicio.Base())
        {
            var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTicket).ToListAsync();
            Assert.Contains(bitacora, b => b.UsuarioId == encargado.UsuarioId && b.CajaId == caja.Id && b.Detalle == "Cambió el encabezado y el pie del ticket");
            Assert.Contains(bitacora, b => b.UsuarioId == encargado.UsuarioId && b.Detalle == "Cambió el logotipo del ticket");
            Assert.Contains(bitacora, b => b.UsuarioId == cajero.UsuarioId && b.Resultado == ResultadoBitacora.Negado);
        }

        // Una venta: su ticket trae el encabezado, el pie y el logo guardados.
        var turnoRespuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = turnoRespuesta.StatusCode == HttpStatusCode.NoContent
            ? (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await turnoRespuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        var ticket = (await (await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 2000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)]))).Content.ReadFromJsonAsync<TicketDto>())!;
        Assert.Equal(["Papelería La Prueba", "Calle 5"], ticket.Encabezado);
        Assert.Equal(["¡Gracias!"], ticket.Pie);
        Assert.Equal(conLogo.Logo.BitsBase64, ticket.Logo!.BitsBase64);

        var sinLogo = await (await http.PutAsJsonAsync($"{url}/logo", new SolicitudLogo(encargado.UsuarioId, null))).Content.ReadFromJsonAsync<ConfiguracionTicketDto>();
        Assert.Null(sinLogo!.Logo);
    }

    [Fact]
    public async Task Un_renglon_que_no_cabe_se_rechaza_con_su_motivo()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await EntrarAsync(http, "encargado", "2222");

        var respuesta = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/ticket/textos",
            new SolicitudTextosTicket(encargado.UsuarioId, [new string('x', 60)], []));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("no cabe", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }
}
