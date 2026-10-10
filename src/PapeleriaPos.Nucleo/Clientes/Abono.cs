using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Clientes;

/// <summary>
/// Un abono a la cuenta de un cliente (fase 3, entrega 2): se recibe en cualquier caja con su turno abierto y entra al
/// corte de ese turno como ingreso aparte (no es venta: la venta ya se contó al fiar). Baja la cuenta con su
/// <see cref="MovimientoCredito"/>. Los montos no cambian; solo su verificación (transferencia) y su cancelación.
/// </summary>
public class Abono
{
    /// <summary>Lo pone la caja: si llega dos veces, se guarda una.</summary>
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid SucursalId { get; set; }
    public Guid CajaId { get; set; }
    public Guid TurnoId { get; set; }
    public Guid ClienteId { get; set; }
    public Guid UsuarioId { get; set; }

    /// <summary>Efectivo, tarjeta o transferencia; nunca crédito.</summary>
    public FormaPago Forma { get; set; }

    /// <summary>Lo que baja la cuenta, exacto.</summary>
    public long MontoCentavos { get; set; }

    /// <summary>Solo en efectivo al liquidar: lo cobrado al peso menos el saldo exacto. Va al corte, nunca a la cuenta.</summary>
    public long RedondeoCentavos { get; set; }

    public long RecibidoCentavos { get; set; }
    public long CambioCentavos { get; set; }
    public string? Referencia { get; set; }

    /// <summary>La transferencia queda por verificar; lo demás no aplica.</summary>
    public Verificacion Verificacion { get; set; }

    public Guid? VerificadoPorId { get; set; }
    public DateTime? VerificadoUtc { get; set; }

    /// <summary>Lo que debía y lo que quedó: así el comprobante se reimprime igual.</summary>
    public long SaldoAnteriorCentavos { get; set; }

    public long SaldoCentavos { get; set; }
    public DateTime FechaUtc { get; set; }
    public bool EnContingencia { get; set; }
    public DateTime? CanceladoUtc { get; set; }
    public Guid? CanceladoPorId { get; set; }
    public Guid? AutorizoCancelacionId { get; set; }
    public string? MotivoCancelacion { get; set; }

    /// <summary>Lo que entró a la caja: el monto con su redondeo.</summary>
    public long CobradoCentavos => MontoCentavos + RedondeoCentavos;

    /// <summary>"AB-1A2B3C": sale del Id, así no choca aunque se haga sin conexión.</summary>
    public string Folio => FolioDe(Id);

    public static string FolioDe(Guid id) => "AB-" + id.ToString("N")[^6..].ToUpperInvariant();
}

/// <param name="MontoCentavos">Lo que baja la cuenta.</param>
/// <param name="ACobrarCentavos">Lo que se le pide al cliente (en efectivo, al peso).</param>
/// <param name="Liquida">Si deja la cuenta en cero.</param>
public sealed record AbonoCalculado(long MontoCentavos, long ACobrarCentavos, long RedondeoCentavos, long CambioCentavos, bool Liquida);

/// <summary>Cuánto baja un abono y cuánto se cobra. Lo usan igual la caja (para mostrarlo) y el servicio (para guardarlo).</summary>
public static class CalculoAbono
{
    /// <summary>
    /// En efectivo se abona en pesos; si el monto alcanza para liquidar (o va vacío), se cobra el saldo al peso (al menos
    /// $1) y la cuenta queda en cero exacto. Con tarjeta o transferencia, exacto y sin pasar del saldo: no dan cambio.
    /// Nunca queda saldo a favor.
    /// </summary>
    /// <param name="montoCentavos">Vacío: liquidar.</param>
    /// <param name="recibidoCentavos">En efectivo, lo que entregó el cliente.</param>
    public static AbonoCalculado Calcular(long saldoCentavos, FormaPago forma, long? montoCentavos, long recibidoCentavos)
    {
        if (saldoCentavos <= 0)
            throw new InvalidOperationException("No debe nada: no hay qué abonar.");
        if (montoCentavos is <= 0)
            throw new ArgumentException("El abono debe ser mayor a cero.");
        switch (forma)
        {
            case FormaPago.Efectivo:
            {
                var liquidar = Math.Max(100, Redondeo.AlPeso(saldoCentavos));
                if (montoCentavos is null || montoCentavos >= liquidar)
                    return Recibir(saldoCentavos, liquidar, recibidoCentavos, liquida: true);
                var monto = montoCentavos.Value;
                if (monto % 100 != 0)
                {
                    var abajo = monto / 100 * 100;
                    var opciones = abajo == 0 ? Pesos(100) : $"{Pesos(abajo)} o {Pesos(abajo + 100)}";
                    throw new ArgumentException($"En efectivo se abona en pesos ({opciones}), o liquida con {Pesos(liquidar)}.");
                }
                return Recibir(monto, monto, recibidoCentavos, liquida: false);
            }
            case FormaPago.Tarjeta or FormaPago.Transferencia:
            {
                var monto = montoCentavos ?? saldoCentavos;
                if (monto > saldoCentavos)
                    throw new ArgumentException(
                        $"{(forma == FormaPago.Tarjeta ? "La tarjeta" : "La transferencia")} no da cambio: abona hasta {Pesos(saldoCentavos)}.");
                return new AbonoCalculado(monto, monto, 0, 0, monto == saldoCentavos);
            }
            default:
                throw new ArgumentException("Un abono se paga en efectivo, con tarjeta o con transferencia.");
        }
    }

    private static AbonoCalculado Recibir(long monto, long aCobrar, long recibido, bool liquida)
    {
        if (recibido < aCobrar)
            throw new InvalidOperationException($"Faltan {Pesos(aCobrar - recibido)}: el abono es de {Pesos(aCobrar)}.");
        return new AbonoCalculado(monto, aCobrar, aCobrar - monto, recibido - aCobrar, liquida);
    }

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", System.Globalization.CultureInfo.GetCultureInfo("es-MX"));
}
