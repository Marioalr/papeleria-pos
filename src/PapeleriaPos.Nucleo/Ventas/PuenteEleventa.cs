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

/// <summary>
/// La ida (docs/migracion-eleventa.md, «Devoluciones de ida y vuelta»): la devolución en la caja nueva de un ticket que se
/// cobró en eleventa (en la caja principal, o en el eleventa de la caja 2). El ticket se leyó en vivo en eleventa y se
/// guarda aquí como se leyó, no como venta: la caja nueva no lo vendió. El dinero sale del turno de la caja nueva como
/// una salida de efectivo (<see cref="MovimientoId"/>, que no va a eleventa como salida) y la devolución va a eleventa por
/// la bandeja, escrita como la escribe eleventa.
/// </summary>
public class DevolucionDeEleventa
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid CajaId { get; set; }

    /// <summary>El turno de la caja nueva de donde salió el dinero.</summary>
    public Guid TurnoId { get; set; }

    public Guid UsuarioId { get; set; }
    public Guid? AutorizoId { get; set; }
    public required string Motivo { get; set; }
    public DateTime FechaUtc { get; set; }

    // El ticket, como se leyó en eleventa.

    /// <summary><c>VENTATICKETS.ID</c>.</summary>
    public int TicketEleventa { get; set; }

    public int Folio { get; set; }

    /// <summary><c>CAJAS.ID</c> y <c>CAJAS.NOMBRE</c> de la caja que lo cobró.</summary>
    public int CajaEleventa { get; set; }

    public required string CajaNombre { get; set; }

    /// <summary><c>VENTATICKETS.TURNO_ID</c>: el turno de eleventa en que se cobró.</summary>
    public int? TurnoTicketEleventa { get; set; }

    public DateTime VendidoUtc { get; set; }

    /// <summary>Lo que pagó el cliente (con lo que ya se había devuelto en eleventa).</summary>
    public long TicketTotalCentavos { get; set; }

    /// <summary>Sus artículos, como estaban al leerlo.</summary>
    public List<ArticuloLeidoDeEleventa> Ticket { get; set; } = [];

    // Lo que se devolvió aquí.

    public List<ArticuloDevueltoDeEleventa> Articulos { get; set; } = [];

    /// <summary>Lo devuelto, exacto.</summary>
    public long TotalCentavos { get; set; }

    /// <summary>Lo que salió de la caja: redondeado al peso, como el reembolso de siempre.</summary>
    public long ReembolsoEfectivoCentavos { get; set; }

    public long RedondeoCentavos { get; set; }

    /// <summary>La salida de efectivo del turno de la caja nueva; vacía si al redondear no salió nada.</summary>
    public Guid? MovimientoId { get; set; }
}

/// <summary>Un artículo de un ticket de eleventa como se leyó (<c>VENTATICKETS_ARTICULOS</c>).</summary>
/// <param name="Id"><c>VENTATICKETS_ARTICULOS.ID</c>.</param>
/// <param name="Cantidad">La que quedaba en eleventa al leerlo (eleventa baja lo devuelto).</param>
/// <param name="ProductoId"><c>PRODUCTOS.ID</c>; vacío si es un artículo común.</param>
public sealed record ArticuloLeidoDeEleventa(int Id, string Codigo, string Nombre, decimal Cantidad, decimal Precio, decimal Total, decimal GananciaPorPieza,
    bool AGranel, int? ProductoId);

/// <param name="ArticuloEleventa"><c>VENTATICKETS_ARTICULOS.ID</c>.</param>
/// <param name="VarianteId">La de la caja nueva, si el producto vino de eleventa: a su inventario regresa lo que no vino dañado.</param>
public sealed record ArticuloDevueltoDeEleventa(int ArticuloEleventa, string Codigo, string Nombre, decimal Cantidad, long ImporteCentavos,
    bool RegresaAlInventario, Guid? VarianteId);

/// <summary>
/// La vuelta (docs/migracion-eleventa.md, «Devoluciones de ida y vuelta»): una devolución o cancelación que se hizo en
/// eleventa de un ticket que mandó la caja 2. La lectura de cada pocos minutos la trae y la pasa a la venta de la caja
/// nueva como una devolución sin efectivo (el dinero salió de la caja de eleventa), antes de las existencias. Lo que vino
/// de eleventa no va a la bandeja. Lo que no se pudo pasar queda por revisar.
/// </summary>
public class DevolucionDesdeEleventa
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary><c>DEVOLUCIONES.ID</c>.</summary>
    public int DevolucionEleventa { get; set; }

    /// <summary><c>VENTATICKETS.ID</c> del ticket de la caja 2.</summary>
    public int TicketEleventa { get; set; }

    /// <summary><c>DEVOLUCIONES.DEVUELTO_EN</c>: con el ID dice que es la misma (una restauración vuelve a dar los números).</summary>
    public DateTime DevueltaUtc { get; set; }

    public bool EsCancelacion { get; set; }

    /// <summary><c>TOTAL_DEVUELTO</c>: lo que salió de la caja de eleventa.</summary>
    public long TotalCentavos { get; set; }

    /// <summary><c>DEVOLUCIONES.CAJERO</c> y <c>CAJA</c>, como texto.</summary>
    public string? Cajero { get; set; }

    public string? Caja { get; set; }

    public Guid VentaId { get; set; }

    /// <summary>La devolución que se le hizo a la venta de la caja nueva; vacía si quedó por revisar.</summary>
    public Guid? DevolucionId { get; set; }

    /// <summary>Por qué no se pasó a la venta (ya se había devuelto aquí, no está en el ticket…).</summary>
    public string? PorRevisar { get; set; }

    /// <summary>Cuándo alguien la revisó (lo que quedó por revisar ya no se enseña).</summary>
    public DateTime? RevisadaUtc { get; set; }

    public DateTime VistaUtc { get; set; }
}

/// <summary>
/// Los respaldos de eleventa (docs/migracion-eleventa.md, «Los respaldos de eleventa»): una vez que el puente vio que
/// eleventa ya no tiene lo que le mandó la caja 2, o que sus números regresaron (se restauró un respaldo anterior), y dejó
/// de escribir. Mientras no se reanude a mano (<see cref="ReanudadoUtc"/>), el puente no escribe en eleventa ni aplica su
/// lectura de cada pocos minutos; la caja 2 sigue vendiendo.
/// </summary>
public class DetencionDelPuente
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public DateTime DetectadaUtc { get; set; }

    /// <summary>Lo que se vio, dicho para la tienda: un renglón por cosa.</summary>
    public required string Pruebas { get; set; }

    /// <summary>Cuándo se mandó lo más viejo que ya no está en eleventa; vacío si solo regresaron los números.</summary>
    public DateTime? DesdeUtc { get; set; }

    /// <summary>Cuándo se reanudó: quien tiene «Configuración de la tienda» revisó eleventa y lo dijo.</summary>
    public DateTime? ReanudadoUtc { get; set; }

    public Guid? ReanudoId { get; set; }
    public string? Nota { get; set; }

    /// <summary>Lo que al reanudar ya no estaba en eleventa y quedó por revisar, para anotarlo a mano allá.</summary>
    public int? Perdidos { get; set; }
}
