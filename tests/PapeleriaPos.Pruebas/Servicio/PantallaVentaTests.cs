using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using PapeleriaPos.Herramientas.Prueba;
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

        // Clientes ya no es una ventana encima del ticket: es una página (entrega 8), y para caja.js no hay ventana. F1 regresa
        // a vender y Ctrl+U cambia de usuario, como en el prototipo. (La pestaña, antes de abrir la página.)
        var pestanaClientes = dibujo.Boton("Clientes")!.Value;
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(pestanaClientes));
        Assert.True(pagina.PaginaAbierta);
        Assert.False(pagina.VentanaAbierta);
        await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F1", null));
        Assert.False(pagina.PaginaAbierta);
        Assert.Equal(2, salidas);
        await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(pestanaClientes));
        Assert.True(pagina.PaginaAbierta);
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
        // Para caja.js es una página, como desde la venta (entrega 8): atrapa Esc, las F y las Ctrl de la caja, y deja a la
        // página las flechas, Enter y lo que se escribe.
        await Esperar(() => pagina.Registrada && pagina.PaginaAbierta == true && pagina.VentanaAbierta == false);

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

        // Al irse, caja.js olvida la «ventana» y la página: si no, en la venta del que entra después el cuadro no tomaría los
        // escaneos.
        await ((IAsyncDisposable)dibujo).DisposeAsync();
        Assert.False(pagina.VentanaAbierta);
        Assert.False(pagina.PaginaAbierta);
        Assert.False(pagina.Registrada);
    }

    /// <summary>
    /// El corte desde la venta (entrega 6): quien no tiene el permiso ve «Que entre a esta caja», que cambia de usuario
    /// como Ctrl+U; quien lo tiene escribe el total y cierra el turno, y cerrar el corte ya hecho (Esc, la × o F1) sigue al
    /// siguiente turno, como «Listo»: la venta no se queda en un turno cerrado.
    /// </summary>
    [Fact]
    public async Task El_corte_desde_la_venta_cambia_de_usuario_o_cierra_y_sigue_al_siguiente_turno()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var turnoCajero = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var pagina = new PaginaFalsa();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var _ = servicios;
        var (salidas, cerrados) = (0, 0);
        Dictionary<string, object?> Parametros(SesionDto sesion, TurnoDto turno) => new()
        {
            [nameof(PantallaVenta.Caja)] = caja,
            [nameof(PantallaVenta.Sesion)] = sesion,
            [nameof(PantallaVenta.Turno)] = turno,
            [nameof(PantallaVenta.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
            [nameof(PantallaVenta.AlCerrarTurno)] = new EventCallback(null, (Action)(() => cerrados++)),
        };

        // El cajero no tiene el corte: «Que entre a esta caja» es cambiar de usuario, y el turno sigue abierto.
        await using (var dibujo = new Dibujo(servicios))
        {
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<PantallaVenta>(Parametros(cajero, turnoCajero)));
            await Esperar(() => dibujo.Textos.Contains("Copia B/N"));
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Corte")!.Value));
            await Esperar(() => dibujo.Boton("Que entre a esta caja") is not null);
            Assert.DoesNotContain("Efectivo contado", dibujo.Textos);
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Que entre a esta caja")!.Value));
            Assert.Equal((1, 0), (salidas, cerrados));
            Assert.Empty(dibujo.Errores);
        }
        Assert.Equal(turnoCajero.Id, (await cliente.TurnoAsync(caja.Id))?.Id);

        // El encargado, con su turno, cierra el suyo con el total; Esc después del corte sigue al siguiente turno.
        await cliente.CorteZAsync(turnoCajero.Id, new SolicitudCorteZ(encargado.UsuarioId, 50000));
        var turnoEncargado = await cliente.AbrirTurnoAsync(caja.Id, encargado.UsuarioId, 50000);
        await using (var dibujo = new Dibujo(servicios))
        {
            var pantalla = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<PantallaVenta>(Parametros(encargado, turnoEncargado)));
            await Esperar(() => dibujo.Textos.Contains("Copia B/N"));
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Corte")!.Value));
            await Esperar(() => dibujo.Evento("corte-contado", "oninput") is not null);
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.EscribirAsync(dibujo.Evento("corte-contado", "oninput")!.Value, "480"));
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Cerrar el turno")!.Value));
            await Esperar(() => dibujo.Boton("Listo: abrir el siguiente turno") is not null);
            Assert.Null(await cliente.TurnoAsync(caja.Id));
            // Todavía no se oprime «Listo»: el corte se ve hasta cerrarlo.
            Assert.Equal((1, 0), (salidas, cerrados));

            await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Escape", ""));
            Assert.Equal((1, 1), (salidas, cerrados));
            Assert.False(pagina.VentanaAbierta);
            Assert.Empty(dibujo.Errores);
        }
    }

    /// <summary>
    /// Lo que queda sin cobrar es de la caja, no del turno, como en el prototipo: el corte ya no dice que se pierde (no era
    /// cierto: los tickets en espera volvían en el siguiente turno), y lo guarda con el ticket que se atendía, igual que
    /// cambiar de usuario. Así vuelven en espera en el siguiente turno, aunque sea de otro cajero.
    /// </summary>
    [Fact]
    public async Task Lo_que_queda_sin_cobrar_se_queda_en_espera_con_el_corte_y_al_cambiar_de_usuario()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"espera-venta-{Guid.NewGuid():N}.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var http = new HttpClient(prueba.Fabrica.Server.CreateHandler());
        // La caja de Windows: con su base local, donde se guardan los tickets en espera.
        var cliente = new ClienteCaja(http, conexion, new Contingencia(new AlmacenLocal(ruta), http, conexion));
        try
        {
            var caja = (await cliente.CajasAsync()).Single();
            var cajero = await cliente.EntrarAsync("cajero", "1111");
            var encargado = await cliente.EntrarAsync("encargado", "2222");
            var turnoEncargado = await TurnoDeAsync(cliente, caja, encargado, encargado);
            var pagina = new PaginaFalsa();
            var servicios = new ServiceCollection()
                .AddSingleton(conexion)
                .AddSingleton(cliente)
                .AddSingleton<IImpresora>(new SinImpresora())
                .AddSingleton<IJSRuntime>(pagina)
                .AddScoped<ModuloCaja>()
                .BuildServiceProvider();
            await using var _ = servicios;
            var (salidas, cerrados) = (0, 0);
            Dictionary<string, object?> Parametros(SesionDto sesion, TurnoDto turno) => new()
            {
                [nameof(PantallaVenta.Caja)] = caja,
                [nameof(PantallaVenta.Sesion)] = sesion,
                [nameof(PantallaVenta.Turno)] = turno,
                [nameof(PantallaVenta.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
                [nameof(PantallaVenta.AlCerrarTurno)] = new EventCallback(null, (Action)(() => cerrados++)),
            };

            // El encargado deja uno en espera (F6) y otro a medias, sin F6: hasta aquí solo el primero está guardado.
            await using (var dibujo = new Dibujo(servicios))
            {
                var pantalla = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<PantallaVenta>(Parametros(encargado, turnoEncargado)));
                await Esperar(() => dibujo.Textos.Contains("Copia B/N"));
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Alt+1", ""));
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("F6", ""));
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Alt+1", ""));
                Assert.Single(cliente.TicketsEnEspera());

                // El corte dice que se quedan, y al cerrar el turno se guardan los dos.
                await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Corte")!.Value));
                await Esperar(() => dibujo.Evento("corte-contado", "oninput") is not null);
                Assert.True(dibujo.TieneRenglon("Hay 2 tickets sin cobrar: se quedan"));
                Assert.DoesNotContain("se pierden", dibujo.Textos);
                await dibujo.Dispatcher.InvokeAsync(() => dibujo.EscribirAsync(dibujo.Evento("corte-contado", "oninput")!.Value, "500"));
                await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Cerrar el turno")!.Value));
                await Esperar(() => dibujo.Boton("Listo: abrir el siguiente turno") is not null);
                Assert.Equal(2, cliente.TicketsEnEspera().Count);
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Escape", ""));
                Assert.Equal((0, 1), (salidas, cerrados));
                Assert.Empty(dibujo.Errores);
            }

            // El siguiente turno es del cajero: le vuelven los dos. Lo que deja a medias tampoco se pierde al cambiar de usuario.
            var turnoCajero = await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
            await using (var dibujo = new Dibujo(servicios))
            {
                var pantalla = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<PantallaVenta>(Parametros(cajero, turnoCajero)));
                await Esperar(() => dibujo.Textos.Contains("Copia B/N"));
                Assert.True(dibujo.TieneRenglon("Volvieron los 2 tickets que estaban en espera."));
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Alt+1", ""));
                await dibujo.Dispatcher.InvokeAsync(() => pantalla.Tecla("Ctrl+U", ""));
                Assert.Equal((1, 1), (salidas, cerrados));
                Assert.Equal(3, cliente.TicketsEnEspera().Count);
                Assert.Empty(dibujo.Errores);
            }
        }
        finally
        {
            ArchivosDePrueba.BorrarBase(ruta);
        }
    }

    /// <summary>
    /// «Imprimir» en el estado de cuenta, en «Productos e inventario, sin cortar»: como en la venta, en la impresora de
    /// tickets o, sin ella, en pantalla. Antes ahí no hacía nada, y la guía de la prueba lo pide como dueño sin cortar.
    /// </summary>
    [Fact]
    public async Task Sin_cortar_el_estado_de_cuenta_se_imprime_como_en_la_venta()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        var dueno = await cliente.EntrarAsync("dueno", "3333");

        async Task<string> ImprimirElEstadoAsync(IImpresora impresora)
        {
            var servicios = new ServiceCollection()
                .AddSingleton(conexion)
                .AddSingleton(cliente)
                .AddSingleton(impresora)
                .AddSingleton<IJSRuntime>(new PaginaFalsa())
                .AddScoped<ModuloCaja>()
                .BuildServiceProvider();
            await using var _ = servicios;
            await using var dibujo = new Dibujo(servicios);
            var administracion = await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<Administracion>(new Dictionary<string, object?>
            {
                [nameof(Administracion.Caja)] = caja,
                [nameof(Administracion.Sesion)] = dueno,
            }));
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Clientes")!.Value));
            await Esperar(() => dibujo.Boton(PreparacionPrueba.ConDeuda) is not null);
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton(PreparacionPrueba.ConDeuda)!.Value));
            // Su cuenta, con lo vencido (solo él debe algo vencido): la página abre con el primero de la lista, que puede ser
            // otro, y el «Estado de cuenta» de la suya sale con ella.
            await Esperar(() => dibujo.Textos.Contains("venció el"));
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Estado de cuenta")!.Value));
            // «Imprimir» sale junto con «Copiar para WhatsApp» (antes de él puede haber otro «Imprimir…» de Inventario).
            await Esperar(() => dibujo.Boton("Copiar para WhatsApp") is not null);
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.ApretarAsync(dibujo.Boton("Imprimir")!.Value));
            // Esc cierra la vista previa (si la hay), como en la venta.
            await dibujo.Dispatcher.InvokeAsync(() => administracion.Tecla("Escape", null));
            Assert.Empty(dibujo.Errores);
            return dibujo.Textos;
        }

        // Sin impresora, en pantalla.
        var textos = await ImprimirElEstadoAsync(new SinImpresora());
        Assert.Contains($"Estado de cuenta de {PreparacionPrueba.ConDeuda}\n", textos);
        Assert.Contains("Sin impresora conectada: así saldrá impreso en la de 80 mm.", textos);

        // Con impresora, el papel; nada en pantalla.
        var impresora = new ImpresoraQueAnota();
        textos = await ImprimirElEstadoAsync(impresora);
        Assert.Equal(1, impresora.Impresos);
        Assert.DoesNotContain("Sin impresora conectada", textos);
    }

    /// <summary>
    /// El turno de otro cajero nombra a quien hace el corte igual que el corte: primero los encargados y al final el dueño.
    /// Antes salía de las fichas, por nombre, y las dos pantallas los decían al revés.
    /// </summary>
    [Fact]
    public async Task El_turno_de_otro_nombra_a_quien_corta_como_el_corte()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaFalsa())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var _ = servicios;
        Dictionary<string, object?> Parametros(SesionDto sesion, TurnoDto turno) => new()
        {
            [nameof(TurnoDeOtro.Caja)] = caja,
            [nameof(TurnoDeOtro.Turno)] = turno,
            [nameof(TurnoDeOtro.Sesion)] = sesion,
        };

        // El encargado, con el turno del cajero: el corte, ahí mismo.
        var turnoCajero = await TurnoDeAsync(cliente, caja, cajero, encargado);
        await using (var dibujo = new Dibujo(servicios))
        {
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<TurnoDeOtro>(Parametros(encargado, turnoCajero)));
            // «Hacer el corte de Cajero de prueba»: el nombre va en otro texto.
            await Esperar(() => dibujo.Boton("Hacer el corte de") is not null);
            Assert.DoesNotContain("El corte lo hace quien tiene ese permiso", dibujo.Textos);
            Assert.Empty(dibujo.Errores);
        }

        // El cajero, con el turno del encargado: quién lo hace, en el orden del corte.
        var turnoEncargado = await TurnoDeAsync(cliente, caja, encargado, encargado);
        var delCorte = (await cliente.CorteInformativoAsync(turnoEncargado.Id, cajero.UsuarioId)).QuienesCortan;
        Assert.Equal(["Encargado de prueba", "Dueño de prueba"], delCorte.Select(q => q.Nombre));
        await using (var dibujo = new Dibujo(servicios))
        {
            await dibujo.Dispatcher.InvokeAsync(() => dibujo.DibujarAsync<TurnoDeOtro>(Parametros(cajero, turnoEncargado)));
            await Esperar(() => dibujo.Textos.Contains("El corte lo hace quien tiene ese permiso"));
            Assert.True(dibujo.TieneRenglon(
                "El corte lo hace quien tiene ese permiso: Encargado de prueba o Dueño de prueba. Pídele que entre en esta caja."));
            Assert.Null(dibujo.Boton("Hacer el corte de"));
            Assert.Empty(dibujo.Errores);
        }

        // Como lo dejan las demás: el turno del cajero abierto.
        await TurnoDeAsync(cliente, caja, cajero, encargado);
    }

    /// <summary>
    /// Deja abierto el turno de <paramref name="quien"/> en la caja: si había otro, <paramref name="encargado"/> lo corta. Las
    /// pruebas de esta clase comparten el servicio y no corren en un orden fijo.
    /// </summary>
    private static async Task<TurnoDto> TurnoDeAsync(ClienteCaja cliente, CajaDto caja, SesionDto quien, SesionDto encargado)
    {
        if (await cliente.TurnoAsync(caja.Id) is { } abierto)
        {
            if (abierto.CajeroId == quien.UsuarioId)
                return abierto;
            await cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(encargado.UsuarioId, 50000));
        }
        return await cliente.AbrirTurnoAsync(caja.Id, quien.UsuarioId, 50000);
    }

    /// <summary>Una impresora que solo cuenta lo que le llega: para ver que se mandó al papel y no a la pantalla.</summary>
    private sealed class ImpresoraQueAnota : IImpresora
    {
        private int _impresos;

        public int Impresos => Volatile.Read(ref _impresos);

        public bool MuestraVistaPrevia => false;

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _impresos);
            return Task.CompletedTask;
        }
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

        /// <summary>Lo último que se le dijo a caja.js de las páginas (entrega 8); null si nunca se le dijo.</summary>
        public bool? PaginaAbierta
        {
            get
            {
                lock (_candado)
                    return _paginaAbierta;
            }
        }

        private bool? _ventanaAbierta;
        private bool? _paginaAbierta;
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
                    "paginaAbierta" => _paginaAbierta = (bool)args![0]!,
                    "registrar" => _registrada = true,
                    // Como caja.js: al quitar las teclas olvida también la página.
                    "quitar" => (_registrada, _paginaAbierta) = (false, false),
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
    private sealed class Dibujo : Renderer
    {
        private readonly List<(string Texto, ulong Clic)> _botones = [];
        private readonly Dictionary<string, ulong> _eventos = [];
        private string _textos = "";

        /// <summary>Enfocar un campo (FocusAsync, en el corte) va a la página falsa, que contesta todo.</summary>
        public Dibujo(IServiceProvider servicios) : base(servicios, NullLoggerFactory.Instance) =>
            ElementReferenceContext = new WebElementReferenceContext(servicios.GetRequiredService<IJSRuntime>());

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

        /// <summary>El último manejador de ese evento («oninput», «onkeydown»…) en el elemento con ese id, o null.</summary>
        public ulong? Evento(string id, string evento)
        {
            lock (_botones)
                return _eventos.TryGetValue($"{id}:{evento}", out var manejador) ? manejador : null;
        }

        /// <summary>Lo que se escribe en un campo con @bind: llega como su «oninput».</summary>
        public Task EscribirAsync(ulong manejador, string valor) => DispatchEventAsync(manejador, null, new ChangeEventArgs { Value = valor });

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
                string? id = null;
                ulong? clicSinNombre = null;
                for (var i = 0; i < lote.ReferenceFrames.Count; i++)
                {
                    var marco = lote.ReferenceFrames.Array[i];
                    switch (marco.FrameType)
                    {
                        case RenderTreeFrameType.Element:
                            (titulo, id) = (null, null);
                            break;
                        case RenderTreeFrameType.Attribute when marco.AttributeName == "title":
                            titulo = marco.AttributeValue as string;
                            break;
                        case RenderTreeFrameType.Attribute when marco.AttributeName == "id":
                            id = marco.AttributeValue as string;
                            break;
                        case RenderTreeFrameType.Attribute when id is not null && marco.AttributeEventHandlerId != 0 && marco.AttributeName != "onclick":
                            _eventos[$"{id}:{marco.AttributeName}"] = marco.AttributeEventHandlerId;
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
