using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Un trabajo de Control Print que se cobró en esta caja (las tarjetas de su cola en F2, 2026-10-25). El POS nunca le
/// escribe a Control Print (el dueño, 2026-10-25): allá sigue «por cobrar», y con esto la caja sabe que ya lo cobró y no
/// vuelve a mostrar su tarjeta. Si se cancela la venta o se devuelve su línea de impresiones, queda liberado
/// (<see cref="LiberadoUtc"/>) y, si en Control Print sigue por cobrar, su tarjeta vuelve a salir. Lo cobrado va a la venta
/// del POS (y en el piloto, a eleventa), como cualquier impresión.
/// </summary>
public class TrabajoControlPrintCobrado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>El número del trabajo en Control Print («#1234»).</summary>
    public long TrabajoId { get; set; }

    /// <summary>Cuándo lo vio Control Print: con el número, lo identifica (un Control Print que se reinstala vuelve a empezar los números).</summary>
    public DateTime DetectadoUtc { get; set; }

    /// <summary>La venta en que se cobró.</summary>
    public Guid VentaId { get; set; }

    /// <summary>Como se veía al cobrarlo: la etiqueta o el documento.</summary>
    public required string Documento { get; set; }

    /// <summary>La PC desde la que se imprimió.</summary>
    public required string Pc { get; set; }

    /// <summary>Lo que se cobró: el precio de Control Print al revisarlo justo antes de cobrar.</summary>
    public long PrecioCentavos { get; set; }

    /// <summary>Lo que costó según Control Print (tinta, papel y lo demás): entra al costo de la línea de impresiones.</summary>
    public long CostoCentavos { get; set; }

    public DateTime CobradoUtc { get; set; }

    /// <summary>Se canceló la venta o se devolvió su línea de impresiones: aquí ya no cuenta como cobrado.</summary>
    public DateTime? LiberadoUtc { get; set; }

    /// <summary>Copias del panel de la impresora (en el piloto van a eleventa como COP, en su propia línea; las demás, como IMP).</summary>
    public bool EsCopia { get; set; }

    /// <summary>
    /// Se cobró sin revisarlo en Control Print justo antes (no contestó; se cobró con lo último que se leyó, de hace 10 minutos o
    /// menos). Cuando vuelve a contestar se revisa: si allá ya estaba cobrado, se avisa como <see cref="CobradoEnControlPrintUtc"/>.
    /// </summary>
    public bool SinRevisar { get; set; }

    /// <summary>
    /// Ya se vio cómo quedó en Control Print después de cobrarlo aquí (allá dejó de estar por cobrar): no se vuelve a revisar. Vacío
    /// mientras allá siga por cobrar.
    /// </summary>
    public DateTime? RevisadoUtc { get; set; }

    /// <summary>
    /// Cobrado dos veces: aquí y también en Control Print, en la caja principal (allá seguía «por cobrar»; por ejemplo, en un lote
    /// suyo junto con lo de otro cliente). Cuándo, según Control Print. Se avisa (F2, el chip de arriba, los avisos del dueño y el
    /// registro del servicio) y se queda guardado.
    /// </summary>
    public DateTime? CobradoEnControlPrintUtc { get; set; }

    /// <summary>Quién lo cobró en Control Print.</summary>
    public string? CobradoEnControlPrintPor { get; set; }
}

/// <summary>Un trabajo de Control Print que se va a cobrar, ya revisado allá (sigue por cobrar y con ese precio) o, si no contestó, con lo último que se leyó.</summary>
/// <param name="Id">Su número en Control Print.</param>
/// <param name="DetectadoUtc">Cuándo lo vio Control Print.</param>
/// <param name="Documento">La etiqueta o el documento.</param>
/// <param name="Pc">La PC que imprimió.</param>
/// <param name="EsCopia">Copias del panel (van en la línea de copias).</param>
/// <param name="SinRevisar">Control Print no contestó al revisarlo: se cobra con lo último que se leyó (<see cref="TrabajoControlPrintCobrado.SinRevisar"/>).</param>
public sealed record TrabajoControlPrintParaCobrar(long Id, DateTime DetectadoUtc, string Documento, string Pc, long PrecioCentavos, long CostoCentavos,
    bool EsCopia = false, bool SinRevisar = false);
