namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Un precio de mayoreo de eleventa (el que el cajero activaba con F11) y qué se hizo con él (docs/fase-3-precios-clientes.md,
/// entrega 1). Si la importación dedujo el paquete, llega ya resuelto y con su <see cref="Escala"/>; si no, queda por
/// revisar y el encargado lo decide en F3: desde cuántas piezas aplica, o sin mayoreo. Lo que decide se repite en la
/// importación final como decisión de depuración, y ahí cambia o quita la escala que esa importación haya deducido.
/// </summary>
public class MayoreoPorRevisar
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid VarianteId { get; init; }

    /// <summary>Los precios de eleventa, por pieza (o metro, kilo), con IVA.</summary>
    public long PrecioMayoreoCentavos { get; init; }
    public long MenudeoCentavos { get; init; }
    public long CostoCentavos { get; init; }

    /// <summary>Cuántas ventas con F11 se tomaron en cuenta (sin las del regreso a clases ni las vendidas al costo).</summary>
    public int VentasConMayoreo { get; init; }

    /// <summary>Las cantidades más vendidas con F11 y cuántas veces: "12 ×5 · 24 ×2".</summary>
    public string Cantidades { get; init; } = "";

    /// <summary>Por qué no se dedujo (pocas ventas, ninguna cantidad domina, a granel, al costo…), o que sí se dedujo.</summary>
    public required string Motivo { get; init; }

    /// <summary>Lo que sugiere la importación (el paquete más probable por sus ventas con F11), si hay.</summary>
    public Cantidad? Sugerida { get; init; }

    public DateTime? ResueltoUtc { get; private set; }
    public string? Resolucion { get; private set; }

    /// <summary>
    /// Desde cuánto va la escala de precio que hoy es este mayoreo (la deducida o la que se decidió), para que otra
    /// decisión la cambie o la quite en vez de sumar otra. Vacía si quedó sin mayoreo o es la de una promoción.
    /// </summary>
    public Cantidad? Escala { get; private set; }

    public void Resolver(string resolucion, DateTime ahoraUtc, Cantidad? escala = null) =>
        (Resolucion, ResueltoUtc, Escala) = (resolucion, ahoraUtc, escala);
}
