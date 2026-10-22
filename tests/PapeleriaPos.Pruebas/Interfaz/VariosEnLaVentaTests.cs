using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Ins en la venta de verdad, como en eleventa (docs/paridad-eleventa.md, 2026-10-22): «Varios» pide la cantidad y luego el
/// código, escaneado o escrito, y lo agrega; el artículo común sigue en Ctrl+P. El cursor se queda en el cuadro de captura,
/// como con el mostrador, y lo que escanea el lector siempre es el código: también el escaneo que llega mientras se abre el
/// panel, antes de que caja.js lo sepa. Lo que se revisa no depende de lo que tarda la PC.
/// </summary>
public sealed class VariosEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-varios-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private Task<VentaConCopia> AbrirAsync(PaginaQueContesta? pagina = null) =>
        VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: pagina);

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    /// <summary>Lo que manda caja.js con «Varios» abierto: el Enter del cuadro de captura, y si llegó en ráfaga (el lector).</summary>
    private static Task EnterAsync(VentaConCopia v, string texto, bool rafaga = false) =>
        v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.EnVarios(texto, 1234.5, rafaga));

    private static async Task<bool> PanelAbiertoAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Clase == "vn-varios");

    private static async Task<string> PanelAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase == "vn-varios", "el panel de «Varios»")).Texto);

    /// <summary>Lo último que se le dijo a caja.js de las ventanas: abierta, en panel y si es «Varios».</summary>
    private static (bool Abierta, bool EnPanel, bool Varios) Ventana(PaginaQueContesta pagina) =>
        pagina.Llamadas.Where(l => l.Funcion == "ventanaAbierta").Select(l => ((bool)l.Argumentos![0]!, (bool)l.Argumentos[1]!, (bool)l.Argumentos[2]!)).Last();

    [Fact]
    public async Task Ins_pide_la_cantidad_y_luego_el_codigo_y_lo_agrega_de_la_copia()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await AbrirAsync(pagina);
        var a = v.DeLaCopia()[0];
        var pedidas = v.Red.Cuantas("/api/productos");

        await TeclaAsync(v, "Insert");

        Assert.True(await PanelAbiertoAsync(v));
        Assert.Contains("Varios: cantidad y código", await v.Pantalla.TextoAsync());
        // Para caja.js, un panel con su Enter en el cuadro de captura, dicho en una sola llamada.
        Assert.Equal((true, true, true), Ventana(pagina));
        Assert.Equal("Varios: ¿cuántos? Escribe la cantidad y Enter", (await v.Pantalla.PorIdAsync("codigo")).Atributos["placeholder"]);

        await EnterAsync(v, "3");
        Assert.Contains("Cantidad 3", await PanelAsync(v));
        Assert.Equal("Varios: escanea o escribe el código y Enter", (await v.Pantalla.PorIdAsync("codigo")).Atributos["placeholder"]);
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));

        await EnterAsync(v, a.Codigo!, rafaga: true);

        Assert.StartsWith($"3 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Equal((false, false, false), Ventana(pagina));
        // De la copia de la caja: el escaneo no fue a la red.
        Assert.Equal(pedidas, v.Red.Cuantas("/api/productos"));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Ins y el lector enseguida: el escaneo puede llegar antes de que caja.js sepa del panel (su Enter va como cualquier
    /// escaneo). Antes se perdía; ahora es el código de «Varios», que se queda y pide la cantidad. Lo que llega en ráfaga
    /// nunca es la cantidad.
    /// </summary>
    [Fact]
    public async Task El_escaneo_que_llega_mientras_se_abre_el_panel_es_su_codigo_y_no_se_pierde()
    {
        await using var v = await AbrirAsync();
        var (a, b) = v.DosDeLaCopia();

        await TeclaAsync(v, "Insert");
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado(a.Codigo!, 1000));

        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        var panel = await PanelAsync(v);
        Assert.Contains(a.Descripcion, panel);
        Assert.Contains("Ya está el código: ahora la cantidad y Enter.", panel);

        await EnterAsync(v, "2");
        Assert.StartsWith($"2 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.False(await PanelAbiertoAsync(v));

        // Un código largo en ráfaga en el paso de la cantidad: es el código, aunque sean solo números. Enter sin nada: una.
        await TeclaAsync(v, "Insert");
        await EnterAsync(v, b.Codigo!, rafaga: true);
        Assert.True(await PanelAbiertoAsync(v));
        await EnterAsync(v, "");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {b.Descripcion}", renglones[1]);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Otro_escaneo_antes_de_la_cantidad_mete_el_primero_con_una_pieza()
    {
        await using var v = await AbrirAsync();
        var (a, b) = v.DosDeLaCopia();

        await TeclaAsync(v, "Insert");
        await EnterAsync(v, a.Codigo!, rafaga: true);
        await EnterAsync(v, b.Codigo!, rafaga: true);

        // Nada se pierde: el primero entró como cualquier escaneo y el panel pide la cantidad del segundo.
        Assert.StartsWith($"1 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Contains(b.Descripcion, await PanelAsync(v));

        await EnterAsync(v, "4");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"4 {b.Descripcion}", renglones[1]);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Las mismas reglas que «5*código»: con decimales, solo lo que se vende por metro, kilo o litro.</summary>
    [Fact]
    public async Task Una_fraccion_de_algo_por_pieza_no_entra_y_el_panel_pide_otra_cantidad()
    {
        await using var v = await AbrirAsync();
        var a = v.DeLaCopia()[0];
        var granel = v.Contingencia.Paquete!.Productos
            .Where(p => p.Unidad != UnidadBase.Pieza && p.Codigo is not null && p.Presentacion.EsBase && v.Contingencia.Foto!.PorCodigo(p.Codigo) is not null)
            .OrderBy(p => p.VarianteId).First();

        await TeclaAsync(v, "Insert");
        await EnterAsync(v, "1.5");
        await EnterAsync(v, a.Codigo!, rafaga: true);

        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        var panel = await PanelAsync(v);
        Assert.Contains($"«{a.Descripcion}» se vende por pieza: escribe cuántos, sin fracciones.", panel);
        Assert.Contains(a.Descripcion, panel);
        await EnterAsync(v, "2");
        Assert.StartsWith($"2 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // A granel sí: metro y medio.
        await TeclaAsync(v, "Insert");
        await EnterAsync(v, "1.5");
        await EnterAsync(v, granel.Codigo!);
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith("1.5", renglones[1]);
        Assert.Contains(granel.Descripcion, renglones[1]);

        // Lo que no es una cantidad lo dice, y cero tampoco lo es.
        await TeclaAsync(v, "Insert");
        await EnterAsync(v, "0");
        Assert.Contains("«0» no es una cantidad", await PanelAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Cinco_e_Ins_es_la_cantidad_y_tres_por_codigo_va_de_una_vez()
    {
        await using var v = await AbrirAsync();
        var (a, b) = v.DosDeLaCopia();

        // «5» en el cuadro e Ins: caja.js ya lo vació y lo manda con la tecla.
        await TeclaAsync(v, "Insert", "5");
        Assert.Contains("Cantidad 5", await PanelAsync(v));
        await EnterAsync(v, a.Codigo!);

        // «3*código» en el paso de la cantidad: las dos de una vez, como en el cuadro.
        await TeclaAsync(v, "Insert");
        await EnterAsync(v, $"3*{b.Codigo}");

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"5 {a.Descripcion}", renglones[0]);
        Assert.StartsWith($"3 {b.Descripcion}", renglones[1]);
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Esc_cancela_y_lo_que_llevaba_no_entra()
    {
        await using var v = await AbrirAsync();
        var a = v.DeLaCopia()[0];

        await TeclaAsync(v, "Insert");
        await EnterAsync(v, a.Codigo!, rafaga: true);
        // Con algo pendiente, otra tecla no lo deja a medias: espera su Enter o Esc.
        await TeclaAsync(v, "F12", null);
        Assert.True(await PanelAbiertoAsync(v));
        Assert.Contains("Primero termina aquí", await v.Pantalla.TextoAsync());

        await TeclaAsync(v, "Escape");

        Assert.False(await PanelAbiertoAsync(v));
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        // Ya cerrado, un Enter que caja.js mandó tarde va como cualquier escaneo.
        await EnterAsync(v, a.Codigo!, rafaga: true);
        Assert.StartsWith($"1 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// La barra de abajo dice lo que hace Ins; el artículo común sigue con Ctrl+P (y en «/»). Ins por costumbre y luego Ctrl+P,
    /// sin nada escrito, pasa directo al artículo común.
    /// </summary>
    [Fact]
    public async Task La_barra_dice_Ins_Varios_y_el_articulo_comun_sigue_en_Ctrl_P()
    {
        await using var v = await AbrirAsync();

        var barra = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button")
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).Where(t => t.StartsWith("Ins", StringComparison.Ordinal)).ToList();
        Assert.Equal(["InsVarios"], barra);
        Assert.DoesNotContain("Art. común", await v.Pantalla.TextoAsync());

        await TeclaAsync(v, "Insert");
        await TeclaAsync(v, "Ctrl+P");
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Contains("No se descuenta del inventario", await v.Pantalla.TextoAsync());
        await TeclaAsync(v, "Escape");

        // En «/», cada una con su tecla.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("/"));
        var acciones = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        Assert.Contains("Varios: cantidad y código Ins", acciones);
        Assert.Contains("Artículo común (sin inventario) Ctrl+P", acciones);
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── Revisión del PR #133 ─────────────────────────────────────────────────

    /// <summary>
    /// «5» escrito y F11 por error: caja.js vacía el cuadro con F11 (usa lo escrito), pero el panel no se deja, así que lo escrito
    /// regresa al cuadro y el Enter siguiente sigue siendo «5». Antes se perdía y entraba una pieza.
    /// </summary>
    [Fact]
    public async Task Cinco_y_F11_por_error_no_se_lleva_la_cantidad()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await AbrirAsync(pagina);
        var a = v.DeLaCopia()[0];

        await TeclaAsync(v, "Insert");
        await TeclaAsync(v, "F11", "5");

        Assert.Contains("Primero termina aquí", await v.Pantalla.TextoAsync());
        Assert.Equal("5", pagina.Llamadas.Last(l => l.Funcion == "escribirCaptura").Argumentos![0]);
        await EnterAsync(v, "5");
        await EnterAsync(v, a.Codigo!, rafaga: true);
        Assert.StartsWith($"5 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Se escaneó uno (espera su cantidad) y luego se escribe «3*otro»: el primero entra con una pieza, como cuando llega otro
    /// código, y el otro con tres. Antes el primero se perdía.
    /// </summary>
    [Fact]
    public async Task Con_un_codigo_esperando_su_cantidad_tres_por_otro_mete_el_primero_con_una_pieza()
    {
        await using var v = await AbrirAsync();
        var (a, b) = v.DosDeLaCopia();

        await TeclaAsync(v, "Insert");
        await EnterAsync(v, a.Codigo!, rafaga: true);
        await EnterAsync(v, $"3*{b.Codigo}");

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {a.Descripcion}", renglones[0]);
        Assert.StartsWith($"3 {b.Descripcion}", renglones[1]);
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// La caja del navegador, sin copia: «5» e Ins le pregunta a la caja principal si «5» es un código. El panel se abre antes de
    /// esperar esa respuesta, así que el escaneo que llega mientras tanto es su código y entran cinco; antes entraba como una pieza.
    /// </summary>
    [Fact]
    public async Task Sin_copia_el_panel_se_abre_antes_de_preguntar_y_el_escaneo_de_mientras_es_su_codigo()
    {
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(red);
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var cliente = new ClienteCaja(http, conexion);
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(new RendimientoCaja())
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        var venta = await pantalla.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = caja,
            [nameof(PantallaVenta.Sesion)] = cajero,
            [nameof(PantallaVenta.Turno)] = turno,
        });
        Assert.Null(cliente.Foto);
        var codigo = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!.Productos
            .First(p => p.Unidad == UnidadBase.Pieza && p.Codigo is { Length: > 6 } && p.Presentacion.EsBase && p.Precios.Any(x => x.PresentacionId == null));

        red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ins = pantalla.Dispatcher.InvokeAsync(() => venta.Tecla("Insert", "5"));
        await VentaConCopia.EsperarAsync(() => red.Pedidas.Any(r => r.StartsWith("/api/productos/codigo/5?", StringComparison.Ordinal)), "la pregunta por «5»");
        Assert.Contains("Varios: cantidad y código", await pantalla.TextoAsync());
        await pantalla.Dispatcher.InvokeAsync(() => venta.Escaneado(codigo.Codigo!, 1234.5));
        red.Detenida.SetResult();
        await ins;

        // Sin copia, el renglón llega de la caja principal: se espera a que esté (con su límite).
        var limite = DateTime.UtcNow.AddSeconds(20);
        List<string> renglones;
        while ((renglones = await VentaConCopia.RenglonesAsync(pantalla)) is not [var unico] || !unico.StartsWith("5 ", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó el renglón: {string.Join(" | ", renglones)}");
            await Task.Delay(20);
        }
        Assert.StartsWith($"5 {codigo.Descripcion}", Assert.Single(renglones));
        Assert.Empty(pantalla.Errores);
    }

    /// <summary>
    /// La página que se detiene al avisarle que la ventana se cierra (como un caja.js que tarda en contestar): ahí, entre el Enter
    /// del código y su renglón, llega F6.
    /// </summary>
    private sealed class PaginaQueSeDetiene : IJSRuntime, IJSObjectReference
    {
        private readonly PaginaQueContesta _pagina = new();

        /// <summary>Mientras está puesta, avisar que la ventana se cerró espera a que se suelte.</summary>
        public TaskCompletionSource? Detener { get; set; }

        /// <summary>Se pone cuando ya está esperando.</summary>
        public TaskCompletionSource Detenida { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return (TValue)(object)this;
            if (identifier == "ventanaAbierta" && args is [false, ..] && Detener is { } detener)
            {
                Detenida.TrySetResult();
                await detener.Task;
            }
            return await _pagina.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// F6 mientras «Varios» se cierra (su renglón todavía no entra): espera a que entre, y el renglón se queda en el ticket que se
    /// pone en espera, no en el del siguiente cliente (revisión del PR #133).
    /// </summary>
    [Fact]
    public async Task F6_mientras_se_cierra_el_panel_espera_a_que_entre_el_renglon()
    {
        var pagina = new PaginaQueSeDetiene();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: pagina);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(b.Codigo!);

        await TeclaAsync(v, "Insert");
        await EnterAsync(v, "3");
        pagina.Detener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var codigo = EnterAsync(v, a.Codigo!, rafaga: true);
        await pagina.Detenida.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var enEspera = TeclaAsync(v, "F6", null);
        pagina.Detener.SetResult();
        await codigo;
        await enEspera;

        // El ticket nuevo quedó vacío: los dos renglones se fueron con el de la espera.
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        await TeclaAsync(v, "F5", null);
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.Contains(renglones, r => r.StartsWith($"3 {a.Descripcion}", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }
}
