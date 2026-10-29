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
/// el valor de cada ajuste que por su nombre lo es (la clave de eleventa, el código de Control Print, el token de Telegram: <see
/// cref="LoQueEsSecreto"/>), un PIN o la contraseña de una cadena de conexión se tachan antes de escribir (<see cref="Tachar"/>),
/// aunque lleguen dentro del mensaje de un error.
/// </para>
/// <para>
/// Ligero para la caja 2 (una PC con pocos recursos): quien registra solo arma su línea y la deja en una fila, sin esperar al
/// disco; un solo hilo escribe lo que se juntó cada poco, de una vez (abre, escribe y cierra: el archivo se puede leer y copiar
/// con el servicio en marcha). Si algo escribe sin parar y la fila se llena, lo que no cabe se cuenta y se dice. Pasado el tope
/// del día, solo los errores, y pasado el doble, nada. Si el archivo está tomado un momento, lo que tocaba se vuelve a intentar.
/// Nunca truena: un registro que no se puede escribir no detiene al servicio.
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

    /// <summary>Lo más que espera en memoria lo que no se pudo escribir (el archivo tomado); de ahí, lo más viejo se pierde y se dice.</summary>
    private const int MaximoSinEscribir = 2 * 1024 * 1024;

    /// <summary>Cada cuánto se vuelve a intentar lo que no se pudo escribir, aunque no llegue nada nuevo.</summary>
    private static readonly TimeSpan Reintento = TimeSpan.FromSeconds(2);

    /// <summary>Las líneas del propio registro (cuándo empieza y termina, sus avisos): esas pasan el tope del día, no el doble.</summary>
    private static readonly string Propia = typeof(RegistroDelServicio).FullName!;

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
    private DateOnly? _topeDobleAvisadoEl;
    private readonly List<Tramo> _sinEscribir = [];
    private string? _falloAlEscribir;
    private DateTimeOffset? _falloDesde;
    private int _perdidasPorElDisco;

    /// <param name="carpeta">Donde van los archivos (<see cref="CarpetaDeRegistros"/>).</param>
    /// <param name="secretos">Lo que nunca se escribe tal cual (la clave de eleventa, el token de Telegram): se tacha.</param>
    /// <param name="capacidad">Cuántas entradas esperan en la fila; las que no caben se cuentan y se dicen.</param>
    public RegistroDelServicio(string carpeta, IEnumerable<string?> secretos, TimeProvider? reloj = null, int dias = DiasPorOmision,
        long tope = TopePorOmision, TimeSpan? juntar = null, int capacidad = 20_000)
    {
        Carpeta = carpeta;
        _secretos = secretos.Where(s => s is { Length: >= LoQueEsSecreto.LargoMinimo }).Select(s => s!).Distinct(StringComparer.Ordinal)
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
        Encolar(LogLevel.Information, Propia, $"{Empieza}: Papelería POS {Version} (proceso {Environment.ProcessId}, "
            + $"en {Environment.MachineName}); se guardan {_dias} días en {carpeta}.");
        _escribiendo = Task.Run(EscribirAsync);
        // Si el proceso termina sin que se deseche el contenedor (restaurar un respaldo sale con 1 para que Windows lo vuelva a
        // arrancar), lo que quedaba en la fila también se escribe; y si termina por un error que nadie atendió, también ese error.
        AppDomain.CurrentDomain.ProcessExit += AlSalirDelProceso;
        AppDomain.CurrentDomain.UnhandledException += AlTronar;
    }

    private void AlSalirDelProceso(object? sender, EventArgs e) => Dispose();

    /// <summary>
    /// Un error que nadie atendió cierra el proceso sin desechar el contenedor y sin <see cref="AppDomain.ProcessExit"/>: el
    /// arranque que truena antes de atender (la base dañada, una restauración a medias) perdía justo la línea que dice por qué
    /// (revisión del PR #145). Se escribe lo que había en la fila, y el error.
    /// </summary>
    internal void AlTronar(object? sender, UnhandledExceptionEventArgs e)
    {
        if (Volatile.Read(ref _desechado) == 0)
            Encolar(LogLevel.Critical, Propia, $"El servicio se cerró por un error que nadie atendió: {e.ExceptionObject}");
        Dispose();
    }

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

    /// <summary>
    /// Lo que se tacha por su valor: cada ajuste de la configuración que por su nombre es un secreto, venga de servicio.json o de una
    /// variable de entorno del servicio («Eleventa__Clave», «ControlPrint__Codigo»: <see cref="LoQueEsSecreto.ValoresDe"/>), y el
    /// token de Telegram aunque esté en su archivo.
    /// </summary>
    public static IEnumerable<string?> Secretos(IConfiguration configuracion)
    {
        foreach (var valor in LoQueEsSecreto.ValoresDe(configuracion))
            yield return valor;
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
    /// El texto sin secretos: cada uno de los configurados, donde aparezca, y el valor de lo que por su nombre es un secreto
    /// («clave=», «userpassword=», «"pinNuevo": "…"», «ISC_PASSWORD=»: <see cref="LoQueEsSecreto.TacharEnUnTexto"/>), y lo que tiene
    /// la forma de un token de Telegram. Lo tachado queda como «***».
    /// </summary>
    public string Tachar(string texto)
    {
        foreach (var secreto in _secretos)
        {
            if (texto.Contains(secreto, StringComparison.Ordinal))
                texto = texto.Replace(secreto, Tachado, StringComparison.Ordinal);
        }
        texto = LoQueEsSecreto.TacharEnUnTexto(texto, Tachado);
        return TokenDeTelegram().Replace(texto, Tachado);
    }

    public const string Tachado = "***";

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

    /// <summary>Las líneas ya armadas de un día que todavía no se escriben (el archivo estaba tomado), en orden.</summary>
    private sealed record Tramo(DateOnly Dia, string Texto, int Lineas);

    // ── Escribir ──

    private async Task EscribirAsync()
    {
        var lector = _fila.Reader;
        var tanda = new List<Entrada>();
        try
        {
            while (await HayQueEscribirAsync(lector).ConfigureAwait(false))
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
            // Al detenerse, lo que no se pudo escribir tiene otra oportunidad.
            Vaciar();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // El registro nunca detiene al servicio: si algo salió mal aquí, lo que siga ya no se escribe.
            foreach (var listo in tanda.Where(e => e.AlDia is not null))
                listo.AlDia!.TrySetResult();
        }
    }

    /// <summary>
    /// Cuando llega algo; con algo que no se pudo escribir, a lo más en <see cref="Reintento"/>, para volver a intentarlo aunque
    /// no llegue nada. Falso al detenerse.
    /// </summary>
    private async Task<bool> HayQueEscribirAsync(ChannelReader<Entrada> lector)
    {
        if (_sinEscribir.Count == 0)
            return await lector.WaitToReadAsync().ConfigureAwait(false);
        using var espera = new CancellationTokenSource(Reintento);
        try
        {
            return await lector.WaitToReadAsync(espera.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private void Escribir(List<Entrada> tanda)
    {
        var perdidas = Interlocked.Exchange(ref _perdidas, 0);
        var entradas = tanda.Where(e => e.Nivel != LogLevel.None).ToList();
        if (perdidas > 0)
        {
            var cuando = entradas.Count > 0 ? entradas[0].Cuando : _reloj.GetLocalNow();
            entradas.Insert(0, new Entrada(cuando, LogLevel.Warning, Propia,
                $"Llegaron demasiadas líneas a la vez: {perdidas} no cupieron en la fila y no se anotaron.", null));
        }
        foreach (var delDia in entradas.GroupBy(e => DateOnly.FromDateTime(e.Cuando.DateTime)))
        {
            if (Armar(delDia.Key, delDia.ToList()) is { } tramo)
                _sinEscribir.Add(tramo);
        }
        Vaciar();
    }

    /// <summary>
    /// Las líneas de un día, sin secretos y con el tope: pasado <see cref="_tope"/>, solo los errores (y las del propio registro);
    /// pasado el doble, nada más ese día (revisión del PR #145: un error que se repite cada 30 s con todo su detalle tampoco llena
    /// el disco).
    /// </summary>
    private Tramo? Armar(DateOnly dia, List<Entrada> entradas)
    {
        if (_dia != dia)
        {
            // Otro día (o el primero): se quitan los de hace más días. El servicio corre días sin detenerse.
            _dia = dia;
            Limpiar(dia);
        }
        long ocupado;
        try
        {
            var ruta = ArchivoDe(dia);
            ocupado = File.Exists(ruta) ? new FileInfo(ruta).Length : 0;
        }
        catch (Exception ex) when (EsDelDisco(ex))
        {
            ocupado = 0;
        }
        ocupado += _sinEscribir.Where(t => t.Dia == dia).Sum(t => (long)t.Texto.Length);
        var texto = new StringBuilder();
        var lineas = 0;
        foreach (var entrada in entradas)
        {
            var lleva = ocupado + texto.Length;
            if (lleva >= 2 * _tope)
            {
                if (_topeDobleAvisadoEl != dia)
                {
                    _topeDobleAvisadoEl = dia;
                    texto.Append(Linea(entrada.Cuando, LogLevel.Warning, Propia,
                        $"El registro de hoy llegó al doble de su tope ({Medida(2 * _tope)}): hasta mañana ya no se anota nada, ni los errores."));
                    lineas++;
                }
                continue;
            }
            if (lleva >= _tope && entrada.Nivel < LogLevel.Error && entrada.Categoria != Propia)
            {
                if (_topeAvisadoEl != dia)
                {
                    _topeAvisadoEl = dia;
                    texto.Append(Linea(entrada.Cuando, LogLevel.Warning, Propia,
                        $"El registro de hoy llegó a su tope ({Medida(_tope)}): lo que sigue ya no se anota, salvo los errores."));
                    lineas++;
                }
                continue;
            }
            texto.Append(Linea(entrada.Cuando, entrada.Nivel, entrada.Categoria, Limpio(entrada.Texto)));
            lineas++;
        }
        return texto.Length == 0 ? null : new Tramo(dia, texto.ToString(), lineas);
    }

    /// <summary>
    /// Escribe lo que espera, en orden. Si el archivo no se deja (otro programa lo tiene tomado, el disco lleno), se queda para el
    /// siguiente intento (revisión del PR #145: antes se perdía), hasta <see cref="MaximoSinEscribir"/>; al volver a escribir, se
    /// dice desde cuándo y cuánto se perdió, si algo.
    /// </summary>
    private void Vaciar()
    {
        while (_sinEscribir.Count > 0)
        {
            var tramo = _sinEscribir[0];
            var texto = tramo.Texto;
            if (_falloAlEscribir is { } fallo)
            {
                var perdidas = _perdidasPorElDisco > 0 ? $", menos {_perdidasPorElDisco} líneas que ya no cupieron" : "";
                texto = Linea(_falloDesde ?? _reloj.GetLocalNow(), LogLevel.Warning, Propia,
                    $"No se pudo escribir el registro un rato ({fallo}); lo de ese rato va enseguida{perdidas}.") + texto;
            }
            try
            {
                Directory.CreateDirectory(Carpeta);
                // Se abre y se cierra en cada tanda: se puede leer (o copiar) con el servicio en marcha.
                using var flujo = new FileStream(ArchivoDe(tramo.Dia), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                flujo.Write(Encoding.UTF8.GetBytes(texto));
            }
            catch (Exception ex) when (EsDelDisco(ex))
            {
                // El servicio sigue igual; se vuelve a intentar en un momento.
                _falloAlEscribir ??= ex.Message;
                _falloDesde ??= _reloj.GetLocalNow();
                var esperando = _sinEscribir.Sum(t => (long)t.Texto.Length);
                while (esperando > MaximoSinEscribir && _sinEscribir.Count > 1)
                {
                    esperando -= _sinEscribir[0].Texto.Length;
                    _perdidasPorElDisco += _sinEscribir[0].Lineas;
                    _sinEscribir.RemoveAt(0);
                }
                return;
            }
            (_falloAlEscribir, _falloDesde, _perdidasPorElDisco) = (null, null, 0);
            _sinEscribir.RemoveAt(0);
        }
    }

    /// <summary>
    /// El texto de una entrada sin secretos y, si es muy largo, recortado: primero se tacha y después se recorta (revisión del PR
    /// #145: una clave que caía justo en el corte quedaba a medias, sin parecerse a la completa). Lo larguísimo se acota antes, para
    /// no revisar megas de texto que de todos modos no se escriben.
    /// </summary>
    private string Limpio(string texto)
    {
        var acotado = texto.Length > 4 * LargoMaximo;
        var limpio = Tachar(acotado ? texto[..(4 * LargoMaximo)] : texto);
        return acotado || limpio.Length > LargoMaximo ? $"{limpio[..Math.Min(LargoMaximo, limpio.Length)]}… (recortado)" : limpio;
    }

    private static string Medida(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";

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
        AppDomain.CurrentDomain.UnhandledException -= AlTronar;
        _fila.Writer.TryWrite(new Entrada(_reloj.GetLocalNow(), LogLevel.Information, Propia, $"{Termina}.", null));
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
