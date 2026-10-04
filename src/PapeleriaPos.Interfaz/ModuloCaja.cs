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
    /// <summary>
    /// Lo que necesita la interfaz de la caja; cada anfitrión (ventana de Windows o navegador) pone su conexión e impresora.
    /// Con <paramref name="baseLocal"/> (la ventana de Windows), la caja sigue vendiendo si la caja principal no contesta.
    /// </summary>
    public static IServiceCollection AgregarInterfazCaja(this IServiceCollection servicios, ConexionCaja conexion, IImpresora impresora, string? baseLocal = null)
    {
        // Si la caja principal está apagada, la conexión falla en 3 s; si contesta pero tarda, se espera hasta 15 s. Así la
        // caja avisa (o pasa a contingencia) en lugar de quedarse congelada.
        var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(15) };
        Contingencia? contingencia = null;
        if (baseLocal is not null)
        {
            contingencia = new Contingencia(new AlmacenLocal(baseLocal), http, conexion);
            servicios.AddSingleton(contingencia);
            contingencia.Iniciar();
        }
        servicios.AddSingleton(conexion);
        servicios.AddScoped(_ => new ClienteCaja(http, conexion, contingencia));
        servicios.AddSingleton(impresora);
        servicios.AddScoped<ModuloCaja>();
        return servicios;
    }
}
