using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>Una petición que recibió el Control Print falso.</summary>
/// <param name="Codigo">El encabezado X-CP-Key tal como llegó (null: no venía).</param>
public sealed record Peticion(string Metodo, string Ruta, string Consulta, string? Codigo, string? Equipo, string? Version)
{
    /// <summary>Lo único que el POS puede pedirle: GET y la búsqueda de trabajos.</summary>
    public bool EsLectura => Metodo == "GET" || (Metodo == "POST" && Ruta == "/api/jobs/search");
}

/// <summary>
/// Un Control Print falso, dentro de la misma prueba: Kestrel con HTTPS y un certificado de prueba, con el protocolo de
/// ServerHost de Control Print 1.12 escrito aparte del cliente (el saludo con su prueba HMAC, el código en X-CP-Key, el
/// invitado cuando no hay sesión, los errores con su «kind», el filtro de «cada quien ve lo suyo», la espera de cambios) y
/// datos inventados. Anota cada petición; cualquiera que no sea de lectura (acciones, cobros, sesión, un PUT a la
/// configuración) se contesta con 418 y queda en <see cref="Escrituras"/>, y al terminar la prueba la hace fallar
/// (<see cref="DisposeAsync"/>): el POS nunca le escribe a Control Print (el dueño, 2026-10-25).
/// </summary>
public sealed class ServidorControlPrintFalso : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> Primero = new(() => CrearCertificado("Control Print PRUEBA"));
    private static readonly Lazy<X509Certificate2> Segundo = new(() => CrearCertificado("Control Print OTRO"));

    /// <summary>El certificado de siempre del servidor falso, y otro para hacerse pasar por él.</summary>
    public static X509Certificate2 CertificadoDePrueba => Primero.Value;
    public static X509Certificate2 OtroCertificado => Segundo.Value;

    private readonly object _datos = new();
    private readonly long[] _versiones = [1, 1, 1, 1];
    private TaskCompletionSource _cambio = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private ServidorControlPrintFalso(string codigo) => Codigo = codigo;

    public string Nombre { get; set; } = "PRINCIPAL";
    public string Version { get; set; } = "1.12.0";
    public int VersionProtocolo { get; set; } = 1;
    /// <summary>El código de conexión de este servidor (se puede cambiar, como «Nuevo código» en Ajustes → Red).</summary>
    public string Codigo { get; set; }
    /// <summary>Un impostor: contesta el saludo sin conocer el código.</summary>
    public bool NoConoceElCodigo { get; set; }
    /// <summary>El certificado que presenta en cada conexión nueva.</summary>
    public X509Certificate2 Certificado { get; set; } = CertificadoDePrueba;
    /// <summary>Cierra la conexión después de cada respuesta (así la siguiente petición abre otra).</summary>
    public bool CerrarConexiones { get; set; }
    /// <summary>«All» u «OwnOnly» (cada quien ve los suyos: sin sesión, los del invitado).</summary>
    public string Visibilidad { get; set; } = "All";
    public List<JobRecordCp> Trabajos { get; } = [];
    public List<QuoteRecordCp> Cotizaciones { get; } = [];
    public List<PrinterStatusCp> Lecturas { get; } = [];
    public string CostosJson { get; set; } = JsonSerializer.Serialize(
        new Medicion.CostSettings { MarginPercent = 85, MinimumPrice = 3, RoundTo = 0.5m, Scaling = Medicion.ScalingMode.BySheetCost },
        Medicion.CostSettings.Json);
    public string? CalibracionJson { get; set; } = DatosDeControlPrint.Calibracion;
    /// <summary>Antes de contestar: para detener la prueba a media recopilación o cortar la conexión. True: ya contestó.</summary>
    public Func<HttpContext, Peticion, Task<bool>>? Antes { get; set; }

    /// <summary>
    /// Desde <see cref="Antes"/>: la búsqueda que pide esa petición (sin quitársela: la vuelve a leer el falso); vacía si no es una
    /// búsqueda.
    /// </summary>
    public static async Task<JobQueryCp?> BusquedaDeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path != "/api/jobs/search")
            return null;
        ctx.Request.EnableBuffering();
        var busqueda = await JsonSerializer.DeserializeAsync<JobQueryCp>(ctx.Request.Body, Json);
        ctx.Request.Body.Position = 0;
        return busqueda;
    }
    public ConcurrentQueue<Peticion> Peticiones { get; } = new();
    /// <summary>Cada búsqueda de trabajos que se pidió, con su filtro.</summary>
    public ConcurrentQueue<JobQueryCp> Busquedas { get; } = new();
    public int Puerto { get; private set; }

    public IReadOnlyList<Peticion> Escrituras => Peticiones.Where(p => !p.EsLectura).ToList();

    /// <summary>Solo para la prueba que manda una escritura a propósito (para ver que el falso la nota): no falla al terminar.</summary>
    public bool PermitirEscrituras { get; set; }
    public string Huella => Convert.ToHexString(SHA256.HashData(Certificado.RawData));
    public int Pedidas(string ruta) => Peticiones.Count(p => p.Ruta == ruta);

    public static async Task<ServidorControlPrintFalso> IniciarAsync(string codigo, Action<ServidorControlPrintFalso>? preparar = null)
    {
        var servidor = new ServidorControlPrintFalso(codigo);
        preparar?.Invoke(servidor);
        await servidor.ArrancarAsync();
        return servidor;
    }

    /// <summary>Cambia los costos y sube su contador, como cuando el dueño guarda Ajustes → Costos.</summary>
    public void CambiarCostos(string json)
    {
        CostosJson = json;
        Avisar(1);
    }

    /// <summary>
    /// Cambia sus trabajos mientras contesta (uno nuevo, uno que se cobró en la caja principal) y sube su contador, como Control
    /// Print cuando cambia un trabajo: quien espera cambios se entera.
    /// </summary>
    public void CambiarTrabajos(Action<List<JobRecordCp>> cambio)
    {
        lock (_datos)
            cambio(Trabajos);
        Avisar(0);
    }

    public void Avisar(int contador)
    {
        Interlocked.Increment(ref _versiones[contador]);
        Interlocked.Exchange(ref _cambio, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private async Task ArrancarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
            // Sin vigilar archivos de configuración: cada servidor de prueba gastaría un vigilante del sistema.
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o =>
            o.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (_, _) => Certificado })));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        _app = builder.Build();
        _app.Use(GuardiaAsync);
        Mapear(_app);
        await _app.StartAsync();
        Puerto = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    // ── Como ServerHost.Guard ─────────────────────────────────────────────────────────────────────────────────────

    private async Task GuardiaAsync(HttpContext ctx, Func<Task> siguiente)
    {
        var encabezado = (string nombre) => ctx.Request.Headers.TryGetValue(nombre, out var valor) ? valor.ToString() : null;
        var peticion = new Peticion(ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value ?? "",
            encabezado("X-CP-Key"), encabezado("X-CP-Device"), encabezado("X-CP-Version"));
        Peticiones.Enqueue(peticion);
        if (CerrarConexiones)
            ctx.Response.Headers.Connection = "close";
        if (!peticion.EsLectura)
        {
            await ErrorAsync(ctx, 418, "write", "El POS no debe escribirle a Control Print.");
            return;
        }
        if (Antes is { } antes && await antes(ctx, peticion))
            return;
        if (!ctx.Request.Path.StartsWithSegments("/api/hello")
            && (peticion.Codigo is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Normalizar(peticion.Codigo)), Encoding.UTF8.GetBytes(Normalizar(Codigo)))))
        {
            await ErrorAsync(ctx, 401, "key", "El código de conexión no es válido. Vuelve a conectar esta PC al servidor.");
            return;
        }
        try
        {
            await siguiente();
        }
        catch (KeyNotFoundException ex)
        {
            await ErrorAsync(ctx, 404, "missing", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            await ErrorAsync(ctx, 403, "denied", ex.Message);
        }
    }

    private static async Task ErrorAsync(HttpContext ctx, int estado, string tipo, string mensaje)
    {
        if (ctx.Response.HasStarted)
            return;
        ctx.Response.StatusCode = estado;
        await ctx.Response.WriteAsJsonAsync(new { kind = tipo, message = mensaje });
    }

    // Como Protocol de Control Print, escrito aparte del cliente.
    private static string Normalizar(string codigo) => new(codigo.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Prueba(string codigo, string reto, string huella) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Normalizar(codigo)), Encoding.UTF8.GetBytes($"{reto}|{huella}")));

    /// <summary>Cuando cada quien ve lo suyo, sin sesión se ve lo del invitado (el POS nunca inicia sesión).</summary>
    private long? SoloDe() => Visibilidad == "OwnOnly" ? DatosDeControlPrint.Invitado : null;

    private JobRecordCp Visible(long id)
    {
        var trabajo = Trabajos.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("El trabajo ya no existe.");
        if (SoloDe() is { } dueno && trabajo.OwnerId != dueno)
            throw new UnauthorizedAccessException("Ese trabajo lo atendió otro usuario.");
        return trabajo;
    }

    private static (DateTime, DateTime) DiaUtc(DateOnly dia) =>
        (HoraMexico.AUtc(dia.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private void Mapear(WebApplication app)
    {
        app.MapGet("/api/hello", (string nonce) => new
        {
            name = Nombre,
            appVersion = Version,
            protocolVersion = VersionProtocolo,
            proof = Prueba(NoConoceElCodigo ? "ZZZZ-ZZZZ-ZZZZ-ZZZZ" : Codigo, nonce, Huella),
        });

        app.MapGet("/api/changes", async (long jobs, long settings, long users, long status, int? wait, CancellationToken ct) =>
        {
            var limite = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 25));
            while (true)
            {
                var senal = Volatile.Read(ref _cambio).Task;
                var v = new { jobs = Interlocked.Read(ref _versiones[0]), settings = Interlocked.Read(ref _versiones[1]),
                    users = Interlocked.Read(ref _versiones[2]), status = Interlocked.Read(ref _versiones[3]), appVersion = Version };
                if (v.jobs != jobs || v.settings != settings || v.users != users || v.status != status)
                    return v;
                var falta = limite - DateTime.UtcNow;
                if (falta <= TimeSpan.Zero)
                    return v;
                await Task.WhenAny(senal, Task.Delay(falta, ct));
                ct.ThrowIfCancellationRequested();
            }
        });

        app.MapPost("/api/jobs/search", (JobQueryCp q) =>
        {
            Busquedas.Enqueue(q);
            lock (_datos)
            {
                IEnumerable<JobRecordCp> filtro = Trabajos;
                if (q.FromUtc is { } desde)
                    filtro = filtro.Where(t => t.DetectedUtc >= desde.ToUniversalTime());
                if (q.ToUtc is { } hasta)
                    filtro = filtro.Where(t => t.DetectedUtc < hasta.ToUniversalTime());
                if (q.OnlyChargeable)
                    filtro = filtro.Where(t => t.IsChargeable);
                if (q.Payment is { } cobro)
                    filtro = filtro.Where(t => t.Payment == cobro);
                if (q.OnlySpecial)
                    filtro = filtro.Where(t => t.PriceKind == PriceKind.Special);
                if ((SoloDe() ?? q.OwnerId) is { } dueno)
                    filtro = filtro.Where(t => t.OwnerId == dueno);
                if (q.Text?.Trim() is { Length: > 0 } texto)
                {
                    // Como JobRepository.Search de Control Print: «#123» o «123» también buscan por número.
                    var numero = long.TryParse(texto.TrimStart('#'), out var id) ? id : (long?)null;
                    filtro = filtro.Where(t => t.Id == numero || t.Document.Contains(texto, StringComparison.OrdinalIgnoreCase)
                        || (t.Label?.Contains(texto, StringComparison.OrdinalIgnoreCase) ?? false)
                        || t.SourcePc.Contains(texto, StringComparison.OrdinalIgnoreCase));
                }
                var todos = filtro.OrderByDescending(t => t.Id).ToList();
                var pagina = todos.Skip(Math.Max(0, q.Offset));
                if (q.Limit > 0)
                    pagina = pagina.Take(q.Limit);
                return ComoControlPrint(new { jobs = pagina.Select(t => t.SinMiniatura()).ToList(), total = todos.Count });
            }
        });
        app.MapGet("/api/jobs/{id:long}", (long id) => { lock (_datos) return ComoControlPrint(Visible(id)); });
        app.MapGet("/api/jobs/{id:long}/thumbnail", (long id) =>
        {
            lock (_datos)
                return Visible(id).Thumbnail is { } png ? Results.Bytes(png, "application/octet-stream") : Results.NoContent();
        });
        app.MapGet("/api/jobs/{id:long}/quotes", (long id) =>
        {
            lock (_datos)
            {
                Visible(id);
                return Cotizaciones.Where(c => c.JobId == id).ToList();
            }
        });
        app.MapGet("/api/summary", (string day, long? owner) =>
        {
            lock (_datos)
            {
                var (desde, hasta) = DiaUtc(DateOnly.ParseExact(day, "yyyy-MM-dd"));
                var dueno = SoloDe() ?? owner;
                var del = Trabajos.Where(t => t.DetectedUtc >= desde && t.DetectedUtc < hasta && t.IsChargeable && (dueno is null || t.OwnerId == dueno)).ToList();
                var vendido = del.Where(t => t.Payment != PaymentState.Unsold).Sum(t => t.EffectivePrice);
                var cobrado = del.Where(t => t.Payment == PaymentState.Paid).Sum(t => t.EffectivePrice);
                return new
                {
                    jobs = del.Count,
                    sides = del.Sum(t => t.CountedSides ?? t.Sides),
                    cost = del.Sum(t => t.CostTotal),
                    price = vendido,
                    collected = cobrado,
                    unsold = del.Count(t => t.Payment == PaymentState.Unsold),
                    unsoldCost = del.Where(t => t.Payment == PaymentState.Unsold).Sum(t => t.CostTotal),
                    pending = vendido - cobrado,
                };
            }
        });
        app.MapGet("/api/report", () => { throw new UnauthorizedAccessException("Solo el administrador puede hacerlo."); });
        app.MapGet("/api/jobs/sides", (DateTime from, DateTime to) =>
        {
            lock (_datos)
            {
                var del = Trabajos.Where(t => t.DetectedUtc >= from.ToUniversalTime() && t.DetectedUtc < to.ToUniversalTime() && t.IsChargeable).ToList();
                var impresas = del.Where(t => t.Kind == JobKind.Print).ToList();
                var copias = del.Where(t => t.Kind == JobKind.PanelCopy).ToList();
                return new
                {
                    printSides = impresas.Sum(t => t.CountedSides ?? t.Sides),
                    printColorSides = impresas.Sum(t => Math.Min(t.ColorSides, t.CountedSides ?? t.Sides)),
                    copySides = copias.Sum(t => t.Sides),
                    copyColorSides = copias.Sum(t => t.ColorSides),
                };
            }
        });

        app.MapGet("/api/quotes", (DateTime since, string? text, int? limit) =>
        {
            lock (_datos)
                return Cotizaciones
                    .Where(c => c.CreatedUtc >= since.ToUniversalTime() && (SoloDe() is not { } dueno || c.OwnerId == dueno)
                                && (string.IsNullOrWhiteSpace(text) || c.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(c => c.Id).Take(Math.Max(1, limit ?? 200)).ToList();
        });
        app.MapGet("/api/quotes/{id:long}", (long id) =>
        {
            lock (_datos)
                return Cotizaciones.FirstOrDefault(c => c.Id == id) is { } cotizacion ? Results.Ok(cotizacion) : Results.NotFound();
        });

        // Los usuarios, con lo que una versión nueva podría mandar de más: el cliente no debe guardarlo nunca.
        app.MapGet("/api/users/any", () => true);
        app.MapGet("/api/users", (bool? all) => Results.Text(UsuariosJson(all == true), "application/json", Encoding.UTF8));

        app.MapGet("/api/settings/{key}", (string key) => key switch
        {
            "cost" => Results.Text(CostosJson, "application/json", Encoding.UTF8),
            "app" => Results.Text(DatosDeControlPrint.Ajustes(Visibilidad), "application/json", Encoding.UTF8),
            _ => throw new KeyNotFoundException("Configuración desconocida."),
        });

        app.MapGet("/api/status/latest", () =>
        {
            lock (_datos)
                return Lecturas.OrderBy(l => l.TakenUtc).LastOrDefault() is { } ultima ? Results.Ok(ultima) : Results.NoContent();
        });
        app.MapGet("/api/status", (DateTime? since) =>
        {
            lock (_datos)
                return Lecturas.Where(l => since is null || l.TakenUtc >= since.Value.ToUniversalTime()).OrderBy(l => l.TakenUtc).ToList();
        });
        app.MapGet("/api/calibration", () => CalibracionJson is { } json ? Results.Text(json, "application/json", Encoding.UTF8) : Results.NoContent());
        app.MapGet("/api/health", () => new { printerText = "Lista · tinta negra 40 %", printerOk = true, monitorText = "", monitorOk = true });
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Como lo serializa la API (camelCase, enum como texto), con el estado que no se conoce donde lo haya.</summary>
    private IResult ComoControlPrint(object respuesta)
    {
        var json = JsonSerializer.SerializeToNode(respuesta, Json)!;
        foreach (var trabajo in json is JsonObject { } objeto && objeto["jobs"] is JsonArray trabajos ? trabajos.ToList() : [json])
            if (trabajo?["id"]?.GetValue<long>() is { } id && Trabajos.FirstOrDefault(t => t.Id == id)?.EstadoQueNoConoce is { } estado)
                trabajo["state"] = estado;
        return Results.Text(json.ToJsonString(), "application/json", Encoding.UTF8);
    }

    private static string UsuariosJson(bool todos)
    {
        var usuarios = new JsonArray();
        foreach (var (id, nombre, rol, activo) in new[]
                 {
                     (DatosDeControlPrint.Dueno, "Dueño de prueba", "Admin", true),
                     (DatosDeControlPrint.Cajera, "Cajera de prueba", "Employee", true),
                     (DatosDeControlPrint.Invitado, "Sin sesión", "Guest", true),
                     (4L, "Cajero que se fue", "Employee", false),
                 })
        {
            if (!activo && !todos)
                continue;
            usuarios.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = nombre,
                ["role"] = rol,
                ["active"] = activo,
                ["createdUtc"] = "2026-01-10T15:00:00Z",
                ["lastLoginUtc"] = "2026-10-24T22:00:00Z",
                ["isAdmin"] = rol == "Admin",
                ["isGuest"] = rol == "Guest",
                ["roleText"] = rol,
                ["passwordHash"] = $"pbkdf2-sha256$100000$SAL-DE-PRUEBA-{id}$HASH-SECRETO-{id}",
                ["sessionToken"] = $"TOKEN-SECRETO-{id}",
            });
        }
        return usuarios.ToJsonString();
    }

    private static X509Certificate2 CrearCertificado(string nombre)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest($"CN={nombre}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        solicitud.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        solicitud.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        solicitud.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var nombres = new SubjectAlternativeNameBuilder();
        nombres.AddIpAddress(IPAddress.Loopback);
        nombres.AddDnsName("localhost");
        solicitud.CertificateExtensions.Add(nombres.Build());
        using var certificado = solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        // Como Control Print: reimportado para que Kestrel pueda usar la llave también en Windows.
        return X509CertificateLoader.LoadPkcs12(certificado.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _app, null) is { } app)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        // Una escritura hace fallar la prueba, aunque la prueba no lo haya revisado.
        if (!PermitirEscrituras && Escrituras is { Count: > 0 } escrituras)
            throw new Xunit.Sdk.XunitException(
                $"El POS le escribió a Control Print: {string.Join(", ", escrituras.Select(e => $"{e.Metodo} {e.Ruta}"))}. Solo debe leer.");
    }
}

/// <summary>Como el JobQuery de Control Print.</summary>
public sealed record JobQueryCp
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public PaymentState? Payment { get; init; }
    public bool OnlyChargeable { get; init; }
    public bool OnlySpecial { get; init; }
    public long? OwnerId { get; init; }
    public string? Text { get; init; }
    public int Limit { get; init; } = 200;
    public int Offset { get; init; }
}
