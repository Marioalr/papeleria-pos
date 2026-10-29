namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// Dinero que regresa al cliente por una venta: una cancelación (todo el ticket, en su mismo turno) o una devolución
/// (algunos artículos, con ticket y hasta 7 días). El reembolso sale en efectivo de la caja donde se hace, redondeado al
/// peso como el cobro. De una venta fiada, lo devuelto baja primero su cuenta (<see cref="AlCreditoCentavos"/>).
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

    /// <summary>
    /// Lo devuelto que bajó la cuenta del cliente en lugar de salir de la caja (fase 3, entrega 2): de una venta fiada,
    /// lo devuelto va primero a lo fiado. No entra al corte.
    /// </summary>
    public long AlCreditoCentavos { get; set; }

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
/// reembolsó. Con esto se redondea lo que sale en efectivo sin regresar centavos sueltos (fase 3). De una venta fiada,
/// todo es sin lo fiado: lo fiado se devuelve a la cuenta, no en dinero.
/// </summary>
/// <param name="PagadoCentavos">Lo que pagó en dinero: lo cobrado menos lo fiado.</param>
/// <param name="DevueltoCentavos">Lo que se devolvió en dinero antes (sin lo que fue a la cuenta), impresiones incluidas.</param>
/// <param name="ImpresionesCentavos">Lo que llevó el ticket de impresiones pagado en dinero: eso se regresa exacto.</param>
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
        var fiado = venta.CreditoCentavos;
        // Lo fiado cubre primero las impresiones, como todo lo que no es efectivo (Venta.Efectivo).
        long ImpresionesEnDinero(Devolucion d) =>
            Math.Max(0, d.Lineas.Where(l => impresiones.Contains(l.VentaLineaId)).Sum(l => l.ImporteCentavos) - d.AlCreditoCentavos);
        return new ResumenReembolsos(venta.TotalCobradoCentavos - fiado, lista.Sum(d => Math.Max(0, d.TotalCentavos - d.AlCreditoCentavos)),
            lista.Sum(ImpresionesEnDinero), lista.Sum(d => d.ReembolsoEfectivoCentavos),
            Math.Max(0, venta.Lineas.Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos) - fiado));
    }
}

public static class CalculoDevolucion
{
    /// <summary>
    /// Si con esta devolución (antes de aplicarla) las impresiones del ticket vuelven a estar por cobrar: se cancela el ticket, o su
    /// línea de impresiones queda devuelta completa. Una parte de ella (solo puede llegar de eleventa, la vuelta) no las libera: el
    /// cliente se quedó con las impresiones y se le regresó una parte (2026-10-25, decidido, por revisar).
    /// </summary>
    public static bool LiberaImpresiones(Venta venta, Devolucion devolucion) => ImpresionesQueLibera(venta, devolucion) is (true, _) or (_, true);

    /// <summary>
    /// Cuáles se liberan (2026-10-29): las impresiones y las copias del panel van en líneas aparte, y devolver una completa solo
    /// libera lo suyo. Cancelar el ticket libera las dos. Con una sola línea de impresiones (las ventas de antes, cuando las copias
    /// iban en la misma, o un ticket con solo una de las dos), devolverla libera todo lo que se cobró con ella, sea de lo que sea.
    /// </summary>
    public static (bool Impresiones, bool Copias) ImpresionesQueLibera(Venta venta, Devolucion devolucion)
    {
        if (devolucion.EsCancelacion)
            return (true, true);
        var completas = devolucion.Lineas
            .Select(d => (Devuelta: d, Linea: venta.Lineas.SingleOrDefault(l => l.Id == d.VentaLineaId)))
            .Where(x => x.Linea is { EsImpresiones: true } linea && linea.CantidadDevuelta + x.Devuelta.Cantidad >= linea.Cantidad)
            .Select(x => x.Linea!)
            .ToList();
        if (completas.Count > 0 && venta.Lineas.Count(l => l.EsImpresiones) == 1)
            return (true, true);
        return (completas.Any(l => !l.EsCopias), completas.Any(l => l.EsCopias));
    }

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
            throw new ArgumentException("La devolución lleva motivo.");
        if (lineas.Count == 0 || lineas.Any(l => !l.Cantidad.EsPositiva))
            throw new ArgumentException("Indica qué se devuelve y cuánto.");

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
                ?? throw new ArgumentException("Esa línea no es de este ticket.");
            var cantidad = new Cantidad(pedida.Sum(p => p.Cantidad.Milesimas));
            if (cantidad > linea.CantidadPorDevolver)
                throw new InvalidOperationException($"De «{linea.Descripcion}» solo quedan {linea.CantidadPorDevolver} por devolver.");
            // Una impresión se devuelve completa (2026-10-25, decidido, por revisar): sus trabajos vuelven a estar por cobrar (los de
            // Control Print, a salir en F2). Una parte no diría cuál: si el cliente se queda con alguno, se vuelve a cobrar desde F2.
            if (linea.EsImpresiones && cantidad != linea.CantidadPorDevolver)
                throw new InvalidOperationException($"«{linea.Descripcion}» se devuelve completa: sus impresiones vuelven a estar por cobrar. "
                    + "Si el cliente se queda con alguna, cóbrala otra vez desde F2.");
            devolucion.Lineas.Add(new DevolucionLinea
            {
                DevolucionId = devolucion.Id,
                VentaLineaId = linea.Id,
                Cantidad = cantidad,
                ImporteCentavos = ParteDe(linea.ImporteCentavos, linea.CantidadDevuelta, cantidad, linea.Cantidad),
                CostoCentavos = ParteDe(linea.CostoCentavos, linea.CantidadDevuelta, cantidad, linea.Cantidad),
                RegresaAlInventario = pedida.All(p => p.RegresaAlInventario),
            });
        }
        devolucion.TotalCentavos = devolucion.Lineas.Sum(l => l.ImporteCentavos);
        var impresiones = devolucion.Lineas.Where(d => venta.Lineas.Single(l => l.Id == d.VentaLineaId).EsImpresiones).Sum(d => d.ImporteCentavos);
        var completa = venta.Lineas.All(l => l.CantidadDevuelta + devolucion.Lineas.Where(d => d.VentaLineaId == l.Id)
            .Aggregate(Cantidad.Cero, (suma, d) => suma + d.Cantidad) >= l.Cantidad);
        // De una venta fiada, lo devuelto baja primero lo que se fió (lo que queda de ello); solo el resto es dinero.
        var previas = (anteriores ?? []).Where(d => d.VentaId == venta.Id).ToList();
        var alCreditoImpresiones = AlCredito(venta, previas, impresiones, devolucion.TotalCentavos - impresiones, completa, out var alCreditoMercancia);
        devolucion.AlCreditoCentavos = alCreditoImpresiones + alCreditoMercancia;
        var enDinero = Math.Max(0, devolucion.TotalCentavos - devolucion.AlCreditoCentavos);
        devolucion.ReembolsoEfectivoCentavos = ReembolsoEnEfectivo(ResumenReembolsos.De(venta, previas), enDinero,
            Math.Max(0, impresiones - alCreditoImpresiones), completa);
        devolucion.RedondeoCentavos = devolucion.ReembolsoEfectivoCentavos - enDinero;
        return devolucion;
    }

    /// <summary>
    /// Cuánto de esta devolución baja la cuenta del cliente, con la misma regla con que se cobró: lo fiado cubrió primero
    /// las impresiones (como todo lo que no es efectivo) y el resto, la mercancía. Cada parte toma de lo suyo que quede, y
    /// la que completa el ticket toma todo lo que falta (los importes repartidos en proporción pueden sumar un centavo
    /// menos). Así lo devuelto a la cuenta suma lo fiado y lo que sale en efectivo de la mercancía va en pesos.
    /// </summary>
    /// <returns>Lo que va a la cuenta por las impresiones; en <paramref name="alCreditoMercancia"/>, lo de la mercancía.</returns>
    private static long AlCredito(Venta venta, List<Devolucion> previas, long impresiones, long mercancia, bool completa, out long alCreditoMercancia)
    {
        var fiado = venta.CreditoCentavos;
        var lineasImpresiones = venta.Lineas.Where(l => l.EsImpresiones).Select(l => l.Id).ToHashSet();
        var reservaImpresiones = Math.Min(fiado, venta.Lineas.Where(l => l.EsImpresiones).Sum(l => l.ImporteCentavos));
        long usadoImpresiones = 0, usadoMercancia = 0;
        foreach (var d in previas)
        {
            var deImpresiones = Math.Min(d.Lineas.Where(l => lineasImpresiones.Contains(l.VentaLineaId)).Sum(l => l.ImporteCentavos), d.AlCreditoCentavos);
            usadoImpresiones += deImpresiones;
            usadoMercancia += d.AlCreditoCentavos - deImpresiones;
        }
        var quedaImpresiones = Math.Max(0, reservaImpresiones - usadoImpresiones);
        var quedaMercancia = Math.Max(0, fiado - reservaImpresiones - usadoMercancia);
        alCreditoMercancia = completa ? quedaMercancia : Math.Min(mercancia, quedaMercancia);
        return completa ? quedaImpresiones : Math.Min(impresiones, quedaImpresiones);
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
        // Sale lo que entró en efectivo, ya redondeado: el redondeo de la venta se revierte completo. Lo fiado se quita
        // completo de la cuenta.
        devolucion.ReembolsoEfectivoCentavos = venta.Cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos);
        devolucion.RedondeoCentavos = venta.RedondeoCentavos;
        devolucion.AlCreditoCentavos = venta.CreditoCentavos;
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

    /// <summary>
    /// Lo que le toca a lo que se devuelve ahora, en proporción a lo vendido y redondeado sobre lo acumulado: lo de lo devuelto
    /// hasta ahora con esto, menos lo de lo devuelto antes. Así, devolver de una en una las tres piezas de un 3×2 de $20.00 da
    /// $6.67, $6.66 y $6.67 (suman lo cobrado; antes, $20.01), y lo que completa la línea toma justo lo que le falta. Devolver
    /// la línea completa de una vez da su importe, como siempre. La ventana de devoluciones calcula igual el monto que autoriza,
    /// y el puente, lo que se devuelve en eleventa de una venta de la caja 2 (la vuelta, 2026-10-24).
    /// </summary>
    /// <param name="total">El importe (o el costo) de la línea.</param>
    /// <param name="antes">Lo que ya se devolvió de la línea, en sus presentaciones.</param>
    /// <param name="parte">Lo que se devuelve ahora.</param>
    /// <param name="todo">Lo que se vendió.</param>
    public static long ParteDe(long total, Cantidad antes, Cantidad parte, Cantidad todo) =>
        Acumulado(total, antes + parte, todo) - Acumulado(total, antes, todo);

    /// <summary>
    /// La misma regla cuando lo que ya salió de la línea se sabe en dinero y pudo no salir con ella (un ticket de eleventa: lo que
    /// eleventa devolvió allá, con su redondeo): lo de lo devuelto hasta ahora con esto, menos lo que de verdad ya salió, sin
    /// pasarse de lo que le queda. Así lo devuelto suma siempre lo que le toca a lo devuelto, y lo que completa la línea es
    /// justo lo que le queda. Es la del puente con los tickets de eleventa (la ida), también en la ventana que pide la
    /// autorización (2026-10-24: antes redondeaba cada parte por su lado y dos de tres salían un centavo arriba).
    /// </summary>
    /// <param name="yaDevuelto">Lo que ya salió de la línea: lo que no le queda de <paramref name="total"/>.</param>
    public static long ParteDe(long total, Cantidad antes, Cantidad parte, Cantidad todo, long yaDevuelto) =>
        Math.Clamp(Acumulado(total, antes + parte, todo) - yaDevuelto, 0, Math.Max(0, total - yaDevuelto));

    private static long Acumulado(long total, Cantidad devuelto, Cantidad todo) =>
        devuelto >= todo ? total : Redondeo.Dividir((Int128)total * devuelto.Milesimas, todo.Milesimas);
}
