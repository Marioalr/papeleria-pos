using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El ticket que manda el celular llega a la espera de la caja que tiene la venta abierta (docs/celular-como-caja.md, «Mandar el
/// ticket a una caja»): con el nombre del celular en su ficha, sin tocar el ticket que se atiende, con los precios de la caja, y
/// se retoma con F5 como cualquier otro. Mandarlo otra vez no lo duplica.
/// </summary>
public sealed class TicketsDelCelularEnLaVentaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-celular-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Llega_a_la_espera_con_el_nombre_del_celular_sin_tocar_el_ticket_y_F5_lo_retoma()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, conCelular: true);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var atendido = await VentaConCopia.RenglonesAsync(v.Pantalla);

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", v.Caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        // La venta abierta ya pregunta por sus tickets: en el celular, la caja sale lista, con quién atiende y cuántos esperan.
        CajaParaMandarDto? caja = null;
        await VentaConCopia.EsperarAsync(() =>
        {
            caja = celular.GetFromJsonAsync<List<CajaParaMandarDto>>($"api/cajas/{alta.Caja.Id}/para-mandar").Result!.Single(c => c.Id == v.Caja.Id);
            return caja.Recibe;
        }, "la caja lista para recibir");
        Assert.Equal(0, caja!.Esperan);
        Assert.NotNull(caja.Atiende);

        var solicitud = new SolicitudMandarTicket(Guid.NewGuid(), v.Caja.Id, [new RenglonMandado(b.VarianteId, b.Presentacion.Id, null, 2000)]);
        var mandado = await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar", solicitud));
        Assert.True(mandado.Recibido);

        await v.Pantalla.EsperarAsync(t => t.Contains("Llegó un ticket de Celular de Luis"), "el aviso del ticket del celular");
        var texto = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        Assert.Contains($"Llegó un ticket de Celular de Luis: 2 artículos, {Formato.Dinero(mandado.TotalCentavos)}. Retómalo arriba o con F5.", texto);
        Assert.Contains($"Celular de Luis · {Formato.Dinero(mandado.TotalCentavos)}", texto);
        // El que se atendía sigue igual.
        Assert.Equal(atendido, await VentaConCopia.RenglonesAsync(v.Pantalla));

        // F5, con el teclado: el que se atendía pasa a la espera y el del celular queda con sus 2, al precio de la caja.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.StartsWith("2 ", renglon);
        Assert.Contains(b.Descripcion, renglon);
        Assert.Contains("Retomaste el ticket de Celular de Luis.", await v.Pantalla.TextoAsync());

        // Otra vez el mismo (la respuesta no llegó al celular): contesta igual y no sale otra ficha del celular.
        Assert.Equal(mandado, await Leer<TicketMandadoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar", solicitud)));
        await Task.Delay(TicketsDelCelular.Cada * 2);
        Assert.DoesNotContain("Celular de Luis ·", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Empty(v.Pantalla.Errores);
    }
}
