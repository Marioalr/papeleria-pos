using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Un campo de dinero que se guarda con Enter donde caja.js no está (wwwroot/corte.js): «Efectivo contado» del corte (también en la
/// caja con el turno de otro) y el fondo al abrir el turno (2026-11-09). Si ahí escribe el lector de teclado, su ráfaga no se toma
/// como dinero: el campo vuelve a lo que tenía y su Enter no cierra ni abre el turno. Lo que escribe una persona se guarda con su
/// Enter, como siempre. Donde sí está caja.js, lo mismo lo hace <c>data-numero</c>.
/// </summary>
public sealed class DineroSinLector(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _modulo;

    /// <summary>Cuida <paramref name="campo"/>, sin decir nada de lo que se leyó (el corte).</summary>
    public Task ProtegerAsync(ElementReference campo) => ProtegerAsync<object>(campo, null);

    /// <summary>
    /// Cuida <paramref name="campo"/>. Con <paramref name="pantalla"/>, le dice qué se leyó (su <c>LectorEnElDinero(string codigo)</c>),
    /// para que lo diga ahí mismo. Si la página ya no contesta, el campo sigue sirviendo; solo sin ese cuidado.
    /// </summary>
    public async Task ProtegerAsync<T>(ElementReference campo, DotNetObjectReference<T>? pantalla) where T : class
    {
        try
        {
            _modulo ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/corte.js");
            await _modulo.InvokeVoidAsync("proteger", campo, pantalla);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException or OperationCanceledException)
        {
            // La página se está yendo (o volviendo a cargar): el campo se queda como cualquier otro.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not { } modulo)
            return;
        _modulo = null;
        try
        {
            await modulo.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException or OperationCanceledException)
        {
            // La página ya no está: el módulo se fue con ella.
        }
    }
}
