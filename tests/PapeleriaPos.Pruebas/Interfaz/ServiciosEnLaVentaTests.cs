using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// «Servicios» en la venta (el dueño, 2026-10-31: «de momento solo la CURP… ejecutar servicios directamente desde el buscador, algo
/// como /servicios, o incluso /curp»), con la pantalla de verdad:
/// <list type="bullet">
/// <item>«/servicios» abre la lista y «/curp» la CURP; «/» las enseña con su comando;</item>
/// <item>un escaneo nunca empieza un comando, y con la lista de «/» abierta lo escaneado entra al ticket;</item>
/// <item>«curp» en la búsqueda trae el trámite arriba de los productos, sin cambiar lo que hace Enter;</item>
/// <item>el rápido «CURP» (Alt+2) abre el trámite, con «Solo cobrar»; con la cantidad antes cobra como siempre;</item>
/// <item>en la ventana de Windows (el panel de mentiras) el documento se atrapa, se imprime como «T-0001 · CURP», la CURP entra al ticket
/// una vez, F12 termina y cobra, y al terminar se borra todo y se anota sin datos del cliente; en la caja del navegador se abre en otra
/// pestaña y se cobra con «Cobrar CURP».</item>
/// </list>
/// </summary>
public sealed class ServiciosEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"servicios-venta-{Guid.NewGuid():N}.db");
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"servicios-tramites-{Guid.NewGuid():N}");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private RegistroDiario Registro => new(Path.Combine(_carpeta, "registros"), "caja");

    private Task<VentaConCopia> AbrirAsync(IPanelDeTramites? panel = null, PaginaQueContesta? pagina = null) =>
        VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: pagina, registro: Registro, tramites: panel,
            archivos: new ArchivosDeTramites(Path.Combine(_carpeta, "tramites")));

    /// <summary>
    /// El producto de la CURP con su código, como en eleventa («CURP», $10). En la base de la prueba es «Trámite CURP», con otro código
    /// o sin él: se le pone (una vez para todas las pruebas de aquí), antes de abrir la venta (así va en su copia).
    /// </summary>
    private async Task ConElProductoCurpAsync()
    {
        await using (var db = servicio.Base())
        {
            if (!await db.CodigosBarras.AnyAsync(c => c.Codigo == "CURP"))
            {
                var variante = await db.Variantes.FirstAsync(v => v.Producto!.Nombre == "Trámite" && v.Descripcion == "CURP");
                var presentacion = await db.Presentaciones.FirstAsync(p => p.VarianteId == variante.Id && p.EsBase);
                db.CodigosBarras.Add(new CodigoBarras { Codigo = "CURP", PresentacionId = presentacion.Id, Origen = OrigenCodigo.Interno });
                await db.SaveChangesAsync();
            }
        }
        await servicio.CatalogoAlDiaAsync();
    }

    /// <summary>Lo que cuesta la CURP en esta caja: su producto (de la copia), una pieza.</summary>
    private static long PrecioDeLaCurp(VentaConCopia v) =>
        v.Contingencia.Foto!.PorCodigo("CURP") is { } curp
            ? PapeleriaPos.Interfaz.Componentes.PantallaVenta.PrecioDelRapido(new PapeleriaPos.Nucleo.Configuracion.Rapido("CURP", curp.VarianteId), curp, v.Caja.SucursalId)
            : throw new Xunit.Sdk.XunitException("La copia no trae el producto CURP.");

    private static Task LugarAsync(VentaConCopia v, double x, double y, double ancho, double alto) =>
        v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.LugarDelTramite(x, y, ancho, alto));

    private async Task<string> AnotadoAsync(string contiene)
    {
        await VentaConCopia.EsperarAsync(() => File.Exists(Registro.ArchivoDeHoy) && File.ReadAllText(Registro.ArchivoDeHoy).Contains(contiene),
            $"«{contiene}» en el registro");
        return File.ReadAllText(Registro.ArchivoDeHoy);
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    /// <summary>El Enter del cuadro de captura: lo tecleado a mano, o lo del lector (<paramref name="delLector"/>).</summary>
    private static Task EnterAsync(VentaConCopia v, string texto, bool delLector = false) =>
        v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado(texto, null, null, delLector));

    private static Task EscribirAsync(VentaConCopia v, string texto) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(texto));

    private static async Task<string> TextoAsync(VentaConCopia v) => PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());

    private static async Task<bool> TramiteAbiertoAsync(VentaConCopia v) => (await TextoAsync(v)).Contains("CURP · T-");

    private static Task<List<string>> TicketAsync(VentaConCopia v) => VentaConCopia.RenglonesAsync(v.Pantalla);

    // ── Desde el buscador: «/servicios», «/curp» y «/» ──

    [Fact]
    public async Task Servicios_abre_la_lista_con_la_curp_su_precio_y_como_se_abre()
    {
        await ConElProductoCurpAsync();
        await using var v = await AbrirAsync();
        var precio = PrecioDeLaCurp(v);

        await EnterAsync(v, "/servicios");

        await v.Pantalla.EsperarAsync(t => t.Contains("Servicios"), "la lista de servicios");
        var curp = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("servicio-curp")).Texto);
        Assert.Contains("CURP", curp);
        Assert.Contains("/curp o Alt+2 · gob.mx/curp", curp);
        Assert.Contains(Formato.Dinero(precio), curp);
        // Se abre con un clic (o Enter, con el cursor ahí).
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("servicio-curp"));
        Assert.True(await TramiteAbiertoAsync(v));
        Assert.Contains("CURP · T-0001", await TextoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task La_lista_de_las_acciones_ensena_los_comandos_y_el_exacto_va_primero()
    {
        await using var v = await AbrirAsync();

        await EscribirAsync(v, "/");

        var texto = await TextoAsync(v);
        Assert.Contains("Servicios: trámites en su página oficial /servicios", texto);
        Assert.Contains("CURP: el trámite en gob.mx/curp /curp Alt+2", texto);
        // «/curp»: la de la CURP primero (Enter la hace).
        await EscribirAsync(v, "/curp");
        Assert.StartsWith("CURP: el trámite", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("acc-0")).Texto));
        await EscribirAsync(v, "/servi");
        Assert.StartsWith("Servicios", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("acc-0")).Texto));

        await EnterAsync(v, "/curp");

        Assert.True(await TramiteAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Un_escaneo_nunca_empieza_un_comando_y_con_la_lista_abierta_entra_al_ticket()
    {
        await using var v = await AbrirAsync();
        var producto = v.DeLaCopia()[0];

        // Un código que empieza con «/» (del lector): no abre nada.
        await EnterAsync(v, "/curp", delLector: true);
        Assert.False(await TramiteAbiertoAsync(v));
        Assert.DoesNotContain("Acciones que tienes", await TextoAsync(v));

        // Con «/» escrito a mano (la lista abierta), el lector lee un producto: caja.js manda solo el código, y entra al ticket.
        await EscribirAsync(v, "/");
        Assert.Contains("Acciones que tienes", await TextoAsync(v));
        var antes = (await TicketAsync(v)).Count;
        await EnterAsync(v, producto.Codigo!, delLector: true);
        await v.Pantalla.EsperarAsync(t => !t.Contains("Acciones que tienes"), "que se cierre la lista de las acciones");
        var ticket = await TicketAsync(v);
        Assert.Equal(antes + 1, ticket.Count);
        Assert.Contains(producto.Descripcion, ticket[^1]);
        Assert.False(await TramiteAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Curp_en_la_busqueda_trae_el_tramite_arriba_y_Enter_sigue_como_siempre()
    {
        await ConElProductoCurpAsync();
        await using var v = await AbrirAsync();

        await EscribirAsync(v, "curp");

        var renglon = await v.Pantalla.PorIdAsync("res-tramite-curp");
        Assert.Contains("Trámite: CURP en gob.mx/curp", PantallaSinNavegador.Limpio(renglon.Texto));
        // Enter sin elegir nada: lo de siempre, el producto CURP por su código.
        await EnterAsync(v, "curp");
        await v.Pantalla.EsperarAsync(t => !t.Contains("Trámite: CURP en"), "que se cierre la lista");
        Assert.Contains("CURP", Assert.Single(await TicketAsync(v)));
        Assert.False(await TramiteAbiertoAsync(v));

        // Un clic en el renglón del trámite lo abre.
        await EscribirAsync(v, "curp");
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("res-tramite-curp"));
        Assert.True(await TramiteAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── El rápido «CURP» (Alt+2) ──

    [Fact]
    public async Task El_rapido_curp_abre_el_tramite_con_solo_cobrar_y_con_la_cantidad_cobra_como_siempre()
    {
        await using var v = await AbrirAsync();

        await TeclaAsync(v, "Alt+2");

        Assert.True(await TramiteAbiertoAsync(v));
        Assert.Empty(await TicketAsync(v));
        // «Solo cobrar»: el cliente trae su archivo; lo del rápido (10 impresiones), y el trámite se termina.
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("tramite-solo-cobrar"));
        await v.Pantalla.EsperarAsync(t => !t.Contains("CURP · T-"), "que se cierre el trámite");
        Assert.StartsWith("10 ", Assert.Single(await TicketAsync(v)));

        // «2» y Alt+2: dos que trajo el cliente, directo, como antes.
        await TeclaAsync(v, "Alt+2", "2");
        Assert.False(await TramiteAbiertoAsync(v));
        Assert.StartsWith("30 ", Assert.Single(await TicketAsync(v)));
        Assert.Contains("Trámite T-0001 · CURP · solo cobrar", await AnotadoAsync("Trámite T-0001"));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── La caja del navegador: en otra pestaña ──

    [Fact]
    public async Task En_la_caja_del_navegador_se_abre_en_otra_pestana_y_se_cobra_una_vez()
    {
        await ConElProductoCurpAsync();
        await using var v = await AbrirAsync();
        var precio = PrecioDeLaCurp(v);

        await EnterAsync(v, "/curp");

        var enlace = await v.Pantalla.PorIdAsync("tramite-otra-pestana");
        Assert.Equal("https://www.gob.mx/curp/", enlace.Atributos["href"]);
        Assert.Equal("_blank", enlace.Atributos["target"]);
        Assert.Contains("Abrir gob.mx/curp en otra pestaña", PantallaSinNavegador.Limpio(enlace.Texto));
        Assert.Contains("bórralo de Descargas", await TextoAsync(v));
        // Sin cobrar, F12 no lo deja: se perdería.
        await TeclaAsync(v, "F12", null);
        Assert.True(await TramiteAbiertoAsync(v));
        Assert.Contains("Primero termina el trámite", await TextoAsync(v));

        var cobrar = await v.Pantalla.PorIdAsync("tramite-cobrar");
        Assert.Equal($"Cobrar CURP {Formato.Dinero(precio)}", PantallaSinNavegador.Limpio(cobrar.Texto));
        await v.Pantalla.OprimirAsync(cobrar);
        await v.Pantalla.EsperarAsync(t => t.Contains("ya está en el ticket"), "la CURP en el ticket");
        Assert.True((await v.Pantalla.PorIdAsync("tramite-cobrar")).Desactivado);
        Assert.Contains("CURP", Assert.Single(await TicketAsync(v)));

        // Ya se cobró: F12 termina el trámite y va al cobro.
        await TeclaAsync(v, "F12", null);
        Assert.False(await TramiteAbiertoAsync(v));
        Assert.Contains("CURP", Assert.Single(await TicketAsync(v)));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── La ventana de Windows: la página oficial en su panel ──

    [Fact]
    public async Task En_la_ventana_de_windows_atrapa_imprime_con_su_folio_cobra_una_vez_y_al_terminar_borra_todo()
    {
        await ConElProductoCurpAsync();
        var panel = new PanelDeTramitesFalso();
        var pagina = new PaginaQueContesta();
        await using var v = await AbrirAsync(panel, pagina);

        await EnterAsync(v, "/curp");

        Assert.Equal(new Uri("https://www.gob.mx/curp/"), panel.Abierta);
        var texto = await TextoAsync(v);
        Assert.Contains("CURP · T-0001", texto);
        Assert.Contains("gob.mx/curp", texto);
        Assert.Contains("Abriendo la página oficial", texto);
        // El hueco del ticket, donde va la página oficial: caja.js dice dónde está, y la página se pone ahí.
        await v.Pantalla.EsperarElementoAsync("tramite-lugar");
        await VentaConCopia.EsperarAsync(() => pagina.Llamadas.Any(l => l.Funcion == "observarLugar"), "que caja.js observe el lugar del ticket");
        await LugarAsync(v, 16, 118, 930, 560);
        Assert.Equal(new LugarEnPantalla(16, 118, 930, 560), panel.Lugares[^1]);
        // Antes del documento no se deja con otra tecla: la página con lo escrito se perdería.
        await TeclaAsync(v, "F12", null);
        Assert.Contains("Primero termina el trámite", await TextoAsync(v));

        // La página carga y descarga «CURP_…pdf»: se atrapa como T-0001.pdf y se ofrece imprimir.
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.Atrapa!.Cargo());
        await v.Pantalla.EsperarAsync(t => t.Contains("Esperando el documento"), "la página oficial");
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.DescargarAsync());
        await v.Pantalla.EsperarAsync(t => t.Contains("Documento listo"), "el documento");
        Assert.Contains("T-0001.pdf · 1 página", await TextoAsync(v));
        Assert.True(File.Exists(Path.Combine(_carpeta, "tramites", "T-0001", "T-0001.pdf")));
        Assert.Empty(await TicketAsync(v));
        // La impresora del agente primero.
        Assert.Equal("EPSON WF-6590 Series", (await v.Pantalla.PorIdAsync("tramite-impresora")).Valor);

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("tramite-imprimir"));

        await v.Pantalla.EsperarAsync(t => t.Contains("ya está en el ticket"), "la CURP en el ticket");
        Assert.Equal([("T-0001 · CURP", 1, "EPSON WF-6590 Series")], panel.Impresos);
        Assert.Contains("CURP", Assert.Single(await TicketAsync(v)));
        // Otra copia: va como copia de más; la CURP no entra otra vez.
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("tramite-imprimir"));
        await VentaConCopia.EsperarAsync(() => panel.Impresos.Count == 2, "la copia de más");
        Assert.Equal(("T-0001 · CURP · copias extra", 1, "EPSON WF-6590 Series"), panel.Impresos[1]);
        await v.Pantalla.EsperarAsync(t => t.Contains("La copia de más sale en F2"), "el aviso de la copia de más");
        Assert.Single(await TicketAsync(v));

        // Entregado: F12 termina el trámite (se borra todo) y va al cobro.
        await TeclaAsync(v, "F12", null);

        await VentaConCopia.EsperarAsync(() => panel.Cerrado, "que se cierre el panel");
        Assert.False(await TramiteAbiertoAsync(v));
        Assert.False(Directory.Exists(Path.Combine(_carpeta, "tramites", "T-0001")));
        Assert.Contains("CURP", Assert.Single(await TicketAsync(v)));
        var anotado = await AnotadoAsync("Trámite T-0001");
        Assert.Matches(@"Trámite T-0001 · CURP · impreso \(2 copias\) · \d+ s", anotado);
        Assert.DoesNotContain("CURP_", anotado);
        // caja.js deja de observar el lugar del ticket.
        await VentaConCopia.EsperarAsync(() => pagina.Llamadas.Any(l => l.Funcion == "dejarDeObservarLugar"), "que caja.js deje de observar");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_otra_pagina_la_pagina_oficial_se_oculta_y_Esc_termina_sin_cobrar()
    {
        var panel = new PanelDeTramitesFalso();
        await using var v = await AbrirAsync(panel);
        await EnterAsync(v, "/curp");
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.Atrapa!.Cargo());
        await LugarAsync(v, 16, 118, 930, 560);

        // A Clientes (otra página): el panel se queda en la venta, oculto, y la página oficial también.
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes",
            "la pestaña Clientes"));
        await LugarAsync(v, 16, 118, 930, 560);
        Assert.False(panel.Lugares[^1].SeVe);
        await TeclaAsync(v, "F1", null);
        await LugarAsync(v, 16, 118, 930, 560);
        Assert.True(panel.Lugares[^1].SeVe);

        // Esc: se termina sin documento y sin cobrar.
        await TeclaAsync(v, "Escape", null);

        await VentaConCopia.EsperarAsync(() => panel.Cerrado, "que se cierre el panel");
        Assert.Empty(await TicketAsync(v));
        Assert.Contains("Trámite T-0001 · CURP · sin documento", await AnotadoAsync("Trámite T-0001"));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Sin_el_producto_curp_cobra_lo_de_su_rapido()
    {
        // En una base sin el código CURP (aquí se quita si ya se puso): lo del rápido «CURP», 10 impresiones.
        await using (var db = servicio.Base())
        {
            await db.CodigosBarras.Where(c => c.Codigo == "CURP").ExecuteDeleteAsync();
        }
        await servicio.CatalogoAlDiaAsync();
        var panel = new PanelDeTramitesFalso();
        await using var v = await AbrirAsync(panel);
        await EnterAsync(v, "/curp");
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.Atrapa!.Cargo());
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.DescargarAsync());
        await v.Pantalla.EsperarAsync(t => t.Contains("Documento listo"), "el documento");

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("tramite-whatsapp"));

        await v.Pantalla.EsperarAsync(t => t.Contains("ya está en el ticket"), "lo del rápido en el ticket");
        Assert.StartsWith("10 ", Assert.Single(await TicketAsync(v)));
        Assert.EndsWith(Path.Combine("T-0001", "CURP.pdf"), panel.Copiado);
        Assert.Empty(v.Pantalla.Errores);
    }
}
