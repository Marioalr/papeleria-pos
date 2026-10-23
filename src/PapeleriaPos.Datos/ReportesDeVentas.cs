using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Reportes;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los reportes de ventas (entrega 9, parte 1, 2026-10-22; docs/propuesta-interfaz.md «Reportes»), los que el dueño usa en
/// eleventa: hoy, la semana, el mes y el año a la fecha, siempre contra el mismo periodo del año pasado
/// (<see cref="PeriodoDeReporte"/>). Para quien tiene «Ver reportes de ventas» en «Puede» (consultar no se autoriza); costos,
/// utilidad y margen, solo con «Ver costos, utilidad y margen». Solo leen.
/// <para>
/// Cuentan como el corte del día: la venta neta es lo cobrado en los tickets que no se cancelaron menos lo devuelto en el
/// periodo, y la utilidad, lo vendido menos el costo que guardó cada renglón al cobrar, con lo devuelto restado y sin el
/// artículo común (<see cref="VendidoPorDepartamento"/>, el mismo código que el corte). Las cancelaciones que se cuentan son
/// las que se hicieron en el periodo, como en el corte del día (revisión del PR #131, 2026-10-22). Lo de este POS se suma con
/// el historial de eleventa (<see cref="Nucleo.Historico.VentaHistorica"/>): sus tickets sin los cancelados, con lo que
/// eleventa les bajó al devolver (lo devuelto cuenta el día de la venta), y la utilidad con el costo que se capturó allá; su
/// artículo común, como el de aquí (aparte y sin utilidad), y lo que el total de un ticket tiene de más o de menos contra sus
/// artículos, «Sin desglose». La importación final ya no trae lo que mandó el puente (2026-10-17), así que nada cuenta dos
/// veces. El historial no trae ni el cajero ni la forma de pago: esas tablas son de lo vendido aquí.
/// </para>
/// <para>
/// En el piloto con eleventa solo está lo que vendió la caja 2 (lo de la caja principal está en eleventa): el reporte es de lo
/// suyo, sin el historial (es de toda la tienda) y sin comparar contra el año pasado (decidido el 2026-10-22, por revisar).
/// </para>
/// <para>
/// Se suma en la base, no en memoria: por hora (UTC) lo de cada día, y por variante lo de cada renglón; aquí solo se acomoda lo
/// que regresa (a lo más unas 9 mil horas y un renglón por producto en un año). Cada hora UTC es una sola hora de la tienda (la
/// zona de México cambia de hora en horas completas), así que pasarlas a la hora de la tienda no parte ninguna.
/// </para>
/// </summary>
public sealed class ReportesDeVentas(PosDbContext db)
{
    /// <summary>Cuántos salen en «Lo que más se vende».</summary>
    public const int MasVendidos = 10;

    public const string TodaviaNoLlega = "Ese periodo todavía no llega: el reporte es del periodo de hoy o de uno que ya pasó.";

    /// <summary>
    /// El reporte de <paramref name="tipo"/> del día <paramref name="fecha"/> (vacío: hoy), visto desde <paramref name="cajaId"/>
    /// por <paramref name="usuarioId"/>, quien entró a esa caja.
    /// </summary>
    /// <param name="piloto">El piloto con eleventa: solo lo de esta caja, sin el historial ni el año pasado.</param>
    public async Task<ReporteVentasDto> ArmarAsync(
        Guid cajaId, Guid usuarioId, PeriodoReporte tipo, DateOnly? fecha = null, bool piloto = false, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(tipo))
            throw new ArgumentException("No hay ese periodo: hoy, la semana, el mes o el año.");
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var sucursal = caja.SucursalId;
        var control = new ServicioControl(db);
        // Consultar no se autoriza: «Puede», o no se ve (también dice si quien entró sigue activo).
        await control.ExigirParaVerAsync(usuarioId, sucursal, AccionControlada.VerReportes, ct);
        var conUtilidad = await control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        var ahora = HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime);
        var dia = fecha ?? DateOnly.FromDateTime(ahora);
        if (dia > DateOnly.FromDateTime(ahora))
            throw new ArgumentException(TodaviaNoLlega);
        var p = PeriodoDeReporte.De(tipo, dia, ahora);
        return await EnUnMismoMomentoAsync(db, () => LeerAsync(sucursal, p, DateOnly.FromDateTime(ahora), conUtilidad, piloto, ct), ct);
    }

    /// <summary>
    /// Todo se lee de un mismo momento de la base: una lectura diferida (sin el candado de escritura, así que no detiene ningún
    /// cobro), con la conexión abierta para todas las sumas. Lo que se cobre mientras tanto no cambia unas cifras sí y otras no:
    /// la venta neta, los departamentos y las formas de pago suman lo mismo. También lo usan las ventas por período.
    /// </summary>
    internal static async Task<T> EnUnMismoMomentoAsync<T>(PosDbContext db, Func<Task<T>> leer, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var lectura = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(lectura, ct);
            try
            {
                return await leer();
            }
            finally
            {
                await db.Database.UseTransactionAsync(null, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<ReporteVentasDto> LeerAsync(Guid sucursal, PeriodoDeReporte p, DateOnly hoy, bool conUtilidad, bool piloto, CancellationToken ct)
    {
        var conHistorial = !piloto;

        // El periodo, completo: si es a la fecha, después de ahora no hay nada (y lo que una caja cobró con su reloj un poco
        // adelantado también cuenta).
        var actual = Rango.De(p.Desde, p.Hasta);
        var horas = await PorHoraAsync(sucursal, actual, conHistorial, ct);
        var renglones = await RenglonesAsync(sucursal, actual, conHistorial, ct);

        // El año pasado: la gráfica con el periodo completo; las cifras, hasta el mismo momento. Sin nada en todo ese periodo,
        // no hay con qué comparar.
        Dictionary<DateTime, DeUnaHora>? horasAnterior = null, horasAlCorte = null;
        List<Renglon>? renglonesAnterior = null;
        if (!piloto)
        {
            var completo = Rango.De(p.DesdeAnterior, p.HastaAnterior);
            horasAnterior = await PorHoraAsync(sucursal, completo, conHistorial, ct);
            if (horasAnterior.Count == 0)
                horasAnterior = null;
            else
            {
                var alCorte = p.CorteAnterior is { } corte ? new Rango(completo.DesdeUtc, HoraMexico.AUtc(corte)) : completo;
                horasAlCorte = p.ALaFecha ? await PorHoraAsync(sucursal, alCorte, conHistorial, ct) : horasAnterior;
                renglonesAnterior = await RenglonesAsync(sucursal, alCorte, conHistorial, ct);
            }
        }

        var catalogo = await CatalogoAsync(renglones.Concat(renglonesAnterior ?? []), ct);
        var (porDepartamento, utilidad) = VendidoPorDepartamento.Armar(renglones.Select(r => r.Vendido).ToList(), catalogo.DepartamentoDe,
            catalogo.Departamentos, conUtilidad);
        (VentasPorDepartamentoDto PorDepartamento, UtilidadDelDiaDto Utilidad)? anterior = renglonesAnterior is null ? null
            : VendidoPorDepartamento.Armar(renglonesAnterior.Select(r => r.Vendido).ToList(), catalogo.DepartamentoDe, catalogo.Departamentos, conUtilidad);
        var sinDesglose = SinDesglose(horas.Values, renglones);
        long? sinDesgloseAnterior = horasAlCorte is null || renglonesAnterior is null ? null : SinDesglose(horasAlCorte.Values, renglonesAnterior);

        var cifras = Cifras(horas.Values, conUtilidad ? utilidad : null);
        var cifrasAnteriores = horasAlCorte is null || anterior is not { } a ? null : Cifras(horasAlCorte.Values, conUtilidad ? a.Utilidad : null);
        return new ReporteVentasDto(p.Tipo, p.Desde, p.Hasta.AddDays(-1), hoy, p.Ahora, p.DesdeAnterior, p.HastaAnterior.AddDays(-1),
            p.CorteAnterior, cifras, cifrasAnteriores, await SerieAsync(sucursal, p, horas, horasAnterior, ct),
            Departamentos(porDepartamento, anterior?.PorDepartamento, conUtilidad, sinDesglose, sinDesgloseAnterior), await CajerosAsync(sucursal, actual, ct),
            await FormasDePagoAsync(sucursal, actual, horas.Values, ct), LoMasVendido(renglones, catalogo), conUtilidad, piloto);
    }

    /// <summary>
    /// Del historial de eleventa, lo que sus tickets suman y sus artículos no (un total que no es la suma de sus artículos: la
    /// importación lo avisa). Va en la venta neta y, en las ventas por departamento, aparte: así suman la venta neta.
    /// </summary>
    private static long SinDesglose(IEnumerable<DeUnaHora> horas, IEnumerable<Renglon> renglones) =>
        horas.Sum(h => h.Historial) - renglones.Where(r => r.DelHistorial).Sum(r => r.Vendido.ImporteCentavos);

    // ── Las sumas en la base ──

    /// <summary>Desde, hasta (sin incluirlo), en UTC.</summary>
    private sealed record Rango(DateTime DesdeUtc, DateTime HastaUtc)
    {
        /// <summary>De la medianoche de <paramref name="desde"/> a la de <paramref name="hasta"/>, en hora de la tienda.</summary>
        public static Rango De(DateOnly desde, DateOnly hasta) =>
            new(HoraMexico.AUtc(desde.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(hasta.ToDateTime(TimeOnly.MinValue)));
    }

    /// <summary>Lo de una hora (UTC): las ventas de aquí, las devoluciones y cancelaciones que se hicieron y el historial de eleventa.</summary>
    private sealed class DeUnaHora
    {
        public long Tickets, Vendido, Redondeo;
        public long Devoluciones, Devuelto, DevueltoAlCredito, Cancelados, CanceladosCentavos;
        public long TicketsHistorial, Historial;

        /// <summary>La venta neta de la hora.</summary>
        public long Neta => Vendido + Historial - Devuelto;
    }

    // Las fechas se guardan como texto («2026-10-22 18:05:00.1234567»): los primeros 13 caracteres son la hora UTC. Con «+» la
    // sucursal no usa su índice: así la base usa el de la fecha (sin estadísticas, prefiere un índice que empieza por la
    // sucursal, que con una sola sucursal recorre todo).
    private const string VentasPorHoraSql = """
        SELECT substr(v."PagadaUtc", 1, 13) AS "Hora", COUNT(*) AS "Tickets", SUM(v."TotalCentavos") AS "Centavos",
               SUM(v."RedondeoCentavos") AS "Redondeo"
        FROM "Ventas" AS v
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" = 'Pagada'
        GROUP BY substr(v."PagadaUtc", 1, 13)
        """;

    /// <summary>
    /// Las devoluciones que se hicieron, de tickets de cualquier día; las cancelaciones, aparte: el ticket cancelado ya no está
    /// en lo vendido, y se cuentan como en el corte del día, el día en que se cancelaron.
    /// </summary>
    private const string DevolucionesPorHoraSql = """
        SELECT substr(d."FechaUtc", 1, 13) AS "Hora",
               SUM(CASE WHEN d."EsCancelacion" = 0 THEN 1 ELSE 0 END) AS "Devoluciones",
               SUM(CASE WHEN d."EsCancelacion" = 0 THEN d."TotalCentavos" ELSE 0 END) AS "Centavos",
               SUM(CASE WHEN d."EsCancelacion" = 0 THEN d."AlCreditoCentavos" ELSE 0 END) AS "AlCredito",
               SUM(CASE WHEN d."EsCancelacion" = 1 THEN 1 ELSE 0 END) AS "Cancelaciones",
               SUM(CASE WHEN d."EsCancelacion" = 1 THEN d."TotalCentavos" ELSE 0 END) AS "Cancelado"
        FROM "Devoluciones" AS d
        WHERE +d."SucursalId" = {0} AND d."FechaUtc" >= {1} AND d."FechaUtc" < {2}
        GROUP BY substr(d."FechaUtc", 1, 13)
        """;

    /// <summary>El historial de eleventa, sin los cancelados (sus totales ya traen lo devuelto).</summary>
    private const string HistorialPorHoraSql = """
        SELECT substr(h."FechaUtc", 1, 13) AS "Hora", COUNT(*) AS "Tickets", SUM(h."TotalCentavos") AS "Centavos"
        FROM "VentasHistoricas" AS h
        WHERE +h."SucursalId" = {0} AND h."FechaUtc" >= {1} AND h."FechaUtc" < {2} AND h."Cancelada" = 0
        GROUP BY substr(h."FechaUtc", 1, 13)
        """;

    private sealed record VentasPorHora(string Hora, long Tickets, long Centavos, long Redondeo);

    private sealed record DevolucionesPorHora(string Hora, long Devoluciones, long Centavos, long AlCredito, long Cancelaciones, long Cancelado);

    private sealed record HistorialPorHora(string Hora, long Tickets, long Centavos);

    private async Task<Dictionary<DateTime, DeUnaHora>> PorHoraAsync(Guid sucursal, Rango r, bool conHistorial, CancellationToken ct)
    {
        var horas = new Dictionary<DateTime, DeUnaHora>();
        DeUnaHora De(string hora)
        {
            var clave = DateTime.SpecifyKind(DateTime.ParseExact(hora.Replace('T', ' '), "yyyy-MM-dd HH", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            if (!horas.TryGetValue(clave, out var de))
                horas[clave] = de = new DeUnaHora();
            return de;
        }
        foreach (var v in await db.Database.SqlQueryRaw<VentasPorHora>(VentasPorHoraSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
        {
            var de = De(v.Hora);
            (de.Tickets, de.Vendido, de.Redondeo) = (v.Tickets, v.Centavos, v.Redondeo);
        }
        foreach (var d in await db.Database.SqlQueryRaw<DevolucionesPorHora>(DevolucionesPorHoraSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
        {
            var de = De(d.Hora);
            (de.Devoluciones, de.Devuelto, de.DevueltoAlCredito, de.Cancelados, de.CanceladosCentavos) =
                (d.Devoluciones, d.Centavos, d.AlCredito, d.Cancelaciones, d.Cancelado);
        }
        if (conHistorial)
        {
            foreach (var h in await db.Database.SqlQueryRaw<HistorialPorHora>(HistorialPorHoraSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
            {
                var de = De(h.Hora);
                (de.TicketsHistorial, de.Historial) = (h.Tickets, h.Centavos);
            }
        }
        return horas;
    }

    /// <summary>Lo vendido de cada variante (o de las impresiones, o del artículo común) en los tickets que no se cancelaron.</summary>
    private const string LineasSql = """
        SELECT l."VarianteId" AS "VarianteId", l."EsImpresiones" AS "EsImpresiones", SUM(l."ImporteCentavos") AS "Importe",
               SUM(l."CostoCentavos") AS "Costo", SUM(l."CantidadBase") AS "Cantidad",
               SUM(CASE WHEN l."VarianteId" IS NOT NULL AND l."EsImpresiones" = 0 AND l."LlevaInventario" = 0 AND l."CostoCentavos" = 0
                        THEN l."ImporteCentavos" ELSE 0 END) AS "SinCosto"
        FROM "VentasLineas" AS l
        INNER JOIN "Ventas" AS v ON v."Id" = l."VentaId"
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" = 'Pagada'
        GROUP BY l."VarianteId", l."EsImpresiones"
        """;

    /// <summary>Lo devuelto de cada una en el periodo (de tickets de cualquier día), con su cantidad en unidad base.</summary>
    private const string DevueltoSql = """
        SELECT l."VarianteId" AS "VarianteId", l."EsImpresiones" AS "EsImpresiones", SUM(dl."ImporteCentavos") AS "Importe",
               SUM(dl."CostoCentavos") AS "Costo", SUM(dl."Cantidad" * l."CantidadBase" / l."Cantidad") AS "Cantidad",
               SUM(CASE WHEN l."VarianteId" IS NOT NULL AND l."EsImpresiones" = 0 AND l."LlevaInventario" = 0 AND l."CostoCentavos" = 0
                        THEN dl."ImporteCentavos" ELSE 0 END) AS "SinCosto"
        FROM "DevolucionesLineas" AS dl
        INNER JOIN "Devoluciones" AS d ON d."Id" = dl."DevolucionId"
        INNER JOIN "VentasLineas" AS l ON l."Id" = dl."VentaLineaId"
        WHERE +d."SucursalId" = {0} AND d."FechaUtc" >= {1} AND d."FechaUtc" < {2} AND d."EsCancelacion" = 0
        GROUP BY l."VarianteId", l."EsImpresiones"
        """;

    /// <summary>
    /// Lo vendido de cada variante en el historial de eleventa, sin los tickets cancelados; el artículo común de eleventa, junto
    /// y sin variante, como el de aquí.
    /// </summary>
    private const string LineasHistorialSql = """
        SELECT CASE WHEN hl."EsArticuloComun" = 0 THEN hl."VarianteId" END AS "VarianteId", SUM(hl."ImporteCentavos") AS "Importe",
               SUM(hl."CostoCentavos") AS "Costo", SUM(hl."Cantidad") AS "Cantidad"
        FROM "VentasHistoricasLineas" AS hl
        INNER JOIN "VentasHistoricas" AS h ON h."Id" = hl."VentaHistoricaId"
        WHERE +h."SucursalId" = {0} AND h."FechaUtc" >= {1} AND h."FechaUtc" < {2} AND h."Cancelada" = 0
        GROUP BY CASE WHEN hl."EsArticuloComun" = 0 THEN hl."VarianteId" END
        """;

    private sealed record LineasAgrupadas(Guid? VarianteId, bool EsImpresiones, long Importe, long Costo, long Cantidad, long SinCosto);

    /// <param name="VarianteId">Vacía: el artículo común de eleventa.</param>
    private sealed record LineasDelHistorial(Guid? VarianteId, long Importe, long Costo, long Cantidad);

    /// <summary>Un renglón de lo vendido, con su cantidad en unidad base (en milésimas).</summary>
    /// <param name="DelHistorial">Del historial de eleventa (para lo que sus tickets suman sin desglose).</param>
    private sealed record Renglon(RenglonVendido Vendido, long Cantidad, bool DelHistorial = false);

    private async Task<List<Renglon>> RenglonesAsync(Guid sucursal, Rango r, bool conHistorial, CancellationToken ct)
    {
        var renglones = (await db.Database.SqlQueryRaw<LineasAgrupadas>(LineasSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
            .Select(l => new Renglon(new RenglonVendido(l.VarianteId, l.EsImpresiones, l.Importe, l.Costo, l.SinCosto), l.Cantidad))
            .Concat((await db.Database.SqlQueryRaw<LineasAgrupadas>(DevueltoSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
                .Select(l => new Renglon(new RenglonVendido(l.VarianteId, l.EsImpresiones, -l.Importe, -l.Costo, -l.SinCosto), -l.Cantidad)))
            .ToList();
        // El artículo común de eleventa (sin variante) va como el de aquí: aparte y sin costo, fuera de la utilidad.
        if (conHistorial)
            renglones.AddRange((await db.Database.SqlQueryRaw<LineasDelHistorial>(LineasHistorialSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
                .Select(l => new Renglon(new RenglonVendido(l.VarianteId, false, l.Importe, l.VarianteId is null ? 0 : l.Costo, 0), l.Cantidad, DelHistorial: true)));
        return renglones;
    }

    // ── Lo que se arma con las sumas ──

    private static CifrasReporteDto Cifras(IEnumerable<DeUnaHora> horas, UtilidadDelDiaDto? utilidad)
    {
        long tickets = 0, vendido = 0, devuelto = 0, devoluciones = 0, cancelados = 0, canceladosCentavos = 0, historial = 0, ticketsHistorial = 0;
        foreach (var h in horas)
        {
            tickets += h.Tickets + h.TicketsHistorial;
            vendido += h.Vendido + h.Historial;
            devuelto += h.Devuelto;
            devoluciones += h.Devoluciones;
            cancelados += h.Cancelados;
            canceladosCentavos += h.CanceladosCentavos;
            historial += h.Historial;
            ticketsHistorial += h.TicketsHistorial;
        }
        return new CifrasReporteDto(vendido - devuelto, (int)tickets, vendido, devuelto, (int)devoluciones, (int)cancelados, canceladosCentavos, historial,
            (int)ticketsHistorial, utilidad);
    }

    /// <summary>
    /// La gráfica: la venta neta de cada hora, día o mes, y la del que le toca del año pasado, completo. Hoy va por las horas
    /// de la tienda (Configuración › Tienda), más las que tengan algo de este día o del de hace un año.
    /// </summary>
    private async Task<List<PuntoReporteDto>> SerieAsync(
        Guid sucursal, PeriodoDeReporte p, Dictionary<DateTime, DeUnaHora> horas, Dictionary<DateTime, DeUnaHora>? horasAnterior, CancellationToken ct)
    {
        Dictionary<int, long> PorCubeta(Dictionary<DateTime, DeUnaHora> deLasHoras, bool anterior) =>
            deLasHoras.GroupBy(h => p.Cubeta(HoraMexico.ALocal(h.Key), anterior)).ToDictionary(g => g.Key, g => g.Sum(h => h.Value.Neta));
        var actual = PorCubeta(horas, anterior: false);
        var anteriores = horasAnterior is null ? null : PorCubeta(horasAnterior, anterior: true);

        var cubetas = p.Cubetas;
        if (p.Tipo == PeriodoReporte.Hoy)
        {
            var tienda = await new RegistroConfiguracion(db).LeerAsync(sucursal, ct);
            var (abre, cierra) = (tienda.HoraApertura.Hour, tienda.HoraCierre.Minute == 0 ? tienda.HoraCierre.Hour - 1 : tienda.HoraCierre.Hour);
            if (cierra < abre)
                (abre, cierra) = (0, 23);
            var conAlgo = actual.Keys.Concat(anteriores?.Keys ?? Enumerable.Empty<int>()).ToList();
            var (primera, ultima) = (Math.Min(abre, conAlgo.Count > 0 ? conAlgo.Min() : abre), Math.Max(cierra, conAlgo.Count > 0 ? conAlgo.Max() : cierra));
            cubetas = Enumerable.Range(primera, ultima - primera + 1);
        }
        return cubetas.Select(c =>
        {
            var inicio = p.Inicio(c)!.Value;
            var inicioAnterior = anteriores is null ? null : p.Inicio(c, anterior: true);
            var porLlegar = p.Ahora is { } ahora && inicio > ahora;
            return new PuntoReporteDto(inicio, inicioAnterior, porLlegar ? null : actual.GetValueOrDefault(c),
                inicioAnterior is null ? null : anteriores!.GetValueOrDefault(c), p.Ahora is { } ya && inicio <= ya && ya < p.Fin(c));
        }).ToList();
    }

    /// <summary>Cómo se llama en las ventas por departamento lo del historial de eleventa sin desglose.</summary>
    public const string NombreSinDesglose = "Sin desglose";

    /// <summary>
    /// Las ventas por departamento de los dos periodos, juntas por su nombre: de lo que más se vendió a lo que menos (lo que
    /// solo se vendió el año pasado, después), y al final las impresiones, el artículo común y lo del historial de eleventa sin
    /// desglose. Suman la venta neta.
    /// </summary>
    private static List<DepartamentoReporteDto> Departamentos(
        VentasPorDepartamentoDto actual, VentasPorDepartamentoDto? anterior, bool conCostos, long sinDesglose, long? sinDesgloseAnterior)
    {
        var deEste = actual.Departamentos.ToDictionary(d => d.Departamento);
        var delAnterior = anterior?.Departamentos.ToDictionary(d => d.Departamento) ?? [];
        var renglones = deEste.Keys.Union(delAnterior.Keys)
            .Select(nombre => new DepartamentoReporteDto(nombre, ClaseVenta.Departamento, deEste.GetValueOrDefault(nombre)?.VentaCentavos ?? 0,
                anterior is null ? null : delAnterior.GetValueOrDefault(nombre)?.VentaCentavos ?? 0,
                conCostos ? deEste.GetValueOrDefault(nombre)?.CostoCentavos ?? 0 : null))
            .OrderByDescending(d => d.VentaCentavos).ThenByDescending(d => d.AnteriorCentavos ?? 0).ThenBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (actual.ImpresionesCentavos != 0 || anterior?.ImpresionesCentavos is not (null or 0))
            renglones.Add(new DepartamentoReporteDto("Impresiones", ClaseVenta.Impresiones, actual.ImpresionesCentavos, anterior?.ImpresionesCentavos,
                conCostos ? actual.ImpresionesCostoCentavos ?? 0 : null));
        if (actual.ArticuloComunCentavos != 0 || anterior?.ArticuloComunCentavos is not (null or 0))
            renglones.Add(new DepartamentoReporteDto("Artículo común", ClaseVenta.ArticuloComun, actual.ArticuloComunCentavos, anterior?.ArticuloComunCentavos));
        if (sinDesglose != 0 || sinDesgloseAnterior is not (null or 0))
            renglones.Add(new DepartamentoReporteDto(NombreSinDesglose, ClaseVenta.SinDesglose, sinDesglose, anterior is null ? null : sinDesgloseAnterior ?? 0));
        return renglones;
    }

    /// <summary>Lo vendido por cada cajero (lo devuelto de sus tickets, restado) y las diferencias de los cortes de sus turnos.</summary>
    private const string CajerosSql = """
        SELECT v."CajeroId" AS "CajeroId", COUNT(*) AS "Tickets", SUM(v."TotalCentavos") AS "Centavos"
        FROM "Ventas" AS v
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" = 'Pagada'
        GROUP BY v."CajeroId"
        """;

    private const string DevueltoPorCajeroSql = """
        SELECT v."CajeroId" AS "CajeroId", COUNT(*) AS "Tickets", SUM(d."TotalCentavos") AS "Centavos"
        FROM "Devoluciones" AS d
        INNER JOIN "Ventas" AS v ON v."Id" = d."VentaId"
        WHERE +d."SucursalId" = {0} AND d."FechaUtc" >= {1} AND d."FechaUtc" < {2} AND d."EsCancelacion" = 0
        GROUP BY v."CajeroId"
        """;

    /// <summary>
    /// Los cortes del turno hechos en el periodo, a nombre del cajero del turno, como en el corte del día: un turno cuenta el de
    /// su último corte.
    /// </summary>
    private const string CortesSql = """
        SELECT t."CajeroId" AS "CajeroId", COUNT(*) AS "Cortes", COALESCE(SUM(c."DiferenciaCentavos"), 0) AS "Diferencia",
               COALESCE(SUM(CASE WHEN c."DiferenciaCentavos" < 0 THEN -c."DiferenciaCentavos" ELSE 0 END), 0) AS "Faltante",
               COALESCE(SUM(CASE WHEN c."DiferenciaCentavos" > 0 THEN c."DiferenciaCentavos" ELSE 0 END), 0) AS "Sobrante"
        FROM "Cortes" AS c
        INNER JOIN "Turnos" AS t ON t."Id" = c."TurnoId"
        WHERE t."SucursalId" = {0} AND c."FechaUtc" >= {1} AND c."FechaUtc" < {2} AND c."Tipo" = 'Z'
          AND NOT EXISTS (SELECT 1 FROM "Cortes" AS o WHERE o."TurnoId" = c."TurnoId" AND o."Tipo" = 'Z' AND o."FechaUtc" > c."FechaUtc")
        GROUP BY t."CajeroId"
        """;

    private sealed record DeUnCajero(Guid CajeroId, long Tickets, long Centavos);

    private sealed record CortesDeUnCajero(Guid CajeroId, long Cortes, long Diferencia, long Faltante, long Sobrante);

    private async Task<List<CajeroReporteDto>> CajerosAsync(Guid sucursal, Rango r, CancellationToken ct)
    {
        var vendido = await db.Database.SqlQueryRaw<DeUnCajero>(CajerosSql, sucursal, r.DesdeUtc, r.HastaUtc).ToDictionaryAsync(c => c.CajeroId, ct);
        var devuelto = await db.Database.SqlQueryRaw<DeUnCajero>(DevueltoPorCajeroSql, sucursal, r.DesdeUtc, r.HastaUtc)
            .ToDictionaryAsync(c => c.CajeroId, c => c.Centavos, ct);
        var cortes = await db.Database.SqlQueryRaw<CortesDeUnCajero>(CortesSql, sucursal, r.DesdeUtc, r.HastaUtc).ToDictionaryAsync(c => c.CajeroId, ct);
        var nombres = (await VentasDeOtrosDias.NombresDeAsync(db, [.. vendido.Keys, .. devuelto.Keys, .. cortes.Keys], ct)).ToDictionary(u => u.Id, u => u.Nombre);
        return nombres.Keys
            .Select(id =>
            {
                var c = cortes.GetValueOrDefault(id);
                return new CajeroReporteDto(id, nombres[id], (int)(vendido.GetValueOrDefault(id)?.Tickets ?? 0),
                    (vendido.GetValueOrDefault(id)?.Centavos ?? 0) - devuelto.GetValueOrDefault(id), (int)(c?.Cortes ?? 0), c?.Diferencia ?? 0,
                    c?.Faltante ?? 0, c?.Sobrante ?? 0);
            })
            .OrderByDescending(c => c.VentaCentavos).ThenBy(c => c.Nombre, StringComparer.CurrentCulture)
            .ToList();
    }

    private const string FormasSql = """
        SELECT c."Forma" AS "Forma", SUM(c."MontoCentavos") AS "Centavos"
        FROM "Cobros" AS c
        INNER JOIN "Ventas" AS v ON v."Id" = c."VentaId"
        WHERE +v."SucursalId" = {0} AND v."PagadaUtc" >= {1} AND v."PagadaUtc" < {2} AND v."Estado" = 'Pagada'
        GROUP BY c."Forma"
        """;

    private sealed record DeUnaForma(string Forma, long Centavos);

    /// <summary>
    /// Cómo se pagó lo vendido aquí: lo cobrado de cada forma (el efectivo, sin el redondeo al peso, para que sumen la venta) y
    /// lo devuelto, restado de como se regresó: a la cuenta del cliente (lo fiado) o, lo demás, en efectivo. Así suman la venta
    /// neta de este POS.
    /// </summary>
    private async Task<List<FormaPagoReporteDto>> FormasDePagoAsync(Guid sucursal, Rango r, IEnumerable<DeUnaHora> horas, CancellationToken ct)
    {
        var formas = (await db.Database.SqlQueryRaw<DeUnaForma>(FormasSql, sucursal, r.DesdeUtc, r.HastaUtc).ToListAsync(ct))
            .ToDictionary(f => Enum.Parse<FormaPago>(f.Forma), f => f.Centavos);
        long redondeo = 0, devuelto = 0, alCredito = 0;
        foreach (var h in horas)
            (redondeo, devuelto, alCredito) = (redondeo + h.Redondeo, devuelto + h.Devuelto, alCredito + h.DevueltoAlCredito);
        formas[FormaPago.Efectivo] = formas.GetValueOrDefault(FormaPago.Efectivo) - redondeo - (devuelto - alCredito);
        formas[FormaPago.Credito] = formas.GetValueOrDefault(FormaPago.Credito) - alCredito;
        return formas.Where(f => f.Value != 0).OrderBy(f => f.Key).Select(f => new FormaPagoReporteDto(f.Key, f.Value)).ToList();
    }

    // ── El catálogo de lo que se vendió ──

    private sealed record DeLaVariante(Guid Id, string Producto, string? Marca, string Descripcion, UnidadBase Unidad, Guid DepartamentoId, Guid? JuntadaEnId);

    private sealed record Catalogo(
        Dictionary<Guid, DeLaVariante> Variantes, Dictionary<Guid, Guid> DepartamentoDe, Dictionary<Guid, string> Departamentos)
    {
        /// <summary>La variante en la que quedó una que se juntó con otra (capturada dos veces): su venta es la de esa.</summary>
        public Guid EnLaQueQuedo(Guid id)
        {
            for (var vueltas = 0; vueltas < 10 && Variantes.TryGetValue(id, out var v) && v.JuntadaEnId is { } otra && Variantes.ContainsKey(otra); vueltas++)
                id = otra;
            return id;
        }
    }

    private async Task<Catalogo> CatalogoAsync(IEnumerable<Renglon> renglones, CancellationToken ct)
    {
        var ids = renglones.Select(r => r.Vendido.VarianteId).OfType<Guid>().ToHashSet();
        var variantes = new Dictionary<Guid, DeLaVariante>();
        // Las juntadas traen la variante en la que quedaron (y esa, la suya).
        while (ids.Count > 0)
        {
            var lista = ids.ToList();
            foreach (var v in await db.Variantes.AsNoTracking().Where(v => lista.Contains(v.Id))
                         .Select(v => new DeLaVariante(v.Id, v.Producto!.Nombre, v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion,
                             v.UnidadBase, v.Producto.DepartamentoId, v.JuntadaEnId))
                         .ToListAsync(ct))
                variantes[v.Id] = v;
            ids = variantes.Values.Select(v => v.JuntadaEnId).OfType<Guid>().Where(id => !variantes.ContainsKey(id)).ToHashSet();
            ids.ExceptWith(lista);
        }
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        return new Catalogo(variantes, variantes.Values.ToDictionary(v => v.Id, v => v.DepartamentoId), departamentos);
    }

    /// <summary>
    /// Lo que más se vende: por lo vendido (menos lo devuelto) en el periodo, de los productos del catálogo (sin las impresiones
    /// ni el artículo común); lo de una variante juntada con otra, en la que quedó.
    /// </summary>
    private static List<ProductoReporteDto> LoMasVendido(List<Renglon> renglones, Catalogo catalogo) =>
        renglones.Where(r => r.Vendido.VarianteId is not null && !r.Vendido.EsImpresiones)
            .GroupBy(r => catalogo.EnLaQueQuedo(r.Vendido.VarianteId!.Value))
            .Select(g => (Id: g.Key, Venta: g.Sum(r => r.Vendido.ImporteCentavos), Cantidad: g.Sum(r => r.Cantidad)))
            .Where(p => p.Venta > 0 && catalogo.Variantes.ContainsKey(p.Id))
            .Select(p =>
            {
                var v = catalogo.Variantes[p.Id];
                return new ProductoReporteDto(p.Id, RegistroReportesInventario.Nombre(v.Producto, v.Marca, v.Descripcion), v.Unidad, p.Cantidad, p.Venta);
            })
            .OrderByDescending(p => p.VentaCentavos).ThenByDescending(p => p.CantidadMilesimas).ThenBy(p => p.Nombre, StringComparer.CurrentCulture)
            .Take(MasVendidos)
            .ToList();
}
