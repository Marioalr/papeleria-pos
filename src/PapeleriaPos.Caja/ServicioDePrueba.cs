using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace PapeleriaPos.Caja;

/// <summary>
/// La prueba con una persona real (docs/plan.md): no hay servicio de Windows. La caja arranca su propio servicio en su
/// puerto y lo apaga al cerrarse; si ya estaba corriendo (la caja se cerró mal), usa ese. La instaló el administrador en
/// Archivos de programa, así que la base de trabajo va en la carpeta de datos (ProgramData, con escritura para los
/// usuarios) y la inicial se lee de la instalación.
/// </summary>
internal sealed class ServicioDePrueba : IDisposable
{
    private static readonly TimeSpan Espera = TimeSpan.FromMinutes(1);
    private Process? _proceso;

    private ServicioDePrueba(ConfiguracionCaja configuracion) => Listo = ArrancarAsync(configuracion);

    /// <summary>Se completa cuando el servicio contesta; si no arranca, falla con un mensaje para mostrar.</summary>
    public Task Listo { get; }

    public static ServicioDePrueba Iniciar(ConfiguracionCaja configuracion) => new(configuracion);

    private static string BaseInicial(ConfiguracionCaja configuracion) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuracion.BaseInicial ?? @"..\datos\prueba.db"));

    /// <summary>Donde van la base de trabajo, las respuestas y la base local de la caja (para trabajar sin conexión).</summary>
    public static string CarpetaDatos(ConfiguracionCaja configuracion) =>
        configuracion.CarpetaDatos is { Length: > 0 } c ? Environment.ExpandEnvironmentVariables(c) : Path.GetDirectoryName(BaseInicial(configuracion))!;

    private async Task ArrancarAsync(ConfiguracionCaja configuracion)
    {
        var url = new Uri(configuracion.Servicio);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        if (await ContestaAsync(http, url))
            return;

        var carpeta = AppContext.BaseDirectory;
        var exe = Path.GetFullPath(Path.Combine(carpeta, configuracion.ServicioLocal ?? @"..\Servicio\PapeleriaPos.Servicio.exe"));
        var inicial = BaseInicial(configuracion);
        var datos = CarpetaDatos(configuracion);
        Directory.CreateDirectory(datos);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"No se encontró el servicio de la prueba ({exe}): reinstala la prueba.");
        var origen = url.GetLeftPart(UriPartial.Authority);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argumento in new[]
                 {
                     // Escucha en la red para el conteo desde el celular; el instalador abrió el firewall solo a la red local.
                     $"--Urls=http://*:{url.Port}", $"--Caja:Servicio={origen}", $"--ConnectionStrings:Pos=Data Source={Path.Combine(datos, "pos.db")}",
                     "--Prueba:Activa=true", $"--Prueba:BaseInicial={inicial}", "--Telegram:Activo=false", "--Descubrimiento:Activo=false",
                     "--Impresora:Vigilar=false", "--Respaldos:Activo=false",
                 })
            info.ArgumentList.Add(argumento);
        _proceso = Process.Start(info) ?? throw new InvalidOperationException("No se pudo arrancar el servicio de la prueba.");

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

    public void Dispose()
    {
        try
        {
            if (_proceso is { HasExited: false })
                _proceso.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
        _proceso?.Dispose();
    }
}
