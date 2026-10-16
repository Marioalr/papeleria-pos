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
public sealed record ResultadoVuelta(int Pasadas, int PorRevisar);

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
            return new LineaDevolucionDto(IdDelArticulo(a.Id), a.Nombre, Milesimas(vendido), Milesimas(allaCantidad + aqui.GetValueOrDefault(a.Id)),
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
    /// efectivo (redondeada al peso, como el reembolso de siempre), la entrada al kardex de lo que regresa y el envío a
    /// eleventa, todo junto. Regresa lo guardado y la salida, para el comprobante.
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
            var queda = articulo.Cantidad - aqui.GetValueOrDefault(articulo.Id);
            if (cantidad > queda + 0.0005m)
                throw new InvalidOperationException($"De «{articulo.Nombre}» solo quedan {queda.ToString("0.###", CultureInfo.InvariantCulture)} por devolver.");
            // Como la ventana: en proporción a lo vendido (con lo devuelto allá), redondeado al centavo.
            var (allaCantidad, allaDinero) = DevueltoAlla(t, articulo);
            var vendido = Milesimas(articulo.Cantidad + allaCantidad);
            var importe = Redondeo.Dividir((Int128)Centavos(articulo.Total + allaDinero) * Milesimas(cantidad), vendido);
            devueltos.Add(new ArticuloDevueltoDeEleventa(articulo.Id, articulo.Codigo, articulo.Nombre, cantidad, importe,
                pedida.All(l => l.RegresaAlInventario) && articulo.ProductoId is not null,
                articulo.ProductoId is { } producto && variantes.TryGetValue(producto, out var variante) ? variante : null));
        }
        var total = devueltos.Sum(d => d.ImporteCentavos);

        // Lo que sale en efectivo, al peso y acumulado por ticket como en la caja nueva: con lo que ya se devolvió en
        // eleventa y aquí (lo que todavía no llega allá), sin regresar centavos sueltos.
        var anteriores = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => d.TicketEleventa == t.Id).ToListAsync(ct);
        var pendientes = await NoLleganAsync(db, anteriores.Select(d => d.Id).ToList(), ct);
        var enEleventa = Centavos(t.Devoluciones.Sum(d => d.Total));
        var resumen = new ResumenReembolsos(Centavos(t.Pagado),
            enEleventa + anteriores.Where(d => pendientes.Contains(d.Id)).Sum(d => d.TotalCentavos), 0,
            enEleventa + anteriores.Where(d => pendientes.Contains(d.Id)).Sum(d => d.ReembolsoEfectivoCentavos));
        var completa = t.Articulos.All(a => a.Cantidad - aqui.GetValueOrDefault(a.Id) - devueltos.Where(d => d.ArticuloEleventa == a.Id).Sum(d => d.Cantidad) <= 0.0005m);
        var reembolso = CalculoDevolucion.ReembolsoEnEfectivo(resumen, total, 0, completa);

        var control = new ServicioControl(db);
        await control.RequerirAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.Devolucion, total,
            $"Devolución del ticket #{t.Folio} de eleventa ({t.Caja})", s.AutorizacionId, s.Motivo, ct: ct);

        var devolucion = new DevolucionDeEleventa
        {
            SucursalId = caja.SucursalId, CajaId = caja.Id, TurnoId = turno.Id, UsuarioId = s.UsuarioId, Motivo = s.Motivo.Trim(), FechaUtc = ahoraUtc,
            AutorizoId = s.AutorizacionId is { } autorizacion ? (await db.Autorizaciones.FindAsync([autorizacion], ct))?.AutorizadorId : null,
            TicketEleventa = t.Id, Folio = t.Folio, CajaEleventa = t.CajaId, CajaNombre = Recortar(t.Caja, 50), TurnoTicketEleventa = t.TurnoId,
            VendidoUtc = HoraMexico.AUtc(vendidoEn), TicketTotalCentavos = Centavos(t.Pagado), Ticket = [.. t.Articulos], Articulos = devueltos,
            TotalCentavos = total, ReembolsoEfectivoCentavos = reembolso, RedondeoCentavos = reembolso - total,
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

    /// <summary>Lo devuelto aquí de cada artículo de ese ticket que todavía no está en eleventa (pendiente o por revisar).</summary>
    private static async Task<Dictionary<int, decimal>> DevueltoAquiAsync(PosDbContext db, int ticket, CancellationToken ct)
    {
        var devoluciones = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => d.TicketEleventa == ticket).ToListAsync(ct);
        var noLlegan = await NoLleganAsync(db, devoluciones.Select(d => d.Id).ToList(), ct);
        return devoluciones.Where(d => noLlegan.Contains(d.Id)).SelectMany(d => d.Articulos)
            .GroupBy(a => a.ArticuloEleventa).ToDictionary(g => g.Key, g => g.Sum(a => a.Cantidad));
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
    /// salta; lo demás es una devolución sin efectivo con su entrada al kardex, o queda por revisar. Quien llama guarda.
    /// </summary>
    public static async Task<ResultadoVuelta> AplicarVueltaAsync(PosDbContext db, IReadOnlyList<DevolucionLeida> devoluciones, DateTime ahoraUtc,
        CancellationToken ct = default)
    {
        int pasadas = 0, porRevisar = 0;
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
        return new ResultadoVuelta(pasadas, porRevisar);
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
        await AplicarVueltaAsync(db, devoluciones, ahoraUtc, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Por qué no se devuelve aquí lo que queda de una venta de la caja 2: ya tuvo una devolución en eleventa (pasada a su
    /// venta, o por revisar).
    /// </summary>
    public static async Task<string?> PorQueNoSeDevuelveAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default) =>
        await db.Set<DevolucionDesdeEleventa>().AsNoTracking().AnyAsync(d => d.VentaId == ventaId, ct) ? YaTuvoDevolucionEnEleventa : null;

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
