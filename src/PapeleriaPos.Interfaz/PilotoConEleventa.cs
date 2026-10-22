namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que la caja dice en el piloto con eleventa (decisión del dueño, 2026-10-12): mientras el servicio manda las ventas a
/// eleventa (<see cref="Nucleo.Contratos.EstadoServicioDto.PilotoEleventa"/>), la caja solo cobra y devuelve en efectivo
/// y su corte se hace en eleventa.
/// La pantalla ya no ofrece la tarjeta, la transferencia, el crédito, el saldo a favor ni el pago mixto, ni los abonos o
/// devolver saldos a favor; el servicio también los rechaza. Tampoco ofrece el corte (el servicio rechaza el de cierre),
/// ni el aviso de retiro (el cajón es de las dos y el turno de esta caja no se cierra aquí), ni cancelar un ticket de otro
/// día (el servicio también lo rechaza). Las pantallas lo reciben como valor en cascada <see cref="Cascada"/>. Los textos son los mismos que los del servicio
/// (SoloEfectivoEnElPiloto y CorteEnElPiloto, en el puente, que la caja no conoce): con o sin conexión, la cajera lee lo
/// mismo; una prueba lo revisa. Solo sirve para convivir con eleventa: se quita al dejarlo.
/// </summary>
public static class PilotoConEleventa
{
    /// <summary>El nombre del valor en cascada que pone la pantalla de venta (y la raíz de la caja, para el corte).</summary>
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

    /// <summary>
    /// En lugar del corte (el parcial y el de cierre), con o sin conexión; y al salir y al cerrar la ventana de la caja,
    /// para recordarlo: eleventa sigue abierto en la caja 2 y las dos venden en su mismo turno (2026-10-12).
    /// </summary>
    public const string CorteEnEleventa = "Mientras dure el piloto, el corte de esta caja se hace en eleventa.";

    /// <summary>En el corte del día (2026-10-14): se ve, pero el día no se cierra aquí.</summary>
    public const string CorteDelDiaEnEleventa =
        "Mientras dure el piloto, el día se cierra con el corte de eleventa: aquí solo se ve lo de esta caja.";

    /// <summary>
    /// En los reportes de ventas (entrega 9, 2026-10-22): la base de la caja 2 solo tiene lo suyo, y el historial de eleventa
    /// que trae es de toda la tienda, así que no se mezclan ni se comparan.
    /// </summary>
    public const string ReportesDeLaCaja2 =
        "Solo lo que vendió la caja 2: lo de la caja principal está en los reportes de eleventa. Mientras dure el piloto no se compara "
        + "contra el año pasado: el historial de eleventa es de toda la tienda.";

    /// <summary>En la ventana de devoluciones: el turno de esta caja no se cierra aquí y duraría días.</summary>
    public const string SoloSeCancelaElMismoDia =
        "Mientras dure el piloto, el corte de esta caja se hace en eleventa: solo se cancela un ticket del mismo día; después, es devolución.";
}
