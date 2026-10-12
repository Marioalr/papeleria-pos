namespace PapeleriaPos.Nucleo.Ventas;

public enum FormaPago
{
    Efectivo,
    /// <summary>Terminal externa: se guarda su referencia.</summary>
    Tarjeta,
    /// <summary>Queda por verificar hasta que alguien con permiso confirma que llegó el dinero.</summary>
    Transferencia,

    /// <summary>Fiado a la cuenta de un cliente registrado (fase 3, entrega 2): no es dinero que entre a la caja.</summary>
    Credito,
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

/// <summary>Lo que falta pagar en efectivo: exacto y ya redondeado al peso.</summary>
public sealed record EfectivoACobrar(long ExactoCentavos, long ACobrarCentavos)
{
    public long RedondeoCentavos => ACobrarCentavos - ExactoCentavos;
}

/// <summary>Lo que dice el cambio de lo que se recibió en efectivo.</summary>
public enum RevisionDelCambio
{
    /// <summary>Lo de todos los días (o todavía falta dinero: eso se revisa aparte).</summary>
    Normal,

    /// <summary>Un cambio grande: la caja lo enseña y pide otro Enter antes de cobrar.</summary>
    PorConfirmar,

    /// <summary>No es dinero: un código de barras que cayó en «Pagó con», o ceros de más. No se cobra.</summary>
    NoEsDinero,
}

/// <summary>
/// Hasta cuánto cambio se da en una venta (2026-10-12). El billete más grande que circula es el de $1,000: quien paga
/// con billetes nunca necesita $1,000 o más de cambio (si el cambio alcanza un billete, ese billete sobraba). Se deja
/// lugar para quien da de más para que le regresen un billete cerrado, y más de dos billetes de $1,000 de cambio ya no
/// es dinero: es un código de barras escaneado en «Pagó con» (7501234567890 serían billones de pesos) o ceros de más.
/// Desde $500 de cambio (un billete de $1,000 por algo de menos de $500, o «1000» por «100») la caja pide confirmarlo.
/// La caja no cobra lo que no es dinero, y el servicio y la copia sin conexión tampoco lo guardan.
/// </summary>
public static class TopeDeCambio
{
    /// <summary>El billete más grande que circula: $1,000.</summary>
    public const long BilleteMayorCentavos = 100_000;

    /// <summary>Desde este cambio la caja pide confirmar lo que dio el cliente: $500.</summary>
    public const long ConfirmarDesdeCentavos = 50_000;

    /// <summary>Más cambio que esto no se da: dos billetes de $1,000.</summary>
    public const long MaximoCentavos = 2 * BilleteMayorCentavos;

    public static RevisionDelCambio Revisar(long aCobrarCentavos, long recibidoCentavos) => (recibidoCentavos - aCobrarCentavos) switch
    {
        > MaximoCentavos => RevisionDelCambio.NoEsDinero,
        >= ConfirmarDesdeCentavos => RevisionDelCambio.PorConfirmar,
        _ => RevisionDelCambio.Normal,
    };

    /// <summary>Por qué no se cobra lo que no es dinero: lo mismo en la caja y en el servicio.</summary>
    public static string NoEsDinero(long recibidoCentavos) =>
        $"«Pagó con» {Pesos(recibidoCentavos)} no puede ser: el cambio pasaría de {Pesos(MaximoCentavos)}. Si fue el lector, el código "
        + "va en el cuadro de captura; escribe lo que te dio el cliente.";

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", System.Globalization.CultureInfo.GetCultureInfo("es-MX"));
}

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
