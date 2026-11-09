using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using static PapeleriaPos.Impresiones.ConciliacionDelContador;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Las tarjetas «Copias o celular» en la base (2026-11-08; docs/impresion.md, «Celular y copias sin registrar»): las caras que contó una
/// impresora y nadie registró (lo impreso desde un celular con las apps de Epson, las copias del panel que no se anotaron) salen solas en F2,
/// por impresora, con el precio estimado. Lo decide <see cref="ConciliacionDelContador"/>; aquí se leen las lecturas, lo registrado y las
/// tarjetas de la impresora, y se ponen, encogen o quitan las tarjetas. También partirlas (copias y celular), ligarlas a unas copias ya
/// registradas y lo del día para la Configuración.
/// <para>
/// Cada tarjeta es un <see cref="TrabajoImpresion"/> de tipo <see cref="TipoTrabajoImpresion.Contador"/>: se cobra, se marca no vendida y se
/// devuelve como cualquier impresión (en la línea «Impresiones»). Sus <see cref="TrabajoImpresion.CarasContadas"/> son las que se le cuentan
/// al contador; sus <see cref="TrabajoImpresion.Caras"/>, las que se cobran (nunca más).
/// </para>
/// </summary>
public sealed class TarjetasDelContador(DbContext db, IPosParaImpresiones pos)
{
    /// <summary>Su «PC»: va en el renglón del ticket («Impresión de celular o copias»).</summary>
    public const string Pc = "celular o copias";

    /// <summary>Lo que dice la nota de una impresión que llegó cuando sus caras ya estaban en una tarjeta cobrada.</summary>
    public const string YaContada = "La impresora ya contó sus caras en «Copias o celular»";

    /// <summary>Lo de cada impresora, de una en una: la vigilancia, las copias que se registran y lo que hace el cajero en F2.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Candados = new();

    private DbSet<Impresora> Impresoras => db.Set<Impresora>();
    private DbSet<LecturaImpresora> Lecturas => db.Set<LecturaImpresora>();
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Si una impresora puede tener tarjetas: con Control Print en la caja principal (la caja 2 del piloto), no las que comparte con él (las
    /// que usan los cartuchos de la sucursal, como dice la Configuración): lo que imprime la caja principal lo registra Control Print y aquí
    /// saldría como «sin registrar» (auditoría del 2026-11-04).
    /// </summary>
    public static bool LaCompartenConControlPrint(Impresora impresora) => ServicioImpresora.Propios(impresora)?.Consumibles is null;

    private static async Task<IDisposable> CandadoAsync(Guid impresoraId, CancellationToken ct)
    {
        var candado = Candados.GetOrAdd(impresoraId, _ => new SemaphoreSlim(1, 1));
        await candado.WaitAsync(ct);
        return new Soltar(candado);
    }

    private sealed class Soltar(SemaphoreSlim candado) : IDisposable
    {
        public void Dispose() => candado.Release();
    }

    // ── Lo que hace la vigilancia con cada lectura ───────────────────────────

    /// <summary>
    /// Después de cada lectura de la impresora (<paramref name="actual"/>, aunque no se haya guardado): pone, encoge o quita sus tarjetas.
    /// La primera vez guarda los ajustes de su modelo, y desde ahí cuenta. Verdadero si hay caras que acomodar (conviene leerla pronto).
    /// </summary>
    /// <param name="conControlPrint">El servicio lee a Control Print (la caja 2 del piloto).</param>
    public async Task<bool> RevisarAsync(Guid impresoraId, PrinterStatus? actual, bool conControlPrint, CancellationToken ct = default)
    {
        using var _ = await CandadoAsync(impresoraId, ct);
        if (await Impresoras.FindAsync([impresoraId], ct) is not { Activa: true } impresora)
            return false;
        if (AjustesDelContador.Leer(impresora.ContadorJson) is not { } ajustes)
        {
            impresora.ContadorJson = AjustesDelContador.Escribir(AjustesDelContador.PorOmision(impresora.Perfil) with { DesdeUtc = actual?.TakenUtc ?? Ahora });
            await db.SaveChangesAsync(ct);
            return false;
        }
        if (!ajustes.Activas || conControlPrint && LaCompartenConControlPrint(impresora))
            return false;
        // La hora de la lectura (la de la vigilancia), para que la pausa se mida con el mismo reloj que las lecturas.
        return await ConciliarAsync(impresora, ajustes, actual, null, actual?.TakenUtc ?? Ahora, ct);
    }

    /// <summary>
    /// Unas copias del panel que se acaban de registrar: sus caras se toman primero de las tarjetas por cobrar de su impresora (de
    /// <paramref name="preferida"/>, si se dice), hasta lo que la impresora ya contó de más.
    /// </summary>
    public async Task ConsumirAsync(TrabajoImpresion copias, Guid? preferida = null, CancellationToken ct = default)
    {
        if (copias.ImpresoraId is not { } impresoraId)
            return;
        using var _ = await CandadoAsync(impresoraId, ct);
        await ConsumirSinCandadoAsync(impresoraId, copias, preferida, ct);
    }

    private async Task ConsumirSinCandadoAsync(Guid impresoraId, TrabajoImpresion copias, Guid? preferida, CancellationToken ct)
    {
        if (await Impresoras.FindAsync([impresoraId], ct) is not { } impresora || AjustesDelContador.Leer(impresora.ContadorJson) is not { } ajustes)
            return;
        await ConciliarAsync(impresora, ajustes, null, new Consumir(copias.Caras, copias.CarasColor, preferida), Ahora, ct);
    }

    /// <summary>Desde cuándo se cuenta: el inicio del día en la tienda, o desde que se pusieron (o cambió la dirección), lo que sea después.</summary>
    private static DateTime Desde(AjustesDelContador ajustes, DateTime ahora)
    {
        var inicioDelDia = HoraMexico.AUtc(HoraMexico.ALocal(ahora).Date);
        return ajustes.DesdeUtc is { } desde && desde > inicioDelDia ? desde : inicioDelDia;
    }

    private async Task<bool> ConciliarAsync(Impresora impresora, AjustesDelContador ajustes, PrinterStatus? actual, Consumir? consumir, DateTime ahora,
        CancellationToken ct)
    {
        var desde = Desde(ajustes, ahora);
        var lecturas = await Lecturas.AsNoTracking().Where(l => l.ImpresoraId == impresora.Id && l.TomadaUtc >= desde && l.Contador != null)
            .OrderBy(l => l.TomadaUtc).Select(l => new Lectura(l.TomadaUtc, l.Contador!.Value, l.ContadorColor, l.Serie)).ToListAsync(ct);
        if (actual is { LifeCount: { } contador } && (lecturas.Count == 0 || actual.TakenUtc > lecturas[^1].TomadaUtc))
            lecturas.Add(new Lectura(actual.TakenUtc, contador, actual.ColorCount, actual.Serial));
        var (registrados, impresas, tarjetas) = await LoDeLaImpresoraAsync(impresora, desde, ct);
        var r = Revisar(lecturas, registrados, tarjetas.Select(t => t.Tarjeta), desde, ahora, ajustes.Pausa, consumir);
        if (r.Nueva is { } nueva)
            await PonerNuevaAsync(impresora, ajustes, nueva, Conocidos(impresas, nueva.DesdeUtc, nueva.HastaUtc), ct);
        foreach (var cambio in r.Encoger)
        {
            var tarjeta = tarjetas.First(t => t.Tarjeta.Id == cambio.Id).Tarjeta;
            await EncogerAsync(impresora, cambio, Conocidos(impresas, tarjeta.DesdeUtc, tarjeta.HastaUtc), ct);
        }
        if (r.Sobran > 0 && consumir is null)
            await AvisarLasYaContadasAsync(impresas, tarjetas.Where(t => !t.Tarjeta.PorCobrar).ToList(), ct);
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return r.Activa;
    }

    /// <summary>Una impresión de una PC: si sigue por cobrar y a qué hora salió.</summary>
    private sealed record Impresa(Guid Id, DateTime Utc, bool PorCobrar);

    /// <summary>Lo de las PC registrado en esas horas (con el margen del reloj).</summary>
    private static List<Guid> Conocidos(List<Impresa> impresas, DateTime desde, DateTime hasta) =>
        [.. impresas.Where(i => i.Utc >= desde - DetalleDelContador.Margen && i.Utc <= hasta + DetalleDelContador.Margen).Select(i => i.Id)];

    /// <summary>Lo registrado en la impresora, sus impresiones de las PC y sus tarjetas, desde esa hora.</summary>
    private async Task<(List<Registrado> Registrados, List<Impresa> Impresas, List<(Tarjeta Tarjeta, DetalleDelContador? Detalle)> Tarjetas)>
        LoDeLaImpresoraAsync(Impresora impresora, DateTime desde, CancellationToken ct)
    {
        var trabajos = await new ServicioImpresora(db, pos).TrabajosDe(impresora).Where(t => t.ImpresoUtc > desde)
            .Select(t => new
            {
                t.Id, t.Tipo, t.ImpresoUtc, t.EnviadoUtc, t.Caras, t.CarasColor, t.CarasContadas, t.Estado, t.Cobro,
                Detalle = t.Tipo == TipoTrabajoImpresion.Contador ? t.DetalleJson : null,
            }).ToListAsync(ct);
        var registrados = new List<Registrado>();
        var impresas = new List<Impresa>();
        var tarjetas = new List<(Tarjeta, DetalleDelContador?)>();
        foreach (var t in trabajos)
        {
            switch (t.Tipo)
            {
                case TipoTrabajoImpresion.Impresion:
                    var salieron = t.CarasContadas ?? (t.Estado == EstadoTrabajo.Impreso ? t.Caras : 0);
                    registrados.Add(new Registrado(t.ImpresoUtc, salieron, Math.Min(t.CarasColor, salieron)));
                    impresas.Add(new Impresa(t.Id, t.ImpresoUtc, t.Cobro == EstadoCobroImpresion.PorCobrar));
                    break;
                case TipoTrabajoImpresion.Copia:
                    registrados.Add(new Registrado(t.ImpresoUtc, t.Caras, t.CarasColor));
                    break;
                case TipoTrabajoImpresion.Contador:
                    var detalle = DetalleDelContador.Leer(t.Detalle);
                    tarjetas.Add((new Tarjeta(t.Id, t.EnviadoUtc ?? t.ImpresoUtc, t.ImpresoUtc, t.CarasContadas ?? t.Caras, detalle?.ContadasColor,
                        t.Cobro == EstadoCobroImpresion.PorCobrar), detalle));
                    break;
            }
        }
        return (registrados, impresas, tarjetas);
    }

    private async Task PonerNuevaAsync(Impresora impresora, AjustesDelContador ajustes, Nueva nueva, List<Guid> conocidos, CancellationToken ct)
    {
        // Cada trabajo es de una caja: el de una tarjeta, de la principal (no salió de ninguna PC).
        if (await pos.CajaPrincipalAsync(impresora.SucursalId, ct) is not { } caja)
            return;
        var tarjeta = new TrabajoImpresion
        {
            SucursalId = impresora.SucursalId, ImpresoraId = impresora.Id, Tipo = TipoTrabajoImpresion.Contador, CajaId = caja.Id, Pc = Pc,
            Cola = impresora.Nombre, Documento = "", ImpresoUtc = nueva.HastaUtc, EnviadoUtc = nueva.DesdeUtc, Estado = EstadoTrabajo.Impreso,
            Tamano = "", TipoPapel = "", Calidad = "Standard", Medido = false, Cobro = EstadoCobroImpresion.PorCobrar,
            DetalleJson = new DetalleDelContador(nueva.Caras, nueva.Color, nueva.DesdeUtc, ajustes.CoberturaBN, ajustes.CoberturaColor, conocidos).Escribir(),
        };
        await PonerAsync(tarjeta, impresora, nueva.Caras, nueva.Color, nueva.Caras, nueva.Color ?? 0, ct);
        Trabajos.Add(tarjeta);
    }

    private async Task EncogerAsync(Impresora impresora, Cambio cambio, List<Guid> conocidos, CancellationToken ct)
    {
        if (await Trabajos.FindAsync([cambio.Id], ct) is not { Cobro: EstadoCobroImpresion.PorCobrar, Tipo: TipoTrabajoImpresion.Contador } tarjeta)
            return;
        if (cambio.Contadas <= 0)
        {
            Trabajos.Remove(tarjeta);
            return;
        }
        var caras = Math.Min(tarjeta.Caras, cambio.Contadas);
        var color = Math.Min(Math.Min(tarjeta.CarasColor, cambio.ContadasColor ?? tarjeta.CarasColor), caras);
        if (DetalleDelContador.Leer(tarjeta.DetalleJson) is { } detalle)
            tarjeta.DetalleJson = (detalle with { Conocidos = conocidos }).Escribir();
        await PonerAsync(tarjeta, impresora, cambio.Contadas, cambio.ContadasColor, caras, color, ct);
    }

    /// <summary>
    /// Lo de una PC que llegó cuando sus caras ya estaban en una tarjeta que se cobró (o se marcó no vendida): ya no se puede encoger. No se
    /// cambia nada solo: su tarjeta en F2 lo dice, para no cobrarlo dos veces.
    /// </summary>
    private async Task AvisarLasYaContadasAsync(List<Impresa> impresas, List<(Tarjeta Tarjeta, DetalleDelContador? Detalle)> cerradas, CancellationToken ct)
    {
        foreach (var impresa in impresas.Where(i => i.PorCobrar))
        {
            // La tarjeta cerrada de esas horas que no la conocía: llegó después de ponerla (o de encogerla).
            var (tarjeta, _) = cerradas.FirstOrDefault(c => impresa.Utc >= c.Tarjeta.DesdeUtc - DetalleDelContador.Margen
                && impresa.Utc <= c.Tarjeta.HastaUtc + DetalleDelContador.Margen && c.Detalle?.Conocidos?.Contains(impresa.Id) != true);
            if (tarjeta is null || await Trabajos.FindAsync([impresa.Id], ct) is not { } trabajo || trabajo.Nota?.Contains(YaContada) == true)
                continue;
            var texto = $"{YaContada} de las {HoraMexico.ALocal(tarjeta.HastaUtc):HH:mm}, que ya no está por cobrar: revisa que no se cobre dos veces.";
            trabajo.Nota = string.IsNullOrWhiteSpace(trabajo.Nota) ? texto : $"{trabajo.Nota} {texto}";
        }
    }

    // ── El precio estimado ───────────────────────────────────────────────────

    /// <summary>
    /// Pone sus caras, su costo y su precio: Carta, papel normal y la tinta de sus ajustes por cara (el costo, de lo que contó la impresora; el
    /// precio, de lo que se cobra), con los costos de su impresora, por el mismo cálculo que todo lo demás (margen, mínimo y redondeo).
    /// </summary>
    private async Task PonerAsync(TrabajoImpresion tarjeta, Impresora impresora, int contadas, int? contadasColor, int caras, int carasColor,
        CancellationToken ct)
    {
        var detalle = DetalleDelContador.Leer(tarjeta.DetalleJson) ?? new DetalleDelContador(contadas, contadasColor, tarjeta.EnviadoUtc ?? tarjeta.ImpresoUtc,
            AjustesDelContador.CoberturaPorOmision, AjustesDelContador.CoberturaPorOmision);
        detalle = detalle with { Contadas = contadas, ContadasColor = contadasColor };
        carasColor = Math.Clamp(carasColor, 0, caras);
        var costos = await new ServicioImpresora(db, pos).CostosAsync(impresora, ct: ct);
        var gasto = Calcular(contadas, Math.Min(contadasColor ?? carasColor, contadas), detalle, costos);
        var cobro = caras == 0 || caras == contadas && (contadasColor ?? carasColor) == carasColor ? gasto : Calcular(caras, carasColor, detalle, costos);
        tarjeta.DetalleJson = detalle.Escribir();
        tarjeta.CarasContadas = contadas;
        (tarjeta.Paginas, tarjeta.Copias, tarjeta.Caras, tarjeta.CarasColor, tarjeta.Hojas) = (caras, 1, caras, carasColor, caras);
        (tarjeta.Color, tarjeta.Duplex, tarjeta.Tamano, tarjeta.TipoPapel) = (carasColor > 0, false, gasto.PaperSize.ToString(), gasto.MediaName);
        tarjeta.CostoTintaCentavos = Centavos(gasto.InkCost);
        tarjeta.CostoPapelCentavos = Centavos(gasto.PaperCost);
        tarjeta.CostoOtrosCentavos = Centavos(gasto.MaintenanceCost + gasto.PowerCost + gasto.ExtrasCost + gasto.DepreciationCost);
        tarjeta.CostoCentavos = Centavos(gasto.Total);
        (tarjeta.TintaC, tarjeta.TintaM, tarjeta.TintaY, tarjeta.TintaK) = (gasto.InkC, gasto.InkM, gasto.InkY, gasto.InkK);
        tarjeta.MargenPorcentaje = cobro.MarginPercent;
        tarjeta.PrecioCentavos = caras == 0 ? 0 : Centavos(cobro.Price);
        tarjeta.Documento = Documento(contadas, contadasColor, caras, carasColor);
    }

    /// <summary>«Copias o celular · 12 caras (10 B/N, 2 color)»; si se cobran menos, «Copias o celular · 4 de 6 caras (B/N)».</summary>
    public static string Documento(int contadas, int? contadasColor, int caras, int carasColor)
    {
        var cuantas = caras == contadas ? $"{caras} {(caras == 1 ? "cara" : "caras")}" : $"{caras} de {contadas} caras";
        var color = contadasColor is null && carasColor == 0 ? "color sin saber"
            : carasColor == 0 ? "B/N"
            : carasColor == caras ? "color"
            : $"{caras - carasColor} B/N, {carasColor} color";
        return $"Copias o celular · {cuantas} ({color})";
    }

    private static CostBreakdown Calcular(int caras, int color, DetalleDelContador detalle, CostSettings costos)
    {
        var (ancho, alto, nombre) = QuoteCalculator.Dimensions(PaperSize.Carta);
        var area = Math.Max(0, ancho - 6) * Math.Max(0, alto - 6) / (25.4 * 25.4);
        color = Math.Clamp(color, 0, Math.Max(0, caras));
        var paginas = Enumerable.Repeat(PageCoverage.Estimated(area, false, detalle.CoberturaBN / 100), Math.Max(0, caras - color))
            .Concat(Enumerable.Repeat(PageCoverage.Estimated(area, true, detalle.CoberturaColor / 100), color)).ToList();
        var ajustes = new JobSettings { Color = color > 0, MediaWidthMm = ancho, MediaHeightMm = alto, MediaSizeName = nombre };
        return CostCalculator.Calculate(new JobAnalysis(ajustes, paginas, AnalysisLevel.Estimated, null, null, TimeSpan.Zero), costos);
    }

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);

    // ── Lo que hace el cajero en F2 ──────────────────────────────────────────

    private async Task<TrabajoImpresion> TarjetaAsync(Guid id, CancellationToken ct) =>
        await Trabajos.FindAsync([id], ct) is { Tipo: TipoTrabajoImpresion.Contador } tarjeta
            ? tarjeta.Cobro == EstadoCobroImpresion.PorCobrar ? tarjeta : throw new InvalidOperationException("Esa tarjeta ya no está por cobrar.")
            : throw new InvalidOperationException("Esa tarjeta ya no existe: la impresora la encogió con lo que llegó después.");

    /// <summary>
    /// Partir o corregir una tarjeta: <see cref="SolicitudPartirTarjeta.CopiasBN"/> y <see cref="SolicitudPartirTarjeta.CopiasColor"/> salen
    /// como copias del panel (por cobrar, en su línea, registradas en la caja que la parte) y la tarjeta se queda con lo demás, cobrando las del
    /// celular (lo que sobre no se cobra). Regresa lo que queda por cobrar de ella: las copias y la tarjeta, si sigue.
    /// </summary>
    public async Task<List<TrabajoImpresion>> PartirAsync(Guid tarjetaId, SolicitudPartirTarjeta s, CancellationToken ct = default)
    {
        var impresoraId = (await Trabajos.AsNoTracking().Where(t => t.Id == tarjetaId).Select(t => t.ImpresoraId).FirstOrDefaultAsync(ct))
            ?? throw new InvalidOperationException("Esa tarjeta ya no existe.");
        using var _ = await CandadoAsync(impresoraId, ct);
        var tarjeta = await TarjetaAsync(tarjetaId, ct);
        var impresora = await Impresoras.FindAsync([impresoraId], ct) ?? throw new InvalidOperationException("La impresora no existe.");
        var detalle = DetalleDelContador.Leer(tarjeta.DetalleJson);
        var contadas = tarjeta.CarasContadas ?? tarjeta.Caras;
        var contadasColor = detalle?.ContadasColor;
        if (new[] { s.CopiasBN, s.CopiasColor, s.CelularBN, s.CelularColor }.Any(n => n < 0))
            throw new ArgumentException("Las caras no pueden ser negativas.");
        var copias = s.CopiasBN + s.CopiasColor;
        var celular = s.CelularBN + s.CelularColor;
        if (copias + celular > contadas)
            throw new ArgumentException($"La impresora contó {contadas} {(contadas == 1 ? "cara" : "caras")}: entre copias y celular no pueden ser más.");
        if (contadasColor is { } cc && (s.CopiasColor + s.CelularColor > cc || s.CopiasBN + s.CelularBN > contadas - cc))
            throw new ArgumentException($"La impresora contó {contadas - cc} en B/N y {cc} a color: no pueden ser más de cada una.");
        var quedan = contadas - copias;
        if (quedan > 0 && celular == 0)
            throw new ArgumentException("Escribe cuántas se cobran como del celular; si las demás no se cobran, márcala «No vendida».");

        var registro = new RegistroImpresiones(db, pos);
        var nuevas = new List<TrabajoImpresion>();
        var cuando = HoraMexico.ALocal(tarjeta.ImpresoUtc).ToString("HH:mm");
        foreach (var (cuantas, color) in new[] { (s.CopiasBN, false), (s.CopiasColor, true) })
            if (cuantas > 0)
                nuevas.Add(await registro.NuevasCopiasAsync(new SolicitudCopias(Ids.Nuevo(), s.CajaId, s.UsuarioId, cuantas, color, "Carta", false, null,
                    $"De la tarjeta «Copias o celular» de las {cuando}.", impresora.Numero), ct));
        foreach (var nueva in nuevas)
            // A la hora en que las contó la impresora, como la tarjeta.
            (nueva.ImpresoUtc, nueva.EnviadoUtc) = (tarjeta.ImpresoUtc, tarjeta.EnviadoUtc);
        if (quedan == 0)
            Trabajos.Remove(tarjeta);
        else
        {
            await PonerAsync(tarjeta, impresora, quedan, contadasColor is { } c ? c - s.CopiasColor : null, celular, s.CelularColor, ct);
            nuevas.Add(tarjeta);
        }
        await db.SaveChangesAsync(ct);
        return nuevas;
    }

    /// <summary>Las copias del panel registradas hoy en la sucursal, lo más nuevo primero: para «Ya se cobró como copias».</summary>
    public async Task<List<TrabajoImpresion>> CopiasParaLigarAsync(Guid tarjetaId, CancellationToken ct = default)
    {
        var tarjeta = await Trabajos.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tarjetaId, ct) ?? throw new InvalidOperationException("Esa tarjeta ya no existe.");
        var desde = HoraMexico.AUtc(HoraMexico.ALocal(Ahora).Date);
        return await Trabajos.AsNoTracking()
            .Where(t => t.SucursalId == tarjeta.SucursalId && t.Tipo == TipoTrabajoImpresion.Copia && t.ImpresoUtc >= desde
                && t.Cobro != EstadoCobroImpresion.NoVendida)
            .OrderByDescending(t => t.ImpresoUtc).Take(50).ToListAsync(ct);
    }

    /// <summary>
    /// «Ya se cobró como copias»: la tarjeta se junta con unas copias del panel ya registradas. Si se registraron en otra impresora, se pasan a
    /// esta (ahí se equivocaron) y sus caras se toman de la tarjeta; lo que quede de ella se suma a esas copias (eran más de las que se
    /// anotaron; su precio no cambia), y la tarjeta se quita.
    /// </summary>
    public async Task LigarAsync(Guid tarjetaId, SolicitudLigarTarjeta s, CancellationToken ct = default)
    {
        var impresoraId = (await Trabajos.AsNoTracking().Where(t => t.Id == tarjetaId).Select(t => t.ImpresoraId).FirstOrDefaultAsync(ct))
            ?? throw new InvalidOperationException("Esa tarjeta ya no existe.");
        using var _ = await CandadoAsync(impresoraId, ct);
        var tarjeta = await TarjetaAsync(tarjetaId, ct);
        if (await Trabajos.FindAsync([s.CopiasId], ct) is not { Tipo: TipoTrabajoImpresion.Copia } copias || copias.SucursalId != tarjeta.SucursalId)
            throw new InvalidOperationException("Esas copias no existen.");
        if (copias.Cobro == EstadoCobroImpresion.NoVendida)
            throw new InvalidOperationException("Esas copias se marcaron no vendidas: elige otras.");
        var cuando = HoraMexico.ALocal(tarjeta.ImpresoUtc).ToString("HH:mm");
        var impresoras = new ServicioImpresora(db, pos);
        if (copias.ImpresoraId != impresoraId)
        {
            var antes = copias.ImpresoraId is { } otra ? (await impresoras.PorIdAsync(otra, ct))?.Nombre : null;
            copias.ImpresoraId = impresoraId;
            copias.Nota = Juntar(copias.Nota, $"Se pasaron de la {antes ?? "otra impresora"} a la {tarjeta.Cola} (tarjeta «Copias o celular» de las {cuando}).");
            await db.SaveChangesAsync(ct);
            await ConsumirSinCandadoAsync(impresoraId, copias, tarjeta.Id, ct);
        }
        if (await Trabajos.FindAsync([tarjeta.Id], ct) is { } queda && db.Entry(queda).State != EntityState.Detached
            && (queda.CarasContadas ?? queda.Caras) is var resto and > 0)
        {
            var restoColor = Math.Min(DetalleDelContador.Leer(queda.DetalleJson)?.ContadasColor ?? queda.CarasColor, resto);
            (copias.Caras, copias.CarasColor, copias.Hojas) = (copias.Caras + resto, copias.CarasColor + restoColor, copias.Hojas + resto);
            copias.CostoTintaCentavos += queda.CostoTintaCentavos;
            copias.CostoPapelCentavos += queda.CostoPapelCentavos;
            copias.CostoOtrosCentavos += queda.CostoOtrosCentavos;
            copias.CostoCentavos += queda.CostoCentavos;
            (copias.TintaC, copias.TintaM, copias.TintaY, copias.TintaK) =
                (copias.TintaC + queda.TintaC, copias.TintaM + queda.TintaM, copias.TintaY + queda.TintaY, copias.TintaK + queda.TintaK);
            copias.Nota = Juntar(copias.Nota, $"Con {resto} {(resto == 1 ? "cara" : "caras")} más que contó la impresora (tarjeta «Copias o celular» de las {cuando}).");
            Trabajos.Remove(queda);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Juntar(string? nota, string texto) => string.IsNullOrWhiteSpace(nota) ? texto : $"{nota} {texto}";

    // ── En la Configuración ──────────────────────────────────────────────────

    /// <summary>Cambia las tarjetas de una impresora (quien llama revisa el permiso). Al ponerlas, se cuenta desde ahora.</summary>
    public void Cambiar(Impresora impresora, AjustesContadorDto nuevos)
    {
        var antes = AjustesDelContador.Leer(impresora.ContadorJson);
        var despues = (antes ?? AjustesDelContador.PorOmision(impresora.Perfil)) with
        {
            Activas = nuevos.Activas, CoberturaBN = nuevos.CoberturaBN, CoberturaColor = nuevos.CoberturaColor, PausaSegundos = nuevos.PausaSegundos,
        };
        despues.Revisar();
        if (antes is null || !antes.Activas && despues.Activas)
            despues = despues with { DesdeUtc = Ahora };
        impresora.ContadorJson = AjustesDelContador.Escribir(despues);
    }

    public static AjustesContadorDto Dto(AjustesDelContador a) => new(a.Activas, a.CoberturaBN, a.CoberturaColor, a.PausaSegundos);

    /// <summary>
    /// Lo del día de una impresora: lo que contó (<paramref name="comparacion"/>), lo registrado y lo que fue a sus tarjetas, cobradas, por
    /// cobrar o no vendidas.
    /// </summary>
    public async Task<ContadorDeImpresoraDto> DeLaImpresoraAsync(Impresora impresora, CounterCheck? comparacion, DateOnly fecha, CancellationToken ct = default)
    {
        var ajustes = Dto(AjustesDelContador.De(impresora));
        if (comparacion is null)
            return new ContadorDeImpresoraDto(ajustes, null);
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var tarjetas = await new ServicioImpresora(db, pos).TrabajosDe(impresora)
            .Where(t => t.Tipo == TipoTrabajoImpresion.Contador && t.ImpresoUtc >= desde && t.ImpresoUtc < hasta)
            .Select(t => new { Contadas = t.CarasContadas ?? t.Caras, t.Cobro }).ToListAsync(ct);
        int De(EstadoCobroImpresion cobro) => tarjetas.Where(t => t.Cobro == cobro).Sum(t => t.Contadas);
        var enTarjetas = tarjetas.Sum(t => t.Contadas);
        return new ContadorDeImpresoraDto(ajustes, new ConciliacionDelDiaDto(comparacion.Counted, comparacion.Registered.PrintSides,
            comparacion.Registered.CopySides, enTarjetas, De(EstadoCobroImpresion.Cobrada), De(EstadoCobroImpresion.PorCobrar),
            De(EstadoCobroImpresion.NoVendida), Math.Max(0, comparacion.Unregistered - enTarjetas)));
    }

    /// <summary>Lo que dice de su contador la tarjeta de F2.</summary>
    public static TarjetaDelContadorDto? Dto(TrabajoImpresion t)
    {
        if (t.Tipo != TipoTrabajoImpresion.Contador)
            return null;
        var d = DetalleDelContador.Leer(t.DetalleJson);
        return new TarjetaDelContadorDto(t.CarasContadas ?? t.Caras, d?.ContadasColor, HoraMexico.ALocal(d?.DesdeUtc ?? t.EnviadoUtc ?? t.ImpresoUtc),
            HoraMexico.ALocal(t.ImpresoUtc), d?.CoberturaBN ?? AjustesDelContador.CoberturaPorOmision, d?.CoberturaColor ?? AjustesDelContador.CoberturaPorOmision);
    }
}
