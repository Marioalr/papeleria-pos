namespace PapeleriaPos.Nucleo.Importacion;

/// <summary>
/// Liga cada registro importado con su origen: permite repetir una importación sin duplicar y rastrear de dónde
/// salió cada registro.
/// </summary>
public class MapeoImportacion
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>"eleventa", o "ficticios" para el generador de datos de prueba.</summary>
    public required string Origen { get; set; }

    /// <summary>"Producto", "Variante", "VentaHistorica"…</summary>
    public required string Entidad { get; set; }

    public required string IdOrigen { get; set; }
    public Guid IdNuevo { get; set; }
}
