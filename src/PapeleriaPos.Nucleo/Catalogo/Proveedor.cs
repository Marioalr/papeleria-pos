namespace PapeleriaPos.Nucleo.Catalogo;

public class Proveedor : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public string? Telefono { get; set; }
    public string? Rfc { get; set; }
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
