using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Pruebas.Interfaz;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Devoluciones y cancelaciones de ida y vuelta con eleventa (decisión del dueño, 2026-10-12; docs/migracion-eleventa.md,
/// «Devoluciones de ida y vuelta»): en la caja 2 se devuelve un ticket cobrado en eleventa (leído en vivo; el dinero sale de
/// su turno y la devolución va a eleventa por la bandeja), y lo que se devuelve en eleventa de un ticket de la caja 2 llega
/// a su venta con la lectura, sin efectivo y sin volver a la bandeja.
/// </summary>
public sealed class DevolucionesIdaYVueltaTests
{
    private static SolicitudDevolucion Devolver(Caja c, string motivo, params LineaDevolucionSolicitada[] lineas) =>
        new(c.Sesion.UsuarioId, c.Datos.Id, motivo, lineas);

    /// <summary>La principal cobra en eleventa (como lo hace eleventa): plumas y un común de $15.50.</summary>
    private static async Task<VentaEnEleventa> CobroDeLaPrincipalAsync(EleventaDePrueba e, int plumas)
    {
        var cobrada = HoraLocal(30);
        return await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(1, 7), new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100,
            new TurnoParaEleventa(HoraLocal(90), 500), [new ArticuloParaEleventa(10, "Pluma", plumas, plumas * 9), new ArticuloParaEleventa(null, "Recuerdo", 1, 15.5m)]));
    }

    [FactConFirebird]
    public async Task Un_ticket_de_la_principal_se_devuelve_en_la_caja_2_sale_de_su_turno_y_queda_por_revisar_hasta_ver_como_lo_anota_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        var principal = await CobroDeLaPrincipalAsync(e, plumas: 3);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        // Se escribe el folio impreso: es de otra caja de eleventa, y se puede devolver aquí.
        var folio = await Leer<FolioEleventaDto>(await c.Http.GetAsync($"api/eleventa/folio/{principal.Folio}"));
        var deEleventa = Assert.Single(folio.Tickets);
        Assert.Equal(("Principal", (string?)null, principal.TicketId, (string?)null, 4250L),
            (deEleventa.Caja, deEleventa.FolioCaja, deEleventa.TicketEleventa, deEleventa.Nota, deEleventa.TotalCentavos));
        var ticket = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{principal.TicketId}"));
        Assert.Equal($"{principal.Folio} · Principal (eleventa)", ticket.Folio);
        Assert.Equal([("Pluma punto fino", 3000L, 0L, 2700L, true), ("Recuerdo", 1000L, 0L, 1550L, false)],
            ticket.Lineas.Select(l => (l.Descripcion, l.CantidadMilesimas, l.CantidadDevueltaMilesimas, l.ImporteCentavos, l.LlevaInventario)));
        Assert.Equal((false, 7, (string?)null), (ticket.PuedeCancelar, ticket.DiasParaDevolver, ticket.NoSeDevuelveEnElPiloto));

        // Dos plumas y el común: $33.50, y en efectivo sale al peso, como cualquier reembolso de la caja nueva.
        var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{principal.TicketId}/devolucion",
            Devolver(c, "No era el color", new LineaDevolucionSolicitada(ticket.Lineas[0].LineaId, 2000), new LineaDevolucionSolicitada(ticket.Lineas[1].LineaId, 1000))));
        var reembolso = CalculoDevolucion.ReembolsoEnEfectivo(new ResumenReembolsos(4250, 0, 0, 0), 3350, 0, completaElTicket: false);
        Assert.Equal(($"{principal.Folio} (eleventa, Principal)", 3350L, reembolso), (comprobante.Folio, comprobante.TotalCentavos, comprobante.ReembolsoEfectivoCentavos));

        // Eleventa todavía no dice cómo anota la devolución de otra caja: queda por revisar, a la vista de la caja 2.
        var envios = await EsperarAsync(() => EnviosAsync(c), x => x.PorRevisar == 1 && x.PorAtender.All(p => p.Estado == EstadoEnvioEleventa.PorRevisar));
        var porRevisar = Assert.Single(envios.PorAtender);
        Assert.Equal((TipoEnvioEleventa.DevolucionDeEleventa, EnvioEleventa.DeOtroTurnoODeOtraCaja, reembolso),
            (porRevisar.Tipo, porRevisar.Error, porRevisar.TotalCentavos));
        var pantalla = await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));
        Assert.Equal($"Devolución del ticket #{principal.Folio} de eleventa (Principal)", Assert.Single(pantalla.PorRevisar).Descripcion);
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));

        await using (var db = servicio.Base())
        {
            // El dinero salió del turno de la caja nueva: una salida de efectivo que no va a eleventa como salida.
            var salida = await db.MovimientosCaja.SingleAsync(m => m.TurnoId == c.Turno.Id);
            Assert.Equal((PapeleriaPos.Nucleo.Turnos.TipoMovimientoCaja.Salida, reembolso), (salida.Tipo, salida.MontoCentavos));
            Assert.StartsWith($"Devolución del ticket #{principal.Folio} de eleventa (Principal): No era el color", salida.Motivo);
            Assert.False(await db.EnviosAEleventa.AnyAsync(x => x.Tipo == TipoEnvioEleventa.MovimientoDeCaja));
            // Las plumas regresan al inventario de aquí; el común no lleva.
            var entrada = await db.MovimientosInventario.SingleAsync(m => m.DocumentoTipo == nameof(DevolucionDeEleventa));
            Assert.Equal((c.Variante, TipoMovimiento.Devolucion, 2000L), (entrada.VarianteId, entrada.Tipo, entrada.Cantidad.Milesimas));
            // El ticket se guardó como se leyó, aparte de las ventas.
            var guardada = await db.Set<DevolucionDeEleventa>().SingleAsync();
            Assert.Equal((principal.TicketId, 2, 2), (guardada.TicketEleventa, guardada.Ticket.Count, guardada.Articulos.Count));
            Assert.False(await db.Ventas.AnyAsync(v => v.TurnoId == c.Turno.Id));
        }

        // Lo devuelto aquí (que todavía no está en eleventa) no se vuelve a devolver.
        var otraVez = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{principal.TicketId}"));
        Assert.Equal(2000, otraVez.Lineas[0].CantidadDevueltaMilesimas);
        Assert.Equal("De «Pluma punto fino» solo quedan 1 por devolver.", await RechazoAsync(await c.Http.PostAsJsonAsync(
            $"api/eleventa/tickets/{principal.TicketId}/devolucion", Devolver(c, "Otra vez", new LineaDevolucionSolicitada(otraVez.Lineas[0].LineaId, 2000)))));
    }

    [FactConFirebird]
    public async Task Un_ticket_del_eleventa_de_la_caja_2_en_el_mismo_turno_se_devuelve_aqui_y_llega_a_eleventa()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        // La cajera cobró en el eleventa de la caja 2, en el turno que adoptó la caja nueva.
        var cobrada = HoraLocal(20);
        var enEleventa = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100, new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloParaEleventa(10, "Pluma", 2, 18)]));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{enEleventa.TicketId}"));

        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{enEleventa.TicketId}/devolucion",
            Devolver(c, "Defecto", new LineaDevolucionSolicitada(ticket.Lineas[0].LineaId, 1000))));
        var envios = await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0 && x.PorAtender.Count == 0);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        // Como la escribe eleventa: la parcial con su renglón, el artículo baja, la existencia sube y el turno lo devuelve.
        var devolucion = (await e.FilasAsync("SELECT * FROM DEVOLUCIONES")).Single();
        Assert.Equal(("P", turno, 9d), ((string)devolucion["TIPO_DEVOLUCION"]!, Convert.ToInt32(devolucion["TURNO_ID"]), Math.Round(Convert.ToDouble(devolucion["TOTAL_DEVUELTO"]), 2)));
        Assert.Equal(1, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", enEleventa.TicketId))), 3);
        Assert.Equal(49, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(9m, (decimal)(await e.ValorAsync("SELECT DEVOLUCIONES_VENTAS_EFECTIVO FROM TURNOS WHERE ID = @t", ("t", turno)))!);
    }

    [FactConFirebird]
    public async Task Lo_que_se_devuelve_en_eleventa_de_un_ticket_de_la_caja_2_llega_a_su_venta_sin_efectivo_ni_bandeja()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var envio = await TicketEnEleventaAsync(servicio, ticket);
        int AjustesDeLaLectura()
        {
            using var db = servicio.Base();
            return db.MovimientosInventario.Count(m => m.VarianteId == c.Variante && m.DocumentoTipo == SincronizacionEleventa.Documento);
        }
        var antes = AjustesDeLaLectura();

        // En eleventa le devuelven una pluma (como lo haría eleventa en la caja 2, en su turno).
        await EnvioEleventa.DevolverAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new DevolucionParaEleventa(envio, false, HoraLocal(0).AddSeconds(-5), new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloDevuelto(10, "Pluma", 1, 9)]));
        await LeerAsync(c);
        await LeerAsync(c);

        await using (var db = servicio.Base())
        {
            var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
            Assert.Equal((EstadoVenta.Pagada, 1000L), (venta.Estado, venta.Lineas.Single().CantidadDevuelta.Milesimas));
            // Una sola vez, sin efectivo (salió de la caja de eleventa) y a nombre de lo que pasó allá.
            var devolucion = await db.Devoluciones.SingleAsync(d => d.VentaId == venta.Id);
            Assert.Equal((false, 900L, 0L), (devolucion.EsCancelacion, devolucion.TotalCentavos, devolucion.ReembolsoEfectivoCentavos));
            Assert.StartsWith("Hecha en eleventa (Caja 2, cajera2", devolucion.Motivo);
            Assert.Single(await db.Set<DevolucionDesdeEleventa>().ToListAsync());
            // No vuelve a eleventa.
            Assert.False(await db.EnviosAEleventa.AnyAsync(x => x.DevolucionId == devolucion.Id));
            // La pluma regresó al inventario con su devolución: la lectura no tuvo que ajustarla.
            Assert.Equal(1000, (await db.MovimientosInventario.SingleAsync(m => m.DocumentoId == devolucion.Id)).Cantidad.Milesimas);
            Assert.True(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.Devolucion && b.Detalle.StartsWith("Devolución hecha en eleventa del ticket")));
        }
        Assert.Equal(antes, AjustesDeLaLectura());
        // Lo que queda se devuelve en eleventa: el reembolso de aquí no sabe de lo que salió allá.
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        Assert.Equal(DevolucionesConEleventa.YaTuvoDevolucionEnEleventa, buscado.NoSeDevuelveEnElPiloto);
        Assert.Equal(DevolucionesConEleventa.YaTuvoDevolucionEnEleventa, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "Otra", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)]))));
    }

    [FactConFirebird]
    public async Task Antes_de_devolver_aqui_se_revisa_en_vivo_que_eleventa_no_lo_haya_cancelado()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        // Eleventa lo canceló; la lectura de cada pocos minutos todavía no pasa.
        await EnvioEleventa.DevolverAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new DevolucionParaEleventa(await TicketEnEleventaAsync(servicio, ticket), true, HoraLocal(0).AddSeconds(-5), new TurnoParaEleventa(HoraLocal(120), 300), []));

        var rechazo = await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));

        Assert.Equal(DevolucionesConEleventa.YaTuvoDevolucionEnEleventa, rechazo);
        await using var db = servicio.Base();
        var venta = await db.Ventas.SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(EstadoVenta.Cancelada, venta.Estado);
        // La cancelación de eleventa entró sin efectivo; ninguna salió de esta caja.
        Assert.Equal(0, (await db.Devoluciones.SingleAsync(d => d.VentaId == venta.Id)).ReembolsoEfectivoCentavos);
    }

    [FactConFirebird]
    public async Task La_devolucion_que_manda_la_caja_2_no_vuelve_como_si_fuera_de_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No era", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)])));
        await MandarAsync(c);

        await LeerAsync(c);

        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));
        await using var db = servicio.Base();
        Assert.Single(await db.Devoluciones.Where(d => d.VentaId == ticket.VentaId).ToListAsync());
        Assert.False(await db.Set<DevolucionDesdeEleventa>().AnyAsync());
    }

    [FactConFirebird]
    public async Task En_la_ventana_de_devoluciones_el_folio_de_otra_caja_abre_su_ticket_de_eleventa_y_se_devuelve_aqui()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        var principal = await CobroDeLaPrincipalAsync(e, plumas: 2);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var alcance = servicios.CreateAsyncScope();
        var pantalla = new PantallaSinNavegador(alcance.ServiceProvider);
        ComprobanteDevolucionDto? comprobante = null;
        await pantalla.AbrirAsync<CascadingValue<bool>>(new()
        {
            ["Name"] = PilotoConEleventa.Cascada, ["Value"] = true,
            ["ChildContent"] = (RenderFragment)(b =>
            {
                b.OpenComponent<DevolverCancelar>(0);
                b.AddAttribute(1, nameof(DevolverCancelar.Caja), c.Datos);
                b.AddAttribute(2, nameof(DevolverCancelar.Sesion), c.Sesion);
                b.AddAttribute(3, nameof(DevolverCancelar.AlTerminar), EventCallback.Factory.Create<ComprobanteDevolucionDto>(new object(), r => comprobante = r));
                b.CloseComponent();
            }),
        });

        // El folio impreso es de la principal: el ticket se lee en eleventa y se ve como uno de esta caja.
        await pantalla.EscribirAsync("folio", principal.Folio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await pantalla.TeclaAsync("folio", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Principal (eleventa)"), "el ticket de la principal");
        Assert.Contains("Pluma punto fino", await pantalla.TextoAsync());
        Assert.DoesNotContain("Cancelar todo el ticket", await pantalla.TextoAsync());

        var cantidad = await pantalla.ElementoAsync(x => x.Clase == "cantidad-devolver", "la cantidad de la pluma");
        await pantalla.DispararAsync(cantidad, "onchange", new ChangeEventArgs { Value = "1" });
        var motivo = await pantalla.ElementoAsync(x => x.Etiqueta == "input" && x.Eventos.ContainsKey("oninput") && x.Id != "folio", "el motivo");
        await pantalla.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "No pinta" });
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Devolver lo marcado"));
        await PilotoDePrueba.EsperarAsync(() => Task.FromResult(comprobante), x => x is not null);

        Assert.Equal(($"{principal.Folio} (eleventa, Principal)", 900L), (comprobante!.Folio, comprobante.TotalCentavos));
        Assert.Empty(pantalla.Errores);
        await using var db = servicio.Base();
        Assert.Equal(principal.TicketId, (await db.Set<DevolucionDeEleventa>().SingleAsync()).TicketEleventa);
    }

    private static async Task<int> TicketEnEleventaAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
    }

    // ── Sin Firebird: la vuelta con filas de eleventa hechas a mano, y lo que se ve en la caja 2 ──

    /// <summary>Una venta de la caja 2 que ya está en eleventa como el ticket 500 (la bandeja la anotó como enviada).</summary>
    private static async Task<TicketDto> EnviadaAsync(ServicioDePrueba servicio, Caja c, int plumas, bool comun = false)
    {
        var ticket = await CobrarAsync(c, plumas, comun);
        await using var db = servicio.Base();
        var envio = await db.EnviosAEleventa.SingleAsync(e => e.VentaId == ticket.VentaId);
        (envio.Estado, envio.FolioEleventa, envio.TicketEleventa, envio.EnviadaUtc) = (EstadoEnvioEleventa.Enviada, 42, 500, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return ticket;
    }

    [Fact]
    public async Task La_vuelta_pasa_la_cancelacion_y_la_parcial_de_eleventa_a_la_venta_una_sola_vez()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 3, comun: true);
        var parcial = new DevolucionLeida(900, 500, false, HoraLocal(5), 24, "cajera2", "Caja 2", 31,
            [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 10), new ArticuloDevueltoLeido("COMUN-1-2", "Recuerdo", 1, 15, null)]);
        var cancelacion = new DevolucionLeida(901, 500, true, HoraLocal(2), 18, "cajera2", "Caja 2", 31, []);

        await using (var db = servicio.Base())
        {
            Assert.Equal(new ResultadoVuelta(2, 0), await DevolucionesConEleventa.AplicarVueltaAsync(db, [parcial, cancelacion], DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        // La lectura siguiente trae las mismas: ya se pasaron.
        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(0, 0), await DevolucionesConEleventa.AplicarVueltaAsync(db, [parcial, cancelacion], DateTime.UtcNow));

        await using var revision = servicio.Base();
        var venta = await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(EstadoVenta.Cancelada, venta.Estado);
        Assert.All(venta.Lineas, l => Assert.Equal(l.Cantidad, l.CantidadDevuelta));
        var devoluciones = await revision.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).OrderBy(d => d.FechaUtc).ToListAsync();
        Assert.Equal([(false, 2), (true, 1)], devoluciones.Select(d => (d.EsCancelacion, d.Lineas.Count)));
        Assert.All(devoluciones, d => Assert.Equal((0L, venta.TurnoId), (d.ReembolsoEfectivoCentavos, d.TurnoId)));
        // Las tres plumas regresaron al inventario (una y luego dos), y nada fue a la bandeja.
        Assert.Equal(3000, (await revision.MovimientosInventario.Where(m => m.DocumentoTipo == nameof(Devolucion) && m.VarianteId == c.Variante).ToListAsync())
            .Sum(m => m.Cantidad.Milesimas));
        Assert.Single(await revision.EnviosAEleventa.ToListAsync());
    }

    [Fact]
    public async Task Lo_que_eleventa_devolvio_y_aqui_ya_no_queda_queda_por_revisar_y_se_ve_en_la_caja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 2);
        // Aquí ya se devolvieron las dos (eleventa no contesta: se deja pasar y va a la bandeja).
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No servían", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 2000)])));

        // Y en eleventa también devolvieron una.
        await using (var db = servicio.Base())
        {
            var resultado = await DevolucionesConEleventa.AplicarVueltaAsync(db,
                [new DevolucionLeida(902, 500, false, HoraLocal(1), 9, "cajera2", "Caja 2", 31, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 10)])],
                DateTime.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(new ResultadoVuelta(0, 1), resultado);
        }

        var pantalla = await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));
        var choque = Assert.Single(pantalla.PorRevisar, p => p.VueltaId is not null);
        Assert.Equal(($"Devolución en eleventa de {ticket.Folio}", DevolucionesConEleventa.NoAlcanza, 900L), (choque.Descripcion, choque.Motivo, choque.TotalCentavos));
        // Lo revisa quien tiene «Configuración de la tienda»; el cajero no.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        Assert.False((await c.Http.PostAsJsonAsync($"api/eleventa/vuelta/{choque.VueltaId}/revisada", new SolicitudRevision(c.Sesion.UsuarioId, null))).IsSuccessStatusCode);
        Assert.True((await c.Http.PostAsJsonAsync($"api/eleventa/vuelta/{choque.VueltaId}/revisada", new SolicitudRevision(dueno.UsuarioId, "La pluma se quedó"))).IsSuccessStatusCode);
        Assert.DoesNotContain((await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"))).PorRevisar, p => p.VueltaId is not null);
        await using var revision = servicio.Base();
        Assert.True(await revision.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.EndsWith("La pluma se quedó")));
    }

    [Fact]
    public async Task La_caja_2_ensena_lo_que_quedo_por_revisar_y_quien_puede_lo_marca_como_anotado_a_mano()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c);
        var pendiente = (await EnviosAsync(c)).PorAtender.Single();
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsJsonAsync($"api/eleventa/envios/{pendiente.Id}/revisar", new SolicitudRevision(dueno.UsuarioId, null)));

        async Task<PantallaSinNavegador> PantallaAsync(SesionDto sesion)
        {
            var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
            var servicios = new ServiceCollection()
                .AddSingleton(new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
                .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
                .AddSingleton<IJSRuntime>(new PaginaQueContesta())
                .BuildServiceProvider();
            var pantalla = new PantallaSinNavegador(servicios);
            await pantalla.AbrirAsync<PuenteEleventa>(new() { [nameof(PuenteEleventa.Caja)] = c.Datos, [nameof(PuenteEleventa.Sesion)] = sesion });
            await pantalla.EsperarAsync(t => t.Contains("por revisar"), "el aviso de eleventa");
            await pantalla.OprimirAsync(await pantalla.BotonAsync("Eleventa"));
            return pantalla;
        }

        // El cajero lo ve, sin poder marcarlo.
        var cajero = await PantallaAsync(c.Sesion);
        var texto = PantallaSinNavegador.Limpio(await cajero.TextoAsync());
        Assert.Contains("Eleventa · 1 por revisar", texto);
        Assert.Contains($"Venta {ticket.Folio}", texto);
        Assert.Contains("Se dejó para revisar a mano.", texto);
        Assert.DoesNotContain("Ya lo anoté en eleventa", texto);

        // El dueño lo marca: ya no se manda ni se enseña.
        var conPermiso = await PantallaAsync(dueno);
        await conPermiso.OprimirAsync(await conPermiso.BotonAsync("Ya lo anoté en eleventa"));
        await conPermiso.EsperarAsync(t => !t.Contains("por revisar"), "que ya no quede nada por revisar");
        await using (var db = servicio.Base())
            Assert.Equal(EstadoEnvioEleventa.AMano, (await db.EnviosAEleventa.SingleAsync(x => x.Id == pendiente.Id)).Estado);
        Assert.Empty(cajero.Errores.Concat(conPermiso.Errores));
    }
}
