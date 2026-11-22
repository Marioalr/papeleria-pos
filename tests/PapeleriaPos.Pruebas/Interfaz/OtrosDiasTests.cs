using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;
using CajaDeLaBase = PapeleriaPos.Nucleo.Turnos.Caja;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las ventas de otros días y el historial de cortes en la caja (paridad con eleventa, 2026-10-17), sin navegador, contra el
/// servicio con datos ficticios y la Caja 2: «Ventas del día» (como la devolución desde el 2026-11-22) se maneja solo con el
/// teclado: ↑ ↓ eligen el ticket, Enter lo abre con su detalle, Esc regresa a la lista, ← → y Alt+H cambian de día, Alt+C de
/// caja, Alt+U de cajero; en el detalle, Alt+I reimprime (con motivo) y Alt+D abre la devolución con el folio. Quien no ve
/// los reportes (el cajero) ve solo sus tickets, de cualquier día y caja. El historial de cortes va en el corte y «/» lo abre ahí (al cajero no se lo ofrece): cambia de día, elige el
/// turno y reimprime el corte Z como al cerrar el turno. Sin la caja principal, los dos lo dicen. Cada prueba con su propia
/// base, y la caja se cierra antes de borrarla.
/// </summary>
public sealed class OtrosDiasTests
{
    /// <summary>
    /// La caja de la prueba (la Caja 1), con la Caja 2 al lado y los usuarios de prueba. Se cierra antes de que el servicio
    /// borre su base: lo que la pantalla tenía en camino (el chip de las impresiones, que se pregunta solo) llega antes, y
    /// nada nuevo sale de ella.
    /// </summary>
    private sealed record Caja(
        ConexionCaja Conexion, ClienteCaja Cliente, CajaDto Datos, CajaDto Caja2, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado,
        SesionDto Dueno, EnCamino Red) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Red.CerrarAsync();
            await Servicios.DisposeAsync();
        }
    }

    /// <summary>
    /// Entre la caja y el servicio de la prueba: cuenta lo que va en camino y, ya cerrada la caja, no deja salir nada más
    /// (como sin conexión). Cerrar espera, con límite, a que llegue lo que ya iba.
    /// </summary>
    private sealed class EnCamino(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        private int _enCamino;
        private volatile bool _cerrada;

        public async Task CerrarAsync()
        {
            _cerrada = true;
            var limite = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref _enCamino) > 0 && DateTime.UtcNow < limite)
                await Task.Delay(20);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _enCamino);
            try
            {
                if (_cerrada)
                    throw new HttpRequestException("La caja de la prueba ya se cerró.");
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _enCamino);
            }
        }
    }

    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio, IImpresora? impresora = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var red = new EnCamino(servicio.Fabrica.Server.CreateHandler());
        var cliente = new ClienteCaja(new HttpClient(red), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        await using (var db = servicio.Base())
        {
            db.Cajas.Add(new CajaDeLaBase { SucursalId = caja.SucursalId, Nombre = "Caja 2", Serie = "B" });
            await db.SaveChangesAsync();
        }
        var caja2 = (await cliente.CajasAsync()).Single(c => c.Serie == "B");
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(conexion, cliente, caja, caja2, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"), red);
    }

    /// <summary>Un artículo común por ese precio, cobrado en efectivo (o como se diga) en el turno de quien lo abrió.</summary>
    private static Task<TicketDto> VenderAsync(Caja c, CajaDto caja, TurnoDto turno, long precio, FormaPago forma = FormaPago.Efectivo) =>
        c.Cliente.CobrarAsync(new SolicitudCobro(caja.Id, turno.Id, turno.CajeroId, [new LineaSolicitada(null, null, 1000, "Recuerdo", precio)],
            [new CobroSolicitado(forma, precio, forma == FormaPago.Efectivo ? null : "Aut 1")]));

    /// <summary>La venta, <paramref name="dias"/> días antes.</summary>
    private static async Task MoverAsync(ServicioDePrueba servicio, TicketDto ticket, int dias)
    {
        await using var db = servicio.Base();
        var pagada = db.Entry((await db.Ventas.FindAsync(ticket.VentaId))!).Property(v => v.PagadaUtc);
        pagada.CurrentValue = pagada.CurrentValue!.Value.AddDays(-dias);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Las ventas de la prueba: en la Caja 1, una de ayer y dos de hoy (efectivo y tarjeta) del cajero; en la Caja 2, una de
    /// hoy del encargado.
    /// </summary>
    private sealed record Ventas(TicketDto DeAyer, TicketDto DeHoy, TicketDto ConTarjeta, TicketDto DeLaCaja2);

    private static async Task<Ventas> VentasAsync(ServicioDePrueba servicio, Caja c)
    {
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var deAyer = await VenderAsync(c, c.Datos, turno, 2500);
        var deHoy = await VenderAsync(c, c.Datos, turno, 1800);
        var conTarjeta = await VenderAsync(c, c.Datos, turno, 1000, FormaPago.Tarjeta);
        var deLaCaja2 = await VenderAsync(c, c.Caja2, await c.Cliente.AbrirTurnoAsync(c.Caja2.Id, c.Encargado.UsuarioId, 50000), 4000);
        await MoverAsync(servicio, deAyer, 1);
        return new Ventas(deAyer, deHoy, conTarjeta, deLaCaja2);
    }

    private static async Task<(PantallaSinNavegador Pantalla, List<Guid> Reimpresas, List<string> Devueltas)> VentasDelDiaAsync(Caja c, SesionDto quien)
    {
        var (p, reimpresas, devueltas, _) = await VentasDelDiaConComponenteAsync(c, quien);
        return (p, reimpresas, devueltas);
    }

    private static async Task<(PantallaSinNavegador Pantalla, List<Guid> Reimpresas, List<string> Devueltas, VentasDelDia Componente)>
        VentasDelDiaConComponenteAsync(Caja c, SesionDto quien)
    {
        var (reimpresas, devueltas) = (new List<Guid>(), new List<string>());
        var p = new PantallaSinNavegador(c.Servicios);
        var componente = await p.AbrirAsync<VentasDelDia>(new()
        {
            [nameof(VentasDelDia.CajaId)] = c.Datos.Id,
            [nameof(VentasDelDia.Sesion)] = quien,
            [nameof(VentasDelDia.AlReimprimir)] = EventCallback.Factory.Create<Guid>(new object(), reimpresas.Add),
            [nameof(VentasDelDia.AlImprimir)] = EventCallback.Factory.Create<Guid>(new object(), reimpresas.Add),
            [nameof(VentasDelDia.AlDevolver)] = EventCallback.Factory.Create<string>(new object(), devueltas.Add),
        });
        return (p, reimpresas, devueltas, componente);
    }

    /// <summary>Las ventas que se ven, en orden (cada renglón lleva el Id de su venta).</summary>
    private static async Task<List<Guid>> TarjetasAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("venta-", StringComparison.Ordinal) == true && e.Id != "venta-detalle")
            .Select(e => Guid.Parse(e.Id!["venta-".Length..])).ToList();

    /// <summary>Espera a que la lista sea esa (lo que se carga después de cambiar de día o de filtro).</summary>
    private static async Task EsperarTarjetasAsync(PantallaSinNavegador p, params TicketDto[] tickets)
    {
        var esperadas = tickets.Select(t => t.VentaId!.Value).ToList();
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!(await TarjetasAsync(p)).SequenceEqual(esperadas))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"La lista no llegó a {string.Join(", ", tickets.Select(t => t.Folio))}:\n{await p.TextoAsync()}");
            await Task.Delay(20);
        }
    }

    private static async Task<string> TarjetaAsync(PantallaSinNavegador p, TicketDto ticket) =>
        PantallaSinNavegador.Limpio((await p.PorIdAsync($"venta-{ticket.VentaId}")).Texto);

    /// <summary>El ticket elegido con ↑ ↓ (el renglón marcado).</summary>
    private static async Task<Guid?> ElegidaAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("venta-", StringComparison.Ordinal) == true && e.Clase?.Contains("seleccionada") == true)
            .Select(e => (Guid?)Guid.Parse(e.Id!["venta-".Length..])).SingleOrDefault();

    /// <summary>Alt y una letra, como la teclea quien atiende (en el folio, o fuera de los campos: «ventas»).</summary>
    private static async Task AltAsync(PantallaSinNavegador p, string id, string letra) =>
        await p.DispararAsync(await p.PorIdAsync(id), "onkeydown", new KeyboardEventArgs { Key = letra, AltKey = true });

    /// <summary>El detalle abierto de ese ticket, ya cargado: su texto.</summary>
    private static async Task<string> DetalleAsync(PantallaSinNavegador p, TicketDto ticket)
    {
        await p.EsperarAsync(t => t.Contains($"Ticket {ticket.Folio}") && !t.Contains("Cargando"), $"el detalle de {ticket.Folio}");
        return PantallaSinNavegador.Limpio((await p.PorIdAsync("venta-detalle")).Texto);
    }

    private static string? Cursor(Caja c) =>
        ((PaginaQueContesta)c.Servicios.GetRequiredService<IJSRuntime>()).Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?[0]?.ToString();

    /// <summary>
    /// El dueño (con «Ver reportes de ventas»), solo con el teclado: abre en hoy de todas las cajas; ↑ ↓ eligen, Enter abre el
    /// detalle (con lo que lleva, cómo se pagó y los botones con su tecla), ↓ en el detalle pasa al siguiente, Alt+I y Alt+D
    /// hacen lo suyo, «Regresar» (el Esc de la pantalla de venta) vuelve a la lista; ← → y Alt+H cambian de día, Alt+C de caja,
    /// Alt+U de cajero, Alt+F lleva a la fecha y Enter ahí regresa al folio; un folio de otro día se busca con Enter y otro Enter
    /// lo abre.
    /// </summary>
    [Fact]
    public async Task Ventas_del_dia_se_maneja_solo_con_el_teclado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var v = await VentasAsync(servicio, c);
        var (p, reimpresas, devueltas, componente) = await VentasDelDiaConComponenteAsync(c, c.Dueno);
        await using var _ = p;

        // Abre en hoy, de todas las cajas y de todos los cajeros: las más nuevas primero, la primera elegida.
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        var hoy = DateOnly.FromDateTime(HoraHoy());
        Assert.Equal(hoy.ToString("yyyy-MM-dd"), (await p.PorIdAsync("ventas-fecha")).Valor);
        Assert.Equal("", (await p.PorIdAsync("ventas-caja")).Valor);
        Assert.Equal(v.DeLaCaja2.VentaId, await ElegidaAsync(p));
        Assert.Equal("#ventas-folio", Cursor(c));
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("3 ventas · $68.00", texto);
        Assert.Contains("Caja 1 (esta)", texto);
        // Las teclas, a la vista.
        Assert.Equal("↑↓ticketEnterabrirlo←→díaAlt+HhoyAlt+FfechaAlt+CcajaAlt+UcajeroEsccerrar",
            Regex.Replace((await p.PorIdAsync("ventas-teclas")).Texto, @"\s+", ""));
        Assert.Matches(@$"^{v.ConTarjeta.Folio} \$10\.00 \d\d:\d\d · Cajero de prueba · Caja 1 1 artículo · Tarjeta$", await TarjetaAsync(p, v.ConTarjeta));
        Assert.Contains("· Encargado de prueba · Caja 2", await TarjetaAsync(p, v.DeLaCaja2));
        Assert.True((await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") as string == "Día siguiente", "el día siguiente")).Desactivado);

        // ↓ ↓ (desde el folio): la de abajo; ya en la última, se queda; ↑ sube.
        await p.TeclaAsync("ventas-folio", "ArrowDown");
        await p.TeclaAsync("ventas-folio", "ArrowDown");
        await p.TeclaAsync("ventas-folio", "ArrowDown");
        Assert.Equal(v.DeHoy.VentaId, await ElegidaAsync(p));
        Assert.Equal(("mostrar", $"#venta-{v.DeHoy.VentaId}"), ((PaginaQueContesta)c.Servicios.GetRequiredService<IJSRuntime>()).Llamadas
            .Where(l => l.Funcion == "mostrar").Select(l => (l.Funcion, l.Argumentos?[0]?.ToString())).Last());
        await p.TeclaAsync("ventas-folio", "ArrowUp");
        Assert.Equal(v.ConTarjeta.VentaId, await ElegidaAsync(p));

        // Enter abre el elegido: lo que lleva, cómo se pagó y sus botones, con el cursor en «Reimprimir».
        await p.TeclaAsync("ventas-folio", "Enter");
        var detalle = await DetalleAsync(p, v.ConTarjeta);
        Assert.Contains("· Cajero de prueba · Caja 1", detalle);
        Assert.Contains("Recuerdo 1 × $10.00 $10.00", detalle);
        Assert.Contains("Total · 1 artículo $10.00 Tarjeta · Aut 1 $10.00", detalle);
        Assert.Contains("Reimprimir Alt+I", detalle);
        Assert.Contains("Devolver o cancelar Alt+D", detalle);
        Assert.Equal("#detalle-imprimir", Cursor(c));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "ventas-folio");
        // Alt+I y Alt+D: la de la pantalla, con su folio de la caja.
        await AltAsync(p, "ventas", "i");
        await AltAsync(p, "ventas", "D");
        Assert.Equal([v.ConTarjeta.VentaId!.Value], reimpresas);
        Assert.Equal([v.ConTarjeta.Folio], devueltas);
        // ↓ en el detalle: el siguiente ticket de la lista.
        await p.TeclaAsync("ventas", "ArrowDown");
        Assert.Contains("Total · 1 artículo $18.00 Efectivo $18.00", await DetalleAsync(p, v.DeHoy));
        // Esc (la pantalla de venta lo pregunta): a la lista, con ese elegido y el cursor en el folio. Ya en la lista, no hay qué regresar.
        Assert.True(await p.Dispatcher.InvokeAsync(componente.Regresar));
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        Assert.Equal(v.DeHoy.VentaId, await ElegidaAsync(p));
        Assert.Equal("#ventas-folio", Cursor(c));
        Assert.False(await p.Dispatcher.InvokeAsync(componente.Regresar));

        // ← con el folio vacío: ayer. Alt+H: hoy otra vez.
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, v.DeAyer);
        Assert.Equal(hoy.AddDays(-1).ToString("yyyy-MM-dd"), (await p.PorIdAsync("ventas-fecha")).Valor);
        Assert.False((await p.PorIdAsync("ventas-hoy")).Desactivado);
        await AltAsync(p, "ventas-folio", "h");
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        // Alt+C: la Caja 1, la Caja 2 y otra vez todas.
        await AltAsync(p, "ventas-folio", "c");
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        Assert.Equal(c.Datos.Id.ToString(), (await p.PorIdAsync("ventas-caja")).Valor);
        await AltAsync(p, "ventas-folio", "c");
        await EsperarTarjetasAsync(p, v.DeLaCaja2);
        await AltAsync(p, "ventas-folio", "c");
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        // Alt+U: el primer cajero (por su nombre), el siguiente y otra vez todos.
        await AltAsync(p, "ventas-folio", "u");
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        await AltAsync(p, "ventas-folio", "u");
        await EsperarTarjetasAsync(p, v.DeLaCaja2);
        await AltAsync(p, "ventas-folio", "u");
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        // Alt+F: el cursor a la fecha; se escribe el día (hace una semana, sin ventas) y Enter regresa al folio.
        await AltAsync(p, "ventas-folio", "f");
        Assert.Equal("#ventas-fecha", Cursor(c));
        await p.DispararAsync(await p.PorIdAsync("ventas-fecha"), "onchange", new ChangeEventArgs { Value = hoy.AddDays(-7).ToString("yyyy-MM-dd") });
        await p.EsperarAsync(t => t.Contains($"No hubo ventas el {hoy.AddDays(-7):dd/MM/yyyy}."), "el día sin ventas");
        await p.TeclaAsync("ventas-fecha", "Enter");
        Assert.Equal("#ventas-folio", Cursor(c));

        // Un folio de otro día: Enter lo busca y otro Enter lo abre; con el folio borrado, Enter regresa al día.
        await p.EscribirAsync("ventas-folio", v.DeAyer.Folio);
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, v.DeAyer);
        Assert.Contains($"Folio «{v.DeAyer.Folio}»: 1 venta · $25.00", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Contains($"{hoy.AddDays(-1):dd/MM/yyyy} ", await TarjetaAsync(p, v.DeAyer));
        await p.TeclaAsync("ventas-folio", "Enter");
        await DetalleAsync(p, v.DeAyer);
        Assert.True(await p.Dispatcher.InvokeAsync(componente.Regresar));
        await p.EscribirAsync("ventas-folio", "");
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains($"No hubo ventas el {hoy.AddDays(-7):dd/MM/yyyy}."), "el día de antes");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// El cajero (sin «Ver reportes de ventas»): solo sus tickets, de cualquier día y caja; sin el filtro de cajero (Alt+U no hace
    /// nada); el folio de otro no sale, y dice dónde se devuelve. El suyo de hace 10 días se reimprime, pero ya no se devuelve.
    /// </summary>
    [Fact]
    public async Task El_cajero_solo_ve_sus_tickets_de_cualquier_dia_y_caja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var v = await VentasAsync(servicio, c);
        var vieja = await VenderAsync(c, c.Datos, (await c.Cliente.TurnoAsync(c.Datos.Id))!, 900);
        await MoverAsync(servicio, vieja, 10);
        var (p, reimpresas, devueltas) = await VentasDelDiaAsync(c, c.Cajero);
        await using var __ = p;

        // Hoy, de todas las cajas: las suyas (la del encargado en la Caja 2 no).
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains("Solo tus tickets", texto);
        Assert.DoesNotContain(elementos, e => e.Id == "ventas-cajero");
        Assert.DoesNotContain("Alt+U", PantallaSinNavegador.Limpio((await p.PorIdAsync("ventas-teclas")).Texto));
        await AltAsync(p, "ventas-folio", "u");
        Assert.Equal([v.ConTarjeta.VentaId!.Value, v.DeHoy.VentaId!.Value], await TarjetasAsync(p));
        // Otro día sí: ← ayer; la Caja 2 (Alt+C dos veces) ese día: ninguno suyo.
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, v.DeAyer);
        await AltAsync(p, "ventas-folio", "c");
        await AltAsync(p, "ventas-folio", "c");
        await p.EsperarAsync(t => t.Contains($"No cobraste ningún ticket el {DateOnly.FromDateTime(HoraHoy()).AddDays(-1):dd/MM/yyyy} en Caja 2."),
            "ninguno suyo en la Caja 2");

        // El folio del encargado (sin letra, con el guion): no sale, y dice dónde se devuelve.
        await p.EscribirAsync("ventas-folio", Folios.Inequivoco(v.DeLaCaja2.Folio));
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains($"No tienes un ticket con el folio «{Folios.Inequivoco(v.DeLaCaja2.Folio)}». Los de otros cajeros los ve quien tiene "
            + "«Ver reportes de ventas»; para devolver uno, escribe su folio en la venta."), "el folio de otro");
        // En línea, sin letra y de un solo consecutivo (2026-11-22): la de ayer fue la primera.
        Assert.Equal("000001", v.DeAyer.Folio);
        Assert.Matches(@"^\d{6}$", v.DeLaCaja2.Folio);
        // Solo el número: el suyo.
        await p.EscribirAsync("ventas-folio", "1");
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, v.DeAyer);
        // El de hace 10 días: Enter lo busca, otro Enter lo abre; se reimprime (Alt+I) y ya no se devuelve (Alt+D no hace nada).
        await p.EscribirAsync("ventas-folio", vieja.Folio);
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, vieja);
        await p.TeclaAsync("ventas-folio", "Enter");
        var detalle = await DetalleAsync(p, vieja);
        Assert.Contains("Reimprimir Alt+I", detalle);
        Assert.DoesNotContain("Devolver o cancelar", detalle);
        await AltAsync(p, "ventas", "d");
        await AltAsync(p, "ventas", "i");
        Assert.Equal([vieja.VentaId!.Value], reimpresas);
        Assert.Empty(devueltas);
        // Un folio que no existe, y lo que no es un folio.
        await p.OprimirAsync(await p.PorIdAsync("detalle-volver"));
        await p.EscribirAsync("ventas-folio", "A-999999");
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains("No tienes un ticket con el folio «A-999999»."), "el folio que no existe");
        await p.EscribirAsync("ventas-folio", "hola");
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains("«hola» no es un folio"), "lo que no es un folio");
        Assert.Empty(p.Errores);
    }

    /// <summary>Un ticket cancelado y uno devuelto en parte lo dicen en la lista y en su detalle (con quién, cuándo y el motivo).</summary>
    [Fact]
    public async Task La_cancelada_y_la_devuelta_lo_dicen_en_la_lista_y_en_su_detalle()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Encargado.UsuarioId, 50000);
        var cancelada = await VenderAsync(c, c.Datos, turno, 1500);
        var devuelta = await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, turno.CajeroId,
            [new LineaSolicitada(null, null, 1000, "Lápiz", 1000), new LineaSolicitada(null, null, 1000, "Goma", 500)], [new CobroSolicitado(FormaPago.Efectivo, 2000)]));
        await c.Cliente.CancelarAsync(cancelada.VentaId!.Value, new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se equivocó"));
        var paraDevolver = (await c.Cliente.VentaPorFolioAsync(c.Datos.SucursalId, devuelta.Folio))!;
        await c.Cliente.DevolverAsync(paraDevolver.Id, new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No le quedó",
            [new LineaDevolucionSolicitada(paraDevolver.Lineas.Single(l => l.Descripcion == "Goma").LineaId, 1000)]));
        var (p, _, devueltas, _) = await VentasDelDiaConComponenteAsync(c, c.Encargado);
        await using var _1 = p;

        await EsperarTarjetasAsync(p, devuelta, cancelada);
        Assert.Contains("Cancelada", await TarjetaAsync(p, cancelada));
        Assert.Contains("cancelada", (await p.PorIdAsync($"venta-{cancelada.VentaId}")).Clase);
        Assert.Contains("Devuelta en parte · $5.00", await TarjetaAsync(p, devuelta));
        Assert.Contains("1 venta · $15.00 · 1 cancelada · devuelto $5.00", PantallaSinNavegador.Limpio(await p.TextoAsync()));

        // La devuelta: Enter. Sale su cambio y la devolución; todavía se devuelve lo que queda (Alt+D).
        await p.TeclaAsync("ventas-folio", "Enter");
        var detalle = await DetalleAsync(p, devuelta);
        Assert.Contains("Goma 1 × $5.00 $5.00", detalle);
        Assert.Contains("Lápiz 1 × $10.00 $10.00", detalle);
        Assert.Contains("Total · 2 artículos $15.00 Efectivo · pagó con $20.00 $15.00", detalle);
        Assert.Contains("Cambio $5.00", detalle);
        Assert.Matches(@"Devolución de \$5\.00 el \d\d/\d\d/\d{4} \d\d:\d\d por Encargado de prueba · salió \$5\.00 en efectivo: «No le quedó»", detalle);
        await AltAsync(p, "ventas", "d");
        Assert.Equal([devuelta.Folio], devueltas);
        // ↓: la cancelada, sin «Devolver o cancelar».
        await p.TeclaAsync("ventas", "ArrowDown");
        detalle = await DetalleAsync(p, cancelada);
        Assert.Contains("Cancelada", detalle);
        Assert.Matches(@"Cancelado el \d\d/\d\d/\d{4} \d\d:\d\d por Encargado de prueba", detalle);
        Assert.DoesNotContain("Devolver o cancelar", detalle);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_la_caja_principal_las_ventas_y_el_historial_dicen_que_esta_caja_solo_tiene_su_copia()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        c.Conexion.SimularSinConexion = true;

        var (ventas, _, _) = await VentasDelDiaAsync(c, c.Dueno);
        await using var _ = ventas;
        await ventas.EsperarAsync(t => t.Contains(ClienteCaja.VentasSinConexion), "el aviso de las ventas");
        var cortes = new PantallaSinNavegador(c.Servicios);
        await using var __ = cortes;
        await cortes.AbrirAsync<HistorialCortes>(new() { [nameof(HistorialCortes.CajaId)] = c.Datos.Id, [nameof(HistorialCortes.Sesion)] = c.Encargado });
        await cortes.EsperarAsync(t => t.Contains(ClienteCaja.CortesSinConexion), "el aviso del historial");

        // Al volver, «Volver a intentar».
        c.Conexion.SimularSinConexion = false;
        await ventas.OprimirAsync(await ventas.BotonAsync("Volver a intentar"));
        await ventas.EsperarAsync(t => t.Contains("No hubo ventas hoy."), "las ventas de hoy");
        await cortes.OprimirAsync(await cortes.BotonAsync("Volver a intentar"));
        await cortes.EsperarAsync(t => t.Contains("Hoy no se ha abierto ningún turno."), "el historial de hoy");
        Assert.Empty(ventas.Errores);
        Assert.Empty(cortes.Errores);
    }

    /// <summary>
    /// Desde la venta, solo con el teclado: «/» abre las ventas del día en el panel; ← ayer, Enter abre el ticket, Esc regresa a la
    /// lista y otro Esc cierra el panel; Alt+I pide el motivo y sale el ticket (sin impresora, en pantalla), y Alt+D abre la
    /// devolución con el folio ya escrito.
    /// </summary>
    [Fact]
    public async Task Desde_la_venta_con_el_teclado_reimprime_con_motivo_y_abre_la_devolucion_con_el_folio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        var deAyer = await VenderAsync(c, c.Datos, turno, 2500);
        await MoverAsync(servicio, deAyer, 1);
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        var venta = await p.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = c.Dueno,
            [nameof(PantallaVenta.Turno)] = turno,
        });
        await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");

        await p.Dispatcher.InvokeAsync(() => venta.Escaneado("/ventas del dia"));
        await p.EsperarAsync(t => t.Contains("No hubo ventas hoy."), "las ventas de hoy");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, deAyer);
        await p.TeclaAsync("ventas-folio", "Enter");
        await DetalleAsync(p, deAyer);
        // Esc: a la lista (el panel sigue); otro Esc lo cierra.
        await p.Dispatcher.InvokeAsync(() => venta.Tecla("Escape", null));
        await EsperarTarjetasAsync(p, deAyer);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "venta-detalle");
        await p.Dispatcher.InvokeAsync(() => venta.Tecla("Escape", null));
        await p.EsperarAsync(t => !t.Contains("Ventas del día"), "el panel cerrado");

        await p.Dispatcher.InvokeAsync(() => venta.Escaneado("/ventas del dia"));
        await p.EsperarAsync(t => t.Contains("No hubo ventas hoy."), "las ventas de hoy");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, deAyer);
        await p.TeclaAsync("ventas-folio", "Enter");
        await DetalleAsync(p, deAyer);
        await AltAsync(p, "ventas", "i");
        await p.EscribirAsync("motivo", "La pidió el cliente");
        await p.TeclaAsync("motivo", "Enter");
        await p.EsperarAsync(t => t.Contains($"Ticket {deAyer.Folio}") && t.Contains("Sin impresora conectada"), "el ticket en pantalla");

        await p.Dispatcher.InvokeAsync(() => venta.Tecla("Escape", null));
        await p.Dispatcher.InvokeAsync(() => venta.Escaneado("/ventas del dia"));
        await p.EsperarAsync(t => t.Contains("No hubo ventas hoy."), "las ventas de hoy");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, deAyer);
        await p.TeclaAsync("ventas-folio", "Enter");
        await DetalleAsync(p, deAyer);
        await AltAsync(p, "ventas", "d");
        await p.EsperarAsync(t => t.Contains("Devoluciones y cancelaciones") && t.Contains("Devolver lo marcado"), "la devolución");
        Assert.Equal(deAyer.Folio, (await p.PorIdAsync("folio")).Valor);

        await using var db = servicio.Base();
        Assert.Contains(db.Bitacora, b => b.Accion == AccionControlada.Reimprimir && b.Detalle.Contains(deAyer.Folio)
            && b.Detalle.Contains("La pidió el cliente"));
        Assert.Empty(p.Errores);

        // El cajero, en la Caja 2: «/» no le ofrece el historial de cortes (su corte es a ciegas), y sus ventas son las suyas.
        var enCaja2 = await c.Cliente.AbrirTurnoAsync(c.Caja2.Id, c.Cajero.UsuarioId, 50000);
        var cajero = new PantallaSinNavegador(c.Servicios);
        await using var __ = cajero;
        var ventaDelCajero = await cajero.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Caja2,
            [nameof(PantallaVenta.Sesion)] = c.Cajero,
            [nameof(PantallaVenta.Turno)] = enCaja2,
        });
        await cajero.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos del cajero");
        await cajero.Dispatcher.InvokeAsync(() => ventaDelCajero.Escaneado("/historial"));
        await cajero.EsperarAsync(t => t.Contains("Ninguna acción se llama «historial»."), "que no hay historial para el cajero");
        await cajero.Dispatcher.InvokeAsync(() => ventaDelCajero.Escaneado("/ventas del dia"));
        await cajero.EsperarAsync(t => t.Contains("Solo tus tickets") && t.Contains("No cobraste ningún ticket hoy."), "las ventas de hoy del cajero");
        Assert.Empty(cajero.Errores);
    }

    /// <summary>
    /// El historial de cortes, desde «/», abre en Reportes (2026-11-20): los turnos del día con lo esperado, lo contado y la
    /// diferencia, ↑ ↓ eligen el turno, ← → cambian de día y «Reimprimir el corte» lo enseña como al cerrar el turno (sin
    /// impresora, en pantalla). El encargado (hace el corte del turno de otro, no ve el del día ni los reportes) solo tiene ahí el
    /// historial.
    /// </summary>
    [Fact]
    public async Task El_historial_de_cortes_se_abre_desde_la_lista_cambia_de_dia_y_reimprime_el_corte()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var delCajero = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, c.Datos, delCajero, 2500);
        await c.Cliente.CorteZAsync(delCajero.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 50000, Pin: "2222"));
        var delEncargado = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Encargado.UsuarioId, 30000);
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        var venta = await p.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = c.Encargado,
            [nameof(PantallaVenta.Turno)] = delEncargado,
        });
        await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");

        await p.Dispatcher.InvokeAsync(() => venta.Escaneado("/historial"));
        await p.EsperarElementoAsync($"turno-{delEncargado.Id}");
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Historial de cortes", texto);
        // Se abre en Reportes (2026-11-20). El encargado (el corte de un turno, sin reportes ni el corte del día) solo tiene ahí el
        // historial: sin «← Reportes», y la pestaña Reportes sale para él.
        Assert.NotNull(await p.PorIdAsync("historial-cortes"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "hc-volver" or "reportes" or "corte-dia");
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reportes");
        // En la tabla: el del cajero con su corte (esperaba $525, contaron $500) y el del encargado, abierto.
        var fila = PantallaSinNavegador.Limpio((await p.PorIdAsync($"turno-{delCajero.Id}")).Texto);
        Assert.Matches(@"^Caja 1 Cajero de prueba \d\d:\d\d–\d\d:\d\d \$525\.00 \$500\.00 −\$25\.00 Encargado de prueba · \d\d:\d\d$", fila);
        Assert.Contains("Turno abierto", PantallaSinNavegador.Limpio((await p.PorIdAsync($"turno-{delEncargado.Id}")).Texto));
        // Elegido, el más reciente: sigue abierto. ↑ elige el del cajero.
        Assert.Contains("on", (await p.PorIdAsync($"turno-{delEncargado.Id}")).Clase);
        Assert.Contains("Su corte sale aquí cuando se cierre el turno.", await p.TextoAsync());
        await p.TeclaAsync("historial-lista", "ArrowUp");
        Assert.Contains("on", (await p.PorIdAsync($"turno-{delCajero.Id}")).Clase);
        var detalle = PantallaSinNavegador.Limpio((await p.PorIdAsync("historial-detalle")).Texto);
        Assert.Contains("Turno de Cajero de prueba · Caja 1", detalle);
        Assert.Contains("Esperado $525.00 Contado $500.00 Faltante · a nombre de Cajero de prueba −$25.00", detalle);
        Assert.Contains("Lo hizo Encargado de prueba el", detalle);

        await p.OprimirAsync(await p.BotonAsync("Reimprimir el corte"));
        var impreso = (await p.ElementoAsync(e => e.Etiqueta == "pre", "el corte en pantalla")).Texto;
        Assert.Contains("CORTE DEL TURNO (Z)", impreso);
        Assert.Contains("Caja 1 · Cajero: Cajero de prueba", impreso);
        Assert.Contains("Hizo el corte: Encargado de prueba", impreso);
        Assert.Contains("Faltante", impreso);

        // ← (con el cursor en la lista): ayer no hubo turnos; → regresa a hoy.
        await p.TeclaAsync("historial", "ArrowLeft");
        await p.EsperarAsync(t => t.Contains($"El {DateOnly.FromDateTime(HoraHoy()).AddDays(-1):dd/MM/yyyy} no hubo turnos."), "el día anterior");
        await p.TeclaAsync("historial", "ArrowRight");
        await p.EsperarElementoAsync($"turno-{delCajero.Id}");
        // Solo los de la Caja 2: ninguno.
        await p.DispararAsync(await p.PorIdAsync("historial-caja"), "onchange", new ChangeEventArgs { Value = c.Caja2.Id.ToString() });
        await p.EsperarAsync(t => t.Contains("Hoy no se ha abierto ningún turno en Caja 2."), "la Caja 2");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// «Reimprimir el corte» con impresora: va a la cola de la impresora de tickets, como el corte Z al cerrar, y sale igual.
    /// El de una caja sin conexión ya se cuadró al subir: el papel ya no dice que se cuadra al volver.
    /// </summary>
    [Fact]
    public async Task El_corte_reimpreso_sale_en_la_impresora_como_el_corte_z()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var impresora = new ImpresoraQueGuarda();
        await using var c = await PrepararAsync(servicio, impresora);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, c.Datos, turno, 2500);
        await c.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(c.Dueno.UsuarioId, 53000, Pin: "3333"));
        await using (var db = servicio.Base())
        {
            // Como si lo hubiera hecho la caja sin conexión (al subir, el servicio lo cuadró).
            (await db.Cortes.SingleAsync(x => x.TurnoId == turno.Id && x.Tipo == TipoCorte.Z)).EnContingencia = true;
            await db.SaveChangesAsync();
        }
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        await p.AbrirAsync<HistorialCortes>(new() { [nameof(HistorialCortes.CajaId)] = c.Datos.Id, [nameof(HistorialCortes.Sesion)] = c.Dueno });
        await p.EsperarElementoAsync($"turno-{turno.Id}");
        Assert.Contains("Cajero de prueba · sin conexión", PantallaSinNavegador.Limpio((await p.PorIdAsync($"turno-{turno.Id}")).Texto));
        Assert.Contains("Se hizo sin conexión con la caja principal", await p.TextoAsync());

        await p.OprimirAsync(await p.BotonAsync("Reimprimir el corte"));
        await p.EsperarAsync(t => t.Contains("Corte del turno de Cajero de prueba impreso"), "el aviso de que salió");

        var papel = Encoding.Latin1.GetString(Assert.Single(impresora.Recibidos));
        Assert.Contains("CORTE DEL TURNO (Z)", papel);
        Assert.Contains("Hizo el corte: Due", papel);
        Assert.Contains("Sobrante", papel);
        Assert.DoesNotContain("se cuadra al volver", papel);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// «Reimprimir el corte» va por la misma cola que el ticket y el corte Z (entrega 5, parte 3): la pantalla no espera a la
    /// impresora; si no sale (se acabó el papel), lo dice junto al corte con «Reintentar» y «No imprimirlo», y con
    /// «Reintentar» sale una sola vez.
    /// </summary>
    [Fact]
    public async Task El_corte_reimpreso_va_por_la_cola_y_si_no_sale_se_reintenta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var sinPapel = true;
        var impresora = new PapeleriaPos.Pruebas.Impresion.TicketeraDePrueba
        {
            Falla = _ => Volatile.Read(ref sinPapel) ? new IOException("Se acabó el papel de la impresora de tickets.") : null,
        };
        await using var c = await PrepararAsync(servicio, impresora);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, c.Datos, turno, 2500);
        await c.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(c.Dueno.UsuarioId, 52500, Pin: "3333"));
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        await p.AbrirAsync<HistorialCortes>(new() { [nameof(HistorialCortes.CajaId)] = c.Datos.Id, [nameof(HistorialCortes.Sesion)] = c.Dueno });
        await p.EsperarElementoAsync($"turno-{turno.Id}");

        await p.OprimirAsync(await p.BotonAsync("Reimprimir el corte"));
        await p.EsperarAsync(t => t.Contains("No salió «Corte del turno de Cajero de prueba»: Se acabó el papel de la impresora de tickets. El corte ya quedó."),
            "el aviso de que no salió");
        Assert.Equal(["Corte del turno de Cajero de prueba"], ColaDeImpresion.De(impresora).Pendientes.Select(t => t.Titulo));
        Assert.Empty(impresora.Salieron);

        Volatile.Write(ref sinPapel, false);
        await p.OprimirAsync(await p.BotonAsync("Reintentar"));
        await p.EsperarAsync(t => t.Contains("Corte del turno de Cajero de prueba impreso"), "el corte impreso");
        Assert.Contains("CORTE DEL TURNO (Z)", Assert.Single(impresora.Salieron));
        Assert.Empty(ColaDeImpresion.De(impresora).Pendientes);
        Assert.Empty(p.Errores);
    }

    /// <summary>Hoy en la tienda, como lo dice el servicio (hora de México).</summary>
    private static DateTime HoraHoy() => HoraMexico.ALocal(DateTime.UtcNow);

    /// <summary>Una impresora que guarda lo que le llega: para ver qué salió en el papel.</summary>
    private sealed class ImpresoraQueGuarda : IImpresora
    {
        private readonly List<byte[]> _recibidos = [];

        public IReadOnlyList<byte[]> Recibidos
        {
            get
            {
                lock (_recibidos)
                    return [.. _recibidos];
            }
        }

        public bool MuestraVistaPrevia => false;

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            lock (_recibidos)
                _recibidos.Add(escPos);
            return Task.CompletedTask;
        }
    }
}
