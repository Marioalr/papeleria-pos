namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Apunta a la presentación: escanear el código del paquete vende un paquete.</summary>
public class CodigoBarras : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Único en todo el catálogo.</summary>
    public required string Codigo { get; set; }

    public Guid PresentacionId { get; set; }
    public Presentacion? Presentacion { get; set; }

    public OrigenCodigo Origen { get; set; } = OrigenCodigo.Fabrica;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
