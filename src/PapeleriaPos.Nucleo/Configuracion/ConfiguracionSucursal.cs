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

    /// <summary>Un ajuste o una merma con este costo o más avisa al dueño.</summary>
    public long UmbralAjusteCentavos { get; set; } = 20000;
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

    // ── Etiquetas (fase 2, entrega 4) ──

    /// <summary>La hoja de etiquetas que se compra: una de <see cref="Catalogo.FormatoEtiquetas.Comunes"/> o la propia.</summary>
    public string EtiquetasFormato { get; set; } = Catalogo.FormatoEtiquetas.PorOmision.Id;

    /// <summary>Las medidas de una hoja que no es de las comunes.</summary>
    public Catalogo.FormatoEtiquetas? EtiquetasPropio { get; set; }

    /// <summary>Cuánto mover todo para que caiga en las etiquetas (décimas de milímetro): + a la derecha, + hacia abajo.</summary>
    public int EtiquetasAjusteXDecimas { get; set; }

    public int EtiquetasAjusteYDecimas { get; set; }

    // ── Sugerido de compra (fase 2, entrega 4) ──

    /// <summary>Cada cuánto se compra: lo que tiene que alcanzar un pedido.</summary>
    public int SugeridoCoberturaDias { get; set; } = Inventario.SugeridoCompra.Parametros.PorOmision.CoberturaDias;

    /// <summary>Lo que se guarda de más por si el proveedor tarda o se vende más: el mínimo.</summary>
    public int SugeridoColchonDias { get; set; } = Inventario.SugeridoCompra.Parametros.PorOmision.ColchonDias;

    // ── Venta (rediseño, entrega 0) ──

    /// <summary>
    /// Los rápidos: botones de la venta para lo que no tiene código de barras y se vende todo el día (Alt+1 a Alt+6).
    /// Vacío: sin rápidos.
    /// </summary>
    public List<Rapido>? Rapidos { get; set; }

    /// <summary>La hoja que se usa, con sus medidas.</summary>
    public Catalogo.FormatoEtiquetas FormatoDeEtiquetas() =>
        EtiquetasFormato == Catalogo.FormatoEtiquetas.Propio && EtiquetasPropio is { } propio
            ? propio
            : Catalogo.FormatoEtiquetas.Comunes.FirstOrDefault(f => f.Id == EtiquetasFormato) ?? Catalogo.FormatoEtiquetas.PorOmision;

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
