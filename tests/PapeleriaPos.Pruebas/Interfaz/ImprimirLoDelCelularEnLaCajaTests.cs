using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Imprimir el ticket de lo que cobra el celular, en la caja (dueño, 2026-11-26; docs/celular-como-caja.md, paso 11): el interruptor
/// «Imprimir lo que cobra el celular» de Configuración › Ticket, que se maneja solo con el teclado, y la ventana de la caja, que imprime
/// lo que le toca desde que abre (con la entrada a la vista, sin que nadie haya entrado a vender).
/// </summary>
public sealed class ImprimirLoDelCelularEnLaCajaTests
{
    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    private sealed class ImpresoraQueGuarda : IImpresora
    {
        public ConcurrentQueue<byte[]> Papeles { get; } = new();
        public bool MuestraVistaPrevia => false;

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            Papeles.Enqueue(escPos);
            return Task.CompletedTask;
        }
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static ServiceCollection Servicios(ServicioDePrueba servicio, ConexionCaja conexion, IImpresora impresora, PaginaQueContesta pagina)
    {
        var coleccion = new ServiceCollection();
        coleccion.AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton(impresora)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .AddSingleton<NavigationManager>(new NavegacionDePrueba());
        return coleccion;
    }

    private static async Task<ConfiguracionTicketDto> TicketAsync(HttpClient http, CajaDto caja) =>
        (await http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{caja.SucursalId}/ticket"))!;

    private static async Task<PantallaSinNavegador> ConfiguracionAsync(ServicioDePrueba servicio, CajaDto caja, SesionDto sesion, PaginaQueContesta pagina)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var pantalla = new PantallaSinNavegador(Servicios(servicio, conexion, new SinImpresora(), pagina).BuildServiceProvider());
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        await pantalla.OprimirAsync(await pantalla.PorIdAsync("cfg-ticket"));
        return pantalla;
    }

    /// <summary>
    /// Solo con el teclado (regla del dueño): «Apagado» y «Prendido» son botones a los que se llega con Tab (ninguno fuera del orden ni
    /// desactivado para quien tiene «Configurar el ticket»); Enter o Espacio eligen (el botón del navegador) y las flechas, Inicio y Fin
    /// pasan al de al lado, lo eligen y le llevan el cursor. Llega apagado; se guarda al tocarlo y queda en la bitácora.
    /// </summary>
    [Fact]
    public async Task El_interruptor_de_Configuracion_Ticket_se_prende_y_se_apaga_solo_con_el_teclado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var pagina = new PaginaQueContesta();
        await using var p = await ConfiguracionAsync(servicio, caja, encargado, pagina);

        var renglon = await p.ElementoAsync(e => e.Clase?.Trim() == "vn-ajuste"
            && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Imprimir lo que cobra el celular", StringComparison.Ordinal), "el renglón del interruptor");
        Assert.Contains("Toda la tienda", renglon.Texto);
        Assert.Contains("sin abrir el cajón", renglon.Texto);
        Assert.Contains("«Ventas del día»", renglon.Texto);
        foreach (var (opcion, elegida) in new[] { ("apagado", "true"), ("prendido", "false") })
        {
            var boton = await p.PorIdAsync($"aj-imprimir-celular-{opcion}");
            Assert.Equal("button", boton.Etiqueta);
            Assert.Equal("radio", boton.Atributos.GetValueOrDefault("role"));
            Assert.False(boton.Desactivado, $"«{opcion}» desactivado");
            Assert.Null(boton.Atributos.GetValueOrDefault("tabindex"));
            Assert.True(boton.Eventos.ContainsKey("onkeydown"), $"«{opcion}» sin flechas");
            Assert.Equal(elegida, boton.Atributos.GetValueOrDefault("aria-checked"));
        }
        Assert.False((await TicketAsync(http, caja)).ImprimirLoDelCelular);

        // → en «Apagado»: «Prendido», guardado, con el cursor en él.
        await p.TeclaAsync("aj-imprimir-celular-apagado", "ArrowRight");
        Assert.True((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        Assert.Equal("true", (await p.PorIdAsync("aj-imprimir-celular-prendido")).Atributos.GetValueOrDefault("aria-checked"));
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && l.Argumentos?[0] as string == "#aj-imprimir-celular-prendido");
        Assert.Contains("Guardado a las", await p.TextoAsync());
        // → en el último no hace nada; ← regresa; Fin e Inicio, a las orillas.
        await p.TeclaAsync("aj-imprimir-celular-prendido", "ArrowRight");
        Assert.True((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        await p.TeclaAsync("aj-imprimir-celular-prendido", "ArrowLeft");
        Assert.False((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && l.Argumentos?[0] as string == "#aj-imprimir-celular-apagado");
        await p.TeclaAsync("aj-imprimir-celular-apagado", "End");
        Assert.True((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        await p.TeclaAsync("aj-imprimir-celular-prendido", "Home");
        Assert.False((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        // Tab es del navegador: la tecla no elige nada aquí. Enter o Espacio en el botón son su clic.
        await p.TeclaAsync("aj-imprimir-celular-apagado", "Tab");
        Assert.False((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        await p.OprimirAsync(await p.PorIdAsync("aj-imprimir-celular-prendido"));
        Assert.True((await TicketAsync(http, caja)).ImprimirLoDelCelular);
        Assert.Equal("true", (await p.PorIdAsync("aj-imprimir-celular-prendido")).Atributos.GetValueOrDefault("aria-checked"));
        // No toca lo demás del ticket.
        Assert.Equal(PapeleriaPos.Nucleo.Configuracion.TicketAlCobrar.Imprimir, (await TicketAsync(http, caja)).AlCobrar);

        // El buscador de ajustes lo encuentra.
        await p.EscribirAsync("cfg-busca", "celular");
        Assert.Contains("Imprimir lo que cobra el celular", await p.TextoAsync());

        await using var db = servicio.Base();
        var cambios = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTicket && b.UsuarioId == encargado.UsuarioId).ToListAsync();
        Assert.Equal(3, cambios.Count(b => b.Detalle == "Prendió «Imprimir lo que cobra el celular»"));
        Assert.Equal(2, cambios.Count(b => b.Detalle == "Apagó «Imprimir lo que cobra el celular»"));
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// La ventana de la caja imprime lo que cobra el celular desde que abre, con la entrada a la vista (nadie ha entrado a vender): dice
    /// qué impresora tiene y lo que le llega va a la cola de su impresora. Al cerrarla deja de preguntar.
    /// </summary>
    [Fact]
    public async Task La_ventana_de_la_caja_imprime_lo_del_celular_desde_que_abre_aunque_nadie_haya_entrado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 50000)));
        await Leer<ConfiguracionTicketDto>(await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/ticket/imprimir-lo-del-celular",
            new SolicitudImprimirLoDelCelular(encargado.UsuarioId, true)));
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        async Task<DondeImprimeDto> DondeAsync() =>
            (await celular.GetFromJsonAsync<List<CajaParaMandarDto>>($"api/cajas/{alta.Caja.Id}/para-mandar"))!.Single(c => c.Id == caja.Id).Impresion!;
        Assert.Equal(CodigosDeImpresion.SinImpresora, (await DondeAsync()).Codigo);

        // Abre la ventana de la caja: la entrada, sin que nadie entre.
        var impresora = new ImpresoraQueGuarda();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var coleccion = Servicios(servicio, conexion, impresora, new PaginaQueContesta());
        coleccion.AddSingleton(new CanalDelCelular(servicio.Fabrica.CreateClient(), conexion, "POS-80C"));
        await using var servicios = coleccion.BuildServiceProvider();
        var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<PantallaCaja>([]);
        await VentaConCopia.EsperarAsync(() => DondeAsync().Result.Puede, "que la caja diga su impresora");
        Assert.Equal((DondeImprimeDto.PorCaja, $"Sale en la impresora de la {caja.Nombre}."), ((await DondeAsync()).Por, (await DondeAsync()).Mensaje));

        // El celular cobra y pide su ticket: sale de la impresora de la caja, y la venta no queda «sin imprimir».
        // Un artículo común: sin depender de qué productos trae la papelería ficticia.
        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/cobrar",
            new SolicitudCobroDelCelular(id, [new RenglonMandado(null, null, null, 1000, "Engargolado", 3550)], [new CobroSolicitado(FormaPago.Efectivo, 5000)],
                Imprimir: true)));
        Assert.Equal((ImpresionDelCelularDto.Impreso, DondeImprimeDto.PorCaja), (cobrado.Impresion!.Estado, cobrado.Impresion.Por));
        Assert.Equal(TicketEscPos.Generar(DisenoTicket.Armar(cobrado.Ticket), false), Assert.Single(impresora.Papeles));
        await using (var db = servicio.Base())
            Assert.False((await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == id)).TicketSinImprimir);
        Assert.Empty(p.Errores);

        // Se cierra la ventana: deja de preguntar y, pasado un momento, el celular ya no puede pedirlo ahí.
        await ((IAsyncDisposable)p).DisposeAsync();
        await Task.Delay(300);
        var preguntas = await DondeAsync();
        Assert.Single(impresora.Papeles);
        Assert.True(preguntas.Puede || preguntas.Codigo == CodigosDeImpresion.VentanaCerrada);
    }
}
