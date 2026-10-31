using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>
/// Una tienda que empieza de cero (la instalación desde cero, 2026-10-31): la base nueva no tiene sucursal ni usuarios, y la caja de
/// la misma PC del servicio pide la tienda, la cuenta del dueño y esa caja («Tienda nueva»). Lo usan el servicio, que la crea, y la
/// caja, que la pide: los dos revisan lo mismo y dicen lo mismo.
/// </summary>
public static partial class TiendaNueva
{
    /// <summary>La de una tienda que empieza de cero: la única de su base («M», como la que deja importar-eleventa).</summary>
    public const string Clave = "M";

    public const string SucursalPorOmision = "Matriz";
    public const string CajaPorOmision = "Caja 1";
    public const string SeriePorOmision = "A";

    /// <summary>El encabezado y el pie que se proponen; se cambian después en Configuración › Ticket, como siempre.</summary>
    public static readonly IReadOnlyList<string> EncabezadoPorOmision = ["PAPELERÍA"];

    public static readonly IReadOnlyList<string> PiePorOmision = ["Gracias por su compra"];

    /// <summary>Lo más largo del nombre de la sucursal: sale en la entrada de cada caja.</summary>
    public const int LargoSucursal = 40;

    /// <summary>Lo más largo del nombre de una caja: sale arriba en la venta y en la entrada.</summary>
    public const int LargoCaja = 30;

    /// <summary>Lo más largo del usuario con que se entra donde todavía se escribe (dar de alta una PC como caja).</summary>
    public const int LargoUsuario = 20;

    /// <summary>
    /// A otra PC que pide dar de alta una caja mientras la tienda no existe (2026-10-31): la tienda se crea primero en la caja
    /// principal, la PC del servicio.
    /// </summary>
    public const string TerminaEnLaPrincipal =
        "Termina primero la instalación en la caja principal: ahí se crea la tienda y la cuenta del dueño. Después, aquí se da de alta esta PC.";

    /// <summary>Su código en el error (<c>ErrorDto.Codigo</c>): la caja lo reconoce sin leer el mensaje.</summary>
    public const string CodigoTerminaEnLaPrincipal = "tienda-nueva";

    /// <summary>A quien pide crear la tienda cuando ya existe: solo se crea una vez, en una base nueva.</summary>
    public const string YaExiste =
        "Esta tienda ya está creada: la tienda y la cuenta del dueño solo se crean una vez, en una base nueva. Para dar de alta esta PC, entra con "
        + "el usuario y el PIN de quien tiene «Dar de alta y quitar cajas».";

    public const string CodigoYaExiste = "tienda-creada";

    /// <summary>
    /// A otra PC que pide dar de alta una caja en la que se instaló como «La única caja» (2026-10-31): así se escogió en el
    /// instalador, y en el instalador se cambia.
    /// </summary>
    public const string UnaSolaCaja =
        "La caja principal se instaló como «La única caja»: no se pueden dar de alta otras PC. Para conectar esta, en la caja principal corre "
        + "otra vez el instalador y elige «Caja principal»; después corre aquí el instalador y elige «Otra caja».";

    public const string CodigoUnaSolaCaja = "una-sola-caja";

    /// <summary>El nombre de la sucursal, de una caja o del dueño, sin espacios de más; vacío si no hay nada escrito.</summary>
    public static string Limpio(string? texto) =>
        string.Join(' ', (texto ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Lo que falta o no sirve del nombre de la sucursal; vacío si está bien.</summary>
    public static string? RevisarSucursal(string? nombre) => Limpio(nombre) switch
    {
        "" => "Escribe el nombre de la sucursal: «Matriz», «Sucursal 2»…",
        { Length: > LargoSucursal } => $"El nombre de la sucursal va hasta {LargoSucursal} letras.",
        _ => null,
    };

    /// <summary>Lo que falta o no sirve del nombre del dueño (como en «Usuarios y permisos»); vacío si está bien.</summary>
    public static string? RevisarNombre(string? nombre) => Limpio(nombre) switch
    {
        "" => "Escribe tu nombre: así sale tu ficha en la entrada de la caja.",
        { Length: > FichasEntrada.LargoNombre } => $"El nombre va hasta {FichasEntrada.LargoNombre} letras: con el nombre y una inicial basta.",
        _ => null,
    };

    /// <summary>
    /// El usuario como se guarda (en minúsculas, sin espacios), o vacío si no sirve: con él se entra donde todavía se escribe, como al
    /// dar de alta otra PC como caja.
    /// </summary>
    public static string? Usuario(string? usuario) =>
        (usuario ?? "").Trim().ToLowerInvariant() is var limpio && UsuarioValido().IsMatch(limpio) ? limpio : null;

    /// <summary>
    /// El usuario que se propone para un nombre, como en «Usuarios y permisos»: la primera palabra, en minúsculas y sin acentos
    /// («Mario Álvarez» → «mario»). Se puede cambiar.
    /// </summary>
    public static string UsuarioPropuesto(string? nombre)
    {
        var primera = Texto.Normalizar(Limpio(nombre)).Replace('Ñ', 'N').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant())
            .FirstOrDefault(p => p.Length > 0) ?? "";
        return primera.Length > LargoUsuario ? primera[..LargoUsuario] : primera;
    }

    public static string? RevisarUsuario(string? usuario) =>
        (usuario ?? "").Trim().Length == 0 ? "Escribe tu usuario: con él y tu PIN se dan de alta las otras cajas."
        : Usuario(usuario) is null ? $"El usuario va de 2 a {LargoUsuario} letras o números, sin espacios ni acentos: «mario», por ejemplo."
        : null;

    /// <summary>Lo que falta o no sirve de la caja (su nombre y su serie de folios); vacío si está bien.</summary>
    public static string? RevisarCaja(string? nombre, string? serie)
    {
        var limpio = Limpio(nombre);
        if (limpio.Length == 0)
            return "Escribe el nombre de esta caja: «Caja 1», por ejemplo.";
        if (limpio.Length > LargoCaja)
            return $"El nombre de la caja va hasta {LargoCaja} letras.";
        return Serie(serie) is null ? "La serie de folios va de 1 a 3 letras (A, B, C…): con ella salen los folios de esta caja, A-000001." : null;
    }

    /// <summary>La serie de folios en mayúsculas, o vacía si no es de 1 a 3 letras.</summary>
    public static string? Serie(string? serie) =>
        (serie ?? "").Trim().ToUpperInvariant() is var limpia && SerieValida().IsMatch(limpia) ? limpia : null;

    /// <summary>Los renglones del encabezado o del pie como se escribieron, sin los vacíos de las orillas (los de en medio también se quitan al guardar).</summary>
    public static IReadOnlyList<string> Renglones(string? texto) =>
        (texto ?? "").Replace("\r", "").Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();

    /// <summary>Lo que no cabe en el ticket de lo escrito (los mismos límites que Configuración › Ticket); vacío si cabe.</summary>
    public static string? RevisarRenglones(IReadOnlyList<string> renglones, string parte)
    {
        if (renglones.Count > ConfiguracionTicket.RenglonesMaximos)
            return $"El {parte} admite hasta {ConfiguracionTicket.RenglonesMaximos} renglones.";
        return renglones.FirstOrDefault(r => r.Length > ConfiguracionTicket.Columnas) is { } largo
            ? $"«{largo}» no cabe en el ticket: son {ConfiguracionTicket.Columnas} caracteres por renglón."
            : null;
    }

    [GeneratedRegex("^[a-z0-9]{2,20}$")]
    private static partial Regex UsuarioValido();

    [GeneratedRegex("^[A-Z]{1,3}$")]
    private static partial Regex SerieValida();
}
