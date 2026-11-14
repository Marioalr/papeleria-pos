using System.Diagnostics;
using System.Net;

namespace PapeleriaPos.Instalacion;

/// <summary>Cuánto se espera a la red en la mudanza. Los de la tienda son fijos; las pruebas ponen los suyos.</summary>
/// <param name="AOtraPc">A cada PC de la red (la de donde salió la base): lo normal es que no conteste.</param>
/// <param name="AEstaPc">Al servicio de esta PC, en total: recién arrancado tarda en contestar.</param>
/// <param name="CadaIntento">A cada pregunta al servicio de esta PC.</param>
/// <param name="EntreIntentos">Entre una pregunta y la siguiente, mientras no conteste.</param>
public sealed record PlazosDeLaMudanza(TimeSpan AOtraPc, TimeSpan AEstaPc, TimeSpan CadaIntento, TimeSpan EntreIntentos)
{
    public static readonly PlazosDeLaMudanza DeLaTienda = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1));

    /// <summary>
    /// A la PC que ya tiene (o puede tener) la base, cuando de lo que conteste depende terminar o cancelar la mudanza
    /// (<c>probar --esta-caja</c>, <c>mudanza-viva</c>): lo mismo que espera <c>probar</c>.
    /// </summary>
    public TimeSpan ALaPrincipal { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Lo que se esperan las respuestas a la pregunta por UDP (<c>mudanza-viva</c>): lo mismo que <c>buscar</c>.</summary>
    public TimeSpan AlBuscar { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Las preguntas que la mudanza le hace a la red: siempre el <c>api/estado</c> que el servicio contesta a cualquiera, nunca nada con
/// llave. Una PC que no contesta, o que contesta algo que no se entiende, es una que no contestó: nada de aquí truena.
/// </summary>
public static class PreguntasDeLaMudanza
{
    /// <summary>
    /// Si esa IP es de esta PC (o es ella misma por 127.x): su propio servicio, ya con la base traída, contestaría con la misma tienda y
    /// parecería que la otra PC sigue viva.
    /// </summary>
    public static bool EsDeEstaPc(IPAddress ip, IEquipo equipo) =>
        IPAddress.IsLoopback(ip) || equipo.IpsPropias().Any(propia => IPAddress.TryParse(propia, out var suya) && suya.Equals(ip));

    /// <summary>
    /// Lo que contesta cada una de esas IP que NO es de esta PC, preguntadas a la vez y con poca espera (lo normal es que la otra PC no
    /// conteste: su servicio está detenido). Solo las que contestaron como Papelería POS.
    /// </summary>
    public static async Task<IReadOnlyList<EstadoDeLaPrincipal>> AOtrasPcAsync(IEnumerable<IPAddress> ips, int puerto, IEquipo equipo, HttpMessageHandler? red,
        TimeSpan espera)
    {
        var estados = await Task.WhenAll(ips.Where(ip => !EsDeEstaPc(ip, equipo)).Distinct()
            .Select(ip => PreguntarAsync(new UriBuilder("http", ip.ToString(), puerto, "/").Uri, red, espera)));
        return estados.Where(e => e.Contesta).ToList();
    }

    /// <summary>
    /// Lo que contesta el servicio de esta PC (http://localhost:5080), esperándolo hasta <see cref="PlazosDeLaMudanza.AEstaPc"/>: el que
    /// se acaba de arrancar tarda. Si al final no contestó, lo dice su <see cref="EstadoDeLaPrincipal.Contesta"/>.
    /// </summary>
    public static async Task<EstadoDeLaPrincipal> AlServicioDeEstaPcAsync(HttpMessageHandler? red, PlazosDeLaMudanza plazos)
    {
        var direccion = new Uri($"http://localhost:{BusquedaDeLaPrincipal.PuertoDelServicio}/");
        var reloj = Stopwatch.StartNew();
        while (true)
        {
            var estado = await PreguntarAsync(direccion, red, plazos.CadaIntento);
            var falta = plazos.AEstaPc - reloj.Elapsed;
            if (estado.Contesta || falta <= TimeSpan.Zero)
                return estado;
            await Task.Delay(falta < plazos.EntreIntentos ? falta : plazos.EntreIntentos);
        }
    }

    /// <summary><see cref="BusquedaDeLaPrincipal.ProbarAsync"/>, sin que nada de lo que conteste (o no) esa PC llegue a tronar.</summary>
    public static async Task<EstadoDeLaPrincipal> PreguntarAsync(Uri direccion, HttpMessageHandler? red, TimeSpan espera)
    {
        try
        {
            return await BusquedaDeLaPrincipal.ProbarAsync(direccion, red, espera);
        }
        catch (Exception)
        {
            // Lo que ProbarAsync no espera de otro programa en ese puerto (un JSON con una clave repetida, por ejemplo): no es la caja principal.
            return new EstadoDeLaPrincipal(direccion, false, null, [], "no contestó (¿está prendida y en la misma red?)");
        }
    }
}

/// <summary>
/// <c>mudanza-revisar</c>: todo lo que se puede saber ANTES de traer a esta PC la base de un paquete de mudanza, sin cambiar nada
/// (docs/instalacion.md). En este orden: el paquete (completo, de una versión que esta conoce y con su base intacta), que la PC de donde
/// salió no haya vuelto a trabajar con esa base, qué base tiene esta PC, y que el servicio.json que va a quedar se pueda (lo arma
/// <see cref="Ayudante.Revisar(ArchivoDeClaves, string, ResumenDeMudanza)"/> con el mismo código que después lo escribe). Lo primero que
/// no se puede lo dice en «error», con la «clave» de la respuesta que lo corrige (la página a la que regresa el asistente).
/// <para>
/// La clave «mudanza» es solo la de «Esta PC ya tiene una tienda»: con ella el asistente pregunta si se aparta la de aquí y, con «Sí»,
/// revisa otra vez con <c>mudanza-encima=si</c>. Ningún otro error sale con esa clave.
/// </para>
/// </summary>
public static class RevisionDeLaMudanza
{
    public static Task<ArchivoDeClaves> RevisarAsync(string? carpeta, ArchivoDeClaves archivo, string? version, IEquipo equipo, HttpMessageHandler? red = null) =>
        RevisarAsync(carpeta, archivo, version, equipo, red, PlazosDeLaMudanza.DeLaTienda);

    public static async Task<ArchivoDeClaves> RevisarAsync(string? carpeta, ArchivoDeClaves archivo, string? version, IEquipo equipo, HttpMessageHandler? red,
        PlazosDeLaMudanza plazos)
    {
        ResumenDeMudanza? resumen = null;
        // Siempre las mismas claves, también si no se puede: de qué paquete se habla le sirve al asistente para decirlo. El identificador
        // del paquete va dos veces: «paquete» (aquí no hay un estado que decir, como en mudanza-ver) y «paquete-id», que es como se llama
        // en las dos salidas.
        ArchivoDeClaves Salida(bool bien, bool yaEstaba, bool apartaBase, string? error, string? clave) => new ArchivoDeClaves()
            .Poner("bien", bien)
            .Poner("ya-estaba", yaEstaba)
            .Poner("aparta-base", apartaBase)
            .Poner("archivo", resumen?.Archivo)
            .Poner("equipo", resumen?.Equipo)
            .Poner("paquete", resumen?.Paquete)
            .Poner("paquete-id", resumen?.Paquete)
            .Poner("tienda", resumen?.Tienda)
            .Poner("error", error)
            .Poner("clave", clave);
        // Si no se puede, no se va a traer ni a apartar nada: «ya-estaba» y «aparta-base» solo valen con «bien=si».
        ArchivoDeClaves NoSePuede(string? error, string? clave) =>
            Salida(false, false, false, error is { Length: > 0 } ? error : "El paquete de mudanza no se puede traer a esta PC.", clave);

        try
        {
            // Las respuestas, como en «revisar»: con una que no se puede usar no hay servicio.json que armar. La clave «mudanza» se guarda
            // para la pregunta de «Esta PC ya tiene una tienda»: lo que no cuadra entre la mudanza y lo que es esta PC se corrige en «rol».
            var (respuestas, errores) = Respuestas.Revisar(archivo);
            if (respuestas is null)
                return NoSePuede($"Una respuesta no se puede usar: {errores[0].Message}", errores[0].Clave == "mudanza" ? "rol" : errores[0].Clave);
            if (!respuestas.ConServicio)
                return NoSePuede(Respuestas.TraerEsDeUnaPcConServicio, "rol");

            // 1. El paquete. La versión de este instalador hace falta: sin ella no se sabe si la base es de un programa más nuevo.
            if (VersionDelPrograma.Numeros(version) is null)
                return NoSePuede($"No se sabe la versión de este instalador (dice «{version?.Trim()}»): sin ella no se puede revisar el paquete de mudanza.",
                    "mudanza-paquete");
            var paquete = PaqueteDeMudanza.Ver(carpeta, version);
            resumen = paquete.Resumen;
            if (paquete.Estado != EstadoDelPaquete.Bien || resumen is null)
                return NoSePuede(paquete.Error, "mudanza-paquete");
            if (paquete.ProblemaDeLaBase() is { } danada)
                return NoSePuede(danada, "mudanza-paquete");

            // 2. La PC de donde salió no volvió a trabajar con su base (allá se canceló la mudanza): serían dos bases vivas de la misma tienda.
            var vivas = await PreguntasDeLaMudanza.AOtrasPcAsync(resumen.Ips, resumen.Puerto, equipo, red, plazos.AOtraPc);
            if (vivas.FirstOrDefault(e => e.TieneLaTienda(resumen.Tienda)) is { } viva)
                return NoSePuede($"{resumen.Equipo} ({viva.Direccion.Host}) volvió a trabajar con su base: la mudanza se canceló allá y este paquete ya no sirve. "
                    + "Sácalo otra vez.", "mudanza-paquete");

            // 3. La base de esta PC. Si este mismo paquete ya se puso aquí (la nota), no se trae otra vez: solo falta la configuración.
            bool yaSePuso;
            try
            {
                yaSePuso = string.Equals(NotaDeLaBasePuesta.Paquete(equipo.Datos), resumen.Paquete, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return NoSePuede($"No se pudo leer {NotaDeLaBasePuesta.Ruta(equipo.Datos)} (la nota de que este paquete ya se puso aquí): {ex.Message}",
                    "mudanza-encima");
            }
            var (yaEstaba, apartaBase) = (false, false);
            var hayBase = File.Exists(Path.Combine(equipo.Datos, "pos.db"));
            if (equipo.ExeDelServicio() is null)
            {
                // Sin servicio nadie dice qué base es. Con la nota y su pos.db, es la del paquete; sin la nota, un pos.db que quedó de antes se
                // aparta solo al traer (nunca se borra). Con la nota y sin pos.db no hay nada puesto: se trae.
                yaEstaba = yaSePuso && hayBase;
                apartaBase = hayBase && !yaEstaba;
            }
            else
            {
                var deAqui = await PreguntasDeLaMudanza.AlServicioDeEstaPcAsync(red, plazos);
                // Con la nota ya está, conteste o no el servicio (eso lo dice la revisión del final). Salvo que conteste que no tiene tienda
                // (entonces se trae) o que tiene OTRA: esa no es la base del paquete, y sobre ella no se escribe su puente; se decide como
                // si la nota no existiera.
                if (yaSePuso && (!deAqui.Contesta || deAqui.TieneLaTienda(resumen.Tienda)))
                    yaEstaba = true;
                else if (!deAqui.Contesta)
                    return NoSePuede("El servicio de esta PC no contesta: no se sabe qué base tiene. Espera a que termine de arrancar (o arráncalo) y vuelve a "
                        + "intentarlo; no se cambió nada.", "mudanza-encima");
                else if (!deAqui.TiendaNueva)
                {
                    if (!respuestas.MudanzaEncima)
                        return NoSePuede($"{YaTieneUnaTienda} ({NombreDeLaTienda.De(deAqui.Sucursales)}): la base de {resumen.Equipo} no se trae encima sin "
                            + "confirmarlo. Si de verdad se quiere apartar la de aquí (no se borra) y traer esa, hay que decirlo («Sí, apartar la de aquí»; sin "
                            + "preguntar, /MUDANZAENCIMA=si).", "mudanza");
                    apartaBase = true;
                }
            }

            // 4. El servicio.json que va a quedar: el mismo «revisar» de siempre, con lo del paquete (y caja.json, que también se escribe).
            var revisado = Ayudante.Revisar(archivo, equipo.Datos, resumen);
            return revisado["error"] is { Length: > 0 } error ? NoSePuede(error, revisado["clave"]) : Salida(true, yaEstaba, apartaBase, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un archivo de esta PC que no se deja leer (servicio.json tomado por otro programa, por ejemplo): con todas sus claves.
            return NoSePuede($"No se pudo leer un archivo de esta PC: {ex.Message} No se cambió nada.", null);
        }
    }

    /// <summary>Con lo que empieza el único error de clave «mudanza» (el asistente pregunta entonces si se aparta la base de aquí).</summary>
    public const string YaTieneUnaTienda = "Esta PC ya tiene una tienda";
}
