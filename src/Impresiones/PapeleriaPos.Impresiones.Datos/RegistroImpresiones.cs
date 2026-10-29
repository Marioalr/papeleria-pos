using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Los trabajos de impresión de la sucursal (docs/fase-1-impresion.md): el agente de cada caja los mide y aquí se
/// les pone costo y precio con el modelo de Control Print y la configuración de la sucursal. Trabaja sobre la base que
/// tenga el mapeo del módulo (<see cref="ModeloImpresiones"/>); lo que necesita del POS (la hora y las cajas) lo pide
/// por <paramref name="pos"/>.
/// </summary>
public sealed class RegistroImpresiones(DbContext db, IPosParaImpresiones pos)
{
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DbSet<ConfiguracionImpresion> Configuraciones => db.Set<ConfiguracionImpresion>();
    private DbSet<HistorialCostosImpresion> Historial => db.Set<HistorialCostosImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Los costos de la sucursal; sin configurar, los valores por omisión de Control Print (WF-6590).</summary>
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

    /// <summary>Los costos de antes de la sucursal, lo más nuevo primero.</summary>
    public async Task<List<HistorialCostosImpresion>> HistorialCostosAsync(Guid sucursalId, CancellationToken ct = default) =>
        (await Historial.AsNoTracking().Where(h => h.SucursalId == sucursalId).ToListAsync(ct)).OrderByDescending(h => h.HastaUtc).ToList();

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
    /// Un trabajo que llegó del agente, con su precio puesto con <paramref name="costos"/> (en la caja 2, los de Control Print leídos
    /// de su servidor, 2026-10-25); sin ellos, con los de la sucursal. Si ya estaba (el agente lo volvió a mandar), no se repite.
    /// </summary>
    public async Task<TrabajoImpresion> RegistrarAsync(TrabajoMedidoDto t, CostSettings? costos, CancellationToken ct = default)
    {
        if (await Trabajos.FindAsync([t.Id], ct) is { } existente)
            return existente;
        var caja = await pos.CajaAsync(t.CajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var costo = CostCalculator.Calculate(Analisis(t), costos ?? await CostosAsync(caja.SucursalId, ct));

        var trabajo = new TrabajoImpresion
        {
            Id = t.Id, SucursalId = caja.SucursalId, CajaId = caja.Id, AtendioId = t.AtendioId, Pc = t.Pc, Cola = t.Cola, SpoolId = t.SpoolId,
            Documento = string.IsNullOrWhiteSpace(t.Documento) ? "(sin nombre)" : t.Documento.Trim(), UsuarioWindows = t.UsuarioWindows,
            ImpresoUtc = t.ImpresoUtc, EnviadoUtc = t.EnviadoUtc, Estado = t.Estado, Tipo = TipoTrabajoImpresion.Impresion,
            Paginas = costo.LogicalPages, Copias = costo.Copies, Caras = costo.Sides, CarasColor = costo.ColorSides, Hojas = costo.Sheets,
            Color = t.Ajustes.Color, Duplex = t.Ajustes.Duplex, Tamano = costo.PaperSize.ToString(), TipoPapel = costo.MediaName,
            Calidad = t.Ajustes.Calidad, Medido = t.Medido, Nota = t.Nota,
            Miniatura = t.MiniaturaBase64 is { Length: > 0 } m ? Convert.FromBase64String(m) : null,
            CostoTintaCentavos = Centavos(costo.InkCost), CostoPapelCentavos = Centavos(costo.PaperCost),
            CostoOtrosCentavos = Centavos(costo.MaintenanceCost + costo.PowerCost + costo.ExtrasCost + costo.DepreciationCost),
            CostoCentavos = Centavos(costo.Total), MargenPorcentaje = costo.MarginPercent, PrecioCentavos = Centavos(costo.Price),
            DetalleJson = JsonSerializer.Serialize(new { t.Ajustes, t.Paginas }, CostSettings.Json),
            TintaC = costo.InkC, TintaM = costo.InkM, TintaY = costo.InkY, TintaK = costo.InkK,
            Cobro = EstadoCobroImpresion.PorCobrar,
        };
        Trabajos.Add(trabajo);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    /// <summary>Lo que midió el agente, en los términos del cálculo de Control Print.</summary>
    public static JobAnalysis Analisis(TrabajoMedidoDto t)
    {
        var a = t.Ajustes;
        var ajustes = new JobSettings
        {
            Quality = a.Calidad, MediaType = a.TipoPapel, MediaSizeName = a.Tamano, MediaWidthMm = a.AnchoMm, MediaHeightMm = a.AltoMm,
            Color = a.Color, Duplex = a.Duplex, Copies = a.Copias, PagesPerSheet = a.PaginasPorHoja, FromPrintTicket = a.DelPrintTicket,
        };
        var paginas = t.Paginas.Select(p => new PageCoverage(p.C, p.M, p.Y, p.K, p.Gris, p.AreaPulg2, p.ConColor)).ToList();
        return new JobAnalysis(ajustes, paginas, t.Medido ? AnalysisLevel.Measured : AnalysisLevel.Estimated, null, t.Nota, TimeSpan.Zero);
    }

    /// <summary>Si ya hay algún trabajo registrado, de cualquier sucursal (la base de la prueba no los repite).</summary>
    public Task<bool> HayTrabajosAsync(CancellationToken ct = default) => Trabajos.AnyAsync(ct);

    public async Task<List<TrabajoImpresion>> PorCobrarAsync(Guid sucursalId, CancellationToken ct = default) =>
        await Trabajos.Where(t => t.SucursalId == sucursalId && t.Cobro == EstadoCobroImpresion.PorCobrar)
            .OrderByDescending(t => t.ImpresoUtc).ToListAsync(ct);

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
        var r = QuoteCalculator.Calculate(medidas, opciones, await CostosAsync(s.SucursalId, ct));
        var c = r.Cost;
        return new CotizacionDto(Centavos(c.Price), c.MinimumApplied, r.Pages.Count, c.Copies, c.Sides, c.Sheets, c.ColorSides, c.PaperSize.ToString(),
            c.MediaName,
            r.PageCosts.Select(p => new PaginaCotizadaDto(r.Pages[p.Page - 1], p.Blank ? "En blanco" : p.Color ? "Color" : "B/N", p.Price * 100,
                conCosto ? p.Cost * 100 : null)).ToList(),
            c.Warnings, conCosto ? Costo(c) : null);
    }

    /// <summary>Precio sugerido de copias hechas en el panel: tinta típica (5 % por canal), el papel, mínimo y redondeo.</summary>
    public async Task<(CostBreakdown Costo, string Descripcion)> CalcularCopiasAsync(
        Guid sucursalId, int cantidad, bool color, string tamano, bool duplex, CancellationToken ct = default)
    {
        if (cantidad is < 1 or > 9999)
            throw new ArgumentException("Escribe cuántas copias (de 1 a 9,999).");
        var tamanoPapel = Tamano(tamano);
        var (ancho, alto, nombre) = QuoteCalculator.Dimensions(tamanoPapel);
        var ajustes = new JobSettings { Color = color, Duplex = duplex, MediaWidthMm = ancho, MediaHeightMm = alto, MediaSizeName = nombre };
        var caras = duplex ? cantidad * 2 : cantidad;
        var area = Math.Max(0, ancho - 6) * Math.Max(0, alto - 6) / (25.4 * 25.4);
        var pagina = PageCoverage.Estimated(area, color, new AnalysisOptions().FallbackCoverage);
        var analisis = new JobAnalysis(ajustes, Enumerable.Repeat(pagina, caras).ToList(), AnalysisLevel.Estimated, null, null, TimeSpan.Zero);
        var descripcion = $"{cantidad} copia{(cantidad == 1 ? "" : "s")} {(color ? "a color" : "en B/N")}" + (duplex ? " a doble cara" : "")
            + (tamanoPapel == PaperSize.Carta ? "" : $" ({tamanoPapel})");
        return (CostCalculator.Calculate(analisis, await CostosAsync(sucursalId, ct)), descripcion);
    }

    /// <summary>Copias cobradas en el mostrador: quedan como un trabajo más, por cobrar, para la línea "Impresiones".</summary>
    public async Task<TrabajoImpresion> RegistrarCopiasAsync(SolicitudCopias s, CancellationToken ct = default)
    {
        if (await Trabajos.FindAsync([s.Id], ct) is { } existente)
            return existente;
        if (s.CobradoCentavos < 0)
            throw new ArgumentException("Lo cobrado no puede ser negativo.");
        var caja = await pos.CajaAsync(s.CajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var (costo, descripcion) = await CalcularCopiasAsync(caja.SucursalId, s.Cantidad, s.Color, s.Tamano, s.Duplex, ct);
        var sugerido = Centavos(costo.Price);
        var cobrado = s.CobradoCentavos ?? sugerido;
        var ahora = Ahora;
        var trabajo = new TrabajoImpresion
        {
            Id = s.Id, SucursalId = caja.SucursalId, CajaId = caja.Id, AtendioId = s.AtendioId, Tipo = TipoTrabajoImpresion.Copia,
            Pc = caja.Nombre, Cola = "Copias en la impresora", Documento = descripcion, ImpresoUtc = ahora, EnviadoUtc = ahora,
            Estado = EstadoTrabajo.Impreso,
            Paginas = s.Cantidad, Copias = 1, Caras = costo.Sides, CarasColor = costo.ColorSides, Hojas = costo.Sheets, Color = s.Color, Duplex = s.Duplex,
            Tamano = costo.PaperSize.ToString(), TipoPapel = costo.MediaName, Calidad = "Standard", Medido = false,
            Nota = string.IsNullOrWhiteSpace(s.Nota) ? null : s.Nota.Trim(),
            CostoTintaCentavos = Centavos(costo.InkCost), CostoPapelCentavos = Centavos(costo.PaperCost),
            CostoOtrosCentavos = Centavos(costo.MaintenanceCost + costo.PowerCost + costo.ExtrasCost + costo.DepreciationCost),
            CostoCentavos = Centavos(costo.Total), MargenPorcentaje = costo.MarginPercent, PrecioCentavos = cobrado,
            PrecioCompletoCentavos = cobrado == sugerido ? null : sugerido,
            DetalleJson = JsonSerializer.Serialize(new { s.Cantidad, s.Color, s.Tamano, s.Duplex }, CostSettings.Json),
            TintaC = costo.InkC, TintaM = costo.InkM, TintaY = costo.InkY, TintaK = costo.InkK,
            Cobro = EstadoCobroImpresion.PorCobrar,
        };
        Trabajos.Add(trabajo);
        await db.SaveChangesAsync(ct);
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

    /// <summary>Caras registradas entre dos lecturas del contador: impresiones de las PC (las que salieron) y copias cobradas.</summary>
    public RegisteredSides CarasRegistradas(Guid sucursalId, DateTime desdeUtc, DateTime hastaUtc)
    {
        var trabajos = Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.ImpresoUtc > desdeUtc && t.ImpresoUtc <= hastaUtc)
            .Select(t => new { t.Tipo, t.Caras, t.CarasColor, t.CarasContadas, t.Estado }).ToList();
        int Salieron(int caras, int? contadas, EstadoTrabajo estado) => contadas ?? (estado == EstadoTrabajo.Impreso ? caras : 0);
        var impresas = trabajos.Where(t => t.Tipo == TipoTrabajoImpresion.Impresion).ToList();
        var copias = trabajos.Where(t => t.Tipo == TipoTrabajoImpresion.Copia).ToList();
        return new RegisteredSides(
            impresas.Sum(t => Salieron(t.Caras, t.CarasContadas, t.Estado)), impresas.Sum(t => Math.Min(t.CarasColor, Salieron(t.Caras, t.CarasContadas, t.Estado))),
            copias.Sum(t => t.Caras), copias.Sum(t => t.CarasColor));
    }

    public static CostoImpresionDto Costo(TrabajoImpresion t) =>
        new(t.CostoTintaCentavos, t.CostoPapelCentavos, t.CostoOtrosCentavos, t.CostoCentavos, t.MargenPorcentaje);

    private static CostoImpresionDto Costo(CostBreakdown c) =>
        new(Centavos(c.InkCost), Centavos(c.PaperCost), Centavos(c.MaintenanceCost + c.PowerCost + c.ExtrasCost + c.DepreciationCost),
            Centavos(c.Total), c.MarginPercent);

    private static PaperSize Tamano(string tamano) =>
        Enum.TryParse<PaperSize>(tamano, ignoreCase: true, out var t) && QuoteCalculator.Sizes.Contains(t)
            ? t
            : throw new ArgumentException("Elige el tamaño del papel: carta, oficio o A4.");

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
}
