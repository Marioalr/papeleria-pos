namespace PapeleriaPos.Nucleo.Catalogo;

public class ProveedorVariante : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid ProveedorId { get; set; }
    public Proveedor? Proveedor { get; set; }

    public Guid VarianteId { get; set; }
    public Variante? Variante { get; set; }

    /// <summary>La clave con la que el proveedor conoce el producto.</summary>
    public string? CodigoProveedor { get; set; }

    /// <summary>Por unidad base.</summary>
    public long? UltimoCostoCentavos { get; set; }

    public DateTime? UltimaCompraUtc { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
