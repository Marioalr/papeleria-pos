namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>
/// Encabezado, pie y logo del ticket de una sucursal (cada sucursal tiene su dirección). El logo se guarda ya
/// convertido para la impresora: blanco y negro, un bit por punto.
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

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    public IReadOnlyList<string> RenglonesEncabezado => Partir(Encabezado);
    public IReadOnlyList<string> RenglonesPie => Partir(Pie);
    public bool TieneLogo => LogoBits is not null;

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
