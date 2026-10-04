namespace PapeleriaPos.Nucleo.Control;

public enum ResultadoBitacora
{
    /// <summary>Permitido por la regla y hecho.</summary>
    Hecho,
    /// <summary>Requería autorización, se autorizó y se hizo.</summary>
    Autorizado,
    /// <summary>Alguien intentó autorizar sin poder hacerlo (PIN incorrecto o sin permiso).</summary>
    Negado,
}

/// <summary>Registro de lo sensible: quién, qué, cuánto, cuándo y quién lo autorizó. Solo se agregan registros.</summary>
public class Bitacora
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid? CajaId { get; init; }
    public Guid? UsuarioId { get; init; }
    public AccionControlada Accion { get; init; }
    public required string Detalle { get; init; }
    public long? MontoCentavos { get; init; }
    public Guid? VentaId { get; init; }
    public Guid? AutorizoId { get; init; }
    public ResultadoBitacora Resultado { get; init; }
    public DateTime FechaUtc { get; init; }

    /// <summary>Pasó en una caja sin conexión (con la copia local de reglas y PIN).</summary>
    public bool EnContingencia { get; init; }
}

/// <summary>
/// Permiso de un encargado (o del dueño) para una acción, una sola vez, por un monto máximo y por poco tiempo: no deja
/// la caja "abierta" para lo que sigue.
/// </summary>
public class Autorizacion
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(2);

    /// <summary>El cambio de precio se autoriza al capturar y se gasta al cobrar: le da tiempo a terminar la venta.</summary>
    public static readonly TimeSpan VigenciaEnVenta = TimeSpan.FromMinutes(30);

    public static TimeSpan VigenciaDe(AccionControlada accion) => accion == AccionControlada.CambiarPrecio ? VigenciaEnVenta : Vigencia;

    public Guid Id { get; init; } = Ids.Nuevo();
    public AccionControlada Accion { get; init; }
    public Guid SucursalId { get; init; }
    public Guid? CajaId { get; init; }
    public Guid SolicitanteId { get; init; }
    public Guid AutorizadorId { get; init; }
    public long? MontoCentavos { get; init; }
    public required string Detalle { get; init; }
    public DateTime CreadaUtc { get; init; }
    public DateTime VenceUtc { get; init; }
    public DateTime? UsadaUtc { get; private set; }

    /// <summary>La dio el dueño desde Telegram (no con su PIN en la caja).</summary>
    public bool PorTelegram { get; init; }

    /// <summary>La gasta en esta acción; falla si es de otra acción o sucursal, ya se usó, venció o el monto es mayor.</summary>
    public void Usar(AccionControlada accion, Guid sucursalId, long? montoCentavos, DateTime ahoraUtc)
    {
        if (accion != Accion || sucursalId != SucursalId)
            throw new InvalidOperationException("La autorización es para otra cosa.");
        if (UsadaUtc is not null)
            throw new InvalidOperationException("La autorización ya se usó.");
        if (ahoraUtc > VenceUtc)
            throw new InvalidOperationException("La autorización venció: pídela de nuevo.");
        if (montoCentavos > MontoCentavos)
            throw new InvalidOperationException("El monto es mayor al que se autorizó.");
        UsadaUtc = ahoraUtc;
    }
}

/// <summary>La acción necesita una autorización (o no se puede hacer). Se le muestra al cajero tal cual.</summary>
public sealed class PermisoRequerido(AccionControlada accion, NivelPermiso nivel, string mensaje) : InvalidOperationException(mensaje)
{
    public AccionControlada Accion { get; } = accion;
    public NivelPermiso Nivel { get; } = nivel;
}
