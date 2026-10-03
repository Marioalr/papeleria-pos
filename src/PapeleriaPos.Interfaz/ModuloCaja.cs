using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;

namespace PapeleriaPos.Interfaz;

/// <summary>El JavaScript de la caja (wwwroot/caja.js): teclas de función y foco.</summary>
public sealed class ModuloCaja(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _modulo;

    private Task<IJSObjectReference> Modulo => _modulo ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/caja.js").AsTask();

    public async Task RegistrarTeclasAsync<T>(DotNetObjectReference<T> pantalla) where T : class =>
        await (await Modulo).InvokeVoidAsync("registrar", pantalla);

    public async Task QuitarTeclasAsync() => await (await Modulo).InvokeVoidAsync("quitar");

    public async Task VentanaAbiertaAsync(bool abierta) => await (await Modulo).InvokeVoidAsync("ventanaAbierta", abierta);

    public async Task EnfocarAsync(string selector) => await (await Modulo).InvokeVoidAsync("enfocar", selector);

    public async Task MostrarAsync(string selector) => await (await Modulo).InvokeVoidAsync("mostrar", selector);

    public async ValueTask DisposeAsync()
    {
        if (_modulo is { IsCompletedSuccessfully: true })
        {
            try
            {
                await (await _modulo).DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El navegador ya se cerró.
            }
        }
    }
}

public static class RegistroInterfaz
{
    /// <summary>Lo que necesita la interfaz de la caja; cada anfitrión (ventana de Windows o navegador) pone su impresora.</summary>
    public static IServiceCollection AgregarInterfazCaja(this IServiceCollection servicios, Uri servicio, Guid? cajaId, IImpresora impresora)
    {
        servicios.AddScoped(_ => new ClienteCaja(new HttpClient { BaseAddress = servicio }));
        servicios.AddSingleton(new OpcionesCaja { CajaId = cajaId });
        servicios.AddSingleton(impresora);
        servicios.AddScoped<ModuloCaja>();
        return servicios;
    }
}
