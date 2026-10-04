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
    /// <summary>Lo que necesita la interfaz de la caja; cada anfitrión (ventana de Windows o navegador) pone su conexión e impresora.</summary>
    public static IServiceCollection AgregarInterfazCaja(this IServiceCollection servicios, ConexionCaja conexion, IImpresora impresora)
    {
        // Sin respuesta en unos segundos, la caja avisa en lugar de quedarse congelada.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        servicios.AddSingleton(conexion);
        servicios.AddScoped(_ => new ClienteCaja(http, conexion));
        servicios.AddSingleton(impresora);
        servicios.AddScoped<ModuloCaja>();
        return servicios;
    }
}
