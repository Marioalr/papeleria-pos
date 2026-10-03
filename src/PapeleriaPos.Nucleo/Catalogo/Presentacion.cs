namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Forma de venta: pieza, paquete de 12, rollo de 50 m.</summary>
public class Presentacion : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid VarianteId { get; set; }
    public Variante? Variante { get; set; }

    public required string Nombre { get; set; }

    /// <summary>Unidades base que contiene. La presentación base tiene factor de una unidad.</summary>
    public Cantidad Factor { get; set; } = Cantidad.Piezas(1);

    public bool EsBase { get; set; }

    public List<CodigoBarras> Codigos { get; set; } = [];

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
