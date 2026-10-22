using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// «Ventas por período» (entrega 9, parte 2), contra datos hechos a mano: los tickets de unos días, de una caja o de todas, del
/// primero al último y de 100 en 100, con su folio, cajero, forma de pago y si se canceló o se devolvió; y los totales de todos
/// esos días, por día y por forma de pago, que cuentan como el corte del día y los reportes. También los días de la tienda (la
/// medianoche y el horario de verano de antes de 2022), el piloto, los permisos y lo que no se entiende. Cada prueba con su base
/// en memoria (la tienda de los reportes de ventas).
/// </summary>
public sealed class VentasPorPeriodoTests
{
    /// <summary>Hoy en las pruebas: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(Ahora);

    private static DateTime A(int anio, int mes, int dia, int hora, int minuto = 0, int segundo = 0) => TiendaDeReportes.A(anio, mes, dia, hora, minuto, segundo);

    /// <summary>
    /// Del lunes 19 al miércoles 21 de octubre de 2026, en la Caja 1 y la Caja 2:
    /// <list type="bullet">
    /// <item>Lunes 19, 10:00, Ana: un cuaderno ($40, por transferencia).</item>
    /// <item>Martes 20, 17:00, Beto: tres plumas ($30); a las 23:59:30, Beto: una goma ($12.50; se cobran $13).</item>
    /// <item>Hoy, 9:10, Beto: una goma ($12.50; se cobran $13: 50 centavos de redondeo).</item>
    /// <item>Hoy, 10:15, Ana: dos plumas y un cuaderno ($60).</item>
    /// <item>Hoy, 11:40, Ana: un engargolado ($35, con tarjeta).</item>
    /// <item>Hoy, 12:00, Carmen en la Caja 2: dos gomas ($25).</item>
    /// <item>Hoy, 12:30, Ana: un cuaderno ($40) que se cancela a las 12:35: se lista, pero no cuenta.</item>
    /// <item>Hoy, 13:00, Ana devuelve una pluma ($10) del ticket de Beto del martes.</item>
    /// <item>Del historial de eleventa, hoy a las 9:30: un cuaderno y una pluma ($50).</item>
    /// </list>
    /// </summary>
    private sealed record Semana(TiendaDeReportes Tienda, Caja Caja2, Venta DelLunes, Venta DePlumas, Venta GomaDeMedianoche, Venta Cancelada, Venta DeLaCaja2);

    private static async Task<Semana> SemanaAsync()
    {
        var t = await TiendaDeReportes.CrearAsync(Ahora);
        var (pluma, goma, cuaderno, engargolado) = (t.Pluma, t.Goma, t.Cuaderno, t.Engargolado);
        var caja2 = await t.OtraCajaAsync();
        var delLunesDeAna = await t.TurnoAsync(t.Ana, A(2026, 10, 19, 9), A(2026, 10, 19, 18));
        var delLunes = await t.VenderAsync(delLunesDeAna, A(2026, 10, 19, 10), [(cuaderno, 1)], cobros: new CobroSolicitado(FormaPago.Transferencia, 4000, "SPEI 1"));
        var deAyerDeBeto = await t.TurnoAsync(t.Beto, A(2026, 10, 20, 16), A(2026, 10, 21, 0, 30));
        var dePlumas = await t.VenderAsync(deAyerDeBeto, A(2026, 10, 20, 17), [(pluma, 3)]);
        var gomaDeMedianoche = await t.VenderAsync(deAyerDeBeto, A(2026, 10, 20, 23, 59, 30), [(goma, 1)]);
        var deBeto = await t.TurnoAsync(t.Beto, A(2026, 10, 21, 8), A(2026, 10, 21, 10), diferencia: -2000);
        var deAna = await t.TurnoAsync(t.Ana, A(2026, 10, 21, 10));
        var deCarmen = await t.TurnoAsync(t.Carmen, A(2026, 10, 21, 9), caja: caja2);
        await t.VenderAsync(deBeto, A(2026, 10, 21, 9, 10), [(goma, 1)]);
        await t.VenderAsync(deAna, A(2026, 10, 21, 10, 15), [(pluma, 2), (cuaderno, 1)]);
        await t.VenderAsync(deAna, A(2026, 10, 21, 11, 40), [(engargolado, 1)], cobros: new CobroSolicitado(FormaPago.Tarjeta, 3500, "Aut 7"));
        var deLaCaja2 = await t.VenderAsync(deCarmen, A(2026, 10, 21, 12), [(goma, 2)]);
        var cancelada = await t.VenderAsync(deAna, A(2026, 10, 21, 12, 30), [(cuaderno, 1)]);
        await t.CancelarAsync(cancelada, A(2026, 10, 21, 12, 35));
        await t.DevolverAsync(dePlumas, pluma, 1, deAna, A(2026, 10, 21, 13));
        await t.HistorialAsync(A(2026, 10, 21, 9, 30), [(cuaderno, 1, 2400), (pluma, 1, 400)]);
        return new Semana(t, caja2, delLunes, dePlumas, gomaDeMedianoche, cancelada, deLaCaja2);
    }

    private static async Task<VentasPorPeriodoDto> PorPeriodoAsync(TiendaDeReportes t, DateOnly? desde, DateOnly? hasta, Guid? caja = null, int pagina = 0,
        bool piloto = false, Usuario? quien = null)
    {
        await using var db = t.Contexto();
        return await new ReporteVentasPorPeriodo(db).ArmarAsync(t.Caja.Id, (quien ?? t.Luis).Id, desde, hasta, caja, pagina, piloto);
    }

    private static FormaPagoReporteDto Forma(FormaPago forma, long centavos) => new(forma, centavos);

    [Fact]
    public async Task Los_tickets_de_esos_dias_con_sus_devoluciones_y_cancelaciones_y_los_totales_al_final()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var d = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy);

        Assert.Equal((new DateOnly(2026, 10, 19), Hoy, Hoy, (Guid?)null, false), (d.Desde, d.Hasta, d.Hoy, d.CajaId, d.Piloto));
        Assert.Equal(["Caja 1", "Caja 2"], d.Cajas.Select(c => c.Nombre));
        // Del primero al último, también el cancelado (que no cuenta); cada uno con su caja, quién y cómo se pagó.
        Assert.Equal(8, d.Total);
        Assert.Equal(
            [
                ("A-000001", "Caja 1", "Ana", 4000L, "Transferencia", false, 0L),
                ("A-000002", "Caja 1", "Beto", 3000L, "Efectivo", false, 1000L),
                ("A-000003", "Caja 1", "Beto", 1250L, "Efectivo", false, 0L),
                ("A-000004", "Caja 1", "Beto", 1250L, "Efectivo", false, 0L),
                ("A-000005", "Caja 1", "Ana", 6000L, "Efectivo", false, 0L),
                ("A-000006", "Caja 1", "Ana", 3500L, "Tarjeta", false, 0L),
                ("B-000007", "Caja 2", "Carmen", 2500L, "Efectivo", false, 0L),
                ("A-000008", "Caja 1", "Ana", 4000L, "Efectivo", true, 0L),
            ],
            d.Ventas.Select(v => (v.Folio, v.Caja, v.Cajero, v.TotalCentavos, TextosDeReportes.FormasDePago(v.Formas), v.Cancelada, v.DevueltoCentavos)));
        // El de las plumas se devolvió en parte: una de tres.
        Assert.False(d.Ventas[1].DevueltaCompleta);
        Assert.Equal("Devuelta en parte", TextosDeReportes.Estado(d.Ventas[1]));
        Assert.Equal("Cancelada", TextosDeReportes.Estado(d.Ventas[^1]));
        Assert.Null(TextosDeReportes.Estado(d.Ventas[0]));

        // Los totales: lo vendido en los que no se cancelaron, menos lo devuelto en esos días.
        var t = d.Totales;
        Assert.Equal((7, 21500L, 1, 1000L, 1, 4000L, 20500L),
            (t.Tickets, t.VendidoCentavos, t.Devoluciones, t.DevueltoCentavos, t.Cancelados, t.CanceladosCentavos, t.VentaCentavos));
        // Cómo se pagó (el efectivo, sin el redondeo al peso): suman lo vendido.
        Assert.Equal([Forma(FormaPago.Efectivo, 14000), Forma(FormaPago.Tarjeta, 3500), Forma(FormaPago.Transferencia, 4000)], t.Formas);
        Assert.Equal(t.VendidoCentavos, t.Formas.Sum(f => f.Centavos));
        // El historial de eleventa no se lista: se dice cuánto hay de él en esos días.
        Assert.Equal((1, 5000L), (d.DelHistorialTickets, d.DelHistorialCentavos));
    }

    [Fact]
    public async Task Cada_ticket_se_lee_igual_que_en_ventas_del_dia()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var d = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy);

        // Lo mismo que dice «Ventas del día» de cada uno de esos días (de todas las cajas), del primero al último.
        static object Como(VentaDeUnDiaDto v) => (v.Id, v.Folio, v.FolioEleventa, v.FechaLocal, v.CajaId, v.Caja, v.CajeroId, v.Cajero, v.TotalCentavos,
            v.Articulos, string.Join(",", v.Formas), v.Cancelada, v.DevueltoCentavos, v.DevueltaCompleta, v.SeDevuelve);
        var delDia = new List<VentaDeUnDiaDto>();
        for (var dia = new DateOnly(2026, 10, 19); dia <= Hoy; dia = dia.AddDays(1))
        {
            await using var db = s.Tienda.Contexto();
            delDia.AddRange((await new VentasDeOtrosDias(db).DelDiaAsync(s.Tienda.Caja.Id, s.Tienda.Luis.Id, dia, null, null)).Ventas.Reverse());
        }
        Assert.Equal(delDia.Select(Como), d.Ventas.Select(Como));
        // También los artículos (dos plumas y un cuaderno: 3) y lo que todavía se puede devolver.
        Assert.Equal(3, d.Ventas.Single(v => v.Folio == "A-000005").Articulos);
        Assert.True(d.Ventas.Single(v => v.Folio == "A-000005").SeDevuelve);
    }

    [Fact]
    public async Task Cada_dia_cuenta_como_su_corte_del_dia_y_como_los_reportes()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var d = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy);

        // Cada día con algo, en orden; la goma de las 23:59:30 del martes, en el martes.
        Assert.Equal(
            [
                (new DateOnly(2026, 10, 19), 1, 4000L, 0L, 0),
                (new DateOnly(2026, 10, 20), 2, 4250L, 0L, 0),
                (Hoy, 4, 13250L, 1000L, 1),
            ],
            d.Dias.Select(x => (x.Fecha!.Value, x.Tickets, x.VendidoCentavos, x.DevueltoCentavos, x.Cancelados)));
        Assert.Equal([Forma(FormaPago.Efectivo, 9750), Forma(FormaPago.Tarjeta, 3500)], d.Dias[2].Formas);

        // Hoy, lo mismo que el corte del día: lo vendido, los tickets, lo devuelto y lo cancelado; el efectivo del corte trae
        // el redondeo al peso (50 centavos de la goma de Beto).
        await using (var db = s.Tienda.Contexto())
        {
            var corte = await new RegistroCorteDelDia(db).ArmarAsync(s.Tienda.Matriz.Id, Hoy, new Dictionary<Guid, PresenciaDeCaja>());
            var hoy = d.Dias[2];
            Assert.Equal((corte.Tickets, corte.VendidoCentavos, corte.Devoluciones, corte.DevolucionesCentavos, corte.Cancelaciones, corte.CancelacionesCentavos),
                (hoy.Tickets, hoy.VendidoCentavos, hoy.Devoluciones, hoy.DevueltoCentavos, hoy.Cancelados, hoy.CanceladosCentavos));
            Assert.Equal(corte.EfectivoCentavos - 50, hoy.Formas.Single(f => f.Forma == FormaPago.Efectivo).Centavos);
            Assert.Equal(corte.TarjetaCentavos, hoy.Formas.Single(f => f.Forma == FormaPago.Tarjeta).Centavos);
        }
        // Y como el reporte de esos días, sin lo del historial de eleventa (que aquí no se lista).
        var semana = await s.Tienda.ReporteAsync(PeriodoReporte.Semana);
        var a = semana.Actual;
        Assert.Equal((a.Tickets - a.DelHistorialTickets, a.VendidoCentavos - a.DelHistorialCentavos, a.DevueltoCentavos, a.Cancelados),
            (d.Totales.Tickets, d.Totales.VendidoCentavos, d.Totales.DevueltoCentavos, d.Totales.Cancelados));
        Assert.Equal(semana.FormasDePago.Sum(f => f.Centavos), d.Totales.VentaCentavos);
    }

    [Fact]
    public async Task De_una_caja_solo_sus_tickets_y_lo_devuelto_de_ellos()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var deLaUno = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy, s.Tienda.Caja.Id);
        var deLaDos = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy, s.Caja2.Id);

        Assert.Equal(7, deLaUno.Total);
        Assert.All(deLaUno.Ventas, v => Assert.Equal("Caja 1", v.Caja));
        // La pluma devuelta es de un ticket de la Caja 1: se resta ahí.
        Assert.Equal((6, 19000L, 1000L, 1), (deLaUno.Totales.Tickets, deLaUno.Totales.VendidoCentavos, deLaUno.Totales.DevueltoCentavos, deLaUno.Totales.Cancelados));
        Assert.Equal(["B-000007"], deLaDos.Ventas.Select(v => v.Folio));
        Assert.Equal((1, 2500L, 0L, 0, s.Caja2.Id), (deLaDos.Totales.Tickets, deLaDos.Totales.VendidoCentavos, deLaDos.Totales.DevueltoCentavos,
            deLaDos.Totales.Cancelados, deLaDos.CajaId!.Value));
        // Las dos cajas suman lo de todas.
        var todas = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 19), Hoy);
        Assert.Equal(todas.Totales.VentaCentavos, deLaUno.Totales.VentaCentavos + deLaDos.Totales.VentaCentavos);
    }

    [Fact]
    public async Task Los_dias_son_los_de_la_tienda_de_medianoche_a_medianoche()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        // Sin fechas: hoy. Solo hoy: sin la goma de las 23:59:30 del martes (en UTC, ya es miércoles).
        var hoy = await PorPeriodoAsync(s.Tienda, null, null);
        var martes = await PorPeriodoAsync(s.Tienda, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20));

        Assert.Equal((Hoy, Hoy), (hoy.Desde, hoy.Hasta));
        Assert.DoesNotContain(hoy.Ventas, v => v.Id == s.GomaDeMedianoche.Id);
        Assert.Equal(5, hoy.Total);
        Assert.Equal([s.DePlumas.Id, s.GomaDeMedianoche.Id], martes.Ventas.Select(v => v.Id));
        Assert.Equal(new DateOnly(2026, 10, 20), martes.Dias.Single().Fecha);
        // Lo que se pide más allá de hoy llega hasta hoy.
        var hastaManana = await PorPeriodoAsync(s.Tienda, Hoy, Hoy.AddDays(3));
        Assert.Equal(Hoy, hastaManana.Hasta);
    }

    [Fact]
    public async Task Con_el_horario_de_verano_de_antes_de_2022_las_once_y_media_de_la_noche_son_de_ese_dia()
    {
        await using var t = await TiendaDeReportes.CrearAsync(Ahora);
        var turno = await t.TurnoAsync(t.Ana, A(2021, 6, 15, 9), A(2021, 6, 16, 1));
        // El 15 de junio de 2021 la tienda estaba en UTC−5: las 23:30 son las 4:30 del 16 en UTC.
        var noche = await t.VenderAsync(turno, A(2021, 6, 15, 23, 30), [(t.Pluma, 1)]);
        var madrugada = await t.VenderAsync(turno, A(2021, 6, 16, 0, 30), [(t.Pluma, 2)]);

        var dia = await PorPeriodoAsync(t, new DateOnly(2021, 6, 15), new DateOnly(2021, 6, 15));
        var dos = await PorPeriodoAsync(t, new DateOnly(2021, 6, 15), new DateOnly(2021, 6, 16));

        Assert.Equal([noche.Id], dia.Ventas.Select(v => v.Id));
        Assert.Equal((1, 1000L), (dia.Totales.Tickets, dia.Totales.VendidoCentavos));
        Assert.Equal([(new DateOnly(2021, 6, 15), 1000L), (new DateOnly(2021, 6, 16), 2000L)], dos.Dias.Select(d => (d.Fecha!.Value, d.VendidoCentavos)));
        Assert.Equal(madrugada.Id, dos.Ventas[^1].Id);
    }

    [Fact]
    public async Task La_pantalla_va_de_100_en_100_y_el_excel_los_lleva_todos()
    {
        await using var t = await TiendaDeReportes.CrearAsync(Ahora);
        var turno = await t.TurnoAsync(t.Ana, A(2026, 10, 20, 8));
        // Ciento cincuenta tickets ayer, uno por minuto desde las 9.
        for (var i = 0; i < 150; i++)
            await t.VenderAsync(turno, A(2026, 10, 20, 9).AddMinutes(i), [(t.Pluma, 1)]);

        var primera = await PorPeriodoAsync(t, Hoy.AddDays(-1), Hoy);
        var segunda = await PorPeriodoAsync(t, Hoy.AddDays(-1), Hoy, pagina: 1);
        var lejos = await PorPeriodoAsync(t, Hoy.AddDays(-1), Hoy, pagina: 9);
        await using var db = t.Contexto();
        var todo = await new ReporteVentasPorPeriodo(db).TodoAsync(t.Caja.Id, t.Luis.Id, Hoy.AddDays(-1), Hoy, null);

        Assert.Equal((150, 0, 100, 100), (primera.Total, primera.Pagina, primera.Tamano, primera.Ventas.Count));
        Assert.Equal((1, 50), (segunda.Pagina, segunda.Ventas.Count));
        Assert.Empty(lejos.Ventas);
        // Del primero al último, sin repetir ni saltar a nadie entre páginas.
        var folios = primera.Ventas.Concat(segunda.Ventas).Select(v => v.Folio).ToList();
        Assert.Equal(Enumerable.Range(1, 150).Select(n => $"A-{n:000000}"), folios);
        Assert.Equal(folios, todo.Ventas.Select(v => v.Folio));
        // Los totales son de todos, en cada página.
        Assert.All([primera, segunda, todo], x => Assert.Equal((150, 150_000L), (x.Totales.Tickets, x.Totales.VendidoCentavos)));
        // El Excel lleva hasta un tope (60,000 en la tienda): con más, se pide elegir menos días.
        var demasiados = await Assert.ThrowsAsync<ArgumentException>(() => new ReporteVentasPorPeriodo(db).TodoAsync(t.Caja.Id, t.Luis.Id, Hoy.AddDays(-1), Hoy,
            null, maximo: 100));
        Assert.Equal("Son 150 tickets, y el Excel lleva hasta 100: elige menos días o una caja.", demasiados.Message);
    }

    [Fact]
    public async Task En_el_piloto_sin_el_historial_de_eleventa()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var d = await PorPeriodoAsync(s.Tienda, Hoy, Hoy, piloto: true);

        Assert.True(d.Piloto);
        Assert.Equal((0, 0L), (d.DelHistorialTickets, d.DelHistorialCentavos));
        // Lo de este POS, igual.
        Assert.Equal((4, 13250L), (d.Totales.Tickets, d.Totales.VendidoCentavos));
    }

    [Fact]
    public async Task Solo_con_ver_reportes_de_ventas_y_lo_que_no_se_entiende_se_dice()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;
        var t = s.Tienda;

        // La cajera no tiene «Ver reportes de ventas»: no lo ve, y se dice quién sí.
        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => PorPeriodoAsync(t, Hoy, Hoy, quien: t.Ana));
        Assert.Contains("«Ver reportes de ventas»", sinPermiso.Message);
        // «Con autorización» tampoco: consultar no se autoriza.
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.RequiereAutorizacion);
        await Assert.ThrowsAsync<PermisoRequerido>(() => PorPeriodoAsync(t, Hoy, Hoy, quien: t.Carmen));
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.Permitido);
        Assert.Equal(5, (await PorPeriodoAsync(t, Hoy, Hoy, quien: t.Carmen)).Total);

        Assert.Equal(ReporteVentasPorPeriodo.TodaviaNoLlega, (await Assert.ThrowsAsync<ArgumentException>(() => PorPeriodoAsync(t, Hoy.AddDays(1), null))).Message);
        Assert.Equal(ReporteVentasPorPeriodo.AlReves, (await Assert.ThrowsAsync<ArgumentException>(() => PorPeriodoAsync(t, Hoy, Hoy.AddDays(-1)))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => PorPeriodoAsync(t, Hoy, Hoy, Guid.NewGuid()));
    }
}
