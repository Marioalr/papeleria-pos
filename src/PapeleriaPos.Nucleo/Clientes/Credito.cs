using PapeleriaPos.Nucleo.Contratos;

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

    /// <summary>
    /// Se le devolvió en efectivo lo que tenía a favor (parte 3): debe más, hasta cero. Su Id es el de la devolución, que
    /// se guarda como un <see cref="Abono"/> de tipo <see cref="TipoAbono.SaldoDevuelto"/>.
    /// </summary>
    SaldoDevuelto,
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

/// <summary>Lo que una caja hizo con la cuenta de un cliente y el servicio todavía no le confirma: + lo que fió, − lo que abonó.</summary>
/// <param name="Vence">De lo fiado: el último día para pagarlo.</param>
/// <param name="PorVerificar">Un abono por transferencia: baja el saldo pero no da crédito hasta que se verifica.</param>
public sealed record CreditoLocal(long MontoCentavos, DateOnly? Vence = null, bool PorVerificar = false);

/// <summary>Lo que sabía la caja cuando fió sin conexión: con eso se imprimió el ticket y con eso se revisa al subir.</summary>
/// <param name="SaldoConocidoCentavos">Lo que la caja creía que debía antes de esta venta.</param>
/// <param name="ExcesoAutorizadoCentavos">Lo que pasó del límite con permiso (autorizado con PIN, o el encargado que puede).</param>
/// <param name="Vence">El que se imprimió (con el plazo de la copia de la caja).</param>
public sealed record FiadoSinConexion(long SaldoConocidoCentavos, long ExcesoAutorizadoCentavos, DateOnly? Vence = null);

/// <summary>Un cargo con lo que le queda por pagar después de aplicar lo pagado a lo más viejo.</summary>
/// <param name="DevueltoCentavos">Lo que se descontó del cargo porque se devolvió mercancía de esa misma venta.</param>
/// <param name="LiquidadoUtc">Cuando quedó en cero (para medir qué tan puntual paga).</param>
/// <param name="Tipo">Una compra a crédito, o lo demás que sube la cuenta (un ajuste, un saldo devuelto).</param>
public sealed record CargoAplicado(Guid MovimientoId, Guid? VentaId, DateTime FechaUtc, DateOnly Vence, long MontoCentavos,
    long PendienteCentavos, long DevueltoCentavos, DateTime? LiquidadoUtc, TipoMovimientoCredito Tipo = TipoMovimientoCredito.Cargo);

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
    /// <summary>La puntualidad se mide con las compras a crédito de estos últimos meses (y las que todavía debe).</summary>
    public const int MesesDePuntualidad = 6;

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
            c.Movimiento.MontoCentavos, c.Restante, c.Devuelto, c.LiquidadoUtc, c.Movimiento.Tipo)).ToList();
        return new CuentaAplicada(aplicados.Sum(c => c.PendienteCentavos) - aFavor, aFavor, aplicados);
    }

    /// <summary>
    /// Qué tan puntual paga (su evaluación): de las compras a crédito de los últimos <see cref="MesesDePuntualidad"/>
    /// meses (y las más viejas que todavía debe), cuántas liquidó a más tardar el día que vencían y en cuántos días, y
    /// cuántas ya vencieron sin pagarse. No cuentan los ajustes (la libreta), lo que se devolvió completo, lo que pagó su
    /// saldo a favor al fiarlo (no fue crédito) ni lo que se le perdonó con un ajuste.
    /// </summary>
    public static PuntualidadDto Puntualidad(IEnumerable<MovimientoCredito> movimientos, DateOnly hoy)
    {
        var lista = movimientos.ToList();
        var cuenta = Aplicar(lista);
        // Lo devuelto o cancelado de cada venta, todo (no solo lo que bajó de lo que le faltaba pagar).
        var devuelto = lista.Where(m => m.Tipo is TipoMovimientoCredito.Devolucion or TipoMovimientoCredito.Cancelacion && m.VentaId is not null)
            .GroupBy(m => m.VentaId!.Value).ToDictionary(g => g.Key, g => -g.Sum(m => m.MontoCentavos));
        var perdones = lista.Where(m => m.Tipo == TipoMovimientoCredito.Ajuste && m.MontoCentavos < 0).Select(m => m.FechaUtc).ToHashSet();
        var desde = hoy.AddMonths(-MesesDePuntualidad);
        var compras = cuenta.Cargos
            .Where(c => c.Tipo == TipoMovimientoCredito.Cargo
                && (c.VentaId is not { } venta || devuelto.GetValueOrDefault(venta) < c.MontoCentavos)
                && c.LiquidadoUtc != c.FechaUtc
                && !(c.LiquidadoUtc is { } liquidado && perdones.Contains(liquidado))
                && (c.PendienteCentavos > 0 || Dia(c.FechaUtc) >= desde))
            .ToList();
        var liquidadas = compras.Where(c => c.PendienteCentavos == 0 && c.LiquidadoUtc is not null)
            .Select(c => (Dias: Dia(c.LiquidadoUtc!.Value).DayNumber - Dia(c.FechaUtc).DayNumber, ATiempo: Dia(c.LiquidadoUtc.Value) <= c.Vence))
            .ToList();
        return new PuntualidadDto(compras.Count, liquidadas.Count, liquidadas.Count(l => l.ATiempo),
            liquidadas.Count == 0 ? null : (int)Math.Round(liquidadas.Average(l => l.Dias), MidpointRounding.AwayFromZero),
            compras.Count(c => c.PendienteCentavos > 0 && c.Vence < hoy));
    }

    private static DateOnly Dia(DateTime fechaUtc) => DateOnly.FromDateTime(HoraMexico.ALocal(fechaUtc));

    /// <summary>
    /// La cuenta que conoce una caja sin conexión: la última que le mandó el servicio más lo suyo sin confirmar, en el
    /// orden en que pasó. Lo fiado primero se come lo que tenga a favor y lo demás vence por su plazo; lo abonado paga lo
    /// más viejo y lo que sobra queda a favor. Lo vencido sigue siendo exacto aunque pasen días sin conexión.
    /// </summary>
    public static CreditoClienteDto Estimar(CreditoClienteDto delServicio, IEnumerable<CreditoLocal> locales)
    {
        var pendientes = delServicio.Pendientes.Select(p => (p.Vence, Pendiente: p.PendienteCentavos)).ToList();
        var (saldo, aFavor, porVerificar) = (delServicio.SaldoCentavos, delServicio.AFavorCentavos, delServicio.PorVerificarCentavos);
        foreach (var local in locales)
        {
            saldo += local.MontoCentavos;
            if (local.MontoCentavos > 0)
            {
                var usado = Math.Min(aFavor, local.MontoCentavos);
                aFavor -= usado;
                if (local.MontoCentavos > usado)
                    pendientes.Add((local.Vence ?? DateOnly.MaxValue, local.MontoCentavos - usado));
                continue;
            }
            if (local.PorVerificar)
                porVerificar -= local.MontoCentavos;
            var resto = -local.MontoCentavos;
            for (var i = 0; i < pendientes.Count && resto > 0; i++)
            {
                var tomado = Math.Min(resto, pendientes[i].Pendiente);
                pendientes[i] = (pendientes[i].Vence, pendientes[i].Pendiente - tomado);
                resto -= tomado;
            }
            aFavor += resto;
        }
        return delServicio with
        {
            SaldoCentavos = saldo, PorVerificarCentavos = porVerificar, Estimado = true,
            Pendientes = pendientes.Where(p => p.Pendiente > 0).Select(p => new CargoPendienteDto(p.Vence, p.Pendiente)).ToList(),
        };
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
