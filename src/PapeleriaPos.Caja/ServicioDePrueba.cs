using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La prueba con una persona real (docs/plan.md): no hay servicio de Windows. La caja arranca su propio servicio en su
/// puerto y lo detiene al cerrarse; si ya estaba corriendo, usa ese. La instaló el administrador en
/// Archivos de programa, así que la base de trabajo va en la carpeta de datos (ProgramData, con escritura para los
/// usuarios) y la inicial se lee de la instalación. Lo que escribe el servicio (su registro de ASP.NET y, si truena, el
/// error) va a registros\servicio-….log, junto al de la caja: antes se perdía.
/// </summary>
internal sealed class ServicioDePrueba : IDisposable
{
    private static readonly TimeSpan Espera = TimeSpan.FromMinutes(1);

    /// <summary>Lo que se espera a que el servicio se detenga bien al cerrar la caja; después, se cierra a la fuerza.</summary>
    private static readonly TimeSpan EsperaAlCerrar = TimeSpan.FromSeconds(5);

    /// <summary>Ya detenido, lo que se espera a que llegue lo último que escribió (al detenerse) antes de soltarlo.</summary>
    private static readonly TimeSpan EsperaDeLoUltimo = TimeSpan.FromSeconds(1);

    /// <summary>El servicio escribe en UTF-8 cuando lo arranca la caja (Program.cs): así llegan los acentos.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly RegistroDiario _salida;
    private readonly TaskCompletionSource _finDeLaSalida = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finDeLosErrores = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _proceso;
    private bool _leyendo;
    private volatile bool _deteniendo;

    private ServicioDePrueba(ConfiguracionCaja configuracion, RegistroDiario salida)
    {
        _salida = salida;
        Listo = ArrancarAsync(configuracion);
    }

    /// <summary>Se completa cuando el servicio contesta; si no arranca, falla con un mensaje para mostrar.</summary>
    public Task Listo { get; }

    /// <param name="salida">Donde va lo que escribe el servicio, un archivo por día (<c>servicio-….log</c>).</param>
    public static ServicioDePrueba Iniciar(ConfiguracionCaja configuracion, RegistroDiario salida) => new(configuracion, salida);

    private static string BaseInicial(ConfiguracionCaja configuracion) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuracion.BaseInicial ?? @"..\datos\prueba.db"));

    /// <summary>Donde van la base de trabajo, las respuestas y la base local de la caja (para trabajar sin conexión).</summary>
    public static string CarpetaDatos(ConfiguracionCaja configuracion) =>
        configuracion.CarpetaDatos is { Length: > 0 } c ? Environment.ExpandEnvironmentVariables(c) : Path.GetDirectoryName(BaseInicial(configuracion))!;

    private async Task ArrancarAsync(ConfiguracionCaja configuracion)
    {
        var url = new Uri(configuracion.Servicio);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _salida.Limpiar();
        if (await ContestaAsync(http, url))
        {
            _salida.Anotar($"Ya había un servicio contestando en {url.GetLeftPart(UriPartial.Authority)}: se usa ese, y lo que escribe no queda aquí.");
            return;
        }

        var carpeta = AppContext.BaseDirectory;
        var exe = Path.GetFullPath(Path.Combine(carpeta, configuracion.ServicioLocal ?? @"..\Servicio\PapeleriaPos.Servicio.exe"));
        var inicial = BaseInicial(configuracion);
        var datos = CarpetaDatos(configuracion);
        Directory.CreateDirectory(datos);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"No se encontró el servicio de la prueba ({exe}): reinstala la prueba.");
        var origen = url.GetLeftPart(UriPartial.Authority);
        // La entrada queda conectada a la caja: al cerrarse (aunque sea a la fuerza), se cierra y el servicio se detiene bien.
        // Lo que escribe se lee en segundo plano (si nadie lo leyera, el servicio se trabaría al llenarse el búfer).
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (var argumento in new[]
                 {
                     // Escucha en la red para el conteo desde el celular; el instalador abrió el firewall solo a la red local.
                     $"--Urls=http://*:{url.Port}", $"--Caja:Servicio={origen}", $"--ConnectionStrings:Pos=Data Source={Path.Combine(datos, "pos.db")}",
                     "--Prueba:Activa=true", $"--Prueba:BaseInicial={inicial}", "--Telegram:Activo=false", "--Descubrimiento:Activo=false",
                     "--Impresora:Vigilar=false", "--Respaldos:Activo=false", "--Prueba:DetenerAlCerrarEntrada=true",
                 })
            info.ArgumentList.Add(argumento);
        _proceso = new Process { StartInfo = info, EnableRaisingEvents = true };
        _proceso.OutputDataReceived += (_, e) => Leer(e.Data, _finDeLaSalida, "");
        _proceso.ErrorDataReceived += (_, e) => Leer(e.Data, _finDeLosErrores, "(error) ");
        _proceso.Exited += AlTerminar;
        if (!_proceso.Start())
            throw new InvalidOperationException("No se pudo arrancar el servicio de la prueba.");
        _salida.Anotar($"Arrancó el servicio de la prueba (proceso {_proceso.Id}, {origen}).");
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();
        _leyendo = true;

        var limite = DateTime.UtcNow + Espera;
        while (DateTime.UtcNow < limite)
        {
            if (_proceso.HasExited)
                throw new InvalidOperationException($"El servicio de la prueba se cerró al arrancar (código {_proceso.ExitCode}).");
            if (await ContestaAsync(http, url))
                return;
            await Task.Delay(250);
        }
        throw new InvalidOperationException("El servicio de la prueba no contestó en un minuto: cierra la caja y vuelve a abrirla.");
    }

    /// <summary>Cada línea con la hora en que llegó; la salida de errores (si el servicio truena, ahí escribe .NET), marcada.</summary>
    private void Leer(string? linea, TaskCompletionSource fin, string marca)
    {
        if (linea is null)
            fin.TrySetResult();
        else
            _salida.Anotar(marca + linea);
    }

    private void AlTerminar(object? sender, EventArgs e)
    {
        string codigo;
        try
        {
            codigo = ((Process)sender!).ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            codigo = "?";
        }
        _salida.Anotar(_deteniendo
            ? $"Se detuvo el servicio de la prueba (código {codigo})."
            : $"El servicio de la prueba se cerró solo (código {codigo}): la caja sigue con su copia, sin conexión.");
    }

    private static async Task<bool> ContestaAsync(HttpClient http, Uri url)
    {
        try
        {
            using var respuesta = await http.GetAsync(new Uri(url, "api/estado"));
            return respuesta.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Le cierra la entrada al servicio para que se detenga bien: así quita la marca de la base contra apagones y, al
    /// volver a abrir la caja, no revisa la base como después de un apagón. Si no termina a tiempo, a la fuerza.
    /// </summary>
    public void Dispose()
    {
        _deteniendo = true;
        try
        {
            if (_proceso is { HasExited: false })
            {
                try
                {
                    _proceso.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Ya se estaba cerrando.
                }
                if (!_proceso.WaitForExit(EsperaAlCerrar))
                    _proceso.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
        // Lo último que escribió (al detenerse) llega por la lectura en segundo plano, que termina al cerrarse su salida.
        if (_leyendo)
            Task.WaitAll([_finDeLaSalida.Task, _finDeLosErrores.Task], EsperaDeLoUltimo);
        _proceso?.Dispose();
    }
}
