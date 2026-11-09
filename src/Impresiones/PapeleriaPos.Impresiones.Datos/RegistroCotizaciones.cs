using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Cotizar (2026-11-08, hito 3 de docs/impresion.md): el precio de un PDF medido en una caja, con <b>el mismo cálculo y los mismos costos que
/// la impresión</b> (<see cref="CostCalculator.Calculate"/>, los costos de la impresora donde se imprimiría, con lo de Control Print en la caja
/// 2), con lo que de verdad se imprime en esa impresora (<see cref="HabitualesAsync"/>) y la corrección de su tinta
/// (<see cref="Impresora.FactorCotizacion"/>). Guarda la cotización, la liga a lo que se imprime (<see cref="LoCotizado"/>) y dice qué tan
/// preciso es cotizar en cada impresora. Por qué cotizar salía más barato que imprimir: docs/impresion.md, «Por qué cotizar daba menos».
/// </summary>
public sealed class RegistroCotizaciones(DbContext db, IPosParaImpresiones pos)
{
    private DbSet<CotizacionImpresion> Cotizaciones => db.Set<CotizacionImpresion>();
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DbSet<Impresora> Impresoras => db.Set<Impresora>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Lo más largo del nombre del documento que se guarda.</summary>
    public const int DocumentoMaximo = 200;

    /// <summary>Trabajos recientes de una impresora con que se sabe cómo se imprime en ella.</summary>
    public const int TrabajosParaLoHabitual = 100;

    /// <summary>Días de trabajos con que se sabe lo habitual, y de pares con que se mide la precisión.</summary>
    public const int DiasParaLoHabitual = 60;

    public const int DiasDePrecision = 90;

    // ── Cotizar ──────────────────────────────────────────────────────────────

    public async Task<CotizacionDto> CotizarAsync(SolicitudCotizacion s, bool conCosto, CostSettings? deLaSucursal = null, CancellationToken ct = default)
    {
        if (s.Paginas.Count == 0)
            throw new ArgumentException("El documento no tiene páginas.");
        var o = s.Opciones;
        var servicio = new ServicioImpresora(db, pos);
        var impresora = await ParaCotizarAsync(s.SucursalId, s.Impresora, s.CajaId, ct);
        var costos = impresora is null
            ? deLaSucursal ?? await new RegistroImpresiones(db, pos).CostosAsync(s.SucursalId, ct)
            : await servicio.CostosAsync(impresora, deLaSucursal, ct);
        var factor = impresora?.FactorCotizacion ?? Impresora.FactorCotizacionPorOmision;
        var opciones = new QuoteOptions
        {
            MediaType = o.TipoPapel, Size = RegistroImpresiones.Tamano(o.Tamano), Color = o.Color, Quality = o.Calidad, Duplex = o.Duplex,
            Copies = o.Copias is >= 1 and <= 9999 ? o.Copias : throw new ArgumentException("Escribe cuántas copias (de 1 a 9,999)."),
            PagesPerSheet = o.PaginasPorHoja is 1 or 2 or 4 or 6 or 9 or 16 ? o.PaginasPorHoja : 1, PageRange = o.Rango, CorrectionFactor = factor,
        };
        var medidas = s.Paginas.Select(p => new PageCoverage(p.C, p.M, p.Y, p.K, p.Gris, p.AreaPulg2, p.ConColor)
        {
            WidthIn = p.AnchoPulg, HeightIn = p.AltoPulg,
        }).ToList();
        var r = QuoteCalculator.Calculate(medidas, opciones, costos);
        var c = r.Cost;
        CotizacionImpresion? guardada = null;
        if (!s.Parcial && s.Id is { } id && id != Guid.Empty && !string.IsNullOrWhiteSpace(s.Documento))
            guardada = await GuardarAsync(id, s, r, opciones, impresora, factor, ct);
        return new CotizacionDto(RegistroImpresiones.Centavos(c.Price), c.MinimumApplied, r.Pages.Count, c.Copies, c.Sides, c.Sheets, c.ColorSides,
            c.PaperSize.ToString(), c.MediaName,
            r.PageCosts.Select(p => new PaginaCotizadaDto(r.Pages[p.Page - 1], p.Blank ? "En blanco" : p.Color ? "Color" : "B/N", p.Price * 100,
                conCosto ? p.Cost * 100 : null)).ToList(),
            c.Warnings, conCosto ? RegistroImpresiones.Costo(c) : null, guardada?.Id, guardada is null ? null : HoraMexico.ALocal(guardada.VenceUtc),
            impresora?.Numero, impresora?.Nombre, factor);
    }

    /// <summary>
    /// En qué impresora se cotiza: la que se eligió (activa); si no, aquella donde imprime la caja (la de su último trabajo); si no, la
    /// primera. Sin impresoras (una tienda nueva), ninguna: con los costos de la sucursal.
    /// </summary>
    private async Task<Impresora?> ParaCotizarAsync(Guid sucursalId, int? numero, Guid? cajaId, CancellationToken ct)
    {
        var servicio = new ServicioImpresora(db, pos);
        if (numero is { } n && await servicio.ImpresoraAsync(sucursalId, n, ct) is { Activa: true } elegida)
            return elegida;
        if (cajaId is { } caja && await ImpresoraDeLaCajaAsync(sucursalId, caja, ct) is { } deLaCaja)
            return deLaCaja;
        return await servicio.PrimeraAsync(sucursalId, ct: ct);
    }

    /// <summary>La impresora (activa) del último trabajo que imprimió la caja.</summary>
    private async Task<Impresora?> ImpresoraDeLaCajaAsync(Guid sucursalId, Guid cajaId, CancellationToken ct)
    {
        var id = await Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == sucursalId && t.CajaId == cajaId && t.Tipo == TipoTrabajoImpresion.Impresion && t.ImpresoraId != null)
            .OrderByDescending(t => t.ImpresoUtc).Select(t => t.ImpresoraId).FirstOrDefaultAsync(ct);
        return id is { } impresoraId && await Impresoras.FindAsync([impresoraId], ct) is { Activa: true } impresora ? impresora : null;
    }

    /// <summary>
    /// La guarda (o la vuelve a guardar con las opciones nuevas: la misma cotización mientras el cajero cambia opciones). Una que ya se usó
    /// (ligada a un trabajo) no cambia.
    /// </summary>
    private async Task<CotizacionImpresion> GuardarAsync(Guid id, SolicitudCotizacion s, QuoteResult r, QuoteOptions opciones, Impresora? impresora,
        double factor, CancellationToken ct)
    {
        var cotizacion = await Cotizaciones.FindAsync([id], ct);
        if (cotizacion is not null && cotizacion.SucursalId != s.SucursalId)
            throw new InvalidOperationException("Esa cotización es de otra sucursal.");
        if (cotizacion is { TrabajoId: not null })
            return cotizacion;
        var ahora = Ahora;
        if (cotizacion is null)
        {
            cotizacion = new CotizacionImpresion
            {
                Id = id, SucursalId = s.SucursalId, CreadaUtc = ahora, VenceUtc = ahora.AddDays(CotizacionImpresion.DiasQueVale), Documento = "",
                Tamano = "", TipoPapel = "", Calidad = "",
            };
            Cotizaciones.Add(cotizacion);
        }
        var c = r.Cost;
        cotizacion.ModificadaUtc = ahora;
        cotizacion.ImpresoraId = impresora?.Id;
        cotizacion.CajaId = s.CajaId;
        cotizacion.UsuarioId = s.UsuarioId;
        cotizacion.Documento = NombreDelArchivo(s.Documento!);
        cotizacion.PaginasDocumento = Math.Max(s.PaginasDocumento, s.Paginas.Count);
        cotizacion.Rango = string.IsNullOrWhiteSpace(opciones.PageRange) ? null : opciones.PageRange.Trim();
        cotizacion.Paginas = r.Pages.Count;
        (cotizacion.Copias, cotizacion.Caras, cotizacion.Hojas, cotizacion.CarasColor) = (c.Copies, c.Sides, c.Sheets, c.ColorSides);
        (cotizacion.Color, cotizacion.Duplex, cotizacion.PaginasPorHoja) = (opciones.Color, opciones.Duplex, opciones.PagesPerSheet);
        (cotizacion.Tamano, cotizacion.TipoPapel, cotizacion.Calidad) = (c.PaperSize.ToString(), c.MediaName, opciones.Quality);
        (cotizacion.PrecioCentavos, cotizacion.CostoCentavos) = (RegistroImpresiones.Centavos(c.Price), RegistroImpresiones.Centavos(c.Total));
        (cotizacion.TintaSinCorregir, cotizacion.FactorCotizacion) = (Math.Round(r.UncorrectedInkCost, 6), factor);
        await db.SaveChangesAsync(ct);
        return cotizacion;
    }

    /// <summary>Solo el nombre del archivo (sin la carpeta), sin espacios de más y no muy largo.</summary>
    public static string NombreDelArchivo(string documento)
    {
        var nombre = documento.Replace('/', '\\').Split('\\')[^1].Trim();
        return nombre.Length <= DocumentoMaximo ? nombre : nombre[..DocumentoMaximo];
    }

    /// <summary>Las de la sucursal de los últimos <see cref="CotizacionImpresion.DiasQueVale"/> días, lo más nuevo primero: para la lista de Cotizar.</summary>
    public async Task<List<CotizacionGuardadaDto>> RecientesAsync(Guid sucursalId, int maximo = 50, CancellationToken ct = default)
    {
        var desde = Ahora.AddDays(-CotizacionImpresion.DiasQueVale);
        var lista = await Cotizaciones.AsNoTracking().Where(c => c.SucursalId == sucursalId && c.CreadaUtc >= desde)
            .OrderByDescending(c => c.CreadaUtc).Take(maximo).ToListAsync(ct);
        var nombres = await Impresoras.AsNoTracking().Where(i => i.SucursalId == sucursalId).ToDictionaryAsync(i => i.Id, i => i.Nombre, ct);
        return lista.Select(c => new CotizacionGuardadaDto(c.Id, c.Documento, HoraMexico.ALocal(c.CreadaUtc), HoraMexico.ALocal(c.VenceUtc), c.Paginas,
            c.Copias, c.Caras, c.Color, c.Duplex, c.Tamano, c.TipoPapel, c.PrecioCentavos,
            c.ImpresoraId is { } i && nombres.Count > 1 ? nombres.GetValueOrDefault(i) : null, c.TrabajoId is not null, c.PrecioImpresoCentavos)).ToList();
    }

    // ── Lo habitual de cada impresora ────────────────────────────────────────

    /// <summary>
    /// Con qué se imprime de verdad en cada impresora activa: lo más usado (calidad, papel, tamaño, color, doble cara y páginas por hoja) en
    /// sus últimos <see cref="TrabajosParaLoHabitual"/> trabajos de los últimos <see cref="DiasParaLoHabitual"/> días; sin trabajos, lo de
    /// siempre (normal, papel normal, carta). Y la impresora donde imprime la caja que pregunta. Cotizar empieza con esto (2026-11-08): cotizar
    /// en «Normal» lo que la tienda imprime en «Normal (colores vivos)» daba un 15 % menos de tinta.
    /// </summary>
    public async Task<(List<AjustesHabitualesDto> Habituales, int? ImpresoraDeLaCaja)> HabitualesAsync(Guid sucursalId, Guid? cajaId,
        CancellationToken ct = default)
    {
        var servicio = new ServicioImpresora(db, pos);
        var activas = await servicio.ImpresorasAsync(sucursalId, ct: ct);
        var costos = await new RegistroImpresiones(db, pos).CostosAsync(sucursalId, ct);
        var desde = Ahora.AddDays(-DiasParaLoHabitual);
        var primera = activas.FirstOrDefault();
        var lista = new List<AjustesHabitualesDto>();
        foreach (var impresora in activas)
        {
            // Lo de antes sin impresora (vacío) cuenta como de la primera.
            var id = impresora.Id;
            var dePrimera = primera?.Id == id;
            var trabajos = await Trabajos.AsNoTracking()
                .Where(t => t.SucursalId == sucursalId && t.Tipo == TipoTrabajoImpresion.Impresion && t.ImpresoUtc >= desde
                    && (t.ImpresoraId == id || (dePrimera && t.ImpresoraId == null)))
                .OrderByDescending(t => t.ImpresoUtc).Take(TrabajosParaLoHabitual)
                .Select(t => new { t.Calidad, t.TipoPapel, t.Tamano, t.Color, t.Duplex, t.Paginas, t.Copias, t.Caras })
                .ToListAsync(ct);
            var opciones = new OpcionesCotizacionDto();
            if (trabajos.Count > 0)
            {
                // El papel del trabajo se guarda con su nombre («Papel normal»): su clave sale de los tipos de papel de la sucursal.
                var papel = LoMasUsado(trabajos.Select(t => t.TipoPapel));
                var clave = costos.Media.FirstOrDefault(m => string.Equals(m.Name, papel, StringComparison.OrdinalIgnoreCase))?.Key ?? "Plain";
                var tamano = LoMasUsado(trabajos.Select(t => t.Tamano));
                var porHoja = LoMasUsado(trabajos.Select(t => PaginasPorHoja(t.Paginas, t.Copias, t.Caras)));
                opciones = new OpcionesCotizacionDto(clave, RegistroImpresiones.Tamanos.Contains(tamano) ? tamano : "Carta",
                    LoMasUsado(trabajos.Select(t => t.Color)), LoMasUsado(trabajos.Select(t => t.Calidad)), LoMasUsado(trabajos.Select(t => t.Duplex)),
                    1, porHoja);
            }
            lista.Add(new AjustesHabitualesDto(impresora.Numero, opciones, trabajos.Count));
        }
        int? deLaCaja = cajaId is { } caja && await ImpresoraDeLaCajaAsync(sucursalId, caja, ct) is { } i ? i.Numero : null;
        return (lista, deLaCaja);
    }

    /// <summary>Las páginas por hoja de un trabajo, de sus páginas, copias y caras (1 si no cuadra con 2 o 4).</summary>
    private static int PaginasPorHoja(int paginas, int copias, int caras)
    {
        if (paginas <= 1 || copias <= 0)
            return 1;
        var porCopia = caras / copias;
        foreach (var n in new[] { 2, 4 })
            if (porCopia == (paginas + n - 1) / n)
                return n;
        return 1;
    }

    /// <summary>El valor más repetido (en un empate, el del trabajo más nuevo: la lista viene del más nuevo al más viejo).</summary>
    private static T LoMasUsado<T>(IEnumerable<T> valores) where T : notnull =>
        valores.Select((v, i) => (v, i)).GroupBy(x => x.v).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.i)).First().Key;

    // ── Lo que se imprime de lo cotizado ─────────────────────────────────────

    /// <summary>
    /// Un trabajo recién medido (todavía sin guardar): si es la impresión de una cotización vigente (<see cref="LoCotizado.Elegir"/>), se
    /// ligan; se guarda el par cotizado-impreso para medir la precisión y, si salió igual y su precio está cerca, se cobra lo cotizado. No
    /// guarda: lo guarda quien registra el trabajo.
    /// </summary>
    public async Task<CotizacionImpresion?> LigarAsync(TrabajoImpresion trabajo, CostBreakdown costo, CancellationToken ct = default)
    {
        if (trabajo.Tipo != TipoTrabajoImpresion.Impresion || trabajo.Cobro != EstadoCobroImpresion.PorCobrar)
            return null;
        var desde = trabajo.ImpresoUtc.AddDays(-CotizacionImpresion.DiasQueVale - 1);
        var hasta = trabajo.ImpresoUtc.AddMinutes(LoCotizado.MinutosAntes);
        var candidatas = await Cotizaciones.Where(c => c.SucursalId == trabajo.SucursalId && c.TrabajoId == null && c.CreadaUtc >= desde
            && c.CreadaUtc <= hasta).ToListAsync(ct);
        if (LoCotizado.Elegir(candidatas, trabajo) is not { } cotizacion)
            return null;
        // El par sirve para medir la precisión si salió igual y de la impresora en que se cotizó (otra tiene otros costos).
        var mismos = LoCotizado.MismosAjustes(cotizacion, trabajo) && (cotizacion.ImpresoraId is null || cotizacion.ImpresoraId == trabajo.ImpresoraId);
        (cotizacion.TrabajoId, cotizacion.LigadaUtc, cotizacion.PrecioImpresoCentavos) = (trabajo.Id, Ahora, trabajo.PrecioCentavos);
        (cotizacion.TintaImpresa, cotizacion.MismosAjustes) = (Math.Round(costo.InkCost, 6), mismos);
        trabajo.LigarCotizacion(cotizacion, LoCotizado.SeCobraSinPreguntar(cotizacion, trabajo, trabajo.PrecioCentavos));
        return cotizacion;
    }

    /// <summary>Lo que elige el cajero en F2 para una impresión cotizada: lo cotizado o lo que calculó la impresión.</summary>
    public async Task<TrabajoImpresion> CobrarLoCotizadoAsync(Guid trabajoId, bool loCotizado, CancellationToken ct = default)
    {
        var trabajo = await Trabajos.FindAsync([trabajoId], ct) ?? throw new InvalidOperationException("La impresión no existe.");
        trabajo.CobrarLoCotizado(loCotizado);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    // ── Qué tan preciso es cotizar ───────────────────────────────────────────

    /// <summary>Lo cotizado contra lo impreso en una impresora, de los últimos <see cref="DiasDePrecision"/> días (<see cref="LoCotizado.Precision"/>).</summary>
    public async Task<PrecisionCotizarDto> PrecisionAsync(Impresora impresora, CancellationToken ct = default)
    {
        var desde = Ahora.AddDays(-DiasDePrecision);
        var pares = await Cotizaciones.AsNoTracking()
            .Where(c => c.ImpresoraId == impresora.Id && c.TrabajoId != null && c.MismosAjustes && c.CreadaUtc >= desde).ToListAsync(ct);
        var p = LoCotizado.Precision(pares);
        return new PrecisionCotizarDto(p.Pares, p.DiferenciaPorcentaje, p.FactorSugerido, DiasDePrecision);
    }
}
