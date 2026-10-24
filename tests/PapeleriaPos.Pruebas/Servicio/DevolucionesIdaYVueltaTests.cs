using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
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
    public async Task Un_ticket_de_la_principal_no_se_devuelve_en_la_caja_2_se_devuelve_en_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        var principal = await CobroDeLaPrincipalAsync(e, plumas: 3);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        // Se escribe el folio impreso: es de otra caja de eleventa. Se ve, pero el puente no sabe escribir su devolución.
        var folio = await Leer<FolioEleventaDto>(await c.Http.GetAsync($"api/eleventa/folio/{principal.Folio}"));
        var deEleventa = Assert.Single(folio.Tickets);
        const string DeOtraCaja = "Este ticket es de otra caja de eleventa (Principal): devuélvelo en eleventa (está abierto en esta caja).";
        Assert.Equal(("Principal", (string?)null, principal.TicketId, DeOtraCaja, 4250L),
            (deEleventa.Caja, deEleventa.FolioCaja, deEleventa.TicketEleventa, deEleventa.Nota, deEleventa.TotalCentavos));
        var ticket = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{principal.TicketId}"));
        Assert.Equal($"{principal.Folio} · Principal (eleventa)", ticket.Folio);
        Assert.Equal([("Pluma punto fino", 3000L, 0L, 2700L, true), ("Recuerdo", 1000L, 0L, 1550L, false)],
            ticket.Lineas.Select(l => (l.Descripcion, l.CantidadMilesimas, l.CantidadDevueltaMilesimas, l.ImporteCentavos, l.LlevaInventario)));
        Assert.Equal((false, 7, DeOtraCaja), (ticket.PuedeCancelar, ticket.DiasParaDevolver, ticket.NoSeDevuelveEnElPiloto));

        // Aunque se pida, no se devuelve aquí: ni sale dinero, ni entra al inventario, ni va a la bandeja.
        Assert.Equal(DeOtraCaja, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{principal.TicketId}/devolucion",
            Devolver(c, "No era el color", new LineaDevolucionSolicitada(ticket.Lineas[0].LineaId, 2000)))));
        await using var db = servicio.Base();
        Assert.False(await db.Set<DevolucionDeEleventa>().AnyAsync());
        Assert.False(await db.MovimientosCaja.AnyAsync(m => m.TurnoId == c.Turno.Id));
        Assert.False(await db.MovimientosInventario.AnyAsync(m => m.DocumentoTipo == nameof(DevolucionDeEleventa)));
        Assert.Empty((await EnviosAsync(c)).PorAtender);
    }

    [FactConFirebird]
    public async Task Un_ticket_del_eleventa_de_la_caja_2_en_el_mismo_turno_se_devuelve_aqui_y_llega_a_eleventa()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        // La cajera cobró en el eleventa de la caja 2, en el turno que adoptó la caja nueva: plumas y un común de $15.50.
        var cobrada = HoraLocal(20);
        var enEleventa = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100, new TurnoParaEleventa(HoraLocal(120), 300),
                [new ArticuloParaEleventa(10, "Pluma", 3, 27), new ArticuloParaEleventa(null, "Recuerdo", 1, 15.5m)]));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{enEleventa.TicketId}"));
        Assert.Equal(($"{enEleventa.Folio} · Caja 2 (eleventa)", (string?)null), (ticket.Folio, ticket.NoSeDevuelveEnElPiloto));

        // Una pluma y el común: $24.50. Sale exacto, al centavo, como lo regresaría eleventa (que cobró con centavos).
        var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{enEleventa.TicketId}/devolucion",
            Devolver(c, "Defecto", new LineaDevolucionSolicitada(ticket.Lineas[0].LineaId, 1000), new LineaDevolucionSolicitada(ticket.Lineas[1].LineaId, 1000))));
        const long reembolso = 2450;
        Assert.Equal(($"{enEleventa.Folio} (eleventa, Caja 2)", reembolso, reembolso), (comprobante.Folio, comprobante.TotalCentavos, comprobante.ReembolsoEfectivoCentavos));
        await using (var db = servicio.Base())
        {
            // El dinero salió del turno de la caja nueva: una salida de efectivo que no va a eleventa como salida.
            var salida = await db.MovimientosCaja.SingleAsync(m => m.TurnoId == c.Turno.Id);
            Assert.Equal((PapeleriaPos.Nucleo.Turnos.TipoMovimientoCaja.Salida, reembolso), (salida.Tipo, salida.MontoCentavos));
            Assert.StartsWith($"Devolución del ticket #{enEleventa.Folio} de eleventa (Caja 2): Defecto", salida.Motivo);
            Assert.False(await db.EnviosAEleventa.AnyAsync(x => x.Tipo == TipoEnvioEleventa.MovimientoDeCaja));
            // La pluma regresa al inventario de aquí; el común no lleva.
            var entrada = await db.MovimientosInventario.SingleAsync(m => m.DocumentoTipo == nameof(DevolucionDeEleventa));
            Assert.Equal((c.Variante, TipoMovimiento.Devolucion, 1000L), (entrada.VarianteId, entrada.Tipo, entrada.Cantidad.Milesimas));
            // El ticket se guardó como se leyó, aparte de las ventas.
            var guardada = await db.Set<DevolucionDeEleventa>().SingleAsync();
            Assert.Equal((enEleventa.TicketId, 2, 2), (guardada.TicketEleventa, guardada.Ticket.Count, guardada.Articulos.Count));
            Assert.False(await db.Ventas.AnyAsync(v => v.TurnoId == c.Turno.Id));
        }
        var envios = await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0 && x.PorAtender.Count == 0);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        // Como la escribe eleventa: la parcial con sus renglones, el artículo baja, la existencia sube y el turno lo devuelve.
        var devolucion = (await e.FilasAsync("SELECT * FROM DEVOLUCIONES")).Single();
        Assert.Equal(("P", turno, 24.5d), ((string)devolucion["TIPO_DEVOLUCION"]!, Convert.ToInt32(devolucion["TURNO_ID"]), Math.Round(Convert.ToDouble(devolucion["TOTAL_DEVUELTO"]), 2)));
        Assert.Equal(2, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t AND PRODUCTO_NOMBRE = 'Pluma punto fino'",
            ("t", enEleventa.TicketId))), 3);
        Assert.Equal(48, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        Assert.Equal(24.5m, (decimal)(await e.ValorAsync("SELECT DEVOLUCIONES_VENTAS_EFECTIVO FROM TURNOS WHERE ID = @t", ("t", turno)))!);

        // Lo ya devuelto no se vuelve a devolver.
        var otraVez = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{enEleventa.TicketId}"));
        Assert.Equal(1000, otraVez.Lineas[0].CantidadDevueltaMilesimas);
        Assert.Equal("De «Pluma punto fino» solo quedan 2 por devolver.", await RechazoAsync(await c.Http.PostAsJsonAsync(
            $"api/eleventa/tickets/{enEleventa.TicketId}/devolucion", Devolver(c, "Otra vez", new LineaDevolucionSolicitada(otraVez.Lineas[0].LineaId, 3000)))));
    }

    [FactConFirebird]
    public async Task Lo_de_otro_turno_de_eleventa_no_se_devuelve_en_la_caja_2_se_devuelve_en_eleventa_y_llega_con_la_lectura()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        // Una venta de la caja nueva y otra del eleventa de la caja 2, las dos en el turno 1 de eleventa.
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var cobrada = HoraLocal(10);
        var cobradaAlla = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100, new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloParaEleventa(10, "Pluma", 2, 18)]));

        // Eleventa hace su corte y abre el turno siguiente; la caja nueva toma el corte y el cajero abre otro turno.
        await e.CortarAsync(turno1!.Value, HoraLocal(0).AddSeconds(-3));
        var turno2 = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(0).AddSeconds(-2), fondo: 300);
        await LeerAsync(c);
        await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Sesion.UsuarioId, 30000)));

        // Las dos son de otro turno: la ventana lo dice, no ofrece devolverlas ni cancelarlas, y no se devuelven aquí.
        var deLaCaja = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        Assert.Equal((DevolucionesConEleventa.DeOtroTurno, false), (deLaCaja.NoSeDevuelveEnElPiloto, deLaCaja.PuedeCancelar));
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(deLaCaja.Lineas[0].LineaId, 1000)]))));
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió"))));
        // El del eleventa de la caja 2 no es una venta de aquí: no se dice que llega.
        const string DeOtroTurnoDeEleventa = "Este ticket es de otro turno de eleventa: devuélvelo en eleventa (está abierto en esta caja).";
        var deEleventa = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{cobradaAlla.TicketId}"));
        Assert.Equal(DeOtroTurnoDeEleventa, deEleventa.NoSeDevuelveEnElPiloto);
        Assert.Equal(DeOtroTurnoDeEleventa, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{cobradaAlla.TicketId}/devolucion",
            Devolver(c, "No pinta", new LineaDevolucionSolicitada(deEleventa.Lineas[0].LineaId, 1000)))));

        // Se devuelve en eleventa (con su hora, sin la marca de la caja 2) y llega sola con la lectura: sin efectivo de esta caja.
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES (TURNO_ID, TICKET_ID, TIPO_DEVOLUCION, PAGADO_EN, DEVUELTO_EN, CAJERO, CAJA, TOTAL_DEVUELTO)
            VALUES (@turno, @ticket, 'P', @pagado, @devuelto, 'cajera2', 'Caja 2', 9)
            """, ("turno", turno2), ("ticket", ticketEleventa), ("pagado", HoraLocal(5)), ("devuelto", HoraLocal(0)));
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES_ARTICULOS (DEVOLUCION_ID, TICKET_ID, CODIGO_PRODUCTO, DESCRIPCION_PRODUCTO, CANTIDAD_DEVUELTA, DINERO_DEVUELTO, INVENTARIO_POST_DEVOLUCION)
            VALUES (@dev, @ticket, 'PLU-FINA', 'Pluma punto fino', 1, 9, 0)
            """, ("dev", Convert.ToInt32(await e.ValorAsync("SELECT MAX(ID) FROM DEVOLUCIONES"))), ("ticket", ticketEleventa));
        await LeerAsync(c);

        await using var db = servicio.Base();
        var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(1000L, venta.Lineas.Single().CantidadDevuelta.Milesimas);
        var devolucion = await db.Devoluciones.SingleAsync(d => d.VentaId == venta.Id);
        Assert.Equal(0L, devolucion.ReembolsoEfectivoCentavos);
        Assert.StartsWith("Hecha en eleventa (Caja 2, cajera2", devolucion.Motivo);
        Assert.Empty((await EnviosAsync(c)).PorAtender);
    }

    [FactConFirebird]
    public async Task El_turno_de_una_venta_de_la_caja_2_lo_dice_eleventa_en_vivo_aunque_la_lectura_no_haya_visto_su_corte()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var antes = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);

        // Eleventa hace su corte y abre el turno siguiente; la lectura todavía no lo ve, y la caja cobra otra venta, que ya
        // entra al turno nuevo de eleventa.
        await e.CortarAsync(turno1!.Value, HoraLocal(0).AddSeconds(-3));
        await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(0).AddSeconds(-2), fondo: 300);
        var despues = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);

        // La de antes ya es de otro turno de eleventa, aunque aquí su turno siga abierto: no se devuelve aquí (no quedaría
        // por revisar para anotarla a mano).
        var deAntes = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{antes.Folio}"));
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, deAntes.NoSeDevuelveEnElPiloto);
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{antes.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(deAntes.Lineas[0].LineaId, 1000)]))));

        // La caja toma el corte (su turno se cierra) y el cajero abre otro. La de después está en el turno de eleventa de
        // ahora: se devuelve aquí, y llega a eleventa.
        await LeerAsync(c);
        await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Sesion.UsuarioId, 30000)));
        var deDespues = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{despues.Folio}"));
        Assert.Null(deDespues.NoSeDevuelveEnElPiloto);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{despues.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(deDespues.Lineas[0].LineaId, 1000)])));
        var envios = await MandarAsync(c);
        Assert.Empty(envios.PorAtender);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES WHERE TICKET_ID = @t", ("t", await TicketEnEleventaAsync(servicio, despues)))));
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

    /// <summary>
    /// En eleventa le devuelven una pluma a un ticket de la caja 2 y después cancelan lo que quedó (eleventa cancela lo que quedó
    /// en el ticket). La lectura trae las dos: aquí la cancelación es la devolución de las otras dos plumas y la venta sigue
    /// cobrada, todo devuelto: el corte del día y los reportes no restan nada dos veces (revisión del PR #131, 2026-10-22).
    /// </summary>
    [FactConFirebird]
    public async Task La_cancelacion_en_eleventa_despues_de_una_devolucion_llega_como_la_devolucion_de_lo_que_quedaba()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var enEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var (caja2, turno) = (new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false), new TurnoParaEleventa(HoraLocal(120), 300));
        await EnvioEleventa.DevolverAsync(e.Conexion, caja2,
            new DevolucionParaEleventa(enEleventa, false, HoraLocal(0).AddSeconds(-10), turno, [new ArticuloDevuelto(10, "Pluma", 1, 9)]));
        await EnvioEleventa.DevolverAsync(e.Conexion, caja2, new DevolucionParaEleventa(enEleventa, true, HoraLocal(0).AddSeconds(-5), turno, []));

        await LeerAsync(c);
        await LeerAsync(c);

        await using var db = servicio.Base();
        var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(EstadoVenta.Pagada, venta.Estado);
        Assert.All(venta.Lineas, l => Assert.Equal(l.Cantidad, l.CantidadDevuelta));
        // Las dos sin efectivo (salió de la caja de eleventa) y como devoluciones: entre las dos, lo que se cobró.
        var devoluciones = await db.Devoluciones.Where(d => d.VentaId == venta.Id).OrderBy(d => d.FechaUtc).ToListAsync();
        Assert.Equal([(false, 900L, 0L), (false, 1800L, 0L)], devoluciones.Select(d => (d.EsCancelacion, d.TotalCentavos, d.ReembolsoEfectivoCentavos)));
        Assert.Equal(venta.TotalCentavos, devoluciones.Sum(d => d.TotalCentavos));
        // Lo que hizo eleventa se queda como fue: una parcial y una cancelación.
        Assert.Equal([false, true], (await db.Set<DevolucionDesdeEleventa>().OrderBy(d => d.DevolucionEleventa).ToListAsync()).Select(d => d.EsCancelacion));
        // Las tres plumas regresaron al inventario, una vez cada una.
        Assert.Equal(3000, (await db.MovimientosInventario.Where(m => m.DocumentoTipo == nameof(Devolucion) && m.VarianteId == c.Variante).ToListAsync())
            .Sum(m => m.Cantidad.Milesimas));
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
    public async Task En_la_ventana_de_devoluciones_el_ticket_de_otra_caja_dice_que_se_devuelve_en_eleventa_y_el_del_turno_de_la_caja_2_se_devuelve_aqui()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        var principal = await CobroDeLaPrincipalAsync(e, plumas: 2);
        var cobrada = HoraLocal(20);
        var deLaCaja2 = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100, new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloParaEleventa(10, "Pluma", 2, 18)]));
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

        // El folio impreso es de la principal: el ticket se lee en eleventa y se ve, pero se devuelve en eleventa.
        await pantalla.EscribirAsync("folio", principal.Folio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await pantalla.TeclaAsync("folio", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Principal (eleventa)"), "el ticket de la principal");
        var dePrincipal = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Pluma punto fino", dePrincipal);
        Assert.Contains("Este ticket es de otra caja de eleventa (Principal): devuélvelo en eleventa (está abierto en esta caja).", dePrincipal);
        Assert.DoesNotContain("Devolver lo marcado", dePrincipal);
        Assert.DoesNotContain("Cancelar todo el ticket", dePrincipal);

        // El que se cobró en el eleventa de la caja 2, en el turno que tiene abierto, sí se devuelve aquí.
        await pantalla.EscribirAsync("folio", deLaCaja2.Folio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await pantalla.TeclaAsync("folio", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Caja 2 (eleventa)"), "el ticket del eleventa de la caja 2");
        Assert.DoesNotContain("Cancelar todo el ticket", await pantalla.TextoAsync());
        // Con todo lo que le queda al ticket, la ventana dice antes de confirmar que en eleventa quedará cancelado (2026-10-24); con
        // menos, no.
        var cantidad = await pantalla.ElementoAsync(x => x.Clase == "cantidad-devolver", "la cantidad de la pluma");
        await pantalla.DispararAsync(cantidad, "onchange", new ChangeEventArgs { Value = "2" });
        Assert.Contains(PilotoConEleventa.DevolverTodoCancela, PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));
        cantidad = await pantalla.ElementoAsync(x => x.Clase == "cantidad-devolver", "la cantidad de la pluma");
        await pantalla.DispararAsync(cantidad, "onchange", new ChangeEventArgs { Value = "1" });
        Assert.DoesNotContain(PilotoConEleventa.DevolverTodoCancela, PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));
        var motivo = await pantalla.ElementoAsync(x => x.Etiqueta == "input" && x.Eventos.ContainsKey("oninput") && x.Id != "folio", "el motivo");
        await pantalla.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "No pinta" });
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Devolver lo marcado"));
        await PilotoDePrueba.EsperarAsync(() => Task.FromResult(comprobante), x => x is not null);

        Assert.Equal(($"{deLaCaja2.Folio} (eleventa, Caja 2)", 900L), (comprobante!.Folio, comprobante.TotalCentavos));
        Assert.Empty(pantalla.Errores);
        await using var db = servicio.Base();
        Assert.Equal(deLaCaja2.TicketId, (await db.Set<DevolucionDeEleventa>().SingleAsync()).TicketEleventa);
    }

    private static async Task<int> TicketEnEleventaAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
    }

    /// <summary>
    /// Devuelto completo, sale exacto lo que cobró eleventa, y allá queda cancelado, como cuando la tienda lo cancela (2026-10-24:
    /// antes quedaba vendido en cero y sin artículos, algo que eleventa nunca deja).
    /// </summary>
    [FactConFirebird]
    public async Task Un_ticket_de_eleventa_con_centavos_se_devuelve_exacto_y_alla_queda_cancelado()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        // Eleventa cobró en la caja 2, en su turno, una pluma a $9.50: eleventa no redondea.
        var cobrada = HoraLocal(20);
        var enEleventa = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(PilotoDePrueba.CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 10, new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloParaEleventa(10, "Pluma", 1, 9.5m)]));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{enEleventa.TicketId}"));

        var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{enEleventa.TicketId}/devolucion",
            new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "Defecto", [new LineaDevolucionSolicitada(ticket.Lineas[0].LineaId, 1000)])));
        await MandarAsync(c);

        // Sale exacto lo que cobró eleventa, y allá el ticket queda cancelado con su total (el subtotal en cero), como lo deja la
        // cancelación de eleventa, y el turno en cero.
        Assert.Equal((950L, 950L, 0L), (comprobante.TotalCentavos, comprobante.ReembolsoEfectivoCentavos, comprobante.RedondeoCentavos));
        var fila = (await e.FilasAsync("SELECT ESTA_CANCELADO, TOTAL, SUBTOTAL FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa.TicketId))).Single();
        Assert.Equal(("t", 9.5, 0d), ((string)fila["ESTA_CANCELADO"]!, Math.Round(Convert.ToDouble(fila["TOTAL"]), 3), Math.Round(Convert.ToDouble(fila["SUBTOTAL"]), 3)));
        Assert.Equal(("C", 9.5), ((string)(await e.ValorAsync("SELECT TIPO_DEVOLUCION FROM DEVOLUCIONES"))!,
            Math.Round(Convert.ToDouble(await e.ValorAsync("SELECT TOTAL_DEVUELTO FROM DEVOLUCIONES")), 3)));
        var enElTurno = (await e.FilasAsync("SELECT ACUMULADO_VENTAS, DEVOLUCIONES_VENTAS_EFECTIVO FROM TURNOS WHERE ID = @t", ("t", turno))).Single();
        Assert.Equal((0d, 9.5d), (Math.Round(Convert.ToDouble(enElTurno["ACUMULADO_VENTAS"]), 3), Math.Round(Convert.ToDouble(enElTurno["DEVOLUCIONES_VENTAS_EFECTIVO"]), 3)));
        await using var db = servicio.Base();
        Assert.Equal(950, (await db.MovimientosCaja.SingleAsync(m => m.Motivo.StartsWith("Devolución del ticket #"))).MontoCentavos);
    }

    /// <summary>Los ajustes de existencia que hizo la lectura de eleventa a la pluma.</summary>
    private static async Task<int> AjustesDeLaLecturaAsync(ServicioDePrueba servicio, Caja c)
    {
        await using var db = servicio.Base();
        return await db.MovimientosInventario.CountAsync(m => m.VarianteId == c.Variante && m.DocumentoTipo == SincronizacionEleventa.Documento);
    }

    /// <summary>La existencia de la pluma aquí.</summary>
    private static async Task<long> ExistenciaAsync(ServicioDePrueba servicio, Caja c)
    {
        await using var db = servicio.Base();
        return (await db.Existencias.SingleAsync(x => x.VarianteId == c.Variante)).Cantidad.Milesimas;
    }

    /// <summary>
    /// Eleventa hace su corte y abre el turno siguiente, y la caja nueva todavía no lo ve (la lectura no ha pasado): lo que se
    /// devuelva aquí de una venta de ese turno se deja (para la caja su turno sigue abierto), y al mandarlo ya es de otro turno
    /// de eleventa: queda por revisar, para anotarlo a mano. El caso raro que queda. Regresa el turno nuevo.
    /// </summary>
    private static async Task<int> EleventaCortaSinQueSeVeaAsync(EleventaDePrueba e, int turno)
    {
        await e.CortarAsync(turno, HoraLocal(0).AddSeconds(-3));
        return await e.AbrirCajaAsync(PilotoDePrueba.CajaEleventa, cajero: 7, HoraLocal(0).AddSeconds(-2), fondo: 300);
    }

    /// <summary>
    /// Aquí se devuelven plumas de la venta sin poder verla en vivo en eleventa (la bandeja estaba mandando: se deja pasar con
    /// lo que se sabe aquí, que su turno sigue abierto), y la bandeja la manda: ya es de otro turno de eleventa y queda por
    /// revisar («anótala a mano»). El caso raro que queda. Regresa su envío.
    /// </summary>
    private static async Task<Guid> DevueltaParaAnotarAsync(ServicioDePrueba servicio, Caja c, TicketDto ticket, int plumas)
    {
        using (await servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.EnvioVentasEleventa>().CandadoAsync(CancellationToken.None))
        {
            var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
            await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, plumas * 1000)])));
        }
        await MandarAsync(c);
        await using var db = servicio.Base();
        var envio = await db.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Devolucion).OrderByDescending(x => x.CreadoUtc).FirstAsync();
        Assert.Equal((EstadoEnvioEleventa.PorRevisar, EnvioEleventa.DeOtroTurnoODeOtraCaja, (MotivoPorRevisar?)MotivoPorRevisar.AnotarAMano),
            (envio.Estado, envio.Error, envio.PorRevisarPorque));
        return envio.Id;
    }

    /// <summary>
    /// En eleventa se anota a mano una devolución de plumas de ese ticket, como la escribe eleventa: con su hora, sin la marca
    /// de la caja 2, y la existencia sube. Regresa su <c>DEVOLUCIONES.ID</c>.
    /// </summary>
    private static async Task<int> AnotadaEnEleventaAsync(EleventaDePrueba e, int turno, int ticket, int plumas, DateTime devuelta)
    {
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES (TURNO_ID, TICKET_ID, TIPO_DEVOLUCION, PAGADO_EN, DEVUELTO_EN, CAJERO, CAJA, TOTAL_DEVUELTO)
            VALUES (@turno, @ticket, 'P', @pagado, @devuelto, 'dueno', 'Caja 2', @total)
            """, ("turno", turno), ("ticket", ticket), ("pagado", HoraLocal(5)), ("devuelto", devuelta), ("total", 9 * plumas));
        var id = Convert.ToInt32(await e.ValorAsync("SELECT MAX(ID) FROM DEVOLUCIONES"));
        await e.EjecutarAsync("""
            INSERT INTO DEVOLUCIONES_ARTICULOS (DEVOLUCION_ID, TICKET_ID, CODIGO_PRODUCTO, DESCRIPCION_PRODUCTO, CANTIDAD_DEVUELTA, DINERO_DEVUELTO, INVENTARIO_POST_DEVOLUCION)
            VALUES (@dev, @ticket, 'PLU-FINA', 'Pluma punto fino', @plumas, @total, 0)
            """, ("dev", id), ("ticket", ticket), ("plumas", plumas), ("total", 9 * plumas));
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL + @plumas WHERE PRODUCTO_ID = 10", ("plumas", plumas));
        return id;
    }

    private static async Task<HttpResponseMessage> AnotarAsync(Caja c, SesionDto quien, Guid envio, params int[] anotaciones) =>
        await c.Http.PostAsJsonAsync($"api/eleventa/envios/{envio}/a-mano", new SolicitudAnotadoAMano(quien.UsuarioId, null, anotaciones));

    private static async Task<List<int>> AnotacionesAsync(Caja c, Guid envio) =>
        (await Leer<List<AnotacionEnEleventaDto>>(await c.Http.GetAsync($"api/eleventa/envios/{envio}/anotaciones"))).Select(a => a.Id).ToList();

    private static async Task<long> DevueltoAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas;
    }

    [FactConFirebird]
    public async Task La_devolucion_de_aqui_anotada_a_mano_en_eleventa_espera_a_que_se_elija_su_anotacion_y_no_se_cuenta_dos_veces()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var turno2 = await EleventaCortaSinQueSeVeaAsync(e, turno1!.Value);
        var envio = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        var (ajustes, existencia) = (await AjustesDeLaLecturaAsync(servicio, c), await ExistenciaAsync(servicio, c));

        // «Anótala a mano en eleventa»: allá se devuelve esa pluma y la lectura la trae antes de que alguien lo marque aquí. Puede
        // ser su anotación: espera, sin pasarse a la venta y sin ajustar la existencia (eleventa ya la tiene; aquí todavía se suma).
        var anotada = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 1, HoraLocal(0));
        await LeerAsync(c);
        Assert.Equal(1000L, await DevueltoAsync(servicio, ticket));
        await using (var db = servicio.Base())
            Assert.False(await db.Set<DevolucionDesdeEleventa>().AnyAsync());
        Assert.Equal((ajustes, existencia), (await AjustesDeLaLecturaAsync(servicio, c), await ExistenciaAsync(servicio, c)));

        // Al marcarla se dice cuál es, de las de eleventa de ese ticket leídas en vivo: sin elegir no se puede, y el cajero no puede.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var anotaciones = await Leer<List<AnotacionEnEleventaDto>>(await c.Http.GetAsync($"api/eleventa/envios/{envio}/anotaciones"));
        Assert.Equal([(anotada, false, "Caja 2", "1 Pluma punto fino", 900L)], anotaciones.Select(a => (a.Id, a.EsCancelacion, a.Caja, a.Detalle, a.TotalCentavos)));
        Assert.Equal(DevolucionesConEleventa.DiCualEs, await RechazoAsync(await AnotarAsync(c, dueno, envio)));
        Assert.Equal(HttpStatusCode.Forbidden, (await AnotarAsync(c, c.Sesion, envio, anotada)).StatusCode);

        // En la pantalla de la caja 2, el dueño la marca eligiendo su anotación.
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<PuenteEleventa>(new() { [nameof(PuenteEleventa.Caja)] = c.Datos, [nameof(PuenteEleventa.Sesion)] = dueno });
        await pantalla.EsperarAsync(t => t.Contains("por revisar"), "el aviso de eleventa");
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Eleventa"));
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Ya lo anoté en eleventa"));
        await pantalla.EsperarAsync(t => t.Contains("¿Cuál es su anotación en eleventa?"), "las devoluciones de eleventa para elegir");
        Assert.Contains($"#{anotada}", await pantalla.TextoAsync());
        var casilla = await pantalla.ElementoAsync(x => x.Etiqueta == "input" && x.Atributos.GetValueOrDefault("type")?.ToString() == "checkbox", "la devolución de eleventa");
        await pantalla.DispararAsync(casilla, "onchange", new ChangeEventArgs { Value = true });
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Es su anotación"));
        await pantalla.EsperarAsync(t => !t.Contains("por revisar"), "que ya no quede nada por revisar");
        Assert.Empty(pantalla.Errores);

        // Ligadas: la de eleventa no se aplica otra vez, la existencia no se mueve, y lo que queda del ticket se devuelve aquí.
        await LeerAsync(c);
        Assert.Equal(1000L, await DevueltoAsync(servicio, ticket));
        Assert.Equal((ajustes, existencia), (await AjustesDeLaLecturaAsync(servicio, c), await ExistenciaAsync(servicio, c)));
        await using var revision = servicio.Base();
        var propia = await revision.Devoluciones.SingleAsync(d => d.VentaId == ticket.VentaId);
        var ligada = Assert.Single(await revision.Set<DevolucionDesdeEleventa>().ToListAsync());
        Assert.Equal((anotada, true, (Guid?)propia.Id, (string?)null), (ligada.DevolucionEleventa, ligada.Propia, ligada.DevolucionId, ligada.PorRevisar));
        var marcado = await revision.EnviosAEleventa.SingleAsync(x => x.Id == envio);
        Assert.Equal(EstadoEnvioEleventa.AMano, marcado.Estado);
        Assert.StartsWith($"Se anotó a mano en eleventa (en eleventa: devolución #{anotada} del ", marcado.Error);
        Assert.True(await revision.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.Contains($"devolución #{anotada}")));
        Assert.Null(await DevolucionesConEleventa.PorQueNoSeDevuelveAsync(revision, ticket.VentaId!.Value));
    }

    /// <summary>Aquí se devuelve una pluma de la venta y la bandeja la manda a eleventa (el mismo turno: entra). Regresa su envío.</summary>
    private static async Task<Guid> DevueltaYMandadaAsync(ServicioDePrueba servicio, Caja c, TicketDto ticket)
    {
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)])));
        await MandarAsync(c);
        await using var db = servicio.Base();
        var envio = await db.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Devolucion).OrderByDescending(x => x.CreadoUtc).FirstAsync();
        Assert.Equal(EstadoEnvioEleventa.Enviada, envio.Estado);
        return envio.Id;
    }

    /// <summary>Lo que entró a eleventa pero aquí no se supo: como si la respuesta se hubiera perdido (sigue pendiente) o se hubiera dejado por revisar.</summary>
    private static async Task<int> SinSaberQueLlegoAsync(ServicioDePrueba servicio, Guid envio, EstadoEnvioEleventa estado)
    {
        await using var db = servicio.Base();
        var x = await db.EnviosAEleventa.SingleAsync(z => z.Id == envio);
        var enEleventa = x.IdEleventa ?? x.TicketEleventa!.Value;
        (x.Estado, x.EnviadaUtc, x.IdEleventa, x.Error) = (estado, null, null, "El eleventa de 127.0.0.1 no terminó de contestar a tiempo: se vuelve a intentar.");
        if (x.Tipo == TipoEnvioEleventa.Venta)
            (x.TicketEleventa, x.FolioEleventa) = (null, null);
        if (estado == EstadoEnvioEleventa.PorRevisar)
            (x.PorRevisarPorque, x.PorRevisarUtc) = (MotivoPorRevisar.AnotarAMano, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return enEleventa;
    }

    [FactConFirebird]
    public async Task Si_lo_anotado_en_eleventa_no_cuadra_se_resuelve_a_mano_y_mientras_la_existencia_sigue_a_eleventa()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var turno2 = await EleventaCortaSinQueSeVeaAsync(e, turno1!.Value);
        var a = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        var b = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        // Las dos de aquí se anotan juntas en eleventa, en una sola de dos plumas: no cuadra con ninguna.
        var juntas = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 2, HoraLocal(0));
        await LeerAsync(c);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        foreach (var x in new[] { a, b })
            Assert.StartsWith("No es lo mismo:", await RechazoAsync(await AnotarAsync(c, dueno, x, juntas)));

        // Mientras se decide, la existencia sigue a eleventa: la principal vende 10 plumas, y aquí también bajan. Y aunque la
        // venta ya sea de hace más de 31 días, lo de eleventa se sigue viendo y no se cuenta dos veces.
        async Task<long> EnEleventaAsync() =>
            (long)Math.Round(Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")) * 1000);
        Assert.Equal(await EnEleventaAsync(), await ExistenciaAsync(servicio, c));
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL - 10 WHERE PRODUCTO_ID = 10");
        await LeerAsync(c);
        Assert.Equal(await EnEleventaAsync(), await ExistenciaAsync(servicio, c));
        await using (var db = servicio.Base())
        {
            (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).EnviadaUtc = DateTime.UtcNow.AddDays(-32);
            await db.SaveChangesAsync();
        }
        await LeerAsync(c);
        Assert.Equal(await EnEleventaAsync(), await ExistenciaAsync(servicio, c));
        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));

        // «Resolverlo a mano»: el cajero no puede; el dueño sí, las dos. Lo de eleventa deja de esperar y se pasa a la venta
        // como cualquier otra de allá (aquí ya no alcanza: queda por revisar, a la vista), y la existencia sigue a eleventa.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await c.Http.PostAsJsonAsync($"api/eleventa/envios/{a}/resuelta", new SolicitudRevision(c.Sesion.UsuarioId, null))).StatusCode);
        foreach (var x in new[] { a, b })
            Assert.True((await c.Http.PostAsJsonAsync($"api/eleventa/envios/{x}/resuelta", new SolicitudRevision(dueno.UsuarioId, "Las anoté juntas"))).IsSuccessStatusCode);
        await LeerAsync(c);

        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        Assert.Equal(await EnEleventaAsync(), await ExistenciaAsync(servicio, c));
        await using var revision = servicio.Base();
        Assert.All(await revision.EnviosAEleventa.Where(x => x.Id == a || x.Id == b).ToListAsync(), x => Assert.Equal(EstadoEnvioEleventa.AMano, x.Estado));
        var pasada = await revision.Set<DevolucionDesdeEleventa>().SingleAsync();
        Assert.Equal((juntas, false, DevolucionesConEleventa.NoAlcanza), (pasada.DevolucionEleventa, pasada.Propia, pasada.PorRevisar));
        Assert.Equal(2, await revision.Bitacora.CountAsync(x => x.Accion == AccionControlada.ConfigurarTienda && x.Detalle.StartsWith("Resolvió a mano en eleventa")
            && x.Detalle.EndsWith("Las anoté juntas")));
    }

    [FactConFirebird]
    public async Task Lo_que_llego_a_eleventa_sin_que_aqui_se_supiera_no_se_deja_por_revisar_queda_enviado()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        var envio = await DevueltaYMandadaAsync(servicio, c, ticket);
        var enEleventa = await SinSaberQueLlegoAsync(servicio, envio, EstadoEnvioEleventa.Pendiente);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        // Se iba a dejar por revisar (para anotarla a mano): ya estaba allá con su marca, y queda como enviada. Anotarla a
        // mano la dejaría dos veces en eleventa.
        await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsJsonAsync($"api/eleventa/envios/{envio}/revisar", new SolicitudRevision(dueno.UsuarioId, null)));

        await using var db = servicio.Base();
        var revisado = await db.EnviosAEleventa.SingleAsync(x => x.Id == envio);
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)enEleventa), (revisado.Estado, revisado.IdEleventa));
        Assert.True(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.Contains("pero ya estaba en eleventa: quedó como enviado")));
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));
    }

    [FactConFirebird]
    public async Task Una_venta_que_llego_a_eleventa_sin_que_aqui_se_supiera_no_se_deja_por_revisar_queda_enviada()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        Guid envio;
        await using (var db = servicio.Base())
            envio = (await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId)).Id;
        var enEleventa = await SinSaberQueLlegoAsync(servicio, envio, EstadoEnvioEleventa.Pendiente);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsJsonAsync($"api/eleventa/envios/{envio}/revisar", new SolicitudRevision(dueno.UsuarioId, null)));

        await using var revision = servicio.Base();
        var revisado = await revision.EnviosAEleventa.SingleAsync(x => x.Id == envio);
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)enEleventa), (revisado.Estado, revisado.TicketEleventa));
    }

    [FactConFirebird]
    public async Task Una_devolucion_de_aqui_que_llego_sola_y_quedo_por_revisar_se_da_por_enviada_al_marcarla_o_al_leer()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        // Llegó con su marca, pero aquí quedó por revisar («anótala a mano»): al ir a marcarla no hay nada que elegir; queda
        // como enviada.
        var primera = await DevueltaYMandadaAsync(servicio, c, ticket);
        var suya = await SinSaberQueLlegoAsync(servicio, primera, EstadoEnvioEleventa.PorRevisar);
        Assert.Equal(DevolucionesConEleventa.LlegoSola, await RechazoAsync(await c.Http.GetAsync($"api/eleventa/envios/{primera}/anotaciones")));
        // Y otra igual, que ve la lectura: también queda como enviada.
        var segunda = await DevueltaYMandadaAsync(servicio, c, ticket);
        var otraSuya = await SinSaberQueLlegoAsync(servicio, segunda, EstadoEnvioEleventa.PorRevisar);
        await LeerAsync(c);

        await using var db = servicio.Base();
        var envios = await db.EnviosAEleventa.Where(x => x.Id == primera || x.Id == segunda).ToDictionaryAsync(x => x.Id);
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)suya), (envios[primera].Estado, envios[primera].IdEleventa));
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)otraSuya), (envios[segunda].Estado, envios[segunda].IdEleventa));
        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        Assert.Empty((await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"))).PorRevisar);
    }

    [FactConFirebird]
    public async Task Dos_devoluciones_de_aqui_del_mismo_ticket_anotadas_a_mano_se_eligen_una_por_una_sin_perder_ni_repetir_nada()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var turno2 = await EleventaCortaSinQueSeVeaAsync(e, turno1!.Value);
        var a = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        var b = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        // Las dos se anotan a mano en eleventa, y la lectura las trae antes de que alguien marque nada: las dos esperan.
        var x = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 1, HoraLocal(0));
        var y = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 1, HoraLocal(0).AddSeconds(20));
        await LeerAsync(c);
        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        // La primera se liga con la que se elige; la otra de eleventa sigue esperando: la segunda de aquí todavía espera su anotación.
        Assert.Equal([x, y], await AnotacionesAsync(c, a));
        Assert.True((await AnotarAsync(c, dueno, a, x)).IsSuccessStatusCode);
        await LeerAsync(c);
        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        // Para la segunda ya no se ofrece la que se ligó con la primera.
        Assert.Equal([y], await AnotacionesAsync(c, b));
        Assert.Equal(DevolucionesConEleventa.YaNoSeElige, await RechazoAsync(await AnotarAsync(c, dueno, b, x)));
        Assert.True((await AnotarAsync(c, dueno, b, y)).IsSuccessStatusCode);
        await LeerAsync(c);

        // Dos plumas regresaron, las dos de aquí, cada una con su anotación: nada se aplicó dos veces ni quedó sin ligar.
        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        await using var db = servicio.Base();
        var ligadas = await db.Set<DevolucionDesdeEleventa>().ToListAsync();
        var devoluciones = await db.EnviosAEleventa.Where(z => z.Id == a || z.Id == b).ToDictionaryAsync(z => z.Id, z => z.DevolucionId);
        Assert.Equal([(x, true, devoluciones[a]), (y, true, devoluciones[b])],
            ligadas.OrderBy(l => l.DevolucionEleventa).Select(l => (l.DevolucionEleventa, l.Propia, l.DevolucionId)));
        Assert.All(await db.EnviosAEleventa.Where(z => z.Id == a || z.Id == b).ToListAsync(), z => Assert.Equal(EstadoEnvioEleventa.AMano, z.Estado));
    }

    [FactConFirebird]
    public async Task Una_devolucion_de_aqui_anotada_en_eleventa_en_dos_partes_se_liga_con_las_dos_y_lo_que_no_es_igual_no_se_liga()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var turno2 = await EleventaCortaSinQueSeVeaAsync(e, turno1!.Value);
        // Aquí se devolvieron dos plumas de una vez; en eleventa se anotaron en dos devoluciones de una.
        var envio = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 2);
        var x = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 1, HoraLocal(0));
        var y = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 1, HoraLocal(0).AddSeconds(30));
        await LeerAsync(c);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        // Una sola no es lo mismo que la de aquí: no se liga (lo que sobre o falte no se esconde en la liga).
        Assert.Equal("No es lo mismo: aquí se devolvió 2 Pluma punto fino Bic Azul; en eleventa, 1 Pluma punto fino. Elige las que, juntas, sean lo mismo.",
            await RechazoAsync(await AnotarAsync(c, dueno, envio, x)));
        Assert.True((await AnotarAsync(c, dueno, envio, x, y)).IsSuccessStatusCode);
        await LeerAsync(c);

        Assert.Equal(2000L, await DevueltoAsync(servicio, ticket));
        await using var db = servicio.Base();
        Assert.Equal([x, y], (await db.Set<DevolucionDesdeEleventa>().Where(l => l.Propia).ToListAsync()).Select(l => l.DevolucionEleventa).Order());
        Assert.Equal(EstadoEnvioEleventa.AMano, (await db.EnviosAEleventa.SingleAsync(z => z.Id == envio)).Estado);
    }

    [FactConFirebird]
    public async Task Si_lo_anotado_en_eleventa_no_es_igual_a_la_de_aqui_no_se_puede_marcar_y_nada_se_pierde_ni_se_repite()
    {
        var (e, turno1) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 3);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);
        var turno2 = await EleventaCortaSinQueSeVeaAsync(e, turno1!.Value);
        // Aquí se devolvió una pluma; en eleventa se anotaron dos.
        var envio = await DevueltaParaAnotarAsync(servicio, c, ticket, plumas: 1);
        var dos = await AnotadaEnEleventaAsync(e, turno2, ticketEleventa, plumas: 2, HoraLocal(0));
        await LeerAsync(c);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        Assert.StartsWith("No es lo mismo: aquí se devolvió 1 Pluma punto fino Bic Azul; en eleventa, 2 Pluma punto fino.",
            await RechazoAsync(await AnotarAsync(c, dueno, envio, dos)));
        await LeerAsync(c);

        // La de aquí sigue por revisar, y la de eleventa sigue esperando, a la vista para elegirla: ni se ligó ni se pasó.
        Assert.Equal(1000L, await DevueltoAsync(servicio, ticket));
        await using var db = servicio.Base();
        Assert.False(await db.Set<DevolucionDesdeEleventa>().AnyAsync());
        Assert.Equal(EstadoEnvioEleventa.PorRevisar, (await db.EnviosAEleventa.SingleAsync(z => z.Id == envio)).Estado);
        Assert.Equal([dos], await AnotacionesAsync(c, envio));
    }

    // ── Sin Firebird: la vuelta con filas de eleventa hechas a mano, y lo que se ve en la caja 2 ──

    /// <summary>
    /// Una pluma de una venta de la caja 2 que ya está en eleventa (el ticket 500), devuelta aquí; su envío se dejó por revisar
    /// (como cuando el ticket es de un turno de eleventa ya cortado). Regresa la venta y el envío de la devolución.
    /// </summary>
    private static async Task<(TicketDto Ticket, Guid Envio)> DevueltaPorRevisarAsync(ServicioDePrueba servicio, Caja c)
    {
        var ticket = await EnviadaAsync(servicio, c, plumas: 2);
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)])));
        var envio = (await EnviosAsync(c)).PorAtender.Single(e => e.Tipo == TipoEnvioEleventa.Devolucion).Id;
        await DejadaPorRevisarAsync(servicio, envio);
        return (ticket, envio);
    }

    /// <summary>
    /// Como cuando se deja por revisar para anotarla a mano y eleventa contesta que no está (sin eleventa no se deja: aquí
    /// eleventa no contesta, así que se pone a mano).
    /// </summary>
    private static async Task DejadaPorRevisarAsync(ServicioDePrueba servicio, Guid envio)
    {
        await using var db = servicio.Base();
        var dejado = await db.EnviosAEleventa.SingleAsync(e => e.Id == envio);
        (dejado.Estado, dejado.Error, dejado.PorRevisarPorque, dejado.PorRevisarUtc) =
            (EstadoEnvioEleventa.PorRevisar, "Se dejó para revisar a mano.", MotivoPorRevisar.AnotarAMano, DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    [FactConFirebird]
    public async Task Si_eleventa_ya_la_habia_cancelado_y_aqui_tambien_se_cancelo_los_dos_reembolsos_quedan_a_la_vista()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await CobrarAsync(c, plumas: 1);
        await MandarAsync(c);
        var ticketEleventa = await TicketEnEleventaAsync(servicio, ticket);

        // El eleventa de la caja 2 la cancela (un reembolso)...
        await EnvioEleventa.DevolverAsync(e.Conexion, new CajaEnEleventa(PilotoDePrueba.CajaEleventa, 7, AbreTurno: false),
            new DevolucionParaEleventa(ticketEleventa, true, HoraLocal(0).AddSeconds(-5), new TurnoParaEleventa(HoraLocal(120), 300), []));
        // ...y la caja nueva también, sin poder revisarlo en vivo (la bandeja tenía el candado): otro reembolso.
        var envios = servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.EnvioVentasEleventa>();
        using (await envios.CandadoAsync(CancellationToken.None))
            await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
                new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var choque = Assert.Single((await MandarAsync(c)).PorAtender, x => x.Estado == EstadoEnvioEleventa.PorRevisar);
        Assert.Contains("ya está cancelado", choque.Error);

        await LeerAsync(c);

        // Eleventa ya la tenía: lo suyo no es la anotación a mano de la de aquí. Los dos reembolsos siguen a la vista.
        await using var revision = servicio.Base();
        Assert.Equal(EstadoEnvioEleventa.PorRevisar, (await revision.EnviosAEleventa.SingleAsync(x => x.Id == choque.Id)).Estado);
        var vuelta = await revision.Set<DevolucionDesdeEleventa>().SingleAsync();
        Assert.Equal((false, DevolucionesConEleventa.NoAlcanza), (vuelta.Propia, vuelta.PorRevisar));
        Assert.Equal(2, (await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"))).PorRevisar.Count);
    }

    /// <summary>Justo después de que la vuelta lee la venta, hace otra cosa (una devolución en la caja que no esperó el candado).</summary>
    private sealed class SeCuela(Func<Task> otraCosa) : DbCommandInterceptor
    {
        private int _hecho;

        /// <summary>Por qué no pudo entrar lo que se quiso colar; vacío si entró.</summary>
        public Exception? Error { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Ventas\"") && Interlocked.Exchange(ref _hecho, 1) == 0)
            {
                try
                {
                    await otraCosa();
                }
                catch (Exception ex)
                {
                    Error = ex;
                }
            }
            return result;
        }
    }

    [Fact]
    public async Task Una_devolucion_de_la_caja_que_se_cuela_mientras_la_lectura_pasa_una_de_eleventa_no_se_pierde()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 3);
        var alla = new DevolucionLeida(904, 500, false, HoraLocal(1), 9, "cajera2", "Caja 2", 31, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 10)]);
        // En cuanto la lectura lee la venta, en la caja se devuelve otra pluma (sin esperar el candado de la bandeja; si la
        // base está apartada, espera un segundo y desiste).
        var cuela = new SeCuela(async () =>
        {
            await using var caja = new PosDbContext(BaseDeDatos.Opciones($"Data Source={servicio.Ruta};Pooling=False;Default Timeout=1"));
            var linea = (await caja.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single();
            await new RegistroDevoluciones(caja).DevolverAsync(ticket.VentaId!.Value, [new LineaPorDevolver(linea.Id, Cantidad.Unidades(1))],
                c.Encargado.UsuarioId, c.Datos.Id, "No pinta", null);
        });

        await using (var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite($"Data Source={servicio.Ruta};Pooling=False").AddInterceptors(cuela).Options))
            Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, [alla], DateTime.UtcNow));

        // La de la caja no pudo meterse en medio, y lo devuelto en la venta es la suma de sus devoluciones.
        Assert.NotNull(cuela.Error);
        await using var revision = servicio.Base();
        var venta = await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        var devueltas = await revision.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).ToListAsync();
        Assert.Equal((1000L, 1000L), (devueltas.SelectMany(d => d.Lineas).Sum(l => l.Cantidad.Milesimas), venta.Lineas.Single().CantidadDevuelta.Milesimas));
    }

    [Fact]
    public async Task La_vuelta_no_pisa_una_devolucion_que_entro_despues_de_que_se_leyo_la_venta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 3);
        var alla = new DevolucionLeida(905, 500, false, HoraLocal(1), 9, "cajera2", "Caja 2", 31, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 10)]);
        // Se buscó el ticket por su folio (la venta queda leída), y antes de pasar lo de eleventa, en la caja se devuelve una pluma.
        await using var db = servicio.Base();
        var leida = await new RegistroDevoluciones(db).PorFolioAsync(c.Datos.SucursalId, ticket.Folio);
        await using (var caja = servicio.Base())
            await new RegistroDevoluciones(caja).DevolverAsync(ticket.VentaId!.Value, [new LineaPorDevolver(leida!.Lineas.Single().Id, Cantidad.Unidades(1))],
                c.Encargado.UsuarioId, c.Datos.Id, "No pinta", null);

        Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, [alla], DateTime.UtcNow));

        // Las dos plumas cuentan: la de la caja no se pisó con la copia vieja de la venta.
        await using var revision = servicio.Base();
        var venta = await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(2000L, venta.Lineas.Single().CantidadDevuelta.Milesimas);
    }

    private static DevolucionLeida Pluma(int id, DateTime devuelta, int plumas = 1) =>
        new(id, 500, false, devuelta, 9 * plumas, "cajera2", "Caja 2", 32, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", plumas, 9 * plumas, 10)]);

    [Fact]
    public async Task Mientras_la_de_aqui_espera_su_anotacion_lo_que_eleventa_devuelve_despues_de_ese_ticket_espera_y_lo_que_no_se_elige_se_pasa_despues()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var (ticket, envio) = await DevueltaPorRevisarAsync(servicio, c);
        // En eleventa devuelven después una pluma (quizá la anotación a mano de la de aquí) y otra cosa más tarde.
        var pluma = Pluma(902, HoraLocal(0));
        var otra = Pluma(903, HoraLocal(0).AddMinutes(1));

        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(0, 0, 2), await DevolucionesConEleventa.PasarVueltaAsync(db, [pluma, otra], DateTime.UtcNow));

        // Ni se pasan ni se adivina: la venta solo tiene la de aquí, y aquí no se impide devolver lo que queda.
        await using (var revision = servicio.Base())
        {
            Assert.Equal(1000L, (await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas);
            Assert.False(await revision.Set<DevolucionDesdeEleventa>().AnyAsync());
            Assert.Null(await DevolucionesConEleventa.PorQueNoSeDevuelveAsync(revision, ticket.VentaId!.Value));
        }

        // Quien la anotó dice que la primera es su anotación (como lo hace «Ya lo anoté en eleventa»): la otra ya no espera y se
        // pasa a la venta como cualquier otra de eleventa.
        await using (var db = servicio.Base())
        {
            var marcado = await db.EnviosAEleventa.SingleAsync(x => x.Id == envio);
            var porAnotar = await DevolucionesConEleventa.PorAnotarAsync(db, marcado);
            Assert.NotNull(porAnotar);
            Assert.Null(await DevolucionesConEleventa.PorQueNoEsAsync(db, porAnotar, [pluma]));
            DevolucionesConEleventa.Ligar(db, porAnotar, [pluma], DateTime.UtcNow);
            marcado.Estado = EstadoEnvioEleventa.AMano;
            await db.SaveChangesAsync();
        }
        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, [pluma, otra], DateTime.UtcNow));

        await using var despues = servicio.Base();
        Assert.Equal(2000L, (await despues.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas);
        Assert.Equal([(902, true), (903, false)],
            (await despues.Set<DevolucionDesdeEleventa>().ToListAsync()).OrderBy(v => v.DevolucionEleventa).Select(v => (v.DevolucionEleventa, v.Propia)));
    }

    [Fact]
    public async Task Una_devolucion_de_aqui_se_marca_anotada_a_mano_diciendo_cual_es_su_anotacion_en_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 2);
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)])));
        var envio = (await EnviosAsync(c)).PorAtender.Single(e => e.Tipo == TipoEnvioEleventa.Devolucion).Id;
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        Task<HttpResponseMessage> AnotarSinElegirAsync() => c.Http.PostAsJsonAsync($"api/eleventa/envios/{envio}/a-mano", new SolicitudAnotadoAMano(dueno.UsuarioId, null));

        // Todavía se manda sola: si ya se anotó allá, primero se deja por revisar (y desde ahí la lectura espera su anotación).
        Assert.Equal(DevolucionesConEleventa.PrimeroPorRevisar, await RechazoAsync(await AnotarSinElegirAsync()));
        await DejadaPorRevisarAsync(servicio, envio);

        // Por revisar: la pantalla dice que al marcarla se elige su anotación, de lo que se lea en vivo en eleventa: sin
        // eleventa no se puede (elegir sin ver qué hay allá lo dice la prueba contra el Firebird).
        var pantalla = await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));
        Assert.True(Assert.Single(pantalla.PorRevisar, p => p.EnvioId == envio).EligeAnotacion);
        Assert.Equal(DevolucionesConEleventa.NoContestaParaElegir, await RechazoAsync(await AnotarSinElegirAsync()));
        Assert.Equal(DevolucionesConEleventa.NoContestaParaElegir, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/eleventa/envios/{envio}/a-mano",
            new SolicitudAnotadoAMano(dueno.UsuarioId, null, [907]))));
        Assert.Equal(DevolucionesConEleventa.NoContestaParaElegir, await RechazoAsync(await c.Http.GetAsync($"api/eleventa/envios/{envio}/anotaciones")));
        await using var db = servicio.Base();
        Assert.Equal(EstadoEnvioEleventa.PorRevisar, (await db.EnviosAEleventa.SingleAsync(x => x.Id == envio)).Estado);
    }

    [Fact]
    public async Task Solo_espera_lo_de_eleventa_que_puede_ser_su_anotacion_y_un_producto_de_eleventa_juntado_aqui_cuenta_como_el_mismo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 2, comun: true);
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta",
                [new LineaDevolucionSolicitada(buscado.Lineas.Single(l => l.Descripcion.StartsWith("Pluma")).LineaId, 1000)])));
        var envio = (await EnviosAsync(c)).PorAtender.Single(e => e.Tipo == TipoEnvioEleventa.Devolucion).Id;
        await DejadaPorRevisarAsync(servicio, envio);
        // En eleventa hay otro producto con el mismo código de la pluma (el 11), que aquí se juntó con ella.
        await using (var db = servicio.Base())
        {
            db.MapeosImportacion.Add(new PapeleriaPos.Nucleo.Importacion.MapeoImportacion
            {
                Origen = SincronizacionEleventa.Origen, Entidad = nameof(PapeleriaPos.Nucleo.Catalogo.Variante), IdOrigen = "11", IdNuevo = c.Variante,
            });
            await db.SaveChangesAsync();
        }
        // Después, en eleventa devuelven el recuerdo (nada que ver con la pluma de aquí) y una pluma, que eleventa encuentra
        // por su código como el producto 11.
        var recuerdo = new DevolucionLeida(930, 500, false, HoraLocal(0), 15, "cajera1", "Principal", 31,
            [new ArticuloDevueltoLeido("COMUN-1-2", "Recuerdo", 1, 15, null)]);
        var pluma = new DevolucionLeida(931, 500, false, HoraLocal(0), 9, "dueno", "Caja 2", 32, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 11)]);

        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(1, 0, 1), await DevolucionesConEleventa.PasarVueltaAsync(db, [recuerdo, pluma], DateTime.UtcNow));

        // El recuerdo se pasó; la pluma espera, y es la misma que la de aquí aunque eleventa le dé otro número.
        await using var revision = servicio.Base();
        Assert.Equal(930, (await revision.Set<DevolucionDesdeEleventa>().SingleAsync()).DevolucionEleventa);
        var porAnotar = await DevolucionesConEleventa.PorAnotarAsync(revision, await revision.EnviosAEleventa.SingleAsync(x => x.Id == envio));
        Assert.Null(await DevolucionesConEleventa.PorQueNoEsAsync(revision, porAnotar!, [pluma]));
    }

    [Fact]
    public async Task Lo_que_eleventa_devolvio_antes_de_la_de_aqui_no_espera_su_anotacion()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var (ticket, _) = await DevueltaPorRevisarAsync(servicio, c);
        // Media hora antes de la de aquí no puede ser su anotación; cinco minutos antes, sí (la hora de cada PC).
        var antes = Pluma(904, HoraLocal(30));
        var casi = Pluma(905, HoraLocal(5));

        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(1, 0, 1), await DevolucionesConEleventa.PasarVueltaAsync(db, [antes, casi], DateTime.UtcNow));

        // La de antes se pasó a la venta (las dos plumas regresaron); la de cinco minutos antes espera.
        await using var revision = servicio.Base();
        var pasada = await revision.Set<DevolucionDesdeEleventa>().SingleAsync();
        Assert.Equal((904, false, (string?)null), (pasada.DevolucionEleventa, pasada.Propia, pasada.PorRevisar));
        Assert.Equal(2000L, (await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas);
    }

    [Fact]
    public async Task Una_de_aqui_que_choco_porque_eleventa_ya_lo_tenia_no_retiene_lo_de_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var (ticket, envio) = await DevueltaPorRevisarAsync(servicio, c);
        await using (var db = servicio.Base())
        {
            var choco = await db.EnviosAEleventa.SingleAsync(x => x.Id == envio);
            choco.PorRevisarPorque = MotivoPorRevisar.EleventaYaLoTenia;
            await db.SaveChangesAsync();
        }

        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, [Pluma(906, HoraLocal(0))], DateTime.UtcNow));

        // Son dos reembolsos (aquí y allá): los dos cuentan y quedan a la vista.
        await using var revision = servicio.Base();
        Assert.Equal(2000L, (await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas);
    }

    [Fact]
    public void La_anotacion_que_se_elige_tiene_que_devolver_lo_mismo_que_la_de_aqui()
    {
        var turno = new TurnoParaEleventa(HoraLocal(120), 300);
        DevolucionParaEleventa Aqui(int plumas) => new(500, false, HoraLocal(10), turno, [new ArticuloDevuelto(10, "Pluma punto fino", plumas, 9 * plumas)]);
        var cancelacion = new DevolucionParaEleventa(500, true, HoraLocal(10), turno, []);
        var cancelada = new DevolucionLeida(910, 500, true, HoraLocal(0), 27, "dueno", "Caja 2", 32, []);
        var sinVariantes = new Dictionary<int, Guid>();

        // Lo mismo, en una o en varias.
        Assert.Null(DevolucionesConEleventa.PorQueNoEs(Aqui(2), [Pluma(911, HoraLocal(0), 2)], sinVariantes));
        Assert.Null(DevolucionesConEleventa.PorQueNoEs(Aqui(2), [Pluma(911, HoraLocal(0)), Pluma(912, HoraLocal(0))], sinVariantes));
        Assert.Null(DevolucionesConEleventa.PorQueNoEs(cancelacion, [cancelada], sinVariantes));
        // Lo que sobra o falta no se liga.
        Assert.NotNull(DevolucionesConEleventa.PorQueNoEs(Aqui(1), [Pluma(911, HoraLocal(0), 2)], sinVariantes));
        Assert.NotNull(DevolucionesConEleventa.PorQueNoEs(Aqui(2), [Pluma(911, HoraLocal(0))], sinVariantes));
        Assert.NotNull(DevolucionesConEleventa.PorQueNoEs(Aqui(1), [cancelada], sinVariantes));
        Assert.NotNull(DevolucionesConEleventa.PorQueNoEs(cancelacion, [Pluma(911, HoraLocal(0))], sinVariantes));
        Assert.NotNull(DevolucionesConEleventa.PorQueNoEs(cancelacion, [cancelada, cancelada with { Id = 913 }], sinVariantes));
        Assert.Equal(DevolucionesConEleventa.DiCualEs, DevolucionesConEleventa.PorQueNoEs(Aqui(1), [], sinVariantes));
        // Un común, por su nombre.
        Assert.Null(DevolucionesConEleventa.PorQueNoEs(new DevolucionParaEleventa(500, false, HoraLocal(10), turno, [new ArticuloDevuelto(null, "Recuerdo", 1, 15)]),
            [new DevolucionLeida(914, 500, false, HoraLocal(0), 15, "dueno", "Caja 2", 32, [new ArticuloDevueltoLeido("COMUN-1-2", "recuerdo ", 1, 15, null)])],
            sinVariantes));
    }

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
        // La cancelación llegó después de la parcial (eleventa cancela lo que quedó): aquí es la devolución de lo que quedaba, y la
        // venta sigue cobrada, todo devuelto (revisión del PR #131: cancelarla restaría dos veces la parcial en el corte del día).
        Assert.Equal(EstadoVenta.Pagada, venta.Estado);
        Assert.All(venta.Lineas, l => Assert.Equal(l.Cantidad, l.CantidadDevuelta));
        var devoluciones = await revision.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).OrderBy(d => d.FechaUtc).ToListAsync();
        Assert.Equal([(false, 2), (false, 1)], devoluciones.Select(d => (d.EsCancelacion, d.Lineas.Count)));
        Assert.All(devoluciones, d => Assert.Equal((0L, venta.TurnoId), (d.ReembolsoEfectivoCentavos, d.TurnoId)));
        // Las tres plumas regresaron al inventario (una y luego dos), y nada fue a la bandeja.
        Assert.Equal(3000, (await revision.MovimientosInventario.Where(m => m.DocumentoTipo == nameof(Devolucion) && m.VarianteId == c.Variante).ToListAsync())
            .Sum(m => m.Cantidad.Milesimas));
        Assert.Single(await revision.EnviosAEleventa.ToListAsync());
    }

    [Fact]
    public async Task Una_venta_de_la_caja_2_que_ya_esta_en_eleventa_se_devuelve_aqui_solo_mientras_sigue_abierto_su_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await EnviadaAsync(servicio, c, plumas: 2);
        var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        Assert.Null(buscado.NoSeDevuelveEnElPiloto);

        // Eleventa hizo su corte y la caja nueva lo tomó: su turno se cerró (TurnosDelPiloto), y el cajero abrió otro.
        await using (var db = servicio.Base())
        {
            (await db.Turnos.SingleAsync(t => t.Id == c.Turno.Id)).CerradoUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Sesion.UsuarioId, 30000)));

        // El ticket ya es de otro turno de eleventa: la ventana lo dice, no ofrece devolverlo ni cancelarlo, y no se devuelve aquí.
        var otroTurno = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        Assert.Equal((DevolucionesConEleventa.DeOtroTurno, false), (otroTurno.NoSeDevuelveEnElPiloto, otroTurno.PuedeCancelar));
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)]))));
        Assert.Equal(DevolucionesConEleventa.DeOtroTurno, await RechazoAsync(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió"))));

        // Se devuelve en eleventa y llega con la lectura, como cualquier otra de allá: sin efectivo de esta caja.
        var alla = new DevolucionLeida(908, 500, false, HoraLocal(0), 9, "cajera2", "Caja 2", 32, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 9, 10)]);
        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, [alla], DateTime.UtcNow));
        await using var revision = servicio.Base();
        var venta = await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal(1000L, venta.Lineas.Single().CantidadDevuelta.Milesimas);
        Assert.Equal(0L, (await revision.Devoluciones.SingleAsync(d => d.VentaId == venta.Id)).ReembolsoEfectivoCentavos);
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
        await DejadaPorRevisarAsync(servicio, pendiente.Id);

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
