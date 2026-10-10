namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// Dinero que regresa al cliente por una venta: una cancelación (todo el ticket, en su mismo turno) o una devolución
/// (algunos artículos, con ticket y hasta 7 días). El reembolso sale en efectivo de la caja donde se hace, redondeado al
/// peso como el cobro.
/// </summary>
public class Devolucion
{
    public const int DiasParaDevolver = 7;

    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid VentaId { get; set; }
    public Guid SucursalId { get; set; }
    public Guid CajaId { get; set; }

    /// <summary>El turno de donde sale el efectivo: cuenta en su corte.</summary>
    public Guid TurnoId { get; set; }

    public Guid UsuarioId { get; set; }
    public Guid? AutorizoId { get; set; }
    public bool EsCancelacion { get; set; }
    public required string Motivo { get; set; }
    public long TotalCentavos { get; set; }

    /// <summary>Lo que salió de la caja en efectivo. En una cancelación pagada con tarjeta o transferencia es menos que el total.</summary>
    public long ReembolsoEfectivoCentavos { get; set; }

    /// <summary>
    /// El redondeo al peso de lo que salió en efectivo (fase 3): en una cancelación, el de la venta (se regresa lo
    /// cobrado); en una devolución, el reembolso menos el total. Informativo, como el de la venta.
    /// </summary>
    public long RedondeoCentavos { get; set; }

    public DateTime FechaUtc { get; set; }

    public List<DevolucionLinea> Lineas { get; set; } = [];
}

public class DevolucionLinea
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid DevolucionId { get; set; }
    public Guid VentaLineaId { get; set; }

    /// <summary>En presentaciones, como se vendió.</summary>
    public Cantidad Cantidad { get; set; }

    public long ImporteCentavos { get; set; }
    public long CostoCentavos { get; set; }

    /// <summary>Si regresa a la venta; si llegó dañado, se registra como merma.</summary>
    public bool RegresaAlInventario { get; set; } = true;
}

/// <summary>Lo que el cajero pide devolver de una línea.</summary>
public sealed record LineaPorDevolver(Guid VentaLineaId, Cantidad Cantidad, bool RegresaAlInventario = true);

/// <summary>
/// Lo que ya pasó con un ticket antes de esta devolución: lo que pagó el cliente y lo que ya se devolvió y se
/// reembolsó. Con esto se redondea lo que sale en efectivo sin regresar centavos sueltos (fase 3).
/// </summary>
/// <param name="DevueltoCentavos">El total de las devoluciones anteriores, impresiones incluidas.</param>
/// <param name="ImpresionesCentavos">Lo que llevó el ticket de impresiones: eso se regresa exacto.</param>
public sealed record ResumenReembolsos(
    long PagadoCentavos, long DevueltoCentavos, long ImpresionesDevueltasCentavos, long ReembolsadoCentavos, long ImpresionesCentavos = 0)
{
    /// <summary>
    /// Lo más que se regresa en efectivo de todo el ticket: lo que pagó el cliente, con la mercancía en pesos (si una
    /// parte se pagó con tarjeta o transferencia, lo pagado trae centavos que en efectivo no se pueden dar).
    /// </summary>
    public long TopeCentavos => Redondeo.AlPeso(PagadoCentavos - ImpresionesCentavos) + ImpresionesCentavos;

    public static ResumenReembolsos De(Venta venta, IEnumerable<Devolucion> anteriores)
    {
        var lista = anteriores.Where(d => d.VentaId == venta.Id).ToList();
        var impresiones = venta.Lineas.Where(l => l.EsImpresiones).Select(l => l.Id).ToHashSet();
        return new ResumenReembolsos(venta.TotalCobradoCentavos, lista.Sum(d => d.TotalCentavos),
            lista.SelectMany(d => d.Lineas).Where(l => impresiones.Contains(l.VentaLineaId)).Sum(l => l.ImporteCentavos),
            lista.Sum(d => d.ReembolsoEfectivoCentavos), venta.Lineas.Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos));
    }
}

public static class CalculoDevolucion
{
    /// <summary>
    /// Arma la devolución de una venta: revisa plazo y cantidades, y calcula importe y costo de cada línea en
    /// proporción a lo vendido. No cambia la venta; eso lo hace <see cref="Aplicar"/>.
    /// </summary>
    /// <param name="anteriores">Las devoluciones que ya tiene el ticket: con ellas se redondea lo que sale en efectivo.</param>
    public static Devolucion Armar(
        Venta venta, IReadOnlyList<LineaPorDevolver> lineas, Guid cajaId, Guid turnoId, Guid usuarioId, string motivo, DateTime ahoraUtc,
        IEnumerable<Devolucion>? anteriores = null)
    {
        if (venta.Estado != EstadoVenta.Pagada)
            throw new InvalidOperationException("Solo se devuelve de un ticket cobrado y no cancelado.");
        if (ahoraUtc - venta.PagadaUtc > TimeSpan.FromDays(Devolucion.DiasParaDevolver))
            throw new InvalidOperationException($"El ticket tiene más de {Devolucion.DiasParaDevolver} días: ya no admite devoluciones.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("La devolución lleva motivo.", nameof(motivo));
        if (lineas.Count == 0 || lineas.Any(l => !l.Cantidad.EsPositiva))
            throw new ArgumentException("Indica qué se devuelve y cuánto.", nameof(lineas));

        var devolucion = new Devolucion
        {
            VentaId = venta.Id,
            SucursalId = venta.SucursalId,
            CajaId = cajaId,
            TurnoId = turnoId,
            UsuarioId = usuarioId,
            Motivo = motivo.Trim(),
            FechaUtc = ahoraUtc,
        };
        foreach (var pedida in lineas.GroupBy(l => l.VentaLineaId))
        {
            var linea = venta.Lineas.SingleOrDefault(l => l.Id == pedida.Key)
                ?? throw new ArgumentException("Esa línea no es de este ticket.", nameof(lineas));
            var cantidad = new Cantidad(pedida.Sum(p => p.Cantidad.Milesimas));
            if (cantidad > linea.CantidadPorDevolver)
                throw new InvalidOperationException($"De «{linea.Descripcion}» solo quedan {linea.CantidadPorDevolver} por devolver.");
            devolucion.Lineas.Add(new DevolucionLinea
            {
                DevolucionId = devolucion.Id,
                VentaLineaId = linea.Id,
                Cantidad = cantidad,
                ImporteCentavos = Proporcion(linea.ImporteCentavos, cantidad, linea.Cantidad),
                CostoCentavos = Proporcion(linea.CostoCentavos, cantidad, linea.Cantidad),
                RegresaAlInventario = pedida.All(p => p.RegresaAlInventario),
            });
        }
        devolucion.TotalCentavos = devolucion.Lineas.Sum(l => l.ImporteCentavos);
        var impresiones = devolucion.Lineas.Where(d => venta.Lineas.Single(l => l.Id == d.VentaLineaId).EsImpresiones).Sum(d => d.ImporteCentavos);
        var completa = venta.Lineas.All(l => l.CantidadDevuelta + devolucion.Lineas.Where(d => d.VentaLineaId == l.Id)
            .Aggregate(Cantidad.Cero, (suma, d) => suma + d.Cantidad) >= l.Cantidad);
        devolucion.ReembolsoEfectivoCentavos = ReembolsoEnEfectivo(ResumenReembolsos.De(venta, anteriores ?? []), devolucion.TotalCentavos, impresiones, completa);
        devolucion.RedondeoCentavos = devolucion.ReembolsoEfectivoCentavos - devolucion.TotalCentavos;
        return devolucion;
    }

    /// <summary>
    /// Lo que sale en efectivo, redondeado al peso y acumulado por ticket: lo devuelto hasta ahora (sin impresiones,
    /// que van exactas) al peso más cercano, menos lo que ya se reembolsó. Al devolver lo último se completa lo que pagó
    /// el cliente, en pesos (<see cref="ResumenReembolsos.TopeCentavos"/>). Así no salen centavos sueltos, partir la
    /// devolución no gana nada y la suma de los reembolsos es lo cobrado; si una parte se pagó con tarjeta, lo cobrado
    /// al peso (la diferencia queda en el redondeo de la devolución).
    /// </summary>
    public static long ReembolsoEnEfectivo(ResumenReembolsos antes, long totalCentavos, long impresionesCentavos, bool completaElTicket)
    {
        var mercancia = antes.DevueltoCentavos - antes.ImpresionesDevueltasCentavos + totalCentavos - impresionesCentavos;
        var impresiones = antes.ImpresionesDevueltasCentavos + impresionesCentavos;
        var objetivo = completaElTicket ? antes.TopeCentavos : Math.Min(Redondeo.AlPeso(mercancia) + impresiones, antes.TopeCentavos);
        return Math.Max(0, objetivo - antes.ReembolsadoCentavos);
    }

    /// <summary>Cancelación: todo lo que queda por devolver; se reembolsa en efectivo solo lo que se pagó en efectivo.</summary>
    public static Devolucion ArmarCancelacion(Venta venta, Guid turnoId, Guid usuarioId, string motivo, DateTime ahoraUtc)
    {
        if (venta.Estado != EstadoVenta.Pagada)
            throw new InvalidOperationException("Solo se cancela un ticket cobrado.");
        if (venta.TurnoId != turnoId)
            throw new InvalidOperationException("Solo se cancela en el mismo turno en que se cobró; después, es devolución.");
        if (venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva))
            throw new InvalidOperationException("El ticket ya tiene devoluciones: devuelve lo que falta en lugar de cancelar.");

        var devolucion = Armar(venta, venta.Lineas.Select(l => new LineaPorDevolver(l.Id, l.Cantidad)).ToList(), venta.CajaId, turnoId, usuarioId, motivo, ahoraUtc);
        devolucion.EsCancelacion = true;
        // Sale lo que entró en efectivo, ya redondeado: el redondeo de la venta se revierte completo.
        devolucion.ReembolsoEfectivoCentavos = venta.Cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos);
        devolucion.RedondeoCentavos = venta.RedondeoCentavos;
        return devolucion;
    }

    /// <summary>Anota en la venta lo devuelto (y la cancela si es cancelación).</summary>
    public static void Aplicar(Venta venta, Devolucion devolucion)
    {
        foreach (var d in devolucion.Lineas)
        {
            var linea = venta.Lineas.Single(l => l.Id == d.VentaLineaId);
            linea.CantidadDevuelta += d.Cantidad;
        }
        if (devolucion.EsCancelacion)
            venta.Cancelar(devolucion.UsuarioId, devolucion.Motivo, devolucion.FechaUtc);
    }

    private static long Proporcion(long total, Cantidad parte, Cantidad todo) =>
        parte == todo ? total : Redondeo.Dividir((Int128)total * parte.Milesimas, todo.Milesimas);
}
