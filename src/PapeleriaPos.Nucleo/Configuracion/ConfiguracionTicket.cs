namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>
/// Qué se hace con el ticket de una venta al cobrarla (Configuración › Ticket › «Al cobrar»; docs/propuesta-interfaz.md,
/// «Configuración»). Nunca se pregunta antes de cobrar: F12 ya es la confirmación (dueño, 2026-10-14), así que el
/// «preguntar» de la propuesta es <see cref="Ofrecer"/>, que no detiene al siguiente cliente. Va por su nombre en la base y
/// por su número en la copia de cada caja y en el ticket: no se cambian ni los nombres ni el orden.
/// </summary>
public enum TicketAlCobrar
{
    /// <summary>«Imprimir el ticket»: sale al cobrar, como siempre. Por omisión.</summary>
    Imprimir,

    /// <summary>
    /// «Ofrecerlo»: se cobra sin imprimir y la caja queda lista para el siguiente cliente; hasta su primer escaneo, el panel
    /// ofrece «Imprimir ticket» (Ctrl+I) para esa venta.
    /// </summary>
    Ofrecer,

    /// <summary>«No imprimirlo»: no sale nada; si el cliente lo pide, se reimprime desde las ventas del día.</summary>
    NoImprimir,
}

/// <summary>
/// Encabezado, pie y logo del ticket de una sucursal (cada sucursal tiene su dirección), y si sale al cobrar. El logo se
/// guarda ya convertido para la impresora: blanco y negro, un bit por punto.
/// </summary>
public class ConfiguracionTicket : IVersionado
{
    public const int Columnas = 48;
    public const int RenglonesMaximos = 8;

    public Guid SucursalId { get; set; }

    /// <summary>Un renglón por línea; el primero sale grande y en negritas.</summary>
    public string Encabezado { get; private set; } = "";

    public string Pie { get; private set; } = "";

    public int? LogoAncho { get; private set; }
    public int? LogoAlto { get; private set; }
    public byte[]? LogoBits { get; private set; }

    /// <summary>
    /// Qué se hace con el ticket al cobrar, en todas las cajas de la sucursal (las dos comparten la impresora y los clientes).
    /// Lo fiado sale siempre (<see cref="Contratos.TicketDto.LoQueSeHaceAlCobrar"/>), y lo demás que se imprime (los
    /// comprobantes, el corte, las reimpresiones) no depende de esto.
    /// </summary>
    public TicketAlCobrar AlCobrar { get; private set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    public IReadOnlyList<string> RenglonesEncabezado => Partir(Encabezado);
    public IReadOnlyList<string> RenglonesPie => Partir(Pie);
    public bool TieneLogo => LogoBits is not null;

    public void CambiarAlCobrar(TicketAlCobrar alCobrar) =>
        AlCobrar = Enum.IsDefined(alCobrar) ? alCobrar : throw new ArgumentException("Al cobrar se imprime el ticket, se ofrece o no se imprime.");

    /// <summary>
    /// Si el ticket de una venta sale al cobrarla, con «Al cobrar» de la sucursal: con «Imprimir el ticket», y lo fiado
    /// siempre (lleva «SU CUENTA»). Con «Ofrecerlo» o «No imprimirlo» no sale: su primera impresión, después, no es una
    /// reimpresión y no pide motivo (2026-10-22).
    /// </summary>
    public static bool SaleAlCobrar(TicketAlCobrar alCobrar, bool fiado) => fiado || alCobrar == TicketAlCobrar.Imprimir;

    /// <summary>Cómo se dice cada opción de «Al cobrar»: en la Configuración y en la bitácora.</summary>
    public static string Nombre(TicketAlCobrar alCobrar) => alCobrar switch
    {
        TicketAlCobrar.Ofrecer => "Ofrecerlo",
        TicketAlCobrar.NoImprimir => "No imprimirlo",
        _ => "Imprimir el ticket",
    };

    /// <summary>Hasta 8 renglones de 48 caracteres cada uno (lo que cabe en papel de 80 mm). Los vacíos se quitan.</summary>
    public void CambiarTextos(IEnumerable<string> encabezado, IEnumerable<string> pie)
    {
        Encabezado = Validar(encabezado, "encabezado");
        Pie = Validar(pie, "pie");
    }

    public void CambiarLogo(int ancho, int alto, byte[] bits)
    {
        if (ancho <= 0 || alto <= 0 || bits.Length != (ancho + 7) / 8 * alto)
            throw new ArgumentException("El logo no tiene el tamaño que dice.", nameof(bits));
        (LogoAncho, LogoAlto, LogoBits) = (ancho, alto, bits);
    }

    public void QuitarLogo() => (LogoAncho, LogoAlto, LogoBits) = (null, null, null);

    private static string Validar(IEnumerable<string> renglones, string parte)
    {
        var limpios = renglones.Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (limpios.Count > RenglonesMaximos)
            throw new ArgumentException($"El {parte} admite hasta {RenglonesMaximos} renglones.");
        if (limpios.FirstOrDefault(r => r.Length > Columnas) is { } largo)
            throw new ArgumentException($"«{largo}» no cabe en el ticket: son {Columnas} caracteres por renglón.");
        return string.Join('\n', limpios);
    }

    private static List<string> Partir(string texto) =>
        texto.Length == 0 ? [] : [.. texto.Split('\n')];
}
