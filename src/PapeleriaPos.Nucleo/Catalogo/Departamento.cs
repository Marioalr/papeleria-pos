namespace PapeleriaPos.Nucleo.Catalogo;

public class Departamento : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public bool Activo { get; set; } = true;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
