namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que la caja dice en el piloto con eleventa (decisión del dueño, 2026-10-12): mientras el servicio manda las ventas a
/// eleventa (<see cref="Nucleo.Contratos.EstadoServicioDto.PilotoEleventa"/>), la caja solo cobra y devuelve en efectivo.
/// La pantalla ya no ofrece la tarjeta, la transferencia, el crédito, el saldo a favor ni el pago mixto, ni los abonos o
/// devolver saldos a favor; el servicio también los rechaza. Las pantallas lo reciben como valor en cascada
/// <see cref="Cascada"/>. Los textos son los mismos que los del servicio (SoloEfectivoEnElPiloto, en el puente, que la
/// caja no conoce): con o sin conexión, la cajera lee lo mismo; una prueba lo revisa. Solo sirve para convivir con
/// eleventa: se quita al dejarlo.
/// </summary>
public static class PilotoConEleventa
{
    /// <summary>El nombre del valor en cascada que pone la pantalla de venta.</summary>
    public const string Cascada = "PilotoEleventa";

    /// <summary>Junto a «Pagó con»: en lugar de los botones de las otras formas.</summary>
    public const string SoloEfectivo = "Solo efectivo (piloto con eleventa)";

    public const string NoSeCobra =
        "Mientras dure el piloto con eleventa, esta caja solo cobra en efectivo: sin tarjeta, transferencia, crédito ni saldo a favor. Cóbralo todo en efectivo.";

    public const string NoSeAbona =
        "Mientras dure el piloto con eleventa, esta caja no recibe abonos: eleventa no los tiene y su corte no cuadraría.";

    public const string NoSeDevuelveSaldo =
        "Mientras dure el piloto con eleventa, esta caja no devuelve saldos a favor: eleventa no los tiene y su corte no cuadraría.";

    public const string SoloSeDevuelveEfectivo = "Mientras dure el piloto con eleventa, esta caja solo devuelve en efectivo lo que se cobró en efectivo.";
}
