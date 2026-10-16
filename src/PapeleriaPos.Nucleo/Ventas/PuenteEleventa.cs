namespace PapeleriaPos.Nucleo.Ventas;

// Las tablas del puente con eleventa (docs/migracion-eleventa.md, «Lo que falta para el piloto»): solo del piloto en la
// caja 2. Lo que la caja nueva necesita recordar para convivir con eleventa vive aquí, no en sus tablas de siempre, y se
// quita al dejar eleventa con la bandeja (EnvioAEleventa) y PapeleriaPos.Eleventa (docs/plan.md, la regla de adaptación).

/// <summary>
/// El turno de eleventa que adoptó un turno de la caja nueva (eleventa sigue abierto en la caja 2 y las dos venden en el
/// mismo turno, decisión del dueño del 2026-10-12): su <c>TURNOS.ID</c> y, cuando eleventa hace su corte, lo que se contó
/// allá. Al ver ese turno cerrado en eleventa, la caja nueva cierra el suyo como «corte hecho en eleventa», sin conteo.
/// </summary>
public class TurnoEnEleventa
{
    /// <summary>El turno de la caja nueva.</summary>
    public Guid TurnoId { get; set; }

    /// <summary><c>TURNOS.ID</c> en eleventa.</summary>
    public int TurnoEleventa { get; set; }

    /// <summary>
    /// <c>TURNOS.INICIO_EN</c>: con el ID dice que sigue siendo el mismo turno (una restauración de eleventa vuelve a dar
    /// los mismos números).
    /// </summary>
    public DateTime InicioUtc { get; set; }

    public DateTime AdoptadoUtc { get; set; }

    /// <summary><c>TURNOS.TERMINO_EN</c>: cuando eleventa hizo su corte. Vacío mientras allá sigue abierto.</summary>
    public DateTime? CortadoUtc { get; set; }

    /// <summary><c>TURNOS.EFECTIVO_AL_CIERRE</c>: lo que se contó en eleventa, el cajón de las dos.</summary>
    public long? ContadoCentavos { get; set; }

    /// <summary>Lo que faltó (−) o sobró (+) al contar en eleventa; vacío si cuadró.</summary>
    public long? DiferenciaCentavos { get; set; }

    /// <summary>Cuándo la caja nueva cerró su turno con ese corte.</summary>
    public DateTime? TomadoUtc { get; set; }
}

/// <summary>
/// Otro ticket de eleventa con el mismo folio que una venta de la caja 2 (eleventa toma el último más uno, sin candado:
/// dos cajas que cobran casi al mismo tiempo lo repiten). Lo encuentra la revisión tardía, cuando el ticket de la caja 2
/// ya pudo salir impreso con ese folio: no se cambia, sus reimpresiones salen con el de la caja y, al escribir el folio
/// para devolver, la caja enseña los dos para elegir por la hora y el total.
/// </summary>
public class FolioRepetidoEnEleventa
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>La venta de la caja 2 en la bandeja.</summary>
    public Guid EnvioId { get; set; }

    public int Folio { get; set; }

    /// <summary><c>VENTATICKETS.ID</c> del otro ticket.</summary>
    public int TicketEleventa { get; set; }

    /// <summary><c>CAJAS.NOMBRE</c> de la caja que lo cobró.</summary>
    public required string Caja { get; set; }

    public DateTime? VendidoUtc { get; set; }
    public long TotalCentavos { get; set; }
    public DateTime VistoUtc { get; set; }
}
