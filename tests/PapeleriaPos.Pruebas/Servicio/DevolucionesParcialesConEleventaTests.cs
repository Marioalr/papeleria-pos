using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Varias devoluciones parciales de una misma línea en el piloto (2026-10-24): desde el PR #132 la caja le da a cada una lo
/// acumulado redondeado menos lo que ya se devolvió (<see cref="CalculoDevolucion.ParteDe(long, Cantidad, Cantidad, Cantidad)"/>),
/// así lo devuelto suma siempre lo de la línea. El puente redondeaba cada parte por su lado: la vuelta (lo devuelto en
/// eleventa de una venta de la caja 2) y la ida de un ticket cobrado en eleventa quedaban un centavo arriba o abajo. Ahora usan
/// la misma regla, y lo que eleventa anota (la devolución, su total, el ticket, el turno y el día) es lo mismo que la caja 2,
/// sin contar nada dos veces.
/// </summary>
public sealed class DevolucionesParcialesConEleventaTests
{
    /// <summary>
    /// Tres plumas con 1.05 % de descuento (Ctrl+D, lo que el cajero da solo): con la pluma a $9, $26.72, que no se reparte
    /// parejo entre tres. Se cobra al peso.
    /// </summary>
    private static async Task<TicketDto> TresConDescuentoAsync(Caja c) =>
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(c.Variante, null, 3000, DescuentoCentesimas: 105, MotivoDescuento: "Cliente frecuente")],
            [new CobroSolicitado(FormaPago.Efectivo, 100000)])));

    /// <summary>Lo que le toca a la pieza que se devuelve, con k piezas ya devueltas de las tres: la regla de la caja.</summary>
    private static long Pieza(long importe, int yaDevueltas) =>
        CalculoDevolucion.ParteDe(importe, Cantidad.Piezas(yaDevueltas), Cantidad.Piezas(1), Cantidad.Piezas(3));

    /// <summary>
    /// La vuelta: en eleventa se devuelven, de una en una y en tres lecturas, las tres plumas de una venta de la caja 2. Aquí cada
    /// una es una devolución sin efectivo con lo que le toca por la regla de la caja (antes, cada una en proporción por su lado,
    /// y las tres no sumaban lo de la línea: un centavo de más o de menos). La lectura siguiente no las vuelve a pasar.
    /// </summary>
    [Fact]
    public async Task La_vuelta_de_varias_parciales_de_eleventa_suma_lo_de_la_linea_sin_un_centavo_de_mas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await TresConDescuentoAsync(c);
        long importe, costo;
        await using (var db = servicio.Base())
        {
            // Como la deja la bandeja: en eleventa, el ticket 500.
            var envio = await db.EnviosAEleventa.SingleAsync(e => e.VentaId == ticket.VentaId);
            (envio.Estado, envio.FolioEleventa, envio.TicketEleventa, envio.EnviadaUtc) = (EstadoEnvioEleventa.Enviada, 42, 500, DateTime.UtcNow);
            await db.SaveChangesAsync();
            var linea = (await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single();
            (importe, costo) = (linea.ImporteCentavos, linea.CostoCentavos);
        }
        Assert.NotEqual(0, importe % 3);
        DevolucionLeida Pluma(int id, int minutos) =>
            new(id, 500, false, HoraLocal(minutos), 8.91m, "cajera2", "Caja 2", 32, [new ArticuloDevueltoLeido("PLU-FINA", "Pluma punto fino", 1, 8.91m, 10)]);
        List<DevolucionLeida> enEleventa = [];

        // Cada lectura trae lo que eleventa tiene hasta ese momento.
        foreach (var (id, minutos) in new[] { (950, 6), (951, 5), (952, 4) })
        {
            enEleventa.Add(Pluma(id, minutos));
            await using var db = servicio.Base();
            Assert.Equal(new ResultadoVuelta(1, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, enEleventa, DateTime.UtcNow));
        }
        await using (var db = servicio.Base())
            Assert.Equal(new ResultadoVuelta(0, 0), await DevolucionesConEleventa.PasarVueltaAsync(db, enEleventa, DateTime.UtcNow));

        await using var revision = servicio.Base();
        var devoluciones = await revision.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == ticket.VentaId).OrderBy(d => d.FechaUtc).ToListAsync();
        Assert.Equal([Pieza(importe, 0), Pieza(importe, 1), Pieza(importe, 2)], devoluciones.Select(d => d.TotalCentavos));
        Assert.Equal((importe, costo), (devoluciones.Sum(d => d.TotalCentavos), devoluciones.SelectMany(d => d.Lineas).Sum(l => l.CostoCentavos)));
        Assert.All(devoluciones, d => Assert.Equal(0, d.ReembolsoEfectivoCentavos));
        Assert.Equal(3000, (await revision.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId)).Lineas.Single().CantidadDevuelta.Milesimas);
    }

    /// <summary>
    /// La ida de una venta de la caja 2: sus tres plumas se devuelven aquí de una en una y cada devolución va a eleventa. Lo que
    /// eleventa anota en cada lugar es lo que salió de la caja 2: cada devolución y sus artículos, los movimientos del corte, lo
    /// devuelto del turno y del día; lo vendido queda en cero y la última, que devuelve lo que quedaba, cancela el ticket, como lo
    /// hace la tienda. Las lecturas siguientes las reconocen como de aquí (no las pasan otra vez) y la existencia queda como en
    /// eleventa.
    /// </summary>
    [FactConFirebird]
    public async Task Las_parciales_de_una_venta_de_la_caja_2_quedan_en_eleventa_igual_que_aqui_y_no_vuelven()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var ticket = await TresConDescuentoAsync(c);
        await MandarAsync(c);

        var comprobantes = new List<ComprobanteDevolucionDto>();
        for (var i = 0; i < 3; i++)
        {
            var buscado = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
            comprobantes.Add(await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No pinta", [new LineaDevolucionSolicitada(buscado.Lineas[0].LineaId, 1000)]))));
            await MandarAsync(c);
        }
        await LeerAsync(c);
        await LeerAsync(c);

        int enEleventa;
        await using (var db = servicio.Base())
        {
            enEleventa = (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
            var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
            var importe = venta.Lineas.Single().ImporteCentavos;
            var devoluciones = await db.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).OrderBy(d => d.FechaUtc).ToListAsync();
            // Aquí: lo de la regla de la caja, y en efectivo al peso, lo que se cobró.
            Assert.Equal([Pieza(importe, 0), Pieza(importe, 1), Pieza(importe, 2)], devoluciones.Select(d => d.TotalCentavos));
            Assert.Equal(venta.TotalCobradoCentavos, devoluciones.Sum(d => d.ReembolsoEfectivoCentavos));
            Assert.Equal(comprobantes.Select(x => x.ReembolsoEfectivoCentavos), devoluciones.Select(d => d.ReembolsoEfectivoCentavos));
            // Lo que escribió la caja 2 no vuelve como si fuera de eleventa.
            Assert.False(await db.Set<DevolucionDesdeEleventa>().AnyAsync());
            Assert.Equal(3000, venta.Lineas.Single().CantidadDevuelta.Milesimas);
        }
        var reembolsos = comprobantes.Select(x => x.ReembolsoEfectivoCentavos).ToList();
        var total = reembolsos.Sum();
        static long Centavos(object? valor) => (long)Math.Round(Convert.ToDecimal(valor) * 100);

        // En eleventa, en cada lugar, lo mismo que salió de la caja 2. La última devuelve lo que quedaba: va como cancelación, como lo
        // hace la tienda (sin renglones de artículos).
        var alla = await e.FilasAsync("SELECT TIPO_DEVOLUCION, TOTAL_DEVUELTO FROM DEVOLUCIONES WHERE TICKET_ID = @t ORDER BY ID", ("t", enEleventa));
        Assert.Equal(reembolsos, alla.Select(f => Centavos(f["TOTAL_DEVUELTO"])));
        Assert.Equal("PPC", string.Join("", alla.Select(f => (string)f["TIPO_DEVOLUCION"]!)));
        Assert.Equal(reembolsos[0] + reembolsos[1],
            Centavos(await e.ValorAsync("SELECT SUM(DINERO_DEVUELTO) FROM DEVOLUCIONES_ARTICULOS WHERE TICKET_ID = @t", ("t", enEleventa))));
        Assert.Equal(total, Centavos(await e.ValorAsync("SELECT SUM(MONTO) FROM CORTE_MOVIMIENTOS WHERE TIPO = 'Devolucion' AND ID_TURNO = @t", ("t", turno))));
        var delTurno = (await e.FilasAsync("SELECT ACUMULADO_VENTAS, DEVOLUCIONES_VENTAS_EFECTIVO FROM TURNOS WHERE ID = @t", ("t", turno))).Single();
        Assert.Equal((0L, total), (Centavos(delTurno["ACUMULADO_VENTAS"]), Centavos(delTurno["DEVOLUCIONES_VENTAS_EFECTIVO"])));
        // Los días de la caja 2 (la venta y sus devoluciones pueden caer en días distintos si pasa la medianoche): lo mismo.
        var delDia = (await e.FilasAsync("SELECT SUM(ACUMULADO_VENTAS) AS VENTAS, SUM(DEVOLUCIONES_VENTAS_EFECTIVO) AS DEVUELTO FROM CORTE_OPERACIONES WHERE ID_CAJA = @c",
            ("c", CajaEleventa))).Single();
        Assert.Equal((0L, total), (Centavos(delDia["VENTAS"]), Centavos(delDia["DEVUELTO"])));
        // El ticket, cancelado con lo que le quedaba (como la cancelación de eleventa: el total no baja).
        var elTicket = (await e.FilasAsync("SELECT ESTA_CANCELADO, TOTAL FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa))).Single();
        Assert.Equal(("t", reembolsos[2]), ((string)elTicket["ESTA_CANCELADO"]!, Centavos(elTicket["TOTAL"])));
        // Y la existencia, como en eleventa: las tres plumas salieron y regresaron una vez.
        var existenciaAlla = Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10"));
        await using var lectura = servicio.Base();
        Assert.Equal((50d, 50_000L), (existenciaAlla, (await lectura.Existencias.SingleAsync(x => x.VarianteId == c.Variante)).Cantidad.Milesimas));
    }

    /// <summary>
    /// La ida de un ticket cobrado en el eleventa de la caja 2: tres plumas por $20.00, devueltas aquí de una en una. Cada una
    /// sale con lo de la regla de la caja ($6.67, $6.66 y $6.67; antes $6.67, $6.67 y $6.66: devolver dos daba $13.34, uno más
    /// que devolverlas juntas), y la ventana pide autorizar justo eso. Allá lo devuelto del turno es lo que salió de aquí, y la
    /// última, que devuelve lo que quedaba, deja el ticket cancelado, como lo hace la tienda.
    /// </summary>
    [FactConFirebird]
    public async Task Un_ticket_de_eleventa_devuelto_en_partes_en_la_caja_2_va_acumulado_y_alla_queda_cancelado()
    {
        var (e, turno) = await EleventaAsync();
        await using var eleventa = e;
        var cobrada = HoraLocal(20);
        var enEleventa = await EnvioEleventa.MandarAsync(e.Conexion, new CajaEnEleventa(CajaEleventa, 7, AbreTurno: false),
            new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 20, new TurnoParaEleventa(HoraLocal(120), 300), [new ArticuloParaEleventa(10, "Pluma", 3, 20m)]));
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var salieron = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var leido = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/eleventa/tickets/{enEleventa.TicketId}"));
            var linea = leido.Lineas[0];
            // Lo que la ventana pide autorizar, con lo que trae la línea (lo que ya salió de ella).
            var ventana = CalculoDevolucion.ParteDe(linea.ImporteCentavos, new Cantidad(linea.CantidadDevueltaMilesimas), Cantidad.Piezas(1),
                new Cantidad(linea.CantidadMilesimas), linea.DevueltoCentavos!.Value);
            var comprobante = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/eleventa/tickets/{enEleventa.TicketId}/devolucion",
                new SolicitudDevolucion(c.Sesion.UsuarioId, c.Datos.Id, "Defecto", [new LineaDevolucionSolicitada(linea.LineaId, 1000)])));
            Assert.Equal((ventana, ventana), (comprobante.TotalCentavos, comprobante.ReembolsoEfectivoCentavos));
            salieron.Add(comprobante.ReembolsoEfectivoCentavos);
            await MandarAsync(c);
        }

        Assert.Equal([667L, 666L, 667L], salieron);
        static long Centavos(object? valor) => (long)Math.Round(Convert.ToDecimal(valor) * 100);
        var devoluciones = await e.FilasAsync("SELECT TIPO_DEVOLUCION, TOTAL_DEVUELTO FROM DEVOLUCIONES WHERE TICKET_ID = @t ORDER BY ID", ("t", enEleventa.TicketId));
        Assert.Equal(salieron, devoluciones.Select(f => Centavos(f["TOTAL_DEVUELTO"])));
        // La última devuelve lo que quedaba: allá queda cancelado, como lo hace la tienda.
        Assert.Equal("PPC", string.Join("", devoluciones.Select(f => (string)f["TIPO_DEVOLUCION"]!)));
        Assert.Equal("t", (string)(await e.ValorAsync("SELECT ESTA_CANCELADO FROM VENTATICKETS WHERE ID = @t", ("t", enEleventa.TicketId)))!);
        var delTurno = (await e.FilasAsync("SELECT ACUMULADO_VENTAS, DEVOLUCIONES_VENTAS_EFECTIVO FROM TURNOS WHERE ID = @t", ("t", turno))).Single();
        Assert.Equal((0L, 2000L), (Centavos(delTurno["ACUMULADO_VENTAS"]), Centavos(delTurno["DEVOLUCIONES_VENTAS_EFECTIVO"])));
        await using var db = servicio.Base();
        Assert.Equal(salieron, (await db.MovimientosCaja.Where(m => m.TurnoId == c.Turno.Id).OrderBy(m => m.FechaUtc).ToListAsync()).Select(m => m.MontoCentavos));
    }
}
