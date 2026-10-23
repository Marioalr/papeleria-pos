using System.Security.Cryptography;
using System.Text;

namespace PapeleriaPos.Nucleo.Turnos;

/// <summary>Una caja de la sucursal. Cada una tiene su serie de folios (A-000123).</summary>
public class Caja : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public required string Nombre { get; set; }

    /// <summary>Única por sucursal: A, B, C…</summary>
    public required string Serie { get; set; }

    public bool EsPrincipal { get; set; }
    public bool Activa { get; set; } = true;

    /// <summary>Huella de la llave del equipo dado de alta como esta caja. Vacía: ningún equipo la usa.</summary>
    public string? LlaveHuella { get; private set; }

    /// <summary>Nombre de la PC dada de alta (para reconocerla en la lista).</summary>
    public string? Equipo { get; private set; }

    public DateTime? AltaUtc { get; private set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Da de alta un equipo como esta caja; el que tenía antes deja de servir. Devuelve la llave, que solo ve el equipo.</summary>
    public string DarDeAlta(string equipo, DateTime ahoraUtc)
    {
        var llave = LlaveCaja.Nueva();
        LlaveHuella = LlaveCaja.Huella(llave);
        Equipo = string.IsNullOrWhiteSpace(equipo) ? null : equipo.Trim();
        AltaUtc = ahoraUtc;
        return llave;
    }

    /// <summary>El equipo deja de poder usar el servicio; la caja, sus ventas y su serie se quedan.</summary>
    public void QuitarEquipo()
    {
        LlaveHuella = null;
        Equipo = null;
        AltaUtc = null;
    }

    public bool TieneEquipo => LlaveHuella is not null;
}

/// <summary>
/// La llave con que una caja se identifica ante el servicio. El servicio solo guarda su huella (SHA-256): con la base
/// en la mano no se puede hacer pasar por una caja.
/// </summary>
public static class LlaveCaja
{
    public static string Nueva() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Huella(string llave) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(llave)));
}

/// <summary>El tiempo de un cajero en una caja, desde que cuenta el fondo hasta el corte Z del encargado.</summary>
public class Turno
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid CajaId { get; set; }
    public Guid CajeroId { get; set; }
    public DateTime AbiertoUtc { get; set; }

    /// <summary>No siempre es el mismo: se cuenta al abrir.</summary>
    public long FondoCentavos { get; set; }

    public DateTime? CerradoUtc { get; set; }

    /// <summary>Abierto en una caja sin conexión con la caja principal.</summary>
    public bool AbiertoEnContingencia { get; set; }

    public bool Abierto => CerradoUtc is null;
}

public enum TipoMovimientoCaja
{
    /// <summary>F7: entra efectivo que no es venta (cambio que se trae, un préstamo).</summary>
    Entrada,
    /// <summary>F8: sale efectivo (retiro, gasto, pago a un proveedor).</summary>
    Salida,
}

public class MovimientoCaja
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid TurnoId { get; set; }
    public TipoMovimientoCaja Tipo { get; set; }

    /// <summary>Siempre positivo; el tipo dice si entra o sale.</summary>
    public long MontoCentavos { get; set; }

    public required string Motivo { get; set; }
    public Guid UsuarioId { get; set; }
    public DateTime FechaUtc { get; set; }

    /// <summary>Hecho en una caja sin conexión con la caja principal; llegó después con la bandeja.</summary>
    public bool EnContingencia { get; set; }

    /// <summary>
    /// Cuándo llegó con la bandeja, si su turno ya tenía corte Z: no entró a ese corte (que no se cambia) y queda para
    /// revisarlo. El efectivo ya entró o salió del cajón, así que no se rechaza.
    /// </summary>
    public DateTime? FueraDeCorteUtc { get; set; }

    /// <summary>
    /// Cuándo se canceló, si era una equivocada (2026-10-22; antes se compensaba con la contraria): el corte ya no la cuenta y
    /// dice que se canceló. Solo en su mismo turno, antes de su corte.
    /// </summary>
    public DateTime? CanceladoUtc { get; private set; }

    public Guid? CanceladoPorId { get; private set; }
    public string? MotivoCancelacion { get; private set; }

    /// <summary>
    /// El Id de la solicitud que la canceló (la da la caja): si la misma llega otra vez porque se perdió la respuesta, ya está
    /// hecha y no es un error (revisión del PR #133).
    /// </summary>
    public Guid? CancelacionId { get; private set; }

    public bool Cancelado => CanceladoUtc is not null;

    /// <summary>La cancela, con motivo. El permiso, el turno y el piloto los revisa quien la guarda.</summary>
    /// <param name="cancelacionId">El de la solicitud de la caja, para reconocerla si llega otra vez.</param>
    public void Cancelar(Guid usuarioId, string motivo, DateTime ahoraUtc, Guid? cancelacionId = null)
    {
        if (Cancelado)
            throw new InvalidOperationException($"Esa {(Tipo == TipoMovimientoCaja.Entrada ? "entrada" : "salida")} ya se canceló.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("La cancelación lleva motivo.");
        (CanceladoUtc, CanceladoPorId, MotivoCancelacion, CancelacionId) = (ahoraUtc, usuarioId, motivo.Trim(), cancelacionId);
    }
}

public enum MotivoApertura
{
    Venta,
    SinVenta,

    /// <summary>Un abono a la cuenta de un cliente en efectivo (o su cancelación), o su saldo a favor que se le devuelve.</summary>
    Abono,
}

/// <summary>Cada vez que se abre el cajón: con una venta o sin ella.</summary>
public class AperturaCajon
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid CajaId { get; set; }
    public Guid? TurnoId { get; set; }
    public Guid UsuarioId { get; set; }
    public MotivoApertura Motivo { get; set; }
    public Guid? VentaId { get; set; }
    public string? Nota { get; set; }
    public DateTime FechaUtc { get; set; }
}
