namespace PapeleriaPos.Nucleo.Clientes;

public enum TipoMovimientoCredito
{
    /// <summary>Lo fiado en una venta (su Id es el de la venta).</summary>
    Cargo,

    /// <summary>Lo que abonó (su Id es el del abono).</summary>
    Abono,

    /// <summary>Lo devuelto de una venta fiada que bajó la cuenta (su Id es el de la devolución).</summary>
    Devolucion,

    /// <summary>La cancelación de una venta fiada: se quita lo fiado (su Id es el de la cancelación).</summary>
    Cancelacion,

    /// <summary>Se canceló un abono en su turno: vuelve a deberse.</summary>
    AbonoCancelado,

    /// <summary>La transferencia de un abono no llegó: vuelve a deberse.</summary>
    AbonoRechazado,

    /// <summary>Un ajuste del dueño: el saldo inicial de la libreta o una corrección.</summary>
    Ajuste,
}

/// <summary>
/// Un renglón de la cuenta de un cliente (docs/fase-3-precios-clientes.md, entrega 2). La cuenta es un libro de solo
/// agregar: el saldo siempre es la suma de sus movimientos (+ debe más, − debe menos) y nunca se guarda aparte. Cada
/// movimiento lleva el Id de lo que lo originó, así nada se cuenta dos veces.
/// </summary>
public class MovimientoCredito
{
    /// <summary>El de la venta (cargo), el del abono o el de la devolución; los demás, uno nuevo.</summary>
    public Guid Id { get; init; }

    public Guid ClienteId { get; init; }
    public Guid SucursalId { get; init; }
    public Guid? CajaId { get; init; }

    /// <summary>Informativo: el corte se arma con los abonos, no con esto.</summary>
    public Guid? TurnoId { get; init; }

    public Guid UsuarioId { get; init; }
    public TipoMovimientoCredito Tipo { get; init; }

    /// <summary>Nunca cero: positivo, debe más; negativo, debe menos.</summary>
    public long MontoCentavos { get; init; }

    /// <summary>Cuándo pasó; sin conexión, la hora de la caja.</summary>
    public DateTime FechaUtc { get; init; }

    /// <summary>En un cargo (y un ajuste que sube la cuenta): el último día para pagarlo a tiempo.</summary>
    public DateOnly? Vence { get; init; }

    public Guid? VentaId { get; init; }
    public Guid? DevolucionId { get; init; }
    public Guid? AbonoId { get; init; }

    /// <summary>El saldo que se le dijo al cliente con este movimiento (el ticket, el comprobante): así se reimprime igual.</summary>
    public long SaldoCentavos { get; init; }

    /// <summary>El motivo de un ajuste, de una cancelación o de un rechazo.</summary>
    public string? Motivo { get; init; }

    public bool EnContingencia { get; init; }
}

public enum MotivoRevisionCredito
{
    /// <summary>Lo fiado sin conexión pasó el límite más de lo que se autorizó en la caja.</summary>
    PasoLimite,

    /// <summary>Se le fió sin conexión a un cliente que ya no tenía crédito o ya no estaba activo.</summary>
    SinCredito,

    /// <summary>Un abono sin conexión dejó la cuenta a favor del cliente.</summary>
    SaldoAFavor,

    /// <summary>Un abono sin conexión llegó cuando su turno ya tenía corte: no entró a su corte.</summary>
    FueraDeSuCorte,
}

/// <summary>Lo que no cuadró al subir lo de una caja sin conexión. El servicio lo acepta siempre; aquí queda para revisarlo.</summary>
public class RevisionCredito
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid ClienteId { get; set; }
    public Guid MovimientoId { get; set; }
    public MotivoRevisionCredito Motivo { get; set; }
    public required string Detalle { get; set; }
    public long MontoCentavos { get; set; }
    public DateTime CreadaUtc { get; set; }
    public DateTime? RevisadaUtc { get; set; }
    public Guid? RevisoId { get; set; }
    public string? Nota { get; set; }
}

/// <summary>Un cargo con lo que le queda por pagar después de aplicar lo pagado a lo más viejo.</summary>
/// <param name="DevueltoCentavos">Lo que se descontó del cargo porque se devolvió mercancía de esa misma venta.</param>
/// <param name="LiquidadoUtc">Cuando quedó en cero (para medir qué tan puntual paga).</param>
public sealed record CargoAplicado(Guid MovimientoId, Guid? VentaId, DateTime FechaUtc, DateOnly Vence, long MontoCentavos,
    long PendienteCentavos, long DevueltoCentavos, DateTime? LiquidadoUtc);

/// <summary>La cuenta con lo pagado aplicado: el saldo, lo que tiene a favor y cada cargo con lo que le falta.</summary>
public sealed record CuentaAplicada(long SaldoCentavos, long AFavorCentavos, IReadOnlyList<CargoAplicado> Cargos)
{
    /// <summary>Lo pendiente de los cargos cuyo último día ya pasó.</summary>
    public long Vencido(DateOnly hoy) => Cargos.Where(c => c.Vence < hoy).Sum(c => c.PendienteCentavos);

    /// <summary>El vencimiento más próximo de lo que se debe.</summary>
    public DateOnly? VenceMasAntiguo => Cargos.Where(c => c.PendienteCentavos > 0).Min(c => (DateOnly?)c.Vence);
}

/// <summary>Las reglas de la cuenta de un cliente: cuándo vence, cuánto pasa del límite y a qué se aplica lo pagado.</summary>
public static class CuentaCredito
{
    /// <summary>El último día para pagar a tiempo: la fecha (de México) del cargo más el plazo. Al día siguiente está vencido.</summary>
    public static DateOnly Vence(DateTime fechaUtc, int plazoDias) => DateOnly.FromDateTime(HoraMexico.ALocal(fechaUtc)).AddDays(plazoDias);

    /// <summary>
    /// Cuánto de lo que se va a fiar pasa del límite. Los abonos por transferencia sin verificar ya bajaron el saldo, pero
    /// no dan crédito hasta que se verifican. Con saldo a favor y sin crédito, se puede usar lo que tiene a favor.
    /// </summary>
    public static long Exceso(long saldoCentavos, long porVerificarCentavos, long creditoCentavos, long limiteCentavos) =>
        Math.Max(0, Math.Min(creditoCentavos, saldoCentavos + porVerificarCentavos + creditoCentavos - limiteCentavos));

    /// <summary>
    /// Aplica lo pagado a lo más viejo, en orden (no se guarda: sale de los movimientos). Un abono rechazado o cancelado
    /// no cuenta, ni su contrapartida. Lo devuelto de una venta va primero a su propio cargo. Lo que sobra queda a favor y
    /// se lo come el siguiente cargo. Siempre: Σ pendientes − a favor = saldo.
    /// </summary>
    public static CuentaAplicada Aplicar(IEnumerable<MovimientoCredito> movimientos)
    {
        var lista = movimientos.ToList();
        var anulados = lista.Where(m => m.Tipo is TipoMovimientoCredito.AbonoCancelado or TipoMovimientoCredito.AbonoRechazado && m.AbonoId is not null)
            .Select(m => m.AbonoId!.Value).ToHashSet();
        var vivos = lista
            .Where(m => !(m.AbonoId is { } abono && anulados.Contains(abono)
                && m.Tipo is TipoMovimientoCredito.Abono or TipoMovimientoCredito.AbonoCancelado or TipoMovimientoCredito.AbonoRechazado))
            .OrderBy(m => m.FechaUtc).ThenBy(m => m.Id)
            .ToList();

        var cargos = new List<Pendiente>();
        long aFavor = 0;
        foreach (var m in vivos)
        {
            if (m.MontoCentavos > 0)
            {
                var cargo = new Pendiente(m, m.Vence ?? DateOnly.FromDateTime(HoraMexico.ALocal(m.FechaUtc)));
                var usado = Math.Min(aFavor, cargo.Restante);
                aFavor -= usado;
                cargo.Pagar(usado, m.FechaUtc);
                cargos.Add(cargo);
                continue;
            }
            var resto = -m.MontoCentavos;
            if (m.Tipo is TipoMovimientoCredito.Devolucion or TipoMovimientoCredito.Cancelacion
                && m.VentaId is { } venta && cargos.FirstOrDefault(c => c.Movimiento.VentaId == venta) is { } propio)
                resto -= propio.Devolver(resto, m.FechaUtc);
            foreach (var cargo in cargos.Where(c => c.Restante > 0))
            {
                if (resto == 0)
                    break;
                resto -= cargo.Pagar(resto, m.FechaUtc);
            }
            aFavor += resto;
        }

        var aplicados = cargos.Select(c => new CargoAplicado(c.Movimiento.Id, c.Movimiento.VentaId, c.Movimiento.FechaUtc, c.Vence,
            c.Movimiento.MontoCentavos, c.Restante, c.Devuelto, c.LiquidadoUtc)).ToList();
        return new CuentaAplicada(aplicados.Sum(c => c.PendienteCentavos) - aFavor, aFavor, aplicados);
    }

    private sealed class Pendiente(MovimientoCredito movimiento, DateOnly vence)
    {
        public MovimientoCredito Movimiento { get; } = movimiento;
        public DateOnly Vence { get; } = vence;
        public long Restante { get; private set; } = movimiento.MontoCentavos;
        public long Devuelto { get; private set; }
        public DateTime? LiquidadoUtc { get; private set; }

        /// <summary>Paga hasta <paramref name="maximo"/>; devuelve cuánto tomó.</summary>
        public long Pagar(long maximo, DateTime fechaUtc)
        {
            var tomado = Math.Min(maximo, Restante);
            Restante -= tomado;
            if (Restante == 0)
                LiquidadoUtc ??= fechaUtc;
            return tomado;
        }

        public long Devolver(long maximo, DateTime fechaUtc)
        {
            var tomado = Pagar(maximo, fechaUtc);
            Devuelto += tomado;
            return tomado;
        }
    }
}
