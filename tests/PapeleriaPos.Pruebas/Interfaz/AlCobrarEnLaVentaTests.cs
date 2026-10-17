using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// «Al cobrar» (Configuración › Ticket; docs/propuesta-interfaz.md, «Configuración»): imprimir el ticket (lo de siempre),
/// ofrecerlo o no imprimirlo. El cobro es el mismo con las tres: F12 ya es la confirmación, nada pregunta y nada se espera
/// (dueño, 2026-10-14). Con «Ofrecerlo», «Imprimir ticket» (Ctrl+I) se queda para esa venta hasta el primer escaneo del
/// siguiente cliente; con «No imprimirlo», a la impresora no le llega nada y el ticket se reimprime si lo piden. Lo fiado
/// sale siempre. La impresora de prueba anota lo que le llega; nada depende de lo que tarda la PC.
/// </summary>
public sealed class AlCobrarEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-al-cobrar-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Dueno)> TiendaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        return (http, caja, dueno);
    }

    /// <summary>«Al cobrar» de la sucursal, como lo deja el dueño en la Configuración.</summary>
    private async Task PonerAsync(TicketAlCobrar alCobrar)
    {
        var (http, caja, dueno) = await TiendaAsync();
        var respuesta = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/configuracion/ticket/al-cobrar", new SolicitudAlCobrar(dueno.UsuarioId, alCobrar));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task<string> CobrarAsync(VentaConCopia v, string codigo)
    {
        await v.EscanearAsync(codigo);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto")).WaitAsync(TimeSpan.FromSeconds(20));
        return Folio(await v.Pantalla.TextoAsync());
    }

    private static string Folio(string texto) => Regex.Match(texto, @"Venta (\S+) cobrada").Groups[1].Value;

    /// <summary>Lo que dice el panel del ticket de la venta («Ticket A-000123 sin imprimir», «… · imprimiendo…»).</summary>
    private static async Task<List<string>> DelTicketAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Clase == "vn-folio-ok").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();

    /// <summary>Si se ofrece «Imprimir ticket» (en el panel del cambio o junto a «Último»).</summary>
    private static async Task<bool> SeOfreceAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Imprimir ticket Ctrl+I");

    /// <summary>La tecla, como la manda caja.js desde el cuadro de captura; regresa lo que hizo la pantalla con ella.</summary>
    private static async Task<Task> TeclaAsync(VentaConCopia v, string tecla)
    {
        Task? hecha = null;
        await v.Pantalla.Dispatcher.InvokeAsync(() => hecha = v.Venta.Tecla(tecla, ""));
        return hecha!;
    }

    /// <summary>Lo de siempre: el ticket va a la cola de la impresora al cobrar, y no se ofrece nada.</summary>
    [Fact]
    public async Task Imprimir_el_ticket_es_lo_de_siempre()
    {
        var impresora = new TicketeraDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
        var (a, _) = v.DosDeLaCopia();

        var folio = await CobrarAsync(v, a.Codigo!);

        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket");
        Assert.Contains(folio, impresora.Salieron.Single());
        await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {folio} impreso"), "el ticket impreso en el panel");
        Assert.False(await SeOfreceAsync(v));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Volver al total"));
        Assert.NotNull(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reimprimir", "«Reimprimir»"));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «Ofrecerlo»: se cobra igual, sin esperar a la impresora (aquí detenida) ni preguntarle nada más a la caja principal, y la
    /// caja queda lista para el siguiente cliente; el panel ofrece «Imprimir ticket» (Ctrl+I), también después de volver al
    /// total con Esc. Sale una vez, sin motivo y sin el pulso del cajón. El de la venta siguiente se ofrece hasta el primer
    /// escaneo del siguiente cliente; después ya no, ni con Ctrl+I, aunque se quite lo escaneado.
    /// </summary>
    [Fact]
    public async Task Ofrecerlo_cobra_sin_imprimir_y_lo_ofrece_hasta_el_primer_escaneo()
    {
        await PonerAsync(TicketAlCobrar.Ofrecer);
        try
        {
            var impresora = new TicketeraDePrueba { Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora, fondoAparte: true);
            var (a, b) = v.DosDeLaCopia();
            var cola = ColaDeImpresion.De(impresora);
            var antes = v.Red.Pedidas.Count;

            var folio = await CobrarAsync(v, a.Codigo!);

            var texto = await v.Pantalla.TextoAsync();
            Assert.Contains("Cobrado", texto);
            Assert.Contains($"Ticket {folio} Imprimir ticket Ctrl+I", await DelTicketAsync(v));
            Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
            Assert.Empty(cola.Pendientes);
            Assert.Equal(0, impresora.Intentos);
            // Lo que se hace con el ticket vino con el cobro: ni la configuración ni el ticket se piden aparte.
            Assert.DoesNotContain(v.Red.Pedidas.Skip(antes), r => r.Contains("/ticket", StringComparison.Ordinal) || r.Contains("/configuracion", StringComparison.Ordinal));

            // Esc vuelve al total: se sigue ofreciendo junto a «Último», en lugar de «Reimprimir».
            await TeclaAsync(v, "Escape");
            texto = await v.Pantalla.TextoAsync();
            Assert.DoesNotContain("Cobrado", texto);
            Assert.True(await SeOfreceAsync(v));
            Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reimprimir");

            // Ctrl+I: a la cola, sin esperarla; el panel vuelve a esa venta y dice cómo va. Otra vez ya no hay qué ofrecer.
            var tecla = await TeclaAsync(v, "Ctrl+I");
            Assert.True(tecla.IsCompletedSuccessfully);
            Assert.Equal([$"Ticket {folio}"], cola.Pendientes.Select(t => t.Titulo));
            await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {folio} · imprimiendo…"), "el ticket imprimiéndose en el panel");
            Assert.False(await SeOfreceAsync(v));
            await TeclaAsync(v, "Ctrl+I");
            Assert.Single(cola.Pendientes);
            impresora.Detenida.SetResult();
            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket ofrecido");
            Assert.Contains(folio, impresora.Salieron.Single());
            Assert.False(impresora.Mandados.Single().AsSpan().StartsWith(TicketEscPos.AbrirCajon()));
            await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {folio} impreso"), "el ticket impreso en el panel");

            // La siguiente venta se ofrece hasta que se escanea lo del siguiente cliente.
            var segundo = await CobrarAsync(v, b.Codigo!);
            Assert.Contains($"Ticket {segundo} Imprimir ticket Ctrl+I", await DelTicketAsync(v));
            await v.EscanearAsync(a.Codigo!);
            Assert.False(await SeOfreceAsync(v));
            await TeclaAsync(v, "Ctrl+I");
            await TeclaAsync(v, "Delete");
            Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
            Assert.False(await SeOfreceAsync(v));
            Assert.Empty(cola.Pendientes);
            Assert.Single(impresora.Mandados);
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Si lo que se ofrece se reimprime por «/» (con su motivo, a la bitácora), ya salió: no se sigue ofreciendo, así que no
    /// queda una copia más sin motivo.
    /// </summary>
    [Fact]
    public async Task Ofrecido_y_reimpreso_con_su_motivo_ya_no_se_ofrece()
    {
        await PonerAsync(TicketAlCobrar.Ofrecer);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();
            var folio = await CobrarAsync(v, a.Codigo!);
            Assert.True(await SeOfreceAsync(v));

            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("/reimprimir"));
            await v.Pantalla.EscribirAsync("motivo", "El cliente quiere dos");
            await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("motivo"), "onkeydown", new KeyboardEventArgs { Key = "Enter" });

            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "la reimpresión");
            Assert.Contains(folio, impresora.Salieron.Single());
            Assert.False(await SeOfreceAsync(v));
            await TeclaAsync(v, "Ctrl+I");
            Assert.Empty(ColaDeImpresion.De(impresora).Pendientes);
            Assert.Single(impresora.Mandados);
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// «No imprimirlo»: a la impresora no le llega nada (ni el pulso del cajón) y no se ofrece nada; el panel dice que el ticket
    /// quedó sin imprimir. Si el cliente lo pide, se reimprime con su motivo, como toda reimpresión (también desde «Ventas del
    /// día»).
    /// </summary>
    [Fact]
    public async Task No_imprimirlo_no_le_manda_nada_a_la_impresora_y_se_reimprime_si_lo_piden()
    {
        await PonerAsync(TicketAlCobrar.NoImprimir);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var (a, _) = v.DosDeLaCopia();

            var folio = await CobrarAsync(v, a.Codigo!);

            Assert.Contains($"Ticket {folio} sin imprimir", await DelTicketAsync(v));
            Assert.False(await SeOfreceAsync(v));
            Assert.True((await TeclaAsync(v, "Ctrl+I")).IsCompletedSuccessfully);
            Assert.Empty(ColaDeImpresion.De(impresora).Pendientes);
            Assert.Equal(0, impresora.Intentos);

            await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Volver al total"));
            await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reimprimir", "«Reimprimir»"));
            await v.Pantalla.EscribirAsync("motivo", "El cliente lo pidió");
            await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("motivo"), "onkeydown", new KeyboardEventArgs { Key = "Enter" });
            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "la reimpresión");
            Assert.Contains(folio, impresora.Salieron.Single());
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Lo fiado sale aunque «Al cobrar» diga «No imprimirlo»: Ctrl+L, el cliente, «Crédito» y cobrar, y el ticket con «SU CUENTA»
    /// va a la impresora como siempre.
    /// </summary>
    [Fact]
    public async Task Lo_fiado_se_imprime_aunque_no_se_imprima_al_cobrar()
    {
        await PonerAsync(TicketAlCobrar.NoImprimir);
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            var encargado = await v.Cliente.EntrarAsync("encargado", "2222");
            var dueno = await v.Cliente.EntrarAsync("dueno", "3333");
            var registrado = await v.Cliente.GuardarClienteAsync(new SolicitudGuardarCliente(encargado.UsuarioId, v.Caja.SucursalId,
                new ClienteRegistradoDto(Guid.Empty, "Al cobrar: Arq. Fiado", 0, null, true)));
            await v.Cliente.GuardarCondicionesAsync(registrado.Id, new SolicitudCreditoCliente(dueno.UsuarioId, v.Caja.SucursalId, 100000, 30));
            var (a, _) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);

            await TeclaAsync(v, "Ctrl+L");
            await v.Pantalla.EscribirAsync((await v.Pantalla.EsperarElementoAsync("buscar-cliente")).Id!, "Arq. Fiado");
            await v.Pantalla.OprimirAsync(await v.Pantalla.EsperarElementoAsync("cliente-0"));
            await v.Pantalla.EsperarAsync(t => t.Contains("\nCrédito"), "«Crédito» junto a «Pagó con»");
            await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Crédito"));
            await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("cobrar-nuevo"));
            await v.Pantalla.EsperarAsync(t => Folio(t).Length > 0, "el cobro");
            var folio = Folio(await v.Pantalla.TextoAsync());

            await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el ticket de lo fiado");
            Assert.Contains(folio, impresora.Salieron.Single());
            Assert.Contains("SU CUENTA", impresora.Salieron.Single());
            await v.Pantalla.EsperarAsync(t => t.Contains($"Ticket {folio} impreso"), "el ticket impreso en el panel");
            Assert.DoesNotContain(await DelTicketAsync(v), t => t.Contains("sin imprimir"));
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Sin conexión, la caja hace lo que dice su copia, que se pone al día con lo que cambió: el dueño cambia «Al cobrar» con la
    /// caja ya abierta, el latido lo trae, y el cobro con la copia ya no le manda nada a la impresora.
    /// </summary>
    [Fact]
    public async Task Sin_conexion_hace_lo_que_dice_la_copia_de_la_caja()
    {
        try
        {
            var impresora = new TicketeraDePrueba();
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, impresora: impresora);
            Assert.Equal(TicketAlCobrar.Imprimir, v.Contingencia.Paquete!.AlCobrar);
            await PonerAsync(TicketAlCobrar.NoImprimir);
            await servicio.CatalogoAlDiaAsync();
            await v.Contingencia.LatidoAsync();
            Assert.Equal(TicketAlCobrar.NoImprimir, v.Contingencia.Paquete!.AlCobrar);
            var (a, _) = v.DosDeLaCopia();
            v.Red.Caida = true;
            await v.Contingencia.LatidoAsync();
            Assert.False(v.Contingencia.EnLinea);

            var folio = await CobrarAsync(v, a.Codigo!);

            Assert.Contains($"Ticket {folio} sin imprimir", await DelTicketAsync(v));
            Assert.Equal(1, v.Contingencia.Pendientes);
            Assert.Equal(0, impresora.Intentos);
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Sin impresora (la prueba con una persona real), «Ofrecerlo» también ofrece «Imprimir ticket» en lugar de «verlo en
    /// pantalla», y Ctrl+I lo enseña en pantalla.
    /// </summary>
    [Fact]
    public async Task Sin_impresora_ofrecerlo_lo_ensena_en_pantalla_con_Ctrl_I()
    {
        await PonerAsync(TicketAlCobrar.Ofrecer);
        try
        {
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
            var (a, _) = v.DosDeLaCopia();

            var folio = await CobrarAsync(v, a.Codigo!);

            Assert.Contains($"Ticket {folio} Imprimir ticket Ctrl+I", await DelTicketAsync(v));
            Assert.DoesNotContain("verlo en pantalla", await v.Pantalla.TextoAsync());
            await TeclaAsync(v, "Ctrl+I");
            await v.Pantalla.EsperarAsync(t => t.Contains($"Folio {folio}"), "el ticket en pantalla");
            Assert.False(await SeOfreceAsync(v));
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    // ── En la Configuración ─────────────────────────────────────────────────

    private async Task<(PantallaSinNavegador Pantalla, CajaDto Caja, HttpClient Http)> ConfiguracionAsync(string usuario, string pin)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        return (pantalla, caja, http);
    }

    private static async Task<List<(string Texto, bool Elegida)>> OpcionesAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos
            .Where(e => e.Etiqueta == "button" && e.Atributos.GetValueOrDefault("role") as string == "radio" && e.Id?.StartsWith("aj-al-cobrar-", StringComparison.Ordinal) == true)
            .Select(e => (PantallaSinNavegador.Limpio(e.Texto), e.Atributos.GetValueOrDefault("aria-checked") as string == "true"))
            .ToList();

    /// <summary>
    /// En Ticket, «Al cobrar» con sus tres opciones en un segmento (la elegida marcada) y su alcance, «Toda la tienda»: se
    /// guarda al tocar una, sin «Guardar» ni PIN, con la hora; el buscador lo encuentra.
    /// </summary>
    [Fact]
    public async Task En_la_configuracion_se_elige_en_Ticket_y_se_guarda_al_tocarlo()
    {
        var abierta = await ConfiguracionAsync("dueno", "3333");
        var (caja, http) = (abierta.Caja, abierta.Http);
        await using var p = abierta.Pantalla;
        try
        {
            await p.OprimirAsync(await p.PorIdAsync("cfg-ticket"));
            Assert.Equal([("Imprimir el ticket", true), ("Ofrecerlo", false), ("No imprimirlo", false)], await OpcionesAsync(p));
            var renglon = await p.ElementoAsync(e => e.Clase?.Trim() == "vn-ajuste" && PantallaSinNavegador.Limpio(e.Texto).StartsWith("Al cobrar", StringComparison.Ordinal),
                "el renglón «Al cobrar»");
            Assert.Contains("Toda la tienda", renglon.Texto);
            Assert.Contains("Ctrl+I", renglon.Texto);
            Assert.Contains("Lo fiado sale siempre", renglon.Texto);

            await p.OprimirAsync(await p.PorIdAsync("aj-al-cobrar-ofrecer"));

            Assert.Equal([("Imprimir el ticket", false), ("Ofrecerlo", true), ("No imprimirlo", false)], await OpcionesAsync(p));
            Assert.Equal(TicketAlCobrar.Ofrecer, (await http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{caja.SucursalId}/ticket"))!.AlCobrar);
            Assert.Contains("Guardado a las", await p.TextoAsync());

            await p.EscribirAsync("cfg-busca", "ofrecer");
            var hallados = await p.TextoAsync();
            Assert.Contains("1 encontrado", hallados);
            Assert.Contains("Ticket · Nunca pregunta antes de cobrar.", hallados);
            await p.OprimirAsync(await p.PorIdAsync("aj-al-cobrar-noimprimir"));
            Assert.Equal(TicketAlCobrar.NoImprimir, (await http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{caja.SucursalId}/ticket"))!.AlCobrar);
            Assert.Empty(p.Errores);
        }
        finally
        {
            await PonerAsync(TicketAlCobrar.Imprimir);
        }
    }

    /// <summary>
    /// Sin «Configurar el ticket» en «Puede», «Al cobrar» no aparece (aquí no se pide autorización: el grupo se ve o no se ve),
    /// ni lo encuentra el buscador. El servicio tampoco lo deja (<see cref="AlCobrarApiTests"/>).
    /// </summary>
    [Fact]
    public async Task Sin_configurar_el_ticket_no_aparece()
    {
        var antes = await CambiarPermisoAsync("encargado", AccionControlada.ConfigurarTicket, NivelPermiso.NoPermitido);
        try
        {
            await using var p = (await ConfiguracionAsync("encargado", "2222")).Pantalla;

            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cfg-ticket");
            Assert.Empty(await OpcionesAsync(p));
            await p.EscribirAsync("cfg-busca", "ofrecer");
            Assert.Contains("Ningún ajuste se llama así.", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }
        finally
        {
            await CambiarPermisoAsync("encargado", AccionControlada.ConfigurarTicket, antes);
        }
    }

    /// <summary>Pone el nivel de un permiso de un usuario de prueba, como en «Usuarios y permisos»; regresa el que tenía.</summary>
    private async Task<NivelPermiso> CambiarPermisoAsync(string usuario, AccionControlada accion, NivelPermiso nivel)
    {
        await using var db = servicio.Base();
        var id = (await db.Usuarios.SingleAsync(u => u.NombreUsuario == usuario)).Id;
        var permiso = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == id && p.Accion == accion);
        var antes = permiso.Nivel;
        permiso.Nivel = nivel;
        await db.SaveChangesAsync();
        return antes;
    }
}
