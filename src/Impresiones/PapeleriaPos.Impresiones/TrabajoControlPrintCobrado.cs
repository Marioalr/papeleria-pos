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
}

/// <summary>Un trabajo de Control Print que se va a cobrar, ya revisado allá (sigue por cobrar y con ese precio).</summary>
/// <param name="Id">Su número en Control Print.</param>
/// <param name="DetectadoUtc">Cuándo lo vio Control Print.</param>
/// <param name="Documento">La etiqueta o el documento.</param>
/// <param name="Pc">La PC que imprimió.</param>
public sealed record TrabajoControlPrintParaCobrar(long Id, DateTime DetectadoUtc, string Documento, string Pc, long PrecioCentavos, long CostoCentavos);
