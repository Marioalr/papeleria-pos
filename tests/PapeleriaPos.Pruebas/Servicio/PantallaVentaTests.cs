using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La pantalla de venta dibujada de verdad, sin navegador, contra el servicio con la base de <c>preparar-prueba</c>: la de
/// antes ya no existe (el dueño, 2026-10-14: «no volveremos a esa versión»). Ni su botón, ni su opción en el menú del
/// usuario, ni sus teclas; y una PC que había vuelto a ella entra a la del rediseño y lo olvida. Tampoco su barra de
/// módulos en productos e inventario sin vender: ahí va la barra de la venta. caja.js no corre aquí: <see cref="PaginaFalsa"/>
/// contesta lo que la pantalla le pide y guarda lo que el navegador recordaría.
/// </summary>
public class PantallaVentaTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    [Fact]
    public async Task Se_vende_en_la_del_rediseno_sin_volver_a_la_de_antes_y_la_pc_que_habia_vuelto_lo_olvida()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        Assert.Equal(cajero.UsuarioId, turno.CajeroId);
        // Esta PC había vuelto a la pantalla de antes con «Volver a la pantalla de hoy».
        var pagina = new PaginaFalsa();
        pagina.Guardado["pantalla-venta"] = "hoy";
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var _ = servicios;
        var salidas = 0;
        await using var dibujo = new Dibujo(servicios);

        var pantalla = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<PantallaVenta>(new Dictionary<string, object?>
        {
            [nameof(PantallaVenta.Caja)] = caja,
            [nameof(PantallaVenta.Sesion)] = cajero,
            [nameof(PantallaVenta.Turno)] = turno,
            [nameof(PantallaVenta.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
        }));
        // Al entrar, lo que recordaba se borra; los rápidos ya llegaron.
        await Esperar(() => !pagina.Recuerda("pantalla-venta") && dibujo.Textos.Contains("Copia B/N"));

        Assert.Contains("Escanea el primer producto", dibujo.Textos);
        Assert.DoesNotContain("Probar la pantalla nueva", dibujo.Textos);
        Assert.DoesNotContain("Le atiende", dibujo.Textos);

        // El menú del usuario: cambiar de usuario o salir, como en el prototipo; ya no hay a dónde volver. «Cambiar de
        // usuario» y Ctrl+U van a la raíz de la caja (AlSalir), que lleva a la entrada.
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.PorTitulo["Cambiar de usuario o salir"]));
        Assert.Contains("Cambiar de usuario", dibujo.Textos);
        Assert.DoesNotContain("Volver a la pantalla de hoy", dibujo.Textos);
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Cambiar de usuario")!.Value));
        Assert.Equal(1, salidas);
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Ctrl+U", ""));
        Assert.Equal(2, salidas);

        // Las teclas son las de la venta del rediseño: F10 no abre la ventana de buscar, F5 retoma de la espera (no cambia
        // de pestaña) y F12 con el ticket vacío no abre la ventana de cobro.
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F10", ""));
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F5", ""));
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F12", ""));
        Assert.Contains("Escribe el nombre aquí", dibujo.Textos);
        Assert.Contains("No hay tickets en espera.", dibujo.Textos);
        // Los títulos de sus ventanas («/» sí tiene «Buscar producto por nombre»).
        Assert.False(dibujo.TieneRenglon("Buscar producto"));
        Assert.DoesNotContain("Pagó con (efectivo)", dibujo.Textos);
        Assert.Null(pagina.VentanaAbierta);

        // Un código que no existe: lo dice y ofrece lo que sus permisos dejan, como el prototipo (el cajero puede las dos).
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Escaneado("7500000000017"));
        await Esperar(() => dibujo.TieneRenglon("No existe «7500000000017». Revisa el código o elige:"));
        Assert.NotNull(dibujo.Boton("Darlo de alta aquí"));
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Venderlo como artículo común")!.Value));
        Assert.True(pagina.VentanaAbierta);
        Assert.True(dibujo.TieneRenglon("Artículo varios / común"));
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Escape", ""));
        Assert.False(pagina.VentanaAbierta);

        // Un rápido y «Cobrar exacto» en el panel del total: queda cobrado, con el ticket listo para verlo en pantalla.
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Alt+1", ""));
        await Esperar(() => dibujo.Boton("Cobrar exacto") is not null);
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Cobrar exacto $2.00")!.Value));
        await Esperar(() => dibujo.Textos.Contains("Cobrado"));
        Assert.Contains("verlo en pantalla", dibujo.Textos);

        // Con algo encima del ticket (aquí Clientes), F1 regresa a vender y Ctrl+U cambia de usuario, como en el prototipo:
        // el fondo de la ventana tapa el menú del usuario. Antes no hacían nada y solo se salía con Esc. (La pestaña, antes de
        // abrir la ventana: adentro hay otro botón «Clientes».)
        var pestanaClientes = dibujo.Boton("Clientes")!.Value;
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(pestanaClientes));
        Assert.True(pagina.VentanaAbierta);
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F1", null));
        Assert.False(pagina.VentanaAbierta);
        Assert.Equal(2, salidas);
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(pestanaClientes));
        Assert.True(pagina.VentanaAbierta);
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Ctrl+U", null));
        Assert.Equal(3, salidas);
        Assert.False(pagina.VentanaAbierta);

        // «Salir» del menú hace lo mismo que «Cambiar de usuario»: la raíz de la caja decide (AlSalir).
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.PorTitulo["Cambiar de usuario o salir"]));
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Salir")!.Value));
        Assert.Equal(4, salidas);
        Assert.Empty(dibujo.Errores);
        Assert.DoesNotContain("Volver a la pantalla de hoy", dibujo.Textos);
    }

    [Fact]
    public async Task Sin_vender_va_la_barra_de_la_venta_con_sus_teclas_y_no_la_de_antes()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var pagina = new PaginaFalsa();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var _ = servicios;
        var salidas = 0;
        var dibujo = new Dibujo(servicios);

        var administracion = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<Administracion>(new Dictionary<string, object?>
        {
            [nameof(Administracion.Caja)] = caja,
            [nameof(Administracion.Sesion)] = encargado,
            [nameof(Administracion.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
        }));
        // Para caja.js es una ventana encima de la venta: solo atrapa Esc, las F y las Ctrl de la caja.
        await Esperar(() => pagina.Registrada && pagina.VentanaAbierta == true);

        // La barra de la venta: la caja, las pestañas y el usuario; ni «F3 Productos» ni «Administra:» de la barra de antes.
        Assert.True(dibujo.TieneRenglon(caja.Nombre));
        Assert.All(new[] { "Productos", "Inventario", "Clientes" }, pestana => Assert.NotNull(dibujo.Boton(pestana)));
        Assert.True(dibujo.TieneRenglon("sin vender"));
        Assert.False(dibujo.TieneRenglon("F3 Productos"));
        Assert.DoesNotContain("Administra:", dibujo.Textos);

        // F3 cambia a Productos; Ctrl+U cambia de usuario, igual que en la venta.
        await dibujo.Dispatcher.InvokeAsync(() => administracion.Tecla("F3", null));
        await Esperar(() => dibujo.Textos.Contains("Decisiones de depuración"));
        await dibujo.Dispatcher.InvokeAsync(() => administracion.Tecla("Ctrl+U", null));
        Assert.Equal(1, salidas);

        // El menú del usuario, como en la venta: «Cambiar de usuario» y «Salir».
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.PorTitulo["Cambiar de usuario o salir"]));
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Cambiar de usuario")!.Value));
        Assert.Equal(2, salidas);
        // Aquí nadie quita la pantalla al salir: el menú sigue abierto.
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Salir")!.Value));
        Assert.Equal(3, salidas);
        Assert.Empty(dibujo.Errores);

        // Al irse, caja.js olvida la «ventana»: si no, en la venta del que entra después el cuadro no tomaría los escaneos.
        await ((IAsyncDisposable)dibujo).DisposeAsync();
        Assert.False(pagina.VentanaAbierta);
        Assert.False(pagina.Registrada);
    }

    private static async Task Esperar(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion())
        {
            Assert.True(DateTime.UtcNow < limite, "La pantalla no llegó a donde debía.");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Hace de la página y de caja.js: contesta todo, y «guardar» y «leer» usan lo que recordaría el navegador
    /// (localStorage). Solo la toca el despachador del dibujo; la prueba la lee mientras.
    /// </summary>
    private sealed class PaginaFalsa : IJSRuntime, IJSObjectReference
    {
        private readonly Lock _candado = new();

        public Dictionary<string, string> Guardado { get; } = [];

        /// <summary>Lo último que se le dijo a caja.js de las ventanas; null si nunca se le dijo.</summary>
        public bool? VentanaAbierta
        {
            get
            {
                lock (_candado)
                    return _ventanaAbierta;
            }
        }

        /// <summary>Hay una pantalla registrada para las teclas.</summary>
        public bool Registrada
        {
            get
            {
                lock (_candado)
                    return _registrada;
            }
        }

        private bool? _ventanaAbierta;
        private bool _registrada;

        public bool Recuerda(string clave)
        {
            lock (_candado)
                return Guardado.ContainsKey(clave);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? resultado;
            lock (_candado)
            {
                resultado = identifier switch
                {
                    "import" => this,
                    "leer" => Guardado.GetValueOrDefault((string)args![0]!),
                    "guardar" when args![1] is string valor => Guardado[(string)args[0]!] = valor,
                    "guardar" => Guardado.Remove((string)args[0]!),
                    "ventanaAbierta" => _ventanaAbierta = (bool)args![0]!,
                    "registrar" => _registrada = true,
                    "quitar" => _registrada = false,
                    _ => null,
                };
            }
            return ValueTask.FromResult(resultado is TValue valor2 ? valor2 : default!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Dibuja sin navegador: guarda los textos que salen (el nombre de un botón va en su primer texto) y con qué se aprieta
    /// cada botón, por su título o por su nombre.
    /// </summary>
#pragma warning disable BL0006 // Los marcos del dibujo: así se encuentra el botón, como lo haría la página.
    private sealed class Dibujo(IServiceProvider servicios) : Renderer(servicios, NullLoggerFactory.Instance)
    {
        private readonly List<(string Texto, ulong Clic)> _botones = [];
        private string _textos = "";

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Dictionary<string, ulong> PorTitulo { get; } = [];

        public List<Exception> Errores { get; } = [];

        public string Textos
        {
            get
            {
                lock (_botones)
                    return _textos;
            }
        }

        /// <summary>Si alguna vez salió este texto solo, como un renglón entero (no dentro de otro más largo).</summary>
        public bool TieneRenglon(string texto) => Textos.Split('\n').Contains(texto);

        /// <summary>El último botón cuyo nombre empieza así (sin las teclas que lleva), o null.</summary>
        public ulong? Boton(string nombre)
        {
            lock (_botones)
                return _botones.LastOrDefault(b => b.Texto.StartsWith(nombre, StringComparison.Ordinal)) is { Clic: > 0 } b ? b.Clic : null;
        }

        public async Task<T> DibujarAsync<T>(Dictionary<string, object?> parametros) where T : IComponent
        {
            var pantalla = (T)InstantiateComponent(typeof(T));
            await RenderRootComponentAsync(AssignRootComponentId(pantalla), ParameterView.FromDictionary(parametros));
            return pantalla;
        }

        public Task ApretarAsync(ulong boton) => DispatchEventAsync(boton, null, new MouseEventArgs());

        protected override void HandleException(Exception exception)
        {
            lock (_botones)
                Errores.Add(exception);
        }

        protected override Task UpdateDisplayAsync(in RenderBatch lote)
        {
            lock (_botones)
            {
                string? titulo = null;
                ulong? clicSinNombre = null;
                for (var i = 0; i < lote.ReferenceFrames.Count; i++)
                {
                    var marco = lote.ReferenceFrames.Array[i];
                    switch (marco.FrameType)
                    {
                        case RenderTreeFrameType.Element:
                            titulo = null;
                            break;
                        case RenderTreeFrameType.Attribute when marco.AttributeName == "title":
                            titulo = marco.AttributeValue as string;
                            break;
                        case RenderTreeFrameType.Attribute when marco.AttributeName == "onclick" && marco.AttributeEventHandlerId != 0:
                            if (titulo is not null)
                                PorTitulo[titulo] = marco.AttributeEventHandlerId;
                            clicSinNombre = marco.AttributeEventHandlerId;
                            break;
                        case RenderTreeFrameType.Text or RenderTreeFrameType.Markup:
                            var texto = Etiquetas.Replace(WebUtility.HtmlDecode(marco.FrameType == RenderTreeFrameType.Text ? marco.TextContent : marco.MarkupContent), "").Trim();
                            if (texto.Length == 0)
                                break;
                            _textos += texto + "\n";
                            if (clicSinNombre is { } clic)
                                _botones.Add((texto, clic));
                            clicSinNombre = null;
                            break;
                    }
                }
            }
            return Task.CompletedTask;
        }

        private static readonly Regex Etiquetas = new("<[^>]*>");
    }
#pragma warning restore BL0006
}
