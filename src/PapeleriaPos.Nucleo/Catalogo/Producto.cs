namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>El modelo que se ve en la búsqueda ("Pluma Bic punto fino"). Agrupa sus variantes.</summary>
public class Producto : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }

    /// <summary>Opcional: genéricos y servicios no tienen marca.</summary>
    public Guid? MarcaId { get; set; }
    public Marca? Marca { get; set; }

    public Guid DepartamentoId { get; set; }
    public Departamento? Departamento { get; set; }

    public TipoProducto Tipo { get; set; } = TipoProducto.Mercancia;

    public string? ClaveSat { get; set; }
    public string? ClaveUnidadSat { get; set; }
    public Impuesto Impuesto { get; set; } = Impuesto.Iva16;

    public bool Activo { get; set; } = true;

    public List<Variante> Variantes { get; set; } = [];

    /// <summary>Solo la mercancía lleva inventario.</summary>
    public bool LlevaInventario => Tipo == TipoProducto.Mercancia;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
