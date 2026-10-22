using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que ModuloCaja le dice a caja.js de las ventanas: la franja de la prueba (la guía, la nota de la tarea) va encima de
/// lo de la venta y, al quitarse, vuelve lo que la venta tenía abierto.
/// </summary>
public class ModuloCajaTests
{
    /// <summary>Hace de caja.js: guarda lo que se le manda a <c>ventanaAbierta</c>.</summary>
    private sealed class CajaJsFalso : IJSRuntime, IJSObjectReference
    {
        public List<(bool Abierta, bool EnPanel)> Ventana { get; } = [];

        /// <summary>El tercero de cada llamada: si el panel es el de «Varios» (Ins), con su Enter en el cuadro de captura.</summary>
        public List<bool> Varios { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "ventanaAbierta")
            {
                Ventana.Add(((bool)args![0]!, (bool)args[1]!));
                Varios.Add((bool)args[2]!);
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Al_cerrar_la_guia_vuelve_el_panel_que_la_venta_tenia_abierto()
    {
        var js = new CajaJsFalso();
        var modulo = new ModuloCaja(js);

        await modulo.VentanaAbiertaAsync(true, enPanel: true); // el verificador, a la derecha del ticket
        await modulo.VentanaDeLaFranjaAsync(true); // la guía, encima
        await modulo.VentanaDeLaFranjaAsync(false);

        // Antes se mandaba «sin ventana» al cerrar la guía, con el panel todavía abierto.
        Assert.Equal([(true, true), (true, false), (true, true)], js.Ventana);
    }

    [Fact]
    public async Task Lo_que_la_venta_cierra_mientras_la_guia_esta_abierta_se_queda_cerrado()
    {
        var js = new CajaJsFalso();
        var modulo = new ModuloCaja(js);

        await modulo.VentanaAbiertaAsync(true, enPanel: true);
        await modulo.VentanaDeLaFranjaAsync(true);
        await modulo.VentanaAbiertaAsync(false); // Esc en la guía también cierra el panel de atrás
        await modulo.VentanaDeLaFranjaAsync(false);

        // Mientras la guía está abierta, sigue siendo una ventana para caja.js; al cerrarla, ya no hay ninguna.
        Assert.Equal([(true, true), (true, false), (true, false), (false, false)], js.Ventana);
    }

    [Fact]
    public async Task Sin_la_pantalla_de_venta_la_franja_no_revive_sus_ventanas()
    {
        var js = new CajaJsFalso();
        var modulo = new ModuloCaja(js);

        await modulo.VentanaAbiertaAsync(true);
        await modulo.QuitarTeclasAsync(); // la pantalla de venta se fue con su ventana abierta
        await modulo.VentanaDeLaFranjaAsync(true);
        await modulo.VentanaDeLaFranjaAsync(false);

        Assert.Equal((false, false), js.Ventana[^1]);
    }

    /// <summary>
    /// «Varios» (Ins, 2026-10-22) va en la misma llamada que el panel: ningún Enter del lector cae entre una y otra. La guía de
    /// la prueba encima lo tapa (su nota no le manda Enter a la venta) y al quitarla vuelve.
    /// </summary>
    [Fact]
    public async Task Varios_va_en_la_misma_llamada_que_el_panel_y_la_franja_lo_tapa()
    {
        var js = new CajaJsFalso();
        var modulo = new ModuloCaja(js);

        await modulo.VentanaAbiertaAsync(true, enPanel: true, varios: true);
        await modulo.VentanaDeLaFranjaAsync(true);
        await modulo.VentanaDeLaFranjaAsync(false);
        await modulo.VentanaAbiertaAsync(false);

        Assert.Equal([(true, true), (true, false), (true, true), (false, false)], js.Ventana);
        Assert.Equal([true, false, true, false], js.Varios);
    }
}
