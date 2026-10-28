using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.ControlPrint;

/// <summary>A qué Control Print conectarse y cómo presentarse.</summary>
/// <param name="Servidor">La IP o el nombre de la PC servidora (la caja principal).</param>
public sealed record OpcionesClienteControlPrint(string Servidor, int Puerto = ProtocoloControlPrint.PuertoPorOmision)
{
    /// <summary>
    /// La huella SHA-256 que tiene que presentar el servidor desde el saludo (64 cifras hexadecimales, con o sin «:»).
    /// Sin ella se fija la que presente en el saludo, después de comprobar con el código que es el que lo conoce: lo mismo
    /// que hacen las PC «Conectadas» de Control Print.
    /// </summary>
    public string? Huella { get; init; }

    /// <summary>Cómo sale esta PC en Control Print (Ajustes → Red, las PC conectadas en las últimas 12 horas).</summary>
    public string Equipo { get; init; } = $"{Environment.MachineName} (POS)";

    /// <summary>La versión que se manda; Control Print solo la muestra junto al equipo.</summary>
    public string Version { get; init; } = "PapeleriaPos " + VersionDelPos();

    public TimeProvider Reloj { get; init; } = TimeProvider.System;

    private static string VersionDelPos() =>
        (typeof(ClienteControlPrint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];
}

/// <summary>Los costos de Control Print (Ajustes → Costos), listos para ponerle precio a una impresión como él.</summary>
/// <param name="Costos">Una copia para quien los pidió: cambiarla no cambia la que guarda el cliente.</param>
/// <param name="LeidosUtc">Cuándo se leyeron de Control Print.</param>
/// <param name="DeAntes">Control Print no contestó y son los que se habían leído antes.</param>
public sealed record CostosDeControlPrint(CostSettings Costos, DateTimeOffset LeidosUtc, bool DeAntes);

/// <summary>
/// Lee Control Print por la red como sus PC «Conectadas» (protocolo 1; ControlPrint.Net, RemoteClient.cs) y SOLO LEE: el
/// POS nunca le escribe a Control Print (el dueño, 2026-10-25). No hay sesión ni cobros, acciones, cambios a la
/// configuración o lecturas pedidas a la impresora: la única petición que no es GET es la búsqueda de trabajos (un POST
/// que solo lee), y <see cref="EnviarAsync"/> rechaza cualquier otra antes de mandarla. Por lo mismo no está el reporte
/// (<c>api/report</c>), que pide la sesión del administrador: la búsqueda trae todos sus campos.
/// <para>
/// Para conectarse: <see cref="SaludarAsync"/> (sin el código: quién contesta, su versión y su certificado, que desde ahí
/// queda fijado) y <see cref="UsarCodigo"/> (compara el código con la prueba del saludo, sin volver a preguntarle al
/// servidor), o las dos juntas con <see cref="ConectarAsync"/>. El código solo viaja después, en el encabezado de cada
/// lectura, y nunca sale en un mensaje.
/// </para>
/// <para>
/// Como en Control Print, las llamadas son cortas (3 s para conectar, 8 s cada una) y, sin conexión, las siguientes
/// fallan al instante durante 5 s para no congelar a quien espera (<see cref="ReintentarYa"/> lo quita). Los errores son
/// <see cref="ErrorControlPrint"/>, con un mensaje para mostrar.
/// </para>
/// </summary>
public sealed class ClienteControlPrint : IDisposable
{
    public static readonly TimeSpan PlazoParaConectar = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PlazoSaludo = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PlazoLlamada = TimeSpan.FromSeconds(8);
    /// <summary>Para lo que puede ser mucho: todo el historial de lecturas de la impresora o de cotizaciones.</summary>
    public static readonly TimeSpan PlazoLargo = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PausaSinConexion = TimeSpan.FromSeconds(5);
    /// <summary>Cada cuánto se vuelven a leer los costos aunque Control Print no haya avisado que cambiaron.</summary>
    public static readonly TimeSpan VigenciaCostos = TimeSpan.FromMinutes(5);

    internal const string RutaBusqueda = "api/jobs/search";

    private readonly HttpClient _http;
    private readonly string? _huellaEsperada;
    private readonly string _equipo;
    private readonly string _version;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _leyendoCostos = new(1, 1);
    private string? _huellaVista;
    private string? _huellaFijada;
    private string? _huellaRechazada;
    private SaludoControlPrint? _saludo;
    private string? _reto;
    private string? _codigo;
    private DateTimeOffset _sinConexionHasta;
    private long _versionConfiguracion;
    private CostosEnMemoria? _costos;

    public string Servidor { get; }
    public int Puerto { get; }
    /// <summary>El nombre de la PC servidora, como lo dice Control Print («PRINCIPAL»).</summary>
    public string NombreServidor => _saludo?.Nombre ?? "";
    /// <summary>La versión de Control Print en el servidor («1.12.0»).</summary>
    public string VersionServidor => _saludo?.VersionApp ?? "";
    /// <summary>La huella SHA-256 del certificado fijado, en hexadecimal (<see cref="ProtocoloControlPrint.HuellaLegible"/>).</summary>
    public string Huella => _huellaFijada ?? "";
    /// <summary>Si la última llamada pudo hablar con el servidor.</summary>
    public bool EnLinea { get; private set; } = true;

    /// <summary>Para decirle al dueño qué hacer cuando <see cref="UsarCodigo"/> no coincide.</summary>
    public string MensajeCodigoNoCoincide =>
        $"El código no coincide con el de Control Print en {NombreServidor}: cópialo de Control Print → Ajustes → Red en esa PC.";

    private ClienteControlPrint(OpcionesClienteControlPrint opciones)
    {
        Servidor = opciones.Servidor.Trim();
        // Antes de buscarlo en la red, y sin repetirlo: podría ser el código de conexión puesto por error.
        if (Uri.CheckHostName(Servidor) == UriHostNameType.Unknown || ProtocoloControlPrint.PareceCodigo(Servidor))
            throw new ArgumentException("El servidor de Control Print va con su IP o el nombre de su PC, y lo que se puso no lo es (no se repite " +
                                        "aquí, por si era el código de conexión).");
        if (opciones.Puerto is < 1 or > 65535)
            throw new ArgumentException($"El puerto va de 1 a 65535 (Control Print usa el {ProtocoloControlPrint.PuertoPorOmision}).");
        Puerto = opciones.Puerto;
        if (opciones.Huella is not null)
            _huellaEsperada = ProtocoloControlPrint.LeerHuella(opciones.Huella)
                ?? throw new ArgumentException("La huella son 64 cifras hexadecimales, con o sin «:».");
        _equipo = ParaEncabezado(opciones.Equipo);
        _version = ParaEncabezado(opciones.Version);
        _reloj = opciones.Reloj;
        var manejador = new SocketsHttpHandler
        {
            ConnectTimeout = PlazoParaConectar,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            // Directo a la PC de la red local: sin proxy, sin cookies y sin seguir redirecciones (el código no se va a otro lado).
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // El certificado es propio del servidor: vale el del saludo (o el de opciones.Huella), no la cadena de Windows.
                RemoteCertificateValidationCallback = (_, certificado, _, _) => AceptarCertificado(certificado),
            },
        };
        var anfitrion = IPAddress.TryParse(Servidor, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{Servidor}]"
            : Servidor;
        _http = new HttpClient(manejador) { BaseAddress = new Uri($"https://{anfitrion}:{Puerto}/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    // ── Conectar ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// El saludo, sin el código: quién contesta (nombre y versión) y su certificado, que desde aquí queda fijado (con
    /// <see cref="OpcionesClienteControlPrint.Huella"/>, tiene que ser ese). Después, <see cref="UsarCodigo"/>.
    /// </summary>
    public static async Task<ClienteControlPrint> SaludarAsync(OpcionesClienteControlPrint opciones, CancellationToken ct = default)
    {
        var cliente = new ClienteControlPrint(opciones);
        try
        {
            await cliente.SaludarAsync(ct);
            return cliente;
        }
        catch
        {
            cliente.Dispose();
            throw;
        }
    }

    /// <summary>El saludo y el código de una vez; si el código no coincide, <see cref="FallaControlPrint.CodigoNoCoincide"/>.</summary>
    public static async Task<ClienteControlPrint> ConectarAsync(OpcionesClienteControlPrint opciones, string codigo, CancellationToken ct = default)
    {
        var cliente = await SaludarAsync(opciones, ct);
        try
        {
            if (!cliente.UsarCodigo(codigo))
                throw new ErrorControlPrint(FallaControlPrint.CodigoNoCoincide, cliente.MensajeCodigoNoCoincide);
            return cliente;
        }
        catch
        {
            cliente.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Compara el código con la prueba que mandó el servidor en el saludo (ligada a su certificado y a un reto de esta
    /// PC), sin volver a preguntarle: si coincide, el servidor es el que conoce el código y desde aquí se puede leer. Si no,
    /// false (se puede volver a escribir). Un código sin 16 letras y números es <see cref="FallaControlPrint.CodigoIncompleto"/>.
    /// </summary>
    public bool UsarCodigo(string codigo)
    {
        if (!ProtocoloControlPrint.CodigoCompleto(codigo))
            throw new ErrorControlPrint(FallaControlPrint.CodigoIncompleto,
                "El código de conexión tiene 16 letras y números, como ABCD-EFGH-JKLM-NPQR (los guiones, los espacios y las mayúsculas dan igual).");
        var esperada = Encoding.ASCII.GetBytes(ProtocoloControlPrint.Prueba(codigo, _reto!, _huellaFijada!));
        var recibida = Encoding.ASCII.GetBytes((_saludo!.Prueba ?? "").ToUpperInvariant());
        if (!CryptographicOperations.FixedTimeEquals(esperada, recibida))
            return false;
        _codigo = ProtocoloControlPrint.NormalizarCodigo(codigo);
        return true;
    }

    /// <summary>
    /// Si un texto lleva el código de conexión de este cliente, escrito de cualquier forma (con o sin guiones o espacios, en
    /// mayúsculas o minúsculas): para no guardarlo nunca, aunque Control Print lo mandara en algún campo. Sin código, false.
    /// </summary>
    public bool LlevaElCodigo(string texto) =>
        _codigo is { } codigo && texto.Length >= codigo.Length
                              && ProtocoloControlPrint.NormalizarCodigo(texto).Contains(codigo, StringComparison.Ordinal);

    private async Task SaludarAsync(CancellationToken ct)
    {
        var reto = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        SaludoControlPrint? saludo;
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(PlazoSaludo);
            try
            {
                // Sin encabezados: el código no se manda hasta saber que el servidor lo conoce.
                using var respuesta = await _http.GetAsync($"api/hello?nonce={reto}", HttpCompletionOption.ResponseContentRead, limite.Token);
                if (!respuesta.IsSuccessStatusCode)
                    throw NoEsControlPrint(null, $"contestó {(int)respuesta.StatusCode} al saludo");
                saludo = await respuesta.Content.ReadFromJsonAsync<SaludoControlPrint>(ProtocoloControlPrint.Json, limite.Token);
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _huellaRechazada, null) is { } otra)
                    throw new ErrorControlPrint(FallaControlPrint.CertificadoDistinto,
                        $"{Servidor}:{Puerto} presentó un certificado con otra huella ({ProtocoloControlPrint.HuellaLegible(otra)}), no la que se " +
                        $"esperaba ({ProtocoloControlPrint.HuellaLegible(_huellaEsperada ?? "")}): por seguridad no se le manda nada.", ex);
                throw SinConexion(ex);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw NoEsControlPrint(ex, "no contestó al saludo como Control Print");
            }
        }
        if (saludo is null || string.IsNullOrEmpty(saludo.Prueba) || _huellaVista is null)
            throw NoEsControlPrint(null, "no contestó al saludo como Control Print");
        if (saludo.VersionProtocolo != ProtocoloControlPrint.Version)
            throw new ErrorControlPrint(FallaControlPrint.VersionDistinta,
                $"Control Print {saludo.VersionApp} en {saludo.Nombre} habla la versión {saludo.VersionProtocolo} de su protocolo y este POS la " +
                $"{ProtocoloControlPrint.Version}: hace falta actualizar el POS.");
        _saludo = saludo;
        _reto = reto;
        // Desde aquí, cualquier conexión nueva tiene que presentar este certificado.
        Volatile.Write(ref _huellaFijada, _huellaVista);
    }

    private bool AceptarCertificado(X509Certificate? certificado)
    {
        if (certificado is null)
            return false;
        var huella = ProtocoloControlPrint.Huella(certificado.GetRawCertData());
        var debeSer = Volatile.Read(ref _huellaFijada) ?? _huellaEsperada;
        if (debeSer is null || string.Equals(huella, debeSer, StringComparison.OrdinalIgnoreCase))
        {
            _huellaVista ??= huella;
            return true;
        }
        _huellaRechazada = huella;
        return false;
    }

    // ── Lo que se manda: solo lecturas ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lo único que el POS le pide a Control Print: GET y la búsqueda de trabajos (un POST que solo lee). Cobrar, las
    /// acciones de un trabajo, la sesión o un PUT a la configuración no salen de este cliente.
    /// </summary>
    internal static bool EsSoloLectura(HttpMethod metodo, string ruta) =>
        metodo == HttpMethod.Get || (metodo == HttpMethod.Post && ruta == RutaBusqueda);

    private async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string ruta, object? cuerpo, TimeSpan plazo, CancellationToken ct,
        bool respetarPausa = true)
    {
        if (!EsSoloLectura(metodo, ruta))
            throw new InvalidOperationException($"El POS solo lee Control Print: «{metodo} {ruta}» no se manda.");
        if (_codigo is null)
            throw new InvalidOperationException("Falta el código de conexión: primero UsarCodigo.");
        if (respetarPausa && _reloj.GetUtcNow() < _sinConexionHasta)
            throw SinConexion(null);
        using var solicitud = new HttpRequestMessage(metodo, ruta);
        solicitud.Headers.Add(ProtocoloControlPrint.EncabezadoCodigo, _codigo);
        solicitud.Headers.Add(ProtocoloControlPrint.EncabezadoEquipo, _equipo);
        solicitud.Headers.Add(ProtocoloControlPrint.EncabezadoVersion, _version);
        if (cuerpo is not null)
            solicitud.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: ProtocoloControlPrint.Json);
        HttpResponseMessage respuesta;
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(plazo);
            try
            {
                respuesta = await _http.SendAsync(solicitud, HttpCompletionOption.ResponseContentRead, limite.Token);
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _huellaRechazada, null) is { } otra)
                    throw new ErrorControlPrint(FallaControlPrint.CertificadoDistinto,
                        $"El certificado de {Servidor}:{Puerto} cambió desde el saludo (ahora su huella es {ProtocoloControlPrint.HuellaLegible(otra)}): " +
                        "por seguridad ya no se le manda nada. Vuelve a conectarte.", ex);
                _sinConexionHasta = _reloj.GetUtcNow() + PausaSinConexion;
                EnLinea = false;
                throw SinConexion(ex);
            }
        }
        EnLinea = true;
        if (!respuesta.IsSuccessStatusCode)
            throw await ErrorDeLaRespuestaAsync(respuesta, ruta, ct);
        return respuesta;
    }

    /// <summary>Vuelve a intentar de inmediato, sin esperar la pausa después de una falla de conexión.</summary>
    public void ReintentarYa() => _sinConexionHasta = DateTimeOffset.MinValue;

    private async Task<ErrorControlPrint> ErrorDeLaRespuestaAsync(HttpResponseMessage respuesta, string ruta, CancellationToken ct)
    {
        using (respuesta)
        {
            ErrorDeControlPrint? error = null;
            try
            {
                error = await respuesta.Content.ReadFromJsonAsync<ErrorDeControlPrint>(ProtocoloControlPrint.Json, ct);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
            }
            var codigoHttp = (int)respuesta.StatusCode;
            var mensaje = error?.Mensaje is { Length: > 0 } texto ? texto : $"contestó {codigoHttp}";
            return (error?.Tipo, respuesta.StatusCode) switch
            {
                ("key", _) => new ErrorControlPrint(FallaControlPrint.CodigoRechazado,
                    $"Control Print en {NombreServidor} ya no acepta el código de conexión: ¿se cambió en Ajustes → Red? Vuelve a conectarte con el código nuevo."),
                ("session" or "login" or "denied", _) or (_, HttpStatusCode.Forbidden) => new ErrorControlPrint(FallaControlPrint.SinPermiso,
                    $"Control Print pide una sesión para «{RutaSinConsulta(ruta)}», y el POS no inicia sesión en Control Print (solo lee): {mensaje}"),
                ("missing", _) or (_, HttpStatusCode.NotFound) => new ErrorControlPrint(FallaControlPrint.NoExiste,
                    $"Control Print no tiene «{RutaSinConsulta(ruta)}»: {mensaje}"),
                _ => new ErrorControlPrint(FallaControlPrint.Servidor, $"Control Print contestó {codigoHttp} a «{RutaSinConsulta(ruta)}»: {mensaje}"),
            };
        }
    }

    private async Task<T> LeerAsync<T>(string ruta, TimeSpan plazo, CancellationToken ct, bool respetarPausa = true)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Get, ruta, null, plazo, ct, respetarPausa);
        return await DeserializarAsync<T>(respuesta, ruta, ct);
    }

    /// <summary>Lo que puede no haber: 204 o 404 son null.</summary>
    private async Task<T?> LeerSiHayAsync<T>(string ruta, TimeSpan plazo, CancellationToken ct) where T : class
    {
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await EnviarAsync(HttpMethod.Get, ruta, null, plazo, ct);
        }
        catch (ErrorControlPrint ex) when (ex.Falla == FallaControlPrint.NoExiste)
        {
            return null;
        }
        using (respuesta)
            return respuesta.StatusCode == HttpStatusCode.NoContent ? null : await DeserializarAsync<T>(respuesta, ruta, ct);
    }

    private async Task<string?> LeerTextoAsync(string ruta, TimeSpan plazo, CancellationToken ct)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Get, ruta, null, plazo, ct);
        return respuesta.StatusCode == HttpStatusCode.NoContent ? null : await respuesta.Content.ReadAsStringAsync(ct);
    }

    private static async Task<T> DeserializarAsync<T>(HttpResponseMessage respuesta, string ruta, CancellationToken ct)
    {
        try
        {
            return await respuesta.Content.ReadFromJsonAsync<T>(ProtocoloControlPrint.Json, ct)
                ?? throw new JsonException("Vacía.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ErrorControlPrint(FallaControlPrint.Servidor,
                $"Control Print contestó a «{RutaSinConsulta(ruta)}» algo que este POS no entiende.", ex);
        }
    }

    // ── Trabajos ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Una página de trabajos (los más nuevos primero) y cuántos cumplen el filtro; sin sus miniaturas.</summary>
    public async Task<PaginaTrabajosControlPrint> BuscarTrabajosAsync(BusquedaTrabajosControlPrint busqueda, CancellationToken ct = default)
    {
        // Las fechas tienen que ir con su «Z»: sin ella, el servidor las tomaría en su hora local.
        busqueda = busqueda with { DesdeUtc = EnUtc(busqueda.DesdeUtc), HastaUtc = EnUtc(busqueda.HastaUtc) };
        using var respuesta = await EnviarAsync(HttpMethod.Post, RutaBusqueda, busqueda, PlazoLlamada, ct);
        return await DeserializarAsync<PaginaTrabajosControlPrint>(respuesta, RutaBusqueda, ct);
    }

    /// <summary>Un trabajo con su miniatura; null si ya no existe.</summary>
    public Task<TrabajoControlPrint?> TrabajoAsync(long id, CancellationToken ct = default) =>
        LeerSiHayAsync<TrabajoControlPrint>($"api/jobs/{id}", PlazoLlamada, ct);

    /// <summary>La miniatura de un trabajo (PNG); null si no tiene o si ya no existe.</summary>
    public async Task<byte[]?> MiniaturaAsync(long id, CancellationToken ct = default)
    {
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await EnviarAsync(HttpMethod.Get, $"api/jobs/{id}/thumbnail", null, PlazoLlamada, ct);
        }
        catch (ErrorControlPrint ex) when (ex.Falla == FallaControlPrint.NoExiste)
        {
            return null;
        }
        using (respuesta)
            return respuesta.StatusCode == HttpStatusCode.NoContent ? null : await respuesta.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Las cotizaciones vigentes que corresponden a ese trabajo (el mismo documento).</summary>
    public async Task<IReadOnlyList<CotizacionControlPrint>> CotizacionesDelTrabajoAsync(long id, CancellationToken ct = default) =>
        await LeerAsync<List<CotizacionControlPrint>>($"api/jobs/{id}/quotes", PlazoLlamada, ct);

    /// <summary>
    /// El resumen de un día, el día de la hora del servidor; con <paramref name="atendioId"/>, solo los de ese usuario (si
    /// Control Print está en «cada quien ve los suyos», el servidor lo limita al invitado).
    /// </summary>
    public Task<ResumenDiaControlPrint> ResumenDelDiaAsync(DateOnly dia, long? atendioId = null, CancellationToken ct = default) =>
        LeerAsync<ResumenDiaControlPrint>(
            $"api/summary?day={dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{(atendioId is { } id ? $"&owner={id}" : "")}",
            PlazoLlamada, ct);

    /// <summary>Las caras registradas (impresas y copias, de todos) de los trabajos detectados en [desde, hasta).</summary>
    public Task<CarasRegistradasControlPrint> CarasAsync(DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct = default) =>
        LeerAsync<CarasRegistradasControlPrint>($"api/jobs/sides?from={Iso(desdeUtc)}&to={Iso(hastaUtc)}", PlazoLlamada, ct);

    // ── La impresora ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Las lecturas guardadas de la impresora desde una fecha (todas, sin ella), de la más vieja a la más nueva.</summary>
    public async Task<IReadOnlyList<LecturaImpresoraControlPrint>> LecturasAsync(DateTime? desdeUtc = null, CancellationToken ct = default) =>
        await LeerAsync<List<LecturaImpresoraControlPrint>>(desdeUtc is { } desde ? $"api/status?since={Iso(desde)}" : "api/status", PlazoLargo, ct);

    /// <summary>La última lectura guardada; null si no hay ninguna.</summary>
    public Task<LecturaImpresoraControlPrint?> UltimaLecturaAsync(CancellationToken ct = default) =>
        LeerSiHayAsync<LecturaImpresoraControlPrint>("api/status/latest", PlazoLlamada, ct);

    /// <summary>La calibración vigente tal como la manda Control Print (JSON); null si no hay.</summary>
    public Task<string?> CalibracionJsonAsync(CancellationToken ct = default) => LeerTextoAsync("api/calibration", PlazoLlamada, ct);

    /// <summary>Cómo está el servidor: la impresora y el monitor de la cola.</summary>
    public Task<SaludControlPrint> SaludAsync(CancellationToken ct = default) => LeerAsync<SaludControlPrint>("api/health", PlazoLlamada, ct);

    // ── Cotizaciones ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Las cotizaciones guardadas desde una fecha, las más nuevas primero, hasta <paramref name="limite"/>.</summary>
    public async Task<IReadOnlyList<CotizacionControlPrint>> CotizacionesAsync(DateTime desdeUtc, string? texto = null, int limite = 200,
        CancellationToken ct = default) =>
        await LeerAsync<List<CotizacionControlPrint>>(
            $"api/quotes?since={Iso(desdeUtc)}&limit={limite}{(string.IsNullOrWhiteSpace(texto) ? "" : $"&text={Uri.EscapeDataString(texto)}")}",
            limite > 200 ? PlazoLargo : PlazoLlamada, ct);

    /// <summary>Una cotización; null si no existe.</summary>
    public Task<CotizacionControlPrint?> CotizacionAsync(long id, CancellationToken ct = default) =>
        LeerSiHayAsync<CotizacionControlPrint>($"api/quotes/{id}", PlazoLlamada, ct);

    // ── Usuarios y configuración ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Los usuarios de Control Print: número, nombre, perfil y si está activo (nada más).</summary>
    public async Task<IReadOnlyList<UsuarioControlPrint>> UsuariosAsync(bool tambienInactivos = true, CancellationToken ct = default) =>
        await LeerAsync<List<UsuarioControlPrint>>($"api/users?all={(tambienInactivos ? "true" : "false")}", PlazoLlamada, ct);

    /// <summary>Una configuración compartida tal como la guarda Control Print (JSON, con los nombres en PascalCase).</summary>
    public async Task<string> ConfiguracionJsonAsync(ConfiguracionControlPrint configuracion, CancellationToken ct = default) =>
        await LeerTextoAsync($"api/settings/{(configuracion == ConfiguracionControlPrint.Costos ? "cost" : "app")}", PlazoLlamada, ct)
            ?? throw new ErrorControlPrint(FallaControlPrint.Servidor, "Control Print no mandó su configuración.");

    /// <summary>De los ajustes de la app, lo que usa el POS: la impresora, cada cuánto la lee y quién ve los trabajos.</summary>
    public async Task<AjustesControlPrint> AjustesAsync(CancellationToken ct = default)
    {
        var json = await ConfiguracionJsonAsync(ConfiguracionControlPrint.Ajustes, ct);
        try
        {
            return JsonSerializer.Deserialize<AjustesControlPrint>(json, ProtocoloControlPrint.Json) ?? throw new JsonException("Vacía.");
        }
        catch (JsonException ex)
        {
            throw new ErrorControlPrint(FallaControlPrint.Servidor, "Los ajustes de Control Print no se entienden.", ex);
        }
    }

    /// <summary>
    /// Los costos de Control Print, para ponerle precio a una impresión como él. Se guardan en memoria y se vuelven a leer
    /// cuando Control Print avisa que cambió su configuración (lo que se vea en <see cref="CambiosAsync"/>), cuando pasaron
    /// <see cref="VigenciaCostos"/> o con <paramref name="forzar"/>. Si Control Print no contesta y ya se tenían, se dan
    /// los de antes (<see cref="CostosDeControlPrint.DeAntes"/>); si nunca se leyeron, el error. Cada llamada recibe su
    /// propia copia.
    /// </summary>
    public async Task<CostosDeControlPrint> CostosAsync(bool forzar = false, CancellationToken ct = default)
    {
        if (!forzar && Volatile.Read(ref _costos) is { } guardados && AlDia(guardados))
            return guardados.Copia(deAntes: false);
        await _leyendoCostos.WaitAsync(ct);
        try
        {
            var antes = Volatile.Read(ref _costos);
            if (!forzar && antes is not null && AlDia(antes))
                return antes.Copia(deAntes: false);
            try
            {
                // Primero el contador de la configuración y después los costos: si cambian en medio, el contador guardado es
                // el de antes y la siguiente vez se vuelven a leer.
                var versiones = await CambiosAsync(VersionesControlPrint.Ninguna, TimeSpan.Zero, ct);
                var json = await ConfiguracionJsonAsync(ConfiguracionControlPrint.Costos, ct);
                var leidos = new CostosEnMemoria(json, _reloj.GetUtcNow(), versiones.Configuracion);
                var copia = leidos.Copia(deAntes: false);
                Volatile.Write(ref _costos, leidos);
                return copia;
            }
            catch (ErrorControlPrint ex) when (ex.Falla == FallaControlPrint.SinConexion && antes is not null)
            {
                return antes.Copia(deAntes: true);
            }
        }
        finally
        {
            _leyendoCostos.Release();
        }
    }

    private bool AlDia(CostosEnMemoria costos) =>
        _reloj.GetUtcNow() - costos.LeidosUtc < VigenciaCostos && Interlocked.Read(ref _versionConfiguracion) <= costos.VersionConfiguracion;

    private sealed record CostosEnMemoria(string Json, DateTimeOffset LeidosUtc, long VersionConfiguracion)
    {
        public CostosDeControlPrint Copia(bool deAntes)
        {
            try
            {
                var costos = JsonSerializer.Deserialize<CostSettings>(Json, CostSettings.Json) ?? throw new JsonException("Vacía.");
                return new CostosDeControlPrint(costos, LeidosUtc, deAntes);
            }
            catch (JsonException ex)
            {
                throw new ErrorControlPrint(FallaControlPrint.Servidor, "Los costos de Control Print no se entienden.", ex);
            }
        }
    }

    // ── Cambios ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Espera hasta <paramref name="espera"/> (hasta 25 s) a que algo cambie en el servidor y devuelve sus contadores; con los
    /// mismos que se le pasan, es que no cambió nada. Con <see cref="VersionesControlPrint.Ninguna"/> contesta de inmediato.
    /// Como en Control Print, no la detiene la pausa sin conexión: es la que avisa que ya volvió.
    /// </summary>
    public async Task<VersionesControlPrint> CambiosAsync(VersionesControlPrint desde, TimeSpan espera, CancellationToken ct = default)
    {
        var segundos = (int)Math.Clamp(Math.Ceiling(espera.TotalSeconds), 0, 25);
        var versiones = await LeerAsync<VersionesControlPrint>(
            $"api/changes?jobs={desde.Trabajos}&settings={desde.Configuracion}&users={desde.Usuarios}&status={desde.Impresora}&wait={segundos}",
            TimeSpan.FromSeconds(segundos + 15), ct, respetarPausa: false);
        ReintentarYa();
        long actual;
        while ((actual = Interlocked.Read(ref _versionConfiguracion)) < versiones.Configuracion
               && Interlocked.CompareExchange(ref _versionConfiguracion, versiones.Configuracion, actual) != actual)
        {
        }
        return versiones;
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────────────────────────────────────────

    private ErrorControlPrint SinConexion(Exception? causa) => new(FallaControlPrint.SinConexion,
        $"No se pudo hablar con Control Print en {Servidor}:{Puerto}: revisa que esa PC esté prendida y en la red, que Control Print esté " +
        $"abierto en modo servidor y que su firewall deje pasar el puerto {Puerto}.", causa);

    private ErrorControlPrint NoEsControlPrint(Exception? causa, string que) =>
        new(FallaControlPrint.Servidor, $"{Servidor}:{Puerto} {que}: ¿es la PC de Control Print en modo servidor?", causa);

    /// <summary>En los mensajes, la ruta sin la consulta.</summary>
    private static string RutaSinConsulta(string ruta) => ruta.Split('?')[0];

    private static DateTime? EnUtc(DateTime? fecha) => fecha is { } f ? EnUtc(f) : null;

    private static DateTime EnUtc(DateTime fecha) => fecha.Kind switch
    {
        DateTimeKind.Local => fecha.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(fecha, DateTimeKind.Utc),
        _ => fecha,
    };

    private static string Iso(DateTime utc) => Uri.EscapeDataString(EnUtc(utc).ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Un encabezado HTTP solo lleva ASCII visible.</summary>
    private static string ParaEncabezado(string texto)
    {
        var limpio = new string(texto.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()).Trim();
        return limpio.Length > 100 ? limpio[..100] : limpio;
    }

    public void Dispose()
    {
        _http.Dispose();
        _leyendoCostos.Dispose();
    }
}
