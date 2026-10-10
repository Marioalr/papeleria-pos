namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// El conteo cíclico de un día en una sucursal (docs/fase-2-inventario.md, entrega 3): los productos que eligió el POS se
/// cuentan a ciegas en la caja y la diferencia entra al kardex como movimiento de conteo. Se arma solo la primera vez
/// que se pide en el día (al abrir el turno o cuando haya calma; más adelante, el asistente inteligente).
/// </summary>
public class ConteoCiclico
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }

    /// <summary>El día en la hora de México: uno por sucursal y día.</summary>
    public DateOnly Fecha { get; init; }

    public DateTime CreadoUtc { get; init; }
    public List<ConteoCiclicoRenglon> Renglones { get; init; } = [];
}

/// <summary>Un producto del conteo del día. Una vez contado no cambia: un error se corrige con un ajuste.</summary>
public class ConteoCiclicoRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid ConteoId { get; init; }
    public int Orden { get; init; }
    public Guid VarianteId { get; init; }

    public Cantidad? Contado { get; private set; }

    /// <summary>Lo que decía el sistema al contarlo (quien cuenta no lo ve).</summary>
    public Cantidad? Esperado { get; private set; }

    /// <summary>Lo que vale la diferencia, con signo: + sobró, − faltó.</summary>
    public long? CostoDiferenciaCentavos { get; private set; }

    public Guid? ContadoPorId { get; private set; }
    public DateTime? ContadoUtc { get; private set; }

    public Cantidad? Diferencia => Contado is { } c && Esperado is { } e ? c - e : null;

    public void Contar(Cantidad contado, Cantidad esperado, long costoDiferenciaCentavos, Guid usuarioId, DateTime ahoraUtc)
    {
        if (Contado is not null)
            throw new InvalidOperationException("Ya se contó hoy: si hubo un error, se corrige con un ajuste.");
        if (contado.Milesimas < 0)
            throw new ArgumentOutOfRangeException(nameof(contado), "Lo contado no puede ser negativo.");
        (Contado, Esperado, CostoDiferenciaCentavos, ContadoPorId, ContadoUtc) = (contado, esperado, costoDiferenciaCentavos, usuarioId, ahoraUtc);
    }
}

/// <summary>
/// Qué se cuenta hoy: primero lo que tiene existencia negativa (se vendió sin haber: algo está mal), luego lo que más
/// pesa (valor del inventario más lo vendido a costo en 90 días) por los días que lleva sin contarse. Así lo caro y lo
/// que más se mueve se cuenta seguido, y lo demás también llega su turno.
/// </summary>
public static class SeleccionConteo
{
    public const int PorDia = 15;

    /// <summary>Lo que se cuenta como "nunca": un año.</summary>
    public const int DiasMaximos = 365;

    /// <param name="ValorCentavos">Lo que vale su existencia, a costo.</param>
    /// <param name="VendidoCentavos">Lo vendido en los últimos 90 días, a costo.</param>
    /// <param name="DiasSinContar">Desde su último conteo o ajuste; vacío, nunca.</param>
    public sealed record Candidato(Guid VarianteId, long ValorCentavos, long VendidoCentavos, int? DiasSinContar, bool ExistenciaNegativa);

    public static List<Guid> Elegir(IEnumerable<Candidato> candidatos, int cuantos = PorDia) =>
        candidatos
            .Select(c => (c.VarianteId, c.ExistenciaNegativa,
                Puntaje: (decimal)(Math.Abs(c.ValorCentavos) + c.VendidoCentavos) * Math.Min(c.DiasSinContar ?? DiasMaximos, DiasMaximos)))
            .Where(c => c.ExistenciaNegativa || c.Puntaje > 0)
            .OrderByDescending(c => c.ExistenciaNegativa).ThenByDescending(c => c.Puntaje).ThenBy(c => c.VarianteId)
            .Take(cuantos)
            .Select(c => c.VarianteId)
            .ToList();
}
