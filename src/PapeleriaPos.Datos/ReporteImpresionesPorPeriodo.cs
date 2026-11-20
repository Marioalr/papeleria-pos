using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Datos;

/// <summary>
/// Reportes › Impresiones por período (2026-11-20): lo que se imprimió en la sucursal de un día a otro y cómo quedó su cobro, por
/// día, por quien atendía, por PC, por impresora y por tipo (de las PC, copias del panel, «Copias o celular» del contador); las no
/// vendidas por motivo y las hojas que faltaron. Es lo que daba el reporte de Control Print (ventas, costo y ganancia por periodo,
/// usuario y PC, y las no vendidas), y era lo que se perdía al quitarlo (docs/impresion.md, «Los hitos, en orden»). Solo lee.
/// <para>
/// Para quien tiene «Ver reportes de ventas» en «Puede», como los reportes de ventas; el costo, la utilidad y la pérdida, solo con
/// «Ver costos, utilidad y margen». Cuenta por el día en que se imprimió (no en el que se cobró): una impresión de ayer cobrada hoy
/// es de ayer, y lo que sigue por cobrar se ve en el día en que salió. La utilidad es lo cobrado menos el costo de lo cobrado; lo
/// que se perdió es el costo de las no vendidas.
/// </para>
/// <para>
/// Una tienda imprime unos 1,800 trabajos al mes (Control Print, 2026-11-20): se leen solo las columnas que hacen falta (nunca la
/// miniatura ni el detalle) y se suman aquí. Hasta tres años a la vez, como las ventas por período.
/// </para>
/// </summary>
public sealed class ReporteImpresionesPorPeriodo(PosDbContext db)
{
    /// <summary>Lo más que lleva la hoja de cada impresión del Excel; los totales siempre van completos.</summary>
    public const int MaximoTrabajosEnExcel = 60_000;

    public const string SinSesion = "Sin sesión";

    /// <summary>
    /// Lo que se imprimió de <paramref name="desde"/> a <paramref name="hasta"/> (vacíos: hoy; el último, si pasa de hoy, llega hasta
    /// hoy), visto desde <paramref name="cajaId"/> por <paramref name="usuarioId"/>. Con <paramref name="conTrabajos"/>, también
    /// cada impresión (para el Excel).
    /// </summary>
    /// <param name="piloto">El piloto con eleventa: solo se dice (lo de la principal sigue en Control Print).</param>
    public async Task<ImpresionesPorPeriodoDto> ArmarAsync(
        Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, bool piloto = false, bool conTrabajos = false, CancellationToken ct = default)
    {
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var sucursal = caja.SucursalId;
        var control = new ServicioControl(db);
        // Consultar no se autoriza: «Puede», o no se ve (también dice si quien entró sigue activo).
        await control.ExigirParaVerAsync(usuarioId, sucursal, AccionControlada.VerReportes, ct);
        var veCosto = await control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
        var (primero, ultimo) = Dias(desde, hasta, hoy);
        var desdeUtc = HoraMexico.AUtc(primero.ToDateTime(TimeOnly.MinValue));
        var hastaUtc = HoraMexico.AUtc(ultimo.AddDays(1).ToDateTime(TimeOnly.MinValue));

        return await ReportesDeVentas.EnUnMismoMomentoAsync(db, async () =>
        {
            var trabajos = await db.Set<TrabajoImpresion>().AsNoTracking()
                .Where(t => t.SucursalId == sucursal && t.ImpresoUtc >= desdeUtc && t.ImpresoUtc < hastaUtc)
                .OrderBy(t => t.ImpresoUtc)
                .Select(t => new Leido(t.Id, t.ImpresoUtc, t.Tipo, t.Documento, t.Pc, t.ImpresoraId, t.AtendioId, t.Paginas, t.Copias, t.Caras,
                    t.CarasColor, t.Hojas, t.CarasContadas, t.Tamano, t.TipoPapel, t.Cobro, t.MotivoNoVendida, t.PrecioCentavos, t.PrecioCompletoCentavos,
                    t.CostoCentavos))
                .ToListAsync(ct);
            if (conTrabajos && trabajos.Count > MaximoTrabajosEnExcel)
                throw new ArgumentException(
                    $"Son {trabajos.Count.ToString("N0", CultureInfo.GetCultureInfo("es-MX"))} impresiones, y el Excel lleva hasta "
                    + $"{MaximoTrabajosEnExcel.ToString("N0", CultureInfo.GetCultureInfo("es-MX"))}: elige menos días.");
            var atendio = await NombresAsync(trabajos, ct);
            var impresora = await ImpresorasAsync(sucursal, ct);
            string Quien(Leido t) => t.AtendioId is { } a ? atendio.GetValueOrDefault(a) ?? SinSesion : SinSesion;

            var dias = trabajos.GroupBy(t => DateOnly.FromDateTime(HoraMexico.ALocal(t.ImpresoUtc))).OrderBy(g => g.Key)
                .Select(g => Sumar(null, g.Key, g, veCosto)).ToList();
            var noVendidas = trabajos.Where(t => t.Cobro == EstadoCobroImpresion.NoVendida)
                .GroupBy(t => t.Motivo ?? MotivoNoVendida.Otro).OrderBy(g => g.Key)
                .Select(g => new NoVendidasPorMotivoDto(g.Key, g.Count(), g.Sum(t => t.Caras), g.Sum(t => t.PrecioCentavos),
                    veCosto ? g.Sum(t => t.CostoCentavos) : null))
                .ToList();
            // Solo lo que salió de una PC: en la tarjeta del contador, sus caras contadas son las que se le cuentan, no una falta.
            var faltaron = trabajos.Where(t => t.Tipo == TipoTrabajoImpresion.Impresion && t.CarasContadas is { } c && c < t.Caras)
                .Select(t => new HojasQueFaltaronDto(t.Id, HoraMexico.ALocal(t.ImpresoUtc), t.Documento, t.Pc, impresora(t.ImpresoraId), Quien(t), t.Caras,
                    t.CarasContadas!.Value, t.Cobro, t.PrecioCompletoCentavos is not null, t.PrecioCentavos))
                .ToList();
            var detalle = conTrabajos
                ? trabajos.Select(t => new TrabajoDelPeriodoDto(t.Id, HoraMexico.ALocal(t.ImpresoUtc), t.Tipo, t.Documento, t.Pc, impresora(t.ImpresoraId),
                    Quien(t), t.Paginas, t.Copias, t.Caras, t.CarasColor, t.Hojas, t.CarasContadas, t.Tamano, t.TipoPapel, t.Cobro, t.Motivo,
                    t.PrecioCentavos, veCosto ? t.CostoCentavos : null)).ToList()
                : [];
            return new ImpresionesPorPeriodoDto(primero, ultimo, hoy, veCosto, piloto, Sumar(null, null, trabajos, veCosto), dias,
                Grupos(trabajos, Quien, veCosto), Grupos(trabajos, t => t.Pc, veCosto),
                Grupos(trabajos, t => impresora(t.ImpresoraId) ?? "—", veCosto),
                trabajos.GroupBy(t => t.Tipo).OrderBy(g => g.Key).Select(g => Sumar(TextosDeImpresionesPorPeriodo.Tipo(g.Key), null, g, veCosto)).ToList(),
                noVendidas, faltaron, detalle);
        }, ct);
    }

    /// <summary>Los días que se piden, revisados como en las ventas por período (los mismos avisos).</summary>
    private static (DateOnly Primero, DateOnly Ultimo) Dias(DateOnly? desde, DateOnly? hasta, DateOnly hoy)
    {
        var primero = desde ?? hasta ?? hoy;
        var ultimo = hasta ?? (desde is null ? hoy : primero);
        if (primero > hoy)
            throw new ArgumentException(ReporteVentasPorPeriodo.TodaviaNoLlega);
        if (ultimo < primero)
            throw new ArgumentException(ReporteVentasPorPeriodo.AlReves);
        if (ultimo > hoy)
            ultimo = hoy;
        if (primero < DiasDeVentas.PrimerDia)
            throw new ArgumentException(ReporteVentasPorPeriodo.AntesDelPrimerDia);
        if (ultimo > DiasDeVentas.UltimoQueCabe(primero))
            throw new ArgumentException(ReporteVentasPorPeriodo.MasDeTresAnios);
        return (primero, ultimo);
    }

    /// <summary>Un grupo por cada nombre, del que más cobró al que menos (y por nombre si empatan).</summary>
    private static List<ResumenImpresionesDto> Grupos(List<Leido> trabajos, Func<Leido, string> nombre, bool veCosto) =>
        trabajos.GroupBy(nombre).Select(g => Sumar(g.Key, null, g, veCosto))
            .OrderByDescending(r => r.CobradoCentavos).ThenByDescending(r => r.Trabajos).ThenBy(r => r.Nombre, StringComparer.CurrentCulture).ToList();

    private static ResumenImpresionesDto Sumar(string? nombre, DateOnly? fecha, IEnumerable<Leido> grupo, bool veCosto)
    {
        int trabajos = 0, caras = 0, color = 0, hojas = 0, cobradas = 0, porCobrar = 0, noVendidas = 0, enTramite = 0;
        long cobrado = 0, pendiente = 0, dejado = 0, costo = 0, costoCobrado = 0, perdida = 0;
        foreach (var t in grupo)
        {
            (trabajos, caras, color, hojas, costo) = (trabajos + 1, caras + t.Caras, color + t.CarasColor, hojas + t.Hojas, costo + t.CostoCentavos);
            switch (t.Cobro)
            {
                case EstadoCobroImpresion.Cobrada:
                    (cobradas, cobrado, costoCobrado) = (cobradas + 1, cobrado + t.PrecioCentavos, costoCobrado + t.CostoCentavos);
                    break;
                case EstadoCobroImpresion.PorCobrar:
                    (porCobrar, pendiente) = (porCobrar + 1, pendiente + t.PrecioCentavos);
                    break;
                case EstadoCobroImpresion.NoVendida:
                    (noVendidas, dejado, perdida) = (noVendidas + 1, dejado + t.PrecioCentavos, perdida + t.CostoCentavos);
                    break;
                case EstadoCobroImpresion.EnTramite:
                    enTramite++;
                    break;
            }
        }
        return new ResumenImpresionesDto(nombre, fecha, trabajos, caras, color, hojas, cobradas, cobrado, porCobrar, pendiente, noVendidas, dejado,
            enTramite, veCosto ? costo : null, veCosto ? costoCobrado : null, veCosto ? perdida : null);
    }

    private async Task<Dictionary<Guid, string>> NombresAsync(List<Leido> trabajos, CancellationToken ct)
    {
        var ids = trabajos.Where(t => t.AtendioId != null).Select(t => t.AtendioId!.Value).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Usuarios.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
    }

    /// <summary>
    /// El nombre de cada impresora de la sucursal, también las dadas de baja (lo que imprimieron sigue siendo suyo); lo de antes de varias
    /// impresoras que no se ligó es de la primera, como en el contador.
    /// </summary>
    private async Task<Func<Guid?, string?>> ImpresorasAsync(Guid sucursal, CancellationToken ct)
    {
        var impresoras = await db.Set<Impresora>().AsNoTracking().Where(i => i.SucursalId == sucursal).OrderBy(i => i.Numero)
            .Select(i => new { i.Id, i.Nombre }).ToListAsync(ct);
        var nombres = impresoras.ToDictionary(i => i.Id, i => i.Nombre);
        var primera = impresoras.FirstOrDefault()?.Nombre;
        return id => id is { } i ? nombres.GetValueOrDefault(i) ?? primera : primera;
    }

    /// <summary>Lo que se lee de cada impresión: sin la miniatura ni el detalle.</summary>
    private sealed record Leido(
        Guid Id, DateTime ImpresoUtc, TipoTrabajoImpresion Tipo, string Documento, string Pc, Guid? ImpresoraId, Guid? AtendioId, int Paginas, int Copias,
        int Caras, int CarasColor, int Hojas, int? CarasContadas, string Tamano, string TipoPapel, EstadoCobroImpresion Cobro, MotivoNoVendida? Motivo,
        long PrecioCentavos, long? PrecioCompletoCentavos, long CostoCentavos);
}
