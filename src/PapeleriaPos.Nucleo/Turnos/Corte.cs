using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Turnos;

public enum TipoCorte
{
    /// <summary>Parcial: informativo, no cierra el turno.</summary>
    X,
    /// <summary>Cierre: el encargado cuenta a ciegas y el turno se cierra.</summary>
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

    /// <summary>Fondo + efectivo de ventas + entradas − salidas − devoluciones.</summary>
    public long EsperadoCentavos { get; set; }

    /// <summary>Lo que contó el encargado (solo en el corte Z).</summary>
    public long? ContadoCentavos { get; set; }

    /// <summary>Contado − esperado: negativo es faltante, positivo sobrante.</summary>
    public long? DiferenciaCentavos { get; set; }

    public List<CorteConteo> Conteo { get; set; } = [];
}

/// <summary>Cuántas piezas de cada billete o moneda se contaron.</summary>
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
}

public static class CalculoCorte
{
    /// <summary>
    /// Arma el corte de un turno con sus ventas pagadas y movimientos de efectivo. Con conteo (corte Z) calcula la
    /// diferencia; el que cuenta nunca ve lo esperado antes de terminar.
    /// </summary>
    public static Corte Calcular(
        Turno turno, IEnumerable<Venta> ventas, IEnumerable<MovimientoCaja> movimientos, TipoCorte tipo, Guid realizadoPorId,
        DateTime ahoraUtc, IReadOnlyDictionary<long, int>? conteo = null, IEnumerable<Devolucion>? devoluciones = null)
    {
        // El efectivo de un ticket que después se canceló sí entró a la caja; su reembolso se resta aparte.
        var cobradas = ventas.Where(v => v.TurnoId == turno.Id && v.Estado is EstadoVenta.Pagada or EstadoVenta.Cancelada).ToList();
        var vigentes = cobradas.Where(v => v.Estado == EstadoVenta.Pagada).ToList();
        var cobros = vigentes.SelectMany(v => v.Cobros).ToList();
        var reembolsos = (devoluciones ?? []).Where(d => d.TurnoId == turno.Id).ToList();
        var delTurno = movimientos.Where(m => m.TurnoId == turno.Id).ToList();

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
        };
        corte.EsperadoCentavos = corte.FondoCentavos + corte.EfectivoVentasCentavos + corte.EntradasCentavos - corte.SalidasCentavos
            - corte.DevolucionesCentavos;

        if (tipo == TipoCorte.Z)
        {
            if (conteo is null)
                throw new ArgumentException("El corte Z necesita el conteo del efectivo.", nameof(conteo));
            if (conteo.Any(c => !Denominaciones.Mexico.Contains(c.Key) || c.Value < 0))
                throw new ArgumentException("Conteo con una denominación que no existe o una cantidad negativa.", nameof(conteo));

            corte.Conteo = conteo.Where(c => c.Value > 0)
                .Select(c => new CorteConteo { CorteId = corte.Id, DenominacionCentavos = c.Key, Cantidad = c.Value })
                .OrderByDescending(c => c.DenominacionCentavos)
                .ToList();
            corte.ContadoCentavos = corte.Conteo.Sum(c => c.DenominacionCentavos * c.Cantidad);
            corte.DiferenciaCentavos = corte.ContadoCentavos - corte.EsperadoCentavos;
        }
        return corte;
    }
}
