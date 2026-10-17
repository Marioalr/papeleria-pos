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
/// <param name="Propias">Las que eran devoluciones de aquí anotadas a mano en eleventa: se ligaron, no se aplicaron otra vez.</param>
public sealed record ResultadoVuelta(int Pasadas, int PorRevisar, int Propias = 0);

/// <summary>
/// Las devoluciones y cancelaciones de ida y vuelta con eleventa (decisión del dueño, 2026-10-12; docs/migracion-eleventa.md,
/// «Devoluciones de ida y vuelta»). Solo del piloto: se quita al dejar eleventa.
/// <list type="bullet">
/// <item><b>La ida:</b> en la caja 2 se devuelve un ticket que se cobró en eleventa. Se lee en vivo (el ticket, sus
/// artículos y sus devoluciones); sin eleventa no se devuelve. Solo efectivo, 7 días y los permisos de la caja nueva. El
/// ticket leído se guarda en el puente (<see cref="DevolucionDeEleventa"/>), no como venta. El dinero sale del turno de la
/// caja nueva como una salida de efectivo, que no va a eleventa como salida: la devolución va por la bandeja, como la
/// escribe eleventa. Lo que regresa al inventario entra al kardex de aquí, y mientras no llega a eleventa la lectura lo
/// sigue sumando.</item>
/// <item><b>La vuelta:</b> la lectura de cada pocos minutos trae, en la misma foto que las existencias, las devoluciones y
/// cancelaciones que se hicieron en eleventa de los tickets que mandó la caja 2, y las pasa a la venta de aquí antes de las
/// existencias: una devolución sin efectivo (el dinero salió de la caja de eleventa, así que el corte de aquí no la
/// cuenta), con la entrada al kardex, a nombre del cajero de la venta y con el motivo «Hecha en eleventa…». Lo propio se
/// reconoce por la marca <c>TICKET_ID</c> + <c>DEVUELTO_EN</c>, y lo ya pasado por <c>DEVOLUCIONES.ID</c>. Nada de esto va
/// a la bandeja. Lo que no se puede pasar (ya se devolvió aquí, no está en el ticket) queda por revisar.</item>
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
    public const string CanceladoEnEleventa = "Ese ticket está cancelado en eleventa.";
    public const string Vencido = "El ticket tiene más de 7 días: ya no admite devoluciones.";

    /// <summary>Por qué una devolución en eleventa de un ticket de la caja 2 no se pasó a su venta: ya no queda tanto aquí.</summary>
    public const string NoAlcanza =
        "En eleventa se devolvió de este ticket algo que aquí ya no queda (¿se devolvió también en la caja 2?): revísalo en las dos.";

    /// <summary>
    /// Por qué una devolución en eleventa de un ticket de la caja 2 no se pasó a su venta: aquí hay una devolución de ese
    /// ticket que se iba a anotar a mano allá, y no se sabe si es esa.
    /// </summary>
    public const string NoSeSabeSiEsLaDeAqui =
        "En eleventa se devolvió algo de este ticket y aquí hay una devolución que se iba a anotar a mano allá, pero no es igual: "
        + "revisa si es la misma.";

    // ── La ida ──

    /// <summary>
    /// Los tickets cobrados con ese folio, en vivo; vacío si eleventa no contestó a tiempo (<see cref="EsperaEnVivo"/>).
    /// </summary>
    public static async Task<List<TicketLeido>?> PorFolioEnVivoAsync(ServidorEleventa servidor, int folio, CancellationToken ct = default) =>
        await EnVivoAsync(servidor, c => TicketsDeEleventa.PorFolioAsync(c, folio, ct), ct);

    /// <summary>Un ticket con sus artículos y sus devoluciones, en vivo. Sin eleventa, el error lo dice (<see cref="NoContesta"/>).</summary>
    public static async Task<TicketLeido> TicketEnVivoAsync(ServidorEleventa servidor, int ticket, CancellationToken ct = default) =>
        (await EnVivoAsync(servidor, async c => new Leido<TicketLeido>(await TicketsDeEleventa.TicketAsync(c, ticket, ct)), ct)
            ?? throw new InvalidOperationException(NoContesta)).Valor
        ?? throw new InvalidOperationException($"En eleventa ya no está ese ticket ({ticket}).");

    /// <summary>Lo que se leyó en vivo, aunque no esté (para distinguirlo de que eleventa no contestó).</summary>
    private sealed record Leido<T>(T? Valor) where T : class;

    /// <summary>
    /// Lo que la ventana de devoluciones enseña de un ticket de eleventa, como uno de la caja: lo vendido, lo ya devuelto
    /// (allá, y aquí lo que todavía no llega) y lo que queda, o por qué no se devuelve aquí. Los Id son los de eleventa
    /// (<see cref="IdDelTicket"/>, <see cref="IdDelArticulo"/>).
    /// </summary>
    public static async Task<VentaParaDevolverDto> ParaDevolverAsync(PosDbContext db, TicketLeido t, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var aqui = await DevueltoAquiAsync(db, t.Id, ct);
        var lineas = t.Articulos.Select(a =>
        {
            var (allaCantidad, allaDinero) = DevueltoAlla(t, a);
            var vendido = a.Cantidad + allaCantidad;
            return new LineaDevolucionDto(IdDelArticulo(a.Id), a.Nombre, Milesimas(vendido), Milesimas(allaCantidad + aqui.GetValueOrDefault(a.Id).Cantidad),
                Centavos(a.Total + allaDinero), a.ProductoId is not null);
        }).ToList();
        var dias = t.VendidoEn is { } vendidoEn ? Devolucion.DiasParaDevolver - (int)(HoraMexico.ALocal(ahoraUtc) - vendidoEn).TotalDays : 0;
        return new VentaParaDevolverDto(IdDelTicket(t.Id), $"{t.Folio} · {t.Caja} (eleventa)", t.VendidoEn ?? HoraMexico.ALocal(ahoraUtc),
            t.Cancelado ? EstadoVenta.Cancelada : EstadoVenta.Pagada, Centavos(t.Pagado), lineas, PuedeCancelar: false, Math.Max(0, dias),
            NoSeDevuelveEnElPiloto: t.Cancelado ? null : t.EnEfectivo ? null : NoFueEnEfectivo);
    }

    /// <summary>Por qué un ticket de eleventa no se devuelve aquí (cancelado, no fue en efectivo, más de 7 días); vacío si sí.</summary>
    public static string? PorQueNoSeDevuelveAqui(TicketLeido t, DateTime ahoraUtc) =>
        t.Cancelado ? CanceladoEnEleventa
        : !t.EnEfectivo ? NoFueEnEfectivo
        : t.VendidoEn is not { } vendido || ahoraUtc - HoraMexico.AUtc(vendido) > TimeSpan.FromDays(Devolucion.DiasParaDevolver) ? Vencido
        : null;

    /// <summary>
    /// La devolución de un ticket de eleventa en la caja nueva: con lo que se acaba de leer en vivo, las reglas de la caja
    /// nueva (motivo, permiso y quién autoriza) y el turno abierto de esta caja. Guarda el ticket como se leyó, la salida de
    /// efectivo, la entrada al kardex de lo que regresa y el envío a eleventa, todo junto. Regresa lo guardado y la salida,
    /// para el comprobante.
    /// <para>
    /// Lo que se regresa es exacto, al centavo, como lo regresaría eleventa (revisión del 2026-10-17): eleventa cobró con
    /// centavos y dio el cambio con centavos, sin redondear. De cada artículo, si se devuelve todo lo que queda de él, sale
    /// exacto lo que le queda en eleventa (su <c>TOTAL_ARTICULO</c>, menos lo de aquí que todavía no llega); si es una parte,
    /// en proporción a su precio, sin pasarse de lo que le queda. Así el ticket de eleventa nunca queda abajo de cero, queda
    /// en cero al devolverlo todo, y lo que sale del cajón es lo que eleventa anota como devuelto. Redondear al peso, como
    /// en las ventas de la caja nueva (que se cobraron ya redondeadas), lo dejaría en −$0.50 o con un total sin artículos.
    /// </para>
    /// </summary>
    public static async Task<(DevolucionDeEleventa Devolucion, MovimientoCaja? Salida)> DevolverAsync(
        PosDbContext db, TicketLeido t, SolicitudDevolucion s, DateTime ahoraUtc, CancellationToken ct = default)
    {
        if (PorQueNoSeDevuelveAqui(t, ahoraUtc) is { } porque)
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
            var (aquiCantidad, aquiDinero) = aqui.GetValueOrDefault(articulo.Id);
            var queda = articulo.Cantidad - aquiCantidad;
            if (cantidad > queda + 0.0005m)
                throw new InvalidOperationException($"De «{articulo.Nombre}» solo quedan {queda.ToString("0.###", CultureInfo.InvariantCulture)} por devolver.");
            // Todo lo que queda: exacto lo que le queda en eleventa. Una parte: como la ventana, en proporción a lo vendido (con
            // lo devuelto allá), al centavo y sin pasarse de lo que le queda.
            var quedaDinero = Math.Max(0, Centavos(articulo.Total) - aquiDinero);
            var (allaCantidad, allaDinero) = DevueltoAlla(t, articulo);
            var vendido = Milesimas(articulo.Cantidad + allaCantidad);
            var importe = cantidad >= queda - 0.0005m
                ? quedaDinero
                : Math.Min(quedaDinero, Redondeo.Dividir((Int128)Centavos(articulo.Total + allaDinero) * Milesimas(cantidad), vendido));
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
        db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.DevolucionDeEleventa, DevolucionDeEleventaId = devolucion.Id, CreadoUtc = ahoraUtc });
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
    private static async Task<HashSet<Guid>> NoLleganAsync(PosDbContext db, List<Guid> devoluciones, CancellationToken ct) =>
        (await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.DevolucionDeEleventaId != null && devoluciones.Contains(e.DevolucionDeEleventaId.Value)
                && (e.Estado == EstadoEnvioEleventa.Pendiente || e.Estado == EstadoEnvioEleventa.PorRevisar))
            .Select(e => e.DevolucionDeEleventaId!.Value).ToListAsync(ct)).ToHashSet();

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
        return await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null && e.EnviadaUtc >= desde)
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
        int pasadas = 0, porRevisar = 0, ligadas = 0;
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
            // La de la caja 2: su marca es la hora en que se hizo aquí (TICKET_ID + DEVUELTO_EN).
            var propias = await db.Devoluciones.AsNoTracking().Where(x => x.VentaId == venta.Id).Select(x => x.FechaUtc).ToListAsync(ct);
            if (propias.Any(f => Marca(f) == d.DevueltoEn))
                continue;

            var registro = new DevolucionDesdeEleventa
            {
                DevolucionEleventa = d.Id, TicketEleventa = d.TicketId, DevueltaUtc = devueltaUtc, EsCancelacion = d.EsCancelacion, TotalCentavos = Centavos(d.Total),
                Cajero = Recortar(d.Cajero, 50), Caja = Recortar(d.Caja, 50), VentaId = venta.Id, VistaUtc = ahoraUtc,
            };
            db.Set<DevolucionDesdeEleventa>().Add(registro);

            // Una devolución de aquí que no pasó sola (de otro turno o de otra caja de eleventa, queda por revisar: «Anótala a
            // mano en eleventa») y se anotó a mano allá: eleventa la escribe con su hora, sin la marca, y parecería nueva. Si es
            // igual (lo mismo de los mismos artículos), es esa: se liga y su envío queda anotado a mano. Si no se sabe cuál es,
            // queda por revisar en lugar de aplicarla (revisión del 2026-10-17).
            if (await AnotadasAManoAsync(db, venta.Id, ct) is { Count: > 0 } aMano)
            {
                if (aMano.Where(p => Iguales(p.Armada, d)).ToList() is [var propia])
                {
                    (registro.DevolucionId, registro.Propia) = (propia.Envio.DevolucionId, true);
                    if (propia.Envio.Estado == EstadoEnvioEleventa.PorRevisar)
                        (propia.Envio.Estado, propia.Envio.Error) = (EstadoEnvioEleventa.AMano,
                            Recortar($"Se anotó a mano en eleventa: la lectura la encontró allá ({d.Caja}, {d.DevueltoEn:dd/MM HH:mm}). {propia.Envio.Error}".Trim(), 500));
                    ligadas++;
                }
                else
                {
                    registro.PorRevisar = NoSeSabeSiEsLaDeAqui;
                    porRevisar++;
                }
                continue;
            }
            var devolucion = await ArmarDeEleventaAsync(db, venta, d, devueltaUtc, ct);
            if (devolucion is null)
            {
                registro.PorRevisar = NoAlcanza;
                porRevisar++;
                continue;
            }
            // Lo que regresa al inventario: al mismo costo con que salió, como una devolución de aquí.
            var inventario = new RegistroInventario(db);
            foreach (var linea in devolucion.Lineas)
            {
                var vendida = venta.Lineas.Single(l => l.Id == linea.VentaLineaId);
                if (!vendida.LlevaInventario || vendida.VarianteId is not { } varianteId || await db.Variantes.FindAsync([varianteId], ct) is not { } variante)
                    continue;
                var cantidadBase = new Cantidad(Redondeo.Dividir((Int128)vendida.CantidadBase.Milesimas * linea.Cantidad.Milesimas, vendida.Cantidad.Milesimas));
                await inventario.AgregarAsync(Kardex.Entrada(venta.SucursalId, variante, TipoMovimiento.Devolucion, cantidadBase, linea.CostoCentavos, devueltaUtc,
                    documentoTipo: nameof(Devolucion), documentoId: devolucion.Id), ct);
            }
            if (devolucion.EsCancelacion || devolucion.Lineas.Any(l => venta.Lineas.Single(x => x.Id == l.VentaLineaId).EsImpresiones))
                await ImpresionesDelPos.EnLaVenta(db).LiberarAsync(venta.Id, ct);
            CalculoDevolucion.Aplicar(venta, devolucion);
            db.Devoluciones.Add(devolucion);
            registro.DevolucionId = devolucion.Id;
            control.Registrar(venta.SucursalId, venta.CajaId, null, d.EsCancelacion ? AccionControlada.CancelarTicket : AccionControlada.Devolucion,
                $"{(d.EsCancelacion ? "Cancelación" : "Devolución")} hecha en eleventa del ticket {venta.FolioTexto} (#{ventas.First(v => v.Ticket == d.TicketId).FolioEleventa}, "
                + $"{d.Caja}, {d.Cajero}): no salió de esta caja", devolucion.TotalCentavos, venta.Id);
            pasadas++;
        }
        return new ResultadoVuelta(pasadas, porRevisar, ligadas);
    }

    /// <summary>
    /// La vuelta, en una sola transacción, y guardada: se lee la venta, se le pasa lo de eleventa y se guarda sin que otra
    /// devolución de esa venta se cuele en medio y se pierda (una de esta caja que no esperó el candado de la bandeja; revisión
    /// del 2026-10-17). Con SQLite la transacción aparta la escritura desde el principio: la otra espera a que termine, y
    /// entonces ve lo de aquí (y si no alcanza a esperar, no entra: el cajero la vuelve a buscar).
    /// </summary>
    public static async Task<ResultadoVuelta> PasarVueltaAsync(PosDbContext db, IReadOnlyList<DevolucionLeida> devoluciones, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        if (devoluciones.Count == 0)
            return new ResultadoVuelta(0, 0);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var resultado = await AplicarVueltaAsync(db, devoluciones, ahoraUtc, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return resultado;
    }

    /// <summary>Una devolución de aquí que se iba a anotar a mano en eleventa, con lo que se habría mandado.</summary>
    private sealed record AnotadaAMano(EnvioAEleventa Envio, DevolucionParaEleventa Armada);

    /// <summary>
    /// Las devoluciones y cancelaciones de aquí de esa venta que no pasaron solas a eleventa (por revisar, o ya anotadas a
    /// mano) y que todavía no se ligan con una de eleventa: lo que se habría mandado, para compararlo.
    /// </summary>
    private static async Task<List<AnotadaAMano>> AnotadasAManoAsync(PosDbContext db, Guid ventaId, CancellationToken ct)
    {
        var envios = await db.EnviosAEleventa
            .Where(e => e.VentaId == ventaId && (e.Tipo == TipoEnvioEleventa.Devolucion || e.Tipo == TipoEnvioEleventa.Cancelacion) && e.DevolucionId != null
                && (e.Estado == EstadoEnvioEleventa.PorRevisar || e.Estado == EstadoEnvioEleventa.AMano))
            .ToListAsync(ct);
        if (envios.Count == 0)
            return [];
        var ligadas = (await db.Set<DevolucionDesdeEleventa>().AsNoTracking().Where(x => x.VentaId == ventaId && x.DevolucionId != null)
                .Select(x => x.DevolucionId!.Value).ToListAsync(ct))
            .Concat(db.ChangeTracker.Entries<DevolucionDesdeEleventa>().Where(x => x.Entity.DevolucionId is not null).Select(x => x.Entity.DevolucionId!.Value))
            .ToHashSet();
        var lista = new List<AnotadaAMano>();
        foreach (var envio in envios.Where(e => !ligadas.Contains(e.DevolucionId!.Value)))
        {
            if ((await VentasParaEleventa.ArmarDevolucionAsync(db, envio.DevolucionId!.Value, ct)).Devolucion is { } armada)
                lista.Add(new AnotadaAMano(envio, armada));
        }
        return lista;
    }

    /// <summary>
    /// Si una devolución de eleventa es la misma que la de aquí: las dos cancelaciones, o lo mismo de los mismos artículos
    /// (por su producto de eleventa o, si fue común, por su nombre). El dinero no cuenta: allá pudo salir con otro precio.
    /// </summary>
    private static bool Iguales(DevolucionParaEleventa aqui, DevolucionLeida alla)
    {
        if (aqui.EsCancelacion != alla.EsCancelacion)
            return false;
        if (aqui.EsCancelacion)
            return true;
        static string Clave(int? producto, string nombre) => producto is { } p ? $"#{p}" : nombre.Trim().ToUpperInvariant();
        var deAqui = aqui.Articulos.GroupBy(a => Clave(a.ProductoId, a.Nombre)).ToDictionary(g => g.Key, g => g.Sum(a => a.Cantidad));
        var deAlla = alla.Articulos.GroupBy(a => Clave(a.ProductoId, a.Nombre)).ToDictionary(g => g.Key, g => g.Sum(a => a.Cantidad));
        return deAqui.Count == deAlla.Count && deAqui.All(a => deAlla.TryGetValue(a.Key, out var cantidad) && Math.Abs(cantidad - a.Value) < 0.0005m);
    }

    /// <summary>
    /// La devolución de la caja nueva que corresponde a una de eleventa: la cancelación, todo lo que queda; la parcial, cada
    /// artículo de eleventa en las líneas de su producto (o, si fue común, en las de su nombre), en proporción. Sin efectivo
    /// (salió de la caja de eleventa) y del turno de la venta. Vacía si aquí ya no queda tanto.
    /// </summary>
    private static async Task<Devolucion?> ArmarDeEleventaAsync(PosDbContext db, Venta venta, DevolucionLeida d, DateTime devueltaUtc, CancellationToken ct)
    {
        if (venta.Estado != EstadoVenta.Pagada)
            return null;
        var devolucion = new Devolucion
        {
            VentaId = venta.Id, SucursalId = venta.SucursalId, CajaId = venta.CajaId, TurnoId = venta.TurnoId, UsuarioId = venta.CajeroId,
            EsCancelacion = d.EsCancelacion, FechaUtc = devueltaUtc,
            Motivo = Recortar($"Hecha en eleventa ({d.Caja}, {d.Cajero}, {d.DevueltoEn:dd/MM HH:mm}): no salió de esta caja", 200),
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
            devolucion.Lineas.Add(new DevolucionLinea
            {
                DevolucionId = devolucion.Id, VentaLineaId = linea.Id, Cantidad = cantidad,
                ImporteCentavos = Proporcion(linea.ImporteCentavos, cantidad, linea.Cantidad), CostoCentavos = Proporcion(linea.CostoCentavos, cantidad, linea.Cantidad),
            });
        }
        devolucion.TotalCentavos = devolucion.Lineas.Sum(l => l.ImporteCentavos);
        return devolucion;
    }

    // ── Antes de devolver aquí un ticket de la caja 2 ──

    /// <summary>
    /// Antes de devolver o cancelar aquí una venta de la caja 2 que ya está en eleventa: lo que se le haya devuelto allá
    /// entra antes, como en la vuelta. Si eleventa no contesta a tiempo, se deja pasar (decisión del dueño, 2026-10-11): un
    /// choque queda por revisar al mandarla. Falso si no se pudo revisar.
    /// </summary>
    public static async Task<bool> RevisarEnVivoAsync(PosDbContext db, ServidorEleventa servidor, Guid ventaId, DateTime ahoraUtc, CancellationToken ct = default)
    {
        var ticket = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null)
            .Select(e => e.TicketEleventa).FirstOrDefaultAsync(ct);
        if (ticket is not { } id)
            return true;
        var devoluciones = await EnVivoAsync(servidor, async c =>
        {
            await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
            }, ct);
            var leidas = await TicketsDeEleventa.DevolucionesAsync(c, tx, [id], ct);
            await tx.CommitAsync(ct);
            return leidas;
        }, ct);
        if (devoluciones is null)
            return false;
        await PasarVueltaAsync(db, devoluciones, ahoraUtc, ct);
        return true;
    }

    /// <summary>
    /// Por qué no se devuelve aquí lo que queda de una venta de la caja 2: ya tuvo una devolución en eleventa (pasada a su
    /// venta, o por revisar).
    /// </summary>
    public static async Task<string?> PorQueNoSeDevuelveAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default) =>
        // La de aquí anotada a mano allá no cuenta: su dinero salió de esta caja.
        await db.Set<DevolucionDesdeEleventa>().AsNoTracking().AnyAsync(d => d.VentaId == ventaId && !d.Propia, ct) ? YaTuvoDevolucionEnEleventa : null;

    // ── Ayudas ──

    /// <summary>Lo que se lee en vivo, con un límite: si eleventa no contesta a tiempo (o la red falla), vacío.</summary>
    private static async Task<T?> EnVivoAsync<T>(ServidorEleventa servidor, Func<FbConnection, Task<T>> leer, CancellationToken ct) where T : class
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaEnVivo);
        try
        {
            await using var conexion = new FbConnection(servidor.Cadena());
            await conexion.OpenAsync(limite.Token).WaitAsync(limite.Token);
            return await leer(conexion).WaitAsync(limite.Token);
        }
        catch (Exception ex) when (ex is FbException or IOException or System.Net.Sockets.SocketException or TimeoutException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
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

    private static long Proporcion(long total, Cantidad parte, Cantidad todo) =>
        parte == todo ? total : Redondeo.Dividir((Int128)total * parte.Milesimas, todo.Milesimas);

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
