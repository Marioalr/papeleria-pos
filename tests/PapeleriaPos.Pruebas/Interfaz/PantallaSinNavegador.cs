using System.Globalization;
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

    /// <summary>
    /// Dónde está en la pantalla: el componente que lo dibuja y su lugar en el marcado de ese componente (cuál de los que salen
    /// del mismo lugar, como los renglones de una lista, o su <c>@key</c>). Si la pantalla se vuelve a dibujar igual, el mismo
    /// elemento queda en el mismo lugar.
    /// </summary>
    internal string Lugar { get; init; } = "";

    /// <summary>Cómo lo buscó la prueba (<see cref="PantallaSinNavegador.ElementoAsync"/>), para buscarlo igual otra vez.</summary>
    internal Func<ElementoDibujado, bool>? Busqueda { get; init; }
}

/// <summary>
/// Dibuja un componente sin navegador, con los componentes que lleva adentro, para leer su texto y sus campos y disparar sus
/// eventos como lo haría quien la usa. Lo de la página (caja.js, el foco) lo contesta <see cref="PaginaQueContesta"/>.
/// </summary>
#pragma warning disable BL0006 // Los marcos del dibujo: así se encuentra cada elemento, como en la página.
public sealed class PantallaSinNavegador(IServiceProvider servicios) : Renderer(servicios, NullLoggerFactory.Instance)
{
    private int _raiz;

    /// <summary>La página de mentiras (si la hay): contesta también el foco de un elemento (FocusAsync), como en la página.</summary>
    private readonly IJSRuntime? _pagina = (IJSRuntime?)servicios.GetService(typeof(IJSRuntime));

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <summary>Lo que falló al dibujar o en un evento: la prueba revisa que no haya nada.</summary>
    public List<Exception> Errores { get; } = [];

    public Task<T> AbrirAsync<T>(Dictionary<string, object?> parametros) where T : IComponent => Dispatcher.InvokeAsync(async () =>
    {
        // Como en la página: el foco de un elemento (FocusAsync) lo contesta la página de mentiras, sin error.
        if (_pagina is not null)
            ElementReferenceContext ??= new WebElementReferenceContext(_pagina);
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

    protected override Task UpdateDisplayAsync(in RenderBatch lote)
    {
        Contar(lote);
        return AcuseDelDibujo > TimeSpan.Zero ? Task.Delay(AcuseDelDibujo) : Task.CompletedTask;
    }

    // ── Lo que se repinta (entrega 5, parte 3, «Pintar menos») ──

    private readonly Lock _candadoDibujos = new();
    private int _lotes;
    private int _cambios;
    private int _manejadores;
    private readonly Dictionary<string, int> _porComponente = [];

    /// <summary>
    /// Lo que se dibujó desde <see cref="ContarDesdeAhora"/>: cuántas veces se mandó algo a «la página», cuántas veces se
    /// pintó cada componente (por su tipo), cuántos cambios llevaban y cuántos manejadores de eventos se cambiaron por otros.
    /// </summary>
    public sealed record Dibujos(int Lotes, IReadOnlyDictionary<string, int> PorComponente, int Cambios, int ManejadoresCambiados)
    {
        public int Componentes => PorComponente.Values.Sum();

        public int De(string componente) => PorComponente.GetValueOrDefault(componente);

        public override string ToString() =>
            $"{Lotes} lotes, {Componentes} componentes pintados ({string.Join(", ", PorComponente.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))}), "
            + $"{Cambios} cambios, {ManejadoresCambiados} manejadores cambiados";
    }

    /// <summary>El tipo del componente; uno que se pintó y se quitó en el mismo dibujo ya no está.</summary>
    private string Tipo(int componente)
    {
        try
        {
            return GetComponentState(componente).Component.GetType().Name;
        }
        catch (ArgumentException)
        {
            return "(quitado en el mismo dibujo)";
        }
    }

    public void ContarDesdeAhora()
    {
        lock (_candadoDibujos)
        {
            (_lotes, _cambios, _manejadores) = (0, 0, 0);
            _porComponente.Clear();
        }
    }

    public Dibujos Contados()
    {
        lock (_candadoDibujos)
            return new Dibujos(_lotes, new Dictionary<string, int>(_porComponente), _cambios, _manejadores);
    }

    private void Contar(in RenderBatch lote)
    {
        lock (_candadoDibujos)
        {
            _lotes++;
            _manejadores += lote.DisposedEventHandlerIDs.Count;
            for (var i = 0; i < lote.UpdatedComponents.Count; i++)
            {
                var diff = lote.UpdatedComponents.Array[i];
                _porComponente[Tipo(diff.ComponentId)] = _porComponente.GetValueOrDefault(Tipo(diff.ComponentId)) + 1;
                _cambios += diff.Edits.Count;
            }
        }
    }

    /// <summary>Todo el texto de la pantalla (cada elemento empieza renglón) y sus elementos, en orden.</summary>
    public Task<(string Texto, List<ElementoDibujado> Elementos)> LeerAsync() => Dispatcher.InvokeAsync(() =>
    {
        var elementos = new List<ElementoDibujado>();
        var texto = new StringBuilder();
        Recorrer(_raiz, elementos, texto);
        return (texto.ToString(), elementos);
    });

    public async Task<string> TextoAsync() => (await LeerAsync()).Texto;

    /// <summary>El primer elemento que cumple; falla si no hay. Recuerda cómo se buscó, por si hay que buscarlo otra vez (<see cref="DispararAsync"/>).</summary>
    public async Task<ElementoDibujado> ElementoAsync(Func<ElementoDibujado, bool> condicion, string que) =>
        (await LeerAsync()).Elementos.FirstOrDefault(condicion) is { } elemento
            ? elemento with { Busqueda = condicion }
            : throw new Xunit.Sdk.XunitException($"No está {que}.");

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

    /// <summary>
    /// Dispara un evento de un elemento que se leyó antes. Todo lo que oprime, escribe o teclea una prueba pasa por aquí.
    /// </summary>
    /// <remarks>
    /// Entre que se leyó el elemento y ahora, la pantalla pudo volver a dibujarse sola (terminó de cargar algo, llegó un aviso)
    /// y su evento ya tener otro número. Si el número de antes todavía vale (un doble clic antes de que la página tome el
    /// dibujo, <see cref="AcuseDelDibujo"/>), se usa ese. Si ya no existe, quien prueba oprime lo que ve: el mismo elemento ya
    /// dibujado otra vez (<see cref="OtraVez"/>), buscado y disparado dentro del despachador, donde la pantalla no cambia
    /// mientras tanto. Antes solo se encontraba otra vez por su id o por un texto único, y un campo sin id (el motivo de una
    /// devolución) fallaba con «There is no event handler associated with this event».
    /// </remarks>
    public Task DispararAsync(ElementoDibujado elemento, string evento, EventArgs datos) =>
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await DispatchEventAsync(Manejador(elemento, evento), null, datos);
            }
            catch (ArgumentException ex) when (ex.ParamName == "eventHandlerId" && OtraVez(elemento) is { } actual)
            {
                await DispatchEventAsync(Manejador(actual, evento), null, datos);
            }
        });

    private static ulong Manejador(ElementoDibujado elemento, string evento) =>
        elemento.Eventos.TryGetValue(evento, out var manejador)
            ? manejador
            : throw new Xunit.Sdk.XunitException($"«{elemento.Etiqueta}#{elemento.Id}» no tiene {evento}.");

    /// <summary>
    /// El mismo elemento en lo que está dibujado ahora, como lo buscaría otra vez quien prueba: igual que la primera vez (la
    /// condición de <see cref="ElementoAsync"/>, también la de <see cref="PorIdAsync"/> y <see cref="BotonAsync"/>); si no se
    /// buscó así o ya no la cumple, el que ocupa su lugar en la pantalla (<see cref="ElementoDibujado.Lugar"/>) con la misma
    /// etiqueta y el mismo id; y si no, el único con su id, o con su etiqueta y su texto. Se llama dentro del despachador,
    /// donde la pantalla no cambia mientras tanto.
    /// </summary>
    private ElementoDibujado? OtraVez(ElementoDibujado elemento)
    {
        var elementos = new List<ElementoDibujado>();
        Recorrer(_raiz, elementos, new StringBuilder());
        if (elemento.Busqueda is { } busqueda && elementos.FirstOrDefault(busqueda) is { } buscado)
            return buscado;
        if (elementos.FirstOrDefault(e => e.Lugar == elemento.Lugar) is { } enSuLugar && enSuLugar.Etiqueta == elemento.Etiqueta
            && enSuLugar.Id == elemento.Id)
            return enSuLugar;
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
                return elemento with { Busqueda = e => e.Id == id };
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó «#{id}»:\n{await TextoAsync()}");
            await Task.Delay(20);
        }
    }

    public static string Limpio(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    private void Recorrer(int componente, List<ElementoDibujado> elementos, StringBuilder texto)
    {
        var marcos = GetCurrentRenderTreeFrames(componente);
        Recorrer(componente.ToString(CultureInfo.InvariantCulture), marcos.Array, 0, marcos.Count, elementos, texto);
    }

    /// <param name="lugar">
    /// Dónde están estos marcos (<see cref="ElementoDibujado.Lugar"/>): el componente y, dentro de él, el lugar del marcado de
    /// cada elemento de arriba (su número de secuencia) y cuál de los que salen de ahí es (su <c>@key</c>, o el primero, el
    /// segundo…).
    /// </param>
    private void Recorrer(string lugar, RenderTreeFrame[] marcos, int desde, int hasta, List<ElementoDibujado> elementos, StringBuilder texto)
    {
        // Cuántos van de cada lugar del marcado entre estos hermanos: los renglones de una lista salen del mismo.
        Dictionary<int, int>? vistos = null;
        string Suyo(string tipo, int secuencia, object? llave)
        {
            vistos ??= [];
            var vez = vistos[secuencia] = vistos.GetValueOrDefault(secuencia) + 1;
            return llave is null ? $"{lugar}/{tipo}{secuencia}.{vez}" : $"{lugar}/{tipo}{secuencia}={llave}";
        }

        for (var i = desde; i < hasta; i++)
        {
            var m = marcos[i];
            switch (m.FrameType)
            {
                case RenderTreeFrameType.Element:
                    var suyo = Suyo("e", m.Sequence, m.ElementKey);
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
                    var indice = elementos.Count;
                    elementos.Add(null!);
                    var propio = new StringBuilder();
                    Recorrer(suyo, marcos, j, i + m.ElementSubtreeLength, elementos, propio);
                    elementos[indice] = new ElementoDibujado(m.ElementName, atributos, eventos, propio.ToString()) { Lugar = suyo };
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
                    Recorrer(Suyo("r", m.Sequence, null), marcos, i + 1, i + m.RegionSubtreeLength, elementos, texto);
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
    private readonly List<(string Funcion, object?[]? Argumentos)> _llamadas = [];

    /// <summary>Lo que se le pidió a la página, en orden (la vista previa de una hoja, imprimirla…), para revisarlo.</summary>
    public IReadOnlyList<(string Funcion, object?[]? Argumentos)> Llamadas
    {
        get
        {
            lock (_guardado)
                return [.. _llamadas];
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? resultado;
        lock (_guardado)
        {
            _llamadas.Add((identifier, args));
            resultado = identifier switch
            {
                "import" => this,
                "leer" => _guardado.GetValueOrDefault((string)args![0]!),
                "guardar" when args![1] is string valor => _guardado[(string)args[0]!] = valor,
                // Lo que la venta no tomó regresa al cuadro de captura: aquí siempre está vacío (caja.js lo vació con su Enter).
                "regresarACaptura" => true,
                _ => null,
            };
        }
        return ValueTask.FromResult(resultado is TValue valor2 ? valor2 : default!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Hace del «Guardar como» de la caja (<see cref="PapeleriaPos.Interfaz.IGuardarArchivo"/>, entrega 9, parte 2): no pregunta nada,
/// se queda con lo que se guardó para revisarlo y contesta que quedó en unos Documentos de mentiras; o, si se le pide, como si quien
/// guardaba hubiera cancelado.
/// </summary>
public sealed class GuardadoDePrueba : PapeleriaPos.Interfaz.IGuardarArchivo
{
    /// <summary>Donde dice que quedó.</summary>
    public const string Carpeta = @"C:\Users\caja\Documents";

    private readonly List<PapeleriaPos.Interfaz.ArchivoPorGuardar> _guardados = [];

    /// <summary>Lo que se le pidió guardar, en orden (también lo cancelado).</summary>
    public IReadOnlyList<PapeleriaPos.Interfaz.ArchivoPorGuardar> Guardados
    {
        get
        {
            lock (_guardados)
                return [.. _guardados];
        }
    }

    /// <summary>Contesta como si se hubiera cancelado «Guardar como».</summary>
    public bool Cancelar { get; set; }

    public Task<PapeleriaPos.Interfaz.ArchivoGuardado?> GuardarAsync(PapeleriaPos.Interfaz.ArchivoPorGuardar archivo)
    {
        lock (_guardados)
            _guardados.Add(archivo);
        return Task.FromResult(Cancelar ? null : new PapeleriaPos.Interfaz.ArchivoGuardado(archivo.Nombre, $@"{Carpeta}\{archivo.Nombre}"));
    }
}

/// <summary>
/// Hace del «Abrir» de la caja (<see cref="PapeleriaPos.Interfaz.IAbrirArchivo"/>, la ventana de Windows): no pregunta nada y
/// contesta con el archivo que se le dé (como si se hubiera elegido), o como si se hubiera cancelado. Anota lo que se le pidió.
/// </summary>
public sealed class AbiertoDePrueba : PapeleriaPos.Interfaz.IAbrirArchivo
{
    private readonly List<PapeleriaPos.Interfaz.ArchivoPorAbrir> _pedidos = [];

    /// <summary>Lo que contesta la siguiente vez; vacío: como si se cancelara.</summary>
    public PapeleriaPos.Interfaz.ArchivoAbierto? Elegido { get; set; }

    public IReadOnlyList<PapeleriaPos.Interfaz.ArchivoPorAbrir> Pedidos
    {
        get
        {
            lock (_pedidos)
                return [.. _pedidos];
        }
    }

    public Task<PapeleriaPos.Interfaz.ArchivoAbierto?> AbrirAsync(PapeleriaPos.Interfaz.ArchivoPorAbrir archivo)
    {
        lock (_pedidos)
            _pedidos.Add(archivo);
        return Task.FromResult(Elegido);
    }
}

/// <summary>
/// Hace del archivo que da el selector del navegador (<c>&lt;input type=file&gt;</c>, la caja del navegador): con su nombre y lo
/// que lleva; como el de verdad, no se deja leer más allá de lo que se pide.
/// </summary>
public sealed class ArchivoDelNavegadorDePrueba(string nombre, byte[] datos) : Microsoft.AspNetCore.Components.Forms.IBrowserFile
{
    public string Name => nombre;
    public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
    public long Size => datos.Length;
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
        Size > maxAllowedSize
            ? throw new IOException($"Supplied file with size {Size} bytes exceeds the maximum of {maxAllowedSize} bytes.")
            : new MemoryStream(datos, writable: false);
}
