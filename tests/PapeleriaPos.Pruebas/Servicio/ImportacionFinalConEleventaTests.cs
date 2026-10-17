using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La importación final contra el Firebird de prueba (docs/migracion-eleventa.md, «La importación final»): la caja 2 vende,
/// cancela y devuelve por el puente; en eleventa se le devuelve algo a un ticket suyo (la vuelta); eleventa vende, cancela y
/// devuelve lo suyo, y la caja 2 devuelve tickets de eleventa (la ida). Con eleventa leído como lo lee importar-eleventa, el
/// historial de la base de la caja 2 queda exactamente con lo de eleventa, suma lo mismo y no se duplica al repetirla.
/// </summary>
public sealed class ImportacionFinalConEleventaTests
{
    /// <summary>Una venta como la escribe una caja de eleventa al cobrar: plumas (el producto 10) o un común.</summary>
    private static VentaParaEleventa Venta(DateTime cobrada, params (int? Producto, decimal Cantidad, decimal Importe)[] articulos) =>
        new(cobrada.AddMinutes(-1), cobrada, 100, new TurnoParaEleventa(HoraLocal(90), 500),
            articulos.Select(a => new ArticuloParaEleventa(a.Producto, a.Producto is null ? "Recuerdo" : "Pluma", a.Cantidad, a.Importe)).ToList());

    private static DevolucionParaEleventa Devolucion(int ticket, DateTime devuelta, params ArticuloDevuelto[] articulos) =>
        new(ticket, articulos.Length == 0, devuelta, new TurnoParaEleventa(HoraLocal(90), 500), articulos);

    /// <summary>El ticket de eleventa de una venta de la caja 2.</summary>
    private static async Task<int> TicketAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
    }

    private static long Centavos(object? pesos) => ReglasEleventa.Centavos(Convert.ToDouble(pesos));

    [FactConFirebird]
    public async Task El_historial_queda_con_lo_de_eleventa_sin_lo_de_la_caja_2_y_repetirla_no_duplica()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var caja2 = new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false);

        // La caja 2: una venta, una que se cancela, una con una pluma devuelta aquí y una a la que se le devuelve una en eleventa.
        await CobrarAsync(c, plumas: 1);
        var cancelada = await CobrarAsync(c, plumas: 2);
        var devuelta = await CobrarAsync(c, plumas: 3, comun: true);
        var vuelta = await CobrarAsync(c, plumas: 2);
        await MandarAsync(c);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{cancelada.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var buscada = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{devuelta.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{devuelta.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No era", [new LineaDevolucionSolicitada(buscada.Lineas[0].LineaId, 1000)])));
        await MandarAsync(c);
        await EnvioEleventa.DevolverAsync(e.Conexion, caja2, Devolucion(await TicketAsync(servicio, vuelta), HoraLocal(0).AddSeconds(-5),
            new ArticuloDevuelto(10, "Pluma", 1, 9)));
        await LeerAsync(c);
        await LeerAsync(c);

        // Lo de eleventa: la principal vende plumas y un común, y otra venta que cancela; en el eleventa de la caja 2 se venden
        // tres plumas y se devuelve una.
        var principal = new CajaEnEleventa(1, 7);
        var dePrincipal = await EnvioEleventa.MandarAsync(e.Conexion, principal, Venta(HoraLocal(30), (10, 2, 18m), (null, 1, 15.5m)));
        var canceladaAlla = await EnvioEleventa.MandarAsync(e.Conexion, principal, Venta(HoraLocal(25), (10, 1, 9m)));
        await EnvioEleventa.DevolverAsync(e.Conexion, principal, Devolucion(canceladaAlla.TicketId, HoraLocal(24)));
        var enLaCaja2 = await EnvioEleventa.MandarAsync(e.Conexion, caja2, Venta(HoraLocal(15), (10, 3, 27m)));
        await EnvioEleventa.DevolverAsync(e.Conexion, caja2, Devolucion(enLaCaja2.TicketId, HoraLocal(14), new ArticuloDevuelto(10, "Pluma", 1, 9)));

        // La ida: la caja 2 devuelve otra pluma del ticket de su eleventa (llega a eleventa) y una del de la principal (queda por
        // revisar: todavía no se ha visto cómo anota eleventa la devolución de otra caja).
        foreach (var ticket in new[] { enLaCaja2.TicketId, dePrincipal.TicketId })
        {
            var leido = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{ticket}"));
            await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{ticket}/devolucion",
                new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "Defecto", [new LineaDevolucionSolicitada(leido.Lineas[0].LineaId, 1000)])));
            await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0);
        }

        // Lo que hay en eleventa: los tickets que escribió el puente (los de la caja 2) y los demás.
        HashSet<int> delPuente;
        int antes;
        await using (var db = servicio.Base())
        {
            delPuente = (await db.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Venta).Select(x => x.TicketEleventa!.Value).ToListAsync()).ToHashSet();
            antes = await db.VentasHistoricas.CountAsync();
        }
        var cobrados = await e.FilasAsync("SELECT ID, TOTAL, ESTA_CANCELADO FROM VENTATICKETS WHERE ESTA_ABIERTO = 'f'");
        var propios = cobrados.Where(f => !delPuente.Contains(Convert.ToInt32(f["ID"]))).ToList();
        long Suma(IEnumerable<Dictionary<string, object?>> tickets) => tickets.Where(f => (string)f["ESTA_CANCELADO"]! != "t").Sum(f => Centavos(f["TOTAL"]));
        Assert.Equal(4, delPuente.Count);
        // El 41 de la principal (de antes), las dos de la principal y la del eleventa de la caja 2.
        Assert.Equal(4, propios.Count);

        var datos = await LectorEleventa.LeerAsync(e.Conexion);
        ResultadoImportacionFinal primera, segunda;
        await using (var db = servicio.Base())
            primera = await ImportacionFinal.AplicarAsync(db, datos);
        (int, int, int) conteos;
        await using (var db = servicio.Base())
            conteos = (await db.VentasHistoricas.CountAsync(), await db.VentasHistoricasLineas.CountAsync(), await db.MapeosImportacion.CountAsync());
        await using (var db = servicio.Base())
            segunda = await ImportacionFinal.AplicarAsync(db, await LectorEleventa.LeerAsync(e.Conexion));

        await using var lectura = servicio.Base();
        var mapeos = await lectura.MapeosImportacion.Where(m => m.Origen == ImportadorEleventa.Origen && m.Entidad == nameof(VentaHistorica)).ToListAsync();
        // El historial tiene exactamente los tickets de eleventa, ninguno de la caja 2.
        Assert.Equal(propios.Select(f => Convert.ToInt32(f["ID"])).Order(), mapeos.Select(m => int.Parse(m.IdOrigen)).Order());
        Assert.Equal(antes + propios.Count, conteos.Item1);
        var ids = mapeos.Select(m => m.IdNuevo).ToList();
        var historial = await lectura.VentasHistoricas.Include(v => v.Lineas).Where(v => ids.Contains(v.Id)).ToListAsync();
        // Suma lo mismo que en eleventa: lo vendido allá, con lo que se devolvió ya bajado (también lo devuelto en la caja 2).
        Assert.Equal(Suma(propios), historial.Where(v => !v.Cancelada).Sum(v => v.TotalCentavos));
        Assert.Equal((Suma(propios), Suma(cobrados) - Suma(propios)), (primera.TotalCentavos, primera.DelPuente.TotalCentavos));
        var deLaCaja2 = historial.Single(v => v.Id == mapeos.Single(m => m.IdOrigen == enLaCaja2.TicketId.ToString()).IdNuevo);
        Assert.Equal((900L, 1000L), (deLaCaja2.TotalCentavos, Assert.Single(deLaCaja2.Lineas).Cantidad.Milesimas));
        Assert.True(historial.Single(v => v.Id == mapeos.Single(m => m.IdOrigen == canceladaAlla.TicketId.ToString()).IdNuevo).Cancelada);

        // Lo de la caja 2 no se trajo, y dice por qué: su cancelación y su devolución las hizo aquí, y la de eleventa ya está en
        // su venta (sin efectivo).
        var p = primera.DelPuente;
        Assert.Equal(delPuente.Order(), p.Tickets.Keys.Order());
        Assert.Equal((0, 1, 3, 2, 1, 0), (p.PorSuMarca, p.Cancelados, p.ConDevoluciones, p.DevolucionesDeAqui, p.DevolucionesDeEleventa, p.DevolucionesSinPasar));
        // La ida de la caja 2 llegó al ticket de su eleventa; la de la principal no llegó: el ticket se importó sin ella.
        Assert.Equal(1, p.Idas);
        var porRevisar = Assert.Single(p.PorRevisar);
        Assert.Contains($"del ticket #{dePrincipal.Folio}", porRevisar);
        Assert.EndsWith("cuenta $9.00 de más.", porRevisar);

        // Otra vez, con lo mismo: nada nuevo, nada cambia.
        Assert.Equal((0, 0, 0, propios.Count), (segunda.Nuevos, segunda.Actualizados, segunda.Quitados, segunda.Iguales));
        Assert.Equal(conteos, (await lectura.VentasHistoricas.CountAsync(), await lectura.VentasHistoricasLineas.CountAsync(),
            await lectura.MapeosImportacion.CountAsync()));
        Assert.Equal(primera.TotalCentavos, segunda.TotalCentavos);
    }
}
