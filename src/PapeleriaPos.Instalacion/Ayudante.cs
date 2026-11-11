using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// PapeleriaPos.Instalacion.exe, el ayudante del instalador (docs/instalacion.md). Lo corre el asistente mientras pregunta, y
/// instalar-pc.ps1 al instalar; cada orden deja lo que encontró en <c>--salida</c> («clave=valor», <see cref="ArchivoDeClaves"/>):
/// <code>
/// leer     --salida archivo [--datos carpeta] [--programa carpeta]   lo que hay en esta PC (LecturaDeLaPc)
/// buscar   --salida archivo [--segundos 2]                           las cajas principales que contestan en la red
/// probar   --direccion IP --salida archivo                           si esa caja principal contesta (api/estado)
/// revisar  --respuestas archivo --salida archivo [--datos carpeta]   lo mismo que escribir, sin escribir nada: si se puede, y qué cambiaría
/// escribir --respuestas archivo --salida archivo [--datos carpeta]   servicio.json y caja.json con las respuestas (solo lo que cambió)
/// </code>
/// Y para mudar la base de la tienda a otra PC (el «paquete de mudanza», una carpeta; PaqueteDeMudanza):
/// <code>
/// mudanza-ver     --paquete carpeta [--version la-del-instalador] --salida archivo       cómo está ese paquete y qué trae, sin leer su base
/// mudanza-revisar --paquete carpeta --respuestas archivo --version la-del-instalador     si se puede traer a esta PC, antes de cambiar nada
///                 --salida archivo [--datos carpeta]
/// revisar | escribir … --mudanza carpeta                                                 además, el puente con eleventa como estaba allá
/// probar --direccion IP --esta-caja si --salida archivo [--datos carpeta]                además, si esa PC ya tiene tienda y si reconoce la
///                                                                                        llave de la caja de esta PC (IdentidadDeLaPrincipal)
/// escribir … --mudada-a IP [--llave-del-servicio llave] [--alta otra-vez]                además, la caja de esta PC queda hablándole a esa PC
/// mudanza-viva --tienda nombre [--direcciones ip;ip] [--puerto 5080] --salida archivo    si otra PC ya trabaja con esa tienda (BaseVivaEnOtraPc)
/// </code>
/// Termina con 0 si hizo lo suyo (lo que encontró va en la salida), con 2 si algo no se entiende o no se puede (un archivo de la PC mal
/// escrito, una respuesta, algo de la red o del disco: «error=» dice qué, «clave=» de qué respuesta, y no se cambió nada) y con 1 si la
/// orden está mal dada. Nunca truena sin dejar su salida, y nunca recibe ni escribe una clave. El asistente corre «revisar» antes de
/// cambiar nada en la PC: así una respuesta que no se puede usar se corrige en su página, en lugar de descubrirse con el programa ya
/// actualizado. La llave de la caja de esta PC tampoco se recibe ni se escribe: solo <c>probar --esta-caja si</c> la abre (de caja.json,
/// con Windows) para la prueba de identidad, y no sale de ahí. La llave que sí va por la línea de órdenes
/// (<c>--llave-del-servicio</c>) es la pública del servicio, que no es un secreto.
/// </summary>
public static class Ayudante
{
    public static Task<int> Main(string[] argumentos) => CorrerAsync(argumentos, null);

    /// <param name="equipo">El de las pruebas; sin él, esta PC (en Windows) o una sin nada (para probarlo en otro sistema).</param>
    public static Task<int> CorrerAsync(string[] argumentos, IEquipo? equipo, HttpMessageHandler? red = null) =>
        CorrerAsync(argumentos, equipo, red, PlazosDeLaMudanza.DeLaTienda);

    /// <param name="plazos">Cuánto se espera a la red en la mudanza: las pruebas ponen los suyos.</param>
    public static Task<int> CorrerAsync(string[] argumentos, IEquipo? equipo, HttpMessageHandler? red, PlazosDeLaMudanza plazos) =>
        CorrerAsync(argumentos, equipo, red, plazos, null);

    /// <param name="enLaRed">
    /// Para <c>mudanza-viva</c>, lo que contestó a la pregunta por UDP: las pruebas dicen qué (no preguntan en la red de verdad); sin
    /// él, la pregunta de verdad.
    /// </param>
    internal static async Task<int> CorrerAsync(string[] argumentos, IEquipo? equipo, HttpMessageHandler? red, PlazosDeLaMudanza plazos,
        Func<TimeSpan, Task<IReadOnlyList<Uri>>>? enLaRed)
    {
        if (argumentos.Length == 0 || Opciones(argumentos[1..]) is not { } opciones || !opciones.TryGetValue("salida", out var salida))
        {
            await Console.Error.WriteLineAsync(
                "Uso: PapeleriaPos.Instalacion.exe leer|buscar|probar|revisar|escribir|mudanza-ver|mudanza-revisar|mudanza-viva --salida <archivo> [...] "
                + "(docs/instalacion.md).");
            return 1;
        }
        equipo ??= Equipo(opciones);
        try
        {
            switch (argumentos[0])
            {
                case "leer":
                    var leido = LecturaDeLaPc.Leer(equipo);
                    leido.Guardar(salida);
                    return leido["error"] is { Length: > 0 } ? 2 : 0;
                case "buscar":
                    var segundos = opciones.TryGetValue("segundos", out var texto) && double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        ? s
                        : 2;
                    (await BuscarAsync(TimeSpan.FromSeconds(Math.Clamp(segundos, 0.5, 10)), red)).Guardar(salida);
                    return 0;
                case "probar" when EsSi(opciones.GetValueOrDefault("esta-caja")):
                    // Para terminar una mudanza: además, si esa PC reconoce la llave de la caja de esta. Sale con 0: decide quien lo lee.
                    (await ProbarConEstaCajaAsync(opciones.GetValueOrDefault("direccion"), equipo, red, plazos.ALaPrincipal)).Guardar(salida);
                    return 0;
                case "probar":
                    (await ProbarAsync(opciones.GetValueOrDefault("direccion"), red)).Guardar(salida);
                    return 0;
                case "revisar" when opciones.TryGetValue("respuestas", out var aRevisar):
                    var revisado = Revisar(ArchivoDeClaves.Leer(aRevisar), equipo.Datos, opciones.GetValueOrDefault("mudanza"), ConversionPedida.De(opciones));
                    revisado.Guardar(salida);
                    return revisado["error"] is { Length: > 0 } ? 2 : 0;
                case "escribir" when opciones.TryGetValue("respuestas", out var respuestas):
                    var escrito = Escribir(ArchivoDeClaves.Leer(respuestas), equipo.Datos, opciones.GetValueOrDefault("mudanza"), ConversionPedida.De(opciones));
                    escrito.Guardar(salida);
                    return escrito["error"] is { Length: > 0 } ? 2 : 0;
                case "mudanza-viva" when opciones.TryGetValue("tienda", out var tienda):
                    // Solo con «viva=no» y 0 se puede cancelar: lo que no se pudo saber es un error.
                    var viva = await BaseVivaEnOtraPc.BuscarAsync(tienda, opciones.GetValueOrDefault("direcciones"), opciones.GetValueOrDefault("puerto"), equipo,
                        red, plazos, enLaRed);
                    viva.Guardar(salida);
                    return viva["error"] is { Length: > 0 } ? 2 : 0;
                case "mudanza-ver" when opciones.TryGetValue("paquete", out var aVer):
                    // Sale con 0 también si el paquete no está bien: el asistente decide por «paquete».
                    MudanzaVer(aVer, opciones.GetValueOrDefault("version"), equipo).Guardar(salida);
                    return 0;
                case "mudanza-revisar" when opciones.TryGetValue("paquete", out var aTraer) && opciones.TryGetValue("respuestas", out var conQue)
                    && opciones.TryGetValue("version", out var version):
                    var traible = await RevisionDeLaMudanza.RevisarAsync(aTraer, ArchivoDeClaves.Leer(conQue), version, equipo, red, plazos);
                    traible.Guardar(salida);
                    return traible["error"] is { Length: > 0 } ? 2 : 0;
                default:
                    await Console.Error.WriteLineAsync($"No conozco la orden «{argumentos[0]}» (o le falta algo).");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NoSePudo(salida, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException)
        {
            return NoSePudo(salida, $"No se pudo preguntar por la red: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Lo que nadie esperaba: también se dice en la salida. Sin ella, quien lo corrió (el asistente, instalar-pc.ps1) no sabría qué
            // pasó, y de «leer» depende que el instalador siga en una PC que ya tiene el POS.
            return NoSePudo(salida, $"El ayudante del instalador no pudo con «{argumentos[0]}» ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>El «error=» de lo que falló y el código 2. Si ni la salida se deja escribir, a la pantalla: nunca una traza.</summary>
    private static int NoSePudo(string salida, string mensaje)
    {
        try
        {
            new ArchivoDeClaves().Poner("error", mensaje).Guardar(salida);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"{mensaje} (Y no se pudo escribir {salida}: {ex.Message})");
        }
        return 2;
    }

    /// <summary>«--clave valor» de la línea de órdenes; vacío si alguna no trae su valor.</summary>
    private static Dictionary<string, string>? Opciones(string[] argumentos)
    {
        var opciones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < argumentos.Length; i += 2)
        {
            if (!argumentos[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= argumentos.Length)
                return null;
            opciones[argumentos[i][2..]] = argumentos[i + 1];
        }
        return opciones;
    }

    private static IEquipo Equipo(Dictionary<string, string> opciones)
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = new EquipoWindows();
            return new EquipoWindows
            {
                Datos = opciones.GetValueOrDefault("datos") ?? windows.Datos,
                Programa = opciones.GetValueOrDefault("programa") ?? windows.Programa,
            };
        }
        return new EquipoSinWindows(
            opciones.GetValueOrDefault("datos") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos"),
            opciones.GetValueOrDefault("programa") ?? Path.Combine(Path.GetTempPath(), "PapeleriaPos-sin-programa"));
    }

    /// <summary>Las cajas principales que contestan: por UDP y después su api/estado (solo cuentan las que dicen ser Papelería POS).</summary>
    public static async Task<ArchivoDeClaves> BuscarAsync(TimeSpan espera, HttpMessageHandler? red = null, IReadOnlyList<IPEndPoint>? destinos = null)
    {
        var encontradas = await BusquedaDeLaPrincipal.BuscarAsync(espera, destinos);
        var estados = await Task.WhenAll(encontradas.Select(uri => BusquedaDeLaPrincipal.ProbarAsync(uri, red)));
        var contestan = estados.Where(e => e.Contesta).ToList();
        var salida = new ArchivoDeClaves().Poner("encontradas", contestan.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < contestan.Count; i++)
            salida.Poner($"encontrada.{i + 1}", BusquedaDeLaPrincipal.Corta(contestan[i].Direccion))
                .Poner($"encontrada.{i + 1}.texto", contestan[i].Texto)
                .Poner($"encontrada.{i + 1}.aviso", contestan[i].Aviso);
        return salida.Poner("sin-contestar", (estados.Length - contestan.Count).ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<ArchivoDeClaves> ProbarAsync(string? direccion, HttpMessageHandler? red = null) =>
        (await PreguntarAsync(direccion, red, null, ", ")).Salida;

    /// <summary>
    /// <c>probar --esta-caja si</c>, para terminar una mudanza en la PC de donde salió la base: lo mismo que <c>probar</c> y, además,
    /// «tienda» (si esa PC ya tiene una: no es una base nueva), sus «sucursales» unidas con «; » (como dice «tienda» la marca de la
    /// mudanza, para compararlas) y «conoce»: si reconoce la llave de la caja de esta PC (<see cref="IdentidadDeLaPrincipal"/>: si, no,
    /// no-se o sin-alta), con su porqué en «conoce-motivo». Con «si», «llave-del-servicio» es la llave pública que presentó, como la
    /// guarda la caja. La llave de la caja nunca sale de aquí.
    /// </summary>
    public static async Task<ArchivoDeClaves> ProbarConEstaCajaAsync(string? direccion, IEquipo equipo, HttpMessageHandler? red = null, TimeSpan? espera = null)
    {
        var (salida, estado) = await PreguntarAsync(direccion, red, espera, "; ");
        var prueba = await IdentidadDeLaPrincipal.ProbarAsync(estado, equipo, red, espera ?? PlazosDeLaMudanza.DeLaTienda.ALaPrincipal);
        return salida.Poner("tienda", estado is { Contesta: true, TiendaNueva: false })
            .Poner("conoce", prueba.Nombre)
            .Poner("conoce-motivo", prueba.Motivo)
            .Poner("llave-del-servicio", prueba.LlaveDelServicio);
    }

    /// <summary>Lo que contesta esa dirección en su api/estado, ya como va en la salida; sin estado si no es una dirección.</summary>
    private static async Task<(ArchivoDeClaves Salida, EstadoDeLaPrincipal? Estado)> PreguntarAsync(string? direccion, HttpMessageHandler? red, TimeSpan? espera,
        string entreSucursales)
    {
        var salida = new ArchivoDeClaves();
        if (BusquedaDeLaPrincipal.Direccion(direccion) is not { } uri)
            return (salida.Poner("contesta", false).Poner("error", $"«{direccion}» no es una dirección: va la IP de la caja principal, como 192.168.1.10."), null);
        var estado = await BusquedaDeLaPrincipal.ProbarAsync(uri, red, espera);
        return (salida.Poner("contesta", estado.Contesta)
            .Poner("direccion", BusquedaDeLaPrincipal.Corta(uri))
            .Poner("version", estado.Version)
            .Poner("sucursales", string.Join(entreSucursales, estado.Sucursales))
            .Poner("una-sola-caja", estado.UnaSolaCaja)
            .Poner("tienda-nueva", estado.TiendaNueva)
            .Poner("aviso", estado.Aviso)
            .Poner("texto", estado.Texto)
            .Poner("error", estado.Error), estado);
    }

    /// <summary>«si» en una opción de la línea de órdenes (también «sí», «true» o «1»).</summary>
    private static bool EsSi(string? valor) => (valor ?? "").Trim().ToLowerInvariant() is "si" or "sí" or "true" or "1";

    /// <summary>
    /// Lo que el asistente enseña de un paquete de mudanza antes de traerlo (<c>mudanza-ver</c>), sin leer su base ni tocar la red:
    /// «paquete» dice cómo está (bien, no-hay, incompleto, ya-traido, otra-version) y, si no está bien, «error» por qué. Con «bien» repite
    /// lo que dice su mudanza.txt (su identificador va como «paquete-id»: «paquete» es cómo está) y agrega «texto», el párrafo para
    /// enseñar, y lo que propone para ESTA PC: si el eleventa o el Control Print de allá estaban en esta misma PC, aquí son 127.0.0.1.
    /// </summary>
    public static ArchivoDeClaves MudanzaVer(string? carpeta, string? versionDelInstalador, IEquipo equipo)
    {
        var paquete = PaqueteDeMudanza.Ver(carpeta, versionDelInstalador);
        var salida = new ArchivoDeClaves().Poner("paquete", paquete.NombreDelEstado).Poner("error", paquete.Error);
        if (paquete is not { Estado: EstadoDelPaquete.Bien, Resumen: { } resumen })
            return salida;
        // Solo lo que se sabe que trae un mudanza.txt, una vez cada cosa: lo demás no se repite (ni puede pisar lo que se dice aquí).
        foreach (var clave in ClavesDelResumen(resumen))
            salida.Poner(clave == "paquete" ? "paquete-id" : clave, resumen.Claves[clave]);
        return salida.Poner("texto", resumen.Texto())
            .Poner("propone-eleventa-servidor", ServidorAqui(resumen.EleventaServidor, resumen, equipo))
            .Poner("propone-controlprint-servidor", ServidorAqui(resumen.ControlPrintServidor, resumen, equipo));
    }

    /// <summary>Las claves de mudanza.txt, en su orden: las fijas y las de cada caja y cada aviso que traiga.</summary>
    private static IEnumerable<string> ClavesDelResumen(ResumenDeMudanza resumen)
    {
        int Cuantos(string clave) => int.TryParse(resumen.Claves[clave]?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, 500) : 0;
        string[] antes = ["formato", "paquete", "hecho-utc", "hecho-local", "equipo", "ips", "puerto", "version", "archivo", "bytes", "sha256", "tienda",
            "productos", "ventas", "ultima-venta", "cajas"];
        string[] deCadaCaja = ["id", "nombre", "serie", "equipo", "con-llave", "con-turnos", "turno-abierto", "eleventa-caja"];
        string[] despues = ["eleventa", "eleventa-escribir", "eleventa-servidor", "eleventa-puerto", "eleventa-base", "eleventa-base-de-la-bandeja",
            "eleventa-usuario", "eleventa-caja", "eleventa-cajero", "eleventa-cajas", "eleventa-ajustes", "eleventa-pendientes", "eleventa-por-revisar",
            "eleventa-detenido", "controlprint", "controlprint-servidor", "avisos"];
        return antes
            .Concat(Enumerable.Range(1, Cuantos("cajas")).SelectMany(i => deCadaCaja.Select(dato => $"caja.{i}.{dato}")))
            .Concat(despues)
            .Concat(Enumerable.Range(1, Cuantos("avisos")).Select(i => $"aviso.{i}"));
    }

    /// <summary>
    /// El servidor que se propone en esta PC para uno de allá, con su puerto si lo traía: si era esta misma PC (una de sus IP o su
    /// nombre), 127.0.0.1; si no, el de allá. Y «el de allá», si allá estaba dicho como la propia PC (127.0.0.1, localhost: el eleventa
    /// vivía en la PC de donde sale la base, como al regresar una mudanza), desde aquí es la IP de aquella PC.
    /// </summary>
    private static string? ServidorAqui(string? deAlla, ResumenDeMudanza resumen, IEquipo equipo)
    {
        if (deAlla is null)
            return null;
        var dosPuntos = deAlla.IndexOf(':');
        var (pc, puerto) = dosPuntos > 0 ? (deAlla[..dosPuntos], deAlla[dosPuntos..]) : (deAlla, "");
        var ip = IPAddress.TryParse(pc, out var leida) ? leida : null;
        var eraLaDeAlla = string.Equals(pc, "localhost", StringComparison.OrdinalIgnoreCase) || (ip is not null && IPAddress.IsLoopback(ip));
        if (eraLaDeAlla)
            return IpDeAlla(resumen, equipo) is { } suya ? suya + puerto : deAlla;
        var esEsta = string.Equals(pc, equipo.Nombre(), StringComparison.OrdinalIgnoreCase) || (ip is not null && PreguntasDeLaMudanza.EsDeEstaPc(ip, equipo));
        return esEsta ? "127.0.0.1" + puerto : deAlla;
    }

    /// <summary>
    /// La IP de la PC de donde salió la base, vista desde esta: de las que dice su paquete, la primera de la misma red que esta PC (sus
    /// tres primeros números) o, si ninguna, la primera. Vacía si no dice ninguna o si todas son de esta PC (el paquete se sacó aquí mismo).
    /// </summary>
    private static string? IpDeAlla(ResumenDeMudanza resumen, IEquipo equipo)
    {
        static string Red(string ip) => ip[..(ip.LastIndexOf('.') + 1)];
        var deAlla = resumen.Ips.Where(ip => !PreguntasDeLaMudanza.EsDeEstaPc(ip, equipo)).Select(ip => ip.ToString()).ToList();
        var redesDeAqui = equipo.Ips().Where(ip => ip.Contains('.')).Select(Red).ToHashSet();
        return deAlla.FirstOrDefault(ip => redesDeAqui.Contains(Red(ip))) ?? deAlla.FirstOrDefault();
    }

    /// <summary>
    /// servicio.json (si esta PC tiene el servicio) y caja.json con las respuestas: solo lo que cambió, y dice qué. Con
    /// <paramref name="mudanza"/> (la carpeta del paquete de mudanza que se trajo a esta PC), además, el puente con eleventa como estaba
    /// en la PC de donde viene la base (<see cref="ServicioJson"/>). Con <paramref name="conversion"/> (<c>--mudada-a</c>, en la PC de donde
    /// salió la base, al terminar la mudanza), además, su caja queda hablándole a la PC nueva (<see cref="ConversionDeLaCaja"/>).
    /// </summary>
    public static ArchivoDeClaves Escribir(ArchivoDeClaves archivo, string datos, string? mudanza = null, ConversionPedida? conversion = null) =>
        ConLaMudanza(archivo, datos, mudanza, escribir: true, conversion);

    /// <summary>
    /// Lo mismo que <see cref="Escribir"/>, sin escribir nada: si las respuestas se pueden usar (todas las que no, cada una con su clave) y
    /// qué cambiaría. Lo corre el asistente antes de cambiar nada en la PC.
    /// </summary>
    public static ArchivoDeClaves Revisar(ArchivoDeClaves archivo, string datos, string? mudanza = null, ConversionPedida? conversion = null) =>
        ConLaMudanza(archivo, datos, mudanza, escribir: false, conversion);

    /// <summary>Lo mismo, con el resumen del paquete ya leído (<c>mudanza-revisar</c>, que antes revisó el paquete entero).</summary>
    public static ArchivoDeClaves Revisar(ArchivoDeClaves archivo, string datos, ResumenDeMudanza mudanza) => Preparar(archivo, datos, escribir: false, mudanza, null);

    /// <summary>Con <c>--mudanza</c>, primero el resumen de ese paquete: si no se puede leer o no se entiende, no se prepara nada.</summary>
    private static ArchivoDeClaves ConLaMudanza(ArchivoDeClaves archivo, string datos, string? mudanza, bool escribir, ConversionPedida? conversion)
    {
        if (mudanza is null)
            return Preparar(archivo, datos, escribir, null, conversion);
        var carpeta = PaqueteDeMudanza.Limpia(mudanza);
        // Sin la carpeta no se busca un mudanza.txt «por ahí» (el de la carpeta desde donde se corrió): se dice que falta.
        if (carpeta.Length == 0)
            return new ArchivoDeClaves().Poner("error", "Falta la carpeta del paquete de mudanza. No se cambió nada.").Poner("clave", "mudanza-paquete");
        ResumenDeMudanza resumen;
        try
        {
            resumen = ResumenDeMudanza.DeLaCarpeta(carpeta);
        }
        catch (FormatException ex)
        {
            return new ArchivoDeClaves().Poner("error", $"{ResumenDeMudanza.NoSeEntiende(carpeta, ex)} No se cambió nada.").Poner("clave", "mudanza-paquete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArchivoDeClaves().Poner("error", $"No se pudo leer el paquete de mudanza de {carpeta}: {ex.Message} No se cambió nada.")
                .Poner("clave", "mudanza-paquete");
        }
        return Preparar(archivo, datos, escribir, resumen, conversion);
    }

    private static ArchivoDeClaves Preparar(ArchivoDeClaves archivo, string datos, bool escribir, ResumenDeMudanza? mudanza, ConversionPedida? pedida)
    {
        var salida = new ArchivoDeClaves();
        var (respuestas, errores) = Respuestas.Revisar(archivo);
        if (respuestas is null)
        {
            salida.Poner("error", $"Una respuesta no se puede usar: {errores[0].Message}").Poner("clave", errores[0].Clave)
                .Poner("errores", errores.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < errores.Count; i++)
                salida.Poner($"error.{i + 1}", errores[i].Message).Poner($"error.{i + 1}.clave", errores[i].Clave);
            return salida;
        }
        // Lo de la mudanza es el puente con eleventa, que vive en servicio.json: en «Otra caja» no hay dónde ponerlo.
        if (mudanza is not null && !respuestas.ConServicio)
            return salida.Poner("error", "La configuración de una mudanza es la del servicio: va con «Caja principal» o «La única caja», no con «Otra caja».")
                .Poner("clave", "rol");
        // Al terminar una mudanza (--mudada-a): a dónde le habla desde ahora la caja de esta PC. Lo que no se puede usar no deja nada a medias.
        ConversionDeLaCaja? conversion;
        try
        {
            conversion = pedida is null ? null : ConversionDeLaCaja.De(pedida, respuestas);
        }
        catch (RespuestaNoValida ex)
        {
            return salida.Poner("error", $"{ex.Message} No se cambió nada.").Poner("clave", ex.Clave);
        }
        // Primero los dos, y solo si los dos se entienden se escriben: no queda uno cambiado y el otro no.
        Pendiente? servicio, caja;
        try
        {
            servicio = respuestas.ConServicio ? ServicioJson.Preparar(Path.Combine(datos, ServicioJson.Archivo), respuestas, mudanza) : null;
            caja = CajaJson.Preparar(datos, respuestas, conversion);
            // Por seguridad, nunca a través de un enlace (lo revisa también Archivos.Guardar, justo antes de escribir).
            servicio?.RevisarQueNoHayEnlaces();
            caja.RevisarQueNoHayEnlaces();
        }
        catch (RespuestaNoValida ex)
        {
            // Lo que el servicio.json de una mudanza no puede llevar: con la respuesta que lo corrige.
            return salida.Poner("error", ex.Message).Poner("clave", ex.Clave);
        }
        catch (FormatException ex)
        {
            return salida.Poner("error", $"{ex.Message} Corrígelo o quítalo y vuelve a correr el instalador; no se cambió nada.");
        }
        catch (EnlaceNoPermitido ex)
        {
            return salida.Poner("error", ex.Message);
        }
        var cambios = new List<string>();
        if (servicio is not null)
        {
            var enServicio = escribir ? servicio.Guardar() : servicio.ComoQuedaria();
            salida.Poner("servicio-json", enServicio.Cambio ? "cambio" : "igual").Poner("servicio-json-completo", enServicio.Completo);
            cambios.AddRange(enServicio.Cambios);
        }
        else
            salida.Poner("servicio-json", "no-aplica");
        var enCaja = escribir ? caja.Guardar() : caja.ComoQuedaria();
        salida.Poner("caja-json", enCaja.Cambio ? "cambio" : "igual");
        cambios.AddRange(enCaja.Cambios);
        return salida.Poner("error", "").Poner("cambios", string.Join("; ", cambios));
    }
}

/// <summary>Una PC sin Windows (las pruebas, o correrlo en Linux): sin servicio registrado, impresoras ni puertos.</summary>
public sealed class EquipoSinWindows(string datos, string programa) : IEquipo
{
    public string Datos => datos;

    public string Programa => programa;

    public string? ExeDelServicio() => null;

    public string? Version(string exe) => null;

    public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [];

    public IReadOnlyList<string> PuertosCom() => [];

    public IReadOnlyList<string> Ips() => [];

    public IReadOnlyList<string> Gbak() => [];

    public string? IniDeEleventa() => null;
}
