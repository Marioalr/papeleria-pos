namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>Ajustes de operación de una sucursal que configura el dueño.</summary>
public class ConfiguracionSucursal : IVersionado
{
    public Guid SucursalId { get; set; }

    /// <summary>Al llegar a este efectivo en una caja, se pide hacer un retiro. Vacío: sin límite.</summary>
    public long? LimiteEfectivoCentavos { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
