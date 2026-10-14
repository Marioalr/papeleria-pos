using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>
/// El piloto en la caja 2 solo cobra en efectivo (decisión del dueño, 2026-10-12): mientras la caja manda sus ventas a
/// eleventa, se desactivan la tarjeta, la transferencia, el crédito y el saldo a favor (y con ellos el pago mixto); lo
/// devuelto sale solo en efectivo y solo de lo que se cobró en efectivo; y no se reciben abonos ni se devuelven saldos a
/// favor. A eleventa solo pasan solas las ventas en efectivo, y lo demás no está en su corte: así el corte de eleventa
/// cuadra con lo que hay en la caja (docs/migracion-eleventa.md, «Entradas y salidas de dinero»).
/// La pantalla ya no ofrece nada de eso; esto es la defensa del servicio para lo que se hace en línea. Lo que sube de la
/// contingencia ya pasó y se guarda: si no fue en efectivo, la bandeja hacia eleventa lo deja por revisar. Lo que la caja
/// manda otra vez con el mismo Id (se perdió la respuesta) también ya pasó: regresa lo guardado, como siempre.
/// La caja dice lo mismo con los mismos textos (PilotoConEleventa, en la interfaz, que no conoce el puente).
/// Solo sirve para convivir con eleventa: se quita al dejarlo, con <c>Eleventa:Escribir</c>.
/// </summary>
public static class SoloEfectivoEnElPiloto
{
    public const string NoSeCobra =
        "Mientras dure el piloto con eleventa, esta caja solo cobra en efectivo: sin tarjeta, transferencia, crédito ni saldo a favor. Cóbralo todo en efectivo.";

    public const string NoSeAbona =
        "Mientras dure el piloto con eleventa, esta caja no recibe abonos: eleventa no los tiene y su corte no cuadraría.";

    public const string NoSeDevuelveSaldo =
        "Mientras dure el piloto con eleventa, esta caja no devuelve saldos a favor: eleventa no los tiene y su corte no cuadraría.";

    public const string SoloSeDevuelveEfectivo = "Mientras dure el piloto con eleventa, esta caja solo devuelve en efectivo lo que se cobró en efectivo.";

    /// <summary>Lo que no es efectivo no pasa, tampoco como una parte de un pago mixto.</summary>
    public static void RevisarCobro(IEnumerable<CobroSolicitado> cobros)
    {
        if (cobros.Any(c => c.Forma != FormaPago.Efectivo))
            throw new InvalidOperationException(NoSeCobra);
    }

    /// <summary>
    /// Un abono o un saldo a favor devuelto nuevo no pasa (<paramref name="mensaje"/>). Si ya está guardado con ese Id, la
    /// caja lo manda otra vez porque se perdió la respuesta: ya pasó, aunque entonces no estuviera el piloto, y se regresa
    /// su comprobante. Rechazarlo le diría a la caja que no quedó.
    /// </summary>
    public static async Task RevisarAbonoNuevoAsync(PosDbContext db, Guid abonoId, string mensaje, CancellationToken ct = default)
    {
        if (!await db.Abonos.AsNoTracking().AnyAsync(a => a.Id == abonoId, ct))
            throw new InvalidOperationException(mensaje);
    }

    /// <summary>
    /// Por qué el ticket no se cancela ni se devuelve en el piloto: lo devuelto sale todo en efectivo, así que el ticket
    /// tuvo que cobrarse todo en efectivo. Lo de tarjeta o transferencia se regresaría por su medio, y lo fiado bajaría la
    /// cuenta del cliente (o le quedaría a favor). Vacío si se cobró todo en efectivo. La ventana de devoluciones lo
    /// enseña al buscar el folio.
    /// </summary>
    public static async Task<string?> PorQueNoSeDevuelveAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        var formas = await db.Cobros.AsNoTracking().Where(c => c.VentaId == ventaId && c.Forma != FormaPago.Efectivo)
            .Select(c => c.Forma).Distinct().ToListAsync(ct);
        return formas.Count == 0 ? null : NoSeDevuelve($"este ticket se cobró {string.Join(" y ", formas.Order().Select(Forma))}", "no se devuelve aquí");
    }

    /// <summary>Una cancelación o devolución en el piloto: solo la de un ticket cobrado todo en efectivo.</summary>
    public static async Task RevisarDevolucionAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        if (await PorQueNoSeDevuelveAsync(db, ventaId, ct) is { } motivo)
            throw new InvalidOperationException(motivo);
    }

    /// <summary>
    /// Cancelar un abono de este turno (uno de antes del piloto, o de la contingencia) sí se puede si fue en efectivo: sale
    /// lo mismo que entró y el corte de eleventa, que no lo tiene, vuelve a cuadrar. El de tarjeta o transferencia se
    /// regresaría por su medio, como lo devuelto de un ticket.
    /// </summary>
    public static async Task RevisarCancelacionDeAbonoAsync(PosDbContext db, Guid abonoId, CancellationToken ct = default)
    {
        var forma = await db.Abonos.AsNoTracking().Where(a => a.Id == abonoId).Select(a => (FormaPago?)a.Forma).SingleOrDefaultAsync(ct);
        if (forma is { } f && f != FormaPago.Efectivo)
            throw new InvalidOperationException(NoSeDevuelve($"este abono se cobró {Forma(f)}", "no se cancela aquí"));
    }

    private static string NoSeDevuelve(string porque, string entonces) => $"{SoloSeDevuelveEfectivo.TrimEnd('.')}, y {porque}: {entonces}.";

    private static string Forma(FormaPago forma) => forma switch
    {
        FormaPago.Tarjeta => "con tarjeta",
        FormaPago.Transferencia => "por transferencia",
        _ => "a crédito o con saldo a favor",
    };
}
