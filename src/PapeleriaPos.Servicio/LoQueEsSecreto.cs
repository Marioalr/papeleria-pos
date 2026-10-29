using System.Text;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Lo que es un secreto por su nombre, para no escribirlo nunca tal cual (revisión del PR #145, 2026-10-28): en el registro del
/// servicio (<see cref="RegistroDelServicio"/>), en la configuración que se lleva el .zip del piloto
/// (<see cref="JuntarDatosDelPiloto"/>) y, con la misma regla, en lo que revisa <c>instalacion\juntar-datos-piloto.ps1</c>. Va por
/// el nombre y no por una lista de ajustes: lo que se agregue después (el código de conexión de Control Print,
/// <c>ControlPrint__Codigo</c>) ya se tacha sin tocar esto.
/// </summary>
public static partial class LoQueEsSecreto
{
    /// <summary>
    /// Cómo termina el nombre de un secreto, sin importar mayúsculas: «Eleventa:Clave», «ControlPrint__Codigo», «Telegram:Token»,
    /// «UserPassword», «ISC_PASSWORD», «SmtpPass». Lo mismo en <c>juntar-datos-piloto.ps1</c> (<c>Es-Secreto</c>).
    /// </summary>
    private static readonly string[] Finales =
    [
        "clave", "codigo", "código", "token", "password", "passwd", "pass", "pwd", "pin", "llave", "llaves", "secreto", "secret",
        "contraseña", "contrasena",
    ];

    /// <summary>
    /// Una palabra del nombre que lo hace secreto aunque no termine en ella: «PinHash», «LlaveProtegida», «pinNuevo», «pin_actual».
    /// «Código» no está: «CodigoBarras» es el de un producto.
    /// </summary>
    private static readonly HashSet<string> Palabras = new(StringComparer.OrdinalIgnoreCase)
    {
        "clave", "claves", "password", "passwd", "pwd", "pass", "contraseña", "contrasena", "token", "pin", "secret", "secreto", "llave",
        "llaves",
    };

    /// <summary>
    /// Si el nombre de un ajuste, de un campo o de una variable es el de un secreto: termina en clave, código, token, password,
    /// pass, pwd, PIN, llave, secreto o contraseña, o alguna de sus palabras es una de esas (menos código). «Servidor», «Pintar»,
    /// «Spinner», «Opinion» o «CodigoBarras» no lo son.
    /// </summary>
    public static bool PorSuNombre(string nombre) =>
        Finales.Any(f => nombre.EndsWith(f, StringComparison.OrdinalIgnoreCase)) || PalabrasDe(nombre).Any(Palabras.Contains);

    /// <summary>
    /// En un texto (un mensaje, el detalle de un error, un JSON que llegó): igual, salvo «código» suelto, que en un texto casi
    /// siempre es el de un producto («"codigo": "7501234567890"»). «ControlPrint__Codigo» sí.
    /// </summary>
    private static bool PorSuNombreEnUnTexto(string nombre) =>
        PorSuNombre(nombre) && !nombre.Equals("codigo", StringComparison.OrdinalIgnoreCase) && !nombre.Equals("código", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lo más corto que se tacha por su valor: uno más corto se confundiría con cualquier palabra o número.</summary>
    public const int LargoMinimo = 4;

    /// <summary>
    /// Los valores de la configuración (servicio.json, appsettings, las variables de entorno del servicio) cuyo nombre es el de un
    /// secreto: los que se tachan donde aparezcan. Sin los cortos, ni «true» y «false» (de un ajuste como «PedirPin»).
    /// </summary>
    public static IEnumerable<string> ValoresDe(IConfiguration configuracion) =>
        configuracion.AsEnumerable()
            .Where(a => a.Value is { Length: >= LargoMinimo } valor && !bool.TryParse(valor, out _) && PorSuNombre(a.Key))
            .Select(a => a.Value!)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// El texto con el valor de cada «nombre=valor», «nombre: valor» o «"nombre": "valor"» cuyo nombre es el de un secreto
    /// cambiado por <paramref name="tachado"/>: una cadena de conexión («userpassword=…»), un JSON («"pinNuevo":"4821"»), un
    /// ajuste («Eleventa:Clave=…»). El valor va hasta un espacio, «;», «,», «&amp;» o el cierre del JSON; entre comillas, completo.
    /// </summary>
    public static string TacharEnUnTexto(string texto, string tachado) => Tachar(texto, tachado, hastaPuntoYComa: false, PorSuNombreEnUnTexto);

    /// <summary>
    /// Igual, para el valor de un ajuste que es una cadena de conexión («Data Source=…;Password=…;Pooling=False»): el valor va
    /// hasta el «;» (puede llevar espacios).
    /// </summary>
    public static string TacharEnUnaCadena(string texto, string tachado) => Tachar(texto, tachado, hastaPuntoYComa: true, PorSuNombre);

    /// <summary>Lo que queda de un texto que no se alcanzó a revisar (uno armado a propósito para tardar): nada de él.</summary>
    public const string SinRevisar = "(se omitió: no se alcanzó a revisar si traía un secreto)";

    private static string Tachar(string texto, string tachado, bool hastaPuntoYComa, Func<string, bool> esSecreto)
    {
        StringBuilder? limpio = null;
        var desde = 0;
        try
        {
            // Solo el nombre y su «=» o «:»: el valor se vuelve a revisar (en «opciones: pin=1234» está «pin»).
            for (var nombre = NombreYSeparador().Match(texto); nombre.Success; nombre = nombre.NextMatch())
            {
                var inicio = nombre.Index + nombre.Length;
                if (nombre.Index < desde || !esSecreto(nombre.Groups["nombre"].Value))
                    continue;
                var largo = LargoDelValor(texto, inicio, hastaPuntoYComa);
                if (largo == 0)
                    continue;
                (limpio ??= new StringBuilder(texto.Length)).Append(texto, desde, inicio - desde).Append(tachado);
                desde = inicio + largo;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return SinRevisar;
        }
        return limpio is null ? texto : limpio.Append(texto, desde, texto.Length - desde).ToString();
    }

    private static int LargoDelValor(string texto, int inicio, bool hastaPuntoYComa)
    {
        if (inicio >= texto.Length)
            return 0;
        if (texto[inicio] is '"' or '\'')
        {
            var cierre = texto.IndexOf(texto[inicio], inicio + 1);
            return (cierre < 0 ? texto.Length : cierre + 1) - inicio;
        }
        var fin = inicio;
        while (fin < texto.Length && (hastaPuntoYComa ? texto[fin] is not ';' : !char.IsWhiteSpace(texto[fin]) && texto[fin] is not (';' or ',' or '&' or '"' or '\'' or '}' or ']')))
            fin++;
        return fin - inicio;
    }

    /// <summary>
    /// Un nombre (letras, números, «_», «.», «-», y «:» entre partes, como «Eleventa:Clave») entre comillas o sin ellas, y su «=» o
    /// «:». No empieza con un número: «14:00» no es un nombre. Con largo y partes acotados, y un límite de tiempo: un texto armado
    /// para tardar no detiene al registro (<see cref="SinRevisar"/>).
    /// </summary>
    [GeneratedRegex("""(?<![\p{L}\p{N}_])(?<comilla>["']?)(?<nombre>[\p{L}_][\p{L}\p{N}_.\-]{0,63}(?::[\p{L}_][\p{L}\p{N}_.\-]{0,63}){0,4})\k<comilla>[ \t]*[=:][ \t]*""",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex NombreYSeparador();

    /// <summary>Las palabras de un nombre: separadas por lo que no es letra ni número, y por mayúsculas («LlaveProtegida», «APIToken»).</summary>
    private static IEnumerable<string> PalabrasDe(string nombre)
    {
        var palabra = new StringBuilder();
        for (var i = 0; i < nombre.Length; i++)
        {
            var c = nombre[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (palabra.Length > 0)
                    yield return palabra.ToString();
                palabra.Clear();
                continue;
            }
            // Empieza otra palabra: una mayúscula después de una minúscula («llaveProtegida»), o la última mayúscula de unas
            // seguidas antes de una minúscula («APIToken»).
            var otra = char.IsUpper(c) && palabra.Length > 0
                       && (char.IsLower(nombre[i - 1]) || (char.IsUpper(nombre[i - 1]) && i + 1 < nombre.Length && char.IsLower(nombre[i + 1])));
            if (otra)
            {
                yield return palabra.ToString();
                palabra.Clear();
            }
            palabra.Append(c);
        }
        if (palabra.Length > 0)
            yield return palabra.ToString();
    }
}
