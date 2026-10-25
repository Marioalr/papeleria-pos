using System.Text;

namespace PapeleriaPos.Interfaz;

/// <summary>Con qué terminó una lectura del lector por puerto.</summary>
public enum FinLeido { CR, LF, CRLF, Tab, Etx, Pausa, SinFin, MuyLarga }

/// <summary>
/// Lo que llegó del lector por puerto de una vez: un código (o lo que se descartó), sus bytes tal como llegaron (con su carácter
/// de fin), lo que tardó del primer byte al fin y con qué terminó. La caja guarda las últimas para Configuración › Acerca de ›
/// Lector, con a dónde fue cada código.
/// </summary>
public sealed class Lectura
{
    /// <summary>El código, sin el carácter de fin ni espacios a los lados; vacío si se descartó (<see cref="Descartada"/>).</summary>
    public string? Codigo { get; init; }

    /// <summary>Por qué no es un código: «trae caracteres que no son de un código», «llegó sin su fin (CR)»…</summary>
    public string? Descartada { get; init; }

    /// <summary>Los bytes como llegaron, con el carácter de fin (con CRLF, también el LF que llegó detrás).</summary>
    public byte[] Crudo { get; internal set; } = [];

    /// <summary>Del primer byte al fin (con una pausa, al último byte).</summary>
    public TimeSpan Tardo { get; init; }

    public FinLeido Fin { get; internal set; }

    /// <summary>La hora de la PC en que terminó (la pone el lector).</summary>
    public DateTimeOffset Hora { get; internal set; }

    /// <summary>A dónde fue en la pantalla («a la venta», «no se usó: …»); vacío mientras no se sabe.</summary>
    public string? Entrega { get; internal set; }

    /// <summary>Si la pantalla lo usó (vacío mientras no se sabe).</summary>
    public bool? Usada { get; internal set; }

    /// <summary>«37 35 30 31 0D»: los bytes en hexadecimal, como para compararlos con el manual del lector.</summary>
    public string Hex => DecodificadorLector.Hex(Crudo);

    /// <summary>«7501<CR>»: los bytes como texto, con los que no se ven por su nombre.</summary>
    public string Visible => DecodificadorLector.Visible(Crudo);

    /// <summary>«CR», «CRLF», «una pausa»…</summary>
    public string FinTexto => Fin switch
    {
        FinLeido.CR => "CR",
        FinLeido.LF => "LF",
        FinLeido.CRLF => "CRLF",
        FinLeido.Tab => "un tabulador",
        FinLeido.Etx => "ETX",
        FinLeido.Pausa => "una pausa",
        FinLeido.MuyLarga => "nada (demasiado larga)",
        _ => "nada",
    };
}

/// <summary>
/// Los bytes del lector por puerto, en códigos (piloto alfa). El lector manda cada código como caracteres seguidos y, casi siempre,
/// un carácter de fin (CR, LF o los dos); unos no mandan nada y el código termina con la pausa. Lo que llega partido en varias
/// lecturas del puerto se junta; lo que no puede ser un código (caracteres de control, letras fuera de ASCII, algo larguísimo: el
/// puerto a otra velocidad, ruido al conectar el cable) se descarta entero y se dice por qué, para que nunca entre a la venta algo
/// a medias. Un STX (02) empieza un código. No usa reloj: cada llamada trae su hora (monótona), y el lector le pregunta cuánto
/// esperar a la pausa.
/// </summary>
public sealed class DecodificadorLector(FinDelCodigo fin, TimeSpan silencio)
{
    /// <summary>Lo más largo de un código: un EAN tiene 13 y un Code 128 de la tienda no pasa de 30.</summary>
    public const int LargoMaximo = 64;

    /// <summary>Sin un fin en tantos bytes, es basura (el puerto a otra velocidad): se descarta y se sigue.</summary>
    public const int CrudoMaximo = 256;

    private const byte Stx = 0x02, Etx = 0x03, Eot = 0x04, Tab = 0x09, Lf = 0x0A, Cr = 0x0D;

    private readonly List<byte> _crudo = [];
    private bool _conBasura;
    private TimeSpan _desde, _ultimo;

    /// <summary>La última lectura que terminó con CR o LF: si el otro llega enseguida (CRLF), es de ella.</summary>
    private Lectura? _anterior;

    private TimeSpan _anteriorHora;

    /// <summary>Si hay algo a medias (sin su fin).</summary>
    public bool HayPendiente => _crudo.Count > 0;

    /// <summary>Cuánto falta para que lo pendiente termine por la pausa; vacío si no hay nada pendiente.</summary>
    public TimeSpan? EsperaHastaLaPausa(TimeSpan ahora) =>
        _crudo.Count == 0 ? null : silencio - (ahora - _ultimo) is var falta && falta > TimeSpan.Zero ? falta : TimeSpan.Zero;

    /// <summary>Lo que llegó del puerto de una vez (puede ser un pedazo de un código, o varios); devuelve lo que terminó.</summary>
    public List<Lectura> Agregar(ReadOnlySpan<byte> bytes, TimeSpan ahora)
    {
        var terminadas = new List<Lectura>();
        foreach (var b in bytes)
        {
            // Lo que llegó en un pedazo anterior y ya estaba en pausa termina antes de lo nuevo.
            if (_crudo.Count > 0 && ahora - _ultimo >= silencio && Pausa(ahora) is { } porPausa)
                terminadas.Add(porPausa);
            if (EsFin(b))
            {
                if (_crudo.Count == 0)
                {
                    // CR y LF juntos (o LF y CR): el segundo es de la lectura que terminó con el primero.
                    if (_anterior is { } antes && ahora - _anteriorHora < silencio && EsElOtroDeCrLf(antes.Fin, b))
                    {
                        antes.Crudo = [.. antes.Crudo, b];
                        antes.Fin = FinLeido.CRLF;
                        _anterior = null;
                    }
                    continue;
                }
                _crudo.Add(b);
                terminadas.Add(Terminar(Nombre(b), ahora));
                continue;
            }
            if (Ignorado(b))
            {
                // Con «Fin»: «CR», el LF de un CRLF no estorba (y al revés).
                if (_crudo.Count == 0 && _anterior is { } antes && ahora - _anteriorHora < silencio)
                {
                    antes.Crudo = [.. antes.Crudo, b];
                    if (EsElOtroDeCrLf(antes.Fin, b))
                        antes.Fin = FinLeido.CRLF;
                }
                else if (_crudo.Count > 0)
                    _crudo.Add(b);
                continue;
            }
            _anterior = null;
            if (b == Stx)
            {
                // Empieza un código: lo de antes, sin su fin, no se completa con lo que sigue.
                if (_crudo.Count > 0)
                    terminadas.Add(Descartar("llegó sin su fin, antes de otro código (STX)", FinLeido.SinFin, _ultimo));
                Empezar(b, ahora);
                continue;
            }
            if (_crudo.Count == 0)
                _desde = ahora;
            _crudo.Add(b);
            _ultimo = ahora;
            if (b is < 0x20 or > 0x7E && b != Eot)
                _conBasura = true;
            if (_crudo.Count >= CrudoMaximo)
                terminadas.Add(Descartar($"más de {CrudoMaximo} bytes sin un fin: ¿el puerto a otra velocidad?", FinLeido.MuyLarga, ahora));
        }
        return terminadas;
    }

    /// <summary>
    /// No llegó nada más: si lo pendiente ya cumplió la pausa, termina (como código, o descartado si se esperaba un carácter de fin
    /// y no llegó: un código a medias nunca entra a la venta).
    /// </summary>
    public Lectura? Pausa(TimeSpan ahora)
    {
        if (_crudo.Count == 0 || ahora - _ultimo < silencio)
            return null;
        return fin is FinDelCodigo.Auto or FinDelCodigo.Silencio
            ? Terminar(FinLeido.Pausa, _ultimo)
            : Descartar($"llegó sin su fin ({NombreDelFin()})", FinLeido.SinFin, _ultimo);
    }

    /// <summary>Al cerrar el puerto: lo pendiente se olvida (un código a medias no se completa con lo de otra vez).</summary>
    public void Reiniciar()
    {
        _crudo.Clear();
        _conBasura = false;
        _anterior = null;
    }

    private void Empezar(byte b, TimeSpan ahora)
    {
        _crudo.Clear();
        _crudo.Add(b);
        _conBasura = false;
        _desde = _ultimo = ahora;
    }

    private Lectura Terminar(FinLeido como, TimeSpan hasta)
    {
        var crudo = _crudo.ToArray();
        var texto = new StringBuilder(crudo.Length);
        foreach (var b in crudo)
            if (b is >= 0x20 and <= 0x7E)
                texto.Append((char)b);
        var codigo = texto.ToString().Trim();
        string? descartada = _conBasura ? "trae caracteres que no son de un código (¿el puerto a otra velocidad o con otra paridad?)"
            : codigo.Length == 0 ? "no trae nada"
            : codigo.Length > LargoMaximo ? $"es más largo que un código ({codigo.Length} caracteres)"
            : null;
        var lectura = new Lectura
        {
            Codigo = descartada is null ? codigo : null,
            Descartada = descartada,
            Crudo = crudo,
            Tardo = hasta - _desde,
            Fin = como,
        };
        _crudo.Clear();
        _conBasura = false;
        (_anterior, _anteriorHora) = como is FinLeido.CR or FinLeido.LF ? (lectura, hasta) : (null, default);
        return lectura;
    }

    private Lectura Descartar(string porQue, FinLeido como, TimeSpan hasta)
    {
        var lectura = new Lectura { Descartada = porQue, Crudo = _crudo.ToArray(), Tardo = hasta - _desde, Fin = como };
        _crudo.Clear();
        _conBasura = false;
        _anterior = null;
        return lectura;
    }

    /// <summary>Los caracteres que terminan un código con este «Fin».</summary>
    private bool EsFin(byte b) => fin switch
    {
        FinDelCodigo.CR => b == Cr,
        FinDelCodigo.LF => b == Lf,
        FinDelCodigo.CRLF => b is Cr or Lf,
        FinDelCodigo.Tab => b == Tab,
        _ => b is Cr or Lf or Tab or Etx,
    };

    /// <summary>Los que no terminan ni son parte del código: con «Fin»: «CR», el LF (y al revés); con «Tab», CR y LF.</summary>
    private bool Ignorado(byte b) => fin switch
    {
        FinDelCodigo.CR => b == Lf,
        FinDelCodigo.LF => b == Cr,
        FinDelCodigo.Tab => b is Cr or Lf,
        _ => false,
    };

    private static bool EsElOtroDeCrLf(FinLeido antes, byte b) => (antes == FinLeido.CR && b == Lf) || (antes == FinLeido.LF && b == Cr);

    private static FinLeido Nombre(byte b) => b switch
    {
        Cr => FinLeido.CR,
        Lf => FinLeido.LF,
        Tab => FinLeido.Tab,
        _ => FinLeido.Etx,
    };

    private string NombreDelFin() => fin switch
    {
        FinDelCodigo.LF => "LF",
        FinDelCodigo.CRLF => "CR o LF",
        FinDelCodigo.Tab => "un tabulador",
        _ => "CR",
    };

    /// <summary>«37 35 30 31 0D»: en hexadecimal, como para compararlos con el manual del lector.</summary>
    public static string Hex(IEnumerable<byte> bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>«7501<CR><LF>»: lo que se ve, tal cual, y lo que no, por su nombre o su código.</summary>
    public static string Visible(IEnumerable<byte> bytes)
    {
        var texto = new StringBuilder();
        foreach (var b in bytes)
            texto.Append(b switch
            {
                Cr => "<CR>",
                Lf => "<LF>",
                Tab => "<TAB>",
                Stx => "<STX>",
                Etx => "<ETX>",
                Eot => "<EOT>",
                >= 0x20 and <= 0x7E => ((char)b).ToString(),
                _ => $"<{b:X2}>",
            });
        return texto.ToString();
    }
}
