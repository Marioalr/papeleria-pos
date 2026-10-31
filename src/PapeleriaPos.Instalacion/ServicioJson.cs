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
        return ComoQuedaria();
    }

    /// <summary>Lo que cambiaría, sin escribir nada.</summary>
    public Escritura ComoQuedaria() => new(Nuevo is null ? [] : Cambios, Completo);

    /// <summary>Si se va a escribir: que ni su carpeta ni el archivo (ni su «.nuevo» o su «.anterior») sean un enlace.</summary>
    public void RevisarQueNoHayEnlaces()
    {
        if (Nuevo is not null)
            Archivos.SinEnlaces(Ruta);
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
            PonerNumero(eleventa, "Caja", respuestas.EleventaCaja);
            PonerNumero(eleventa, "Cajero", respuestas.EleventaCajero);
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
    /// La caja o el cajero de eleventa. Vacío («falta») quita el que tenía, salvo un 0: la plantilla lo trae así y quiere decir lo mismo
    /// (falta), así que se queda como está y servicio.json no cambia.
    /// </summary>
    private static void PonerNumero(JsonObject seccion, string nombre, int? valor)
    {
        if (valor is null && Json.Texto(Json.Valor(seccion, nombre))?.Trim() == "0")
            return;
        PonerOQuitar(seccion, nombre, valor);
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

/// <summary>Un archivo de la PC que el instalador no escribe porque él, su carpeta, su «.nuevo» o su «.anterior» es un enlace.</summary>
public sealed class EnlaceNoPermitido(string mensaje) : IOException(mensaje);

/// <summary>Escribir un archivo de la PC sin dejarlo a medias si se va la luz, y con el de antes a un lado.</summary>
internal static class Archivos
{
    /// <summary>
    /// Primero a un archivo aparte, hasta el disco, y después se cambia de un jalón (como guarda la caja su caja.json). Si había uno,
    /// queda como «.anterior» junto a él.
    /// <para>
    /// Lo corre el ayudante como administrador, y la carpeta de la caja (C:\ProgramData\PapeleriaPos\Caja) la puede cambiar cualquier
    /// cuenta de Windows: por eso nunca escribe a través de un enlace (simbólico, «junction» o punto de montaje), que lo mandaría a otro
    /// lado. Antes de escribir revisa que no lo sean ni la carpeta ni el archivo, su «.nuevo» o su «.anterior»; y los crea nuevos
    /// (CreateNew), así que un enlace que aparezca en ese momento hace que falle, en lugar de seguirlo.
    /// </para>
    /// </summary>
    public static void Guardar(string ruta, byte[]? antes, byte[] nuevo)
    {
        ruta = Path.GetFullPath(ruta);
        if (Path.GetDirectoryName(ruta) is { Length: > 0 } carpeta)
            Directory.CreateDirectory(carpeta);
        SinEnlaces(ruta);
        if (antes is { Length: > 0 })
        {
            var anterior = ruta + ".anterior";
            EscribirNuevo(anterior + ".nuevo", antes);
            File.Move(anterior + ".nuevo", anterior, overwrite: true);
        }
        var temporal = ruta + ".nuevo";
        EscribirNuevo(temporal, nuevo);
        File.Move(temporal, ruta, overwrite: true);
    }

    /// <summary>Un archivo que no existía (si quedó uno de una vez anterior, se quita antes); nunca a través de un enlace.</summary>
    private static void EscribirNuevo(string ruta, byte[] bytes)
    {
        if (EsEnlace(ruta))
            throw Enlace(ruta);
        if (File.Exists(ruta))
            File.Delete(ruta);
        using var archivo = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        archivo.Write(bytes);
        archivo.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Que no sean un enlace ni la carpeta de ese archivo ni la de arriba (la de caja.json y C:\ProgramData\PapeleriaPos, que una cuenta
    /// sin permisos de administrador podría crear antes como enlace), ni él, su «.nuevo», su «.anterior» o el «.nuevo» de este. Si alguno
    /// lo es: <see cref="EnlaceNoPermitido"/>, sin escribir nada.
    /// </summary>
    public static void SinEnlaces(string ruta)
    {
        ruta = Path.GetFullPath(ruta);
        var carpeta = Path.GetDirectoryName(ruta);
        foreach (var revisar in new[] { carpeta is null ? null : Path.GetDirectoryName(carpeta), carpeta, ruta, ruta + ".nuevo", ruta + ".anterior",
                     ruta + ".anterior.nuevo" })
            if (revisar is { Length: > 0 } && EsEnlace(revisar))
                throw Enlace(revisar);
    }

    /// <summary>
    /// Si en esa ruta hay un enlace, sin seguirlo (también uno que no lleva a nada): cualquier «reparse point» de Windows (simbólico,
    /// «junction», punto de montaje) o un enlace simbólico de Linux.
    /// </summary>
    public static bool EsEnlace(string ruta)
    {
        FileSystemInfo info = Directory.Exists(ruta) ? new DirectoryInfo(ruta) : new FileInfo(ruta);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return true;
        try
        {
            return info.LinkTarget is not null;
        }
        catch (IOException)
        {
            // No es un enlace que se pueda leer; si fuera un reparse point, ya lo dijeron sus atributos.
            return false;
        }
    }

    private static EnlaceNoPermitido Enlace(string ruta) =>
        new($"{ruta} es un enlace a otro lado (simbólico o «junction»): por seguridad, el instalador no escribe ahí y no cambió nada. Quita el "
            + "enlace (o pon ahí la carpeta o el archivo de verdad) y vuelve a correr el instalador.");
}
