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

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
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
