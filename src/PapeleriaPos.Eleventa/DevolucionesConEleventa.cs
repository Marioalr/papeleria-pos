using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que trajo la vuelta: cuántas devoluciones de eleventa se pasaron a las ventas de aquí y cuántas quedaron por revisar.</summary>
/// <param name="Retenidas">
/// Las que esperan: su ticket tiene una devolución de aquí por anotar a mano en eleventa, y pueden ser esa anotación (se
/// elige al marcarla «Ya lo anoté en eleventa»). No se pasan todavía.
/// </param>
public sealed record ResultadoVuelta(int Pasadas, int PorRevisar, int Retenidas = 0);

/// <summary>
/// Una devolución o cancelación de aquí, de un ticket que está en eleventa, que espera su anotación a mano allá: al marcarla
/// «Ya lo anoté en eleventa» se elige cuál de las de eleventa de ese ticket es (<see cref="DevolucionesConEleventa.AnotacionesEnVivoAsync"/>).
/// </summary>
/// <param name="HechaUtc">Cuándo se hizo aquí: su anotación es de después.</param>
/// <param name="Armada">Lo que se habría mandado a eleventa, para compararlo con lo que se elija.</param>
/// <param name="LoQueQuedaba">
/// La de aquí devolvió todo lo que le quedaba a la venta: el puente la habría escrito en eleventa como la cancelación del
/// ticket (2026-10-24), y así la anota la tienda. Su anotación puede ser esa cancelación (revisión del PR #139).
/// </param>
public sealed record PorAnotarEnEleventa(
    EnvioAEleventa Envio, Guid VentaId, int TicketEleventa, DateTime HechaUtc, DevolucionParaEleventa Armada, bool LoQueQuedaba = false);

/// <summary>
/// Lo que eleventa devolvió de un ticket y la vuelta retiene porque puede ser la anotación a mano de devoluciones de aquí
/// (<see cref="DevolucionesConEleventa.RetenidasAsync"/>).
/// </summary>
/// <param name="Esperan">Las devoluciones de aquí que lo esperan.</param>
/// <param name="Retenido">Lo que devolvió allá, por variante de aquí (lo común no lleva existencia).</param>
/// <param name="Todo">Si entre lo retenido hay una cancelación: se lleva todo lo que quedaba en el ticket.</param>
public sealed record AnotacionRetenida(IReadOnlySet<Guid> Esperan, IReadOnlyDictionary<Guid, Cantidad> Retenido, bool Todo);

/// <summary>
/// La caja 2 en eleventa (la del puente) y el turno que eleventa tiene abierto ahora en ella, leído en vivo: solo los tickets
/// de ese turno se devuelven en la caja 2 (<see cref="DevolucionesConEleventa.DeOtroTurno"/>).
/// </summary>
/// <param name="CajaId"><c>CAJAS.ID</c>.</param>
/// <param name="TurnoId"><c>TURNOS.ID</c>; vacío si eleventa no tiene turno abierto en la caja.</param>
public sealed record TurnoDelPuente(int CajaId, int? TurnoId)
{
    /// <summary>Si el ticket es de este turno de la caja 2: el puente sabe escribir su devolución.</summary>
    public bool Tiene(TicketLeido t) => t.CajaId == CajaId && t.TurnoId is { } turno && turno == TurnoId;
}

/// <summary>
/// Las devoluciones y cancelaciones de ida y vuelta con eleventa (decisión del dueño, 2026-10-12; docs/migracion-eleventa.md,
/// «Devoluciones de ida y vuelta»). Solo del piloto: se quita al dejar eleventa.
/// <list type="bullet">
/// <item><b>Solo lo del turno de ahora</b> (revisión del 2026-10-17, decidido; por revisar con el dueño): mientras dure el
/// piloto, la caja 2 devuelve o cancela solo lo que el puente sabe escribir en eleventa: sus ventas y los tickets cobrados en
/// el eleventa de la caja 2 cuyo ticket es del turno que eleventa tiene abierto ahora (se ve en vivo; sin eleventa, para sus
/// ventas, mientras su turno aquí siga abierto). Lo de otro turno, de otra caja o de la principal se devuelve en eleventa
/// (<see cref="DeOtroTurno"/>, <see cref="DeOtroTurnoEnEleventa"/>); la de una venta de aquí llega sola con la lectura (la
/// vuelta), como cualquier otra de eleventa.</item>
/// <item><b>La ida:</b> en la caja 2 se devuelve un ticket que se cobró en eleventa. Se lee en vivo (el ticket, sus
/// artículos y sus devoluciones); sin eleventa no se devuelve. Solo efectivo, 7 días y los permisos de la caja nueva. El
/// ticket leído se guarda en el puente (<see cref="DevolucionDeEleventa"/>), no como venta. El dinero sale del turno de la
/// caja nueva como una salida de efectivo, que no va a eleventa como salida: la devolución va por la bandeja, como la
/// escribe eleventa. Lo que regresa al inventario entra al kardex de aquí, y mientras no llega a eleventa la lectura lo
/// sigue sumando.</item>
/// <item><b>La vuelta:</b> la lectura de cada pocos minutos trae, en la misma foto que las existencias, las devoluciones y
/// cancelaciones que se hicieron en eleventa de los tickets que mandó la caja 2, y las pasa a la venta de aquí antes de las
/// existencias: una devolución sin efectivo (el dinero salió de la caja de eleventa, así que el corte de aquí no la
/// cuenta), con la entrada al kardex, a nombre del cajero de la venta y con el motivo «Hecha en eleventa…». La cancelación de
/// un ticket que ya tenía devoluciones (eleventa cancela lo que quedó) es aquí la devolución de lo que quedaba: la venta sigue
/// cobrada, como cuando se devuelve todo en la caja (revisión del PR #131, 2026-10-22). Lo propio se
/// reconoce por la marca <c>TICKET_ID</c> + <c>DEVUELTO_EN</c>, y lo ya pasado por <c>DEVOLUCIONES.ID</c>. Nada de esto va
/// a la bandeja. Lo que no se puede pasar (ya se devolvió aquí, no está en el ticket) queda por revisar. Lo que puede ser
/// la anotación a mano de una devolución de aquí que no pasó sola espera: se elige al marcar la de aquí «Ya lo anoté en
/// eleventa» (<see cref="AnotacionesEnVivoAsync"/>), y lo que no se elija se pasa después.</item>
/// <item>Antes de devolver aquí un ticket de la caja 2 que ya está en eleventa, se revisa en vivo que allá no se haya
/// devuelto (<see cref="RevisarEnVivoAsync"/>); si eleventa no contesta, se deja pasar (decisión del 2026-10-11) y un
/// choque queda por revisar. Si eleventa ya le devolvió algo, lo demás se devuelve en eleventa: el reembolso de aquí se
/// redondea con lo que salió de esta caja, y lo de eleventa no salió de ella.</item>
/// </list>
/// </summary>
public static class DevolucionesConEleventa
{
    /// <summary>Cuánto se espera a eleventa al buscar o revisar un ticket para devolver: la cajera tiene al cliente enfrente.</summary>
    public static readonly TimeSpan EsperaEnVivo = TimeSpan.FromSeconds(3);

    /// <summary>Hasta cuántos días atrás se buscan en eleventa las devoluciones de lo que mandó la caja 2.</summary>
    public const int DiasDeLaVuelta = 31;

    public const string NoContesta =
        "No contesta eleventa: un ticket de eleventa solo se devuelve con la red (para ver cómo está allá). Espera a que vuelva.";

    public const string YaTuvoDevolucionEnEleventa =
        "Este ticket ya tuvo una devolución en eleventa: lo que queda se devuelve en eleventa.";

    public const string NoFueEnEfectivo = "Se cobró con otra forma de pago en eleventa: se devuelve en eleventa.";

    /// <summary>
    /// Por qué la caja 2 no devuelve ni cancela una venta suya de otro turno de eleventa: el puente todavía no sabe cómo anota
    /// eleventa esa devolución (docs/pruebas-en-tienda.md). Se devuelve en eleventa, que sigue abierto en la caja 2, y su
    /// devolución llega a la venta de aquí sola con la lectura.
    /// </summary>
    public const string DeOtroTurno =
        "Este ticket es de otro turno: devuélvelo en eleventa (está abierto en esta caja). La devolución llega aquí sola con la lectura.";

    /// <summary>
    /// Por qué no se devuelve aquí un ticket cobrado en eleventa de otro turno o de otra caja (la ida): se devuelve en
    /// eleventa. No es una venta de la caja 2: aquí no llega nada (cuarta revisión: no se dice que llega).
    /// </summary>
    public static string DeOtroTurnoEnEleventa(TicketLeido t, TurnoDelPuente puente) =>
        t.CajaId == puente.CajaId
            ? "Este ticket es de otro turno de eleventa: devuélvelo en eleventa (está abierto en esta caja)."
            : $"Este ticket es de otra caja de eleventa ({t.Caja}): devuélvelo en eleventa (está abierto en esta caja).";
    public const string CanceladoEnEleventa = "Ese ticket está cancelado en eleventa.";
    public const string Vencido = "El ticket tiene más de 7 días: ya no admite devoluciones.";

    /// <summary>Por qué una devolución en eleventa de un ticket de la caja 2 no se pasó a su venta: ya no queda tanto aquí.</summary>
    public const string NoAlcanza =
        "En eleventa se devolvió de este ticket algo que aquí ya no queda (¿se devolvió también en la caja 2?): revísalo en las dos.";

    /// <summary>Lo que dice la lectura cuando una devolución de eleventa no se pasó sola a su venta.</summary>
    public const string VueltaPorRevisar =
        "Devoluciones hechas en eleventa que no se pasaron solas a su venta: se revisan en la pantalla de la caja 2.";

    /// <summary>Lo que dice la lectura cuando una devolución de eleventa espera a que se diga si es la anotación a mano de una de aquí.</summary>
    public const string VueltaRetenida =
        "Devoluciones hechas en eleventa que esperan: su ticket tiene una devolución de aquí por anotar a mano (al marcarla "
        + "«Ya lo anoté en eleventa» se elige cuál es su anotación).";

    /// <summary>Lo que puede ir atrasada la hora de la PC de eleventa respecto a la de la caja 2, al buscar una anotación a mano.</summary>
    public static readonly TimeSpan HolguraDeHora = TimeSpan.FromMinutes(10);

    /// <summary>Lo que pide «Ya lo anoté en eleventa» de una devolución de aquí de un ticket que está en eleventa.</summary>
    public const string DiCualEs =
        "Di cuál es su anotación en eleventa: elige la devolución que hiciste allá a mano (o las que, juntas, son la de aquí).";

    public const string NoSeElige = "Esto no se anota eligiendo una devolución de eleventa.";

    /// <summary>Lo que queda anotado en una devolución de aquí que se resolvió a mano sin elegir su anotación en eleventa.</summary>
    public const string ResueltaAMano =
        "Se resolvió a mano en eleventa, sin elegir su anotación: lo que eleventa devolvió de ese ticket se pasa a la venta como cualquier otra devolución de allá.";

    /// <summary>
    /// «Ya lo anoté en eleventa» de una devolución de aquí que todavía se va a mandar sola: si se anotó allá, primero se deja
    /// por revisar, para que la lectura espere a que se diga cuál es su anotación (y no la pase como otra).
    /// </summary>
    public const string PrimeroPorRevisar =
        "Esta devolución todavía se manda sola a eleventa. Si ya la anotaste allá, primero déjala por revisar: así la lectura "
        + "espera a que digas cuál es su anotación.";

    /// <summary>Si es una devolución o cancelación de aquí que todavía se va a mandar sola a eleventa, detrás de su venta que ya está allá.</summary>
    public static async Task<bool> SeMandaSolaAsync(PosDbContext db, EnvioAEleventa envio, CancellationToken ct = default) =>
        envio is { Estado: EstadoEnvioEleventa.Pendiente, Tipo: TipoEnvioEleventa.Devolucion or TipoEnvioEleventa.Cancelacion, VentaId: { } ventaId }
        && await db.EnviosAEleventa.AsNoTracking().AnyAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId
            && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null, ct);

    public const string NoContestaParaElegir =
        "No contesta eleventa: para marcarla hay que ver sus devoluciones de ese ticket. Espera a que vuelva la red.";

    /// <summary>Lo elegido ya no está entre las que pueden ser su anotación (ya se pasó, ya se ligó o ya no está en eleventa).</summary>
    public const string YaNoSeElige =
        "Esa devolución de eleventa ya no se puede elegir (ya se tomó en cuenta o ya no está): vuelve a abrir la lista.";

    // ── La ida ──

    /// <summary>
    /// Los tickets cobrados con ese folio, en vivo, y el turno que eleventa tiene abierto ahora en la caja del puente; vacío si
    /// eleventa no contestó a tiempo (<see cref="EsperaEnVivo"/>).
    /// </summary>
    /// <param name="cajaDelPuente"><c>CAJAS.ID</c> de la caja 2 en eleventa.</param>
    public static async Task<(List<TicketLeido> Tickets, TurnoDelPuente Puente)?> PorFolioEnVivoAsync(ServidorEleventa servidor, int folio, int cajaDelPuente,
        CancellationToken ct = default) =>
        await EnVivoAsync(servidor, async c => new Leido<List<TicketLeido>>(await TicketsDeEleventa.PorFolioAsync(c, folio, FirebirdSinCancelar.Nunca),
            await PuenteAsync(c, cajaDelPuente)), ct) is { } leido
            ? (leido.Valor!, leido.Puente)
            : null;

    /// <summary>
    /// Un ticket con sus artículos y sus devoluciones, en vivo, y el turno que eleventa tiene abierto ahora en la caja del
    /// puente. Sin eleventa, el error lo dice (<see cref="NoContesta"/>).
    /// </summary>
    public static async Task<(TicketLeido Ticket, TurnoDelPuente Puente)> TicketEnVivoAsync(ServidorEleventa servidor, int ticket, int cajaDelPuente,
        CancellationToken ct = default)
    {
        var leido = await EnVivoAsync(servidor, async c => new Leido<TicketLeido>(await TicketsDeEleventa.TicketAsync(c, ticket, FirebirdSinCancelar.Nunca),
            await PuenteAsync(c, cajaDelPuente)), ct) ?? throw new InvalidOperationException(NoContesta);
        return (leido.Valor ?? throw new InvalidOperationException($"En eleventa ya no está ese ticket ({ticket})."), leido.Puente);
    }

    private static async Task<TurnoDelPuente> PuenteAsync(FbConnection c, int cajaDelPuente) =>
        new(cajaDelPuente, await TicketsDeEleventa.TurnoAbiertoAsync(c, cajaDelPuente, FirebirdSinCancelar.Nunca));

    /// <summary>Lo que se leyó en vivo, aunque no esté (para distinguirlo de que eleventa no contestó), con el turno del puente.</summary>
    private sealed record Leido<T>(T? Valor, TurnoDelPuente Puente) where T : class;

    /// <summary>
    /// Lo que la ventana de devoluciones enseña de un ticket de eleventa, como uno de la caja: lo vendido, lo ya devuelto
    /// (allá, y aquí lo que todavía no llega) y lo que queda, o por qué no se devuelve aquí. Los Id son los de eleventa
    /// (<see cref="IdDelTicket"/>, <see cref="IdDelArticulo"/>).
    /// </summary>
    public static async Task<VentaParaDevolverDto> ParaDevolverAsync(PosDbContext db, TicketLeido t, TurnoDelPuente puente, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        var aqui = await DevueltoAquiAsync(db, t.Id, ct);
        var lineas = t.Articulos.Select(a =>
        {
            var x = ParaDevolverDe(t, a, aqui.GetValueOrDefault(a.Id));
            return new LineaDevolucionDto(IdDelArticulo(a.Id), a.Nombre, x.Vendido.Milesimas, x.Devuelto.Milesimas, x.Importe, a.ProductoId is not null,
                DevueltoCentavos: x.YaSalio);
        }).ToList();
        var dias = t.VendidoEn is { } vendidoEn ? Devolucion.DiasParaDevolver - (int)(HoraMexico.ALocal(ahoraUtc) - vendidoEn).TotalDays : 0;
        // Cancelado o vencido, la ventana ya lo dice.
        return new VentaParaDevolverDto(IdDelTicket(t.Id), $"{t.Folio} · {t.Caja} (eleventa)", t.VendidoEn ?? HoraMexico.ALocal(ahoraUtc),
            t.Cancelado ? EstadoVenta.Cancelada : EstadoVenta.Pagada, Centavos(t.Pagado), lineas, PuedeCancelar: false, Math.Max(0, dias),
            NoSeDevuelveEnElPiloto: t.Cancelado || dias <= 0 ? null : !puente.Tiene(t) ? DeOtroTurnoEnEleventa(t, puente) : t.EnEfectivo ? null : NoFueEnEfectivo);
    }

    /// <summary>
    /// Por qué un ticket de eleventa no se devuelve aquí (cancelado, más de 7 días, de otro turno o de otra caja, no fue en
    /// efectivo); vacío si sí.
    /// </summary>
    public static string? PorQueNoSeDevuelveAqui(TicketLeido t, TurnoDelPuente puente, DateTime ahoraUtc) =>
        t.Cancelado ? CanceladoEnEleventa
        : t.VendidoEn is not { } vendido || ahoraUtc - HoraMexico.AUtc(vendido) > TimeSpan.FromDays(Devolucion.DiasParaDevolver) ? Vencido
        : !puente.Tiene(t) ? DeOtroTurnoEnEleventa(t, puente)
        : !t.EnEfectivo ? NoFueEnEfectivo
        : null;

    /// <summary>
    /// Una venta de la caja 2 que ya está en eleventa se devuelve o se cancela aquí solo si su ticket es del turno que eleventa
    /// tiene abierto en la caja 2 (revisión del 2026-10-17, decidido): lo dice eleventa en vivo (<see cref="RevisarEnVivoAsync"/>,
    /// cuarta revisión). Esto es lo que se usa sin eleventa: su turno aquí, que se cierra cuando la caja toma el corte de
    /// eleventa (<see cref="TurnosDelPiloto"/>). Se devuelve en eleventa (<see cref="DeOtroTurno"/>) y llega aquí con la
    /// vuelta. Lo que todavía no llega a eleventa sí se devuelve aquí: va detrás de su venta.
    /// </summary>
    public static async Task<string?> DeOtroTurnoAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        if (!await db.EnviosAEleventa.AsNoTracking().AnyAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId
                && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null, ct))
            return null;
        var abierto = await (from v in db.Ventas.AsNoTracking()
                             join t in db.Turnos.AsNoTracking() on v.TurnoId equals t.Id
                             where v.Id == ventaId
                             select t.CerradoUtc == null).FirstOrDefaultAsync(ct);
        return abierto ? null : DeOtroTurno;
    }

    /// <summary>
    /// La devolución de un ticket de eleventa en la caja nueva: con lo que se acaba de leer en vivo, las reglas de la caja
    /// nueva (motivo, permiso y quién autoriza) y el turno abierto de esta caja. Guarda el ticket como se leyó, la salida de
    /// efectivo, la entrada al kardex de lo que regresa y el envío a eleventa, todo junto. Regresa lo guardado y la salida,
    /// para el comprobante.
    /// <para>
    /// Lo que se regresa es exacto, al centavo, como lo regresaría eleventa (revisión del 2026-10-17): eleventa cobró con
    /// centavos y dio el cambio con centavos, sin redondear. De cada artículo, si se devuelve todo lo que queda de él, sale
    /// exacto lo que le queda en eleventa (su <c>TOTAL_ARTICULO</c>, menos lo de aquí que todavía no llega); si es una parte,
    /// con la regla de la caja (2026-10-24): lo que le toca a lo devuelto hasta ahora con esto, en proporción a lo vendido,
    /// menos lo que de verdad ya salió (allá y aquí), sin pasarse de lo que le queda
    /// (<see cref="CalculoDevolucion.ParteDe(long, Cantidad, Cantidad, Cantidad, long)"/>). Antes cada parte se redondeaba por
    /// su lado: de tres por $20.00, devolver dos de una en una daba $13.34, uno más que juntas. Así el ticket de eleventa nunca
    /// queda abajo de cero, queda en cero al devolverlo todo, y lo que sale del cajón es lo que eleventa anota como devuelto.
    /// Redondear al peso, como en las ventas de la caja nueva (que se cobraron ya redondeadas), lo dejaría en −$0.50 o con un
    /// total sin artículos.
    /// </para>
    /// </summary>
    public static async Task<(DevolucionDeEleventa Devolucion, MovimientoCaja? Salida)> DevolverAsync(
        PosDbContext db, TicketLeido t, TurnoDelPuente puente, SolicitudDevolucion s, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (PorQueNoSeDevuelveAqui(t, puente, ahoraUtc) is { } porque)
            throw new InvalidOperationException(porque);
        var vendidoEn = t.VendidoEn!.Value;
        if (string.IsNullOrWhiteSpace(s.Motivo))
            throw new ArgumentException("La devolución lleva motivo.");
        if (s.Lineas.Count == 0 || s.Lineas.Any(l => l.CantidadMilesimas <= 0))
            throw new ArgumentException("Indica qué se devuelve y cuánto.");
        var caja = await db.Cajas.FindAsync([s.CajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        var turno = await new RegistroCaja(db).TurnoAbiertoAsync(s.CajaId, ct) ?? throw new InvalidOperationException("Abre un turno en esta caja para reembolsar.");

        var aqui = await DevueltoAquiAsync(db, t.Id, ct);
        var devueltos = new List<ArticuloDevueltoDeEleventa>();
        var variantes = await VariantesDeAsync(db, t.Articulos.Where(a => a.ProductoId is not null).Select(a => a.ProductoId!.Value), ct);
        foreach (var pedida in s.Lineas.GroupBy(l => l.LineaId))
        {
            var articulo = ArticuloDe(pedida.Key) is { } id ? t.Articulos.FirstOrDefault(a => a.Id == id) : null;
            if (articulo is null)
                throw new ArgumentException("Esa línea no es de este ticket.");
            var cantidad = pedida.Sum(l => l.CantidadMilesimas) / 1000m;
            var x = ParaDevolverDe(t, articulo, aqui.GetValueOrDefault(articulo.Id));
            if (cantidad > x.Queda + 0.0005m)
                throw new InvalidOperationException($"De «{articulo.Nombre}» solo quedan {x.Queda.ToString("0.###", CultureInfo.InvariantCulture)} por devolver.");
            // Como la ventana y la caja: lo acumulado con esto menos lo que ya salió, sin pasarse de lo que le queda. Todo lo que
            // queda completa la línea: justo lo que le queda en eleventa.
            var parte = cantidad >= x.Queda - 0.0005m ? x.Vendido - x.Devuelto : new Cantidad(pedida.Sum(l => l.CantidadMilesimas));
            var importe = CalculoDevolucion.ParteDe(x.Importe, x.Devuelto, parte, x.Vendido, x.YaSalio);
            devueltos.Add(new ArticuloDevueltoDeEleventa(articulo.Id, articulo.Codigo, articulo.Nombre, cantidad, importe,
                pedida.All(l => l.RegresaAlInventario) && articulo.ProductoId is not null,
                articulo.ProductoId is { } producto && variantes.TryGetValue(producto, out var variante) ? variante : null));
        }
        var total = devueltos.Sum(d => d.ImporteCentavos);
        // Exacto, sin redondear: lo que eleventa anota como devuelto es lo que sale del cajón.
        var reembolso = total;

        var control = new ServicioControl(db);
        await control.RequerirAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.Devolucion, total,
            $"Devolución del ticket #{t.Folio} de eleventa ({t.Caja})", s.AutorizacionId, s.Motivo, ct: ct);

        var devolucion = new DevolucionDeEleventa
        {
            SucursalId = caja.SucursalId, CajaId = caja.Id, TurnoId = turno.Id, UsuarioId = s.UsuarioId, Motivo = s.Motivo.Trim(), FechaUtc = ahoraUtc,
            AutorizoId = s.AutorizacionId is { } autorizacion ? (await db.Autorizaciones.FindAsync([autorizacion], ct))?.AutorizadorId : null,
            TicketEleventa = t.Id, Folio = t.Folio, CajaEleventa = t.CajaId, CajaNombre = Recortar(t.Caja, 50), TurnoTicketEleventa = t.TurnoId,
            VendidoUtc = HoraMexico.AUtc(vendidoEn), TicketTotalCentavos = Centavos(t.Pagado), Ticket = [.. t.Articulos], Articulos = devueltos,
            TotalCentavos = total, ReembolsoEfectivoCentavos = reembolso, RedondeoCentavos = 0,
        };
        MovimientoCaja? salida = null;
        if (reembolso > 0)
        {
            // El dinero sale del turno de la caja nueva como cualquier devolución; a eleventa no va como salida, va la devolución.
            salida = new MovimientoCaja
            {
                TurnoId = turno.Id, Tipo = TipoMovimientoCaja.Salida, MontoCentavos = reembolso, UsuarioId = s.UsuarioId, FechaUtc = ahoraUtc,
                Motivo = Recortar($"Devolución del ticket #{t.Folio} de eleventa ({t.Caja}): {s.Motivo.Trim()}", 500),
            };
            db.MovimientosCaja.Add(salida);
            devolucion.MovimientoId = salida.Id;
        }
        db.AperturasCajon.Add(new AperturaCajon
        {
            CajaId = caja.Id, TurnoId = turno.Id, UsuarioId = s.UsuarioId, Motivo = MotivoApertura.SinVenta, Nota = $"Devolución del ticket #{t.Folio} de eleventa",
            FechaUtc = ahoraUtc,
        });
        db.Set<DevolucionDeEleventa>().Add(devolucion);
        // Lo que regresa al inventario entra al kardex de aquí; mientras no llega a eleventa, la lectura lo sigue sumando.
        var inventario = new RegistroInventario(db);
        foreach (var d in devueltos.Where(d => d.RegresaAlInventario && d.VarianteId is not null))
        {
            var articulo = t.Articulos.First(a => a.Id == d.ArticuloEleventa);
            var cantidad = Cantidad.Unidades(Math.Round(d.Cantidad, 3));
            if (await db.Variantes.FindAsync([d.VarianteId!.Value], ct) is not { } variante || !variante.AdmiteCantidad(cantidad))
                continue;
            var costo = Math.Max(0, Centavos((articulo.Precio - articulo.GananciaPorPieza) * d.Cantidad));
            await inventario.AgregarAsync(Kardex.Entrada(caja.SucursalId, variante, TipoMovimiento.Devolucion, cantidad, costo, ahoraUtc,
                documentoTipo: nameof(DevolucionDeEleventa), documentoId: devolucion.Id), ct);
        }
        // A nombre de quien la hizo (2026-10-25), como cualquier devolución de esta caja.
        db.EnviosAEleventa.Add(new EnvioAEleventa
        {
            Tipo = TipoEnvioEleventa.DevolucionDeEleventa, DevolucionDeEleventaId = devolucion.Id, CreadoUtc = ahoraUtc,
            CajeroEleventa = await CajeroDeEleventa.DeAsync(db, devolucion.UsuarioId, ct),
        });
        return (devolucion, salida);
    }

    /// <summary>
    /// Lo devuelto aquí de cada artículo de ese ticket que todavía no está en eleventa (pendiente o por revisar): cuánto y por
    /// cuánto dinero.
    /// </summary>
    private static async Task<Dictionary<int, (decimal Cantidad, long Dinero)>> DevueltoAquiAsync(PosDbContext db, int ticket, CancellationToken ct)
    {
        var devoluciones = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => d.TicketEleventa == ticket).ToListAsync(ct);
        var noLlegan = await NoLleganAsync(db, devoluciones.Select(d => d.Id).ToList(), ct);
        return devoluciones.Where(d => noLlegan.Contains(d.Id)).SelectMany(d => d.Articulos)
            .GroupBy(a => a.ArticuloEleventa).ToDictionary(g => g.Key, g => (g.Sum(a => a.Cantidad), g.Sum(a => a.ImporteCentavos)));
    }

    /// <summary>De esas devoluciones de la ida, las que eleventa todavía no tiene: su envío sigue pendiente o quedó por revisar.</summary>
    /// <remarks>
    /// Después de restaurar un respaldo del POS (2026-10-23), una que ya había llegado puede regresar pendiente (como «ya se
    /// intentó»): aquí cuenta como que no ha llegado hasta que el puente la vuelva a mandar y la encuentre allá por su marca. Mientras,
    /// lo devuelto se retiene de más: del lado seguro, nunca se devuelve dos veces. Revisión del PR #136: se deja así, dicho.
    /// </remarks>
    private static async Task<HashSet<Guid>> NoLleganAsync(PosDbContext db, List<Guid> devoluciones, CancellationToken ct) =>
        (await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.DevolucionDeEleventaId != null && devoluciones.Contains(e.DevolucionDeEleventaId.Value)
                && (e.Estado == EstadoEnvioEleventa.Pendiente || e.Estado == EstadoEnvioEleventa.PorRevisar))
            .Select(e => e.DevolucionDeEleventaId!.Value).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// Un artículo de un ticket de eleventa para devolverlo aquí (la ida), como lo pide la regla de la caja
    /// (<see cref="CalculoDevolucion.ParteDe(long, Cantidad, Cantidad, Cantidad, long)"/>): lo vendido y su importe (lo que tiene
    /// allá más lo que eleventa ya devolvió), lo ya devuelto (allá, y aquí lo que todavía no llega) y lo que de verdad ya salió
    /// en dinero (lo que no le queda). Lo mismo para la ventana, que pide autorizar justo eso, y para devolver.
    /// </summary>
    /// <param name="Queda">Lo que queda por devolver, en su unidad: lo que tiene allá menos lo de aquí que todavía no llega.</param>
    private sealed record ArticuloParaDevolver(Cantidad Vendido, long Importe, Cantidad Devuelto, long YaSalio, decimal Queda);

    private static ArticuloParaDevolver ParaDevolverDe(TicketLeido t, ArticuloLeidoDeEleventa a, (decimal Cantidad, long Dinero) aqui)
    {
        var (allaCantidad, allaDinero) = DevueltoAlla(t, a);
        var importe = Centavos(a.Total + allaDinero);
        // Lo que le queda en eleventa, menos lo de aquí que todavía no llega: lo más que puede salir.
        var quedaDinero = Math.Max(0, Centavos(a.Total) - aqui.Dinero);
        return new ArticuloParaDevolver(new Cantidad(Milesimas(a.Cantidad + allaCantidad)), importe, new Cantidad(Milesimas(allaCantidad + aqui.Cantidad)),
            Math.Max(0, importe - quedaDinero), a.Cantidad - aqui.Cantidad);
    }

    /// <summary>Lo que eleventa ya devolvió de un artículo (por su código: va una vez por ticket).</summary>
    private static (decimal Cantidad, decimal Dinero) DevueltoAlla(TicketLeido t, ArticuloLeidoDeEleventa a)
    {
        var renglones = t.Devoluciones.SelectMany(d => d.Articulos).Where(x => string.Equals(x.Codigo, a.Codigo, StringComparison.OrdinalIgnoreCase)).ToList();
        return (renglones.Sum(x => x.Cantidad), renglones.Sum(x => x.Dinero));
    }

    // ── La vuelta ──

    /// <summary>Los tickets que mandó la caja 2 en los últimos días: de esos se buscan sus devoluciones en eleventa.</summary>
    public static async Task<List<int>> TicketsDeLaVueltaAsync(PosDbContext db, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var desde = ahoraUtc.AddDays(-DiasDeLaVuelta);
        // También los de más días con una devolución de aquí que espera su anotación a mano: lo que eleventa le devolvió se
        // sigue viendo (y reteniendo) hasta que se decida (cuarta revisión).
        var esperan = db.EnviosAEleventa.Where(e => (e.Tipo == TipoEnvioEleventa.Devolucion || e.Tipo == TipoEnvioEleventa.Cancelacion)
            && e.Estado == EstadoEnvioEleventa.PorRevisar && e.PorRevisarPorque == MotivoPorRevisar.AnotarAMano).Select(e => e.VentaId);
        return await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null
                && (e.EnviadaUtc >= desde || esperan.Contains(e.VentaId)))
            .Select(e => e.TicketEleventa!.Value).Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// Pasa a las ventas de la caja 2 lo que se devolvió o canceló en eleventa (la vuelta): lo que ya se pasó o es de aquí se
    /// salta; lo demás es una devolución sin efectivo con su entrada al kardex, o queda por revisar. Quien llama guarda, en
    /// la misma transacción en que se leyó (<see cref="PasarVueltaAsync"/>).
    /// </summary>
    public static async Task<ResultadoVuelta> AplicarVueltaAsync(PosDbContext db, IReadOnlyList<DevolucionLeida> devoluciones, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        int pasadas = 0, porRevisar = 0, retenidas = 0;
        if (devoluciones.Count == 0)
            return new ResultadoVuelta(0, 0);
        var tickets = devoluciones.Select(d => d.TicketId).Distinct().ToList();
        var ventas = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa != null && tickets.Contains(e.TicketEleventa.Value))
            .Select(e => new { Ticket = e.TicketEleventa!.Value, VentaId = e.VentaId!.Value, e.FolioEleventa }).ToListAsync(ct);
        var ids = devoluciones.Select(d => d.Id).ToList();
        var vistas = await db.Set<DevolucionDesdeEleventa>().AsNoTracking().Where(d => ids.Contains(d.DevolucionEleventa))
            .Select(d => new { d.DevolucionEleventa, d.DevueltaUtc }).ToListAsync(ct);
        var control = new ServicioControl(db);
        foreach (var d in devoluciones)
        {
            var devueltaUtc = HoraMexico.AUtc(d.DevueltoEn);
            if (vistas.Any(v => v.DevolucionEleventa == d.Id && v.DevueltaUtc == devueltaUtc)
                || db.ChangeTracker.Entries<DevolucionDesdeEleventa>().Any(e => e.Entity.DevolucionEleventa == d.Id && e.Entity.DevueltaUtc == devueltaUtc))
                continue;
            // Una venta repetida en la caja 2 (una restauración de eleventa) no se toca: la toma la que tenga ese ticket.
            if (ventas.Where(v => v.Ticket == d.TicketId).Select(v => v.VentaId).Distinct().ToList() is not [var ventaId])
                continue;
            var venta = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == ventaId, ct);
            // La de la caja 2: su marca es la hora en que se hizo aquí (TICKET_ID + DEVUELTO_EN). Si aquí no se supo que llegó
            // (se cortó al contestar, o se dejó por revisar mientras la bandeja la mandaba), queda como enviada: anotarla a
            // mano la dejaría dos veces en eleventa (cuarta revisión, 2026-10-17).
            var propias = await db.Devoluciones.AsNoTracking().Where(x => x.VentaId == venta.Id).Select(x => new { x.Id, x.FechaUtc }).ToListAsync(ct);
            if (propias.FirstOrDefault(f => Marca(f.FechaUtc) == d.DevueltoEn) is { } propia)
            {
                await DarPorEnviadaAsync(db, propia.Id, d.Id, ahoraUtc, ct);
                continue;
            }
            // Mientras una devolución de aquí de ese ticket espera su anotación a mano en eleventa, lo que eleventa le devolvió
            // después puede ser esa anotación: no se pasa ni se adivina (revisión del 2026-10-17, decidido). Lo dice quien la marca
            // «Ya lo anoté en eleventa», eligiendo cuál es; lo que no se elija se pasa después, como cualquier otra.
            if (await EsperanAnotacionAsync(db, venta.Id, ct) is { Count: > 0 } esperan
                && await VariantesDeAsync(db, ProductosDe(esperan, [d]), ct) is var variantes
                && esperan.Any(x => PuedeSer(x, d, devueltaUtc, variantes)))
            {
                retenidas++;
                continue;
            }

            var registro = new DevolucionDesdeEleventa
            {
                DevolucionEleventa = d.Id, TicketEleventa = d.TicketId, DevueltaUtc = devueltaUtc, EsCancelacion = d.EsCancelacion, TotalCentavos = Centavos(d.Total),
                Cajero = Recortar(d.Cajero, 50), Caja = Recortar(d.Caja, 50), VentaId = venta.Id, VistaUtc = ahoraUtc,
                Articulos = d.Articulos.Select(a => new ArticuloDevueltoEnEleventa(a.Codigo, a.Nombre, a.Cantidad, a.Dinero, a.ProductoId)).ToList(),
            };
            db.Set<DevolucionDesdeEleventa>().Add(registro);
            if (await PasarALaVentaAsync(db, venta, registro, d, ventas.First(v => v.Ticket == d.TicketId).FolioEleventa, ct))
                pasadas++;
            else
                porRevisar++;
        }
        return new ResultadoVuelta(pasadas, porRevisar, retenidas);
    }

    /// <summary>
    /// Pasa una devolución de eleventa a su venta de aquí: una devolución sin efectivo (salió de la caja de eleventa), con su
    /// entrada al kardex, a nombre del cajero de la venta y en la bitácora. Si aquí ya no queda tanto, queda por revisar
    /// (falso). Quien llama guarda.
    /// </summary>
    private static async Task<bool> PasarALaVentaAsync(PosDbContext db, Venta venta, DevolucionDesdeEleventa registro, DevolucionLeida d, int? folioEleventa,
        CancellationToken ct)
    {
        var devolucion = await ArmarDeEleventaAsync(db, venta, d, registro.DevueltaUtc, ct);
        if (devolucion is null)
        {
            registro.PorRevisar = NoAlcanza;
            return false;
        }
        // Lo que regresa al inventario: al mismo costo con que salió, como una devolución de aquí.
        var inventario = new RegistroInventario(db);
        foreach (var linea in devolucion.Lineas)
        {
            var vendida = venta.Lineas.Single(l => l.Id == linea.VentaLineaId);
            if (!vendida.LlevaInventario || vendida.VarianteId is not { } varianteId || await db.Variantes.FindAsync([varianteId], ct) is not { } variante)
                continue;
            var cantidadBase = new Cantidad(Redondeo.Dividir((Int128)vendida.CantidadBase.Milesimas * linea.Cantidad.Milesimas, vendida.Cantidad.Milesimas));
            await inventario.AgregarAsync(Kardex.Entrada(venta.SucursalId, variante, TipoMovimiento.Devolucion, cantidadBase, linea.CostoCentavos, registro.DevueltaUtc,
                documentoTipo: nameof(Devolucion), documentoId: devolucion.Id), ct);
        }
        // Las impresiones vuelven a estar por cobrar si se canceló o se devolvió completa su línea; una parte (eleventa sí la deja)
        // fue lo que se le regresó al cliente, que se quedó con ellas (2026-10-25).
        if (CalculoDevolucion.LiberaImpresiones(venta, devolucion))
            await ImpresionesDelPos.EnLaVenta(db).LiberarAsync(venta.Id, ct);
        CalculoDevolucion.Aplicar(venta, devolucion);
        db.Devoluciones.Add(devolucion);
        registro.DevolucionId = devolucion.Id;
        // En la bitácora, lo que se hizo en eleventa (una cancelación lo es aunque aquí sea la devolución de lo que quedaba).
        new ServicioControl(db).Registrar(venta.SucursalId, venta.CajaId, null, d.EsCancelacion ? AccionControlada.CancelarTicket : AccionControlada.Devolucion,
            $"{(d.EsCancelacion ? "Cancelación" : "Devolución")} hecha en eleventa del ticket {venta.FolioTexto} (#{folioEleventa}, {d.Caja}, {d.Cajero})"
            + (d.EsCancelacion && !devolucion.EsCancelacion ? ", que ya tenía devoluciones: aquí es la devolución de lo que quedaba;" : ":")
            + " no salió de esta caja", devolucion.TotalCentavos, venta.Id);
        return true;
    }

    /// <summary>
    /// La vuelta, en una sola transacción, y guardada: se lee la venta, se le pasa lo de eleventa y se guarda sin que otra
    /// devolución de esa venta se cuele en medio y se pierda (una de esta caja que no esperó el candado de la bandeja; revisión
    /// del 2026-10-17). Con SQLite la transacción aparta la escritura desde el principio: la otra espera a que termine, y
    /// entonces ve lo de aquí (y si no alcanza a esperar, no entra: el cajero la vuelve a buscar). Lo que quien llama ya
    /// tenía leído (la venta que se buscó por su folio) se suelta: pudo cambiar antes de la transacción, y con la copia vieja
    /// se pisaría lo que cambió. Quien llama lo vuelve a leer.
    /// </summary>
    public static async Task<ResultadoVuelta> PasarVueltaAsync(PosDbContext db, IReadOnlyList<DevolucionLeida> devoluciones, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        if (devoluciones.Count == 0)
            return new ResultadoVuelta(0, 0);
        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("La vuelta de eleventa se pasa sin nada a medio guardar.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var resultado = await AplicarVueltaAsync(db, devoluciones, ahoraUtc, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return resultado;
    }

    // ── Lo que se anota a mano en eleventa ──

    /// <summary>
    /// Las devoluciones de aquí de esa venta que esperan su anotación a mano en eleventa, con la hora en que se hicieron: las
    /// que quedaron por revisar para anotarlas a mano (de un turno de eleventa que se cortó antes de mandarlas, perdidas al
    /// restaurar, dejadas por revisar a mano). No las que chocaron porque eleventa ya lo tenía: eso son dos reembolsos, y los
    /// dos quedan a la vista.
    /// </summary>
    private static async Task<List<EsperaAnotacion>> EsperanAnotacionAsync(PosDbContext db, Guid ventaId, CancellationToken ct)
    {
        var esperan = await (from e in db.EnviosAEleventa.AsNoTracking()
                             join x in db.Devoluciones.AsNoTracking() on e.DevolucionId equals x.Id
                             where e.VentaId == ventaId && (e.Tipo == TipoEnvioEleventa.Devolucion || e.Tipo == TipoEnvioEleventa.Cancelacion)
                                 && e.Estado == EstadoEnvioEleventa.PorRevisar && e.PorRevisarPorque == MotivoPorRevisar.AnotarAMano
                             select new { x.Id, x.FechaUtc }).ToListAsync(ct);
        var lista = new List<EsperaAnotacion>();
        foreach (var x in esperan)
            lista.Add(new EsperaAnotacion(x.Id, x.FechaUtc, (await VentasParaEleventa.ArmarDevolucionAsync(db, x.Id, ct)).Devolucion));
        return lista;
    }

    /// <summary>Una devolución de aquí que espera su anotación a mano: cuándo se hizo y lo que se habría mandado (vacío si no se puede armar).</summary>
    private sealed record EsperaAnotacion(Guid DevolucionId, DateTime HechaUtc, DevolucionParaEleventa? Armada);

    /// <summary>
    /// Si una devolución de eleventa puede ser la anotación a mano de una de aquí: es de después (con unos minutos por la hora
    /// de cada PC) y tienen algo en común (algún artículo, o una de las dos es la cancelación del ticket). Lo que no tiene
    /// nada en común no espera: se pasa a la venta como cualquier otra (cuarta revisión, 2026-10-17).
    /// </summary>
    private static bool PuedeSer(EsperaAnotacion aqui, DevolucionLeida alla, DateTime devueltaUtc, IReadOnlyDictionary<int, Guid> variantes) =>
        devueltaUtc >= aqui.HechaUtc - HolguraDeHora
        && (aqui.Armada is not { } armada || armada.EsCancelacion || alla.EsCancelacion
            || armada.Articulos.Select(a => Clave(a.ProductoId, a.Nombre, variantes))
                .Intersect(alla.Articulos.Select(a => Clave(a.ProductoId, a.Nombre, variantes))).Any());

    private static IEnumerable<int> ProductosDe(IEnumerable<EsperaAnotacion> aqui, IEnumerable<DevolucionLeida> alla) =>
        aqui.SelectMany(x => x.Armada?.Articulos.Select(a => a.ProductoId) ?? []).Concat(alla.SelectMany(d => d.Articulos.Select(a => a.ProductoId)))
            .Where(p => p is not null).Select(p => p!.Value).Distinct();

    /// <summary>
    /// De las devoluciones de eleventa que trajo la lectura, las que la vuelta retiene porque pueden ser la anotación a mano de
    /// una de aquí (<see cref="PuedeSer"/>), por ticket: cuáles de aquí las esperan y lo que devolvieron allá, por variante.
    /// La lectura no suma otra vez lo de aquí que ya está allá así (si es su anotación, se contaría dos veces), y sigue a
    /// eleventa en todo lo demás mientras se decide (revisiones del 2026-10-17).
    /// </summary>
    public static async Task<List<AnotacionRetenida>> RetenidasAsync(PosDbContext db, IReadOnlyList<DevolucionLeida> devoluciones,
        CancellationToken ct = default)
    {
        var resultado = new List<AnotacionRetenida>();
        if (devoluciones.Count == 0)
            return resultado;
        var tickets = devoluciones.Select(d => d.TicketId).Distinct().ToList();
        var ventas = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa != null && tickets.Contains(e.TicketEleventa.Value))
            .Select(e => new { Ticket = e.TicketEleventa!.Value, VentaId = e.VentaId!.Value }).ToListAsync(ct);
        var ids = devoluciones.Select(d => d.Id).ToList();
        var vistas = await db.Set<DevolucionDesdeEleventa>().AsNoTracking().Where(d => ids.Contains(d.DevolucionEleventa))
            .Select(d => new { d.DevolucionEleventa, d.DevueltaUtc }).ToListAsync(ct);
        foreach (var venta in ventas.GroupBy(v => v.Ticket).Where(g => g.Select(v => v.VentaId).Distinct().Count() == 1).Select(g => g.First()))
        {
            var esperan = await EsperanAnotacionAsync(db, venta.VentaId, ct);
            if (esperan.Count == 0)
                continue;
            var propias = (await db.Devoluciones.AsNoTracking().Where(x => x.VentaId == venta.VentaId).Select(x => x.FechaUtc).ToListAsync(ct)).Select(Marca).ToHashSet();
            var delTicket = devoluciones.Where(d => d.TicketId == venta.Ticket && !propias.Contains(d.DevueltoEn)
                && !vistas.Any(v => v.DevolucionEleventa == d.Id && v.DevueltaUtc == HoraMexico.AUtc(d.DevueltoEn))).ToList();
            var variantes = await VariantesDeAsync(db, ProductosDe(esperan, delTicket), ct);
            var (esperanAlgo, retenido, todo) = (new HashSet<Guid>(), new Dictionary<Guid, Cantidad>(), false);
            foreach (var d in delTicket)
            {
                var puedeSer = esperan.Where(x => PuedeSer(x, d, HoraMexico.AUtc(d.DevueltoEn), variantes)).ToList();
                if (puedeSer.Count == 0)
                    continue;
                esperanAlgo.UnionWith(puedeSer.Select(x => x.DevolucionId));
                // La cancelación se lleva todo lo que quedaba en el ticket.
                todo |= d.EsCancelacion;
                foreach (var a in d.Articulos.Where(a => a.ProductoId is { } p && variantes.ContainsKey(p)))
                {
                    var variante = variantes[a.ProductoId!.Value];
                    retenido[variante] = retenido.GetValueOrDefault(variante) + new Cantidad((long)Math.Round(a.Cantidad * 1000));
                }
            }
            if (esperanAlgo.Count > 0)
                resultado.Add(new AnotacionRetenida(esperanAlgo, retenido, todo));
        }
        return resultado;
    }

    /// <summary>
    /// Si «Ya lo anoté en eleventa» de ese envío pide elegir su anotación: es una devolución o cancelación de aquí de una venta
    /// que está en eleventa, y quedó por revisar para anotarla a mano (<see cref="EsperanAnotacionAsync"/>). Vacío si no: se
    /// marca como siempre.
    /// </summary>
    public static async Task<PorAnotarEnEleventa?> PorAnotarAsync(PosDbContext db, EnvioAEleventa envio, CancellationToken ct = default)
    {
        if (envio is not
            {
                Tipo: TipoEnvioEleventa.Devolucion or TipoEnvioEleventa.Cancelacion, DevolucionId: { } devolucionId, VentaId: { } ventaId,
                Estado: EstadoEnvioEleventa.PorRevisar, PorRevisarPorque: MotivoPorRevisar.AnotarAMano,
            })
            return null;
        if (await db.EnviosAEleventa.AsNoTracking()
                .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null)
                .Select(e => e.TicketEleventa).FirstOrDefaultAsync(ct) is not { } ticket
            || (await VentasParaEleventa.ArmarDevolucionAsync(db, devolucionId, ct)).Devolucion is not { } armada)
            return null;
        var hecha = await db.Devoluciones.AsNoTracking().Where(x => x.Id == devolucionId).Select(x => x.FechaUtc).SingleAsync(ct);
        return new PorAnotarEnEleventa(envio, ventaId, ticket, hecha, armada, await DevolvioLoQueQuedabaAsync(db, ventaId, devolucionId, ct));
    }

    /// <summary>
    /// Si esa devolución de aquí (no la cancelación) devolvió todo lo que le quedaba a la venta en ese momento: cada línea, lo
    /// vendido menos lo de las devoluciones de antes. Es lo que el puente escribe en eleventa como la cancelación del ticket
    /// (<see cref="EnvioEleventa.DevolverAsync"/>, 2026-10-24).
    /// </summary>
    private static async Task<bool> DevolvioLoQueQuedabaAsync(PosDbContext db, Guid ventaId, Guid devolucionId, CancellationToken ct)
    {
        var devoluciones = await db.Devoluciones.AsNoTracking().Include(d => d.Lineas).Where(d => d.VentaId == ventaId).ToListAsync(ct);
        if (devoluciones.SingleOrDefault(d => d.Id == devolucionId) is not { EsCancelacion: false } esta)
            return false;
        var antes = devoluciones.Where(d => d.Id != esta.Id && (d.FechaUtc, d.Id).CompareTo((esta.FechaUtc, esta.Id)) < 0)
            .SelectMany(d => d.Lineas).GroupBy(l => l.VentaLineaId).ToDictionary(g => g.Key, g => g.Sum(l => l.Cantidad.Milesimas));
        var devuelve = esta.Lineas.GroupBy(l => l.VentaLineaId).ToDictionary(g => g.Key, g => g.Sum(l => l.Cantidad.Milesimas));
        var lineas = await db.Set<VentaLinea>().AsNoTracking().Where(l => l.VentaId == ventaId).ToListAsync(ct);
        var quedaban = lineas.Select(l => (l.Id, Queda: l.Cantidad.Milesimas - antes.GetValueOrDefault(l.Id))).Where(l => l.Queda > 0).ToList();
        return quedaban.Count > 0 && quedaban.All(l => devuelve.GetValueOrDefault(l.Id) >= l.Queda);
    }

    /// <summary>Lo que se leyó en vivo de un ticket para elegir la anotación de una devolución de aquí.</summary>
    /// <param name="Candidatas">Las que pueden ser su anotación (<see cref="AnotacionesEnVivoAsync"/>).</param>
    /// <param name="Suya">
    /// La que escribió la propia caja 2 con la marca de esa devolución: llegó sola aunque aquí no se supo, y no hay nada que
    /// anotar (quien llama la da por enviada).
    /// </param>
    public sealed record AnotacionesLeidas(List<DevolucionLeida> Candidatas, DevolucionLeida? Suya);

    /// <summary>
    /// Las devoluciones y cancelaciones de eleventa de ese ticket que pueden ser la anotación a mano de la de aquí, leídas en
    /// vivo (hasta 3 segundos): de después de que se hizo aquí (con unos minutos por la hora de cada PC), que no escribió la
    /// caja 2 (su marca) y que no se han pasado a la venta ni ligado con otra. Si una tiene la marca de la de aquí, es que
    /// llegó sola (<see cref="AnotacionesLeidas.Suya"/>). Vacío si eleventa no contestó.
    /// </summary>
    public static async Task<AnotacionesLeidas?> AnotacionesEnVivoAsync(PosDbContext db, ServidorEleventa servidor, PorAnotarEnEleventa porAnotar,
        CancellationToken ct = default)
    {
        if (await DevolucionesEnVivoAsync(servidor, porAnotar.TicketEleventa, ct) is not { } leidas)
            return null;
        var propias = (await db.Devoluciones.AsNoTracking().Where(x => x.VentaId == porAnotar.VentaId).Select(x => x.FechaUtc).ToListAsync(ct))
            .Select(Marca).ToHashSet();
        var ids = leidas.Select(d => d.Id).ToList();
        var vistas = await db.Set<DevolucionDesdeEleventa>().AsNoTracking().Where(d => ids.Contains(d.DevolucionEleventa))
            .Select(d => new { d.DevolucionEleventa, d.DevueltaUtc }).ToListAsync(ct);
        var candidatas = leidas
            .Where(d => HoraMexico.AUtc(d.DevueltoEn) >= porAnotar.HechaUtc - HolguraDeHora && !propias.Contains(d.DevueltoEn)
                && !vistas.Any(v => v.DevolucionEleventa == d.Id && v.DevueltaUtc == HoraMexico.AUtc(d.DevueltoEn)))
            .OrderBy(d => d.DevueltoEn)
            .ToList();
        return new AnotacionesLeidas(candidatas, leidas.FirstOrDefault(d => d.DevueltoEn == Marca(porAnotar.HechaUtc)));
    }

    /// <summary>
    /// Una devolución de aquí que sí llegó a eleventa (allá está con su marca) aunque aquí no se supo: si su envío sigue
    /// pendiente o por revisar, queda como enviada, con su número de eleventa. Quien llama guarda.
    /// </summary>
    public static async Task<bool> DarPorEnviadaAsync(PosDbContext db, Guid devolucionId, int devolucionEleventa, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (await db.EnviosAEleventa.FirstOrDefaultAsync(e => e.DevolucionId == devolucionId
                && (e.Tipo == TipoEnvioEleventa.Devolucion || e.Tipo == TipoEnvioEleventa.Cancelacion)
                && (e.Estado == EstadoEnvioEleventa.Pendiente || e.Estado == EstadoEnvioEleventa.PorRevisar), ct) is not { } envio)
            return false;
        (envio.Estado, envio.EnviadaUtc, envio.IdEleventa, envio.Error) =
            (EstadoEnvioEleventa.Enviada, ahoraUtc, devolucionEleventa, LlegoSola);
        return true;
    }

    /// <summary>Lo que se anota de una devolución de aquí que llegó a eleventa sin que aquí se supiera.</summary>
    public const string LlegoSola =
        "Llegó a eleventa aunque aquí no se supo (allá está con su marca): no hay nada que anotar a mano.";

    /// <summary>
    /// Por qué esas devoluciones de eleventa no son, juntas, la anotación a mano de la de aquí; vacío si lo son: las dos son la
    /// cancelación del ticket, o devuelven lo mismo de los mismos artículos (por su producto de eleventa o, si fue común, por
    /// su nombre). El dinero no cuenta: allá pudo salir con otro precio. Así nada se pierde ni se cuenta dos veces: lo que
    /// sobre o falte en eleventa no se esconde en la liga.
    /// </summary>
    /// <param name="loQueQuedaba">
    /// La de aquí devolvió todo lo que le quedaba a la venta (<see cref="PorAnotarEnEleventa.LoQueQuedaba"/>): también puede ser
    /// la cancelación del ticket, que es como la escribe el puente y la anota la tienda (revisión del PR #139).
    /// </param>
    public static string? PorQueNoEs(DevolucionParaEleventa aqui, IReadOnlyList<DevolucionLeida> alla, IReadOnlyDictionary<int, Guid> variantes,
        bool loQueQuedaba = false)
    {
        if (alla.Count == 0)
            return DiCualEs;
        if (aqui.EsCancelacion)
            return alla is [{ EsCancelacion: true }] ? null : "La de aquí es la cancelación del ticket: su anotación en eleventa es una cancelación.";
        if (loQueQuedaba && alla is [{ EsCancelacion: true }])
            return null;
        if (alla.Any(d => d.EsCancelacion))
            return loQueQuedaba
                ? "La de aquí devolvió todo lo que le quedaba al ticket: su anotación en eleventa es una cancelación, sola, o devoluciones de lo mismo."
                : "La de aquí es una devolución de algunos artículos: su anotación en eleventa no es una cancelación.";
        var deAqui = aqui.Articulos.GroupBy(a => Clave(a.ProductoId, a.Nombre, variantes)).ToDictionary(g => g.Key, g => g.Sum(a => a.Cantidad));
        var deAlla = alla.SelectMany(d => d.Articulos).GroupBy(a => Clave(a.ProductoId, a.Nombre, variantes)).ToDictionary(g => g.Key, g => g.Sum(a => a.Cantidad));
        return deAqui.Count == deAlla.Count && deAqui.All(a => deAlla.TryGetValue(a.Key, out var cantidad) && Math.Abs(cantidad - a.Value) < 0.0005m)
            ? null
            : $"No es lo mismo: aquí se devolvió {Lo(aqui.Articulos.Select(a => (a.Nombre, a.Cantidad)))}; en eleventa, "
              + $"{Lo(alla.SelectMany(d => d.Articulos).Select(a => (a.Nombre, a.Cantidad)))}. Elige las que, juntas, sean lo mismo.";
    }

    /// <summary>Lo mismo, con las variantes de aquí de sus productos (las de la importación o la lectura).</summary>
    public static async Task<string?> PorQueNoEsAsync(PosDbContext db, PorAnotarEnEleventa porAnotar, IReadOnlyList<DevolucionLeida> alla,
        CancellationToken ct = default) =>
        PorQueNoEs(porAnotar.Armada, alla, await VariantesDeAsync(db,
            porAnotar.Armada.Articulos.Select(a => a.ProductoId).Concat(alla.SelectMany(d => d.Articulos.Select(a => a.ProductoId)))
                .Where(p => p is not null).Select(p => p!.Value), ct), porAnotar.LoQueQuedaba);

    /// <summary>
    /// «Ya lo anoté en eleventa» de una devolución de aquí, con las de eleventa que se eligieron como su anotación: se ligan a
    /// ella (no se pasan a la venta: ya está devuelto aquí y su dinero salió de esta caja). Quien llama revisó que son
    /// (<see cref="PorQueNoEs"/>), marca el envío y guarda.
    /// </summary>
    public static void Ligar(PosDbContext db, PorAnotarEnEleventa porAnotar, IReadOnlyList<DevolucionLeida> elegidas, DateTime ahoraUtc)
    {
        foreach (var d in elegidas)
            db.Set<DevolucionDesdeEleventa>().Add(new DevolucionDesdeEleventa
            {
                DevolucionEleventa = d.Id, TicketEleventa = d.TicketId, DevueltaUtc = HoraMexico.AUtc(d.DevueltoEn), EsCancelacion = d.EsCancelacion,
                TotalCentavos = Centavos(d.Total), Cajero = Recortar(d.Cajero, 50), Caja = Recortar(d.Caja, 50), VentaId = porAnotar.VentaId,
                DevolucionId = porAnotar.Envio.DevolucionId, Propia = true, VistaUtc = ahoraUtc,
                Articulos = d.Articulos.Select(a => new ArticuloDevueltoEnEleventa(a.Codigo, a.Nombre, a.Cantidad, a.Dinero, a.ProductoId)).ToList(),
            });
    }

    /// <summary>Cómo se ve, para elegirla, una devolución de eleventa que puede ser la anotación a mano de la de aquí.</summary>
    public static AnotacionEnEleventaDto ComoAnotacion(DevolucionLeida d) =>
        new(d.Id, d.DevueltoEn, d.EsCancelacion, d.Caja, d.Cajero, Centavos(d.Total),
            d.EsCancelacion ? "Cancelación del ticket" : Lo(d.Articulos.Select(a => (a.Nombre, a.Cantidad))));

    /// <summary>Lo devuelto, para la tienda: «1 Pluma punto fino, 2 Lápiz».</summary>
    private static string Lo(IEnumerable<(string Nombre, decimal Cantidad)> articulos) =>
        string.Join(", ", articulos.GroupBy(a => a.Nombre.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Sum(a => a.Cantidad).ToString("0.###", CultureInfo.InvariantCulture)} {g.Key}"));

    /// <summary>
    /// Un artículo: por su variante de aquí (la de su producto de eleventa; así dos productos de eleventa con el mismo código,
    /// que aquí son uno, son el mismo), por su producto de eleventa si aquí no se conoce, o, si fue común, por su nombre.
    /// </summary>
    private static string Clave(int? producto, string nombre, IReadOnlyDictionary<int, Guid> variantes) =>
        producto is { } p ? variantes.TryGetValue(p, out var variante) ? $"v{variante:N}" : $"#{p}" : nombre.Trim().ToUpperInvariant();

    /// <summary>
    /// La devolución de la caja nueva que corresponde a una de eleventa: la cancelación, todo lo que queda; la parcial, cada
    /// artículo de eleventa en las líneas de su producto (o, si fue común, en las de su nombre), con lo que le toca por la regla
    /// de la caja (<see cref="CalculoDevolucion.ParteDe(long, Cantidad, Cantidad, Cantidad)"/>, 2026-10-24). Sin efectivo
    /// (salió de la caja de eleventa) y del turno de la venta. Vacía si aquí ya no queda tanto.
    /// <para>
    /// Si el ticket ya tenía devoluciones, eleventa cancela lo que quedó en él; aquí eso es la devolución de lo que quedaba, y la
    /// venta sigue cobrada (revisión del PR #131, 2026-10-22). Aquí no se cancela un ticket con devoluciones
    /// (<see cref="CalculoDevolucion.ArmarCancelacion"/>): el corte del día y los reportes dejarían de contar el ticket y
    /// seguirían restando lo que ya se había devuelto, dos veces.
    /// </para>
    /// </summary>
    private static async Task<Devolucion?> ArmarDeEleventaAsync(PosDbContext db, Venta venta, DevolucionLeida d, DateTime devueltaUtc, CancellationToken ct)
    {
        if (venta.Estado != EstadoVenta.Pagada)
            return null;
        var loQueQuedaba = d.EsCancelacion && LoQueQuedaba(venta);
        var devolucion = new Devolucion
        {
            VentaId = venta.Id, SucursalId = venta.SucursalId, CajaId = venta.CajaId, TurnoId = venta.TurnoId, UsuarioId = venta.CajeroId,
            EsCancelacion = d.EsCancelacion && !loQueQuedaba, FechaUtc = devueltaUtc,
            Motivo = Recortar(loQueQuedaba
                ? $"Cancelada en eleventa ({d.Caja}, {d.Cajero}, {d.DevueltoEn:dd/MM HH:mm}) después de otra devolución: lo que quedaba; no salió de esta caja"
                : $"Hecha en eleventa ({d.Caja}, {d.Cajero}, {d.DevueltoEn:dd/MM HH:mm}): no salió de esta caja", 200),
        };
        // En unidades base (las de eleventa) por línea de la venta.
        var porLinea = new Dictionary<Guid, long>();
        long QuedaBase(VentaLinea l) =>
            Redondeo.Dividir((Int128)l.CantidadBase.Milesimas * l.CantidadPorDevolver.Milesimas, l.Cantidad.Milesimas) - porLinea.GetValueOrDefault(l.Id);
        if (d.EsCancelacion)
        {
            foreach (var linea in venta.Lineas.Where(l => l.CantidadPorDevolver.EsPositiva))
                porLinea[linea.Id] = QuedaBase(linea);
            if (porLinea.Count == 0)
                return null;
        }
        else
        {
            var productos = d.Articulos.Where(a => a.ProductoId is not null).Select(a => a.ProductoId!.Value).ToList();
            var variantes = await VariantesDeAsync(db, productos, ct);
            foreach (var articulo in d.Articulos)
            {
                var deEsteProducto = articulo.ProductoId is { } producto && variantes.TryGetValue(producto, out var variante)
                    ? venta.Lineas.Where(l => l.VarianteId == variante).ToList()
                    : [];
                if (deEsteProducto.Count == 0)
                    deEsteProducto = venta.Lineas.Where(l => string.Equals(l.Descripcion.Trim(), articulo.Nombre, StringComparison.OrdinalIgnoreCase)).ToList();
                var falta = Milesimas(articulo.Cantidad);
                foreach (var linea in deEsteProducto)
                {
                    var toma = Math.Min(falta, QuedaBase(linea));
                    if (toma <= 0)
                        continue;
                    porLinea[linea.Id] = porLinea.GetValueOrDefault(linea.Id) + toma;
                    falta -= toma;
                }
                if (falta > 0)
                    return null;
            }
            if (porLinea.Count == 0)
                return null;
        }
        foreach (var (lineaId, baseMilesimas) in porLinea.Where(p => p.Value > 0))
        {
            var linea = venta.Lineas.Single(l => l.Id == lineaId);
            var cantidad = baseMilesimas >= Redondeo.Dividir((Int128)linea.CantidadBase.Milesimas * linea.CantidadPorDevolver.Milesimas, linea.Cantidad.Milesimas)
                ? linea.CantidadPorDevolver
                : new Cantidad(Redondeo.Dividir((Int128)baseMilesimas * linea.Cantidad.Milesimas, linea.CantidadBase.Milesimas));
            // Con la regla de la caja (2026-10-24): lo acumulado redondeado menos lo de lo ya devuelto, así varias parciales de una
            // línea suman su importe. Antes cada una se redondeaba por su lado y tres de tres podían sumar un centavo de más.
            devolucion.Lineas.Add(new DevolucionLinea
            {
                DevolucionId = devolucion.Id, VentaLineaId = linea.Id, Cantidad = cantidad,
                ImporteCentavos = CalculoDevolucion.ParteDe(linea.ImporteCentavos, linea.CantidadDevuelta, cantidad, linea.Cantidad),
                CostoCentavos = CalculoDevolucion.ParteDe(linea.CostoCentavos, linea.CantidadDevuelta, cantidad, linea.Cantidad),
            });
        }
        devolucion.TotalCentavos = devolucion.Lineas.Sum(l => l.ImporteCentavos);
        return devolucion;
    }

    /// <summary>Si una cancelación de eleventa de esa venta es aquí la devolución de lo que quedaba: la venta ya tiene devoluciones.</summary>
    private static bool LoQueQuedaba(Venta venta) => venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva);

    // ── Antes de devolver aquí un ticket de la caja 2 ──

    /// <summary>Lo que se vio en vivo de una venta de la caja 2 que está en eleventa.</summary>
    /// <param name="DelTurno">
    /// Si su ticket es del turno que eleventa tiene abierto ahora en la caja 2: el único donde el puente sabe escribir su
    /// devolución. Si no se preguntó por la caja, sí.
    /// </param>
    public sealed record VistaEnVivo(bool DelTurno);

    /// <summary>
    /// Antes de devolver o cancelar aquí una venta de la caja 2 que ya está en eleventa: lo que se le haya devuelto allá
    /// entra antes, como en la vuelta, y, con la caja del puente, se ve si su ticket es del turno que eleventa tiene abierto
    /// ahora (cuarta revisión: así no se acepta aquí lo que ya es de otro turno, ni se rechaza lo que se cobró después del
    /// corte de eleventa aunque la lectura no lo haya visto). Vacío si la venta no está en eleventa o eleventa no contesta a
    /// tiempo: se deja pasar (decisión del dueño, 2026-10-11) y un choque queda por revisar al mandarla.
    /// </summary>
    /// <param name="cajaDelPuente"><c>CAJAS.ID</c> de la caja 2 en eleventa; vacío si no hace falta ver el turno.</param>
    public static async Task<VistaEnVivo?> RevisarEnVivoAsync(PosDbContext db, ServidorEleventa servidor, Guid ventaId, DateTime ahoraUtc,
        int? cajaDelPuente = null, CancellationToken ct = default)
    {
        var ticket = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null)
            .Select(e => e.TicketEleventa).FirstOrDefaultAsync(ct);
        if (ticket is not { } id)
            return null;
        var leido = await EnVivoAsync(servidor, async c =>
        {
            await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
            }, FirebirdSinCancelar.Nunca);
            var devoluciones = await TicketsDeEleventa.DevolucionesAsync(c, tx, [id], FirebirdSinCancelar.Nunca);
            var delTicket = await TicketsDeEleventa.TurnoDelTicketAsync(c, tx, id, FirebirdSinCancelar.Nunca);
            await tx.CommitAsync(FirebirdSinCancelar.Nunca);
            var abierto = cajaDelPuente is { } caja ? await TicketsDeEleventa.TurnoAbiertoAsync(c, caja, FirebirdSinCancelar.Nunca) : null;
            return new LeidoDeLaVenta(devoluciones, delTicket, abierto);
        }, ct);
        if (leido is null)
            return null;
        await PasarVueltaAsync(db, leido.Devoluciones, ahoraUtc, ct);
        return new VistaEnVivo(cajaDelPuente is null || leido.TurnoDelTicket is { } turno && turno == leido.TurnoAbierto);
    }

    private sealed record LeidoDeLaVenta(List<DevolucionLeida> Devoluciones, int? TurnoDelTicket, int? TurnoAbierto);

    /// <summary>
    /// Por qué no se devuelve aquí lo que queda de una venta de la caja 2: ya tuvo una devolución en eleventa (pasada a su
    /// venta, o por revisar).
    /// </summary>
    public static async Task<string?> PorQueNoSeDevuelveAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default) =>
        // La de aquí anotada a mano allá no cuenta: su dinero salió de esta caja.
        await db.Set<DevolucionDesdeEleventa>().AsNoTracking().AnyAsync(d => d.VentaId == ventaId && !d.Propia, ct) ? YaTuvoDevolucionEnEleventa : null;

    // ── Ayudas ──

    /// <summary>Las devoluciones y cancelaciones de un ticket de eleventa, en vivo; vacío si eleventa no contestó a tiempo.</summary>
    private static async Task<List<DevolucionLeida>?> DevolucionesEnVivoAsync(ServidorEleventa servidor, int ticket, CancellationToken ct) =>
        await EnVivoAsync(servidor, async c =>
        {
            await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
            }, FirebirdSinCancelar.Nunca);
            var leidas = await TicketsDeEleventa.DevolucionesAsync(c, tx, [ticket], FirebirdSinCancelar.Nunca);
            await tx.CommitAsync(FirebirdSinCancelar.Nunca);
            return leidas;
        }, ct);

    /// <summary>
    /// Lo que se lee en vivo, con un límite: si eleventa no contesta a tiempo (o la red falla), vacío. Lo que no contestó se
    /// deja de esperar, sin cancelarlo (<see cref="FirebirdSinCancelar"/>), y con su conexión: cerrarla aquí esperaría a
    /// eleventa sin fin, con el candado de la bandeja tomado (revisión del 2026-10-17, segunda vuelta).
    /// </summary>
    private static async Task<T?> EnVivoAsync<T>(ServidorEleventa servidor, Func<FbConnection, Task<T>> leer, CancellationToken ct) where T : class
    {
        try
        {
            return await LeerAsync().WaitAsync(EsperaEnVivo, ct);
        }
        catch (Exception ex) when (ex is FbException or IOException or System.Net.Sockets.SocketException or TimeoutException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }

        async Task<T> LeerAsync()
        {
            await using var conexion = new FbConnection(servidor.Cadena());
            await conexion.OpenAsync(FirebirdSinCancelar.Nunca);
            return await leer(conexion);
        }
    }

    /// <summary>La variante de la caja nueva de cada producto de eleventa (la de la importación o la lectura).</summary>
    private static async Task<Dictionary<int, Guid>> VariantesDeAsync(PosDbContext db, IEnumerable<int> productos, CancellationToken ct)
    {
        var claves = productos.Distinct().Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList();
        var mapeos = await db.MapeosImportacion.AsNoTracking()
            .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante) && claves.Contains(m.IdOrigen))
            .Select(m => new { m.IdOrigen, m.IdNuevo }).ToListAsync(ct);
        return mapeos.GroupBy(m => int.Parse(m.IdOrigen, CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.First().IdNuevo);
    }

    /// <summary>La marca con que la caja 2 escribe una devolución en eleventa: su hora local en diezmilésimas de segundo.</summary>
    private static DateTime Marca(DateTime fechaUtc)
    {
        var local = HoraMexico.ALocal(fechaUtc);
        return new DateTime(local.Ticks - local.Ticks % 1000);
    }

    /// <summary>Un ticket de eleventa en la ventana de devoluciones: su <c>VENTATICKETS.ID</c> en un Guid que se reconoce.</summary>
    public static Guid IdDelTicket(int ticket) => new(ticket, 0x0e11, 0x0001, new byte[8]);

    /// <summary>Un artículo de un ticket de eleventa en la ventana de devoluciones: su <c>VENTATICKETS_ARTICULOS.ID</c>.</summary>
    public static Guid IdDelArticulo(int articulo) => new(articulo, 0x0e11, 0x0002, new byte[8]);

    /// <summary>El <c>VENTATICKETS_ARTICULOS.ID</c> de un Id de <see cref="IdDelArticulo"/>; vacío si no es uno.</summary>
    public static int? ArticuloDe(Guid id)
    {
        var bytes = id.ToByteArray();
        return BitConverter.ToInt16(bytes, 4) == 0x0e11 && BitConverter.ToInt16(bytes, 6) == 0x0002 && bytes[8..].All(b => b == 0)
            ? BitConverter.ToInt32(bytes, 0)
            : null;
    }

    private static long Milesimas(decimal cantidad) => (long)Math.Round(cantidad * 1000, MidpointRounding.AwayFromZero);
    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];
}
