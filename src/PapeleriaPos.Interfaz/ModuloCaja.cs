using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;

namespace PapeleriaPos.Interfaz;

/// <summary>El JavaScript de la caja (wwwroot/caja.js): teclas de función, foco y lo que recuerda el celular.</summary>
public sealed class ModuloCaja(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _modulo;

    private Task<IJSObjectReference> Modulo => _modulo ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/caja.js").AsTask();

    public async Task RegistrarTeclasAsync<T>(DotNetObjectReference<T> pantalla) where T : class =>
        await (await Modulo).InvokeVoidAsync("registrar", pantalla);

    /// <summary>La pantalla de venta se va, y sus ventanas con ella.</summary>
    public async Task QuitarTeclasAsync()
    {
        _deLaVenta = default;
        await (await Modulo).InvokeVoidAsync("quitar");
    }

    /// <summary>Lo último que dijo la pantalla de venta de sus ventanas.</summary>
    private (bool Abierta, bool EnPanel) _deLaVenta;

    /// <summary>Si la franja de la prueba tiene algo encima de la venta (<see cref="VentanaDeLaFranjaAsync"/>).</summary>
    private bool _deLaFranja;

    /// <summary>
    /// Con una ventana abierta, caja.js solo atrapa las teclas que no escriben en sus campos. <paramref name="enPanel"/>: es
    /// un panel de la pantalla nueva (el ticket y la barra de teclas a la vista), y también Ins y Ctrl+Q son de la venta.
    /// </summary>
    public async Task VentanaAbiertaAsync(bool abierta, bool enPanel = false)
    {
        _deLaVenta = (abierta, enPanel);
        await MandarVentanaAsync();
    }

    /// <summary>
    /// La franja de la prueba con algo encima de la venta (la guía, sus avisos, el cuadro de la nota de la tarea): mientras,
    /// caja.js lo trata como una ventana, y la venta de atrás no se queda con las teclas que se escriben ahí. Al quitarlo
    /// vuelve lo que dijo la pantalla de venta, que puede tener un panel abierto: la franja se puede tocar con un panel de la
    /// pantalla nueva a la vista. Antes la franja mandaba «sin ventana» al cerrar la guía, y con el panel abierto caja.js
    /// mandaba a la venta, como teclas del ticket, Supr en un campo vacío del panel o las flechas fuera de sus campos.
    /// </summary>
    public async Task VentanaDeLaFranjaAsync(bool abierta)
    {
        _deLaFranja = abierta;
        await MandarVentanaAsync();
    }

    private async Task MandarVentanaAsync() =>
        await (await Modulo).InvokeVoidAsync("ventanaAbierta", _deLaFranja || _deLaVenta.Abierta, !_deLaFranja && _deLaVenta.EnPanel);

    /// <summary>Vacía el cuadro de captura de la pantalla nueva (no está enlazado: lo maneja caja.js).</summary>
    public async Task LimpiarCapturaAsync() => await (await Modulo).InvokeVoidAsync("limpiarCaptura");

    /// <summary>Deja un texto en el cuadro de captura de la pantalla nueva, con el cursor al final («/» para las acciones).</summary>
    public async Task EscribirCapturaAsync(string texto) => await (await Modulo).InvokeVoidAsync("escribirCaptura", texto);

    /// <summary>Un clic en un elemento, como el selector de archivos de Ctrl+Q.</summary>
    public async Task ClicAsync(string selector) => await (await Modulo).InvokeVoidAsync("clic", selector);

    public async Task EnfocarAsync(string selector) => await (await Modulo).InvokeVoidAsync("enfocar", selector);

    public async Task MostrarAsync(string selector) => await (await Modulo).InvokeVoidAsync("mostrar", selector);

    public async Task CopiarAsync(string texto) => await (await Modulo).InvokeVoidAsync("copiar", texto);

    /// <summary>Pone una página en un marco (la hoja de etiquetas) y espera a que cargue.</summary>
    public async Task MostrarEnMarcoAsync(string selector, string html) => await (await Modulo).InvokeVoidAsync("mostrarEnMarco", selector, html);

    /// <summary>Abre el diálogo de impresión solo con lo que hay en el marco.</summary>
    public async Task ImprimirMarcoAsync(string selector) => await (await Modulo).InvokeVoidAsync("imprimirMarco", selector);

    /// <summary>Guarda en el navegador (vacío: lo borra).</summary>
    public async Task GuardarAsync(string clave, string? valor) => await (await Modulo).InvokeVoidAsync("guardar", clave, valor);

    public async Task<string?> LeerAsync(string clave) => await (await Modulo).InvokeAsync<string?>("leer", clave);

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
