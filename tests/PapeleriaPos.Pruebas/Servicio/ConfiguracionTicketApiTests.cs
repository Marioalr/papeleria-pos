using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo.Contratos;
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

    [Fact]
    public async Task El_encargado_cambia_textos_y_logo_y_el_ticket_los_usa()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var url = $"api/sucursales/{caja.SucursalId}/ticket";

        var antes = await http.GetFromJsonAsync<ConfiguracionTicketDto>(url);
        Assert.Equal("PAPELERÍA", antes!.Encabezado[0]); // lo de appsettings mientras no se guarde nada

        var porCajero = await http.PutAsJsonAsync($"{url}/textos", new SolicitudTextosTicket("cajero", "1111", ["X"], []));
        Assert.Equal(HttpStatusCode.BadRequest, porCajero.StatusCode);

        var textos = await http.PutAsJsonAsync($"{url}/textos", new SolicitudTextosTicket("encargado", "2222", ["Papelería La Prueba", "Calle 5"], ["¡Gracias!"]));
        Assert.True(textos.IsSuccessStatusCode, await textos.Content.ReadAsStringAsync());
        var conLogo = await (await http.PutAsJsonAsync($"{url}/logo", new SolicitudLogo("encargado", "2222", LogoBase64()))).Content.ReadFromJsonAsync<ConfiguracionTicketDto>();
        Assert.Equal((300, 100), (conLogo!.Logo!.Ancho, conLogo.Logo.Alto));
        Assert.NotNull(conLogo.LogoPngBase64);

        // Una venta: su ticket trae el encabezado, el pie y el logo guardados.
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var turnoRespuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = turnoRespuesta.StatusCode == HttpStatusCode.NoContent
            ? (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await turnoRespuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        var ticket = (await (await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 2000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)]))).Content.ReadFromJsonAsync<TicketDto>())!;
        Assert.Equal(["Papelería La Prueba", "Calle 5"], ticket.Encabezado);
        Assert.Equal(["¡Gracias!"], ticket.Pie);
        Assert.Equal(conLogo.Logo.BitsBase64, ticket.Logo!.BitsBase64);

        var sinLogo = await (await http.PutAsJsonAsync($"{url}/logo", new SolicitudLogo("encargado", "2222", null))).Content.ReadFromJsonAsync<ConfiguracionTicketDto>();
        Assert.Null(sinLogo!.Logo);
    }

    [Fact]
    public async Task Un_renglon_que_no_cabe_se_rechaza_con_su_motivo()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();

        var respuesta = await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/ticket/textos",
            new SolicitudTextosTicket("encargado", "2222", [new string('x', 60)], []));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("no cabe", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }
}
