using System.Net.Http.Json;
using System.Text;
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
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;
using CajaDeLaBase = PapeleriaPos.Nucleo.Turnos.Caja;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las ventas de otros días y el historial de cortes en la caja (paridad con eleventa, 2026-10-17), sin navegador, contra el
/// servicio con datos ficticios y la Caja 2: «Ventas del día» cambia de día con ← → y de caja, filtra por cajero, busca un
/// folio en cualquier día, reimprime con motivo y abre la devolución con el folio; el cajero ve las de hoy de su caja, como
/// antes. El historial de cortes va en el corte y «/» lo abre ahí (al cajero no se lo ofrece): cambia de día, elige el
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
        var (reimpresas, devueltas) = (new List<Guid>(), new List<string>());
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<VentasDelDia>(new()
        {
            [nameof(VentasDelDia.CajaId)] = c.Datos.Id,
            [nameof(VentasDelDia.Sesion)] = quien,
            [nameof(VentasDelDia.AlReimprimir)] = EventCallback.Factory.Create<Guid>(new object(), reimpresas.Add),
            [nameof(VentasDelDia.AlDevolver)] = EventCallback.Factory.Create<string>(new object(), devueltas.Add),
        });
        return (p, reimpresas, devueltas);
    }

    /// <summary>Las ventas que se ven, en orden (cada tarjeta lleva el Id de su venta).</summary>
    private static async Task<List<Guid>> TarjetasAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("venta-", StringComparison.Ordinal) == true)
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

    /// <summary>Un botón de la tarjeta de esa venta (no el de la siguiente).</summary>
    private static async Task<ElementoDibujado?> BotonDeLaVentaAsync(PantallaSinNavegador p, TicketDto ticket, string texto)
    {
        var elementos = (await p.LeerAsync()).Elementos;
        var tarjeta = elementos.FindIndex(e => e.Id == $"venta-{ticket.VentaId}");
        Assert.True(tarjeta >= 0, $"No está la venta {ticket.Folio}:\n{await p.TextoAsync()}");
        return elementos.Skip(tarjeta + 1).TakeWhile(e => e.Id?.StartsWith("venta-", StringComparison.Ordinal) != true)
            .FirstOrDefault(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto).StartsWith(texto, StringComparison.Ordinal));
    }

    private static async Task<string> TarjetaAsync(PantallaSinNavegador p, TicketDto ticket) =>
        PantallaSinNavegador.Limpio((await p.PorIdAsync($"venta-{ticket.VentaId}")).Texto);

    [Fact]
    public async Task Ventas_del_dia_cambia_de_dia_y_de_caja_filtra_por_cajero_busca_un_folio_reimprime_y_abre_la_devolucion()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var v = await VentasAsync(servicio, c);
        var (p, reimpresas, devueltas) = await VentasDelDiaAsync(c, c.Dueno);
        await using var _ = p;

        // Abre en las de hoy de esta caja, de todos los cajeros, como antes: las más nuevas primero.
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        var hoy = DateOnly.FromDateTime(HoraHoy());
        Assert.Equal(hoy.ToString("yyyy-MM-dd"), (await p.PorIdAsync("ventas-fecha")).Valor);
        Assert.Equal(c.Datos.Id.ToString(), (await p.PorIdAsync("ventas-caja")).Valor);
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("2 ventas · $28.00", texto);
        Assert.Contains("Caja 1 (esta)", texto);
        Assert.DoesNotContain(v.DeAyer.Folio, texto);
        Assert.Matches(@$"^{v.ConTarjeta.Folio} \$10\.00 \d\d:\d\d · Cajero de prueba · Caja 1 · Tarjeta", await TarjetaAsync(p, v.ConTarjeta));
        Assert.Contains("· Efectivo", await TarjetaAsync(p, v.DeHoy));
        // Hoy no hay día siguiente.
        Assert.True((await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") as string == "Día siguiente", "el día siguiente")).Desactivado);

        // ← con el folio vacío (el cursor está ahí): ayer. → (fuera de los campos): hoy otra vez.
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, v.DeAyer);
        Assert.Equal(hoy.AddDays(-1).ToString("yyyy-MM-dd"), (await p.PorIdAsync("ventas-fecha")).Valor);
        Assert.NotNull(await p.BotonAsync("Hoy"));
        await p.TeclaAsync("ventas", "ArrowRight");
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        // Otro día, con la fecha: el de hace una semana no tuvo ventas.
        await p.DispararAsync(await p.PorIdAsync("ventas-fecha"), "onchange", new ChangeEventArgs { Value = hoy.AddDays(-7).ToString("yyyy-MM-dd") });
        await p.EsperarAsync(t => t.Contains($"No hubo ventas el {hoy.AddDays(-7):dd/MM/yyyy} en Caja 1."), "el día sin ventas");
        await p.OprimirAsync(await p.BotonAsync("Hoy"));
        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);

        // Todas las cajas: también la de la Caja 2; y solo las del encargado.
        await p.DispararAsync(await p.PorIdAsync("ventas-caja"), "onchange", new ChangeEventArgs { Value = "" });
        await EsperarTarjetasAsync(p, v.DeLaCaja2, v.ConTarjeta, v.DeHoy);
        Assert.Contains("· Encargado de prueba · Caja 2 ·", await TarjetaAsync(p, v.DeLaCaja2));
        await p.DispararAsync(await p.PorIdAsync("ventas-cajero"), "onchange", new ChangeEventArgs { Value = c.Encargado.UsuarioId.ToString() });
        await EsperarTarjetasAsync(p, v.DeLaCaja2);

        // Un folio, de cualquier día: el de ayer, con su fecha. «Ver el día» regresa a la lista.
        await p.EscribirAsync("ventas-folio", v.DeAyer.Folio);
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, v.DeAyer);
        Assert.Contains($"Folio «{v.DeAyer.Folio}»: 1 venta · $25.00", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Contains($"{hoy.AddDays(-1):dd/MM/yyyy} ", await TarjetaAsync(p, v.DeAyer));
        await p.OprimirAsync(await p.BotonAsync("Ver el día"));
        await EsperarTarjetasAsync(p, v.DeLaCaja2);

        // Reimprimir y «Devolver o cancelar»: la venta de la pantalla, con su folio de la caja.
        await p.OprimirAsync((await BotonDeLaVentaAsync(p, v.DeLaCaja2, "Reimprimir"))!);
        await p.OprimirAsync((await BotonDeLaVentaAsync(p, v.DeLaCaja2, "Devolver o cancelar"))!);
        Assert.Equal([v.DeLaCaja2.VentaId!.Value], reimpresas);
        Assert.Equal([v.DeLaCaja2.Folio], devueltas);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_cajero_ve_las_de_hoy_de_su_caja_sin_cambiar_de_dia_ni_de_caja_y_un_folio_de_cualquiera()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var v = await VentasAsync(servicio, c);
        // Una de hace 10 días (ya no se devuelve) y, en el piloto, el folio que eleventa le dio a la de la Caja 2.
        var vieja = await VenderAsync(c, c.Datos, (await c.Cliente.TurnoAsync(c.Datos.Id))!, 900);
        await MoverAsync(servicio, vieja, 10);
        await using (var db = servicio.Base())
        {
            db.EnviosAEleventa.Add(new EnvioAEleventa
            {
                Tipo = TipoEnvioEleventa.Venta, VentaId = v.DeLaCaja2.VentaId, Estado = EstadoEnvioEleventa.Enviada, CreadoUtc = DateTime.UtcNow, FolioEleventa = 4521,
            });
            await db.SaveChangesAsync();
        }
        var (p, _, devueltas) = await VentasDelDiaAsync(c, c.Cajero);
        await using var __ = p;

        await EsperarTarjetasAsync(p, v.ConTarjeta, v.DeHoy);
        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains("Hoy · Caja 1", texto);
        // Sin «Ver reportes de ventas» no hay otro día ni otra caja (ni sus controles), como antes.
        Assert.DoesNotContain(elementos, e => e.Id is "ventas-fecha" or "ventas-caja");
        Assert.DoesNotContain(elementos, e => e.Atributos.GetValueOrDefault("aria-label") as string == "Día anterior");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await p.TeclaAsync("ventas", "ArrowLeft");
        Assert.Equal([v.ConTarjeta.VentaId!.Value, v.DeHoy.VentaId!.Value], await TarjetasAsync(p));
        // El cajero puede devolver: «Devolver o cancelar»; y reimprimir.
        Assert.NotNull(await BotonDeLaVentaAsync(p, v.DeHoy, "Devolver o cancelar"));
        Assert.NotNull(await BotonDeLaVentaAsync(p, v.DeHoy, "Reimprimir"));

        // Un folio sí, de cualquier día y caja: lo mismo que escribirlo para devolver. La de la Caja 2 sale con el folio de
        // eleventa (el del ticket) y el de la caja en chico, con su espacio; la devolución se abre con el de la caja.
        await p.EscribirAsync("ventas-folio", "4521");
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, v.DeLaCaja2);
        var deLaCaja2 = (await p.PorIdAsync($"venta-{v.DeLaCaja2.VentaId}")).Texto;
        Assert.StartsWith("4521", PantallaSinNavegador.Limpio(deLaCaja2));
        // El espacio antes de «· en la caja» va dentro del texto (Razor quita el que queda al principio de una etiqueta).
        Assert.Contains($"· Efectivo · en la caja \n{v.DeLaCaja2.Folio}", deLaCaja2);
        await p.OprimirAsync((await BotonDeLaVentaAsync(p, v.DeLaCaja2, "Devolver o cancelar"))!);
        Assert.Equal([v.DeLaCaja2.Folio], devueltas);
        // La de hace 10 días: se reimprime, pero ya no se devuelve.
        await p.EscribirAsync("ventas-folio", vieja.Folio);
        await p.TeclaAsync("ventas-folio", "Enter");
        await EsperarTarjetasAsync(p, vieja);
        Assert.NotNull(await BotonDeLaVentaAsync(p, vieja, "Reimprimir"));
        Assert.Null(await BotonDeLaVentaAsync(p, vieja, "Devolver o cancelar"));
        // Un folio que no existe, y lo que no es un folio.
        await p.EscribirAsync("ventas-folio", "A-999999");
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains("No hay un ticket con el folio «A-999999»."), "el folio que no existe");
        await p.EscribirAsync("ventas-folio", "hola");
        await p.TeclaAsync("ventas-folio", "Enter");
        await p.EsperarAsync(t => t.Contains("«hola» no es un folio"), "lo que no es un folio");
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
        await ventas.EsperarAsync(t => t.Contains("Esta caja no ha vendido hoy."), "las ventas de hoy");
        await cortes.OprimirAsync(await cortes.BotonAsync("Volver a intentar"));
        await cortes.EsperarAsync(t => t.Contains("Hoy no se ha abierto ningún turno."), "el historial de hoy");
        Assert.Empty(ventas.Errores);
        Assert.Empty(cortes.Errores);
    }

    /// <summary>
    /// Desde la venta: «/» abre las ventas del día en el panel; reimprimir pide el motivo y sale el ticket (sin impresora, en
    /// pantalla), y «Devolver o cancelar» abre la devolución con el folio ya escrito.
    /// </summary>
    [Fact]
    public async Task Desde_la_venta_reimprime_con_motivo_y_abre_la_devolucion_con_el_folio()
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
        await p.EsperarAsync(t => t.Contains("Esta caja no ha vendido hoy."), "las ventas de hoy");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, deAyer);
        await p.OprimirAsync((await BotonDeLaVentaAsync(p, deAyer, "Reimprimir"))!);
        await p.EscribirAsync("motivo", "La pidió el cliente");
        await p.TeclaAsync("motivo", "Enter");
        await p.EsperarAsync(t => t.Contains($"Ticket {deAyer.Folio}") && t.Contains("Sin impresora conectada"), "el ticket en pantalla");

        await p.Dispatcher.InvokeAsync(() => venta.Tecla("Escape", null));
        await p.Dispatcher.InvokeAsync(() => venta.Escaneado("/ventas del dia"));
        await p.EsperarAsync(t => t.Contains("Esta caja no ha vendido hoy."), "las ventas de hoy");
        await p.TeclaAsync("ventas-folio", "ArrowLeft");
        await EsperarTarjetasAsync(p, deAyer);
        await p.OprimirAsync((await BotonDeLaVentaAsync(p, deAyer, "Devolver o cancelar"))!);
        await p.EsperarAsync(t => t.Contains("Devoluciones y cancelaciones") && t.Contains("Devolver lo marcado"), "la devolución");
        Assert.Equal(deAyer.Folio, (await p.PorIdAsync("folio")).Valor);

        await using var db = servicio.Base();
        Assert.Contains(db.Bitacora, b => b.Accion == AccionControlada.Reimprimir && b.Detalle.Contains(deAyer.Folio)
            && b.Detalle.Contains("La pidió el cliente"));
        Assert.Empty(p.Errores);

        // El cajero, en la Caja 2: «/» no le ofrece el historial de cortes (su corte es a ciegas), y sus ventas son las de hoy
        // de su caja.
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
        await cajero.EsperarAsync(t => t.Contains("Hoy · Caja 2") && t.Contains("Esta caja no ha vendido hoy."), "las ventas de hoy del cajero");
        Assert.Empty(cajero.Errores);
    }

    /// <summary>
    /// El historial de cortes, desde «/», abre en el corte: los turnos del día con lo esperado, lo contado y la diferencia,
    /// ↑ ↓ eligen el turno, ← → cambian de día y «Reimprimir el corte» lo enseña como al cerrar el turno (sin impresora, en
    /// pantalla). Al encargado (hace el corte, no ve el del día) le sale «Turno | Historial».
    /// </summary>
    [Fact]
    public async Task El_historial_de_cortes_se_abre_desde_la_lista_cambia_de_dia_y_reimprime_el_corte()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var delCajero = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, c.Datos, delCajero, 2500);
        await c.Cliente.CorteZAsync(delCajero.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 50000));
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
        var segmento = (await p.LeerAsync()).Elementos.Where(e => e.Atributos.GetValueOrDefault("role") as string == "tab")
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Equal(["Turno", "Historial"], segmento);
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
    /// «Reimprimir el corte» con impresora: sale en la impresora de tickets igual que el corte Z al cerrar. El de una caja sin
    /// conexión ya se cuadró al subir: el papel ya no dice que se cuadra al volver.
    /// </summary>
    [Fact]
    public async Task El_corte_reimpreso_sale_en_la_impresora_como_el_corte_z()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var impresora = new ImpresoraQueGuarda();
        await using var c = await PrepararAsync(servicio, impresora);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, c.Datos, turno, 2500);
        await c.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(c.Dueno.UsuarioId, 53000));
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
        await p.EsperarAsync(t => t.Contains("Salió el corte del turno de Cajero de prueba."), "el aviso de que salió");

        var papel = Encoding.Latin1.GetString(Assert.Single(impresora.Recibidos));
        Assert.Contains("CORTE DEL TURNO (Z)", papel);
        Assert.Contains("Hizo el corte: Due", papel);
        Assert.Contains("Sobrante", papel);
        Assert.DoesNotContain("se cuadra al volver", papel);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// En el corte, la pestaña «Historial» la ve quien hace el corte de un turno o ve el del día; el cajero no, ni aunque se
    /// le pida abrir ahí (abre en el turno, como siempre).
    /// </summary>
    [Fact]
    public async Task En_el_corte_el_historial_lo_ve_quien_hace_o_ve_los_cortes()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);

        // El corte de quien entró, cuando ya dice lo que se espera: sus pestañas y su título.
        async Task<(List<string> Pestanas, string Titulo)> CorteAsync(SesionDto quien, Func<string, bool> listo)
        {
            var p = new PantallaSinNavegador(c.Servicios);
            await using var _ = p;
            await p.AbrirAsync<CorteCaja>(new()
            {
                [nameof(CorteCaja.Turno)] = turno, [nameof(CorteCaja.Sesion)] = quien, [nameof(CorteCaja.Caja)] = "Caja 1",
                [nameof(CorteCaja.Historial)] = true,
            });
            await p.EsperarAsync(listo, "el corte");
            var elementos = (await p.LeerAsync()).Elementos;
            Assert.Empty(p.Errores);
            return (elementos.Where(e => e.Atributos.GetValueOrDefault("role") as string == "tab").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList(),
                PantallaSinNavegador.Limpio(elementos.First(e => e.Etiqueta == "h2").Texto));
        }

        // El cajero: el corte X, sin pestañas.
        var cajero = await CorteAsync(c.Cajero, t => t.Contains("El corte lo hace quien tiene ese permiso"));
        Assert.Empty(cajero.Pestanas);
        Assert.Equal("Corte del turno", cajero.Titulo);
        // El dueño (hace el corte y ve el del día): las tres, en el historial.
        var dueno = await CorteAsync(c.Dueno, t => t.Contains("Del día · todas las cajas") && t.Contains("Turno abierto"));
        Assert.Equal(["Turno", "Del día · todas las cajas", "Historial"], dueno.Pestanas);
        Assert.Equal("Historial de cortes", dueno.Titulo);
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
