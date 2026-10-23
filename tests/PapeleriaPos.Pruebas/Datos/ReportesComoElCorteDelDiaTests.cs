using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Pruebas.Datos.TiendaDeReportes;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El reporte de un día cuenta como el corte del día de ese día (revisión del PR #131, 2026-10-22): lo cobrado, lo devuelto, la
/// utilidad, los departamentos, las impresiones, el artículo común y las cancelaciones, también con lo difícil (un turno que
/// pasa de la medianoche, una cancelación al día siguiente, fiado con devolución a la cuenta, mayoreo con descuento, tarjeta),
/// una venta sin conexión que sube después de que su turno se cerró y, en el piloto, una cancelación hecha en eleventa de un
/// ticket que ya tenía una devolución.
/// </summary>
public sealed class ReportesComoElCorteDelDiaTests
{
    /// <summary>Hoy en las pruebas: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(Ahora);

    /// <summary>Lo que el reporte del día y el corte de ese día deben contar igual.</summary>
    private static void ComoElCorte(ReporteVentasDto r, CorteDelDiaDto c)
    {
        var a = r.Actual;
        Assert.Equal(c.VendidoCentavos - c.DevolucionesCentavos, a.VentaCentavos);
        Assert.Equal(c.PorDepartamento!.TotalCentavos, a.VentaCentavos);
        Assert.Equal((c.Tickets, c.VendidoCentavos, c.DevolucionesCentavos, c.Devoluciones, c.Cancelaciones, c.CancelacionesCentavos),
            (a.Tickets, a.VendidoCentavos, a.DevueltoCentavos, a.Devoluciones, a.Cancelados, a.CanceladosCentavos));
        Assert.Equal(c.Utilidad, a.Utilidad);
        Assert.Equal(c.PorDepartamento.Departamentos.Select(d => (d.Departamento, d.VentaCentavos, d.CostoCentavos)),
            r.Departamentos.Where(d => d.Clase == ClaseVenta.Departamento).Select(d => (d.Nombre, d.VentaCentavos, d.CostoCentavos)));
        Assert.Equal(c.PorDepartamento.ImpresionesCentavos, r.Departamentos.SingleOrDefault(d => d.Clase == ClaseVenta.Impresiones)?.VentaCentavos ?? 0);
        Assert.Equal(c.PorDepartamento.ArticuloComunCentavos, r.Departamentos.SingleOrDefault(d => d.Clase == ClaseVenta.ArticuloComun)?.VentaCentavos ?? 0);
        // Y entre ellas: las formas de pago y los cajeros suman la venta neta (todo es de este POS).
        Assert.Equal(a.VentaCentavos, r.FormasDePago.Sum(f => f.Centavos));
        Assert.Equal(a.VentaCentavos, r.Cajeros.Sum(f => f.VentaCentavos));
    }

    /// <summary>
    /// Ana abre ayer a las 16:00 y sigue abierta (su turno pasa de la medianoche). Ayer: tres plumas a las 18:00 y un cuaderno a
    /// las 23:50 que se cancela hoy a las 00:10. Hoy: dos cuadernos fiados a Doña Rosa (9:00); una goma y $7.30 de impresiones
    /// (cuestan $2.10) en efectivo, con redondeo (9:30); doce plumas al mayoreo ($8 desde 12) con 10 % de descuento y un
    /// artículo común de $15.50, $50 con tarjeta y lo demás en efectivo (10:00); se devuelve un cuaderno fiado, a la cuenta
    /// (11:00), y una pluma del ticket de ayer (11:30); un engargolado se cobra a las 12:00 y se cancela a las 12:05.
    /// </summary>
    [Fact]
    public async Task Hoy_y_ayer_cuentan_como_el_corte_del_dia_tambien_con_lo_dificil()
    {
        await using var t = await CrearAsync(Ahora);
        var cliente = new ClienteRegistrado { Nombre = "Doña Rosa", SucursalId = t.Matriz.Id, LimiteCreditoCentavos = 1_000_000 };
        await using (var db = t.Contexto())
        {
            db.Add(cliente);
            await db.SaveChangesAsync();
        }
        var largo = await t.TurnoAsync(t.Ana, A(2026, 10, 20, 16));
        var plumasDeAyer = await t.CobrarAsync(largo, A(2026, 10, 20, 18), v => Agregar(v, t.Pluma, 3));
        var cuadernoDeLaNoche = await t.CobrarAsync(largo, A(2026, 10, 20, 23, 50), v => Agregar(v, t.Cuaderno, 1));
        await t.CancelarAsync(cuadernoDeLaNoche, A(2026, 10, 21, 0, 10));
        var fiado = await t.CobrarAsync(largo, A(2026, 10, 21, 9), v =>
        {
            v.ClienteId = cliente.Id;
            Agregar(v, t.Cuaderno, 2);
        }, new CobroSolicitado(FormaPago.Credito, 8000));
        await t.CobrarAsync(largo, A(2026, 10, 21, 9, 30), v =>
        {
            Agregar(v, t.Goma, 1);
            v.FijarImpresiones(730, 210);
        });
        await t.CobrarAsync(largo, A(2026, 10, 21, 10), v =>
        {
            var plumas = Agregar(v, t.Pluma, 12, new Precio { VarianteId = t.Pluma.Variante.Id, CantidadMinima = Cantidad.Piezas(12), PrecioCentavos = 800 });
            v.AplicarDescuentoSolicitado(plumas, 1000, null, "Cliente frecuente");
            v.AgregarArticuloComun("Recuerdo", 1550, Cantidad.Piezas(1));
        }, new CobroSolicitado(FormaPago.Tarjeta, 5000, "Aut 1"), new CobroSolicitado(FormaPago.Efectivo, 20000));
        await t.DevolverAsync(fiado, t.Cuaderno, 1, largo, A(2026, 10, 21, 11));
        await t.DevolverAsync(plumasDeAyer, t.Pluma, 1, largo, A(2026, 10, 21, 11, 30));
        var engargolado = await t.CobrarAsync(largo, A(2026, 10, 21, 12), v => Agregar(v, t.Engargolado, 1));
        await t.CancelarAsync(engargolado, A(2026, 10, 21, 12, 5));

        var hoy = await t.ReporteAsync(PeriodoReporte.Hoy);
        var ayer = await t.ReporteAsync(PeriodoReporte.Hoy, Hoy.AddDays(-1));

        ComoElCorte(hoy, await t.CorteDelDiaAsync(Hoy));
        ComoElCorte(ayer, await t.CorteDelDiaAsync(Hoy.AddDays(-1)));
        // Hoy: $80 fiados, $19.80 (sin el redondeo) y $101.90 ($86.40 de plumas y el común), menos $40 y $10 devueltos. Las dos
        // cancelaciones se hicieron hoy: el cuaderno de anoche y el engargolado ($75).
        Assert.Equal((15170L, 3, 20170L, 5000L, 2), (hoy.Actual.VentaCentavos, hoy.Actual.Tickets, hoy.Actual.VendidoCentavos, hoy.Actual.DevueltoCentavos,
            hoy.Actual.Devoluciones));
        Assert.Equal((2, 7500L), (hoy.Actual.Cancelados, hoy.Actual.CanceladosCentavos));
        // Ayer: las tres plumas; el cuaderno cancelado no cuenta en lo cobrado ni como cancelación de ayer.
        Assert.Equal((3000L, 1, 0, 0L), (ayer.Actual.VentaCentavos, ayer.Actual.Tickets, ayer.Actual.Cancelados, ayer.Actual.CanceladosCentavos));
    }

    /// <summary>
    /// Un cuaderno cobrado ayer a las 23:50 y cancelado hoy a las 00:10: la cancelación es de hoy, como en el corte del día, y el
    /// ticket no cuenta en lo cobrado de ayer.
    /// </summary>
    [Fact]
    public async Task Las_cancelaciones_cuentan_el_dia_en_que_se_hicieron_como_en_el_corte()
    {
        await using var t = await CrearAsync(Ahora);
        var largo = await t.TurnoAsync(t.Ana, A(2026, 10, 20, 16));
        var cuaderno = await t.CobrarAsync(largo, A(2026, 10, 20, 23, 50), v => Agregar(v, t.Cuaderno, 1));
        await t.CancelarAsync(cuaderno, A(2026, 10, 21, 0, 10));

        var hoy = await t.ReporteAsync(PeriodoReporte.Hoy);
        var ayer = await t.ReporteAsync(PeriodoReporte.Hoy, Hoy.AddDays(-1));
        var corteDeHoy = await t.CorteDelDiaAsync(Hoy);
        var corteDeAyer = await t.CorteDelDiaAsync(Hoy.AddDays(-1));

        Assert.Equal((1, 4000L), (corteDeHoy.Cancelaciones, corteDeHoy.CancelacionesCentavos));
        Assert.Equal((corteDeHoy.Cancelaciones, corteDeHoy.CancelacionesCentavos), (hoy.Actual.Cancelados, hoy.Actual.CanceladosCentavos));
        Assert.Equal((0, 0), (corteDeAyer.Cancelaciones, ayer.Actual.Cancelados));
        Assert.Equal((0L, 0, 0L, 0), (hoy.Actual.VentaCentavos, hoy.Actual.Tickets, ayer.Actual.VentaCentavos, ayer.Actual.Tickets));
        // La semana las cuenta igual: una cancelación, la de hoy.
        var semana = await t.ReporteAsync(PeriodoReporte.Semana);
        Assert.Equal((1, 4000L), (semana.Actual.Cancelados, semana.Actual.CanceladosCentavos));
    }

    /// <summary>
    /// El turno de Ana de ayer se cerró a las 23:55, con su corte; su caja, sin conexión, todavía cobró un cuaderno a las 00:20
    /// de hoy en ese turno, y subió después. Lo cobrado hoy es de hoy: el corte de hoy lo cuenta (con ese turno, sin su corte,
    /// que es de ayer) y el reporte de hoy también; el de ayer, no.
    /// </summary>
    [Fact]
    public async Task Una_venta_que_sube_tarde_despues_del_cierre_de_su_turno_cuenta_el_dia_en_que_se_cobro()
    {
        await using var t = await CrearAsync(Ahora);
        var deAyer = await t.TurnoAsync(t.Ana, A(2026, 10, 20, 9), A(2026, 10, 20, 23, 55), diferencia: -500);
        await t.CobrarAsync(deAyer, A(2026, 10, 21, 0, 20), v => Agregar(v, t.Cuaderno, 1));

        var hoy = await t.ReporteAsync(PeriodoReporte.Hoy);
        var corteDeHoy = await t.CorteDelDiaAsync(Hoy);
        var corteDeAyer = await t.CorteDelDiaAsync(Hoy.AddDays(-1));

        Assert.Equal((4000L, 1), (corteDeHoy.VendidoCentavos, corteDeHoy.Tickets));
        ComoElCorte(hoy, corteDeHoy);
        // Hoy, su turno con lo que vendió hoy y sin su corte (es de ayer); ayer, su corte, sin esa venta.
        var turnoDeHoy = Assert.Single(corteDeHoy.Turnos);
        Assert.Equal((deAyer.Id, 1, 4000L, (long?)null, (long?)null), (turnoDeHoy.Id, turnoDeHoy.Tickets, turnoDeHoy.VendidoCentavos, turnoDeHoy.ContadoCentavos,
            turnoDeHoy.DiferenciaCentavos));
        var turnoDeAyer = Assert.Single(corteDeAyer.Turnos);
        Assert.Equal((0, 0L, (long?)-500), (turnoDeAyer.Tickets, turnoDeAyer.VendidoCentavos, turnoDeAyer.DiferenciaCentavos));
        Assert.Equal((0L, 0), (corteDeAyer.VendidoCentavos, corteDeAyer.Tickets));
        Assert.Equal(0L, (await t.ReporteAsync(PeriodoReporte.Hoy, Hoy.AddDays(-1))).Actual.VentaCentavos);
    }

    /// <summary>
    /// El piloto: dos plumas de la caja 2 ($20, a las 9:00) que entraron a eleventa como el ticket 20; aquí se devuelve una a las
    /// 10:00 y a las 11:00 eleventa cancela lo que quedaba del ticket (como lo hace eleventa: «si antes hubo una parcial, se
    /// cancela lo que quedó»). La lectura la trae: aquí es la devolución de la otra pluma, la venta sigue cobrada y no se resta
    /// nada dos veces. Un cuaderno que eleventa cancela sin ninguna devolución antes sí se cancela.
    /// </summary>
    [Fact]
    public async Task Una_cancelacion_en_eleventa_despues_de_una_devolucion_no_resta_dos_veces()
    {
        await using var t = await CrearAsync(Ahora);
        var turno = await t.TurnoAsync(t.Ana, A(2026, 10, 21, 8));
        var plumas = await t.CobrarAsync(turno, A(2026, 10, 21, 9), v => Agregar(v, t.Pluma, 2));
        var cuaderno = await t.CobrarAsync(turno, A(2026, 10, 21, 9, 30), v => Agregar(v, t.Cuaderno, 1));
        await t.DevolverAsync(plumas, t.Pluma, 1, turno, A(2026, 10, 21, 10));
        await using (var db = t.Contexto())
        {
            foreach (var (venta, ticket) in new[] { (plumas, 20), (cuaderno, 21) })
                db.EnviosAEleventa.Add(new EnvioAEleventa
                {
                    Tipo = TipoEnvioEleventa.Venta, VentaId = venta.Id, CreadoUtc = venta.PagadaUtc!.Value, Estado = EstadoEnvioEleventa.Enviada, Intentos = 1,
                    TicketEleventa = ticket, FolioEleventa = 100 + ticket, EnviadaUtc = venta.PagadaUtc,
                });
            await db.SaveChangesAsync();
        }

        await using (var db = t.Contexto())
        {
            var vuelta = await DevolucionesConEleventa.AplicarVueltaAsync(db,
            [
                new DevolucionLeida(900, 20, true, new DateTime(2026, 10, 21, 11, 0, 0), 10, "admin", "Principal", null, []),
                new DevolucionLeida(901, 21, true, new DateTime(2026, 10, 21, 11, 5, 0), 40, "admin", "Principal", null, []),
            ], A(2026, 10, 21, 11, 10));
            await db.SaveChangesAsync();
            Assert.Equal(new ResultadoVuelta(2, 0), vuelta);
        }

        await using (var db = t.Contexto())
        {
            var conDevolucion = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == plumas.Id);
            Assert.Equal(EstadoVenta.Pagada, conDevolucion.Estado);
            Assert.All(conDevolucion.Lineas, l => Assert.Equal(l.Cantidad, l.CantidadDevuelta));
            var devoluciones = await db.Devoluciones.Where(d => d.VentaId == plumas.Id).OrderBy(d => d.FechaUtc).ToListAsync();
            Assert.Equal([(false, 1000L), (false, 1000L)], devoluciones.Select(d => (d.EsCancelacion, d.TotalCentavos)));
            Assert.StartsWith("Cancelada en eleventa (Principal, admin, 21/10 11:00) después de otra devolución", devoluciones[1].Motivo);
            // Lo que hizo eleventa se queda como fue: una cancelación, en su registro y en la bitácora.
            Assert.True((await db.Set<DevolucionDesdeEleventa>().SingleAsync(d => d.DevolucionEleventa == 900)).EsCancelacion);
            Assert.Contains(await db.Bitacora.Where(b => b.VentaId == plumas.Id).ToListAsync(),
                b => b.Accion == AccionControlada.CancelarTicket && b.Detalle.Contains("que ya tenía devoluciones: aquí es la devolución de lo que quedaba"));
            // Sin devoluciones antes, la cancelación de eleventa cancela.
            Assert.Equal(EstadoVenta.Cancelada, (await db.Ventas.SingleAsync(v => v.Id == cuaderno.Id)).Estado);
            Assert.True((await db.Devoluciones.SingleAsync(d => d.VentaId == cuaderno.Id)).EsCancelacion);
        }

        var r = await t.ReporteAsync(PeriodoReporte.Hoy, piloto: true);
        var c = await t.CorteDelDiaAsync(Hoy);
        // Las plumas: $20 cobrados, $10 devueltos aquí y $10 cancelados en eleventa. El cuaderno, cancelado.
        Assert.Equal((0L, 1, 2000L, 2000L, 2), (r.Actual.VentaCentavos, r.Actual.Tickets, r.Actual.VendidoCentavos, r.Actual.DevueltoCentavos, r.Actual.Devoluciones));
        Assert.Equal((1, 4000L), (r.Actual.Cancelados, r.Actual.CanceladosCentavos));
        Assert.Equal(0L, r.Departamentos.Where(d => d.Clase == ClaseVenta.Departamento).Sum(d => d.VentaCentavos));
        ComoElCorte(r, c);
    }
}
