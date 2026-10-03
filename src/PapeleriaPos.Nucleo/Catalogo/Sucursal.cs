namespace PapeleriaPos.Nucleo.Catalogo;

public class Sucursal : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Clave corta para folios: M, S2.</summary>
    public required string Clave { get; set; }

    public required string Nombre { get; set; }
    public bool Activa { get; set; } = true;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
