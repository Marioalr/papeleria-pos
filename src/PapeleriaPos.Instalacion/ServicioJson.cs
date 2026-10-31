using System.Text;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>Lo que cambió al escribir un archivo de la PC: los nombres de lo que cambió (vacío: nada, el archivo no se tocó).</summary>
/// <param name="Completo">servicio.json se tuvo que volver a escribir entero (sin sus comentarios de antes).</param>
public sealed record Escritura(IReadOnlyList<string> Cambios, bool Completo = false)
{
    public bool Cambio => Cambios.Count > 0;
}

/// <summary>
/// Un archivo de la PC listo para escribirse: primero se preparan servicio.json y caja.json, y solo si los dos se entienden se escriben
/// (no queda uno cambiado y el otro no).
/// </summary>
/// <param name="Nuevo">Lo que se escribe; vacío si no cambió nada (el archivo no se toca).</param>
public sealed record Pendiente(string Ruta, byte[]? Antes, byte[]? Nuevo, IReadOnlyList<string> Cambios, bool Completo = false)
{
    public Escritura Guardar()
    {
        if (Nuevo is not null)
            Archivos.Guardar(Ruta, Antes, Nuevo);
        return new Escritura(Nuevo is null ? [] : Cambios, Completo);
    }
}

/// <summary>
/// La configuración del servicio de esta PC, <c>C:\ProgramData\PapeleriaPos\servicio.json</c> (src/PapeleriaPos.Servicio/ConfiguracionDeLaPc.cs):
/// lo que el instalador pone ahí de sus respuestas. Solo toca tres secciones, y en ellas solo lo que pregunta; lo demás se queda como
/// está, también los comentarios (<see cref="TextoJsonc"/>):
/// <list type="bullet">
/// <item>«Eleventa», si la caja convive con eleventa: <c>Activo</c> en true y la conexión (servidor, base, caja, cajero, gbak y la carpeta
/// de sus respaldos). <c>Escribir</c> nunca se prende: en una sección nueva va en false; si el dueño ya lo prendió, se queda. Si la
/// respuesta es «No», la sección no se toca: el puente se apaga con el instructivo (docs/pruebas-en-tienda.md, «Regresar o terminar el
/// piloto»).</item>
/// <item>«ControlPrint»: la IP de la PC de Control Print; con «No», la sección se quita (como «Para quitarlo» del instructivo).</item>
/// <item>«Instalacion»: <c>UnaSolaCaja</c> en true para «La única caja»; en «Caja principal», en false solo si ya estaba.</item>
/// </list>
/// Nunca la clave de eleventa ni el código de Control Print: van solo en las variables del servicio.
/// </summary>
public static class ServicioJson
{
    public const string Archivo = "servicio.json";

    /// <summary>Lo que dice arriba un servicio.json que el instalador tuvo que escribir entero.</summary>
    private static readonly string[] Encabezado =
    [
        "La configuración de ESTA PC para el servicio de Papelería POS (docs/instalacion.md). La escribió el instalador:",
        "el archivo de antes quedó en servicio.json.anterior. La clave de eleventa nunca va aquí (va en la variable del servicio).",
    ];

    /// <summary>Lo que queda en servicio.json con esas respuestas (sin tocar <paramref name="actual"/>).</summary>
    public static JsonObject Mezclar(JsonObject actual, Respuestas respuestas)
    {
        var objetivo = (JsonObject)actual.DeepClone();
        if (respuestas.Eleventa == true)
        {
            var nueva = Json.Valor(objetivo, "Eleventa") is not JsonObject;
            var eleventa = Json.Seccion(objetivo, "Eleventa");
            Json.Poner(eleventa, "Activo", true);
            // Escribirle a eleventa lo empieza el dueño con el instructivo: aquí nunca se prende, ni se apaga si ya lo prendió. En una
            // sección nueva va en false a la vista, como en el instructivo (y el puerto de siempre); a una que ya está no se le agrega.
            if (nueva)
                Json.Poner(eleventa, "Escribir", false);
            Json.Poner(eleventa, "Servidor", respuestas.EleventaServidor);
            if (nueva)
                Json.Poner(eleventa, "Puerto", 3050);
            Json.Poner(eleventa, "Base", respuestas.EleventaBase);
            PonerOQuitar(eleventa, "Caja", respuestas.EleventaCaja);
            PonerOQuitar(eleventa, "Cajero", respuestas.EleventaCajero);
            PonerOQuitar(eleventa, "Gbak", respuestas.EleventaGbak);
            PonerOQuitar(eleventa, "CarpetaRespaldos", respuestas.EleventaRespaldos);
        }
        if (respuestas.ControlPrint == true)
            Json.Poner(Json.Seccion(objetivo, "ControlPrint"), "Servidor", respuestas.ControlPrintServidor);
        else if (respuestas.ControlPrint == false)
            Json.Quitar(objetivo, "ControlPrint");
        if (respuestas.Rol == RolDeLaPc.Unica)
            Json.Poner(Json.Seccion(objetivo, "Instalacion"), "UnaSolaCaja", true);
        else if (respuestas.Rol == RolDeLaPc.Principal && Json.Valor(objetivo, "Instalacion") is JsonObject instalacion
            && Json.Clave(instalacion, "UnaSolaCaja") is not null)
            Json.Poner(instalacion, "UnaSolaCaja", false);
        return objetivo;
    }

    private static void PonerOQuitar(JsonObject seccion, string nombre, JsonNode? valor)
    {
        if (valor is null)
            Json.Quitar(seccion, nombre);
        else
            Json.Poner(seccion, nombre, valor);
    }

    /// <summary>
    /// servicio.json con las respuestas: solo lo que cambió, y si nada cambió no se toca. El de antes queda en servicio.json.anterior. Si
    /// no existe (no debería: instalar-servicio.ps1 y actualizar.ps1 ponen la plantilla), se hace uno. Mal escrito: FormatException.
    /// </summary>
    public static Escritura Escribir(string ruta, Respuestas respuestas) => Preparar(ruta, respuestas).Guardar();

    public static Pendiente Preparar(string ruta, Respuestas respuestas)
    {
        byte[]? antes = File.Exists(ruta) ? File.ReadAllBytes(ruta) : null;
        var texto = antes is null || Encoding.UTF8.GetString(Json.SinBom(antes)).Trim().Length == 0
            ? new TextoJsonc(Encoding.UTF8.GetBytes("{" + Environment.NewLine + "}" + Environment.NewLine))
            : new TextoJsonc(antes);
        var actual = texto.Objeto();
        var objetivo = Mezclar(actual, respuestas);
        var cambios = TextoJsonc.Cambios(actual, objetivo);
        if (cambios.Count == 0)
            return new Pendiente(ruta, antes, null, []);
        var completo = false;
        try
        {
            texto.Igualar(objetivo);
        }
        catch (InvalidOperationException)
        {
            texto = TextoJsonc.Completo(objetivo, Encabezado, texto.ConBom, texto.Salto);
            completo = true;
        }
        return new Pendiente(ruta, antes, texto.Bytes, cambios, completo);
    }
}

/// <summary>Escribir un archivo de la PC sin dejarlo a medias si se va la luz, y con el de antes a un lado.</summary>
internal static class Archivos
{
    /// <summary>
    /// Primero a un archivo aparte, hasta el disco, y después se cambia de un jalón (como guarda la caja su caja.json). Si había uno,
    /// queda como «.anterior» junto a él.
    /// </summary>
    public static void Guardar(string ruta, byte[]? antes, byte[] nuevo)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(ruta)) is { Length: > 0 } carpeta)
            Directory.CreateDirectory(carpeta);
        if (antes is { Length: > 0 })
            File.WriteAllBytes(ruta + ".anterior", antes);
        var temporal = ruta + ".nuevo";
        using (var archivo = new FileStream(temporal, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            archivo.Write(nuevo);
            archivo.Flush(flushToDisk: true);
        }
        File.Move(temporal, ruta, overwrite: true);
    }
}
