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

    /// <summary>Lo que queda en caja.json con esas respuestas (sin tocar <paramref name="actual"/>).</summary>
    public static JsonObject Mezclar(JsonObject actual, Respuestas respuestas)
    {
        var objetivo = (JsonObject)actual.DeepClone();
        var servicio = Json.Texto(Json.Valor(objetivo, "Servicio"));
        if (respuestas.Rol == RolDeLaPc.Otra)
        {
            // La caja principal que se dio (y contestó o se confirmó); sin ella, la que ya tenía, y si no, se busca al abrir la caja.
            if (respuestas.Principal is { } principal && BusquedaDeLaPrincipal.Direccion(principal) is { } direccion)
            {
                if (!MismaDireccion(servicio, direccion))
                    Json.Poner(objetivo, "Servicio", direccion.ToString());
            }
            else if (servicio is null)
                Json.Poner(objetivo, "Servicio", ServicioLocal);
        }
        else if (!EsDeEstaPc(servicio))
            // El servicio está en esta misma PC; una dirección de esta PC que ya tenía (localhost, 127.0.0.1) se queda como está.
            Json.Poner(objetivo, "Servicio", ServicioLocal);
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

    /// <summary>Cambia caja.json con las respuestas: solo si algo cambió. El de antes queda en caja.json.anterior.</summary>
    public static Escritura Escribir(string datos, Respuestas respuestas) => Preparar(datos, respuestas).Guardar();

    /// <summary>caja.json con las respuestas, sin escribirlo todavía. Mal escrito (la caja tampoco abriría): FormatException.</summary>
    public static Pendiente Preparar(string datos, Respuestas respuestas)
    {
        var ruta = Ruta(datos);
        var deAntes = RutaDeAntes(datos);
        var origen = File.Exists(ruta) ? ruta : File.Exists(deAntes) ? deAntes : null;
        var antes = origen is null ? null : File.ReadAllBytes(origen);
        var actual = antes is null ? Nueva() : Json.Objeto(antes, "caja.json", comentarios: false);
        var objetivo = Mezclar(actual, respuestas);
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
