using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>Si la caja principal reconoce a la caja de esta PC (lo que dice «conoce» en <c>probar --esta-caja si</c>).</summary>
public enum ConoceALaCaja
{
    /// <summary>Probó con la huella de la llave de esta caja que es su caja principal, y lo firmó con su propia llave.</summary>
    Si,

    /// <summary>
    /// Dice que no la conoce (404), o lo que contestó no es la prueba de quien conoce la llave de esta caja. También cuando por eso una
    /// corrida anterior de «Terminar la mudanza» ya le quitó el alta a esta caja (<see cref="AltaDeLaCaja.SeQuitoAlTerminarLaMudanza"/>).
    /// </summary>
    No,

    /// <summary>No se pudo saber: la red, un 403 (esta PC no está en su red local), la llave de esta caja no se dejó abrir…</summary>
    NoSe,

    /// <summary>La caja de esta PC no tiene alta: no hay llave con qué probar nada.</summary>
    SinAlta,
}

/// <summary>Lo que salió de pedirle a la caja principal que pruebe quién es.</summary>
/// <param name="Motivo">Por qué, dicho para quien lea el registro de la instalación. Nunca trae la llave de la caja ni su huella.</param>
/// <param name="LlaveDelServicio">Solo con <see cref="ConoceALaCaja.Si"/>: la llave pública que presentó, como la guarda la caja.</param>
public sealed record PruebaDeIdentidad(ConoceALaCaja Conoce, string? Motivo, string? LlaveDelServicio = null)
{
    /// <summary>Como va en la salida: si, no, no-se o sin-alta.</summary>
    public string Nombre => Conoce switch
    {
        ConoceALaCaja.Si => "si",
        ConoceALaCaja.No => "no",
        ConoceALaCaja.SinAlta => "sin-alta",
        _ => "no-se",
    };
}

/// <summary>El alta de la caja de esta PC, como la guarda en caja.json: qué caja es y su llave, cifrada para esta PC.</summary>
internal sealed record AltaDeLaCaja(Guid CajaId, string LlaveProtegida)
{
    /// <summary>
    /// La del caja.json de esta PC (el de la carpeta Caja o, si no, el de antes: el mismo que abre la caja); vacía si no tiene alta
    /// (le falta <c>CajaId</c> o <c>LlaveProtegida</c>) o no hay caja.json. Mal escrito, FormatException (la caja tampoco abriría).
    /// </summary>
    public static AltaDeLaCaja? DeEstaPc(string datos) => RutaEnUso(datos) is { } ruta ? De(Json.Objeto(File.ReadAllBytes(ruta), "caja.json", comentarios: false)) : null;

    /// <summary>El caja.json que abre la caja: el de la carpeta Caja o, si no, el de antes; vacío si no hay.</summary>
    private static string? RutaEnUso(string datos) =>
        File.Exists(CajaJson.Ruta(datos)) ? CajaJson.Ruta(datos) : File.Exists(CajaJson.RutaDeAntes(datos)) ? CajaJson.RutaDeAntes(datos) : null;

    private static AltaDeLaCaja? De(JsonObject caja) =>
        Guid.TryParse(Json.Texto(Json.Valor(caja, "CajaId")), out var cajaId)
        && Json.Texto(Json.Valor(caja, "LlaveProtegida")) is { } llave && llave.Trim().Length > 0
            ? new AltaDeLaCaja(cajaId, llave)
            : null;

    /// <summary>
    /// Si a la caja de esta PC le quitó el alta una corrida anterior de «Terminar la mudanza» (<c>escribir --mudada-a … --alta otra-vez</c>,
    /// porque la PC nueva ya no reconocía su llave) y esa corrida no llegó al final: caja.json ya está convertido (su dirección de confianza
    /// es otra PC, la misma de «Servicio») y sin alta, y el de antes (caja.json.anterior, junto a él) sí la tenía. Repetir «Terminar» tiene
    /// que decir lo mismo que la primera vez (la caja pide su alta y se da como la MISMA de antes), no que nunca tuvo alta. Lo que no se
    /// deja leer no lo prueba.
    /// </summary>
    public static bool SeQuitoAlTerminarLaMudanza(string datos)
    {
        try
        {
            if (RutaEnUso(datos) is not { } ruta)
                return false;
            var caja = Json.Objeto(File.ReadAllBytes(ruta), "caja.json", comentarios: false);
            var anterior = ruta + ".anterior";
            return De(caja) is null
                && Uri.TryCreate(Json.Texto(Json.Valor(caja, "DireccionDeConfianza")), UriKind.Absolute, out var deConfianza) && !deConfianza.IsLoopback
                && Uri.TryCreate(Json.Texto(Json.Valor(caja, "Servicio")), UriKind.Absolute, out var servicio)
                && string.Equals(servicio.Scheme, deConfianza.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(servicio.Host, deConfianza.Host, StringComparison.OrdinalIgnoreCase) && servicio.Port == deConfianza.Port
                && new FileInfo(anterior) is { Exists: true, Length: <= 1024 * 1024 }
                && De(Json.Objeto(File.ReadAllBytes(anterior), "caja.json.anterior", comentarios: false)) is not null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// La prueba de identidad de siempre, hecha desde el instalador para terminar una mudanza (<c>probar --esta-caja si</c>;
/// docs/instalacion.md): antes de dejar la caja de esta PC apuntando a la PC nueva, que esa PC pruebe que conoce la llave de ESTA caja.
/// Es lo mismo que hace la caja antes de mandarle su llave a nadie (src/PapeleriaPos.Interfaz/ReencuentroDeLaPrincipal.cs y
/// Descubrimiento.cs): un reto nuevo, <c>GET api/identidad?caja=…&amp;reto=…</c> sin llave, y de lo que contesta, dos cosas. Una, el
/// HMAC-SHA256 del reto, la caja y la dirección («ip:puerto») a la que se le habló, hecho con la huella de la llave de la caja (lo
/// único que el servicio guarda de ella, y que viaja con la base): solo cuadra si esa base conoce la llave que esta caja tiene hoy. Dos,
/// lo mismo firmado con la llave propia de ese servicio (ECDSA P-256), cuya parte pública es la que la caja recuerda como
/// <c>LlaveDelServicio</c>.
/// <para>
/// El ayudante no usa otros proyectos ni paquetes, así que aquí está escrito otra vez lo mínimo, con lo que trae .NET; una prueba
/// revisa que dé lo mismo que el código de la caja y que la caja de verdad dé por buena la llave que sale de aquí. La llave de la caja
/// se abre con Windows (<see cref="IEquipo.AbrirLoDeEstaPc"/>), se usa y se borra: ni ella ni su huella salen de aquí, ni a la salida,
/// ni a la pantalla, ni en el texto de un error.
/// </para>
/// </summary>
public static class IdentidadDeLaPrincipal
{
    /// <summary>Lo que se le pide al servicio para que pruebe quién es, sin llave (Descubrimiento.RutaIdentidad).</summary>
    public const string RutaIdentidad = "api/identidad";

    /// <summary>Con qué empieza lo que va en el HMAC, y lo que el servicio firma con su llave (Descubrimiento).</summary>
    private const string Version = "papeleria-pos/identidad/1";

    private const string VersionDeLaFirma = "papeleria-pos/identidad-del-servicio/1";

    /// <summary>Lo más que se lee de lo que contesta (la prueba es un renglón), como la caja.</summary>
    public const int TopeDeLaRespuesta = 4096;

    private const string LaLlaveNoSeAbrio =
        "La llave de la caja de esta PC (LlaveProtegida, en caja.json) no se pudo abrir aquí: viene de otra PC, o Windows se reinstaló.";

    /// <summary>
    /// Si esa caja principal (ya se le preguntó su api/estado) reconoce a la caja de esta PC. Nunca truena: lo que no se puede saber
    /// es <see cref="ConoceALaCaja.NoSe"/>, con su porqué.
    /// </summary>
    public static async Task<PruebaDeIdentidad> ProbarAsync(EstadoDeLaPrincipal? principal, IEquipo equipo, HttpMessageHandler? red, TimeSpan espera)
    {
        try
        {
            AltaDeLaCaja? alta;
            try
            {
                alta = AltaDeLaCaja.DeEstaPc(equipo.Datos);
            }
            catch (FormatException ex)
            {
                return NoSe($"{ex.Message} No se sabe si la caja de esta PC tiene alta.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return NoSe($"No se pudo leer el caja.json de esta PC: {ex.Message}");
            }
            if (alta is null)
                return AltaDeLaCaja.SeQuitoAlTerminarLaMudanza(equipo.Datos)
                    ? No("La caja de esta PC ya no tiene alta: se la quitó una corrida anterior de «Terminar la mudanza», porque la caja principal ya no "
                        + "reconocía su llave (la de antes quedó en caja.json.anterior). Al abrirla pide su alta: se da como la MISMA caja de antes.")
                    : new PruebaDeIdentidad(ConoceALaCaja.SinAlta,
                        "La caja de esta PC no tiene alta (no hay caja.json, o le falta CajaId o LlaveProtegida): se da de alta al abrirla.");
            if (principal is not { Contesta: true })
                return NoSe("La caja principal no contestó: no se le pidió que probara quién es.");

            // La huella de la llave de esta caja: lo que el servicio guarda de ella. La llave, abierta, no dura más que esto.
            string huella;
            byte[]? abierta = null;
            try
            {
                abierta = equipo.AbrirLoDeEstaPc(Convert.FromBase64String(alta.LlaveProtegida));
                if (abierta is null)
                    return NoSe(LaLlaveNoSeAbrio);
                huella = Huella(abierta);
            }
            catch (FormatException)
            {
                return NoSe(LaLlaveNoSeAbrio);
            }
            finally
            {
                if (abierta is not null)
                    CryptographicOperations.ZeroMemory(abierta);
            }

            var direcciones = await DireccionesAsync(principal.Direccion, espera);
            if (direcciones.Count == 0)
                return NoSe($"No se encontró la IP de «{principal.Direccion.Host}»: la prueba de identidad va con la IP de la caja principal.");
            var revisiones = await Task.WhenAll(direcciones.Select(direccion => RevisarAsync(direccion, alta.CajaId, huella, red, espera)));
            // Por nombre puede haber varias IP (dos tarjetas): basta con que una lo pruebe; y si ninguna, pesa más un «no» que un «no sé».
            return revisiones.FirstOrDefault(r => r.Conoce == ConoceALaCaja.Si) ?? revisiones.FirstOrDefault(r => r.Conoce == ConoceALaCaja.No) ?? revisiones[0];
        }
        catch (Exception ex)
        {
            // Lo que nadie esperaba tampoco detiene la mudanza: no se pudo saber, y la caja hace su propia prueba al abrirla. Solo el
            // tipo del error: su texto no se repite (aquí se manejó la llave de la caja).
            return NoSe($"No se pudo hacer la prueba de identidad ({ex.GetType().Name}).");
        }
    }

    private static PruebaDeIdentidad NoSe(string motivo) => new(ConoceALaCaja.NoSe, motivo);

    private static PruebaDeIdentidad No(string motivo) => new(ConoceALaCaja.No, motivo);

    /// <summary>
    /// Le pide a esa dirección (por su IP) que pruebe que es la caja principal de esa caja, como ReencuentroDeLaPrincipal.RevisarAsync:
    /// un reto nuevo y, de lo que contesta, el HMAC esperado para ESA dirección y la firma de su llave sobre lo mismo.
    /// </summary>
    private static async Task<PruebaDeIdentidad> RevisarAsync(Uri servicio, Guid caja, string huella, HttpMessageHandler? red, TimeSpan espera)
    {
        var donde = servicio.Authority;
        if (Direccion(servicio) is not { } direccion)
            return NoSe($"«{servicio.Host}» no es una IP: la prueba de identidad va con la IP de la caja principal.");
        var reto = NuevoReto();
        // El plazo es para todo: conectar, que conteste y leer lo que contesta.
        using var limite = new CancellationTokenSource(espera);
        try
        {
            using var cliente = BusquedaDeLaPrincipal.Cliente(red, espera);
            using var pregunta = new HttpRequestMessage(HttpMethod.Get, new Uri(servicio, $"{RutaIdentidad}?caja={caja:D}&reto={Uri.EscapeDataString(reto)}"));
            using var respuesta = await cliente.SendAsync(pregunta, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            if (respuesta.StatusCode == HttpStatusCode.NotFound)
                return No($"La caja principal ({donde}) dice que no conoce a esta caja: su base no trae el alta de esta PC (o allá la quitaron).");
            if (respuesta.StatusCode == HttpStatusCode.Forbidden)
                return NoSe($"La caja principal ({donde}) no le prueba quién es a esta PC (contestó 403): solo lo hace dentro de su red local, y esta PC "
                    + "no está en ella. La caja de esta PC tampoco va a poder comprobarla.");
            if (!respuesta.IsSuccessStatusCode)
                return NoSe($"La caja principal ({donde}) contestó con el código {(int)respuesta.StatusCode} a la prueba de identidad.");

            // Como la lee la caja (IdentidadServicioDto): «prueba», y si las trae, «llaveDelServicio» y «firma». Lo que no es eso no prueba nada.
            if (await LeerAsync(respuesta, limite.Token) is not { } identidad || !Texto(identidad, "prueba", out var prueba)
                || !Texto(identidad, "llaveDelServicio", out var publica) || !Texto(identidad, "firma", out var firma)
                || !EsLaPrueba(prueba, Prueba(huella, caja, reto, direccion)))
                return No($"La caja principal ({donde}) contestó, pero no probó conocer la llave de esta caja: allá esta caja tiene otra llave (se dio de "
                    + "alta otra PC como esta caja), o quien contesta no es la caja principal de esta caja.");
            // La llave del servicio cuenta solo con una firma suya que cuadre con este reto, esta dirección y esa prueba; y escrita
            // como se guarda (la caja la compara letra por letra): la que sale de aquí es la que acepta «escribir --llave-del-servicio».
            return publica is not null && FirmaValida(publica, MensajeFirmado(caja, reto, direccion, prueba!), firma) && EsLlaveDeServicio(publica)
                ? new PruebaDeIdentidad(ConoceALaCaja.Si, null, publica)
                : NoSe($"La caja principal ({donde}) probó conocer la llave de esta caja, pero no presentó su propia llave con una firma que cuadre: "
                    + "la caja la aprende al abrirla.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {
            return NoSe($"La caja principal ({donde}) no contestó a la prueba de identidad ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// La dirección por IP, esa; por nombre, cada IPv4 de ese nombre (hasta cuatro, si el DNS contesta a tiempo), como
    /// ReencuentroDeLaPrincipal.DireccionesAsync: la prueba se hace sobre la IP a la que se le habla.
    /// </summary>
    private static async Task<IReadOnlyList<Uri>> DireccionesAsync(Uri servicio, TimeSpan espera)
    {
        if (IPAddress.TryParse(servicio.Host.Trim('[', ']'), out _))
            return [servicio];
        try
        {
            var ips = await Dns.GetHostAddressesAsync(servicio.Host).WaitAsync(espera);
            return [.. ips.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Distinct().Take(4)
                .Select(ip => new UriBuilder(servicio) { Host = ip.ToString(), Path = "/" }.Uri)];
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Lo que contestó, si es un objeto JSON que cabe en el tope; vacío si no es eso.</summary>
    private static async Task<JsonObject?> LeerAsync(HttpResponseMessage respuesta, CancellationToken ct)
    {
        if (respuesta.Content.Headers.ContentLength is > TopeDeLaRespuesta)
            return null;
        await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);
        var leido = new byte[TopeDeLaRespuesta + 1];
        var largo = 0;
        int parte;
        while (largo < leido.Length && (parte = await flujo.ReadAsync(leido.AsMemory(largo), ct)) > 0)
            largo += parte;
        if (largo is 0 or > TopeDeLaRespuesta)
            return null;
        try
        {
            return JsonNode.Parse(leido.AsSpan(0, largo)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Un dato de la respuesta, que es un texto o no viene (o viene en null); con otra cosa ahí, la respuesta no se entiende.</summary>
    private static bool Texto(JsonObject objeto, string nombre, out string? texto)
    {
        texto = null;
        try
        {
            var nodo = Json.Valor(objeto, nombre);
            if (nodo is null)
                return true;
            if (nodo is not JsonValue valor || valor.GetValueKind() != JsonValueKind.String)
                return false;
            texto = valor.GetValue<string>();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Una clave repetida: JsonObject no la acepta.
            return false;
        }
    }

    // ── Lo mismo que Descubrimiento (src/PapeleriaPos.Interfaz) y LlaveCaja (src/PapeleriaPos.Nucleo), escrito aquí ──

    /// <summary>Un reto nuevo, al azar (32 bytes en base64 para URL), como Descubrimiento.NuevoReto.</summary>
    public static string NuevoReto() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// La huella de la llave de una caja (SHA-256 en hexadecimal), como LlaveCaja.Huella: es lo que guarda el servicio. Recibe la llave
    /// abierta (sus bytes en UTF-8) y la lee como la lee la caja, que la tiene como texto.
    /// </summary>
    public static string Huella(ReadOnlySpan<byte> llave) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(llave))));

    /// <summary>
    /// La dirección con que se firma: la IP y el puerto a los que se le habla («192.168.0.10:5080»), como Descubrimiento.Direccion.
    /// Vacía si no es una IP.
    /// </summary>
    public static string? Direccion(Uri servicio)
    {
        if (!IPAddress.TryParse(servicio.Host.Trim('[', ']'), out var ip))
            return null;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{servicio.Port}" : $"{ip}:{servicio.Port}";
    }

    /// <summary>
    /// La prueba: HMAC-SHA256 de la versión, la caja, el reto y la dirección, con la huella de la llave de la caja como llave. En
    /// hexadecimal, como Descubrimiento.Prueba.
    /// </summary>
    public static string Prueba(string huellaDeLaLlave, Guid cajaId, string reto, string direccion) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(huellaDeLaLlave), Encoding.UTF8.GetBytes($"{Version}\n{cajaId:D}\n{reto}\n{direccion}")));

    /// <summary>Si la prueba que contestó es la que se esperaba (sin delatar por el tiempo cuánto coinciden), como Descubrimiento.EsLaPrueba.</summary>
    public static bool EsLaPrueba(string? contesto, string esperada) =>
        contesto is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(contesto.ToUpperInvariant()), Encoding.ASCII.GetBytes(esperada));

    /// <summary>Lo que el servicio firma con su propia llave: lo mismo de la prueba y la prueba (Descubrimiento.MensajeFirmado).</summary>
    public static byte[] MensajeFirmado(Guid cajaId, string reto, string direccion, string prueba) =>
        Encoding.UTF8.GetBytes($"{VersionDeLaFirma}\n{cajaId:D}\n{reto}\n{direccion}\n{prueba}");

    /// <summary>
    /// Si <paramref name="firma"/> es de esa llave pública (en base64, SubjectPublicKeyInfo) sobre ese mensaje: ECDSA con SHA-256, en
    /// formato IEEE P1363. Cualquier cosa rara, no (Descubrimiento.FirmaValida).
    /// </summary>
    public static bool FirmaValida(string? llavePublica, byte[] mensaje, string? firma)
    {
        if (llavePublica is not { Length: > 0 and <= 1024 } || firma is not { Length: > 0 and <= 1024 })
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(llavePublica), out _);
            return ecdsa.VerifyData(mensaje, Convert.FromBase64String(firma), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Si ese texto sirve como la llave pública de un servicio tal como la guarda la caja (<c>LlaveDelServicio</c>): el base64, escrito
    /// como lo escribe el servicio, de una llave pública que se deja leer. La caja la compara letra por letra con la que presenta su caja
    /// principal: una que no es exactamente esa la dejaría sin poder comprobarla nunca.
    /// </summary>
    public static bool EsLlaveDeServicio(string? texto)
    {
        if (texto is not { Length: > 0 and <= 1024 })
            return false;
        try
        {
            var bytes = Convert.FromBase64String(texto);
            if (Convert.ToBase64String(bytes) != texto)
                return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var leidos);
            return leidos == bytes.Length;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
