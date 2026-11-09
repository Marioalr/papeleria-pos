using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Los trabajos de impresión de la sucursal (docs/fase-1-impresion.md): el agente de cada caja los mide y aquí se
/// les pone costo y precio con el modelo de Control Print y los costos de la impresora de donde salieron (los de la sucursal con lo
/// propio de la impresora encima, <see cref="CostosDeImpresora"/>; 2026-11-03). Trabaja sobre la base que tenga el mapeo del módulo
/// (<see cref="ModeloImpresiones"/>); lo que necesita del POS (la hora y las cajas) lo pide por <paramref name="pos"/>.
/// </summary>
public sealed class RegistroImpresiones(DbContext db, IPosParaImpresiones pos)
{
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DbSet<ConfiguracionImpresion> Configuraciones => db.Set<ConfiguracionImpresion>();
    private DbSet<HistorialCostosImpresion> Historial => db.Set<HistorialCostosImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Los costos de la sucursal; sin configurar, los valores por omisión de Control Print (WF-6590). Son los de la impresora 1 y de las
    /// que usan sus cartuchos; los de una impresora en particular, <see cref="ServicioImpresora.CostosAsync"/>.
    /// </summary>
    public async Task<CostSettings> CostosAsync(Guid sucursalId, CancellationToken ct = default) =>
        await Configuraciones.FindAsync([sucursalId], ct) is { } c
            ? JsonSerializer.Deserialize<CostSettings>(c.CostosJson, CostSettings.Json) ?? new CostSettings()
            : new CostSettings();

    /// <summary>
    /// Guarda la configuración (por ejemplo, la importada de Control Print). Los costos que reemplaza se quedan en su historial
    /// (<see cref="HistorialCostosImpresion"/>, 2026-10-29), con sus fechas y de dónde vienen los nuevos: nada se pierde, ni la
    /// calibración de la tinta.
    /// </summary>
    /// <param name="origen">De dónde vienen los nuevos («Control Print», «Configuración»…), para el historial.</param>
    public async Task GuardarCostosAsync(Guid sucursalId, CostSettings costos, CancellationToken ct = default, string? origen = null)
    {
        var json = JsonSerializer.Serialize(costos, CostSettings.Json);
        if (await Configuraciones.FindAsync([sucursalId], ct) is { } existente)
        {
            if (existente.CostosJson != json)
                Historial.Add(new HistorialCostosImpresion
                {
                    SucursalId = sucursalId, DesdeUtc = existente.ModificadoUtc, HastaUtc = Ahora, PorLosDe = origen, CostosJson = existente.CostosJson,
                });
            existente.CostosJson = json;
            existente.ModificadoUtc = Ahora;
        }
        else
        {
            Configuraciones.Add(new ConfiguracionImpresion { SucursalId = sucursalId, CostosJson = json, ModificadoUtc = Ahora });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Los costos de antes de la sucursal, lo más nuevo primero (lo propio de cada impresora que se reemplazó tiene otra forma y no va aquí).</summary>
    public async Task<List<HistorialCostosImpresion>> HistorialCostosAsync(Guid sucursalId, CancellationToken ct = default) =>
        (await Historial.AsNoTracking().Where(h => h.SucursalId == sucursalId && h.ImpresoraId == null).ToListAsync(ct))
        .OrderByDescending(h => h.HastaUtc).ToList();

    /// <summary>
    /// Guarda los costos de la sucursal solo si son otros que los guardados (los de Control Print, cada vez que se leen: así, si al
    /// arrancar no contesta, se cobra con los últimos que dio). Verdadero si cambiaron.
    /// </summary>
    public async Task<bool> GuardarCostosSiCambiaronAsync(Guid sucursalId, CostSettings costos, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(costos, CostSettings.Json);
        if (await Configuraciones.FindAsync([sucursalId], ct) is { } existente && existente.CostosJson == json)
            return false;
        await GuardarCostosAsync(sucursalId, costos, ct, "Control Print");
        return true;
    }

    /// <summary>Un trabajo que llegó del agente. Si ya estaba (el agente lo volvió a mandar), no se repite.</summary>
    public Task<TrabajoImpresion> RegistrarAsync(TrabajoMedidoDto t, CancellationToken ct = default) => RegistrarAsync(t, null, ct);

    /// <summary>
    /// Un trabajo que llegó del agente, con su precio puesto con los costos de la impresora de donde salió (la que dice la caja,
    /// <see cref="TrabajoMedidoDto.Impresora"/>, o la primera; 2026-11-03): los de la sucursal, o <paramref name="costos"/> (en la caja 2,
    /// los de Control Print leídos de su servidor, 2026-10-25), con lo propio de la impresora encima. Si ya estaba (el agente lo volvió a
    /// mandar), no se repite.
    /// <para>
    /// El de un trámite (2026-10-31) llega ya marcado por la caja que lo imprimió desde su panel (<see cref="TrabajoMedidoDto.Tramite"/>);
    /// aquí no se adivina por el nombre (revisión de servicios/curp: un archivo de un cliente llamado «T-2026.pdf» salía gratis). Nunca
    /// guarda su miniatura, aunque llegue con una: sería el documento de un cliente. Su copia incluida no queda por cobrar ni lleva precio
    /// (se cobra con el trámite, <see cref="EstadoCobroImpresion.EnTramite"/>), solo su costo; sus copias de más quedan por cobrar como
    /// cualquier impresión. Tampoco guarda la miniatura de uno sin marca que se llame exactamente como uno de un trámite
    /// (<see cref="TrabajoDeTramite.SeLlamaComoUnTramite"/>, segunda revisión): ese sí lleva su precio y sale en F2.
    /// </para>
    /// </summary>
    public async Task<TrabajoImpresion> RegistrarAsync(TrabajoMedidoDto t, CostSettings? costos, CancellationToken ct = default)
    {
        if (await Trabajos.FindAsync([t.Id], ct) is { } existente)
            return existente;
        var caja = await pos.CajaAsync(t.CajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var impresoras = new ServicioImpresora(db, pos);
        var (impresora, nota) = await impresoras.ParaElTrabajoAsync(caja.SucursalId, t.Impresora, ct);
        if (nota is not null && t.Impresora is { } numero)
            // Un caja.json con un número que no es de la sucursal (o de una dada de baja): el trabajo no se pierde, y se avisa una vez por caja y número.
            pos.Avisar(caja.SucursalId, AvisoImpresiones.Impresora,
                $"Impresora que no se usa\nLa {caja.Nombre} manda trabajos de la impresora {numero}, que no existe en la sucursal o está dada de baja: se "
                + $"anotan en la {impresora.Numero} ({impresora.Nombre}). Revisa «ColasImpresion» en su caja.json.",
                $"impresora:no-existe:{caja.Id:N}:{numero}");
        var costo = CostCalculator.Calculate(Analisis(t), await impresoras.CostosAsync(impresora, costos, ct));
        var tramite = t.Tramite;
        var incluido = tramite is { CopiasExtra: false };

        var trabajo = new TrabajoImpresion
        {
            Id = t.Id, SucursalId = caja.SucursalId, ImpresoraId = impresora.Id, CajaId = caja.Id, AtendioId = t.AtendioId, Pc = t.Pc, Cola = t.Cola,
            SpoolId = t.SpoolId,
            Documento = string.IsNullOrWhiteSpace(t.Documento) ? "(sin nombre)" : t.Documento.Trim(), UsuarioWindows = t.UsuarioWindows,
            ImpresoUtc = t.ImpresoUtc, EnviadoUtc = t.EnviadoUtc, Estado = t.Estado, Tipo = TipoTrabajoImpresion.Impresion,
            Paginas = costo.LogicalPages, Copias = costo.Copies, Caras = costo.Sides, CarasColor = costo.ColorSides, Hojas = costo.Sheets,
            Color = t.Ajustes.Color, Duplex = t.Ajustes.Duplex, Tamano = costo.PaperSize.ToString(), TipoPapel = costo.MediaName,
            Calidad = t.Ajustes.Calidad, Medido = t.Medido, Nota = nota is null ? t.Nota : string.IsNullOrWhiteSpace(t.Nota) ? nota : $"{t.Nota} {nota}",
            Miniatura = tramite is null && !TrabajoDeTramite.SeLlamaComoUnTramite(t.Documento) && t.MiniaturaBase64 is { Length: > 0 } m
                ? Convert.FromBase64String(m) : null,
            CostoTintaCentavos = Centavos(costo.InkCost), CostoPapelCentavos = Centavos(costo.PaperCost),
            CostoOtrosCentavos = Centavos(costo.MaintenanceCost + costo.PowerCost + costo.ExtrasCost + costo.DepreciationCost),
            CostoCentavos = Centavos(costo.Total), MargenPorcentaje = costo.MarginPercent, PrecioCentavos = incluido ? 0 : Centavos(costo.Price),
            DetalleJson = JsonSerializer.Serialize(new { t.Ajustes, t.Paginas, Margen = costo.MarginTier }, CostSettings.Json),
            TintaC = costo.InkC, TintaM = costo.InkM, TintaY = costo.InkY, TintaK = costo.InkK,
            Cobro = incluido ? EstadoCobroImpresion.EnTramite : EstadoCobroImpresion.PorCobrar,
        };
        Trabajos.Add(trabajo);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    /// <summary>Lo que midió el agente, en los términos del cálculo de Control Print.</summary>
    public static JobAnalysis Analisis(TrabajoMedidoDto t) => Analisis(t.Ajustes, t.Paginas, t.Medido, t.Nota);

    private static JobAnalysis Analisis(AjustesTrabajoDto a, IEnumerable<CoberturaPaginaDto> paginasMedidas, bool medido, string? nota)
    {
        var ajustes = new JobSettings
        {
            Quality = a.Calidad, MediaType = a.TipoPapel, MediaSizeName = a.Tamano, MediaWidthMm = a.AnchoMm, MediaHeightMm = a.AltoMm,
            Color = a.Color, Duplex = a.Duplex, Copies = a.Copias, PagesPerSheet = a.PaginasPorHoja, FromPrintTicket = a.DelPrintTicket,
        };
        var paginas = paginasMedidas.Select(p => new PageCoverage(p.C, p.M, p.Y, p.K, p.Gris, p.AreaPulg2, p.ConColor)).ToList();
        return new JobAnalysis(ajustes, paginas, medido ? AnalysisLevel.Measured : AnalysisLevel.Estimated, null, nota, TimeSpan.Zero);
    }

    /// <summary>Si ya hay algún trabajo registrado, de cualquier sucursal (la base de la prueba no los repite).</summary>
    public Task<bool> HayTrabajosAsync(CancellationToken ct = default) => Trabajos.AnyAsync(ct);

    /// <summary>Lo más que manda F2 de una vez (como las de Control Print): lo más nuevo; las demás salen al resolver estas.</summary>
    public const int MaximoPorCobrar = 200;

    /// <summary>
    /// Lo que falta cobrar en la sucursal, lo más nuevo primero y hasta <see cref="MaximoPorCobrar"/>, sin las miniaturas (unos 50 KB cada
    /// una: F2 las pide aparte, por su Id, al mostrarlas). La <c>Miniatura</c> de cada uno solo dice si tiene (vacía, de cero bytes). Solo
    /// para leer.
    /// </summary>
    public async Task<List<TrabajoImpresion>> PorCobrarAsync(Guid sucursalId, CancellationToken ct = default) =>
        await Trabajos.AsNoTracking().Where(t => t.SucursalId == sucursalId && t.Cobro == EstadoCobroImpresion.PorCobrar)
            .OrderByDescending(t => t.ImpresoUtc).Take(MaximoPorCobrar)
            .Select(t => new TrabajoImpresion
                {
                    Id = t.Id, SucursalId = t.SucursalId, ImpresoraId = t.ImpresoraId, Tipo = t.Tipo, CajaId = t.CajaId, AtendioId = t.AtendioId, Pc = t.Pc,
                    Cola = t.Cola, SpoolId = t.SpoolId, Documento = t.Documento, UsuarioWindows = t.UsuarioWindows, ImpresoUtc = t.ImpresoUtc,
                    EnviadoUtc = t.EnviadoUtc, Estado = t.Estado, Paginas = t.Paginas, Copias = t.Copias, Caras = t.Caras, CarasColor = t.CarasColor,
                    Hojas = t.Hojas, Color = t.Color, Duplex = t.Duplex, Tamano = t.Tamano, TipoPapel = t.TipoPapel, Calidad = t.Calidad,
                    Medido = t.Medido, Nota = t.Nota, TintaC = t.TintaC, TintaM = t.TintaM, TintaY = t.TintaY, TintaK = t.TintaK,
                    CarasContadas = t.CarasContadas, CostoTintaCentavos = t.CostoTintaCentavos, CostoPapelCentavos = t.CostoPapelCentavos,
                    CostoOtrosCentavos = t.CostoOtrosCentavos, CostoCentavos = t.CostoCentavos, MargenPorcentaje = t.MargenPorcentaje,
                    PrecioCentavos = t.PrecioCentavos, PrecioCompletoCentavos = t.PrecioCompletoCentavos, DetalleJson = t.DetalleJson, Cobro = t.Cobro,
                    VentaId = t.VentaId, CobroUtc = t.CobroUtc, MotivoNoVendida = t.MotivoNoVendida, NotaNoVendida = t.NotaNoVendida, MarcoId = t.MarcoId,
                    Miniatura = t.Miniatura != null ? Array.Empty<byte>() : null,
                }).ToListAsync(ct);

    /// <summary>La miniatura de una impresión que falta cobrar (vacía si no tiene, o si ya se cobró).</summary>
    public async Task<byte[]?> MiniaturaAsync(Guid id, CancellationToken ct = default) =>
        await Trabajos.AsNoTracking().Where(t => t.Id == id && t.Cobro == EstadoCobroImpresion.PorCobrar).Select(t => t.Miniatura).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Cuántos faltan por cobrar en la sucursal y una firma que cambia cuando cambian (uno nuevo, uno que se cobró, un precio de
    /// «solo lo que salió», lo que contó la impresora): sin leer los trabajos con sus miniaturas, para el chip de arriba y para que F2
    /// sepa si tiene que volver a leer.
    /// </summary>
    public async Task<(int Cuantos, long Firma)> CuantosPorCobrarAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var porCobrar = await Trabajos.AsNoTracking().Where(t => t.SucursalId == sucursalId && t.Cobro == EstadoCobroImpresion.PorCobrar)
            .Select(t => new { t.Id, t.PrecioCentavos, t.CarasContadas }).ToListAsync(ct);
        var firma = 17L;
        foreach (var t in porCobrar.OrderBy(t => t.Id))
            firma = unchecked(firma * 31 + t.Id.GetHashCode() * 7 + t.PrecioCentavos.GetHashCode() * 3 + (t.CarasContadas ?? -1));
        return (porCobrar.Count, firma);
    }

    public async Task<TrabajoImpresion> NoVendidaAsync(Guid trabajoId, SolicitudNoVendida s, CancellationToken ct = default)
    {
        var trabajo = await Trabajos.FindAsync([trabajoId], ct) ?? throw new InvalidOperationException("La impresión no existe.");
        trabajo.MarcarNoVendida(s.Motivo, s.Nota, s.UsuarioId, Ahora);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    /// <summary>Con un atasco o una cancelación: se cobra solo lo que contó la impresora.</summary>
    public async Task<TrabajoImpresion> SoloLoQueSalioAsync(Guid trabajoId, CancellationToken ct = default)
    {
        var trabajo = await Trabajos.FindAsync([trabajoId], ct) ?? throw new InvalidOperationException("La impresión no existe.");
        trabajo.CobrarSoloLoQueSalio(PrecioSoloLoQueSalio(trabajo, await CostosAsync(trabajo.SucursalId, ct)) ?? 0);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    /// <summary>
    /// Precio de solo lo que salió: el costo ya ajustado a las caras que contó la impresora, con el margen del trabajo,
    /// redondeo y cobro mínimo; nunca más que el trabajo completo. Vacío si no aplica (salió completo o nada).
    /// </summary>
    public static long? PrecioSoloLoQueSalio(TrabajoImpresion t, CostSettings cfg)
    {
        if (!t.FaltaronCaras || t.CarasContadas is not ({ } contadas and > 0) || t.PrecioCompletoCentavos is not null || t.CostoCentavos <= 0)
            return null;
        var precio = CostCalculator.PriceFor(t.CostoCentavos / 100m, t.MargenPorcentaje, cfg.RoundTo, t.CostoPapelCentavos / 100m, cfg.PaperMarginPercent);
        if ((cfg.MinimumApplies == MinimumScope.AllJobs || contadas == 1) && precio < cfg.MinimumPrice)
            precio = cfg.MinimumPrice;
        return Math.Min(Centavos(precio), t.PrecioCentavos);
    }

    // ── Cotizar y copias ─────────────────────────────────────────────────────

    /// <summary>Tamaños que se cotizan y se copian (los del papel que se vende).</summary>
    public static readonly IReadOnlyList<string> Tamanos = QuoteCalculator.Sizes.Select(s => s.ToString()).ToList();

    private static readonly IReadOnlyList<OpcionDto> Calidades =
    [
        new("Draft", "Borrador"), new("Standard", "Normal"), new("StandardVivid", "Normal (colores vivos)"), new("HighQuality", "Alta"),
    ];

    public async Task<OpcionesImpresionDto> OpcionesAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var costos = await CostosAsync(sucursalId, ct);
        return new OpcionesImpresionDto(costos.Media.Select(m => new OpcionDto(m.Key, m.Name)).ToList(), Tamanos, Calidades);
    }

    /// <summary>El precio de un PDF que midió la caja: el mismo cálculo que una impresión real (mínimo, mayoreo y redondeo).</summary>
    public async Task<CotizacionDto> CotizarAsync(SolicitudCotizacion s, bool conCosto, CancellationToken ct = default)
    {
        if (s.Paginas.Count == 0)
            throw new ArgumentException("El documento no tiene páginas.");
        var o = s.Opciones;
        var opciones = new QuoteOptions
        {
            MediaType = o.TipoPapel, Size = Tamano(o.Tamano), Color = o.Color, Quality = o.Calidad, Duplex = o.Duplex,
            Copies = o.Copias is >= 1 and <= 9999 ? o.Copias : throw new ArgumentException("Escribe cuántas copias (de 1 a 9,999)."),
            PagesPerSheet = o.PaginasPorHoja, PageRange = o.Rango,
        };
        var medidas = s.Paginas.Select(p => new PageCoverage(p.C, p.M, p.Y, p.K, p.Gris, p.AreaPulg2, p.ConColor)).ToList();
        var r = QuoteCalculator.Calculate(medidas, opciones, await CostosDeAsync(s.SucursalId, s.Impresora, ct));
        var c = r.Cost;
        return new CotizacionDto(Centavos(c.Price), c.MinimumApplied, r.Pages.Count, c.Copies, c.Sides, c.Sheets, c.ColorSides, c.PaperSize.ToString(),
            c.MediaName,
            r.PageCosts.Select(p => new PaginaCotizadaDto(r.Pages[p.Page - 1], p.Blank ? "En blanco" : p.Color ? "Color" : "B/N", p.Price * 100,
                conCosto ? p.Cost * 100 : null)).ToList(),
            c.Warnings, conCosto ? Costo(c) : null);
    }

    /// <summary>
    /// Los costos de la impresora con ese número en la sucursal (2026-11-03); sin número, o si no existe, los de la primera. Sin
    /// impresoras, los de la sucursal.
    /// </summary>
    private async Task<CostSettings> CostosDeAsync(Guid sucursalId, int? numero, CancellationToken ct)
    {
        var impresoras = new ServicioImpresora(db, pos);
        var impresora = numero is { } n ? await impresoras.ImpresoraAsync(sucursalId, n, ct) : null;
        impresora ??= await impresoras.PrimeraAsync(sucursalId, ct: ct);
        return impresora is null ? await CostosAsync(sucursalId, ct) : await impresoras.CostosAsync(impresora, ct: ct);
    }

    /// <summary>
    /// Precio sugerido de copias hechas en el panel de una impresora (su número; vacío, la primera): tinta típica (5 % por canal), el
    /// papel, mínimo y redondeo, con los costos de esa impresora.
    /// </summary>
    public async Task<(CostBreakdown Costo, string Descripcion)> CalcularCopiasAsync(
        Guid sucursalId, int cantidad, bool color, string tamano, bool duplex, int? impresora = null, CancellationToken ct = default)
    {
        if (cantidad is < 1 or > 9999)
            throw new ArgumentException("Escribe cuántas copias (de 1 a 9,999).");
        var tamanoPapel = Tamano(tamano);
        var analisis = AnalisisCopias(cantidad, color, tamanoPapel, duplex);
        var descripcion = $"{cantidad} copia{(cantidad == 1 ? "" : "s")} {(color ? "a color" : "en B/N")}" + (duplex ? " a doble cara" : "")
            + (tamanoPapel == PaperSize.Carta ? "" : $" ({tamanoPapel})");
        return (CostCalculator.Calculate(analisis, await CostosDeAsync(sucursalId, impresora, ct)), descripcion);
    }

    // ── Las curvas del margen: probarlas y compararlas antes de usarlas (2026-11-08) ──

    /// <summary>Lo más que se prueba de una vez en el probador.</summary>
    public const int CarasMaximasDelProbador = 100_000;

    /// <summary>
    /// El probador de las curvas: un trabajo de tantas caras, todas con esa tinta (a color, repartida pareja entre los cuatro canales; en B/N,
    /// toda negra), con los costos de la impresora (vacío: la primera) y las curvas escritas, aunque no estén guardadas ni elegidas. También
    /// da el precio con el margen de hoy. Quién puede lo revisa quien llama.
    /// </summary>
    public async Task<PruebaCurvasDto> ProbarCurvasAsync(Guid sucursalId, SolicitudProbarCurvas s, CancellationToken ct = default)
    {
        var curvas = CostosEnLaConfiguracion.Curvas(s.Curvas ?? throw new ArgumentException("Faltan las curvas."));
        if (s.Caras is < 1 or > CarasMaximasDelProbador)
            throw new ArgumentException($"Escribe cuántas caras (de 1 a {CarasMaximasDelProbador:N0}).");
        var tope = s.Color ? MarginCurves.MaxCoverage : 100m;
        if (s.Tinta < 0 || s.Tinta > tope)
            throw new ArgumentException(s.Color
                ? $"La tinta de cada cara va de 0 a {tope:0} % (los cuatro colores llenos)."
                : "En B/N la tinta de cada cara va de 0 a 100 % (toda negra).");
        var hoy = await CostosDeAsync(sucursalId, s.Impresora, ct);
        var conCurvas = ConCurvas(hoy, curvas);
        var opciones = new QuoteOptions { Size = Tamano(s.Tamano), Color = s.Color, MediaType = string.IsNullOrWhiteSpace(s.TipoPapel) ? "Plain" : s.TipoPapel };
        var ajustes = QuoteCalculator.SettingsFor(opciones) with { Copies = s.Caras };
        double fraccion = (double)s.Tinta / 100;
        var pagina = s.Color
            ? new PageCoverage(fraccion / 4, fraccion / 4, fraccion / 4, fraccion / 4, fraccion, Area(ajustes.MediaWidthMm, ajustes.MediaHeightMm), true)
            : new PageCoverage(0, 0, 0, fraccion, fraccion, Area(ajustes.MediaWidthMm, ajustes.MediaHeightMm), false);
        var analisis = new JobAnalysis(ajustes, [pagina], AnalysisLevel.Measured, null, null, TimeSpan.Zero);
        var c = CostCalculator.Calculate(analisis, conCurvas);
        var deHoy = CostCalculator.Calculate(analisis, hoy);
        var porPagina = PageCosts.Compute(c, ajustes, [pagina])[0];
        return new PruebaCurvasDto(
            MarginCurves.Evaluate(curvas.ByCoverage, CostCalculator.SideCoverage([pagina], s.Color, 1)) ?? 0, c.SidesFactor ?? 100m,
            c.PageMargins is [var margen, ..] ? Math.Round(margen, 2) : c.MarginPercent, Math.Round(porPagina.Cost * 100, 2),
            Math.Round(porPagina.Price * 100, 2), Centavos(c.Price), c.MinimumApplied, Centavos(c.InkCost), Centavos(c.PaperCost),
            Centavos(c.MaintenanceCost + c.PowerCost + c.ExtrasCost + c.DepreciationCost), Centavos(c.Total), Centavos(deHoy.Price), deHoy.MarginPercent,
            deHoy.MarginTier);
    }

    /// <summary>Unos costos con las curvas elegidas: así se cobraría si se eligen.</summary>
    private static CostSettings ConCurvas(CostSettings costos, MarginCurves curvas)
    {
        var c = costos.Clone();
        (c.Scaling, c.Curves) = (ScalingMode.ByCurves, curvas.Clone());
        return c;
    }

    /// <summary>Cuántas de las diferencias más grandes se enseñan.</summary>
    public const int MayoresDiferencias = 10;

    /// <summary>
    /// Lo impreso en los últimos días (impresiones y copias del panel; sin las no vendidas ni las incluidas en un trámite) con las curvas
    /// escritas: cuánto se cobró (o se pide, si falta cobrarlo), cuánto daría hoy con los costos y el margen de hoy, y cuánto con las curvas, con
    /// los costos de la impresora de donde salió cada uno. Con <see cref="SolicitudCompararCurvas.Impresora"/>, solo lo de esa impresora; sin
    /// ella, lo de las que cobran con los márgenes de la sucursal. Quién puede lo revisa quien llama.
    /// </summary>
    public async Task<ComparacionCurvasDto> CompararCurvasAsync(Guid sucursalId, SolicitudCompararCurvas s, CancellationToken ct = default)
    {
        var curvas = CostosEnLaConfiguracion.Curvas(s.Curvas ?? throw new ArgumentException("Faltan las curvas."));
        var dias = s.Dias is >= 1 and <= 90 ? s.Dias : throw new ArgumentException("Se compara con 1 a 90 días.");
        var desde = Ahora.AddDays(-dias);
        var servicio = new ServicioImpresora(db, pos);
        var impresoras = await servicio.ImpresorasAsync(sucursalId, tambienDeBaja: true, ct);
        var primera = await servicio.PrimeraAsync(sucursalId, ct: ct);
        var deLaSucursal = await CostosAsync(sucursalId, ct);
        var costos = new Dictionary<Guid, (CostSettings Hoy, CostSettings Curvas, bool Propios, int Numero, string Nombre)>();
        foreach (var i in impresoras)
        {
            var propios = ServicioImpresora.Propios(i);
            var hoy = CostosDeImpresora.Aplicar(deLaSucursal, propios);
            costos[i.Id] = (hoy, ConCurvas(hoy, curvas), propios?.MargenesPropios == true, i.Numero, i.Nombre);
        }
        var sinImpresora = (Hoy: deLaSucursal, Curvas: ConCurvas(deLaSucursal, curvas), Propios: false, Numero: 1, Nombre: "");
        var trabajos = await Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.ImpresoUtc >= desde
                && (t.Cobro == EstadoCobroImpresion.Cobrada || t.Cobro == EstadoCobroImpresion.PorCobrar))
            .Select(t => new { t.Id, t.ImpresoraId, t.Tipo, t.Documento, t.ImpresoUtc, t.Caras, t.PrecioCentavos, t.DetalleJson, t.Medido, t.Paginas, t.Color, t.Duplex, t.Tamano })
            .ToListAsync(ct);
        var nombres = await servicio.NombresAsync(sucursalId, ct);
        var filas = new List<DiferenciaCurvasDto>();
        long cobrado = 0, conHoy = 0, conCurvas = 0;
        foreach (var t in trabajos.OrderBy(t => t.ImpresoUtc))
        {
            var deEsta = (t.ImpresoraId ?? primera?.Id) is { } id && costos.TryGetValue(id, out var suyos) ? suyos : sinImpresora;
            if (s.Impresora is { } numero ? deEsta.Numero != numero || !deEsta.Propios : deEsta.Propios)
                continue;
            if (AnalisisGuardado(t.Tipo, t.DetalleJson, t.Medido, t.Paginas, t.Color, t.Duplex, t.Tamano) is not { } analisis)
                continue;
            var hoy = CostCalculator.Calculate(analisis, deEsta.Hoy);
            var nuevo = CostCalculator.Calculate(analisis, deEsta.Curvas);
            cobrado += t.PrecioCentavos;
            conHoy += Centavos(hoy.Price);
            conCurvas += Centavos(nuevo.Price);
            filas.Add(new DiferenciaCurvasDto(HoraMexico.ALocal(t.ImpresoUtc), t.Documento, nombres(t.ImpresoraId), nuevo.Sides, nuevo.CoveragePercent ?? 0,
                t.PrecioCentavos, Centavos(hoy.Price), Centavos(nuevo.Price), nuevo.MarginTier));
        }
        var mayores = filas.Where(f => f.ConCurvasCentavos != f.HoyCentavos)
            .OrderByDescending(f => Math.Abs(f.ConCurvasCentavos - f.HoyCentavos)).ThenByDescending(f => f.FechaLocal).Take(MayoresDiferencias).ToList();
        return new ComparacionCurvasDto(dias, filas.Count, cobrado, conHoy, conCurvas, mayores);
    }

    /// <summary>Lo que se midió de un trabajo guardado (su detalle), para volver a ponerle precio; vacío si no se puede leer.</summary>
    private static JobAnalysis? AnalisisGuardado(TipoTrabajoImpresion tipo, string detalleJson, bool medido, int paginas, bool color, bool duplex, string tamano)
    {
        try
        {
            if (tipo == TipoTrabajoImpresion.Copia)
            {
                var copias = JsonSerializer.Deserialize<DetalleCopias>(detalleJson, CostSettings.Json);
                var cantidad = copias?.Cantidad is > 0 and <= 9999 ? copias.Cantidad : paginas;
                return cantidad is < 1 or > 9999 ? null : AnalisisCopias(cantidad, color, Enum.TryParse<PaperSize>(tamano, true, out var t) ? t : PaperSize.Carta, duplex);
            }
            var detalle = JsonSerializer.Deserialize<DetalleImpresion>(detalleJson, CostSettings.Json);
            return detalle?.Ajustes is { } ajustes && detalle.Paginas is { Count: > 0 } medidas ? Analisis(ajustes, medidas, medido, null) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record DetalleImpresion(AjustesTrabajoDto? Ajustes, List<CoberturaPaginaDto>? Paginas);

    private sealed record DetalleCopias(int Cantidad);

    /// <summary>Unas copias del panel, como las mide el cálculo: tinta típica (5 % por canal) en cada cara.</summary>
    private static JobAnalysis AnalisisCopias(int cantidad, bool color, PaperSize tamanoPapel, bool duplex)
    {
        var (ancho, alto, nombre) = QuoteCalculator.Dimensions(tamanoPapel);
        var ajustes = new JobSettings { Color = color, Duplex = duplex, MediaWidthMm = ancho, MediaHeightMm = alto, MediaSizeName = nombre };
        var caras = duplex ? cantidad * 2 : cantidad;
        var pagina = PageCoverage.Estimated(Area(ancho, alto), color, new AnalysisOptions().FallbackCoverage);
        return new JobAnalysis(ajustes, Enumerable.Repeat(pagina, caras).ToList(), AnalysisLevel.Estimated, null, null, TimeSpan.Zero);
    }

    /// <summary>El área imprimible (pulgadas²) de un papel, con 3 mm de orilla, como al cotizar.</summary>
    private static double Area(double anchoMm, double altoMm) => Math.Max(0, anchoMm - 6) * Math.Max(0, altoMm - 6) / (25.4 * 25.4);

    /// <summary>
    /// Copias cobradas en el mostrador: quedan como un trabajo más, por cobrar, para la línea "Impresiones", de la impresora en cuyo panel
    /// se hicieron (cuentan contra su contador).
    /// <para>
    /// Si en esa impresora hay una tarjeta «Copias o celular» por cobrar (2026-11-08), sus caras se toman de ella primero (la tarjeta se
    /// encoge o se quita): son las mismas caras, y no se cobran dos veces.
    /// </para>
    /// </summary>
    public async Task<TrabajoImpresion> RegistrarCopiasAsync(SolicitudCopias s, CancellationToken ct = default)
    {
        if (await Trabajos.FindAsync([s.Id], ct) is { } existente)
            return existente;
        var trabajo = await NuevasCopiasAsync(s, ct);
        await db.SaveChangesAsync(ct);
        await new TarjetasDelContador(db, pos).ConsumirAsync(trabajo, ct: ct);
        return trabajo;
    }

    /// <summary>Las copias, en el contexto y sin guardar (también las que salen al partir una tarjeta «Copias o celular»).</summary>
    internal async Task<TrabajoImpresion> NuevasCopiasAsync(SolicitudCopias s, CancellationToken ct = default)
    {
        if (s.CobradoCentavos < 0)
            throw new ArgumentException("Lo cobrado no puede ser negativo.");
        var caja = await pos.CajaAsync(s.CajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var (impresora, _) = await new ServicioImpresora(db, pos).ParaElTrabajoAsync(caja.SucursalId, s.Impresora, ct);
        var (costo, descripcion) = await CalcularCopiasAsync(caja.SucursalId, s.Cantidad, s.Color, s.Tamano, s.Duplex, impresora.Numero, ct);
        var sugerido = Centavos(costo.Price);
        var cobrado = s.CobradoCentavos ?? sugerido;
        var ahora = Ahora;
        var trabajo = new TrabajoImpresion
        {
            Id = s.Id, SucursalId = caja.SucursalId, ImpresoraId = impresora.Id, CajaId = caja.Id, AtendioId = s.AtendioId, Tipo = TipoTrabajoImpresion.Copia,
            Pc = caja.Nombre, Cola = "Copias en la impresora", Documento = descripcion, ImpresoUtc = ahora, EnviadoUtc = ahora,
            Estado = EstadoTrabajo.Impreso,
            Paginas = s.Cantidad, Copias = 1, Caras = costo.Sides, CarasColor = costo.ColorSides, Hojas = costo.Sheets, Color = s.Color, Duplex = s.Duplex,
            Tamano = costo.PaperSize.ToString(), TipoPapel = costo.MediaName, Calidad = "Standard", Medido = false,
            Nota = string.IsNullOrWhiteSpace(s.Nota) ? null : s.Nota.Trim(),
            CostoTintaCentavos = Centavos(costo.InkCost), CostoPapelCentavos = Centavos(costo.PaperCost),
            CostoOtrosCentavos = Centavos(costo.MaintenanceCost + costo.PowerCost + costo.ExtrasCost + costo.DepreciationCost),
            CostoCentavos = Centavos(costo.Total), MargenPorcentaje = costo.MarginPercent, PrecioCentavos = cobrado,
            PrecioCompletoCentavos = cobrado == sugerido ? null : sugerido,
            DetalleJson = JsonSerializer.Serialize(new { s.Cantidad, s.Color, s.Tamano, s.Duplex, Margen = costo.MarginTier }, CostSettings.Json),
            TintaC = costo.InkC, TintaM = costo.InkM, TintaY = costo.InkY, TintaK = costo.InkK,
            Cobro = EstadoCobroImpresion.PorCobrar,
        };
        Trabajos.Add(trabajo);
        return trabajo;
    }

    // ── Lo que pasó en el día y contra el contador ───────────────────────────

    public async Task<List<TrabajoImpresion>> DelDiaAsync(Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return await Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.ImpresoUtc >= desde && t.ImpresoUtc < hasta)
            .OrderByDescending(t => t.ImpresoUtc).ToListAsync(ct);
    }

    /// <summary>Caras registradas en la sucursal entre dos momentos, de todas sus impresoras.</summary>
    public RegisteredSides CarasRegistradas(Guid sucursalId, DateTime desdeUtc, DateTime hastaUtc) =>
        CarasRegistradas(Trabajos.AsNoTracking().Where(t => t.SucursalId == sucursalId), desdeUtc, hastaUtc);

    /// <summary>
    /// Caras registradas entre dos lecturas del contador de una impresora (<paramref name="deLaImpresora"/>: lo que salió de ella,
    /// <see cref="ServicioImpresora.TrabajosDe"/>): impresiones de las PC (las que salieron) y copias cobradas en su panel.
    /// </summary>
    public RegisteredSides CarasRegistradas(IQueryable<TrabajoImpresion> deLaImpresora, DateTime desdeUtc, DateTime hastaUtc)
    {
        var trabajos = deLaImpresora
            .Where(t => t.ImpresoUtc > desdeUtc && t.ImpresoUtc <= hastaUtc)
            .Select(t => new { t.Tipo, t.Caras, t.CarasColor, t.CarasContadas, t.Estado }).ToList();
        int Salieron(int caras, int? contadas, EstadoTrabajo estado) => contadas ?? (estado == EstadoTrabajo.Impreso ? caras : 0);
        var impresas = trabajos.Where(t => t.Tipo == TipoTrabajoImpresion.Impresion).ToList();
        var copias = trabajos.Where(t => t.Tipo == TipoTrabajoImpresion.Copia).ToList();
        return new RegisteredSides(
            impresas.Sum(t => Salieron(t.Caras, t.CarasContadas, t.Estado)), impresas.Sum(t => Math.Min(t.CarasColor, Salieron(t.Caras, t.CarasContadas, t.Estado))),
            copias.Sum(t => t.Caras), copias.Sum(t => t.CarasColor));
    }

    public static CostoImpresionDto Costo(TrabajoImpresion t) =>
        new(t.CostoTintaCentavos, t.CostoPapelCentavos, t.CostoOtrosCentavos, t.CostoCentavos, t.MargenPorcentaje, Margen(t.DetalleJson));

    private static CostoImpresionDto Costo(CostBreakdown c) =>
        new(Centavos(c.InkCost), Centavos(c.PaperCost), Centavos(c.MaintenanceCost + c.PowerCost + c.ExtrasCost + c.DepreciationCost),
            Centavos(c.Total), c.MarginPercent, c.MarginTier);

    /// <summary>De dónde salió el margen de un trabajo («curva: …», «mayoreo: …»; 2026-11-08), guardado con su detalle; vacío en los de antes.</summary>
    private static string? Margen(string? detalleJson)
    {
        if (detalleJson is null || !detalleJson.Contains("\"Margen\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var detalle = JsonDocument.Parse(detalleJson);
            return detalle.RootElement.TryGetProperty("Margen", out var margen) && margen.ValueKind == JsonValueKind.String ? margen.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PaperSize Tamano(string tamano) =>
        Enum.TryParse<PaperSize>(tamano, ignoreCase: true, out var t) && QuoteCalculator.Sizes.Contains(t)
            ? t
            : throw new ArgumentException("Elige el tamaño del papel: carta, oficio o A4.");

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
}
