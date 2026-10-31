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
