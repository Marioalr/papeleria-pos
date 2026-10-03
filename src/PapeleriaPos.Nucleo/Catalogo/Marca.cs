namespace PapeleriaPos.Nucleo.Catalogo;

public class Marca : IVersionado
{
    private string _nombre = "";

    public Guid Id { get; set; } = Ids.Nuevo();

    public required string Nombre
    {
        get => _nombre;
        set
        {
            _nombre = value.Trim();
            NombreNormalizado = Texto.Normalizar(value);
        }
    }

    /// <summary>Único en el catálogo: "Bic", "BIC" y " bic " son la misma marca.</summary>
    public string NombreNormalizado { get; private set; } = "";

    public bool Activa { get; set; } = true;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
