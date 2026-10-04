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
}

public enum MotivoApertura
{
    Venta,
    SinVenta,
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
