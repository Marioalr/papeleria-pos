using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// AlgoFallo en la ventana de Windows: «Volver a cargar» con la pantalla de venta abierta arma la caja a la primera, un
/// error en un botón dice «Algo falló», y uno de la franja de la prueba solo quita la franja. WebView2 no corre aquí: la
/// página es <see cref="PaginaDePrueba"/>, que contesta como blazor.webview.js.
/// </summary>
public sealed class AlgoFalloTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"algo-fallo-{Guid.NewGuid():N}");
    private readonly List<PaginaDePrueba> _paginas = [];

    public void Dispose()
    {
        // Sin desecharlas: lo que la de antes dejó sin contestar se quedaría esperando, como en la ventana al cerrarse.
        foreach (var pagina in _paginas)
            pagina.Detener();
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>
    /// La revisión del 2026-10-14: con la pantalla de venta abierta, la página nueva se quedaba en «Cargando…» porque
    /// Blazor desechaba la pantalla hasta conectarla y lo que la pantalla le pide a la página al irse le llegaba a la nueva.
    /// Con RecargaDeLaCaja se quita antes, en la de ahora (que contesta, o ya no), y la nueva arma la caja a la primera.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Volver_a_cargar_con_la_pantalla_de_venta_abierta_arma_la_caja_a_la_primera(bool laDeAhoraContesta)
    {
        var teclas = new Teclas();
        var recarga = new RecargaDeLaCaja();
        await using var proveedor = Servicios(teclas, recarga);
        var pagina = new PaginaDePrueba(proveedor, Microsoft.AspNetCore.Components.Dispatcher.CreateDefault());
        _paginas.Add(pagina);
        pagina.Abrir();
        await pagina.Despachador.InvokeAsync(pagina.AgregarRaiz<RaizDePrueba>);
        await Esperar(() => teclas.Registradas == 1);
        // La ventana detiene la recarga para quitar antes la pantalla; la suya ya no la detiene.
        Assert.True(recarga.HayPantalla);

        // La de ahora se cayó, o Blazor la dio por perdida tras un error: ya no contesta nada.
        pagina.Contesta = laDeAhoraContesta;
        await recarga.SoltarAsync(TimeSpan.FromMilliseconds(200));
        // Con la máquina cargada (el CI), quitar la pantalla puede tardar más que la espera de la ventana: se espera a que
        // termine, con su límite, en lugar de suponer que ya pasó.
        await Esperar(() => !recarga.HayPantalla);
        if (laDeAhoraContesta)
            await Esperar(() => teclas.Quitadas == 1);
        pagina.Abrir();
        await Esperar(() => teclas.Registradas == 2);
        Assert.True(recarga.HayPantalla);

        // Las teclas se quitaron en la página de antes (si contestaba), y la nueva no recibió ningún error.
        Assert.Equal(laDeAhoraContesta ? 1 : 0, teclas.Quitadas);
        Assert.DoesNotContain(pagina.Enviados, m => m.Contains("NotifyUnhandledException"));
        Assert.Contains(pagina.Enviados, m => m.StartsWith("2 ") && m.Contains("\"AttachToDocument\""));
        Assert.DoesNotContain("Algo falló", File.Exists(Archivo) ? File.ReadAllText(Archivo) : "");
    }

    [Fact]
    public async Task Un_error_en_un_boton_dice_algo_fallo_y_queda_anotado()
    {
        await using var proveedor = Servicios(new Teclas(), new RecargaDeLaCaja());
        await using var pagina = new PaginaSinNavegador(proveedor);

        await pagina.Dispatcher.InvokeAsync(pagina.Dibujar<RaizConBoton>);
        Assert.Contains("Cobrar", pagina.Textos);
        await pagina.Dispatcher.InvokeAsync(() => pagina.Apretar(pagina.Clics.Single()));

        Assert.Contains("Algo falló", pagina.Textos);
        Assert.Contains("Volver a cargar", pagina.Textos);
        var texto = File.ReadAllText(Archivo);
        Assert.Contains("Algo falló en la pantalla:", texto);
        Assert.Contains("System.InvalidOperationException: Se rompió al apretar", texto);
    }

    [Fact]
    public async Task Si_falla_la_franja_de_la_prueba_solo_se_quita_ella_y_la_venta_sigue()
    {
        await using var proveedor = Servicios(new Teclas(), new RecargaDeLaCaja());
        await using var dibujo = new HtmlRenderer(proveedor, NullLoggerFactory.Instance);

        var html = await dibujo.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await dibujo.RenderComponentAsync<RaizConFranja>()).ToHtmlString()));

        Assert.Contains("Vendiendo", html);
        Assert.Contains("Algo falló en la franja de la prueba y quedó anotado; la venta sigue igual.", html);
        Assert.DoesNotContain("La caja no se cerró", html);
        // Sin pasar por .NET; en la ventana de Windows, MainWindow quita antes la pantalla (RecargaDeLaCaja).
        Assert.Contains("onclick=\"location.reload()\"", html);
        var texto = File.ReadAllText(Archivo);
        Assert.Contains("Algo falló en la franja de la prueba (la venta siguió):", texto);
        Assert.DoesNotContain("Algo falló en la pantalla", texto);
    }

    private string Archivo => Path.Combine(_carpeta, $"caja-{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}.log");

    private ServiceProvider Servicios(Teclas teclas, RecargaDeLaCaja recarga)
    {
        var servicios = new ServiceCollection();
        servicios.AddBlazorWebView();
        servicios.AddLogging();
        servicios.AddSingleton(new RegistroDiario(_carpeta, "caja"));
        servicios.AddSingleton(recarga);
        servicios.AddSingleton(teclas);
        return servicios.BuildServiceProvider();
    }

    private static async Task Esperar(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicion())
        {
            Assert.True(DateTime.UtcNow < limite, "La página no llegó a donde debía.");
            await Task.Delay(20);
        }
    }

    public sealed class Teclas
    {
        public int Registradas;
        public int Quitadas;
    }

    /// <summary>Como RaizCaja: la pantalla dentro de AlgoFallo.</summary>
    private sealed class RaizDePrueba : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<AlgoFallo>(0);
            b.AddAttribute(1, nameof(AlgoFallo.ChildContent), (RenderFragment)(c =>
            {
                c.OpenComponent<ConTeclas>(0);
                c.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    /// <summary>
    /// Como PantallaVenta: registra sus teclas en caja.js y al irse las quita (con una ventana abierta, también avisa que se
    /// cerró), y solo atrapa que el navegador ya se haya cerrado.
    /// </summary>
    private sealed class ConTeclas : ComponentBase, IAsyncDisposable
    {
        private IJSObjectReference? _modulo;

        [Inject] public IJSRuntime Js { get; set; } = default!;

        [Inject] public Teclas Teclas { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, "Vendiendo");

        protected override async Task OnAfterRenderAsync(bool primera)
        {
            if (!primera)
                return;
            _modulo = await Js.InvokeAsync<IJSObjectReference>("import", "./_content/PapeleriaPos.Interfaz/caja.js");
            await _modulo.InvokeVoidAsync("registrar");
            Interlocked.Increment(ref Teclas.Registradas);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_modulo is null)
                    return;
                await _modulo.InvokeVoidAsync("ventanaAbierta", false);
                await _modulo.InvokeVoidAsync("quitar");
                Interlocked.Increment(ref Teclas.Quitadas);
            }
            catch (JSDisconnectedException)
            {
                // El navegador ya se cerró.
            }
        }
    }

    private sealed class RaizConBoton : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<AlgoFallo>(0);
            b.AddAttribute(1, nameof(AlgoFallo.ChildContent), (RenderFragment)(c =>
            {
                c.OpenComponent<BotonQueTruena>(0);
                c.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private sealed class BotonQueTruena : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenElement(0, "button");
            b.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => throw new InvalidOperationException("Se rompió al apretar")));
            b.AddContent(2, "Cobrar");
            b.CloseElement();
        }
    }

    /// <summary>Como PantallaCaja: la franja de la prueba en su propio AlgoFallo, y abajo la venta.</summary>
    private sealed class RaizConFranja : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<AlgoFallo>(0);
            b.AddAttribute(1, nameof(AlgoFallo.ChildContent), (RenderFragment)(c =>
            {
                c.OpenComponent<AlgoFallo>(0);
                c.AddAttribute(1, nameof(AlgoFallo.Parte), "la franja de la prueba");
                c.AddAttribute(2, nameof(AlgoFallo.ChildContent), (RenderFragment)(f =>
                {
                    f.OpenComponent<FranjaQueTruena>(0);
                    f.CloseComponent();
                }));
                c.CloseComponent();
                c.AddContent(3, "Vendiendo");
            }));
            b.CloseComponent();
        }
    }

    private sealed class FranjaQueTruena : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("La guía se rompió");
    }

    /// <summary>
    /// La página de la ventana de Windows sin WebView2: contesta lo que la caja le pide como blazor.webview.js, y lo que se
    /// le pide a un objeto de JavaScript de otra página falla como allá («JS object instance with ID … does not exist»).
    /// Habla el protocolo de Microsoft.AspNetCore.Components.WebView 10.0.0, la versión que usa la caja. Cada página nueva
    /// (<see cref="Abrir"/>) es como volver a cargar: Blazor la conecta y desecha lo de la anterior.
    /// </summary>
    private sealed class PaginaDePrueba : WebViewManager
    {
        private const string Prefijo = "__bwv:";
        private static readonly Uri Base = new("https://0.0.0.1/");

        private readonly ConcurrentQueue<(int Pagina, string Mensaje)> _porContestar = new();
        private readonly HashSet<long> _objetos = [];
        private readonly CancellationTokenSource _fin = new();
        private int _numero;
        private long _siguienteObjeto;

        public PaginaDePrueba(IServiceProvider servicios, Microsoft.AspNetCore.Components.Dispatcher despachador)
            : base(servicios, despachador, Base, new NullFileProvider(), new JSComponentConfigurationStore(), "index.html")
        {
            Despachador = despachador;
            _ = Task.Run(ContestarAsync);
        }

        public Microsoft.AspNetCore.Components.Dispatcher Despachador { get; }

        /// <summary>Lo que la caja le mandó a cada página, con su número: «2 __bwv:[…]».</summary>
        public ConcurrentQueue<string> Enviados { get; } = new();

        /// <summary>Si la página de ahora contesta; la que se abre después siempre contesta.</summary>
        public bool Contesta { get; set; } = true;

        public void Abrir()
        {
            lock (_objetos)
            {
                _numero++;
                _objetos.Clear();
                _siguienteObjeto = 0;
            }
            Contesta = true;
            Recibir("AttachPage", Base.ToString(), Base.ToString());
        }

        public Task AgregarRaiz<T>() where T : IComponent => AddRootComponentAsync(typeof(T), "#app", ParameterView.Empty);

        public void Detener() => _fin.Cancel();

        protected override void NavigateCore(Uri absoluteUri)
        {
        }

        protected override void SendMessage(string message)
        {
            Enviados.Enqueue($"{_numero} {message}");
            if (Contesta)
                _porContestar.Enqueue((_numero, message));
        }

        private void Recibir(string tipo, params object?[] argumentos) =>
            MessageReceived(Base, Prefijo + JsonSerializer.Serialize(new object?[] { tipo }.Concat(argumentos).ToArray()));

        private async Task ContestarAsync()
        {
            while (!_fin.IsCancellationRequested)
            {
                if (!_porContestar.TryDequeue(out var pendiente))
                {
                    await Task.Delay(5);
                    continue;
                }
                // Lo que se le mandó a una página que ya no está, no lo contesta nadie.
                if (pendiente.Pagina != _numero || !pendiente.Mensaje.StartsWith(Prefijo, StringComparison.Ordinal))
                    continue;
                var mensaje = JsonDocument.Parse(pendiente.Mensaje[Prefijo.Length..]).RootElement;
                switch (mensaje[0].GetString())
                {
                    case "RenderBatch":
                        Recibir("OnRenderCompleted", mensaje[1].GetInt64(), null);
                        break;
                    case "BeginInvokeJS":
                        ContestarJs(mensaje[1].GetInt64(), mensaje[2].GetString()!, mensaje.GetArrayLength() > 5 ? mensaje[5].GetInt64() : 0);
                        break;
                }
            }
        }

        private void ContestarJs(long llamada, string funcion, long objeto)
        {
            if (llamada == 0)
                return;
            bool existe;
            long nuevo = 0;
            lock (_objetos)
            {
                existe = objeto == 0 || _objetos.Contains(objeto);
                if (existe && funcion == "import")
                    _objetos.Add(nuevo = ++_siguienteObjeto);
            }
            object? resultado = funcion == "import" ? new Dictionary<string, long> { ["__jsObjectId"] = nuevo } : null;
            if (existe)
                Recibir("EndInvokeJS", llamada, true, JsonSerializer.Serialize(new object?[] { llamada, true, resultado }));
            else
                Recibir("EndInvokeJS", llamada, false, JsonSerializer.Serialize(new object?[]
                {
                    llamada, false, $"Error: JS object instance with ID {objeto} does not exist (has it been disposed?).",
                }));
        }
    }

    /// <summary>
    /// Para apretar un botón sin navegador: guarda los textos que se dibujan y los botones que se pueden apretar.
    /// </summary>
#pragma warning disable BL0006 // Los marcos del dibujo: así se encuentra el botón, como lo haría la página.
    private sealed class PaginaSinNavegador(IServiceProvider servicios) : Renderer(servicios, NullLoggerFactory.Instance)
    {
        public override Microsoft.AspNetCore.Components.Dispatcher Dispatcher { get; } = Microsoft.AspNetCore.Components.Dispatcher.CreateDefault();

        public List<ulong> Clics { get; } = [];

        public string Textos { get; private set; } = "";

        public async Task Dibujar<T>() where T : IComponent, new() =>
            await RenderRootComponentAsync(AssignRootComponentId(new T()));

        public Task Apretar(ulong boton) => DispatchEventAsync(boton, null, new MouseEventArgs());

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch lote)
        {
            for (var i = 0; i < lote.ReferenceFrames.Count; i++)
            {
                var marco = lote.ReferenceFrames.Array[i];
                if (marco.FrameType == RenderTreeFrameType.Attribute && marco.AttributeName == "onclick" && marco.AttributeEventHandlerId != 0)
                    Clics.Add(marco.AttributeEventHandlerId);
                else if (marco.FrameType == RenderTreeFrameType.Text)
                    Textos += marco.TextContent + "\n";
                else if (marco.FrameType == RenderTreeFrameType.Markup)
                    Textos += WebUtility.HtmlDecode(marco.MarkupContent) + "\n";
            }
            return Task.CompletedTask;
        }
    }
#pragma warning restore BL0006
}
