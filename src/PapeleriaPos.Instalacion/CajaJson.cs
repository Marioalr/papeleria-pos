using System.Text;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// La configuración de la caja de esta PC, <c>C:\ProgramData\PapeleriaPos\Caja\caja.json</c> (ConfiguracionCaja, en
/// src/PapeleriaPos.Caja/MainWindow.xaml.cs). El instalador escribe solo lo que ya existe ahí y pregunta: <c>Servicio</c>,
/// <c>Impresora</c>, <c>ColaImpresion</c> y <c>Lector</c>, sobre lo que ya tiene. Nunca quita su alta (<c>CajaId</c>,
/// <c>LlaveProtegida</c>, <c>LlaveDelServicio</c>, <c>DireccionDeConfianza</c>) ni lo demás que traiga.
/// <para>
/// Si la caja guardaba su configuración donde lo hacían las versiones de antes (C:\ProgramData\PapeleriaPos\caja.json) y todavía no la
/// tiene en la carpeta Caja, se parte de esa y se escribe en la carpeta Caja, que es la que lee primero.
/// </para>
/// <para>
/// La única vez que toca el alta es al terminar una mudanza de la base a otra PC (<c>escribir --mudada-a</c>,
/// <see cref="ConversionDeLaCaja"/>): la caja de la PC de donde salió la base pasa a hablarle a la PC nueva.
/// </para>
/// </summary>
public static class CajaJson
{
    /// <summary>La del servicio de la misma PC, como la trae el caja.json del programa.</summary>
    public const string ServicioLocal = "http://localhost:5080";

    public static string Ruta(string datos) => Path.Combine(datos, "Caja", "caja.json");

    /// <summary>Donde la guardaban las versiones de antes (ConfiguracionCaja.GuardadaAntes).</summary>
    public static string RutaDeAntes(string datos) => Path.Combine(datos, "caja.json");

    /// <summary>La de una PC que todavía no tiene: como el caja.json del programa.</summary>
    public static JsonObject Nueva() => new()
    {
        ["Servicio"] = ServicioLocal,
        ["CajaId"] = null,
        ["Impresora"] = "",
        ["ColaImpresion"] = null,
    };

    /// <summary>
    /// Lo que queda en caja.json con esas respuestas (sin tocar <paramref name="actual"/>). Con <paramref name="conversion"/>, además, la
    /// caja de la PC de donde salió la base queda hablándole a la PC nueva; sin ella, lo de siempre (el alta no se toca).
    /// </summary>
    public static JsonObject Mezclar(JsonObject actual, Respuestas respuestas, ConversionDeLaCaja? conversion = null)
    {
        var objetivo = (JsonObject)actual.DeepClone();
        var servicio = Json.Texto(Json.Valor(objetivo, "Servicio"));
        if (respuestas.Rol == RolDeLaPc.Otra)
        {
            // La caja principal que se dio (y contestó o se confirmó); sin ella, la que ya tenía, y si no, se busca al abrir la caja.
            if (respuestas.Principal is { } principal && BusquedaDeLaPrincipal.Direccion(principal) is { } direccion)
                PonerDireccion(objetivo, "Servicio", direccion);
            else if (servicio is null)
                Json.Poner(objetivo, "Servicio", ServicioLocal);
        }
        else if (!EsDeEstaPc(servicio))
            // El servicio está en esta misma PC; una dirección de esta PC que ya tenía (localhost, 127.0.0.1) se queda como está.
            Json.Poner(objetivo, "Servicio", ServicioLocal);
        if (conversion is not null)
            Convertir(objetivo, conversion);
        // Vacío y sin la clave es lo mismo para la caja: no se agrega solo para decir que no hay.
        if (respuestas.Impresora.Length > 0 || Json.Clave(objetivo, "Impresora") is not null)
            Json.Poner(objetivo, "Impresora", respuestas.Impresora);
        if (respuestas.Cola is not null || Json.Clave(objetivo, "ColaImpresion") is not null)
            Json.Poner(objetivo, "ColaImpresion", respuestas.Cola);
        if (respuestas.LectorPuerto is { } puerto)
        {
            // Sobre lo que ya tenía (la paridad, los bits, el fin del código que se copiaron de eleventa): solo el puerto y la velocidad.
            var lector = Json.Seccion(objetivo, "Lector");
            // «com3» y «COM3» son el mismo puerto para la caja: el que ya tiene se queda como está escrito.
            if (!string.Equals(Json.Texto(Json.Valor(lector, "Puerto"))?.Trim(), puerto, StringComparison.OrdinalIgnoreCase))
                Json.Poner(lector, "Puerto", puerto);
            Json.Poner(lector, "Baudios", respuestas.LectorBaudios);
        }
        else if (Json.Clave(objetivo, "Lector") is { } clave && objetivo[clave] is not null)
            objetivo[clave] = null;
        return objetivo;
    }

    /// <summary>
    /// Al terminar una mudanza: la caja de esta PC, que le hablaba al servicio de esta misma PC, queda hablándole a la PC a donde se
    /// llevó la base. <c>Servicio</c> y <c>DireccionDeConfianza</c>, en esa dirección (la de confianza es la única en que la caja aprende
    /// la llave de un servicio); y <c>LlaveDelServicio</c>, la que presentó la PC nueva al probar quién es, o vacía para que la caja la
    /// aprenda al abrirla: la que tenía es la del servicio de esta PC, que no se muda (pos.llave), y con ella guardada la caja
    /// rechazaría a la PC nueva. <c>CajaId</c> y <c>LlaveProtegida</c> (su alta) se quedan, salvo que la PC nueva ya no reconozca esa
    /// llave: entonces se vacían para que la caja pida su alta, en lugar de quedarse sin poder conectarse ni pedirla.
    /// </summary>
    private static void Convertir(JsonObject objetivo, ConversionDeLaCaja conversion)
    {
        PonerDireccion(objetivo, "Servicio", conversion.Direccion);
        PonerDireccion(objetivo, "DireccionDeConfianza", conversion.Direccion);
        // Vacía y sin la clave es lo mismo para la caja: no se agrega solo para decir que no hay.
        if (conversion.LlaveDelServicio is not null || Json.Valor(objetivo, "LlaveDelServicio") is not null)
            Json.Poner(objetivo, "LlaveDelServicio", conversion.LlaveDelServicio);
        if (conversion.AltaOtraVez)
            foreach (var delAlta in new[] { "CajaId", "LlaveProtegida" })
                if (Json.Valor(objetivo, delAlta) is not null)
                    Json.Poner(objetivo, delAlta, null);
    }

    /// <summary>Esa dirección, escrita como la escribe la caja («http://192.168.1.10:5080/»); la que ya es esa se queda como está escrita.</summary>
    private static void PonerDireccion(JsonObject objetivo, string clave, Uri direccion)
    {
        if (!MismaDireccion(Json.Texto(Json.Valor(objetivo, clave)), direccion))
            Json.Poner(objetivo, clave, direccion.ToString());
    }

    /// <summary>Cambia caja.json con las respuestas: solo si algo cambió. El de antes queda en caja.json.anterior.</summary>
    public static Escritura Escribir(string datos, Respuestas respuestas, ConversionDeLaCaja? conversion = null) => Preparar(datos, respuestas, conversion).Guardar();

    /// <summary>caja.json con las respuestas, sin escribirlo todavía. Mal escrito (la caja tampoco abriría): FormatException.</summary>
    public static Pendiente Preparar(string datos, Respuestas respuestas, ConversionDeLaCaja? conversion = null)
    {
        var ruta = Ruta(datos);
        var deAntes = RutaDeAntes(datos);
        var origen = File.Exists(ruta) ? ruta : File.Exists(deAntes) ? deAntes : null;
        var antes = origen is null ? null : File.ReadAllBytes(origen);
        var actual = antes is null ? Nueva() : Json.Objeto(antes, "caja.json", comentarios: false);
        var objetivo = Mezclar(actual, respuestas, conversion);
        var cambios = TextoJsonc.Cambios(actual, objetivo).Select(c => $"caja.json: {c}").ToList();
        if (origen is null)
            cambios.Insert(0, "caja.json (nuevo)");
        else if (origen != ruta)
            cambios.Insert(0, "caja.json (pasa a la carpeta Caja)");
        if (cambios.Count == 0)
            return new Pendiente(ruta, antes, null, []);
        var texto = Json.Escribir(objetivo).Replace("\n", Environment.NewLine) + Environment.NewLine;
        return new Pendiente(ruta, origen == ruta ? antes : null, Encoding.UTF8.GetBytes(texto), cambios);
    }

    private static bool EsDeEstaPc(string? servicio) => Uri.TryCreate(servicio, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static bool MismaDireccion(string? servicio, Uri direccion) =>
        Uri.TryCreate(servicio, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, direccion.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, direccion.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == direccion.Port;
}

/// <summary>
/// Lo que se pidió en la línea de órdenes de <c>escribir</c> para la caja de una PC que termina una mudanza, tal como llegó:
/// <c>--mudada-a</c>, <c>--llave-del-servicio</c> y <c>--alta</c>. Todavía sin revisar (<see cref="ConversionDeLaCaja.De"/>).
/// </summary>
public sealed record ConversionPedida(string? MudadaA, string? LlaveDelServicio = null, string? Alta = null)
{
    /// <summary>La de esas opciones; vacía si no viene ninguna de las tres (lo de siempre: el alta no se toca).</summary>
    public static ConversionPedida? De(IReadOnlyDictionary<string, string> opciones) =>
        opciones.ContainsKey("mudada-a") || opciones.ContainsKey("llave-del-servicio") || opciones.ContainsKey("alta")
            ? new ConversionPedida(opciones.GetValueOrDefault("mudada-a"), opciones.GetValueOrDefault("llave-del-servicio"), opciones.GetValueOrDefault("alta"))
            : null;
}

/// <summary>
/// La conversión de la caja de la PC de donde salió la base, al terminar la mudanza (<c>escribir --mudada-a</c>; docs/instalacion.md):
/// a qué PC le habla desde ahora y con qué de su alta se queda. Lo que hace con caja.json está en <see cref="CajaJson"/>.
/// </summary>
/// <param name="Direccion">La de la PC que ya tiene la base (su caja principal desde ahora).</param>
/// <param name="LlaveDelServicio">La llave pública que esa PC presentó al probar quién es; vacía, la caja la aprende al abrirla.</param>
/// <param name="AltaOtraVez">Esa PC ya no reconoce la llave de esta caja: se le quita el alta, para que la pida al abrirla.</param>
public sealed record ConversionDeLaCaja(Uri Direccion, string? LlaveDelServicio, bool AltaOtraVez)
{
    /// <summary>Como se pide quitar el alta: <c>--alta otra-vez</c>.</summary>
    public const string OtraVez = "otra-vez";

    /// <summary>
    /// La que se pidió, ya revisada. Lo que no se puede usar se dice (<see cref="RespuestaNoValida"/>, con la clave de la respuesta que
    /// lo corrige si la hay) en lugar de dejar caja.json a medias: de él depende que la caja vuelva a conectarse.
    /// </summary>
    public static ConversionDeLaCaja De(ConversionPedida pedida, Respuestas respuestas)
    {
        // Sin la dirección no hay conversión, y el alta no se toca: lo demás, solo, no dice a dónde.
        if (pedida.MudadaA is not { } mudadaA)
            throw new RespuestaNoValida("", "«--llave-del-servicio» y «--alta» son de la conversión de la caja al terminar una mudanza: van con «--mudada-a».");
        if (respuestas.Rol != RolDeLaPc.Otra)
            throw new RespuestaNoValida("rol",
                "Dejar la caja hablándole a la PC nueva («--mudada-a») es de la PC de donde salió la base, que queda como «Otra caja»: no va con «Caja "
                + "principal» ni con «La única caja».");
        if (BusquedaDeLaPrincipal.Direccion(mudadaA) is not { } direccion)
            throw new RespuestaNoValida("principal",
                $"La dirección de la caja principal no se entiende (está «{mudadaA.Trim()}»): va su IP, como 192.168.1.10 (con su puerto, de 1 a 65535, si no es el 5080).");
        // La respuesta «principal» es la que pone «Servicio» en todas las corridas que siguen: con otra, la caja quedaría hablándole a
        // una PC y confiando en otra.
        if (respuestas.Principal is { } principal && BusquedaDeLaPrincipal.Direccion(principal) is { } deLaRespuesta && !deLaRespuesta.Equals(direccion))
            throw new RespuestaNoValida("principal",
                $"La caja se muda a «{BusquedaDeLaPrincipal.Corta(direccion)}», pero la dirección de la caja principal que se contestó es "
                + $"«{BusquedaDeLaPrincipal.Corta(deLaRespuesta)}»: tienen que ser la misma PC.");
        var llave = pedida.LlaveDelServicio?.Trim() is { Length: > 0 } texto ? texto : null;
        if (llave is not null && !IdentidadDeLaPrincipal.EsLlaveDeServicio(llave))
            throw new RespuestaNoValida("",
                "«--llave-del-servicio» no es la llave pública de un servicio: va tal cual la dice «llave-del-servicio» en «probar --esta-caja si».");
        var alta = (pedida.Alta ?? "").Trim();
        if (alta.Length > 0 && !string.Equals(alta, OtraVez, StringComparison.OrdinalIgnoreCase))
            throw new RespuestaNoValida("", $"«--alta» solo va como {OtraVez} (está «{alta}»).");
        return new ConversionDeLaCaja(direccion, llave, alta.Length > 0);
    }
}
