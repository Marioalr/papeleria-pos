namespace PapeleriaPos.Nucleo.Contratos;

/// <summary>
/// Qué pasó con el ticket de una venta que el celular pidió imprimir (docs/celular-como-caja.md, paso 11). El celular nunca le habla a
/// la impresora: se lo pide al servicio, que lo manda directo a una impresora de red o lo deja para que lo recoja la ventana de la caja.
/// </summary>
/// <param name="Estado">
/// <see cref="Impreso"/> (salió), <see cref="EnCamino"/> (la ventana de la caja ya lo tiene y todavía no dice que salió),
/// <see cref="NoImpreso"/> (no salió: <paramref name="Codigo"/> y <paramref name="Mensaje"/> dicen por qué) o <see cref="NoPedida"/>
/// (no se pidió).
/// </param>
/// <param name="Codigo">Por qué no salió (los de <see cref="CodigosDeImpresion"/>); vacío si salió o no se pidió.</param>
/// <param name="Mensaje">Para enseñarse tal cual: «Salió en la impresora de la Caja 1.», o por qué no.</param>
/// <param name="CajaId">La caja en cuya impresora sale (la del turno donde entró la venta).</param>
/// <param name="Por"><see cref="DondeImprimeDto.PorRed"/> o <see cref="DondeImprimeDto.PorCaja"/>; vacío si no se intentó.</param>
public sealed record ImpresionDelCelularDto(string Estado, string? Codigo, string Mensaje, Guid? CajaId = null, string? Caja = null, string? Por = null)
{
    public const string Impreso = "impreso";
    public const string EnCamino = "en-camino";
    public const string NoImpreso = "no-impreso";
    public const string NoPedida = "no-pedida";

    public bool Salio => Estado == Impreso;
}

/// <summary>Por qué el ticket de lo que cobra el celular no puede salir, o no salió.</summary>
public static class CodigosDeImpresion
{
    /// <summary>«Imprimir lo que cobra el celular» está apagado en Configuración › Ticket.</summary>
    public const string Apagado = "apagado";

    /// <summary>La caja no tiene impresora de tickets (o su ventana todavía no dice cuál).</summary>
    public const string SinImpresora = "sin-impresora";

    /// <summary>La impresora es de Windows y la ventana de esa caja no está abierta (o no lo recogió).</summary>
    public const string VentanaCerrada = "ventana-cerrada";

    /// <summary>La impresora no contestó: apagada o fuera de la red. No se le mandó nada.</summary>
    public const string NoContesta = "impresora-no-contesta";

    /// <summary>Se acabó el papel (lo dice la impresora de red).</summary>
    public const string SinPapel = "sin-papel";

    /// <summary>Se le mandó y no confirmó: pudo salir. Hay que mirar antes de pedirlo otra vez.</summary>
    public const string SinConfirmar = "sin-confirmar";

    /// <summary>No salió por otra cosa (lo dice el mensaje: la impresora de Windows, el logo dañado).</summary>
    public const string NoSalio = "no-salio";
}

/// <summary>
/// Si el ticket de lo que el celular cobre en una caja puede salir en su impresora, y por dónde (en <c>para-mandar</c>): para que la
/// casilla «Imprimir ticket» del celular lo diga («Sale en la impresora de la Caja 1.») o salga apagada con su razón.
/// </summary>
/// <param name="Puede">Se puede pedir. Con impresora de red no se sabe si está prendida hasta mandarle algo.</param>
/// <param name="Por"><see cref="PorRed"/> (el servicio se la manda directo) o <see cref="PorCaja"/> (la imprime la ventana de esa caja).</param>
/// <param name="Codigo">Si no puede, por qué (<see cref="CodigosDeImpresion"/>).</param>
/// <param name="Mensaje">Para enseñarse tal cual, pueda o no.</param>
/// <param name="Automatico">«Imprimir lo que cobra el celular» está prendido: lo que la casilla trae marcado si el celular no ha elegido.</param>
public sealed record DondeImprimeDto(bool Puede, string? Por, string? Codigo, string Mensaje, bool Automatico = false)
{
    public const string PorRed = "red";
    public const string PorCaja = "caja";
}

/// <summary>
/// <c>POST /api/cajas/{id}/ventas/{ventaId}/imprimir</c>: imprimir después el ticket de una venta ya cobrada (no salió la primera vez, o
/// lo piden otra vez).
/// </summary>
/// <param name="Id">
/// El de esta petición (uno nuevo cada vez que se oprime «Imprimir»): si se manda otra vez porque se perdió la respuesta, es la misma y
/// no sale otro papel. Vacío: cada llamada es una petición nueva.
/// </param>
/// <param name="CajeroId">Quien lo pide (el que entró en el celular); vacío, quien cobró la venta.</param>
/// <param name="Motivo">Solo si ya había salido (una reimpresión) y a ese usuario se le pide motivo.</param>
public sealed record SolicitudImprimirDelCelular(Guid? Id = null, Guid? CajeroId = null, string? Motivo = null);

/// <summary>Un ticket que el celular pidió imprimir y espera a que la ventana de la caja lo recoja y lo mande a su impresora de Windows.</summary>
/// <param name="Id">El del trabajo: con él dice la caja si salió.</param>
/// <param name="De">El celular que lo pidió («Celular de Luis»).</param>
/// <param name="Ticket">El mismo que imprime la caja al cobrar. Sale sin abrir el cajón.</param>
public sealed record TicketPorImprimirDto(Guid Id, Guid VentaId, string De, TicketDto Ticket);

/// <summary>La ventana de la caja dice qué pasó con un ticket que recogió: salió de su impresora o no (y por qué).</summary>
public sealed record SolicitudTicketImpreso(Guid Id, bool Salio, string? Motivo = null);
