using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// La impresora de la sucursal vista desde la caja principal (docs/fase-1-impresion.md, parte 2): sus lecturas por la
/// red, los avisos de consumibles, las hojas que de verdad salieron, el contador contra lo cobrado y la calibración.
/// Quien lee la impresora (la tarea de fondo del servicio) le pasa aquí cada lectura. Lo que necesita del POS (la hora
/// y los avisos) lo pide por <paramref name="pos"/>; quién puede cambiar la configuración o aplicar la calibración lo
/// revisa quien llama (la API).
/// </summary>
public sealed class ServicioImpresora(DbContext db, IPosParaImpresiones pos)
{
    /// <summary>Un salto hacia arriba de al menos esto (puntos) es un cartucho nuevo; en la caja de mantenimiento, 10.</summary>
    private const int CambioDeCartucho = 5;
    private const int CambioDeCaja = 10;

    private DbSet<ConfiguracionImpresion> Configuraciones => db.Set<ConfiguracionImpresion>();
    private DbSet<LecturaImpresora> Lecturas => db.Set<LecturaImpresora>();
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    public async Task<ConfiguracionImpresion> ConfiguracionAsync(Guid sucursalId, CancellationToken ct = default) =>
        await Configuraciones.FindAsync([sucursalId], ct)
        ?? new ConfiguracionImpresion { SucursalId = sucursalId, CostosJson = JsonSerializer.Serialize(new CostSettings(), CostSettings.Json) };

    /// <summary>Las sucursales con impresora configurada, para leerlas.</summary>
    public async Task<List<ConfiguracionImpresion>> ConImpresoraAsync(CancellationToken ct = default) =>
        await Configuraciones.AsNoTracking().Where(c => c.ImpresoraHost != null && c.ImpresoraHost != "").ToListAsync(ct);

    /// <summary>
    /// Cómo se lee la impresora. La cambia quien tiene «Configurar la impresora»: quien llama ya lo revisó (la API, con su
    /// usuario y su PIN).
    /// </summary>
    public async Task<ConfiguracionImpresion> GuardarConfiguracionAsync(Guid sucursalId, ConfiguracionImpresoraDto c, CancellationToken ct = default)
    {
        if (c.AvisoTintaPorcentaje is < 0 or > 100)
            throw new ArgumentException("El aviso de tinta va de 0 a 100 %.");
        if (c.MinutosEntreLecturas is < 1 or > 120)
            throw new ArgumentException("Se lee cada 1 a 120 minutos.");
        var configuracion = await Configuraciones.FindAsync([sucursalId], ct);
        if (configuracion is null)
        {
            configuracion = await ConfiguracionAsync(sucursalId, ct);
            configuracion.ModificadoUtc = Ahora;
            Configuraciones.Add(configuracion);
        }
        configuracion.ImpresoraHost = string.IsNullOrWhiteSpace(c.Host) ? null : c.Host.Trim();
        configuracion.AvisoTintaPorcentaje = c.AvisoTintaPorcentaje;
        configuracion.MinutosEntreLecturas = c.MinutosEntreLecturas;
        await db.SaveChangesAsync(ct);
        return configuracion;
    }

    public async Task<LecturaImpresora?> UltimaAsync(Guid sucursalId, CancellationToken ct = default) =>
        await Lecturas.AsNoTracking().Where(l => l.SucursalId == sucursalId).OrderByDescending(l => l.TomadaUtc).FirstOrDefaultAsync(ct);

    /// <summary>La última lectura con contador tomada a más tardar en ese momento (la base para verificar las hojas).</summary>
    public async Task<LecturaImpresora?> AntesDeAsync(Guid sucursalId, DateTime utc, CancellationToken ct = default) =>
        await Lecturas.AsNoTracking()
            .Where(l => l.SucursalId == sucursalId && l.Contador != null && l.TomadaUtc <= utc)
            .OrderByDescending(l => l.TomadaUtc).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Guarda una lectura y avisa lo que cambió desde la anterior: una tinta o la caja de mantenimiento que bajó al
    /// umbral, o un consumible nuevo.
    /// </summary>
    public async Task<LecturaImpresora> GuardarLecturaAsync(Guid sucursalId, PrinterStatus s, CancellationToken ct = default)
    {
        var anterior = await UltimaAsync(sucursalId, ct);
        var lectura = new LecturaImpresora
        {
            SucursalId = sucursalId, TomadaUtc = s.TakenUtc, TintaK = s.InkK, TintaC = s.InkC, TintaM = s.InkM, TintaY = s.InkY,
            Mantenimiento = s.Maintenance, Contador = s.LifeCount, ContadorColor = s.ColorCount, ContadorBN = s.MonoCount, Serie = s.Serial,
        };
        Lecturas.Add(lectura);

        var umbral = (await ConfiguracionAsync(sucursalId, ct)).AvisoTintaPorcentaje;
        foreach (var (nombre, antes, ahora, salto) in Consumibles(anterior, lectura))
        {
            if (ahora is not { } nivel)
                continue;
            if (antes is { } previo && nivel - previo >= salto)
                pos.Avisar(sucursalId, AvisoImpresiones.Impresora, $"Impresora: {nombre} nuevo\nPasó de {previo} % a {nivel} %.",
                    $"impresora:cambio:{nombre}:{lectura.Id}");
            else if (nivel <= umbral && (antes is null || antes > umbral))
                pos.Avisar(sucursalId, AvisoImpresiones.Impresora,
                    $"Impresora: {nombre} {(nivel == 0 ? "agotado" : "bajo")}\nQueda {nivel} % (el aviso es desde {umbral} %).",
                    $"impresora:bajo:{nombre}:{lectura.Id}");
        }
        await db.SaveChangesAsync(ct);
        return lectura;
    }

    private static IEnumerable<(string Nombre, int? Antes, int? Ahora, int Salto)> Consumibles(LecturaImpresora? a, LecturaImpresora b)
    {
        yield return ("cartucho negro", a?.TintaK, b.TintaK, CambioDeCartucho);
        yield return ("cartucho cian", a?.TintaC, b.TintaC, CambioDeCartucho);
        yield return ("cartucho magenta", a?.TintaM, b.TintaM, CambioDeCartucho);
        yield return ("cartucho amarillo", a?.TintaY, b.TintaY, CambioDeCartucho);
        yield return ("caja de mantenimiento", a?.Mantenimiento, b.Mantenimiento, CambioDeCaja);
    }

    /// <summary>
    /// Lo que contó la impresora para un lote de trabajos (<see cref="PageAuditor"/>). Si faltaron caras, el costo baja
    /// a lo que salió y se avisa: puede ser un atasco o una cancelación en el panel de la impresora.
    /// </summary>
    public async Task AplicarVerificacionAsync(IReadOnlyList<PageAuditor.JobCount> conteo, CancellationToken ct = default)
    {
        foreach (var c in conteo)
        {
            if (await Trabajos.FindAsync([c.JobId], ct) is not { } trabajo || trabajo.CarasContadas is not null)
                continue;
            trabajo.AplicarCarasContadas(c.Counted);
            if (trabajo.FaltaronCaras)
                pos.Avisar(trabajo.SucursalId, AvisoImpresiones.HojasFaltantes,
                    $"Impresión incompleta · {trabajo.Pc}\n«{trabajo.Documento}»: la impresora contó {trabajo.CarasContadas} de {trabajo.Caras} caras. "
                    + "Puede ser un atasco o una cancelación en el panel.",
                    $"hojas:{trabajo.Id}");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lo que necesita la verificación de cada trabajo del lote: caras esperadas y si salió (todos ya terminaron en Windows).</summary>
    public async Task<Dictionary<Guid, PageAuditor.JobInfo>> InfoAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await Trabajos.AsNoTracking().Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new PageAuditor.JobInfo(t.Caras, t.Estado == EstadoTrabajo.Impreso, Finished: true), ct);

    // ── Contador contra lo cobrado ───────────────────────────────────────────

    /// <summary>Desde que abrió la tienda hoy (o el día que se pida): lo que contó la impresora contra lo registrado.</summary>
    public async Task<CounterCheck?> ComparacionAsync(Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var historial = await Historial(sucursalId, desde.AddDays(-1), hasta, ct);
        var registro = new RegistroImpresiones(db, pos);
        return CounterCheck.Compute(historial, desde, hasta, (a, b) => registro.CarasRegistradas(sucursalId, a, b));
    }

    public async Task<EstadoImpresoraDto> EstadoAsync(Guid sucursalId, string? error, CancellationToken ct = default)
    {
        var configuracion = await ConfiguracionAsync(sucursalId, ct);
        var ultima = await UltimaAsync(sucursalId, ct);
        var hoy = await ComparacionAsync(sucursalId, DateOnly.FromDateTime(HoraMexico.ALocal(Ahora)), ct);
        return new EstadoImpresoraDto(
            new ConfiguracionImpresoraDto(configuracion.ImpresoraHost, configuracion.AvisoTintaPorcentaje, configuracion.MinutosEntreLecturas),
            ultima is null ? null : Dto(ultima), error, hoy is null ? null : Dto(hoy));
    }

    public static LecturaImpresoraDto Dto(LecturaImpresora l) =>
        new(HoraMexico.ALocal(l.TomadaUtc), l.TintaK, l.TintaC, l.TintaM, l.TintaY, l.Mantenimiento, l.Contador, l.ContadorColor, l.ContadorBN, l.Serie);

    public static ComparacionContadorDto Dto(CounterCheck c) =>
        new(HoraMexico.ALocal(c.FromUtc), HoraMexico.ALocal(c.ToUtc), c.Counted, c.CountedColor, c.Registered.PrintSides, c.Registered.CopySides,
            c.Unregistered, c.UnregisteredColor);

    // ── Calibración ──────────────────────────────────────────────────────────

    /// <summary>
    /// La tinta real (niveles de la impresora) contra la que estimó el modelo, desde la última calibración aplicada:
    /// de ahí salen los factores que corrigen el costo de cada tinta y las páginas reales por caja de mantenimiento.
    /// </summary>
    public async Task<(CalibrationResult Resultado, CostSettings Costos)> CalcularCalibracionAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var costos = await new RegistroImpresiones(db, pos).CostosAsync(sucursalId, ct);
        var desde = costos.Calibration.SinceUtc ?? DateTime.MinValue;
        var historial = await Historial(sucursalId, desde, DateTime.MaxValue, ct);
        var trabajos = await Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.ImpresoUtc >= desde && (t.Estado == EstadoTrabajo.Impreso || t.CarasContadas > 0))
            .Select(t => new { t.ImpresoUtc, t.EnviadoUtc, t.Caras, t.CarasContadas, t.CarasColor, t.TintaC, t.TintaM, t.TintaY, t.TintaK })
            .ToListAsync(ct);
        var paraCalibrar = trabajos.Select(t => new CalibrationJob(
            // Si el agente lo mandó mucho después de imprimirse (la caja estaba cerrada), cuenta cuando se envió.
            t.EnviadoUtc is { } enviado && t.ImpresoUtc - enviado > TimeSpan.FromMinutes(10) ? enviado.AddMinutes(1) : t.ImpresoUtc,
            t.CarasContadas ?? t.Caras, t.CarasColor, t.TintaC, t.TintaM, t.TintaY, t.TintaK)).ToList();
        return (Calibrator.Calibrate(historial, paraCalibrar, costos), costos);
    }

    public async Task<CalibracionDto> CalibracionAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var (r, costos) = await CalcularCalibracionAsync(sucursalId, ct);
        var actual = costos.Calibration;
        double Actual(InkChannel canal) => canal switch
        {
            InkChannel.Cyan => actual.C,
            InkChannel.Magenta => actual.M,
            InkChannel.Yellow => actual.Y,
            _ => actual.K,
        };
        return new CalibracionDto(
            r.FromUtc is { } desde ? HoraMexico.ALocal(desde) : null, r.ToUtc is { } hasta ? HoraMexico.ALocal(hasta) : null,
            r.Channels.Select(c => new CanalCalibradoDto(Calibrator.Name(c.Channel), c.ObservedPoints, c.EstimatedPoints, c.Ratio, c.Factor,
                Actual(c.Channel), Calidad(c.Quality))).ToList(),
            r.PrinterPages, r.OurSides, r.MaintenancePagesPerBox, costos.Maintenance.PagesPerBox,
            r.Changes.Select(c => $"{HoraMexico.ALocal(c.Utc):dd/MM HH:mm} · {c.Supply}: de {c.From} % a {c.To} %").ToList(),
            actual.UpdatedUtc is { } aplicada ? HoraMexico.ALocal(aplicada) : null);
    }

    /// <summary>
    /// Para quien tiene «Configurar la impresora» (quien llama ya lo revisó: la API, con su usuario y su PIN): guarda los
    /// factores calculados en los costos de la sucursal; las impresiones nuevas ya salen con ellos. Como en Control Print,
    /// el factor es la tinta real entre la del modelo (no se acumula) y la caja de mantenimiento se corrige con datos
    /// preliminares. No es automática: mientras el agente vigila la cola de laboratorio, lo que registra el POS no sale de
    /// la impresora y la calibración no sirve.
    /// </summary>
    public async Task<CalibracionDto> AplicarCalibracionAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var (r, costos) = await CalcularCalibracionAsync(sucursalId, ct);
        var canales = r.Channels.Where(c => c.Quality >= CalibrationQuality.Preliminary).ToList();
        var caja = r.MaintenanceQuality >= CalibrationQuality.Preliminary ? r.MaintenancePagesPerBox : null;
        if (canales.Count == 0 && caja is null)
            throw new InvalidOperationException("Todavía no hay datos suficientes: los niveles de tinta tienen que bajar al menos 3 puntos.");
        var cal = costos.Calibration;
        foreach (var c in canales)
        {
            switch (c.Channel)
            {
                case InkChannel.Cyan: cal.C = c.Factor; break;
                case InkChannel.Magenta: cal.M = c.Factor; break;
                case InkChannel.Yellow: cal.Y = c.Factor; break;
                default: cal.K = c.Factor; break;
            }
        }
        if (caja is { } paginas)
            costos.Maintenance.PagesPerBox = paginas;
        cal.Enabled = true;
        cal.UpdatedUtc = Ahora;
        await new RegistroImpresiones(db, pos).GuardarCostosAsync(sucursalId, costos, ct);
        return await CalibracionAsync(sucursalId, ct);
    }

    private static string Calidad(CalibrationQuality q) => q switch
    {
        CalibrationQuality.NoData => "Sin datos",
        CalibrationQuality.Collecting => "Juntando datos",
        CalibrationQuality.Preliminary => "Preliminar",
        _ => "Confiable",
    };

    private async Task<List<PrinterStatus>> Historial(Guid sucursalId, DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct) =>
        (await Lecturas.AsNoTracking()
            .Where(l => l.SucursalId == sucursalId && l.TomadaUtc >= desdeUtc && l.TomadaUtc <= hastaUtc)
            .OrderBy(l => l.TomadaUtc).ToListAsync(ct))
        .Select(l => new PrinterStatus
        {
            TakenUtc = l.TomadaUtc, InkK = l.TintaK, InkC = l.TintaC, InkM = l.TintaM, InkY = l.TintaY, Maintenance = l.Mantenimiento,
            LifeCount = l.Contador, ColorCount = l.ContadorColor, MonoCount = l.ContadorBN, Serial = l.Serie,
        }).ToList();
}
