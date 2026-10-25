using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El registro del servicio en archivos de texto, uno por día (el piloto alfa, 2026-10-25: «recopilar datos en un entorno
/// real»): <c>servicio-AAAA-MM-DD.log</c> en «registros», junto a la base (<c>C:\ProgramData\PapeleriaPos\registros</c>; en
/// desarrollo, junto a la base que se use), con <see cref="DiasPorOmision"/> días de historia. Antes el servicio solo escribía
/// avisos y errores en el Visor de eventos de Windows: lo que pasaba con eleventa (cada envío, cada lectura, el respaldo del
/// día) no quedaba en ningún lado, y el error de un envío se borraba en cuanto entraba.
/// <para>
/// Lo propio del POS (las categorías <c>PapeleriaPos.*</c>: el puente, la lectura, los turnos, los respaldos, la contingencia)
/// va desde «Información»; lo de Microsoft y lo demás, desde «Aviso» (las reglas, en <see cref="Agregar"/>). Nunca un secreto:
/// la clave de eleventa, el token de Telegram, un PIN o la contraseña de una cadena de conexión se tachan antes de escribir
/// (<see cref="Tachar"/>), aunque lleguen dentro del mensaje de un error.
/// </para>
/// <para>
/// Ligero para la caja 2 (una PC con pocos recursos): quien registra solo arma su línea y la deja en una fila, sin esperar al
/// disco; un solo hilo escribe lo que se juntó cada poco, de una vez (abre, escribe y cierra: el archivo se puede leer y copiar
/// con el servicio en marcha). Si algo escribe sin parar y la fila se llena, lo que no cabe se cuenta y se dice. Pasado el tope
/// del día, solo los errores. Nunca truena: un registro que no se puede escribir no detiene al servicio.
/// </para>
/// </summary>
[ProviderAlias("Archivo")]
public sealed partial class RegistroDelServicio : ILoggerProvider
{
    /// <summary>Un mes: el piloto se revisa al final de cada fin de semana, y con margen si una semana no se pudo.</summary>
    public const int DiasPorOmision = 30;

    /// <summary>
    /// Lo más que crece el archivo de un día: unas 20 mil líneas, más de diez veces un día normal del piloto (cada venta, cada
    /// envío, una lectura cada 5 minutos). Lo que se repita sin parar no llena el disco: pasado esto, solo los errores.
    /// </summary>
    public const long TopePorOmision = 8 * 1024 * 1024;

    /// <summary>Lo que se junta antes de escribir: unas cuantas líneas de una vez, sin que se note al leerlo.</summary>
    public static readonly TimeSpan JuntarPorOmision = TimeSpan.FromMilliseconds(250);

    /// <summary>Lo más largo de una entrada (un error con todo su detalle cabe de sobra).</summary>
    private const int LargoMaximo = 16 * 1024;

    /// <summary>Lo más corto que se tacha por su valor: uno más corto se confundiría con cualquier palabra o número.</summary>
    public const int LargoMinimoDeUnSecreto = 4;

    public const string Nombre = "servicio";

    internal const string Empieza = "Empieza el registro del servicio";
    internal const string Termina = "Termina el registro del servicio";

    private readonly Channel<Entrada> _fila;
    private readonly string[] _secretos;
    private readonly TimeProvider _reloj;
    private readonly int _dias;
    private readonly long _tope;
    private readonly TimeSpan _juntar;
    private readonly CancellationTokenSource _ya = new();
    private readonly Task _escribiendo;
    private int _perdidas;
    private int _desechado;

    // Del hilo que escribe.
    private DateOnly? _dia;
    private DateOnly? _topeAvisadoEl;
    private string? _falloAlEscribir;
    private int _sinEscribir;

    /// <param name="carpeta">Donde van los archivos (<see cref="CarpetaDeRegistros"/>).</param>
    /// <param name="secretos">Lo que nunca se escribe tal cual (la clave de eleventa, el token de Telegram): se tacha.</param>
    /// <param name="capacidad">Cuántas entradas esperan en la fila; las que no caben se cuentan y se dicen.</param>
    public RegistroDelServicio(string carpeta, IEnumerable<string?> secretos, TimeProvider? reloj = null, int dias = DiasPorOmision,
        long tope = TopePorOmision, TimeSpan? juntar = null, int capacidad = 20_000)
    {
        Carpeta = carpeta;
        _secretos = secretos.Where(s => s is { Length: >= LargoMinimoDeUnSecreto }).Select(s => s!).Distinct(StringComparer.Ordinal)
            // El más largo primero: si uno contiene a otro, se tacha completo.
            .OrderByDescending(s => s.Length).ToArray();
        _reloj = reloj ?? TimeProvider.System;
        _dias = Math.Max(1, dias);
        _tope = tope;
        _juntar = juntar ?? JuntarPorOmision;
        _fila = Channel.CreateBounded<Entrada>(new BoundedChannelOptions(Math.Max(16, capacidad))
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false,
        }, _ => Interlocked.Increment(ref _perdidas));
        Encolar(LogLevel.Information, typeof(RegistroDelServicio).FullName!, $"{Empieza}: Papelería POS {Version} (proceso {Environment.ProcessId}, "
            + $"en {Environment.MachineName}); se guardan {_dias} días en {carpeta}.");
        _escribiendo = Task.Run(EscribirAsync);
        // Si el proceso termina sin que se deseche el contenedor (restaurar un respaldo sale con 1 para que Windows lo vuelva a
        // arrancar), lo que quedaba en la fila también se escribe.
        AppDomain.CurrentDomain.ProcessExit += AlSalirDelProceso;
    }

    private void AlSalirDelProceso(object? sender, EventArgs e) => Dispose();

    public string Carpeta { get; }

    /// <summary>El archivo de un día.</summary>
    public string ArchivoDe(DateOnly dia) => Path.Combine(Carpeta, $"{Nombre}-{dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    /// <summary>El archivo de hoy (hora de la PC).</summary>
    public string ArchivoDeHoy => ArchivoDe(DateOnly.FromDateTime(_reloj.GetLocalNow().DateTime));

    private static string Version =>
        typeof(RegistroDelServicio).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    // ── La configuración: dónde, cuántos días y qué se tacha ──

    /// <summary>
    /// «registros», junto a la base (<c>C:\ProgramData\PapeleriaPos\registros</c>), como la bitácora de red; <c>Registro:Carpeta</c>
    /// la cambia.
    /// </summary>
    public static string CarpetaDeRegistros(IConfiguration configuracion, string cadenaConexion) =>
        configuracion["Registro:Carpeta"] is { Length: > 0 } carpeta
            ? carpeta
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cadenaConexion).DataSource)) ?? ".",
                "registros");

    /// <summary>
    /// Si el servicio escribe su registro: sí, salvo que se apague (<c>Registro:Activo</c>, las pruebas) o sea el de la prueba con
    /// una persona real, cuya caja ya guarda lo que escribe el servicio en <c>registros\servicio-….log</c> (el mismo nombre, en la
    /// misma carpeta: serían dos que escriben el mismo archivo).
    /// </summary>
    public static bool Activo(IConfiguration configuracion) =>
        configuracion.GetValue("Registro:Activo", true) && !configuracion.GetValue("Prueba:Activa", false)
        && !configuracion.GetValue("Prueba:DetenerAlCerrarEntrada", false);

    /// <summary>Lo que se tacha por su valor: la clave de eleventa (también si llega por la variable de entorno) y el token de Telegram.</summary>
    public static IEnumerable<string?> Secretos(IConfiguration configuracion)
    {
        yield return configuracion["Eleventa:Clave"];
        string? token;
        try
        {
            token = TelegramBot.LeerToken(configuracion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            token = null;
        }
        yield return token;
    }

    /// <summary>
    /// El registro en archivos, con sus reglas: desde «Información» lo del POS (<c>PapeleriaPos.*</c>) y desde «Aviso» lo demás (lo
    /// de Microsoft, como el registro de cada petición de ASP.NET, no entra). Nada si está apagado (<see cref="Activo"/>). Lo crea
    /// el contenedor, que lo desecha al detenerse el servicio: así se escribe lo último antes de salir.
    /// </summary>
    public static void Agregar(WebApplicationBuilder builder, string cadenaConexion)
    {
        if (!Activo(builder.Configuration))
            return;
        var carpeta = CarpetaDeRegistros(builder.Configuration, cadenaConexion);
        var dias = builder.Configuration.GetValue("Registro:Dias", DiasPorOmision);
        builder.Services.AddSingleton<ILoggerProvider>(proveedor => new RegistroDelServicio(carpeta,
            Secretos(proveedor.GetRequiredService<IConfiguration>()), proveedor.GetService<TimeProvider>(), dias));
        builder.Logging.AddFilter<RegistroDelServicio>(null, LogLevel.Warning);
        builder.Logging.AddFilter<RegistroDelServicio>("PapeleriaPos", LogLevel.Information);
    }

    // ── Lo que se tacha ──

    /// <summary>
    /// El texto sin secretos: cada uno de los configurados, donde aparezca, y lo que va después de «clave=», «password=», «pin:»,
    /// «"token": "…"» y parecidos (una cadena de conexión, un JSON), y lo que tiene la forma de un token de Telegram. Lo tachado
    /// queda como «***».
    /// </summary>
    public string Tachar(string texto)
    {
        foreach (var secreto in _secretos)
        {
            if (texto.Contains(secreto, StringComparison.Ordinal))
                texto = texto.Replace(secreto, Tachado, StringComparison.Ordinal);
        }
        texto = ClaveYValor().Replace(texto, m => m.Groups["clave"].Value + Tachado);
        return TokenDeTelegram().Replace(texto, Tachado);
    }

    public const string Tachado = "***";

    [GeneratedRegex("""(?<clave>(?<![\p{L}\p{N}_])["']?(?:password|pwd|clave|contrase(?:ñ|n)a|isc_password|token|pin|secret|secreto|llave\p{L}*)["']?\s*[=:]\s*)(?<valor>"[^"]*"|'[^']*'|[^\s;,&"'}\]]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaveYValor();

    [GeneratedRegex(@"(?<![\p{N}])\d{6,12}:[A-Za-z0-9_-]{30,}")]
    private static partial Regex TokenDeTelegram();

    // ── Registrar ──

    public ILogger CreateLogger(string categoryName) => new Registrador(this, categoryName);

    private void Encolar(LogLevel nivel, string categoria, string texto) =>
        _fila.Writer.TryWrite(new Entrada(_reloj.GetLocalNow(), nivel, categoria, texto, null));

    /// <summary>
    /// Para las pruebas (y para quien necesite leer el archivo enseguida): termina cuando ya se escribió todo lo que se había
    /// registrado hasta ahora.
    /// </summary>
    public Task AlDiaAsync()
    {
        var listo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_fila.Writer.TryWrite(new Entrada(_reloj.GetLocalNow(), LogLevel.None, "", "", listo)))
            listo.TrySetResult();
        return listo.Task;
    }

    private sealed class Registrador(RegistroDelServicio registro, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None || Volatile.Read(ref registro._desechado) == 1)
                return;
            string texto;
            try
            {
                texto = formatter(state, exception);
                if (exception is not null)
                    texto = texto.Length > 0 ? $"{texto}{Environment.NewLine}{exception}" : exception.ToString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Uno que ni se deja armar: al menos qué era, sin causar otro error dentro del registro.
                texto = $"(no se pudo armar la línea: {ex.GetType().Name}) {exception?.GetType().FullName}";
            }
            registro.Encolar(logLevel, categoria, texto);
        }
    }

    private readonly record struct Entrada(DateTimeOffset Cuando, LogLevel Nivel, string Categoria, string Texto, TaskCompletionSource? AlDia);

    // ── Escribir ──

    private async Task EscribirAsync()
    {
        var lector = _fila.Reader;
        var tanda = new List<Entrada>();
        try
        {
            while (await lector.WaitToReadAsync().ConfigureAwait(false))
            {
                // Lo que llegue en un momento va junto, en una sola escritura (al detenerse, ya sin esperar).
                if (!_ya.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_juntar, _ya.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Se está deteniendo: lo que hay se escribe ya.
                    }
                }
                while (lector.TryRead(out var entrada))
                    tanda.Add(entrada);
                Escribir(tanda);
                foreach (var listo in tanda.Where(e => e.AlDia is not null))
                    listo.AlDia!.TrySetResult();
                tanda.Clear();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // El registro nunca detiene al servicio: si algo salió mal aquí, lo que siga ya no se escribe.
            foreach (var listo in tanda.Where(e => e.AlDia is not null))
                listo.AlDia!.TrySetResult();
        }
    }

    private void Escribir(List<Entrada> tanda)
    {
        var perdidas = Interlocked.Exchange(ref _perdidas, 0);
        var entradas = tanda.Where(e => e.Nivel != LogLevel.None).ToList();
        if (perdidas > 0)
        {
            var cuando = entradas.Count > 0 ? entradas[0].Cuando : _reloj.GetLocalNow();
            entradas.Insert(0, new Entrada(cuando, LogLevel.Warning, typeof(RegistroDelServicio).FullName!,
                $"Llegaron demasiadas líneas a la vez: {perdidas} no cupieron en la fila y no se anotaron.", null));
        }
        foreach (var delDia in entradas.GroupBy(e => DateOnly.FromDateTime(e.Cuando.DateTime)))
            EscribirDelDia(delDia.Key, delDia.ToList());
    }

    private void EscribirDelDia(DateOnly dia, List<Entrada> entradas)
    {
        if (_dia != dia)
        {
            // Otro día (o el primero): se quitan los de hace más días. El servicio corre días sin detenerse.
            _dia = dia;
            Limpiar(dia);
        }
        var ruta = ArchivoDe(dia);
        try
        {
            Directory.CreateDirectory(Carpeta);
            var ocupado = File.Exists(ruta) ? new FileInfo(ruta).Length : 0;
            var texto = new StringBuilder();
            if (_falloAlEscribir is { } fallo)
            {
                texto.Append(Linea(entradas[0].Cuando, LogLevel.Warning, typeof(RegistroDelServicio).FullName!,
                    $"No se pudo escribir el registro un rato ({_sinEscribir} líneas se perdieron): {fallo}"));
                (_falloAlEscribir, _sinEscribir) = (null, 0);
            }
            foreach (var entrada in entradas)
            {
                if (ocupado + texto.Length >= _tope && entrada.Nivel < LogLevel.Error)
                {
                    if (_topeAvisadoEl != dia)
                    {
                        _topeAvisadoEl = dia;
                        texto.Append(Linea(entrada.Cuando, LogLevel.Warning, typeof(RegistroDelServicio).FullName!,
                            $"El registro de hoy llegó a su tope ({_tope / (1024 * 1024)} MB): lo que sigue ya no se anota, salvo los errores."));
                    }
                    continue;
                }
                texto.Append(Linea(entrada.Cuando, entrada.Nivel, entrada.Categoria, Tachar(Recortar(entrada.Texto))));
            }
            if (texto.Length == 0)
                return;
            var bytes = Encoding.UTF8.GetBytes(texto.ToString());
            // Se abre y se cierra en cada tanda: se puede leer (o copiar) con el servicio en marcha.
            using var flujo = new FileStream(ruta, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            flujo.Write(bytes);
        }
        catch (Exception ex) when (EsDelDisco(ex))
        {
            // Sin disco (lleno, sin permiso) el servicio sigue igual; se dice en cuanto se pueda volver a escribir.
            _falloAlEscribir = ex.Message;
            _sinEscribir += entradas.Count;
        }
    }

    private static string Recortar(string texto) => texto.Length > LargoMaximo ? $"{texto[..LargoMaximo]}… (recortado)" : texto;

    /// <summary>«2026-10-31 14:03:12.345  Info   EnvioVentasEleventa  Envío a eleventa · …»: lo del POS, con el nombre corto.</summary>
    private static string Linea(DateTimeOffset cuando, LogLevel nivel, string categoria, string texto)
    {
        var corta = categoria.StartsWith("PapeleriaPos.", StringComparison.Ordinal) && categoria.LastIndexOf('.') is var punto and > 0
            ? categoria[(punto + 1)..]
            : categoria;
        return $"{cuando.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {NombreDelNivel(nivel),-5}  {corta}  {texto}{Environment.NewLine}";
    }

    public static string NombreDelNivel(LogLevel nivel) => nivel switch
    {
        LogLevel.Trace => "Traza",
        LogLevel.Debug => "Depur",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Aviso",
        LogLevel.Error => "Error",
        _ => "Grave",
    };

    /// <summary>Quita los archivos de este registro de hace más de los días que se guardan. Los demás archivos de la carpeta no los toca.</summary>
    private void Limpiar(DateOnly hoy)
    {
        try
        {
            if (!Directory.Exists(Carpeta))
                return;
            var limite = hoy.AddDays(-_dias);
            foreach (var archivo in Directory.EnumerateFiles(Carpeta, $"{Nombre}-????-??-??.log").ToList())
            {
                var fecha = Path.GetFileNameWithoutExtension(archivo)[(Nombre.Length + 1)..];
                if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia) || dia > limite)
                    continue;
                try
                {
                    File.Delete(archivo);
                }
                catch (Exception ex) when (EsDelDisco(ex))
                {
                    // Abierto en otro lado (alguien lo está leyendo): se quita al cambiar de día o la siguiente vez que arranque.
                }
            }
        }
        catch (Exception ex) when (EsDelDisco(ex))
        {
            // Sin poder leer la carpeta, se quitan la siguiente vez.
        }
    }

    private static bool EsDelDisco(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException;

    /// <summary>Al detenerse el servicio: lo que queda en la fila se escribe (hasta 5 s) y se dice que termina.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _desechado, 1) == 1)
            return;
        AppDomain.CurrentDomain.ProcessExit -= AlSalirDelProceso;
        _fila.Writer.TryWrite(new Entrada(_reloj.GetLocalNow(), LogLevel.Information, typeof(RegistroDelServicio).FullName!, $"{Termina}.", null));
        _fila.Writer.TryComplete();
        _ya.Cancel();
        try
        {
            _escribiendo.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ya se dijo lo que se pudo.
        }
        _ya.Dispose();
    }
}
