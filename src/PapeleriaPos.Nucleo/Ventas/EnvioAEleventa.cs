namespace PapeleriaPos.Nucleo.Ventas;

public enum EstadoEnvioEleventa
{
    /// <summary>Todavía no entra a eleventa: se vuelve a intentar.</summary>
    Pendiente,
    Enviada,
    /// <summary>Eleventa no la entendería (por ejemplo, no fue en efectivo): no se manda sola, alguien la revisa.</summary>
    PorRevisar,
}

/// <summary>
/// La bandeja de salida hacia eleventa durante el piloto en la caja 2 (docs/migracion-eleventa.md, «Mandar cada
/// venta»): un renglón por venta cobrada, guardado en la misma transacción que la venta, así ninguna se queda sin
/// mandar aunque se apague la caja justo después de cobrar.
/// </summary>
public class EnvioAEleventa
{
    public Guid VentaId { get; set; }
    public EstadoEnvioEleventa Estado { get; set; }
    public DateTime CreadoUtc { get; set; }

    public int Intentos { get; set; }
    public DateTime? UltimoIntentoUtc { get; set; }

    /// <summary>Por qué no entró la última vez (o por qué queda por revisar), dicho para la tienda.</summary>
    public string? Error { get; set; }

    public DateTime? EnviadaUtc { get; set; }

    /// <summary><c>VENTATICKETS.ID</c> en eleventa.</summary>
    public int? TicketEleventa { get; set; }

    /// <summary>El folio que le dio eleventa: es el que lleva el ticket impreso si llegó a tiempo.</summary>
    public int? FolioEleventa { get; set; }

    /// <summary>Otra caja de eleventa cobró con el mismo folio en el mismo instante: queda para revisarlo.</summary>
    public bool FolioRepetido { get; set; }
}
