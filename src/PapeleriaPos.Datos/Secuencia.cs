namespace PapeleriaPos.Datos;

/// <summary>Contadores de la base. "catalogo" da el número de versión de cada cambio al catálogo.</summary>
public class Secuencia
{
    public const string Catalogo = "catalogo";

    public required string Nombre { get; set; }
    public long Valor { get; set; }
}
