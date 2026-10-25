using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que se le manda a eleventa de una venta, o por qué no se manda sola.</summary>
/// <param name="Descartada">Ya no hace falta mandarla (se canceló antes de llegar a eleventa); <paramref name="PorRevisar"/> dice por qué.</param>
public sealed record VentaArmada(VentaParaEleventa? Venta, string? PorRevisar, bool Descartada = false);

/// <summary>Lo que se le manda a eleventa de una cancelación o devolución, o por qué no se manda sola.</summary>
public sealed record DevolucionArmada(DevolucionParaEleventa? Devolucion, string? PorRevisar, bool Descartada = false);

/// <summary>Lo que se le manda a eleventa de una entrada o salida de efectivo, o por qué no se manda sola.</summary>
public sealed record MovimientoArmado(MovimientoParaEleventa? Movimiento, string? PorRevisar);

/// <summary>
/// Pasa una venta de la caja nueva a lo que entiende eleventa (docs/migracion-eleventa.md, «Mandar cada venta»): cada
/// línea con el producto de eleventa del que vino (el de la importación o la lectura), y lo demás como artículo común.
/// También sus cancelaciones y devoluciones, y las entradas y salidas de efectivo.
/// </summary>
public static class VentasParaEleventa
{
    public static async Task<VentaArmada> ArmarAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        var venta = await db.Ventas.AsNoTracking().Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == ventaId, ct);
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == venta.TurnoId, ct);
        return Armar(venta, turno, await ProductosDeAsync(db, venta, ct));
    }

    /// <summary>
    /// Una cancelación o devolución de una venta que ya está en eleventa. Si la venta no llegó (se descartó o quedó por
    /// revisar), lo que depende de ella tampoco va.
    /// </summary>
    public static async Task<DevolucionArmada> ArmarDevolucionAsync(PosDbContext db, Guid devolucionId, CancellationToken ct = default)
    {
        var devolucion = await db.Devoluciones.AsNoTracking().Include(d => d.Lineas).SingleAsync(d => d.Id == devolucionId, ct);
        var deLaVenta = await db.EnviosAEleventa.AsNoTracking()
            .SingleAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == devolucion.VentaId, ct);
        switch (deLaVenta.Estado)
        {
            case EstadoEnvioEleventa.Descartada:
                return new(null, "Su venta no llegó a eleventa: no hay nada que devolver allá.", Descartada: true);
            case EstadoEnvioEleventa.PorRevisar:
                return new(null, $"Su venta quedó por revisar ({deLaVenta.Error}).");
            case EstadoEnvioEleventa.AMano:
                return new(null, "Su venta se anotó a mano en eleventa: no se sabe su ticket allá, también va a mano.");
            case EstadoEnvioEleventa.Pendiente:
                throw new InvalidOperationException("Su venta todavía no llega a eleventa.");
        }
        if (devolucion.AlCreditoCentavos > 0)
            return new(null, "Bajó la cuenta de un cliente: a eleventa solo pasan solas las devoluciones en efectivo.");

        var venta = await db.Ventas.AsNoTracking().Include(v => v.Lineas).SingleAsync(v => v.Id == devolucion.VentaId, ct);
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == devolucion.TurnoId, ct);
        return ArmarDevolucion(devolucion, venta, turno, deLaVenta.TicketEleventa!.Value, await ProductosDeAsync(db, venta, ct));
    }

    public static DevolucionArmada ArmarDevolucion(Devolucion devolucion, Venta venta, Turno turno, int ticketEleventa, IReadOnlyDictionary<Guid, int> productoDe)
    {
        var articulos = devolucion.Lineas.Select(d =>
        {
            var linea = venta.Lineas.Single(l => l.Id == d.VentaLineaId);
            // Lo devuelto viene en presentaciones (2 paquetes); a eleventa va en su unidad, como se le mandó la venta.
            var cantidad = linea.CantidadBase.EnUnidades * d.Cantidad.Milesimas / linea.Cantidad.Milesimas;
            return new ArticuloDevuelto(linea.VarianteId is { } v && productoDe.TryGetValue(v, out var id) ? id : null,
                linea.Descripcion, cantidad, d.ImporteCentavos / 100m, d.RegresaAlInventario && linea.LlevaInventario);
        }).ToList();

        // Como en la venta: el redondeo del efectivo va en lo más grande, para que eleventa devuelva lo que salió de la caja.
        if (!devolucion.EsCancelacion && devolucion.RedondeoCentavos != 0 && articulos.Count > 0)
        {
            var mayor = articulos.IndexOf(articulos.MaxBy(a => a.Importe)!);
            articulos[mayor] = articulos[mayor] with { Importe = articulos[mayor].Importe + devolucion.RedondeoCentavos / 100m };
        }

        return new(new DevolucionParaEleventa(ticketEleventa, devolucion.EsCancelacion, HoraMexico.ALocal(devolucion.FechaUtc),
            new TurnoParaEleventa(HoraMexico.ALocal(turno.AbiertoUtc), turno.FondoCentavos / 100m), articulos), null);
    }

    /// <summary>
    /// La devolución de un ticket cobrado en eleventa (la ida, <see cref="DevolucionesConEleventa"/>): cada artículo por su
    /// renglón del ticket (<c>VENTATICKETS_ARTICULOS.ID</c>), con lo que salió de la caja. Lo que sale es exacto, artículo
    /// por artículo, sin redondeo (revisión del 2026-10-17): nunca más de lo que le queda en eleventa a ese renglón.
    /// </summary>
    public static async Task<DevolucionArmada> ArmarDevolucionDeEleventaAsync(PosDbContext db, Guid devolucionId, CancellationToken ct = default)
    {
        var d = await db.Set<DevolucionDeEleventa>().AsNoTracking().SingleAsync(x => x.Id == devolucionId, ct);
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == d.TurnoId, ct);
        var articulos = d.Articulos
            .Select(a => new ArticuloDevuelto(null, a.Nombre, a.Cantidad, a.ImporteCentavos / 100m, a.RegresaAlInventario, a.ArticuloEleventa))
            .ToList();
        return new(new DevolucionParaEleventa(d.TicketEleventa, EsCancelacion: false, HoraMexico.ALocal(d.FechaUtc),
            new TurnoParaEleventa(HoraMexico.ALocal(turno.AbiertoUtc), turno.FondoCentavos / 100m), articulos), null);
    }

    public static async Task<MovimientoArmado> ArmarMovimientoAsync(PosDbContext db, Guid movimientoId, CancellationToken ct = default)
    {
        var movimiento = await db.MovimientosCaja.AsNoTracking().SingleAsync(m => m.Id == movimientoId, ct);
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == movimiento.TurnoId, ct);
        return ArmarMovimiento(movimiento, turno);
    }

    /// <summary>
    /// Una entrada o salida de efectivo (F7 o F8) va con su motivo, su hora y su turno. La que llegó sin conexión cuando su
    /// turno ya tenía corte Z no se manda sola: en eleventa ese turno ya se cerró, y mandarla abriría otro.
    /// </summary>
    public static MovimientoArmado ArmarMovimiento(MovimientoCaja movimiento, Turno turno)
    {
        if (movimiento.FueraDeCorteUtc is not null)
            return new(null, "Llegó cuando su turno ya tenía corte: en eleventa hay que anotarla a mano.");
        return new(new MovimientoParaEleventa(movimiento.Tipo == TipoMovimientoCaja.Entrada, movimiento.MontoCentavos / 100m, movimiento.Motivo,
            HoraMexico.ALocal(movimiento.FechaUtc), new TurnoParaEleventa(HoraMexico.ALocal(turno.AbiertoUtc), turno.FondoCentavos / 100m)), null);
    }

    /// <summary>El <c>PRODUCTOS.ID</c> de eleventa de cada variante de la venta que vino de allá (la importación o la lectura).</summary>
    private static async Task<Dictionary<Guid, int>> ProductosDeAsync(PosDbContext db, Venta venta, CancellationToken ct)
    {
        var variantes = venta.Lineas.Where(l => l.VarianteId is not null).Select(l => l.VarianteId!.Value).Distinct().ToList();
        var mapeos = await db.MapeosImportacion.AsNoTracking()
            .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante) && variantes.Contains(m.IdNuevo))
            .ToListAsync(ct);
        // Si la depuración juntó dos productos de eleventa en uno, se usa el primero que se dio de alta allá.
        return mapeos.Where(m => int.TryParse(m.IdOrigen, out _)).GroupBy(m => m.IdNuevo)
            .ToDictionary(g => g.Key, g => g.Min(m => int.Parse(m.IdOrigen)));
    }

    /// <param name="productoDe">El <c>PRODUCTOS.ID</c> de eleventa de cada variante que vino de allá.</param>
    public static VentaArmada Armar(Venta venta, Turno turno, IReadOnlyDictionary<Guid, int> productoDe)
    {
        if (venta.Estado == EstadoVenta.Cancelada)
            return new(null, "Se canceló antes de pasar a eleventa: ya no hace falta mandarla.", Descartada: true);
        if (venta.Estado != EstadoVenta.Pagada || venta.PagadaUtc is not { } pagada)
            return new(null, "No está cobrada.");
        if (venta.Cobros.FirstOrDefault(c => c.Forma != FormaPago.Efectivo) is { } otra)
            return new(null, $"Fue con {Forma(otra.Forma)}: a eleventa solo pasan solas las ventas en efectivo.");

        // Lo cobrado con una escala de mayoreo va como el F11 de eleventa, con lo que se ahorró contra el menudeo (2026-10-24); lo
        // rebajado (el descuento, una promoción o el precio cambiado a la baja), como el descuento de eleventa (revisión del PR #139).
        var articulos = venta.Lineas
            .Select(l => new ArticuloParaEleventa(l.VarianteId is { } v && productoDe.TryGetValue(v, out var id) ? id : null,
                l.Descripcion, l.CantidadBase.EnUnidades, l.ImporteCentavos / 100m, l.TieneMayoreo, l.AhorroMayoreoCentavos / 100m, Rebajado(l) / 100m))
            .ToList();
        if (articulos.Count == 0)
            return new(null, "No tiene artículos.");

        // El redondeo del efectivo va en la línea más grande, para que el total de eleventa sea lo que entró a la caja.
        if (venta.RedondeoCentavos != 0)
        {
            var mayor = articulos.IndexOf(articulos.MaxBy(a => a.Importe)!);
            articulos[mayor] = articulos[mayor] with { Importe = articulos[mayor].Importe + venta.RedondeoCentavos / 100m, Redondeo = venta.RedondeoCentavos / 100m };
        }

        return new(new VentaParaEleventa(
            HoraMexico.ALocal(venta.CreadaUtc), HoraMexico.ALocal(pagada),
            venta.Cobros.Sum(c => c.RecibidoCentavos) / 100m,
            new TurnoParaEleventa(HoraMexico.ALocal(turno.AbiertoUtc), turno.FondoCentavos / 100m),
            articulos), null);
    }

    /// <summary>
    /// Lo que se rebajó de la línea abajo de su precio (el de menudeo o, con mayoreo, el de mayoreo), en centavos: el descuento, lo
    /// de una promoción y, si el precio se cambió a la baja, lo que bajó contra el del catálogo.
    /// </summary>
    private static long Rebajado(VentaLinea l) =>
        l.DescuentoCentavos + l.AhorroPromocionCentavos
        + (l.PrecioOriginalCentavos is { } original && original > l.PrecioCentavos
            ? Math.Max(0, l.Cantidad.Importe(original) - l.Cantidad.Importe(l.PrecioCentavos))
            : 0);

    private static string Forma(FormaPago forma) => forma switch
    {
        FormaPago.Tarjeta => "tarjeta",
        FormaPago.Transferencia => "transferencia",
        FormaPago.Credito => "crédito",
        _ => forma.ToString(),
    };
}
