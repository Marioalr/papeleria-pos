using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Turnos;

public enum TipoCorte
{
    /// <summary>Parcial: informativo, no cierra el turno.</summary>
    X,
    /// <summary>
    /// Cierre (el corte del turno): quien tiene el permiso cuenta a ciegas, escribe el total y el turno se cierra.
    /// </summary>
    Z,
}

public class Corte
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid TurnoId { get; set; }
    public TipoCorte Tipo { get; set; }
    public DateTime FechaUtc { get; set; }

    /// <summary>Corte Z que hizo la caja sin conexión; el servicio lo recalcula con lo que llegó en la bandeja.</summary>
    public bool EnContingencia { get; set; }
    public Guid RealizadoPorId { get; set; }

    public int Ventas { get; set; }
    public long FondoCentavos { get; set; }
    public long EfectivoVentasCentavos { get; set; }
    public long EntradasCentavos { get; set; }
    public long SalidasCentavos { get; set; }
    public long TarjetaCentavos { get; set; }
    public long TransferenciaCentavos { get; set; }

    /// <summary>Efectivo que salió de la caja por cancelaciones y devoluciones.</summary>
    public long DevolucionesCentavos { get; set; }

    public int Cancelaciones { get; set; }

    /// <summary>
    /// El redondeo al peso del turno (fase 3): lo de las ventas menos lo de los reembolsos. Informativo: ya viene dentro
    /// del efectivo de ventas y de los reembolsos, así que no se suma otra vez al esperado.
    /// </summary>
    public long RedondeoCentavos { get; set; }

    /// <summary>Lo fiado en las ventas del turno (fase 3, entrega 2): ya cuenta como venta y no es dinero. Informativo.</summary>
    public long CreditoCentavos { get; set; }

    /// <summary>Cuántos abonos de clientes se recibieron en el turno (sin los cancelados).</summary>
    public int Abonos { get; set; }

    /// <summary>Lo que entró al cajón por abonos en efectivo (con su redondeo): sí suma al esperado.</summary>
    public long AbonosEfectivoCentavos { get; set; }

    /// <summary>Abonos con tarjeta y por transferencia: aparte, como los de las ventas; no son venta.</summary>
    public long AbonosTarjetaCentavos { get; set; }

    public long AbonosTransferenciaCentavos { get; set; }

    /// <summary>El saldo a favor que se les devolvió en efectivo a los clientes (parte 3, con su redondeo): sale del esperado.</summary>
    public long SaldoDevueltoCentavos { get; set; }

    /// <summary>Fondo + efectivo de ventas + abonos en efectivo + entradas − salidas − devoluciones − saldo devuelto.</summary>
    public long EsperadoCentavos { get; set; }

    /// <summary>El total del efectivo que se contó (solo en el corte Z).</summary>
    public long? ContadoCentavos { get; set; }

    /// <summary>Contado − esperado: negativo es faltante, positivo sobrante.</summary>
    public long? DiferenciaCentavos { get; set; }

    /// <summary>
    /// Billete por billete, como se contaba antes del 2026-10-14. Ya no se llena: el corte del turno es con el total
    /// (docs/propuesta-interfaz.md, «Corte»). La tabla se queda con los conteos de los cortes de antes.
    /// </summary>
    public List<CorteConteo> Conteo { get; set; } = [];
}

/// <summary>Cuántas piezas de cada billete o moneda se contaron en un corte de antes del 2026-10-14.</summary>
public class CorteConteo
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid CorteId { get; set; }
    public long DenominacionCentavos { get; set; }
    public int Cantidad { get; set; }
}

public static class Denominaciones
{
    /// <summary>Billetes y monedas de México, de mayor a menor.</summary>
    public static readonly long[] Mexico = [100000, 50000, 20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50];

    /// <summary>
    /// El total de un conteo billete por billete: lo que sube en la bandeja una caja de antes del 2026-10-14 que hizo el
    /// corte sin conexión. Se acepta, para que nada se atore en la bandeja, y se guarda solo el total.
    /// </summary>
    public static long Total(IReadOnlyDictionary<long, int> conteo)
    {
        if (conteo.Any(c => !Mexico.Contains(c.Key) || c.Value < 0))
            throw new ArgumentException("Conteo con una denominación que no existe o una cantidad negativa.");
        return conteo.Sum(c => c.Key * c.Value);
    }
}

public static class CalculoCorte
{
    /// <summary>
    /// Arma el corte de un turno con sus ventas pagadas, sus movimientos de efectivo, los abonos de clientes que se
    /// recibieron en él y el saldo a favor que se devolvió. Con lo contado (corte Z) calcula la diferencia; el que cuenta
    /// nunca ve lo esperado antes de terminar.
    /// </summary>
    /// <param name="contadoCentavos">En el corte Z, el total del efectivo que se contó (2026-10-14: ya no billete por billete).</param>
    public static Corte Calcular(
        Turno turno, IEnumerable<Venta> ventas, IEnumerable<MovimientoCaja> movimientos, TipoCorte tipo, Guid realizadoPorId,
        DateTime ahoraUtc, long? contadoCentavos = null, IEnumerable<Devolucion>? devoluciones = null,
        IEnumerable<Abono>? abonos = null)
    {
        // El efectivo de un ticket que después se canceló sí entró a la caja; su reembolso se resta aparte.
        var cobradas = ventas.Where(v => v.TurnoId == turno.Id && v.Estado is EstadoVenta.Pagada or EstadoVenta.Cancelada).ToList();
        var vigentes = cobradas.Where(v => v.Estado == EstadoVenta.Pagada).ToList();
        var cobros = vigentes.SelectMany(v => v.Cobros).ToList();
        var reembolsos = (devoluciones ?? []).Where(d => d.TurnoId == turno.Id).ToList();
        var delTurno = movimientos.Where(m => m.TurnoId == turno.Id).ToList();
        var deClientes = (abonos ?? []).Where(a => a.TurnoId == turno.Id && a.CanceladoUtc is null).ToList();
        var abonosDelTurno = deClientes.Where(a => a.Tipo == TipoAbono.Abono).ToList();
        var devueltos = deClientes.Where(a => a.Tipo == TipoAbono.SaldoDevuelto).ToList();

        var corte = new Corte
        {
            TurnoId = turno.Id,
            Tipo = tipo,
            FechaUtc = ahoraUtc,
            RealizadoPorId = realizadoPorId,
            Ventas = vigentes.Count,
            Cancelaciones = cobradas.Count - vigentes.Count,
            FondoCentavos = turno.FondoCentavos,
            EfectivoVentasCentavos = cobradas.SelectMany(v => v.Cobros).Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos),
            TarjetaCentavos = cobros.Where(c => c.Forma == FormaPago.Tarjeta).Sum(c => c.MontoCentavos),
            TransferenciaCentavos = cobros.Where(c => c.Forma == FormaPago.Transferencia).Sum(c => c.MontoCentavos),
            EntradasCentavos = delTurno.Where(m => m.Tipo == TipoMovimientoCaja.Entrada).Sum(m => m.MontoCentavos),
            SalidasCentavos = delTurno.Where(m => m.Tipo == TipoMovimientoCaja.Salida).Sum(m => m.MontoCentavos),
            DevolucionesCentavos = reembolsos.Sum(d => d.ReembolsoEfectivoCentavos),
            RedondeoCentavos = cobradas.Sum(v => v.RedondeoCentavos) - reembolsos.Sum(d => d.RedondeoCentavos)
                + abonosDelTurno.Sum(a => a.RedondeoCentavos) - devueltos.Sum(a => a.RedondeoCentavos),
            // Lo fiado de una venta que se canceló deja de contar, como la tarjeta.
            CreditoCentavos = cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos),
            Abonos = abonosDelTurno.Count,
            AbonosEfectivoCentavos = abonosDelTurno.Where(a => a.Forma == FormaPago.Efectivo).Sum(a => a.CobradoCentavos),
            AbonosTarjetaCentavos = abonosDelTurno.Where(a => a.Forma == FormaPago.Tarjeta).Sum(a => a.MontoCentavos),
            AbonosTransferenciaCentavos = abonosDelTurno.Where(a => a.Forma == FormaPago.Transferencia).Sum(a => a.MontoCentavos),
            SaldoDevueltoCentavos = devueltos.Sum(a => a.CobradoCentavos),
        };
        corte.EsperadoCentavos = corte.FondoCentavos + corte.EfectivoVentasCentavos + corte.AbonosEfectivoCentavos + corte.EntradasCentavos
            - corte.SalidasCentavos - corte.DevolucionesCentavos - corte.SaldoDevueltoCentavos;

        if (tipo == TipoCorte.Z)
        {
            if (contadoCentavos is not { } contado)
                throw new ArgumentException("El corte del turno necesita el efectivo que se contó.");
            if (contado < 0)
                throw new ArgumentException("El efectivo contado no puede ser negativo.");
            corte.ContadoCentavos = contado;
            corte.DiferenciaCentavos = contado - corte.EsperadoCentavos;
        }
        return corte;
    }
}
