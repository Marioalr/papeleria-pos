namespace PapeleriaPos.Nucleo.Catalogo;

public class Departamento : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public bool Activo { get; set; } = true;

    /// <summary>Margen mínimo propio del departamento (centésimas: 3000 = 30 %). Vacío: el general de la sucursal.</summary>
    public int? MargenMinimoCentesimas { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
