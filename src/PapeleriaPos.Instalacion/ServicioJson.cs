using System.Globalization;
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
/// <para>
/// La mudanza de la base a otra PC (<c>escribir --mudanza</c>; docs/instalacion.md) es el único caso en que pone algo más, y solo dentro
/// de «Eleventa»: el puente llega con la base como estaba en la otra PC (<see cref="ResumenDeMudanza"/>). De allá salen <c>Escribir</c>,
/// el puerto y el usuario, el cajero si aquí no se dio, los ajustes del puente y <c>Cajas</c> (cada caja que ya vendió, con el número de
/// caja de eleventa con que escribía). Nunca un archivo ajeno: todo se pone sobre el servicio.json de ESTA PC.
/// </para>
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

    /// <summary>
    /// Lo que queda en servicio.json con esas respuestas (sin tocar <paramref name="actual"/>). Con <paramref name="mudanza"/>, además, el
    /// puente con eleventa como estaba en la PC de donde viene la base; sin ella, lo de siempre.
    /// </summary>
    public static JsonObject Mezclar(JsonObject actual, Respuestas respuestas, ResumenDeMudanza? mudanza = null)
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
            if (mudanza is not null)
                PonerLoDeLaMudanza(eleventa, respuestas, mudanza);
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

    /// <summary>
    /// El puente como estaba en la PC de donde viene la base, sobre la sección «Eleventa» que ya lleva las respuestas (el servidor, la
    /// base, el gbak y la carpeta de sus respaldos son siempre los de ESTA PC: de allá nunca sale una ruta, ni la clave).
    /// </summary>
    private static void PonerLoDeLaMudanza(JsonObject eleventa, Respuestas respuestas, ResumenDeMudanza mudanza)
    {
        // El único camino por el que el instalador escribe Escribir: es el mismo puente, mudado. Si ya escribía, sigue escribiendo (apagarlo
        // dejaría a la caja que se mudó sin mandar a eleventa); si solo leía, solo lee.
        Json.Poner(eleventa, "Escribir", mudanza.EleventaEscribir);
        PonerSiNoEsElDeSiempre(eleventa, "Puerto", mudanza.EleventaPuerto.ToString(CultureInfo.InvariantCulture),
            ResumenDeMudanza.PuertoDeSiempre.ToString(CultureInfo.InvariantCulture), mudanza.EleventaPuerto);
        PonerSiNoEsElDeSiempre(eleventa, "Usuario", mudanza.EleventaUsuario, ResumenDeMudanza.UsuarioDeSiempre, mudanza.EleventaUsuario);
        // El cajero de omisión: el que aquí se contestó; si no, el de allá. La caja de omisión, en cambio, es SOLO la de las respuestas
        // (ya la puso o la quitó Mezclar): la de allá era la de la caja que se mudó, y aquí la tomaría toda caja que no esté en Cajas.
        if (respuestas.EleventaCajero is null && mudanza.EleventaCajero is { } cajero)
            Json.Poner(eleventa, "Cajero", cajero);
        // Cada caja que ya vendió sigue escribiendo como su caja de eleventa de antes: lo que viaja pendiente en la base se busca allá con
        // ese número, y con otro se escribiría dos veces. Dentro de la entrada que ya haya, sin quitarle su cajero ni tocar otras series.
        foreach (var caja in mudanza.EleventaCajas)
        {
            var suya = Json.Seccion(Json.Seccion(eleventa, "Cajas"), caja.Serie);
            Json.Poner(suya, "Caja", caja.Caja);
            if (caja.Cajero is { } suCajero)
                Json.Poner(suya, "Cajero", suCajero);
        }
        foreach (var (nombre, valor) in mudanza.EleventaAjustes)
            Json.Poner(eleventa, nombre, valor.DeepClone());
    }

    /// <summary>
    /// Que quede lo mismo que allá: se escribe si no es el valor de siempre, o si aquí ya había escrito otro (para no llenar la sección con
    /// lo que el servicio ya trae por omisión).
    /// </summary>
    private static void PonerSiNoEsElDeSiempre(JsonObject seccion, string nombre, string deAlla, string deSiempre, JsonNode valor)
    {
        var aqui = Json.Texto(Json.Valor(seccion, nombre))?.Trim();
        if (!string.Equals(aqui ?? deSiempre, deAlla, StringComparison.OrdinalIgnoreCase))
            Json.Poner(seccion, nombre, valor);
    }

    /// <summary>
    /// Lo que no puede quedar en el servicio.json de una mudanza, revisado sobre <paramref name="objetivo"/> (lo que se va a escribir, ya
    /// con lo de la otra PC): cada cosa con la respuesta que la corrige, en el orden de las páginas. Lo usan igual <c>mudanza-revisar</c>,
    /// antes de cambiar nada en la PC, y <c>escribir --mudanza</c>: se revisa exactamente lo que se escribe.
    /// </summary>
    public static IReadOnlyList<RespuestaNoValida> ProblemasDeLaMudanza(JsonObject objetivo, Respuestas respuestas, ResumenDeMudanza mudanza)
    {
        var problemas = new List<RespuestaNoValida>();
        // Allá el puente estaba prendido: no llega apagado. Con «No» la sección no se escribe, y lo que cobre la caja que se mudó no llegaría
        // a eleventa (ni lo que viaja pendiente en la base).
        if (respuestas.Eleventa != true)
        {
            if (mudanza.HabiaPuente)
                problemas.Add(new RespuestaNoValida("eleventa",
                    $"En {mudanza.Equipo} el puente con eleventa estaba prendido{(mudanza.EleventaEscribir ? " y escribiendo" : "")}, y llega con la base como "
                    + "estaba: aquí no se puede contestar «No» a eleventa. Contesta «Sí» y revisa sus datos (el servidor, el gbak y la clave son los de esta PC)."));
            return problemas;
        }
        var eleventa = Json.Valor(objetivo, "Eleventa") as JsonObject ?? new JsonObject();

        // La bandeja que viaja en la base está anotada con la ruta de la base de eleventa: con otra (aunque sea la misma escrita distinto),
        // el puente se detiene al arrancar («Cambió la base de eleventa»).
        if (mudanza.EleventaEscribir || (mudanza.Eleventa && (mudanza.EleventaPendientes > 0 || mudanza.EleventaPorRevisar > 0)))
        {
            var aqui = Json.Texto(Json.Valor(eleventa, "Base")) ?? "";
            var porQue = mudanza.EleventaEscribir ? "el puente escribía en" : "hay cosas pendientes para";
            if (mudanza.EleventaBase is { } deAlla && !MismaRuta(aqui, deAlla))
                problemas.Add(new RespuestaNoValida("eleventa-base",
                    $"En una mudanza la base de eleventa no se cambia: en {mudanza.Equipo} {porQue} «{deAlla}» y aquí dice «{aqui}». Deja la misma ruta "
                    + "(lo que cambia de una PC a otra es el servidor)."));
            else if (mudanza.EleventaBaseDeLaBandeja is { } deLaBandeja && !MismaRuta(aqui, deLaBandeja))
                // Si allá la configuración ya decía otra que la bandeja (su puente estaba detenido por eso), aquí no hay ruta que sirva a las
                // dos: se arregla en la PC de donde sale la base, antes de mudarla.
                problemas.Add(new RespuestaNoValida("eleventa-base",
                    $"En una mudanza la base de eleventa no se cambia: lo que viaja en la bandeja es de «{deLaBandeja}» y aquí dice «{aqui}». Con otra ruta el "
                    + "puente llegaría detenido."
                    + (mudanza.EleventaBase is { } configurada && !MismaRuta(configurada, deLaBandeja)
                        ? $" En {mudanza.Equipo} la configuración ya decía otra («{configurada}»): eso se arregla allá, antes de mudar la base (cancela la "
                            + "mudanza allá, corrígelo y sácala otra vez)."
                        : "")));
        }

        // Escribiendo, sin el respaldo del día no se manda nada: el gbak tiene que estar en ESTA PC (el de allá no viaja).
        if (mudanza.EleventaEscribir)
        {
            var gbak = Json.Texto(Json.Valor(eleventa, "Gbak"))?.Trim() ?? "";
            if (gbak.Length == 0 || !File.Exists(gbak))
                problemas.Add(new RespuestaNoValida("eleventa-gbak",
                    "Con el puente escribiendo hace falta el gbak de Firebird 2.0 de esta PC: sin el respaldo del día no se manda nada. "
                    + (gbak.Length == 0 ? "Falta su ruta." : $"No está «{gbak}».")));
        }

        // Cada caja que se muda queda con su número de antes, y la caja de omisión de esta PC no puede ser uno de ellos: toda caja que
        // no esté en Cajas (la de esta PC, cuando se dé de alta) escribiría como la caja que se mudó, en su turno.
        var cajas = Json.Valor(eleventa, "Cajas") as JsonObject;
        var deOmision = Numero(Json.Valor(eleventa, "Caja"));
        foreach (var caja in mudanza.EleventaCajas)
        {
            var suya = cajas is null ? null : Json.Valor(cajas, caja.Serie) as JsonObject;
            if (suya is null || Numero(Json.Valor(suya, "Caja")) != caja.Caja)
                problemas.Add(new RespuestaNoValida("eleventa-caja",
                    $"En Eleventa:Cajas, {mudanza.NombreDeLaSerie(caja.Serie)} no quedó con su caja de eleventa de antes (la {caja.Caja})."));
            else if (deOmision == caja.Caja)
                problemas.Add(new RespuestaNoValida("eleventa-caja",
                    $"La caja {caja.Caja} de eleventa es la de {mudanza.NombreDeLaSerie(caja.Serie)}, que llega con la base: el número de ESTA PC en eleventa es "
                    + "otro. Pon el suyo, o déjalo vacío si no lo sabes (se pone después, corriendo el instalador otra vez)."));
        }

        // Escribiendo, cada caja que se muda necesita a nombre de qué cajero va lo suyo: el de omisión o el de su entrada.
        if (mudanza.EleventaEscribir)
        {
            var cajeroDeOmision = Numero(Json.Valor(eleventa, "Cajero"));
            foreach (var caja in mudanza.EleventaCajas)
                if (cajeroDeOmision is null && (cajas is null || Json.Valor(cajas, caja.Serie) is not JsonObject suya || Numero(Json.Valor(suya, "Cajero")) is null))
                    problemas.Add(new RespuestaNoValida("eleventa-cajero",
                        $"Con el puente escribiendo, a {mudanza.NombreDeLaSerie(caja.Serie)} le falta el cajero de eleventa: pon el número del cajero."));
        }
        return problemas;
    }

    /// <summary>
    /// La caja de eleventa de omisión (<c>Eleventa:Caja</c>: la de toda caja del POS que no esté en <c>Eleventa:Cajas</c>) no puede ser la de
    /// una caja de <c>Cajas</c>: esa otra caja escribiría en eleventa como ella, en su turno. Es la regla de la mudanza
    /// (<see cref="ProblemasDeLaMudanza"/>) para la corrida de DESPUÉS, sin --mudanza, en la que se pone el número de la PC a donde se trajo
    /// la base (ese día se deja vacío). Solo si la respuesta la cambia: lo que ya estaba así se queda como estaba, sin detener al
    /// instalador.
    /// </summary>
    private static RespuestaNoValida? CajaDeOmisionRepetida(JsonObject actual, JsonObject objetivo)
    {
        if (Json.Valor(objetivo, "Eleventa") is not JsonObject eleventa || Numero(Json.Valor(eleventa, "Caja")) is not { } caja
            || Json.Valor(eleventa, "Cajas") is not JsonObject cajas)
            return null;
        var antes = Json.Valor(actual, "Eleventa") is JsonObject deAntes ? Numero(Json.Valor(deAntes, "Caja")) : null;
        if (antes == caja)
            return null;
        foreach (var (serie, entrada) in cajas)
            if (entrada is JsonObject suya && Numero(Json.Valor(suya, "Caja")) == caja)
                return new RespuestaNoValida("eleventa-caja",
                    $"La caja {caja} de eleventa ya es la de la caja del POS con la serie {serie} (en Eleventa:Cajas): el número de ESTA PC en eleventa es "
                    + $"otro. {(antes is { } suyo ? $"Pon el suyo (hasta hoy era la {suyo})." : "Pon el suyo, o déjalo vacío si no lo sabes.")}");
        return null;
    }

    /// <summary>
    /// Si dos rutas de la base de eleventa son la misma para el puente, que las compara como <c>ServidorEleventa.RutaDe</c> (sin los
    /// espacios ni los puntos del final, que Windows ignora) y sin distinguir mayúsculas. Una prueba revisa que sea lo mismo.
    /// </summary>
    public static bool MismaRuta(string una, string otra) => string.Equals(RutaDe(una), RutaDe(otra), StringComparison.OrdinalIgnoreCase);

    internal static string RutaDe(string ruta) => ruta.Trim().TrimEnd('.', ' ');

    /// <summary>Un número de eleventa de servicio.json (la caja, el cajero): vacío si falta, y un 0 es que falta.</summary>
    private static int? Numero(JsonNode? nodo) =>
        int.TryParse(Json.Texto(nodo)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero > 0 ? numero : null;

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
    public static Escritura Escribir(string ruta, Respuestas respuestas, ResumenDeMudanza? mudanza = null) => Preparar(ruta, respuestas, mudanza).Guardar();

    /// <summary>
    /// El servicio.json que va a quedar, armado en memoria y sin escribirlo todavía. Con <paramref name="mudanza"/> es la única función que
    /// lo arma, para <c>mudanza-revisar</c> y para <c>escribir --mudanza</c>: si lo que quedaría no se puede (<see cref="ProblemasDeLaMudanza"/>),
    /// lo dice con su respuesta (<see cref="RespuestaNoValida"/>) y no hay nada que escribir.
    /// </summary>
    public static Pendiente Preparar(string ruta, Respuestas respuestas, ResumenDeMudanza? mudanza = null)
    {
        byte[]? antes = File.Exists(ruta) ? File.ReadAllBytes(ruta) : null;
        var texto = antes is null || Encoding.UTF8.GetString(Json.SinBom(antes)).Trim().Length == 0
            ? new TextoJsonc(Encoding.UTF8.GetBytes("{" + Environment.NewLine + "}" + Environment.NewLine))
            : new TextoJsonc(antes);
        var actual = texto.Objeto();
        var objetivo = Mezclar(actual, respuestas, mudanza);
        if (mudanza is not null && ProblemasDeLaMudanza(objetivo, respuestas, mudanza) is [var primero, ..])
            throw primero;
        if (CajaDeOmisionRepetida(actual, objetivo) is { } repetida)
            throw repetida;
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
