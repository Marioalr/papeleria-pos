using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>
/// La sección «ControlPrint» del servicio (servicio.json, en la caja 2): a qué Control Print conectarse para ver su cola. El
/// código de conexión va solo en la variable de entorno del servicio, <c>ControlPrint__Codigo</c> (como <c>Eleventa__Clave</c>; lo
/// pone <c>instalacion\poner-codigo-controlprint.ps1</c>): uno en un archivo se ignora (<see cref="ServiciosImpresiones.CodigoDelEntorno"/>).
/// Sin la sección no hay tarjetas de Control Print (como antes).
/// </summary>
public sealed class OpcionesControlPrint
{
    /// <summary>La IP o el nombre de la PC de Control Print (la caja principal, «192.168.1.106»).</summary>
    public string? Servidor { get; set; }

    public int Puerto { get; set; } = ProtocoloControlPrint.PuertoPorOmision;

    /// <summary>La huella SHA-256 de su certificado; sin ella, se fija la del primer saludo y se guarda junto a la base.</summary>
    public string? Huella { get; set; }

    /// <summary>Solo de la variable de entorno ControlPrint__Codigo (la pone el servicio al arrancar); nunca de un archivo.</summary>
    public string? Codigo { get; set; }

    /// <summary>De cuántos días se ven sus «por cobrar» (2: hoy y ayer).</summary>
    public int Dias { get; set; } = 2;
}

/// <summary>
/// Un trabajo de Control Print del ticket ya no se puede cobrar así (ya no está por cobrar allá, se cobró aquí, cambió su precio,
/// tiene hojas por revisar o Control Print no contestó y no se leyó hace poco): el cobro no se hace y la caja recibe
/// <see cref="ControlPrintAlCobrarDto"/> con un 409.
/// </summary>
public sealed class ControlPrintCambio(ControlPrintAlCobrarDto cambio) : InvalidOperationException(cambio.Mensaje)
{
    public ControlPrintAlCobrarDto Cambio { get; } = cambio;
}

/// <summary>
/// La cola de Control Print en el servicio de la caja 2 (2026-10-25): la mantiene al día (<see cref="ColaControlPrint"/>, solo
/// leyendo), guarda sus costos como los de la sucursal cuando cambian (con ellos se cobra lo que imprime la caja 2), al cobrar revisa
/// otra vez los trabajos del ticket y, después de cada lectura, revisa si algo que se cobró aquí se cobró también allá (2026-10-29).
/// Sin la sección «ControlPrint», no hace nada.
/// </summary>
public sealed class ControlPrintEnElServicio : BackgroundService
{
    /// <summary>
    /// Lo más que el cobro espera a Control Print al revisar sus tarjetas (2026-10-29). La caja espera el cobro 2.5 s
    /// (<c>Transporte.PlazoEscritura</c>): 2 s del folio de eleventa (<c>Eleventa:SegundosAlCobrar</c>) y medio segundo para la red y
    /// la base. Lo que tarde la revisión se le quita a la espera de eleventa (<c>ApiCaja</c>), así que por Control Print el cobro nunca
    /// pasa de ese plazo; con medio segundo le sobra a la red de la tienda (una búsqueda por hora, indexada allá, tarda milisegundos) y
    /// a eleventa le quedan al menos 1.5 s. Pasado esto, Control Print «no contesta».
    /// </summary>
    public static readonly TimeSpan PresupuestoRevision = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Si Control Print no contesta al cobrar, sus tarjetas del ticket se cobran con lo último que se leyó si es de hace esto o menos
    /// (2026-10-29, decidido, por revisar): quedan «sin revisar» y se revisan cuando vuelva a contestar.
    /// </summary>
    public static readonly TimeSpan UltimaLecturaVale = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _alcances;
    private readonly TimeProvider _reloj;
    private readonly ILogger<ControlPrintEnElServicio> _log;
    private readonly int _dias;
    private readonly string? _avisoCodigo;

    /// <param name="avisoCodigo">El código de Control Print estaba en un archivo y se ignoró: dónde (nunca el código).</param>
    public ControlPrintEnElServicio(OpcionesControlPrint? opciones, string? archivoHuella, IServiceScopeFactory alcances, TimeProvider reloj,
        ILogger<ControlPrintEnElServicio> log, string? avisoCodigo = null)
    {
        (_alcances, _reloj, _log, _avisoCodigo) = (alcances, reloj, log, avisoCodigo);
        _dias = Math.Clamp(opciones?.Dias ?? 2, 1, 31);
        Cola = new ColaControlPrint(opciones?.Servidor is { Length: > 0 } servidor
            ? new OpcionesColaControlPrint(servidor)
            {
                Puerto = opciones.Puerto,
                Huella = string.IsNullOrWhiteSpace(opciones.Huella) ? null : opciones.Huella.Trim(),
                Codigo = string.IsNullOrWhiteSpace(opciones.Codigo) ? null : opciones.Codigo,
                Dias = _dias,
                ArchivoHuella = archivoHuella,
                Reloj = reloj,
            }
            : null)
        {
            AlCambiarCostos = GuardarCostosAsync,
            AlLeer = RevisarLoCobradoAsync,
        };
        Cola.CambioDeEstado += estado =>
        {
            if (estado.Contesta == true)
                _log.LogInformation("Control Print en {Servidor} ({Nombre}, {Version}) contesta: se lee su cola por cobrar.", estado.Servidor, estado.Nombre,
                    estado.VersionServidor);
            else if (estado.Problema is { } problema)
                _log.LogWarning("Control Print no contesta: {Problema}", problema);
            if (estado.AvisoCostos is { } costos)
                _log.LogWarning("{Aviso}", costos);
        };
        if (Cola.Configurado && avisoCodigo is not null)
            _log.LogWarning("{Aviso}", avisoCodigo);
    }

    public ColaControlPrint Cola { get; }

    /// <summary>
    /// La cola corre mientras corra el servicio. Algo que no se esperaba no detiene el servicio (que es el de la venta): se anota y la
    /// cola vuelve a empezar en un minuto.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Cola.CorrerAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "La cola de Control Print se detuvo por algo que no se esperaba: vuelve a empezar en un minuto.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Los costos de Control Print quedan como los de cada sucursal de este servicio (hoy, una): con ellos se le pone precio a lo que
    /// imprime la caja 2, también a las copias del panel y al cotizar, y si al arrancar Control Print no contesta, son los de antes. Los
    /// que reemplazan se quedan en su historial; los de fábrica de Control Print ni llegan aquí (<see cref="ColaControlPrint"/>).
    /// </summary>
    private async Task GuardarCostosAsync(CostSettings costos, CancellationToken ct)
    {
        try
        {
            using var alcance = _alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var registro = ImpresionesDelPos.Registro(db);
            foreach (var sucursal in await db.Sucursales.AsNoTracking().Select(s => s.Id).ToListAsync(ct))
            {
                if (await registro.GuardarCostosSiCambiaronAsync(sucursal, costos, ct))
                    _log.LogInformation("Los costos de impresión de la sucursal ahora son los de Control Print (los de antes quedan en su historial).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "No se pudieron guardar los costos de Control Print: se siguen usando los de memoria.");
        }
    }

    // ── Lo cobrado aquí que también se cobró allá (2026-10-29) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Después de cada lectura de la cola (la de los días que se ven, al menos cada 20 s aunque Control Print no avise, y de vez en
    /// cuando la de antes, hasta 30 días): lo que se cobró aquí sigue «por cobrar» en Control Print (nunca se le escribe). Si dejó de
    /// estarlo, se lee cómo quedó: si allá también se cobró (la caja principal lo cobró, por ejemplo en un lote suyo con lo de otro
    /// cliente), se avisa (F2, el chip de arriba, los avisos del dueño y el registro del servicio) y se guarda, así sigue después de
    /// reiniciar; si no (no vendida, cancelada, borrada), queda revisado y no se vuelve a buscar. Lo que se cobró sin revisar porque
    /// Control Print no contestaba queda revisado al verlo todavía por cobrar allá.
    /// </summary>
    private async Task RevisarLoCobradoAsync(LecturaControlPrint lectura, CancellationToken ct)
    {
        try
        {
            using var alcance = _alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var hasta = lectura.HastaUtc ?? DateTime.MaxValue;
            var candidatos = await db.Set<TrabajoControlPrintCobrado>()
                .Where(c => c.LiberadoUtc == null && c.RevisadoUtc == null && c.DetectadoUtc >= lectura.DesdeUtc && c.DetectadoUtc < hasta)
                .ToListAsync(ct);
            if (candidatos.Count == 0)
                return;
            var cambio = false;
            foreach (var cobrado in candidatos.Where(c => c.SinRevisar && lectura.SiguePorCobrar(c.TrabajoId, c.DetectadoUtc)))
            {
                // Allá sigue por cobrar: no se había cobrado en la caja principal cuando se cobró aquí sin revisar.
                cobrado.SinRevisar = false;
                cambio = true;
            }
            var yaNoEstan = candidatos.Where(c => !lectura.SiguePorCobrar(c.TrabajoId, c.DetectadoUtc) && lectura.Alcanza(c.DetectadoUtc)).ToList();
            if (yaNoEstan.Count > 0)
            {
                var leidos = await Cola.LeerTrabajosAsync(yaNoEstan.Select(c => (c.TrabajoId, (DateTime?)c.DetectadoUtc)).ToList(), ct);
                if (leidos.SinRespuesta.Count > 0)
                    // Se vuelve a intentar con la siguiente lectura.
                    _log.LogInformation("No se pudo leer en Control Print lo que dejó de estar por cobrar: {Problema}", leidos.Problema);
                var ahora = _reloj.GetUtcNow().UtcDateTime;
                foreach (var cobrado in yaNoEstan.Where(c => !leidos.SinRespuesta.Contains(c.TrabajoId)))
                {
                    var trabajo = leidos.Trabajos.GetValueOrDefault(cobrado.TrabajoId);
                    // Sigue por cobrar y cobrable allá (no salió en la lectura por otra cosa, como una página que se recorrió): en la siguiente.
                    // Por cobrar pero ya no cobrable (cancelado en su cola, 2026-10-29) cuenta como revisado: ya no se cobra allá.
                    if (trabajo is { Cobro: CobroControlPrint.PorCobrar, Estado: EstadoTrabajoControlPrint.Analizado or EstadoTrabajoControlPrint.Impreso })
                        continue;
                    (cobrado.RevisadoUtc, cobrado.SinRevisar, cambio) = (ahora, false, true);
                    if (trabajo is not { Cobro: CobroControlPrint.Cobrado })
                        continue;
                    cobrado.CobradoEnControlPrintUtc = trabajo.CobroUtc is { } cuando ? TarjetaControlPrint.Utc(cuando) : ahora;
                    cobrado.CobradoEnControlPrintPor = string.IsNullOrWhiteSpace(trabajo.CobroPor) ? null : trabajo.CobroPor.Trim();
                    var venta = await db.Ventas.AsNoTracking().Where(v => v.Id == cobrado.VentaId).Select(v => new { v.Serie, v.Folio }).FirstOrDefaultAsync(ct);
                    var texto = CobradaDosVeces(cobrado, venta is { Folio: { } folio } ? $"{venta.Serie}-{folio:000000}" : null);
                    _log.LogWarning("{Aviso}", texto);
                    ImpresionesDelPos.Pos(db).Avisar(cobrado.SucursalId, AvisoImpresiones.CobradaDosVeces, texto, $"controlprint-dos-veces-{cobrado.Id:N}");
                }
            }
            if (cambio)
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "No se pudo revisar si lo cobrado aquí se cobró también en Control Print: se revisa en la siguiente lectura.");
        }
    }

    /// <summary>«#1234 (Tesis.pdf) se cobró dos veces: aquí, en la venta A-000001 ($15.00, 28/10 10:31), y en Control Print (Ana, 28/10 11:02)…».</summary>
    private static string CobradaDosVeces(TrabajoControlPrintCobrado c, string? folio) =>
        $"#{c.TrabajoId} ({c.Documento}) se cobró dos veces: aquí, en la venta {folio ?? "de esta caja"} ({AlertasTelegram.Dinero(c.PrecioCentavos)}, "
        + $"{HoraMexico.ALocal(c.CobradoUtc):dd/MM HH:mm}), y en Control Print, en la caja principal"
        + $" ({(c.CobradoEnControlPrintPor is { } quien ? $"{quien}, " : "")}{HoraMexico.ALocal(c.CobradoEnControlPrintUtc ?? c.CobradoUtc):dd/MM HH:mm}). "
        + "Revisa con el cliente y devuélvele uno.";

    /// <summary>Lo cobrado aquí y también allá de los últimos días, mientras la venta de aquí siga cobrada; lo más nuevo primero.</summary>
    private async Task<List<string>> CobradasDosVecesAsync(PosDbContext db, Guid? sucursalId, CancellationToken ct)
    {
        var desde = _reloj.GetUtcNow().UtcDateTime.AddDays(-_dias);
        var filas = await (
                from c in db.Set<TrabajoControlPrintCobrado>().AsNoTracking()
                where (sucursalId == null || c.SucursalId == sucursalId) && c.LiberadoUtc == null && c.CobradoEnControlPrintUtc != null
                      && c.RevisadoUtc >= desde
                join v in db.Ventas.AsNoTracking() on c.VentaId equals v.Id into ventas
                from v in ventas.DefaultIfEmpty()
                select new { Cobrado = c, Serie = v == null ? null : v.Serie, Folio = v == null ? null : v.Folio })
            .ToListAsync(ct);
        return filas.OrderByDescending(f => f.Cobrado.RevisadoUtc)
            .Select(f => CobradaDosVeces(f.Cobrado, f.Folio is { } folio ? $"{f.Serie}-{folio:000000}" : null))
            .ToList();
    }

    // ── El cobro ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Los trabajos de Control Print que van en un ticket, revisados justo antes de cobrar (solo leer, con
    /// <see cref="PresupuestoRevision"/>): siguen por cobrar allá, no se han cobrado aquí, tienen el precio que vio el cajero y no tienen
    /// hojas por revisar. Si Control Print no contesta a tiempo, los que se leyeron hace <see cref="UltimaLecturaVale"/> o menos se
    /// cobran con eso y quedan «sin revisar»; los demás no («cóbrala cuando conteste»). Si algo no se cobra,
    /// <see cref="ControlPrintCambio"/>: no se cobra nada y la caja corrige el ticket.
    /// </summary>
    public async Task<List<TrabajoControlPrintParaCobrar>> ParaCobrarAsync(PosDbContext db, Guid sucursalId,
        IReadOnlyList<ImpresionDeControlPrintSolicitada> pedidas, CancellationToken ct = default)
    {
        if (pedidas.Count == 0)
            return [];
        var ids = pedidas.Select(p => p.Id).Distinct().ToList();
        if (!Cola.Configurado)
            throw new ControlPrintCambio(new ControlPrintAlCobrarDto(
                "Este servicio no tiene configurado Control Print: se quitaron sus impresiones del ticket.", ids, []));
        var revision = await Cola.RevisarAsync(pedidas.Select(p => (p.Id, p.PrecioCentavos)).ToList(), PresupuestoRevision, ct);

        // Lo que Control Print contestó a tiempo cuenta tal cual (2026-10-29): lo cobrado allá, lo no vendido o con otro precio no se
        // cobra aquí, ni siquiera «sin revisar». Solo lo que no contestó usa lo último que se leyó (si es de hace 10 minutos o menos).
        var porque = new List<string>(revision.YaNo.Select(y => y.Porque));
        var quitar = new List<long>(revision.YaNo.Select(y => y.Id));
        var precios = revision.OtroPrecio.Select(p => new ImpresionDeControlPrintSolicitada(p.Id, p.PrecioCentavos)).ToList();
        porque.AddRange(precios.Select(p => $"a #{p.Id} le cambiaron el precio en Control Print: ahora es {AlertasTelegram.Dinero(p.PrecioCentavos)}"));
        var siguen = revision.Siguen.Select(t => (Trabajo: t, SinRevisar: false)).ToList();
        if (revision.SinRespuesta is { Count: > 0 } sinRespuesta)
        {
            var conLoUltimo = ConLoUltimoQueSeLeyo(pedidas.Where(p => sinRespuesta.Contains(p.Id)).ToList(), quitar, precios, porque);
            siguen.AddRange(conLoUltimo.Select(t => (t, true)));
            if (conLoUltimo.Count > 0)
                _log.LogWarning("Control Print no contesta al cobrar ({Problema}): {Cuantas} tarjeta(s) se cobran con lo último que se leyó y se revisan cuando "
                    + "conteste.", revision.NoContesta, conLoUltimo.Count);
        }

        var cobradosAqui = await ImpresionesDelPos.EnLaVenta(db).CobradosAsync(sucursalId, ids, ct);
        var paraCobrar = new List<(TrabajoControlPrint Trabajo, bool SinRevisar)>();
        foreach (var (t, sinRevisar) in siguen)
        {
            if (cobradosAqui.FirstOrDefault(c => EsElMismo(c, t)) is { } cobrado)
            {
                quitar.Add(t.Id);
                porque.Add($"#{t.Id} ya se cobró en esta caja ({HoraMexico.ALocal(cobrado.CobradoUtc):dd/MM HH:mm})");
            }
            else
                paraCobrar.Add((t, sinRevisar));
        }
        if (quitar.Count > 0 || precios.Count > 0)
            throw new ControlPrintCambio(new ControlPrintAlCobrarDto(
                $"No se cobró: {string.Join("; ", porque)}. {(quitar.Count > 0 ? "Se quitó del ticket lo que no se cobra aquí; " : "")}"
                + "revisa el total y cobra otra vez.", quitar, precios));
        return paraCobrar.Select(x => new TrabajoControlPrintParaCobrar(x.Trabajo.Id, TarjetaControlPrint.Utc(x.Trabajo.DetectadoUtc),
            TarjetaControlPrint.Titulo(x.Trabajo), x.Trabajo.PcOrigen.Trim(), TarjetaControlPrint.Centavos(TarjetaControlPrint.PrecioACobrar(x.Trabajo)),
            TarjetaControlPrint.Centavos(x.Trabajo.CostoTotal), x.Trabajo.Tipo == TipoTrabajoControlPrint.CopiaEnElPanel, x.SinRevisar)).ToList();
    }

    /// <summary>
    /// Control Print no contestó la revisión de estas tarjetas (2026-10-29, decidido, por revisar): una se cobra con lo último que se
    /// leyó de ella si es de hace <see cref="UltimaLecturaVale"/> o menos (al precio de entonces) y queda «sin revisar»; si no, se quita
    /// del ticket: se cobra cuando conteste. Nunca «a mano»: así no queda ligada a su trabajo y se cobraría dos veces.
    /// </summary>
    private List<TrabajoControlPrint> ConLoUltimoQueSeLeyo(IReadOnlyList<ImpresionDeControlPrintSolicitada> pedidas, List<long> quitar,
        List<ImpresionDeControlPrintSolicitada> precios, List<string> porque)
    {
        var estado = Cola.Estado;
        var reciente = estado.LeidaUtc is { } leida && _reloj.GetUtcNow() - leida <= UltimaLecturaVale;
        var siguen = new List<TrabajoControlPrint>();
        foreach (var pedida in pedidas.DistinctBy(p => p.Id))
        {
            var trabajo = reciente ? estado.PorCobrar.FirstOrDefault(t => t.Id == pedida.Id) : null;
            if (trabajo is null)
            {
                quitar.Add(pedida.Id);
                porque.Add($"Control Print no contesta: cobra #{pedida.Id} cuando conteste (no se ha leído en los últimos "
                    + $"{UltimaLecturaVale.TotalMinutes:0} minutos)");
            }
            else if (TarjetaControlPrint.RevisarHojas(trabajo) is not null)
            {
                quitar.Add(pedida.Id);
                porque.Add(ColaControlPrint.HojasPorRevisar(pedida.Id));
            }
            else if (TarjetaControlPrint.Centavos(TarjetaControlPrint.PrecioACobrar(trabajo)) is var precio && precio != pedida.PrecioCentavos)
            {
                precios.Add(new ImpresionDeControlPrintSolicitada(pedida.Id, precio));
                porque.Add($"a #{pedida.Id} le cambiaron el precio en Control Print: ahora es {AlertasTelegram.Dinero(precio)}");
            }
            else
                siguen.Add(trabajo);
        }
        return siguen;
    }

    /// <summary>El mismo trabajo de Control Print: el mismo número y la misma hora (un Control Print reinstalado repite números).</summary>
    public static bool EsElMismo(TrabajoControlPrintCobrado cobrado, TrabajoControlPrint trabajo) =>
        cobrado.TrabajoId == trabajo.Id && Math.Abs((cobrado.DetectadoUtc - TarjetaControlPrint.Utc(trabajo.DetectadoUtc)).TotalSeconds) < 1;

    // ── Lo que ve la caja ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// La cola como la ve la caja de esa sucursal: sin los que ya se cobraron aquí y, de los que sí faltan, hasta
    /// <see cref="ColaControlPrint.Limite"/> (2026-10-29: primero se quitan, después se cuentan); si Control Print no contesta, sin
    /// tarjetas (no se pueden revisar al cobrar). También lo cobrado aquí y allá.
    /// </summary>
    public async Task<ColaControlPrintDto> ParaLaCajaAsync(PosDbContext db, Guid sucursalId, bool conCosto, CancellationToken ct = default)
    {
        var estado = Cola.Estado;
        if (!estado.Configurado)
            return new ColaControlPrintDto(false, null, null, null, null, 0, []);
        var dosVeces = await CobradasDosVecesAsync(db, sucursalId, ct);
        var visibles = new List<TrabajoControlPrint>();
        var mas = 0;
        if (estado.Contesta == true)
        {
            var cobrados = await ImpresionesDelPos.EnLaVenta(db).CobradosDesdeAsync(sucursalId, Cola.DesdeUtc().AddDays(-1), ct);
            var faltan = estado.PorCobrar.Where(t => !cobrados.Any(c => EsElMismo(c, t))).ToList();
            visibles = faltan.Take(ColaControlPrint.Limite).ToList();
            mas = faltan.Count - visibles.Count + Math.Max(0, estado.Total - estado.PorCobrar.Count);
        }
        var tarjetas = visibles.Select(t => TarjetaControlPrint.De(t, conCosto)).ToList();
        var servidor = string.IsNullOrEmpty(estado.Nombre) ? estado.Servidor : estado.Nombre;
        var aviso = estado.Contesta == false ? estado.Problema ?? "Control Print no contesta." : null;
        var version = TarjetaControlPrint.Version(tarjetas, estado.Contesta, aviso, estado.SoloInvitado, mas, servidor, string.Join("\n", dosVeces));
        return new ColaControlPrintDto(true, estado.Contesta, aviso, servidor, estado.LeidaUtc is { } leida ? HoraMexico.ALocal(leida.UtcDateTime) : null,
            version, tarjetas, estado.SoloInvitado, mas, dosVeces);
    }

    /// <summary>Cómo ve este servicio a Control Print (sin el código), con lo que hay que saber.</summary>
    public async Task<EstadoControlPrintDto> EstadoParaVerAsync(PosDbContext db, CancellationToken ct = default)
    {
        var e = Cola.Estado;
        var avisos = new List<string>();
        if (e.Configurado && _avisoCodigo is not null)
            avisos.Add(_avisoCodigo);
        if (e.AvisoCostos is { } costos)
            avisos.Add(costos);
        if (e.Configurado)
            avisos.AddRange(await CobradasDosVecesAsync(db, null, ct));
        return new EstadoControlPrintDto(e.Configurado, e.Servidor, e.Puerto, e.TieneCodigo, e.Contesta, e.Nombre, e.VersionServidor,
            string.IsNullOrEmpty(e.Huella) ? null : ProtocoloControlPrint.HuellaLegible(e.Huella), e.HuellaConfigurada,
            e.LeidaUtc is { } leida ? HoraMexico.ALocal(leida.UtcDateTime) : null, e.Total, e.Lecturas, e.Problema, e.SoloInvitado, avisos);
    }

    public override void Dispose()
    {
        Cola.Dispose();
        base.Dispose();
    }
}
