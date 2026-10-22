using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// «Ventas por período» (entrega 9, parte 2, 2026-10-22), como la de eleventa (F3 › Ventas por período: las fechas y la caja; a
/// Excel o a la impresora de tickets): los tickets de una fecha a otra, de una caja o de todas, cada uno con su folio, la fecha
/// y la hora, la caja, quién cobró, el total, cómo se pagó y si se canceló o se devolvió; y los totales de todos esos días, por
/// día y por forma de pago. Para quien tiene «Ver reportes de ventas» en «Puede» (consultar no se autoriza). Solo lee.
/// <para>
/// Cuenta como los reportes y el corte del día (<see cref="ReportesDeVentas"/>): un ticket que se canceló se lista, pero no
/// cuenta (en el día en que se cobró); lo devuelto en esos días se resta de lo vendido, sea de un ticket de esos días o de
/// antes (de la caja que se ve: la del ticket). Cada forma de pago es lo cobrado así (el efectivo, sin el redondeo al peso), así
/// que suman lo vendido.
/// </para>
/// <para>
/// Solo los tickets de este POS: el historial de eleventa no trae caja, cajero ni forma de pago, así que no se lista; se dice
/// cuánto hay de él en esos días (cuenta en los reportes). En el piloto con eleventa la base de la caja 2 solo tiene lo suyo, y
/// el historial (de toda la tienda) no se cuenta (decidido el 2026-10-22, por revisar).
/// </para>
/// <para>
/// Rápido con un mes de unos 1,800 tickets: los totales se suman en la base, por hora (UTC: cada una es una sola hora de la
/// tienda, y aquí se juntan por día), y la pantalla trae los tickets de <see cref="TicketsPorPagina"/> en
/// <see cref="TicketsPorPagina"/>, por el índice de la fecha de cobro. El Excel los trae todos.
/// </para>
/// </summary>
public sealed class ReporteVentasPorPeriodo(PosDbContext db)
{
    /// <summary>Cuántos tickets trae cada página de la pantalla.</summary>
    public const int TicketsPorPagina = 100;

    /// <summary>Hasta dónde se puede pedir una página: más allá ya no hay nada (y la cuenta no se desborda).</summary>
    private const int UltimaPagina = 1_000_000;

    /// <summary>Los tickets se cargan de tantos en tantos para el Excel.</summary>
    private const int DeAUnaVez = 1000;

    public const string TodaviaNoLlega = "Esos días todavía no llegan: el reporte es de hoy o de días que ya pasaron.";

    public const string AlReves = "El primer día va antes que el último.";

    /// <summary>
    /// Una página de los tickets de <paramref name="desde"/> a <paramref name="hasta"/> (vacíos: hoy; el último, si pasa de hoy,
    /// llega hasta hoy) de <paramref name="deCaja"/> (vacía: todas), con los totales de todos esos días, vista desde
    /// <paramref name="cajaId"/> por <paramref name="usuarioId"/>, quien entró a esa caja.
    /// </summary>
    /// <param name="piloto">El piloto con eleventa: sin el historial de eleventa, que es de toda la tienda.</param>
    public Task<VentasPorPeriodoDto> ArmarAsync(
        Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? deCaja, int pagina = 0, bool piloto = false, CancellationToken ct = default) =>
        LeerAsync(cajaId, usuarioId, desde, hasta, deCaja, Math.Clamp(pagina, 0, UltimaPagina), piloto, ct);

    /// <summary>
    /// Lo más que lleva el Excel: unos dos años de la tienda. Armarlo tarda poco (un año, medio segundo), pero mientras se arma
    /// cada celda ocupa memoria del servicio de la caja principal.
    /// </summary>
    public const int MaximoTicketsEnExcel = 60_000;

    /// <summary>Lo mismo, con todos los tickets de esos días en una sola lista (para el Excel), hasta <paramref name="maximo"/>.</summary>
    public Task<VentasPorPeriodoDto> TodoAsync(
        Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? deCaja, bool piloto = false, int maximo = MaximoTicketsEnExcel,
        CancellationToken ct = default) =>
        LeerAsync(cajaId, usuarioId, desde, hasta, deCaja, pagina: null, piloto, ct, maximo);

    private async Task<VentasPorPeriodoDto> LeerAsync(
        Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? deCaja, int? pagina, bool piloto, CancellationToken ct,
        int maximo = MaximoTicketsEnExcel)
    {
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var sucursal = caja.SucursalId;
        // Consultar no se autoriza: «Puede», o no se ve (también dice si quien entró sigue activo).
        await new ServicioControl(db).ExigirParaVerAsync(usuarioId, sucursal, AccionControlada.VerReportes, ct);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
        var primero = desde ?? hasta ?? hoy;
        var ultimo = hasta ?? (desde is null ? hoy : primero);
        if (primero > hoy)
            throw new ArgumentException(TodaviaNoLlega);
        if (ultimo < primero)
            throw new ArgumentException(AlReves);
        if (ultimo > hoy)
            ultimo = hoy;
        var cajas = await VentasDeOtrosDias.CajasDeAsync(db, sucursal, ct);
        if (deCaja is { } elegida && cajas.All(c => c.Id != elegida))
            throw new ArgumentException("Esa caja no es de esta tienda.");

        var rango = new Rango(HoraMexico.AUtc(primero.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(ultimo.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        return await ReportesDeVentas.EnUnMismoMomentoAsync(db, async () =>
        {
            var dias = await PorDiaAsync(sucursal, rango, deCaja, ct);
            var totales = Sumar(null, dias);
            var listados = totales.Tickets + totales.Cancelados;
            if (pagina is null && listados > maximo)
                throw new ArgumentException(
                    $"Son {listados.ToString("N0", CultureInfo.GetCultureInfo("es-MX"))} tickets, y el Excel lleva hasta "
                    + $"{maximo.ToString("N0", CultureInfo.GetCultureInfo("es-MX"))}: elige menos días o una caja.");
            var (historialTickets, historialCentavos) = piloto ? (0, 0L) : await HistorialAsync(sucursal, rango, ct);
            var tamano = pagina is null ? Math.Max(listados, 1) : TicketsPorPagina;
            var ventas = await TicketsAsync(sucursal, rango, deCaja, pagina is { } p ? (p * TicketsPorPagina, TicketsPorPagina) : null, ct);
            return new VentasPorPeriodoDto(primero, ultimo, hoy, deCaja, cajas, ventas, listados, pagina ?? 0, tamano, totales, dias, historialTickets,
                historialCentavos, piloto);
        }, ct);
    }

    /// <summary>Desde, hasta (sin incluirlo), en UTC.</summary>
    private sealed record Rango(DateTime DesdeUtc, DateTime HastaUtc);

    // ── Los tickets ──

    // Por el índice de la fecha de cobro, también en el orden: con «+» la sucursal y la caja no usan los suyos (sin estadísticas,
    // la base prefiere un índice que empieza por la sucursal, que con una sola recorre todo; el de la caja recorre todos sus días).
    private const string TicketsSql = """
        SELECT v."Id" AS "Id", v."Serie" AS "Serie", v."Folio" AS "Folio", v."PagadaUtc" AS "PagadaUtc", v."CajaId" AS "CajaId",
               v."CajeroId" AS "CajeroId", v."TotalCentavos" AS "TotalCentavos", v."Estado" AS "Estado"
        FROM "Ventas" AS v
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" IN ('Pagada', 'Cancelada')
          AND v."Folio" IS NOT NULL {3}
        ORDER BY v."PagadaUtc", v."Folio", v."Id"
        """;

    // Lo demás de cada ticket, de varios a la vez (sus Id van en una lista de JSON, como los manda EF: en mayúsculas, como se
    // guardan), por el índice de la venta en cada tabla, sin traer cada renglón.

    /// <summary>Sus renglones: cuántos, cuántos ya se devolvieron completos y los artículos (piezas; lo de metro o granel, uno).</summary>
    private const string RenglonesSql = """
        SELECT l."VentaId" AS "VentaId", COUNT(*) AS "Renglones", SUM(CASE WHEN l."CantidadDevuelta" >= l."Cantidad" THEN 1 ELSE 0 END) AS "Devueltos",
               SUM(CASE WHEN l."Cantidad" % 1000 = 0 THEN l."Cantidad" / 1000 ELSE 1 END) AS "Articulos"
        FROM "VentasLineas" AS l
        WHERE l."VentaId" IN (SELECT value FROM json_each({0}))
        GROUP BY l."VentaId"
        """;

    /// <summary>Cómo se pagó (lo que cubrió algo).</summary>
    private const string FormasDelTicketSql = """
        SELECT c."VentaId" AS "VentaId", c."Forma" AS "Forma"
        FROM "Cobros" AS c
        WHERE c."VentaId" IN (SELECT value FROM json_each({0})) AND c."MontoCentavos" > 0
        GROUP BY c."VentaId", c."Forma"
        """;

    /// <summary>Lo que se devolvió de cada uno (sin las cancelaciones), cuando sea.</summary>
    private const string DevueltoSql = """
        SELECT d."VentaId" AS "VentaId", SUM(d."TotalCentavos") AS "Centavos"
        FROM "Devoluciones" AS d
        WHERE d."VentaId" IN (SELECT value FROM json_each({0})) AND d."EsCancelacion" = 0
        GROUP BY d."VentaId"
        """;

    private sealed record Ticket(Guid Id, string Serie, int Folio, DateTime PagadaUtc, Guid CajaId, Guid CajeroId, long TotalCentavos, string Estado);

    private sealed record RenglonesDelTicket(Guid VentaId, long Renglones, long Devueltos, long Articulos);

    private sealed record FormaDelTicket(Guid VentaId, string Forma);

    private sealed record DevueltoDelTicket(Guid VentaId, long Centavos);

    /// <summary>La condición de la caja, si se eligió una (su valor va en el lugar <paramref name="lugar"/>).</summary>
    private static string DeLaCaja(Guid? caja, int lugar, string tabla = "v") => caja is null ? "" : $"AND +{tabla}.\"CajaId\" = {{{lugar}}}";

    private static object[] Parametros(Guid sucursal, Rango r, Guid? caja, params object[] demas) =>
        caja is { } c ? [sucursal, r.DesdeUtc, r.HastaUtc, c, .. demas] : [sucursal, r.DesdeUtc, r.HastaUtc, .. demas];

    /// <summary>
    /// Los tickets de esos días del primero al último, como en la lista de «Ventas del día» (su folio, el de eleventa en el piloto,
    /// la caja, quién cobró, cómo se pagó y si se canceló o se devolvió, con las mismas reglas que
    /// <see cref="VentasDeOtrosDias"/>); con <paramref name="pagina"/>, solo esos. Un año entero, sin traer cada renglón: con la
    /// papelería ficticia, unos 20 mil tickets se leían en 3.5 s trayendo cada venta con sus renglones.
    /// </summary>
    private async Task<List<VentaDeUnDiaDto>> TicketsAsync(Guid sucursal, Rango r, Guid? caja, (int Saltar, int Tomar)? pagina, CancellationToken ct)
    {
        // Los lugares de la página van después de los de la caja.
        var lugar = caja is null ? 3 : 4;
        var sql = ConCaja(TicketsSql, caja) + (pagina is null ? "" : $"\nLIMIT {{{lugar}}} OFFSET {{{lugar + 1}}}");
        var parametros = pagina is { } p ? Parametros(sucursal, r, caja, p.Tomar, p.Saltar) : Parametros(sucursal, r, caja);
        var tickets = await db.Database.SqlQueryRaw<Ticket>(sql, parametros).ToListAsync(ct);
        if (tickets.Count == 0)
            return [];
        var cajas = await db.Cajas.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var personas = tickets.Select(t => t.CajeroId).Distinct().ToList();
        var nombres = await db.Usuarios.AsNoTracking().Where(u => personas.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;
        var filas = new List<VentaDeUnDiaDto>(tickets.Count);
        foreach (var grupo in tickets.Chunk(DeAUnaVez))
        {
            var ids = grupo.Select(t => t.Id).ToList();
            var lista = JsonSerializer.Serialize(ids.Select(id => id.ToString().ToUpperInvariant()));
            var renglones = (await db.Database.SqlQueryRaw<RenglonesDelTicket>(RenglonesSql, lista).ToListAsync(ct)).ToDictionary(x => x.VentaId);
            var formas = (await db.Database.SqlQueryRaw<FormaDelTicket>(FormasDelTicketSql, lista).ToListAsync(ct))
                .ToLookup(x => x.VentaId, x => Enum.Parse<FormaPago>(x.Forma));
            var devuelto = (await db.Database.SqlQueryRaw<DevueltoDelTicket>(DevueltoSql, lista).ToListAsync(ct)).ToDictionary(x => x.VentaId, x => x.Centavos);
            var conVenta = ids.Select(id => (Guid?)id).ToList();
            var folios = (await db.EnviosAEleventa.AsNoTracking()
                    .Where(e => e.Tipo == TipoEnvioEleventa.Venta && conVenta.Contains(e.VentaId) && e.FolioEleventa != null)
                    .Select(e => new { e.VentaId, e.FolioEleventa }).ToListAsync(ct))
                .GroupBy(e => e.VentaId!.Value).ToDictionary(g => g.Key, g => g.First().FolioEleventa);
            filas.AddRange(grupo.Select(t =>
            {
                var deLosRenglones = renglones.GetValueOrDefault(t.Id);
                return new VentaDeUnDiaDto(t.Id, $"{t.Serie}-{t.Folio:000000}", folios.GetValueOrDefault(t.Id), HoraMexico.ALocal(t.PagadaUtc), t.CajaId,
                    cajas.GetValueOrDefault(t.CajaId, "Caja"), t.CajeroId, nombres.GetValueOrDefault(t.CajeroId, "—"), t.TotalCentavos,
                    (int)(deLosRenglones?.Articulos ?? 0), formas[t.Id].Distinct().Order().ToList(), t.Estado == nameof(EstadoVenta.Cancelada),
                    devuelto.GetValueOrDefault(t.Id), deLosRenglones is { Renglones: > 0 } x && x.Devueltos == x.Renglones,
                    // Como la ventana de la devolución: le queda alguno de sus días para devolver.
                    ahora - t.PagadaUtc < TimeSpan.FromDays(Devolucion.DiasParaDevolver));
            }));
        }
        return filas;
    }

    // ── Los totales: por hora en la base, por día aquí ──

    /// <summary>Lo cobrado por hora (UTC): los tickets, lo vendido, el redondeo al peso y los cancelados.</summary>
    private const string VentasPorHoraSql = """
        SELECT substr(v."PagadaUtc", 1, 13) AS "Hora",
               SUM(CASE WHEN v."Estado" = 'Pagada' THEN 1 ELSE 0 END) AS "Tickets",
               SUM(CASE WHEN v."Estado" = 'Pagada' THEN v."TotalCentavos" ELSE 0 END) AS "Centavos",
               SUM(CASE WHEN v."Estado" = 'Pagada' THEN v."RedondeoCentavos" ELSE 0 END) AS "Redondeo",
               SUM(CASE WHEN v."Estado" = 'Cancelada' THEN 1 ELSE 0 END) AS "Cancelados",
               SUM(CASE WHEN v."Estado" = 'Cancelada' THEN v."TotalCentavos" ELSE 0 END) AS "CanceladosCentavos"
        FROM "Ventas" AS v
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" IN ('Pagada', 'Cancelada')
          AND v."Folio" IS NOT NULL {3}
        GROUP BY substr(v."PagadaUtc", 1, 13)
        """;

    /// <summary>Cómo se pagaron, por hora, los tickets que no se cancelaron.</summary>
    private const string FormasPorHoraSql = """
        SELECT substr(v."PagadaUtc", 1, 13) AS "Hora", c."Forma" AS "Forma", SUM(c."MontoCentavos") AS "Centavos"
        FROM "Cobros" AS c
        INNER JOIN "Ventas" AS v ON v."Id" = c."VentaId"
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" = 'Pagada' AND v."Folio" IS NOT NULL {3}
        GROUP BY substr(v."PagadaUtc", 1, 13), c."Forma"
        """;

    /// <summary>Las devoluciones de esos días, sin las cancelaciones (el ticket cancelado ya no está en lo vendido), por hora.</summary>
    private const string DevolucionesPorHoraSql = """
        SELECT substr(d."FechaUtc", 1, 13) AS "Hora", COUNT(*) AS "Devoluciones", SUM(d."TotalCentavos") AS "Centavos"
        FROM "Devoluciones" AS d
        INNER JOIN "Ventas" AS v ON v."Id" = d."VentaId"
        WHERE +d."SucursalId" = {0} AND d."FechaUtc" >= {1} AND d."FechaUtc" < {2} AND d."EsCancelacion" = 0 {3}
        GROUP BY substr(d."FechaUtc", 1, 13)
        """;

    /// <summary>El historial de eleventa de esos días, sin los cancelados: solo cuánto hay.</summary>
    private const string HistorialSql = """
        SELECT COUNT(*) AS "Tickets", COALESCE(SUM(h."TotalCentavos"), 0) AS "Centavos"
        FROM "VentasHistoricas" AS h
        WHERE +h."SucursalId" = {0} AND h."FechaUtc" >= {1} AND h."FechaUtc" < {2} AND h."Cancelada" = 0
        """;

    private sealed record VentasPorHora(string Hora, long Tickets, long Centavos, long Redondeo, long Cancelados, long CanceladosCentavos);

    private sealed record FormaPorHora(string Hora, string Forma, long Centavos);

    private sealed record DevolucionesPorHora(string Hora, long Devoluciones, long Centavos);

    private sealed record DelHistorial(long Tickets, long Centavos);

    /// <summary>Lo de un día, mientras se suma.</summary>
    private sealed class DeUnDia
    {
        public long Tickets, Vendido, Redondeo, Cancelados, CanceladosCentavos, Devoluciones, Devuelto;
        public readonly Dictionary<FormaPago, long> Formas = [];
    }

    /// <summary>La consulta con la condición de la caja en su lugar ({3}), o sin ella.</summary>
    private static string ConCaja(string sql, Guid? caja) => sql.Replace("{3}", DeLaCaja(caja, 3));

    /// <summary>Lo de cada día con algo (tickets, devoluciones o cancelados), en orden.</summary>
    private async Task<List<ResumenDeVentasDto>> PorDiaAsync(Guid sucursal, Rango r, Guid? caja, CancellationToken ct)
    {
        var dias = new SortedDictionary<DateOnly, DeUnDia>();
        DeUnDia De(string hora)
        {
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(hora.Replace('T', ' '), "yyyy-MM-dd HH", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var dia = DateOnly.FromDateTime(HoraMexico.ALocal(utc));
            if (!dias.TryGetValue(dia, out var de))
                dias[dia] = de = new DeUnDia();
            return de;
        }
        var parametros = Parametros(sucursal, r, caja);
        foreach (var v in await db.Database.SqlQueryRaw<VentasPorHora>(ConCaja(VentasPorHoraSql, caja), parametros).ToListAsync(ct))
        {
            var de = De(v.Hora);
            de.Tickets += v.Tickets;
            de.Vendido += v.Centavos;
            de.Redondeo += v.Redondeo;
            de.Cancelados += v.Cancelados;
            de.CanceladosCentavos += v.CanceladosCentavos;
        }
        foreach (var f in await db.Database.SqlQueryRaw<FormaPorHora>(ConCaja(FormasPorHoraSql, caja), parametros).ToListAsync(ct))
        {
            var forma = Enum.Parse<FormaPago>(f.Forma);
            var de = De(f.Hora);
            de.Formas[forma] = de.Formas.GetValueOrDefault(forma) + f.Centavos;
        }
        foreach (var d in await db.Database.SqlQueryRaw<DevolucionesPorHora>(ConCaja(DevolucionesPorHoraSql, caja), parametros).ToListAsync(ct))
        {
            var de = De(d.Hora);
            de.Devoluciones += d.Devoluciones;
            de.Devuelto += d.Centavos;
        }
        return dias.Select(d =>
        {
            var de = d.Value;
            // El efectivo, sin el redondeo al peso: así las formas suman lo vendido, como el ticket.
            if (de.Redondeo != 0)
                de.Formas[FormaPago.Efectivo] = de.Formas.GetValueOrDefault(FormaPago.Efectivo) - de.Redondeo;
            return new ResumenDeVentasDto(d.Key, (int)de.Tickets, de.Vendido, (int)de.Devoluciones, de.Devuelto, (int)de.Cancelados, de.CanceladosCentavos,
                Formas(de.Formas));
        }).ToList();
    }

    /// <summary>Las formas de pago con algo, en el orden de siempre (lo fiado al final).</summary>
    private static List<FormaPagoReporteDto> Formas(IEnumerable<KeyValuePair<FormaPago, long>> formas) =>
        formas.Where(f => f.Value != 0).OrderBy(f => f.Key).Select(f => new FormaPagoReporteDto(f.Key, f.Value)).ToList();

    /// <summary>Los totales de esos días: la suma de cada día.</summary>
    internal static ResumenDeVentasDto Sumar(DateOnly? fecha, IReadOnlyCollection<ResumenDeVentasDto> dias) =>
        new(fecha, dias.Sum(d => d.Tickets), dias.Sum(d => d.VendidoCentavos), dias.Sum(d => d.Devoluciones), dias.Sum(d => d.DevueltoCentavos),
            dias.Sum(d => d.Cancelados), dias.Sum(d => d.CanceladosCentavos),
            Formas(dias.SelectMany(d => d.Formas).GroupBy(f => f.Forma).Select(g => KeyValuePair.Create(g.Key, g.Sum(f => f.Centavos)))));

    private async Task<(int Tickets, long Centavos)> HistorialAsync(Guid sucursal, Rango r, CancellationToken ct)
    {
        var h = (await db.Database.SqlQueryRaw<DelHistorial>(HistorialSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct)).Single();
        return ((int)h.Tickets, h.Centavos);
    }
}
