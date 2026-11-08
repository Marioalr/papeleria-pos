using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría (2026-11-04), arreglado con la revisión de #155: con una tarjeta de Control Print en el ticket y Control Print sin contestar,
/// «Agregar al ticket» en F2 la quitaba sin decir nada y el cliente pagaba de menos. Ahora se queda, con lo último que se leyó (al cobrar se
/// revisa contra Control Print, como siempre).
/// </summary>
public sealed class F2SinControlPrintTests : IAsyncLifetime
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"aud-cp-{Guid.NewGuid():N}.db");
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;

    public async Task InitializeAsync()
    {
        _controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")]));
        _servicio = await ServicioDePrueba.CrearAsync(ControlPrintEnLaCajaTests.Ajustes(_controlPrint));
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        ArchivosDePrueba.BorrarBase(_baseLocal);
        await _controlPrint.DisposeAsync();
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, null));

    private async Task<ColaControlPrintDto> ColaDelServicioAsync(Func<ColaControlPrintDto, bool> lista)
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var usuario = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var limite = DateTime.UtcNow.AddSeconds(40);
        while (true)
        {
            var cola = (await http.GetFromJsonAsync<ColaControlPrintDto>($"api/sucursales/{caja.SucursalId}/impresiones/control-print?usuarioId={usuario.UsuarioId}"))!;
            if (lista(cola) || DateTime.UtcNow > limite)
                return cola;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Con_Control_Print_sin_contestar_agregar_en_F2_deja_en_el_ticket_su_tarjeta()
    {
        await ColaDelServicioAsync(c => c.Trabajos.Count == 1);
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("#1234"), "la tarjeta");
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("#1234") == true, "la tarjeta"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregar al ticket"));
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("#1234", renglon);

        _controlPrint.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _controlPrint.Avisar(0);
        var cola = await ColaDelServicioAsync(c => c.Contesta == false);
        Assert.False(cola.Contesta);

        await TeclaAsync(v, "F2");
        await v.Pantalla.EsperarAsync(t => t.Contains("Control Print no contesta"), "el aviso en F2");
        Assert.Contains("Las de Control Print que ya estaban en el ticket se quedan en él", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        var agregar = await v.Pantalla.BotonAsync("Agregar al ticket");
        Assert.False(agregar.Desactivado);
        await v.Pantalla.OprimirAsync(agregar);

        Assert.Contains("#1234", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
    }
}
