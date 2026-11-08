using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Un trámite que se hace en su página oficial desde la caja («Servicios»; docs/tramites.md, 2026-10-31): la página tal cual, los
/// sitios que puede abrir, el producto con que se cobra y cómo se llama su documento. El cajero escribe y da clic en la página
/// oficial; el POS nunca la llena ni toca su captcha. Uno más (la cita de verificación, los del IMSS) es otra entrada en
/// <see cref="Tramites.Todos"/>.
/// </summary>
/// <param name="Clave">Su comando en el cuadro de captura («/curp») y lo que lo encuentra en la búsqueda: sin acentos, en minúsculas.</param>
/// <param name="Nombre">Como se le dice en la tienda, y en el nombre del trabajo de impresión («T-0042 · CURP»).</param>
/// <param name="Descripcion">Lo que se hace, en la lista de los servicios.</param>
/// <param name="Direccion">Donde empieza: la página oficial.</param>
/// <param name="DireccionALaVista">La dirección en grande, arriba del panel, para no caer en sitios falsos que cobran por ella.</param>
/// <param name="Sitios">
/// Los sitios a los que puede ir la página (y sus marcos) dentro de la caja: el nombre exacto, o «*.» y el dominio para sus subdominios.
/// Lo que la página carga de otros lados (sus scripts, el reCAPTCHA) no se filtra (<see cref="PoliticaDeNavegacion"/>).
/// </param>
/// <param name="CodigoProducto">El código del producto con que se cobra, como en eleventa (CURP, $10).</param>
/// <param name="NombreDelDocumento">Cómo se llama el documento al compartirlo con el cliente (el de la página trae sus datos).</param>
/// <param name="Instrucciones">Lo que hace el cajero en la página oficial.</param>
public sealed record Tramite(
    string Clave, string Nombre, string Descripcion, Uri Direccion, string DireccionALaVista, IReadOnlyList<string> Sitios, string CodigoProducto,
    string NombreDelDocumento, string Instrucciones)
{
    /// <summary>A dónde puede ir su página dentro de la caja.</summary>
    public PoliticaDeNavegacion Politica() => new(Sitios);

    /// <summary>«/curp»: como se escribe en el cuadro de captura.</summary>
    public string Comando => $"/{Clave}";
}

/// <summary>Los trámites de la caja, en el orden en que salen en «Servicios». Hoy solo la CURP (el dueño, 2026-10-31).</summary>
public static class Tramites
{
    /// <summary>
    /// La CURP en gob.mx/curp (RENAPO): la página consulta por www.gob.mx y baja el PDF de consultas.curp.gob.mx; el reCAPTCHA de
    /// Google es invisible (docs/tramites.md, «CURP»).
    /// </summary>
    public static Tramite Curp { get; } = new(
        "curp", "CURP", "Consultar e imprimir la CURP en la página oficial (RENAPO). La consulta es gratis: se cobra el servicio.",
        new Uri("https://www.gob.mx/curp/"), "gob.mx/curp", ["gob.mx", "www.gob.mx", "curp.gob.mx", "*.curp.gob.mx"], "CURP", "CURP.pdf",
        "Escribe (o pega) la CURP en la página oficial y da «Buscar»; si el cliente no la sabe, usa «Datos personales». Revisa con el "
        + "cliente el nombre y la fecha que salen, y da «Descargar pdf».");

    public static IReadOnlyList<Tramite> Todos { get; } = [Curp];

    /// <summary>El de un comando («curp», «/curp», «CURP»); vacío si no hay.</summary>
    public static Tramite? PorClave(string? clave)
    {
        var buscada = Texto.Normalizar((clave ?? "").Trim().TrimStart('/'));
        return buscada.Length == 0 ? null : Todos.FirstOrDefault(t => Texto.Normalizar(t.Clave) == buscada);
    }

    /// <summary>
    /// El del rápido que se llama como el trámite («CURP», Alt+2): abre el trámite en lugar de vender lo suyo, que se queda como «Solo
    /// cobrar» (docs/tramites.md, decisión 6). El nombre del rápido, sin acentos ni mayúsculas, y sin lo que lleve entre paréntesis
    /// («CURP (10)»).
    /// </summary>
    public static Tramite? DelRapido(string nombreDelRapido)
    {
        var nombre = nombreDelRapido.Split('(')[0];
        return PorClave(nombre) ?? Todos.FirstOrDefault(t => Texto.Normalizar(t.Nombre) == Texto.Normalizar(nombre.Trim()));
    }

    /// <summary>
    /// Los que salen arriba de la búsqueda (escribir «curp» en el cuadro): lo escrito, de cuatro letras o más y en una palabra, es el
    /// principio de su clave o de su nombre. Con menos («cur», camino de «curitas») no sale: estorbaría a lo que se busca.
    /// </summary>
    public static IReadOnlyList<Tramite> Buscados(string texto)
    {
        var buscado = Texto.Normalizar(texto.Trim());
        if (buscado.Length < 4 || buscado.Contains(' '))
            return [];
        return Todos.Where(t => Texto.Normalizar(t.Clave).StartsWith(buscado, StringComparison.Ordinal)
            || Texto.Normalizar(t.Nombre).StartsWith(buscado, StringComparison.Ordinal)).ToList();
    }
}

/// <summary>
/// A dónde puede ir la página de un trámite dentro de la caja (docs/tramites.md, «Solo sitios oficiales»; 2026-10-31). Se revisa lo
/// que se navega (la página, sus marcos y las ventanas nuevas que abra), no lo que cada página carga para verse (sus scripts, sus
/// imágenes, sus consultas): ahí el sitio oficial decide, como en Edge, y lo suyo cambia sin avisar.
/// <list type="bullet">
/// <item>La página y sus marcos: solo https a los sitios del trámite;</item>
/// <item>los marcos del reCAPTCHA de Google (la CURP lo usa, invisible), solo como marco y solo en su ruta;</item>
/// <item>en blanco (about:blank) y lo que la propia página arma (blob: de un sitio del trámite);</item>
/// <item>el documento atrapado, solo ese archivo y solo para enseñarlo.</item>
/// </list>
/// Nada más: ni http, ni otros sitios (un enlace a Facebook no se abre ahí), ni data:, ni otros archivos de la PC.
/// </summary>
public sealed class PoliticaDeNavegacion(IReadOnlyList<string> sitios)
{
    /// <summary>Los marcos del reCAPTCHA (el sitio y el principio de su ruta): solo como marcos de la página.</summary>
    public static IReadOnlyList<(string Sitio, string Ruta)> MarcosDelCaptcha { get; } =
        [("www.google.com", "/recaptcha/"), ("www.recaptcha.net", "/recaptcha/"), ("recaptcha.google.com", "/recaptcha/")];

    private string? _archivo;

    /// <summary>El documento atrapado se puede enseñar en el panel (solo ese archivo).</summary>
    public void PermitirArchivo(string ruta) => _archivo = Path.GetFullPath(ruta);

    /// <summary>Si la página puede ir ahí.</summary>
    /// <param name="esMarco">Es un marco de la página (el del reCAPTCHA), no la página.</param>
    public bool Permite(Uri? direccion, bool esMarco = false)
    {
        if (direccion is null || !direccion.IsAbsoluteUri)
            return false;
        switch (direccion.Scheme)
        {
            case "about":
                return direccion.AbsoluteUri is "about:blank" or "about:srcdoc";
            case "blob":
                // blob:https://www.gob.mx/… : lo que armó una página del trámite.
                return Uri.TryCreate(direccion.AbsolutePath, UriKind.Absolute, out var dentro) && dentro.Scheme == Uri.UriSchemeHttps && EsDelTramite(dentro.Host);
            case "file":
                return !esMarco && _archivo is { } archivo && direccion.IsFile
                    && string.Equals(Path.GetFullPath(direccion.LocalPath), archivo, StringComparison.OrdinalIgnoreCase);
            case "https":
                if (EsDelTramite(direccion.Host))
                    return true;
                return esMarco && MarcosDelCaptcha.Any(m => string.Equals(m.Sitio, direccion.Host, StringComparison.OrdinalIgnoreCase)
                    && direccion.AbsolutePath.StartsWith(m.Ruta, StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private bool EsDelTramite(string host) =>
        sitios.Any(s => s.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(s[1..], StringComparison.OrdinalIgnoreCase) && host.Length > s.Length - 1
            : string.Equals(host, s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Una impresora que se ofrece para un trámite.</summary>
/// <param name="LaMideElAgente">
/// Es una cola que vigila el agente de impresión de esta caja: sus copias de más llegan solas a F2. En otra, no se cuentan: se cobran a mano.
/// </param>
public sealed record ImpresoraParaTramite(string Nombre, bool LaMideElAgente);

/// <summary>
/// En qué impresoras se imprime un trámite (revisión de servicios/curp, 2026-10-31; por su puerto, segunda revisión; la del agente siempre,
/// tercera). La copia incluida y las de más solo se cuentan bien en la cola que vigila el agente de la caja (<c>ColaImpresion</c>): va
/// primero, siempre, salvo que imprima en otra PC (la compartida de la caja principal, por su nombre «\\PRINCIPAL\…» o por su puerto, o el
/// escritorio remoto: ahí lo mediría Control Print y saldría por cobrar otra vez). De las demás, solo las de esta PC por USB (o paralelo o
/// serie) o por la red directo a la impresora (TCP/IP), y no las que no son una impresora (guardar como PDF o XPS, OneNote, el fax: el
/// documento del cliente quedaría en la PC) ni las del soporte remoto (AnyDesk, TeamViewer): por su puerto, por las palabras de su nombre o
/// por el nombre exacto de su driver, nunca por un pedazo del driver (tercera revisión: una Konica o una Kyocera con driver «…XPS» sí
/// imprime). Ahí las copias de más se cobran a mano. Un puerto WSD o de otra clase no se sabe a dónde imprime: no se ofrece.
/// </summary>
public static class ImpresorasParaTramites
{
    /// <summary>Los puertos de lo que no es una impresora: guardar como archivo, XPS, el fax, nada.</summary>
    private static readonly string[] PuertosQueNoImprimen = ["PORTPROMPT:", "XPSPORT:", "SHRFAX:", "NUL:", "FILE:", "PDF:"];

    /// <summary>Lo que dice el nombre de la cola de lo que no es una impresora, o de lo que imprime en otra PC (el soporte remoto).</summary>
    private static readonly string[] NombresQueNoImprimenAqui =
    [
        "PDF", "XPS", "ONENOTE", "FAX", "DOCUMENT WRITER", "SEND TO", "ANYDESK", "TEAMVIEWER", "SPLASHTOP", "RUSTDESK", "CHROME REMOTE",
        "REMOTE DESKTOP", "ESCRITORIO REMOTO", "REDIRECT", "REDIRIG", "REDIRECCION",
    ];

    /// <summary>Los drivers de Windows que no son una impresora, con su nombre exacto (no un pedazo: «…XPS» de una láser sí imprime).</summary>
    private static readonly HashSet<string> DriversQueNoImprimen = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft XPS Document Writer", "Microsoft XPS Document Writer v4", "Microsoft Print To PDF", "Microsoft Shared Fax Driver",
        "Send to Microsoft OneNote 16 Driver", "Send To Microsoft OneNote 2013 Driver", "Send To Microsoft OneNote 2010 Driver",
        "Microsoft Software Printer Driver", "Remote Desktop Easy Print",
    };

    /// <summary>TCP/IP estándar sin el registro: «IP_192.168.1.197», «192.168.1.197» o «192.168.1.197_1».</summary>
    private static readonly Regex PuertoTcpIp = new(@"^(IP_.+|\d{1,3}(\.\d{1,3}){3}(_\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Una impresora conectada a esta PC: USB (o el Dot4 de las multifuncionales), paralelo o serie.</summary>
    private static readonly Regex PuertoDeEstaPc = new(@"^(USB\d+|DOT4_\d+|LPT\d+:?|COM\d+:?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Los del escritorio remoto (TS001…): imprimen en la PC desde donde se conectaron.</summary>
    private static readonly Regex PuertoDeEscritorioRemoto = new(@"^TS\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ImpresoraParaTramite> Elegir(IEnumerable<ColaDeLaPc> colas, string? colaDelAgente) =>
        ElegirDeVarias(colas, string.IsNullOrWhiteSpace(colaDelAgente) ? [] : [colaDelAgente]);

    /// <summary>
    /// Con varias colas que vigila el agente (2026-11-03): todas van primero, en su orden y como medidas. En cualquiera de ellas el agente
    /// reconoce el trabajo del trámite y sus copias de más salen en F2: decir «cóbralas a mano» haría que se cobraran dos veces.
    /// </summary>
    public static IReadOnlyList<ImpresoraParaTramite> ElegirDeVarias(IEnumerable<ColaDeLaPc> colas, IReadOnlyList<string> colasDelAgente)
    {
        var todas = colas.ToList();
        var delAgente = colasDelAgente
            .Select(nombre => todas.FirstOrDefault(c => string.Equals(c.Nombre, nombre.Trim(), StringComparison.OrdinalIgnoreCase) && ImprimeAqui(c)))
            .OfType<ColaDeLaPc>().Distinct().ToList();
        var lista = delAgente.Select(c => new ImpresoraParaTramite(c.Nombre, LaMideElAgente: true)).ToList();
        lista.AddRange(todas.Where(c => !delAgente.Contains(c) && EsImpresoraDeEstaPc(c)).Select(c => new ImpresoraParaTramite(c.Nombre, LaMideElAgente: false)));
        return lista;
    }

    /// <summary>
    /// No imprime en otra PC: ni la compartida (por su nombre o por su puerto) ni el escritorio remoto. Es lo único que se le pide a la del
    /// agente (la que él mide): su nombre y su driver no cuentan.
    /// </summary>
    public static bool ImprimeAqui(ColaDeLaPc cola)
    {
        var puerto = cola.Puerto?.Trim() ?? "";
        return !(cola.DeOtraPc || cola.Nombre.StartsWith(@"\\", StringComparison.Ordinal) || puerto.StartsWith(@"\\", StringComparison.Ordinal)
            || cola.Tipo == TipoDePuerto.Compartida || PuertoDeEscritorioRemoto.IsMatch(puerto));
    }

    /// <summary>
    /// Lo que no es una impresora (o es del soporte remoto): por su puerto, por las palabras del nombre de la cola, o por el nombre exacto de
    /// su driver. Solo para las que no son la del agente.
    /// </summary>
    public static bool NoEsUnaImpresora(ColaDeLaPc cola)
    {
        var puerto = cola.Puerto?.Trim() ?? "";
        if (PuertosQueNoImprimen.Any(p => string.Equals(puerto, p, StringComparison.OrdinalIgnoreCase)))
            return true;
        var nombre = cola.Nombre.ToUpperInvariant();
        return NombresQueNoImprimenAqui.Any(nombre.Contains) || (cola.Driver is { } driver && DriversQueNoImprimen.Contains(driver.Trim()));
    }

    /// <summary>
    /// Las demás (no la del agente): que imprima aquí, que sea una impresora, y solo por USB, paralelo o serie, o por TCP/IP directo a la
    /// impresora.
    /// </summary>
    public static bool EsImpresoraDeEstaPc(ColaDeLaPc cola)
    {
        if (!ImprimeAqui(cola) || NoEsUnaImpresora(cola))
            return false;
        var puerto = cola.Puerto?.Trim() ?? "";
        return cola.Tipo switch
        {
            TipoDePuerto.TcpIp => true,
            TipoDePuerto.Local => PuertoDeEstaPc.IsMatch(puerto),
            null => PuertoTcpIp.IsMatch(puerto) || PuertoDeEstaPc.IsMatch(puerto),
            _ => false,
        };
    }
}
