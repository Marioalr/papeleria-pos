using System.Globalization;
using PapeleriaPos.Impresiones.ControlPrint;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>Las opciones de <c>recopilar-control-print</c> (PapeleriaPos.Herramientas).</summary>
/// <param name="Servidor">La IP o el nombre de la PC de Control Print (la caja principal).</param>
/// <param name="Salida">Dónde va la carpeta con la fecha de hoy.</param>
/// <param name="Desde">Solo los trabajos (y cotizaciones y lecturas) desde ese día, en hora de México; sin él, todos.</param>
/// <param name="Miniaturas">También la miniatura de cada trabajo, en PNG (una petición por trabajo: tarda más).</param>
/// <param name="Huella">La huella SHA-256 que tiene que presentar Control Print; sin ella, la del saludo, comprobada con el código.</param>
public sealed record OpcionesRecopilacion(string Servidor, int Puerto, string Salida, DateOnly? Desde, bool Miniaturas, string? Huella)
{
    /// <summary>Para correrlo sin teclado (las pruebas): el código en esta variable, nunca en la línea de comandos.</summary>
    public const string VariableCodigo = "CONTROLPRINT_CODIGO";

    /// <summary>
    /// ~/.cache/control-print-datos en Linux (la PC de desarrollo); en Windows, %USERPROFILE%\Documents\TPV\control-print-datos,
    /// con los demás datos de la tienda (como scripts\observar-en-vivo.cmd; la carpeta de la cuenta y no la de Documentos de
    /// Windows, que puede estar en OneDrive). Son datos reales: nunca van al repositorio.
    /// </summary>
    public static string SalidaPorOmision => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "TPV", "control-print-datos")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "control-print-datos");

    /// <summary>Las opciones escritas; null con el error para mostrar. Lo que no es una opción no se repite en el error: podría ser el código.</summary>
    public static OpcionesRecopilacion? Leer(IReadOnlyList<string> args, out string? error)
    {
        string? servidor = null, salida = null, huella = null;
        var puerto = ProtocoloControlPrint.PuertoPorOmision;
        DateOnly? desde = null;
        var miniaturas = false;
        for (var i = 0; i < args.Count; i++)
        {
            var opcion = args[i];
            if (opcion == "--miniaturas")
            {
                miniaturas = true;
                continue;
            }
            if (opcion is "--codigo" or "--código" or "--clave")
            {
                error = $"El código no va en la línea de comandos (se queda en el historial): se pide al correrlo, sin que se vea, o va en la variable {VariableCodigo}.";
                return null;
            }
            if (opcion is not ("--servidor" or "--puerto" or "--salida" or "--desde" or "--huella"))
            {
                error = opcion.StartsWith("--", StringComparison.Ordinal)
                    ? $"No conozco la opción «{opcion}»."
                    : "Sobra algo en la línea de comandos (no se repite aquí, por si era el código: ese se escribe cuando lo pide).";
                return null;
            }
            if (i + 1 >= args.Count)
            {
                error = $"Falta lo que va después de {opcion}.";
                return null;
            }
            var valor = args[++i];
            switch (opcion)
            {
                case "--servidor":
                    servidor = valor.Trim();
                    break;
                case "--salida":
                    salida = valor;
                    break;
                case "--puerto" when int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero is > 0 and < 65536:
                    puerto = numero;
                    break;
                case "--puerto":
                    error = $"El puerto es un número de 1 a 65535 (Control Print usa el {ProtocoloControlPrint.PuertoPorOmision}).";
                    return null;
                case "--desde" when DateOnly.TryParseExact(valor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia):
                    desde = dia;
                    break;
                case "--desde":
                    error = "La fecha de --desde va como AAAA-MM-DD (por ejemplo 2026-01-01).";
                    return null;
                case "--huella" when ProtocoloControlPrint.LeerHuella(valor) is { } leida:
                    huella = leida;
                    break;
                case "--huella":
                    error = "La huella son las 64 cifras hexadecimales del SHA-256 del certificado, con o sin «:».";
                    return null;
            }
        }
        if (string.IsNullOrEmpty(servidor) || Uri.CheckHostName(servidor) == UriHostNameType.Unknown || PareceCodigo(servidor))
        {
            error = servidor is null or ""
                ? "Indica con --servidor la IP o el nombre de la PC de Control Print (la caja principal)."
                : PareceCodigo(servidor)
                    ? "Lo de --servidor parece el código de conexión (no se repite aquí): en --servidor va la IP o el nombre de la PC, y el código se escribe cuando lo pide."
                    : $"«{servidor}» no es una IP ni un nombre de PC.";
            return null;
        }
        error = null;
        return new OpcionesRecopilacion(servidor, puerto, salida ?? SalidaPorOmision, desde, miniaturas, huella);
    }

    /// <summary>Como los códigos de Control Print: cuatro grupos de cuatro letras y números con guiones.</summary>
    private static bool PareceCodigo(string texto) =>
        System.Text.RegularExpressions.Regex.IsMatch(texto, "^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$");
}

/// <summary>
/// De dónde sale el código de conexión: del teclado, sin que se vea (se puede volver a pedir si no coincide), o de la
/// variable <see cref="OpcionesRecopilacion.VariableCodigo"/> (una vez). <paramref name="Leer"/> devuelve null si no hay.
/// </summary>
/// <param name="DeDonde">Para decirlo cuando no se teclea («de la variable CONTROLPRINT_CODIGO»).</param>
public sealed record CodigoDeConexion(Func<string?> Leer, bool SePuedeVolverAPedir, string? DeDonde = null);
