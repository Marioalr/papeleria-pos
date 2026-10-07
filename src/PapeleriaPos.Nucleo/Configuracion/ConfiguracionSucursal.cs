using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>Ajustes de operación de una sucursal que configura el dueño.</summary>
public class ConfiguracionSucursal : IVersionado
{
    public Guid SucursalId { get; set; }

    /// <summary>Al llegar a este efectivo en una caja, se pide hacer un retiro. Vacío: sin límite.</summary>
    public long? LimiteEfectivoCentavos { get; set; }

    // ── Alertas por Telegram (entrega 4) ──

    /// <summary>Las alertas que el dueño apagó, separadas por coma.</summary>
    public string AlertasApagadas { get; set; } = "";

    /// <summary>Faltante o sobrante en un corte a partir del cual se avisa.</summary>
    public long UmbralCorteCentavos { get; set; } = 5000;

    public long UmbralDevolucionCentavos { get; set; } = 20000;
    public long UmbralSalidaCentavos { get; set; } = 50000;
    public int MinutosSinConexion { get; set; } = 10;

    /// <summary>Horario de la tienda: un turno abierto fuera de él avisa.</summary>
    public TimeOnly HoraApertura { get; set; } = new(8, 0);

    public TimeOnly HoraCierre { get; set; } = new(21, 0);

    // ── Compras (fase 2, entrega 2) ──

    /// <summary>
    /// Debajo de este margen (centésimas: 3000 = 30 %), una compra avisa y sugiere precio. Cada departamento puede tener
    /// el suyo.
    /// </summary>
    public int MargenMinimoCentesimas { get; set; } = Catalogo.Margen.MinimoInicial;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    public bool Avisa(TipoAlerta tipo) => !Apagadas().Contains(tipo);

    public IReadOnlyList<TipoAlerta> Apagadas() =>
        AlertasApagadas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => Enum.TryParse<TipoAlerta>(a, out var t) ? (TipoAlerta?)t : null)
            .OfType<TipoAlerta>()
            .ToList();

    public void Apagar(IEnumerable<TipoAlerta> tipos) => AlertasApagadas = string.Join(",", tipos.Distinct().Order());

    /// <summary>Fuera del horario de la tienda (hora de México).</summary>
    public bool FueraDeHorario(DateTime local)
    {
        var hora = TimeOnly.FromDateTime(local);
        return hora < HoraApertura || hora > HoraCierre;
    }
}
