using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Un elemento de la pantalla dibujada: su etiqueta, sus atributos, sus eventos y todo su texto (también el de los componentes de adentro).</summary>
public sealed record ElementoDibujado(string Etiqueta, IReadOnlyDictionary<string, object?> Atributos, IReadOnlyDictionary<string, ulong> Eventos, string Texto)
{
    public string? Id => Atributos.GetValueOrDefault("id")?.ToString();
    public string? Clase => Atributos.GetValueOrDefault("class")?.ToString();
    public string? Valor => Atributos.GetValueOrDefault("value")?.ToString();
    public bool Desactivado => Atributos.GetValueOrDefault("disabled") is true;
}

/// <summary>
/// Dibuja un componente sin navegador, con los componentes que lleva adentro, para leer su texto y sus campos y disparar sus
/// eventos como lo haría quien la usa. Lo de la página (caja.js, el foco) lo contesta <see cref="PaginaQueContesta"/>.
/// </summary>
#pragma warning disable BL0006 // Los marcos del dibujo: así se encuentra cada elemento, como en la página.
public sealed class PantallaSinNavegador(IServiceProvider servicios) : Renderer(servicios, NullLoggerFactory.Instance)
{
    private int _raiz;

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <summary>Lo que falló al dibujar o en un evento: la prueba revisa que no haya nada.</summary>
    public List<Exception> Errores { get; } = [];

    public Task<T> AbrirAsync<T>(Dictionary<string, object?> parametros) where T : IComponent => Dispatcher.InvokeAsync(async () =>
    {
        // Como en la página: el foco de un elemento (FocusAsync) lo contesta la página de mentiras, sin error.
        ElementReferenceContext ??= new WebElementReferenceContext((IJSRuntime)servicios.GetService(typeof(IJSRuntime))!);
        var componente = (T)InstantiateComponent(typeof(T));
        _raiz = AssignRootComponentId(componente);
        await RenderRootComponentAsync(_raiz, ParameterView.FromDictionary(parametros));
        return componente;
    });

    protected override void HandleException(Exception exception)
    {
        lock (Errores)
            Errores.Add(exception);
    }

    /// <summary>
    /// Lo que tarda «la página» en tomar cada dibujo; sin nada, al momento. Mientras tanto, como en el navegador, un clic
    /// todavía llega a lo de antes: un doble clic antes de que el botón se vea desactivado.
    /// </summary>
    public TimeSpan AcuseDelDibujo { get; set; }

    protected override Task UpdateDisplayAsync(in RenderBatch lote) => AcuseDelDibujo > TimeSpan.Zero ? Task.Delay(AcuseDelDibujo) : Task.CompletedTask;

    /// <summary>Todo el texto de la pantalla (cada elemento empieza renglón) y sus elementos, en orden.</summary>
    public Task<(string Texto, List<ElementoDibujado> Elementos)> LeerAsync() => Dispatcher.InvokeAsync(() =>
    {
        var elementos = new List<ElementoDibujado>();
        var texto = new StringBuilder();
        Recorrer(_raiz, elementos, texto);
        return (texto.ToString(), elementos);
    });

    public async Task<string> TextoAsync() => (await LeerAsync()).Texto;

    /// <summary>El primer elemento que cumple; falla si no hay.</summary>
    public async Task<ElementoDibujado> ElementoAsync(Func<ElementoDibujado, bool> condicion, string que) =>
        (await LeerAsync()).Elementos.FirstOrDefault(condicion) ?? throw new Xunit.Sdk.XunitException($"No está {que}.");

    public Task<ElementoDibujado> PorIdAsync(string id) => ElementoAsync(e => e.Id == id, $"«#{id}»");

    /// <summary>El botón cuyo texto (sin espacios de más) empieza así.</summary>
    public Task<ElementoDibujado> BotonAsync(string texto) =>
        ElementoAsync(e => e.Etiqueta == "button" && Limpio(e.Texto).StartsWith(texto, StringComparison.Ordinal), $"el botón «{texto}»");

    public async Task OprimirAsync(ElementoDibujado boton) => await DispararAsync(boton, "onclick", new MouseEventArgs());

    /// <summary>Lo que se teclea en un campo (su «oninput»), sin salir de él.</summary>
    public async Task EscribirAsync(string id, string valor) =>
        await DispararAsync(await PorIdAsync(id), "oninput", new ChangeEventArgs { Value = valor });

    /// <summary>Salir del campo, como en el navegador: primero su «onchange» y luego su «onblur» (los que tenga).</summary>
    public async Task SalirAsync(string id)
    {
        var campo = await PorIdAsync(id);
        if (!campo.Eventos.ContainsKey("onchange") && !campo.Eventos.ContainsKey("onblur"))
            throw new Xunit.Sdk.XunitException($"«{campo.Etiqueta}#{id}» no hace nada al salir de él.");
        if (campo.Eventos.ContainsKey("onchange"))
            await DispararAsync(campo, "onchange", new ChangeEventArgs { Value = campo.Valor });
        campo = await PorIdAsync(id);
        if (campo.Eventos.ContainsKey("onblur"))
            await DispararAsync(campo, "onblur", new FocusEventArgs { Type = "blur" });
    }

    public async Task TeclaAsync(string id, string tecla) =>
        await DispararAsync(await PorIdAsync(id), "onkeydown", new KeyboardEventArgs { Key = tecla });

    public Task DispararAsync(ElementoDibujado elemento, string evento, EventArgs datos) =>
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await DispatchEventAsync(Manejador(elemento, evento), null, datos);
            }
            catch (ArgumentException ex) when (ex.ParamName == "eventHandlerId" && Mismo(elemento) is { } actual)
            {
                // Entre que se leyó el elemento y ahora, la pantalla se volvió a dibujar sola (terminó de cargar algo) y su
                // evento ya tiene otro número: quien prueba oprime lo que ve, el mismo elemento ya dibujado otra vez. Si el
                // número de antes todavía vale (un doble clic antes de que la página tome el dibujo), se usa ese.
                await DispatchEventAsync(Manejador(actual, evento), null, datos);
            }
        });

    private static ulong Manejador(ElementoDibujado elemento, string evento) =>
        elemento.Eventos.TryGetValue(evento, out var manejador)
            ? manejador
            : throw new Xunit.Sdk.XunitException($"«{elemento.Etiqueta}#{elemento.Id}» no tiene {evento}.");

    /// <summary>
    /// El mismo elemento en lo que está dibujado ahora: por su id o, si no tiene, por su etiqueta y su texto, cuando hay uno
    /// solo así. Se llama dentro del despachador, donde la pantalla no cambia mientras tanto.
    /// </summary>
    private ElementoDibujado? Mismo(ElementoDibujado elemento)
    {
        var elementos = new List<ElementoDibujado>();
        Recorrer(_raiz, elementos, new StringBuilder());
        var iguales = elementos.Where(e => e.Etiqueta == elemento.Etiqueta
            && (elemento.Id is { } id ? e.Id == id : e.Texto == elemento.Texto)).ToList();
        return iguales.Count == 1 ? iguales[0] : null;
    }

    /// <summary>Espera a que la pantalla llegue a algo (lo que se carga o se guarda en segundo plano).</summary>
    public async Task EsperarAsync(Func<string, bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion(await TextoAsync()))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"La pantalla no llegó a {que}:\n{await TextoAsync()}");
            await Task.Delay(20);
        }
    }

    /// <summary>Espera a que esté el elemento (lo que se carga en segundo plano) y lo regresa.</summary>
    public async Task<ElementoDibujado> EsperarElementoAsync(string id)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if ((await LeerAsync()).Elementos.FirstOrDefault(e => e.Id == id) is { } elemento)
                return elemento;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó «#{id}»:\n{await TextoAsync()}");
            await Task.Delay(20);
        }
    }

    public static string Limpio(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    private void Recorrer(int componente, List<ElementoDibujado> elementos, StringBuilder texto)
    {
        var marcos = GetCurrentRenderTreeFrames(componente);
        Recorrer(marcos.Array, 0, marcos.Count, elementos, texto);
    }

    private void Recorrer(RenderTreeFrame[] marcos, int desde, int hasta, List<ElementoDibujado> elementos, StringBuilder texto)
    {
        for (var i = desde; i < hasta; i++)
        {
            var m = marcos[i];
            switch (m.FrameType)
            {
                case RenderTreeFrameType.Element:
                    var atributos = new Dictionary<string, object?>();
                    var eventos = new Dictionary<string, ulong>();
                    var j = i + 1;
                    for (; j < i + m.ElementSubtreeLength && marcos[j].FrameType == RenderTreeFrameType.Attribute; j++)
                    {
                        if (marcos[j].AttributeEventHandlerId != 0)
                            eventos[marcos[j].AttributeName] = marcos[j].AttributeEventHandlerId;
                        else
                            atributos[marcos[j].AttributeName] = marcos[j].AttributeValue;
                    }
                    var lugar = elementos.Count;
                    elementos.Add(null!);
                    var propio = new StringBuilder();
                    Recorrer(marcos, j, i + m.ElementSubtreeLength, elementos, propio);
                    elementos[lugar] = new ElementoDibujado(m.ElementName, atributos, eventos, propio.ToString());
                    texto.Append('\n').Append(propio);
                    i += m.ElementSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Text:
                    texto.Append(m.TextContent);
                    break;
                case RenderTreeFrameType.Markup:
                    texto.Append(WebUtility.HtmlDecode(Regex.Replace(m.MarkupContent, "<[^>]*>", "")));
                    break;
                case RenderTreeFrameType.Component:
                    Recorrer(m.ComponentId, elementos, texto);
                    i += m.ComponentSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Region:
                    Recorrer(marcos, i + 1, i + m.RegionSubtreeLength, elementos, texto);
                    i += m.RegionSubtreeLength - 1;
                    break;
            }
        }
    }
}
#pragma warning restore BL0006

/// <summary>
/// Hace de la página y de caja.js: contesta todo (el foco, las teclas, lo que se guarda en el navegador) sin hacer nada,
/// y «import» se contesta a sí misma, como el módulo de caja.js.
/// </summary>
public sealed class PaginaQueContesta : IJSRuntime, IJSObjectReference
{
    private readonly Dictionary<string, string> _guardado = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? resultado;
        lock (_guardado)
        {
            resultado = identifier switch
            {
                "import" => this,
                "leer" => _guardado.GetValueOrDefault((string)args![0]!),
                "guardar" when args![1] is string valor => _guardado[(string)args[0]!] = valor,
                _ => null,
            };
        }
        return ValueTask.FromResult(resultado is TValue valor2 ? valor2 : default!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
