namespace PapeleriaPos.Nucleo.Ventas;

public enum EstadoEnvioEleventa
{
    /// <summary>Todavía no entra a eleventa: se vuelve a intentar.</summary>
    Pendiente,
    Enviada,
    /// <summary>Eleventa no la entendería (por ejemplo, no fue en efectivo): no se manda sola, alguien la revisa.</summary>
    PorRevisar,
    /// <summary>Ya no hace falta: la venta se canceló antes de llegar a eleventa (y con ella, lo que dependía de ella).</summary>
    Descartada,
    /// <summary>
    /// Alguien ya lo dejó en eleventa a mano (lo registró allá, o allá ya estaba): no se manda, y la lectura de eleventa ya
    /// no lo descuenta de la existencia.
    /// </summary>
    AMano,
}

/// <summary>
/// Por qué algo de la bandeja quedó por revisar (revisión del 2026-10-17): con eso, la vuelta sabe si una devolución que
/// llega de eleventa puede ser la anotación a mano de una de aquí.
/// </summary>
public enum MotivoPorRevisar
{
    /// <summary>Otra cosa: no fue en efectivo, se dejó una venta que no llegó…</summary>
    Otro,

    /// <summary>
    /// No se puede mandar solo y no está en eleventa: se anota a mano allá (es de otro turno o de otra caja de eleventa, su
    /// turno ya se cortó, hay dos turnos abiertos, se perdió al restaurar un respaldo, o alguien lo dejó para revisarlo a mano).
    /// </summary>
    AnotarAMano,

    /// <summary>Choca con lo que eleventa ya tiene (el ticket ya está cancelado, ya no le queda eso por devolver): no se anota, se revisa.</summary>
    EleventaYaLoTenia,
}

public enum TipoEnvioEleventa
{
    Venta,
    Cancelacion,
    /// <summary>Una devolución de parte del ticket.</summary>
    Devolucion,
    /// <summary>El corte Z de la caja nueva: cierra el turno de eleventa.</summary>
    CierreDeTurno,
    /// <summary>Una entrada (F7) o salida (F8) de efectivo.</summary>
    MovimientoDeCaja,

    /// <summary>
    /// La devolución en la caja nueva de un ticket que se cobró en eleventa (la ida, <see cref="DevolucionDeEleventa"/>).
    /// </summary>
    DevolucionDeEleventa,
}

/// <summary>
/// La bandeja de salida hacia eleventa durante el piloto en la caja 2 (docs/migracion-eleventa.md, «Mandar cada
/// venta», «Cancelar, devolver y cerrar el turno» y «Entradas y salidas de dinero»): un renglón por venta, cancelación,
/// devolución, entrada o salida de efectivo o cierre de turno, guardado en la misma transacción que la operación, así
/// nada se queda sin mandar aunque se apague la caja justo después. Se mandan en el orden en que pasaron: una devolución
/// nunca llega antes que su venta, ni un turno se cierra antes de que lleguen sus ventas, entradas y salidas.
/// </summary>
public class EnvioAEleventa
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public TipoEnvioEleventa Tipo { get; set; }

    /// <summary>La venta (también la de una cancelación o devolución).</summary>
    public Guid? VentaId { get; set; }

    public Guid? DevolucionId { get; set; }

    /// <summary>El turno de la caja nueva que se cerró (solo el cierre).</summary>
    public Guid? TurnoId { get; set; }

    /// <summary>La entrada o salida de efectivo.</summary>
    public Guid? MovimientoId { get; set; }

    /// <summary>La devolución de un ticket de eleventa (la ida).</summary>
    public Guid? DevolucionDeEleventaId { get; set; }

    public EstadoEnvioEleventa Estado { get; set; }
    public DateTime CreadoUtc { get; set; }

    public int Intentos { get; set; }
    public DateTime? UltimoIntentoUtc { get; set; }

    /// <summary>Por qué no entró la última vez (o por qué queda por revisar), dicho para la tienda.</summary>
    public string? Error { get; set; }

    /// <summary>Por qué quedó por revisar, si quedó.</summary>
    public MotivoPorRevisar? PorRevisarPorque { get; set; }

    /// <summary>Cuándo quedó por revisar.</summary>
    public DateTime? PorRevisarUtc { get; set; }

    public DateTime? EnviadaUtc { get; set; }

    /// <summary><c>VENTATICKETS.ID</c> en eleventa (la venta).</summary>
    public int? TicketEleventa { get; set; }

    /// <summary>El folio que le dio eleventa a la venta: es el que lleva el ticket impreso si llegó a tiempo.</summary>
    public int? FolioEleventa { get; set; }

    /// <summary>Otra caja de eleventa cobró con el mismo folio en el mismo instante: queda para revisarlo.</summary>
    public bool FolioRepetido { get; set; }

    /// <summary>
    /// Lo que quedó en eleventa: <c>DEVOLUCIONES.ID</c> de una cancelación o devolución (también la de un ticket de
    /// eleventa), <c>CORTE_MOVIMIENTOS.ID</c> de una entrada o salida, <c>TURNOS.ID</c> de un cierre.
    /// </summary>
    public int? IdEleventa { get; set; }
}
