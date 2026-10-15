using Microsoft.JSInterop;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Las teclas de la entrada (wwwroot/entrada.js): en «¿Quién atiende?», la letra de cada ficha, las cifras del PIN, Enter,
/// Borrar y Esc; en abrir el turno y en la caja con el turno de otro, solo Ctrl+U. Cada pantalla de la entrada las pone al
/// aparecer y las quita al irse. Si la página ya no contesta (se está volviendo a cargar, el navegador se cerró), no pasa
/// nada: la pantalla nueva pone las suyas.
/// </summary>
public sealed class TeclasEntrada(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>¿Quién atiende? y el PIN: todas las teclas de la entrada.</summary>
    public const string Fichas = "fichas";

    /// <summary>Abrir el turno y el turno de otro: solo Ctrl+U, lo demás es de sus campos y botones.</summary>
    public const string SoloCambiar = "cambiar";

    /// <summary>
    /// Con qué se reconocen las teclas de esta pantalla en entrada.js. La da .NET, no entrada.js: así se pueden quitar aunque
    /// la pantalla se vaya antes de que entrada.js conteste que ya las puso.
    /// </summary>
    private readonly string _ficha = Guid.NewGuid().ToString("N");

    private IJSObjectReference? _modulo;
    private bool _quitadas;

    public async Task PonerAsync<T>(DotNetObjectReference<T> pantalla, string modo) where T : class
    {
        try
        {
            var modulo = await js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/entrada.js");
            if (_quitadas)
            {
                // La pantalla se fue mientras llegaba el módulo (se cambió de pantalla muy rápido): no se ponen.
                await modulo.DisposeAsync();
                return;
            }
            _modulo = modulo;
            // Si la pantalla se va mientras tanto, «quitar» llega a entrada.js después de «registrar»: van en orden.
            await modulo.InvokeVoidAsync("registrar", pantalla, modo, _ficha);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException or OperationCanceledException)
        {
            // Sin las teclas todavía se puede tocar: las fichas, el teclado de la pantalla y los botones.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _quitadas = true;
        if (_modulo is not { } modulo)
            return;
        _modulo = null;
        try
        {
            await modulo.InvokeVoidAsync("quitar", _ficha);
            await modulo.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException or OperationCanceledException)
        {
            // La página ya no está (se cerró o se está volviendo a cargar): sus teclas se fueron con ella.
        }
    }
}
