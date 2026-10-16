using Microsoft.Data.Sqlite;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F12 → listo para teclear lo que pagó, en menos de 50 ms (entrega 5, parte 3, docs/propuesta-interfaz.md, «Rendimiento»):
/// caja.js mueve el cursor a «Pagó con» en la misma tecla, en el navegador, sin esperar a .NET, y le avisa a la pantalla
/// («F12-pago»). Lo que eso necesita de la pantalla, sin depender de lo que tarda la PC: que «Pagó con» ya esté en cuanto el
/// ticket tiene algo, y que lo que hace la pantalla con esa tecla no espere nada, ni lo que se le pregunta a la caja principal.
/// </summary>
public sealed class F12AlCobroTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-f12-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task F12_solo_mueve_el_cursor_y_no_espera_nada()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        // Sin nada en el ticket no hay «Pagó con» (caja.js le deja F12 a la pantalla, que espera la primera línea).
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "pago-nuevo");

        await v.EscanearAsync(a.Codigo!);

        // Ya está, en el mismo dibujo que la línea: caja.js lo encuentra y lo enfoca en la misma tecla.
        var pago = await v.Pantalla.PorIdAsync("pago-nuevo");
        Assert.Equal("input", pago.Etiqueta);
        Assert.True(pago.Atributos.ContainsKey("data-pago"));

        // Con un código preguntándose a la caja principal, que no contesta: lo que la pantalla hace con F12 no lo espera.
        var nuevo = await v.DarDeAltaEnOtraCajaAsync("9900000001207", "Marcatextos recién dado de alta");
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(nuevo.Codigo!);
        var pedidas = v.Red.Cuantas("/api/");
        Task? f12 = null;
        await v.Pantalla.Dispatcher.InvokeAsync(() => f12 = v.Venta.Tecla("F12-pago", ""));

        Assert.True(f12!.IsCompletedSuccessfully);
        Assert.Equal(pedidas, v.Red.Cuantas("/api/"));
        Assert.Contains("Buscando…", await v.Pantalla.TextoAsync());

        // Y el cobro sí espera lo que se busca (nunca se cobra un «buscando…»).
        v.Red.Detenida.SetResult();
        await v.Pantalla.EsperarAsync(t => t.Contains("Marcatextos recién dado de alta") && !t.Contains("Buscando…"), "la línea que se buscaba");
        Assert.Empty(v.Pantalla.Errores);
    }
}
