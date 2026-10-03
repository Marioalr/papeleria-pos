namespace PapeleriaPos.Nucleo.Ventas;

public enum FormaPago
{
    Efectivo,
    /// <summary>Terminal externa: se guarda su referencia.</summary>
    Tarjeta,
    /// <summary>Queda por verificar hasta que alguien con permiso confirma que llegó el dinero.</summary>
    Transferencia,
}

public enum Verificacion
{
    NoAplica,
    PorVerificar,
    Verificada,
    Rechazada,
}

/// <summary>Lo que captura el cajero al cobrar. En efectivo, el monto es lo que entregó el cliente.</summary>
public sealed record CobroSolicitado(FormaPago Forma, long MontoCentavos, string? Referencia = null);

public class Cobro
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid VentaId { get; set; }
    public FormaPago Forma { get; set; }

    /// <summary>Lo que cubre de la venta. En efectivo es lo recibido menos el cambio.</summary>
    public long MontoCentavos { get; set; }

    public long RecibidoCentavos { get; set; }
    public long CambioCentavos { get; set; }
    public string? Referencia { get; set; }
    public Verificacion Verificacion { get; set; }
    public Guid? VerificadoPorId { get; set; }
    public DateTime? VerificadoUtc { get; set; }
}
