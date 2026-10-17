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

    /// <summary>La pantalla de venta se va, y sus ventanas y su página con ella.</summary>
    public async Task QuitarTeclasAsync()
    {
        _deLaVenta = default;
        await (await Modulo).InvokeVoidAsync("quitar");
    }

    /// <summary>
    /// Se ve una página (Productos, Inventario o Clientes, entrega 8) en lugar de la venta: caja.js deja las flechas, Enter y
    /// lo que se escribe a la página, y lo que se escribe fuera de un campo (también el lector) va a su búsqueda. Esc, las F y
    /// las Ctrl de la caja van a la pantalla, que decide.
    /// </summary>
    public async Task PaginaAbiertaAsync(bool abierta) => await (await Modulo).InvokeVoidAsync("paginaAbierta", abierta);

    /// <summary>
    /// El mostrador (Ctrl+M, entrega 13) está abierto con el cursor en el cuadro de captura: caja.js le pasa a la venta las
    /// flechas, Tab y el Enter del cuadro (<c>EnMostrador</c>), y lo escrito ahí es la cantidad. Con la franja de la prueba
    /// encima, no (caja.js lo revisa con el panel).
    /// </summary>
    public async Task MostradorAbiertoAsync(bool abierto) => await (await Modulo).InvokeVoidAsync("mostradorAbierto", abierto);

    /// <summary>Lo último que dijo la pantalla de venta de sus ventanas.</summary>
    private (bool Abierta, bool EnPanel) _deLaVenta;

    /// <summary>Si la franja de la prueba tiene algo encima de la venta (<see cref="VentanaDeLaFranjaAsync"/>).</summary>
    private bool _deLaFranja;

    /// <summary>
    /// La franja de la prueba tiene algo encima (la guía, sus avisos o el cursor en su nota): mientras, Esc no toca la página
    /// de abajo (entrega 8). Si no, Esc en la guía cerraba sin que se viera lo abierto en la página (la ficha de un producto,
    /// una tarea) o regresaba a la venta.
    /// </summary>
    public bool FranjaEncima => _deLaFranja;

    /// <summary>
    /// Con una ventana abierta, caja.js solo atrapa las teclas que no escriben en sus campos. <paramref name="enPanel"/>: es
    /// un panel a la derecha (el ticket y la barra de teclas a la vista), y también Ins y Ctrl+Q son de la venta.
    /// </summary>
    public async Task VentanaAbiertaAsync(bool abierta, bool enPanel = false)
    {
        _deLaVenta = (abierta, enPanel);
        await MandarVentanaAsync();
    }

    /// <summary>
    /// La franja de la prueba con algo encima de la venta (la guía, sus avisos, el cuadro de la nota de la tarea): mientras,
    /// caja.js lo trata como una ventana, y la venta de atrás no se queda con las teclas que se escriben ahí. Al quitarlo
    /// vuelve lo que dijo la pantalla de venta, que puede tener un panel abierto: la franja se puede tocar con un panel a la
    /// vista. Antes la franja mandaba «sin ventana» al cerrar la guía, y con el panel abierto caja.js
    /// mandaba a la venta, como teclas del ticket, Supr en un campo vacío del panel o las flechas fuera de sus campos.
    /// </summary>
    public async Task VentanaDeLaFranjaAsync(bool abierta)
    {
        _deLaFranja = abierta;
        await MandarVentanaAsync();
    }

    private async Task MandarVentanaAsync() =>
        await (await Modulo).InvokeVoidAsync("ventanaAbierta", _deLaFranja || _deLaVenta.Abierta, !_deLaFranja && _deLaVenta.EnPanel);

    /// <summary>
    /// Ya se dibujó lo de esos escaneos (entrega 5): con la hora de su Enter (el reloj de la página), caja.js mide en el
    /// siguiente cuadro cuánto tardaron en verse y se lo dice a la pantalla (<c>Pintados</c>), de a varios.
    /// </summary>
    public async Task PintadoAsync(IReadOnlyList<double> horas)
    {
        try
        {
            await (await Modulo).InvokeVoidAsync("pintado", horas);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException or InvalidOperationException)
        {
            // Sin la página (se está volviendo a cargar), esos no se miden: no es un error de la venta.
        }
    }

    /// <summary>Vacía el cuadro de captura de la venta (no está enlazado: lo maneja caja.js).</summary>
    public async Task LimpiarCapturaAsync() => await (await Modulo).InvokeVoidAsync("limpiarCaptura");

    /// <summary>Deja un texto en el cuadro de captura de la venta, con el cursor al final («/» para las acciones).</summary>
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
        // La red con la caja principal (entrega 5, parte 2): un cliente para lo que espera el cajero, con un plazo por operación
        // (lo de la venta, 800 ms leer y 2.5 s cobrar; lo de atrás, hasta 15 s), y otro para lo de fondo (el latido, el aviso
        // por SSE, la copia, la bandeja). Con keepalive de TCP: una conexión medio muerta tras reconectarse el Wi-Fi se nota en
        // segundos. Si la caja principal está apagada, conectar falla en 2 s.
        var red = RedCaja.Crear();
        servicios.AddSingleton(red);
        // Lo que mide la caja de su rendimiento (entrega 5): los escaneos y lo que tarda, para el vigía de la pantalla.
        var rendimiento = new RendimientoCaja();
        servicios.AddSingleton(rendimiento);
        Contingencia? contingencia = null;
        if (baseLocal is not null)
        {
            var almacen = new AlmacenLocal(baseLocal);
            almacen.EscrituraLarga += (que, tardo) => rendimiento.Actividad($"la base de la caja ({que})", tardo);
            // Buena, lenta o caída según lo que tarda el latido (con histéresis); en lenta, como sin conexión. La caja con el
            // servicio en la misma PC no tiene red de por medio: solo si contesta o no.
            contingencia = new Contingencia(almacen, red.Fondo, conexion, rendimiento: rendimiento,
                reglas: conexion.Servicio.IsLoopback ? ReglasDeLaRed.SoloSiContesta : ReglasDeLaRed.DeLaTienda);
            servicios.AddSingleton(contingencia);
            contingencia.Iniciar();
        }
        servicios.AddSingleton(conexion);
        servicios.AddScoped(_ => new ClienteCaja(red.Interactivo, conexion, contingencia)
        {
            PlazoLectura = Transporte.PlazoLectura,
            PlazoEscritura = Transporte.PlazoEscritura,
        });
        servicios.AddSingleton(impresora);
        servicios.AddScoped<ModuloCaja>();
        return servicios;
    }
}
