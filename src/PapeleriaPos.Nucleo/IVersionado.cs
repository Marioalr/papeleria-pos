namespace PapeleriaPos.Nucleo;

/// <summary>
/// Registros del catálogo que después bajan del central a las sucursales por número de versión.
/// La capa de datos llena estos campos al guardar.
/// </summary>
public interface IVersionado
{
    long Version { get; set; }
    DateTime ModificadoUtc { get; set; }
}
