using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>Lo que contestó una página por HTTP (eSCL o Web Config): su código y su contenido, o por qué no.</summary>
public sealed record LecturaHttp(string Nombre, string Url, int? Codigo, string? Cuerpo, string? Error)
{
    public string Estado => Error is { } e ? $"no contestó ({e})" : $"HTTP {Codigo}";
}

/// <summary>Lo que contestó por IPP: la dirección que sirvió, los atributos de la impresora y sus trabajos.</summary>
public sealed record LecturaIpp(string? Url, string? Error, Ipp.Respuesta? Atributos, Ipp.Respuesta? Terminados, Ipp.Respuesta? Pendientes)
{
    public IEnumerable<Ipp.Grupo> Trabajos =>
        (Pendientes?.Trabajos ?? []).Concat(Terminados?.Trabajos ?? []);
}

/// <summary>El «@BDC ST2» de Epson, como lo lee el módulo (serie y contadores de color y B/N), con todos sus campos.</summary>
public sealed record LecturaSt2(string? Serie, long? Color, long? BlancoYNegro, IReadOnlyDictionary<byte, byte[]> Campos, string? Error);

/// <summary>
/// La foto de una impresora en un momento: todo lo que contesta por la red, solo leyendo. Para el modo «antes y después» de
/// diagnosticar-impresoras (docs/pruebas-en-tienda.md).
/// </summary>
public sealed partial record FotoImpresora(string Host, DateTimeOffset Inicio, DateTimeOffset Fin, IReadOnlyList<Caminata> Snmp, LecturaSt2? St2,
    LecturaIpp Ipp, IReadOnlyList<LecturaHttp> Escl, IReadOnlyList<LecturaHttp> Web)
{
    /// <summary>Lo que dice ser (hrDeviceDescr o sysDescr).</summary>
    public string? Modelo => Dato("1.3.6.1.2.1.25.3.2.1.3.1") is { Length: > 0 } d ? d : Dato("1.3.6.1.2.1.1.1.0");

    public string? Serie => St2?.Serie is { Length: > 0 } s ? s : Dato("1.3.6.1.2.1.43.5.1.1.17.1");

    public string? Dato(string oid) =>
        Snmp.SelectMany(c => c.Entradas).FirstOrDefault(e => e.Oid == oid)?.Texto.Trim('\0', ' ');

    /// <summary>
    /// Todo lo que se leyó, por partes, como «clave → valor», para compararlo con otra foto. Una parte que no contestó lleva su error y
    /// no se compara.
    /// </summary>
    public IReadOnlyList<Parte> Partes()
    {
        var partes = new List<Parte>();
        foreach (var c in Snmp)
        {
            var valores = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in c.Entradas)
                valores.TryAdd(e.Oid, e.Texto);
            partes.Add(new Parte($"snmp {c.Raiz}", valores, c.Entradas.Count == 0 ? c.Error ?? "no tiene nada en ese árbol" : null,
                c.Cortada && c.Entradas.Count > 0 ? c.Entradas[^1].Oid : null));
        }
        if (St2 is { } st2)
        {
            var valores = new Dictionary<string, string>(StringComparer.Ordinal);
            if (st2.Serie is { } serie) valores["serie"] = serie;
            if (st2.Color is { } color) valores["color (campo 0x36)"] = color.ToString(CultureInfo.InvariantCulture);
            if (st2.BlancoYNegro is { } bn) valores["B/N (campo 0x36)"] = bn.ToString(CultureInfo.InvariantCulture);
            foreach (var (codigo, datos) in st2.Campos)
                valores[$"campo 0x{codigo:X2}"] = Convert.ToHexString(datos);
            partes.Add(new Parte("st2", valores, st2.Error, null));
        }
        var atributos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Ipp.Atributos?.Impresora?.Atributos ?? [])
            atributos.TryAdd(a.Nombre, a.Texto);
        partes.Add(new Parte("ipp impresora", atributos, Ipp.Atributos is null ? Ipp.Error ?? "no contestó" : null, null));
        var trabajos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in Ipp.Trabajos)
        {
            var id = t["job-id"] ?? "?";
            foreach (var a in t.Atributos)
                trabajos.TryAdd($"trabajo {id} {a.Nombre}", a.Texto);
        }
        partes.Add(new Parte("ipp trabajos", trabajos, Ipp.Terminados is null && Ipp.Pendientes is null ? Ipp.Error ?? "no contestó" : null, null));
        foreach (var l in Escl)
            partes.Add(new Parte($"escl {l.Nombre}", Xml(l), l.Error, null));
        foreach (var l in Web)
            partes.Add(new Parte($"web {l.Nombre}", l.Cuerpo is { } html && l.Codigo == 200 ? NumerosDeLaPagina(html) : new Dictionary<string, string> { ["(código HTTP)"] = $"{l.Codigo}" },
                l.Error, null));
        return partes;
    }

    /// <summary>Un XML de eSCL como «ruta → valor» (los repetidos con su número); si no es XML, solo su código.</summary>
    private static Dictionary<string, string> Xml(LecturaHttp l)
    {
        var valores = new Dictionary<string, string>(StringComparer.Ordinal) { ["(código HTTP)"] = $"{l.Codigo}" };
        if (l.Codigo != 200 || l.Cuerpo is not { } cuerpo)
            return valores;
        try
        {
            var doc = XDocument.Parse(cuerpo);
            void Recorrer(XElement e, string ruta)
            {
                if (!e.HasElements)
                {
                    valores.TryAdd(ruta, e.Value.Trim());
                    return;
                }
                foreach (var grupo in e.Elements().GroupBy(x => x.Name.LocalName))
                {
                    var i = 0;
                    foreach (var hijo in grupo)
                        Recorrer(hijo, grupo.Count() > 1 ? $"{ruta}/{grupo.Key}[{++i}]" : $"{ruta}/{grupo.Key}");
                }
            }
            Recorrer(doc.Root!, doc.Root!.Name.LocalName);
        }
        catch (System.Xml.XmlException)
        {
            valores["(no es XML)"] = $"{cuerpo.Length} caracteres";
        }
        return valores;
    }

    /// <summary>
    /// Los números de una página de Web Config que parecen contadores: «Etiqueta: 1,234» o una etiqueta y su número en el renglón de abajo,
    /// y el alto de cada barra (las de la tinta y la caja de mantenimiento son imágenes con su alto). Los repetidos llevan #2, #3…
    /// </summary>
    public static Dictionary<string, string> NumerosDeLaPagina(string html)
    {
        var numeros = new Dictionary<string, string>(StringComparer.Ordinal);
        void Agregar(string clave, string valor)
        {
            clave = Regex.Replace(clave, @"\s+", " ").Trim().TrimEnd(':', '：').Trim();
            if (clave.Length > 70)
                clave = clave[..70];
            var final = clave;
            for (var i = 2; numeros.ContainsKey(final); i++)
                final = $"{clave} #{i}";
            numeros[final] = valor;
        }
        foreach (Match barra in Barra().Matches(html))
            Agregar($"barra {barra.Groups[1].Value}", barra.Groups[2].Value);

        var texto = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        texto = Regex.Replace(texto, "<[^>]+>", "\n");
        var renglones = WebUtility.HtmlDecode(texto).Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        string? anterior = null;
        foreach (var renglon in renglones)
        {
            if (SoloNumero().Match(renglon) is { Success: true } solo)
            {
                if (anterior is not null)
                    Agregar(anterior, Limpio(solo.Groups[1].Value));
                continue;
            }
            if (ConNumero().Match(renglon) is { Success: true } con && con.Groups[1].Value.Any(char.IsLetter))
                Agregar(con.Groups[1].Value, Limpio(con.Groups[2].Value));
            anterior = renglon.Any(char.IsLetter) ? renglon : null;
        }
        return numeros;
    }

    private static string Limpio(string numero) => numero.Replace(",", "").Replace(".", "").Replace(" ", "");

    [GeneratedRegex(@"src=['""][^'""]*?([\w\-]+)\.(?:png|gif|jpg)['""][^>]*?\bheight=['""]?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Barra();

    [GeneratedRegex(@"^(\d{1,3}(?:,\d{3})+|\d+)(?:\s*(?:%|sheets?|pages?|hojas?|p[aá]ginas?|caras?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SoloNumero();

    [GeneratedRegex(@"^(.*?\D)\s*[:：]?\s*(\d{1,3}(?:,\d{3})+|\d+)\s*(?:%|sheets?|pages?|hojas?|p[aá]ginas?|caras?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ConNumero();
}

/// <summary>Una parte de la foto: «clave → valor», o por qué no se leyó.</summary>
/// <param name="CortadaEn">Si se cortó por el tope: el último OID leído (después de él no se compara).</param>
public sealed record Parte(string Nombre, IReadOnlyDictionary<string, string> Valores, string? Error, string? CortadaEn);

/// <summary>Cuánto se lee de cada impresora en cada foto.</summary>
public sealed record OpcionesFoto
{
    /// <summary>Los árboles SNMP que se caminan (el de sistema es corto: dice qué impresora es y cuánto lleva prendida).</summary>
    public IReadOnlyList<(string Raiz, string Nombre)> Arboles { get; init; } =
    [
        ("1.3.6.1.2.1.1", "sistema"),
        ("1.3.6.1.2.1.43", "printer-mib"),
        ("1.3.6.1.2.1.25.3", "host-resources"),
        ("1.3.6.1.4.1.1248", "epson"),
        ("1.3.6.1.4.1.2699", "pwg"),
    ];

    public int MaximoPorArbol { get; init; } = 5000;
    public TimeSpan LimitePorArbol { get; init; } = TimeSpan.FromSeconds(60);
    public int PuertoSnmp { get; init; } = 161;
    public int EsperaSnmpMs { get; init; } = 1500;

    /// <summary>Las páginas de Web Config: la de arriba y las que ya conoce el módulo (PRTINFO es la de la barra de la caja).</summary>
    public IReadOnlyList<(string Nombre, string Ruta)> PaginasWeb { get; init; } =
    [
        ("inicio", "/"),
        ("INDEX", "/PRESENTATION/HTML/TOP/INDEX.HTML"),
        ("PRTINFO", "/PRESENTATION/HTML/TOP/PRTINFO.HTML"),
    ];
}

/// <summary>
/// Toma las fotos de una impresora (<see cref="FotoImpresora"/>) y vigila sus trabajos por IPP. <b>Solo lee:</b> SNMP Get, GetNext y
/// GetBulk; IPP Get-Printer-Attributes y Get-Jobs; GET de eSCL y de Web Config, sin usuario ni contraseña. Nunca truena: lo que no contesta
/// queda con su error. El <see cref="HttpClient"/> lo pone quien llama (en la tienda, uno por impresora que acepta su certificado propio;
/// en las pruebas, uno falso).
/// </summary>
public sealed class LectorImpresora(string host, HttpClient http, TimeProvider reloj, OpcionesFoto? opciones = null)
{
    /// <summary>El «st» de Epson (respuesta «@BDC ST2»), el mismo OID que pide el módulo (PrinterStatusReader).</summary>
    public const string EpsonSt = "1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.115.116.1.0.1";

    private readonly OpcionesFoto _opciones = opciones ?? new();
    private string? _urlIpp;

    public string Host { get; } = host;

    /// <summary>La dirección de IPP que contestó en la última foto (vacío si ninguna): la que se vigila.</summary>
    public string? UrlIpp => _urlIpp;

    public async Task<FotoImpresora> FotoAsync(CancellationToken ct)
    {
        var inicio = reloj.GetLocalNow();
        var muertos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SNMP por un lado y HTTP por otro: una impresora lenta en su página no atrasa el SNMP.
        var snmp = SnmpAsync(ct);
        var http = HttpAsync(muertos, ct);
        await Task.WhenAll(snmp, http);
        var (caminatas, st2) = await snmp;
        var (ipp, escl, web) = await http;
        return new FotoImpresora(Host, inicio, reloj.GetLocalNow(), caminatas, st2, ipp, escl, web);
    }

    private async Task<(List<Caminata>, LecturaSt2?)> SnmpAsync(CancellationToken ct)
    {
        var cliente = new CaminataSnmp(Host, _opciones.PuertoSnmp, esperaMs: _opciones.EsperaSnmpMs);
        var caminatas = new List<Caminata>();
        foreach (var (raiz, _) in _opciones.Arboles)
            caminatas.Add(await cliente.CaminarAsync(raiz, _opciones.MaximoPorArbol, _opciones.LimitePorArbol, ct));
        if (cliente.SinRespuesta)
            return (caminatas, new LecturaSt2(null, null, null, new Dictionary<byte, byte[]>(), "no contestó (se saltó)"));
        try
        {
            // El mismo «st» que pide el módulo (PrinterStatusReader): un GET, como siempre.
            var st = await cliente.PedirAsync(EpsonSt, ct);
            if (st is null)
                return (caminatas, new LecturaSt2(null, null, null, new Dictionary<byte, byte[]>(), "no lo tiene"));
            return EpsonSt2.TryParse(st.Datos, out var r)
                ? (caminatas, new LecturaSt2(r.Serial, r.ColorPages, r.MonoPages, r.Fields, null))
                : (caminatas, new LecturaSt2(null, null, null, new Dictionary<byte, byte[]>(), $"contestó sin «ST2» ({st.Datos.Length} bytes)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (caminatas, new LecturaSt2(null, null, null, new Dictionary<byte, byte[]>(), ex.Message));
        }
    }

    private async Task<(LecturaIpp, List<LecturaHttp>, List<LecturaHttp>)> HttpAsync(HashSet<string> muertos, CancellationToken ct)
    {
        var ipp = await IppAsync(muertos, ct);
        var escl = new List<LecturaHttp>();
        foreach (var nombre in new[] { "ScannerCapabilities", "ScannerStatus" })
            escl.Add(await GetAsync(nombre, $"/eSCL/{nombre}", muertos, ct));
        var web = new List<LecturaHttp>();
        foreach (var (nombre, ruta) in _opciones.PaginasWeb)
            web.Add(await GetAsync(nombre, ruta, muertos, ct));
        return (ipp, escl, web);
    }

    /// <summary>Un GET por https y, si no conecta, por http. Un 404 es una respuesta (no tiene esa página): no se reintenta.</summary>
    private async Task<LecturaHttp> GetAsync(string nombre, string ruta, HashSet<string> muertos, CancellationToken ct)
    {
        string? error = null;
        var url = "";
        foreach (var esquema in new[] { "https", "http" })
        {
            url = $"{esquema}://{Host}{ruta}";
            if (muertos.Contains(esquema))
            {
                error ??= $"{esquema} no conecta (se saltó)";
                continue;
            }
            try
            {
                using var respuesta = await http.GetAsync(url, ct);
                return new LecturaHttp(nombre, url, (int)respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync(ct), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (NoConecta(ex))
                    muertos.Add(esquema);
                error = Mensaje(ex);
            }
        }
        return new LecturaHttp(nombre, url, null, null, error);
    }

    private async Task<LecturaIpp> IppAsync(HashSet<string> muertos, CancellationToken ct)
    {
        string? error = null;
        var candidatas = _urlIpp is { } conocida
            ? new[] { conocida }.Concat(Candidatas().Where(u => u != conocida))
            : Candidatas();
        foreach (var url in candidatas)
        {
            try
            {
                var atributos = await PreguntarIppAsync(url, Ipp.PedirAtributos(url, 1), ct);
                Ipp.Respuesta? terminados = null, pendientes = null;
                string? errorTrabajos = null;
                try
                {
                    terminados = await PreguntarIppAsync(url, Ipp.PedirTrabajos(url, 2, "completed"), ct);
                    pendientes = await PreguntarIppAsync(url, Ipp.PedirTrabajos(url, 3, "not-completed"), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errorTrabajos = $"Get-Jobs: {Mensaje(ex)}";
                }
                _urlIpp = url;
                return new LecturaIpp(url, errorTrabajos, atributos, terminados, pendientes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = $"{url}: {Mensaje(ex)}";
            }
        }
        _urlIpp = null;
        return new LecturaIpp(null, error, null, null, null);
    }

    private IEnumerable<string> Candidatas() => [$"https://{Host}:631/ipp/print", $"http://{Host}:631/ipp/print"];

    /// <summary>Get-Jobs de los terminados y de los que faltan, en la dirección que contestó en la foto (para vigilar mientras se imprime).</summary>
    public async Task<IReadOnlyList<Ipp.Grupo>> TrabajosAsync(CancellationToken ct)
    {
        if (_urlIpp is not { } url)
            return [];
        var pendientes = await PreguntarIppAsync(url, Ipp.PedirTrabajos(url, 4, "not-completed"), ct);
        var terminados = await PreguntarIppAsync(url, Ipp.PedirTrabajos(url, 5, "completed"), ct);
        return [.. pendientes.Trabajos, .. terminados.Trabajos];
    }

    private async Task<Ipp.Respuesta> PreguntarIppAsync(string url, byte[] pregunta, CancellationToken ct)
    {
        using var contenido = new ByteArrayContent(pregunta);
        contenido.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        using var respuesta = await http.PostAsync(url, contenido, ct);
        if (!respuesta.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)respuesta.StatusCode}");
        return Ipp.Leer(await respuesta.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>No conectó o no contestó a tiempo: lo que sigue de esa foto por ese esquema se salta.</summary>
    private static bool NoConecta(Exception ex) => ex is HttpRequestException or OperationCanceledException or IOException;

    private static string Mensaje(Exception ex) => ex switch
    {
        OperationCanceledException => "tardó demasiado",
        HttpRequestException { InnerException: SocketException s } => s.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "rechazó la conexión",
            SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => "no conecta",
            _ => s.Message,
        },
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "falló el TLS",
        InvalidDataException => $"respuesta que no se entiende ({ex.Message})",
        _ => ex.Message,
    };

    /// <summary>
    /// El cliente HTTP de la tienda para una impresora: acepta el certificado propio de la impresora (solo en este diagnóstico, y solo
    /// porque este cliente no habla con nadie más), no espera más de 3 s a conectar ni más de 10 s por respuesta.
    /// </summary>
    public static HttpClient ClienteParaLaTienda() => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
}
