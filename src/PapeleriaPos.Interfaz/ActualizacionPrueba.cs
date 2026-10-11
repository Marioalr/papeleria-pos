using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace PapeleriaPos.Interfaz;

/// <summary>Un instalador de prueba ya armado: una corrida de .github/workflows/prueba.yml con su artefacto.</summary>
/// <param name="Nombre">Como lo nombra el instalador: la rama y el número de la corrida («main.8»).</param>
/// <param name="Huella">El SHA-256 del zip que da GitHub, para revisar la descarga.</param>
public sealed record VersionArmada(long Corrida, string Nombre, string Rama, string Commit, DateTime ArmadaUtc, long Artefacto, long Bytes, string? Huella)
{
    public string CommitCorto => Commit[..Math.Min(7, Commit.Length)];
}

/// <summary>Una corrida del instalador que todavía no termina.</summary>
public sealed record VersionArmandose(string Rama, DateTime DesdeUtc, string Url);

/// <summary>Lo que hay en GitHub: los instaladores armados (el más nuevo primero), el último cambio de main y lo que se está armando.</summary>
public sealed record EstadoVersiones(IReadOnlyList<VersionArmada> Versiones, string? UltimoDeMain, IReadOnlyList<VersionArmandose> Armandose);

/// <summary>Un cambio entre dos versiones: el PR fusionado con su título, o el commit si no vino de un PR.</summary>
public sealed record CambioIncluido(int? Pr, string Titulo);

/// <param name="Anterior">La versión elegida es más vieja que la instalada (o de otra rama, sin lo de la instalada).</param>
public sealed record CambiosEntre(IReadOnlyList<CambioIncluido> Cambios, bool Anterior);

public sealed class ErrorActualizacion(string mensaje) : Exception(mensaje);

/// <summary>
/// La caja de prueba se actualiza desde los instaladores que arma GitHub Actions. El repositorio es privado: todo va con
/// un token de GitHub de quien prueba. Sin seguir redirecciones solo: el zip se baja de otro servidor, y a ese no se le
/// manda el token.
/// </summary>
public sealed class GitHubPrueba
{
    public const string RepositorioPorOmision = "Marioalr/papeleria-pos";
    public const string Flujo = "prueba.yml";

    /// <summary>Lo que espera cada consulta (no la descarga): GitHub contesta en segundos.</summary>
    private static readonly TimeSpan LimiteConsulta = TimeSpan.FromSeconds(30);
    public const string NombreArtefacto = "PapeleriaPos-Prueba";
    public const string Instalador = "PapeleriaPos-Prueba.exe";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http;
    private readonly string _repositorio;

    /// <param name="http">Sin redirecciones automáticas (<see cref="CrearHttp"/>).</param>
    /// <param name="repositorio">«dueño/nombre».</param>
    public GitHubPrueba(HttpClient http, string repositorio)
    {
        _http = http;
        _repositorio = repositorio;
    }

    public static HttpClient CrearHttp() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(15),
        };

    /// <summary>Del commit con que se compiló esta caja («1.0.0+abc123…», lo pone el SDK).</summary>
    public static string? CommitInstalado(Assembly? ensamblado = null) =>
        (ensamblado ?? typeof(GitHubPrueba).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { } version && version.IndexOf('+') is var mas and >= 0 && version[(mas + 1)..] is { Length: >= 7 } commit
            ? commit
            : null;

    public async Task<EstadoVersiones> BuscarAsync(string token, CancellationToken ct = default)
    {
        var corridas = (await LeerAsync<Corridas>(token, $"repos/{_repositorio}/actions/workflows/{Flujo}/runs?per_page=30", ct)).WorkflowRuns;
        var artefactos = (await LeerAsync<Artefactos>(token, $"repos/{_repositorio}/actions/artifacts?name={NombreArtefacto}&per_page=30", ct)).Artifacts;
        var porId = corridas.ToDictionary(c => c.Id);
        var versiones = artefactos
            .Where(a => !a.Expired && a.WorkflowRun is not null && porId.TryGetValue(a.WorkflowRun.Id, out var c) && c.Conclusion == "success")
            .Select(a =>
            {
                var corrida = porId[a.WorkflowRun!.Id];
                return new VersionArmada(corrida.Id, $"{corrida.HeadBranch}.{corrida.RunNumber}", corrida.HeadBranch, corrida.HeadSha, a.CreatedAt,
                    a.Id, a.SizeInBytes, a.Digest is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) ? d["sha256:".Length..] : null);
            })
            .OrderByDescending(v => v.ArmadaUtc)
            .ToList();
        var armandose = corridas.Where(c => c.Status is "queued" or "in_progress" or "waiting" or "pending" or "requested")
            .Select(c => new VersionArmandose(c.HeadBranch, c.CreatedAt, c.HtmlUrl)).ToList();
        string? main = null;
        try
        {
            main = (await LeerAsync<Commit>(token, $"repos/{_repositorio}/commits/main", ct)).Sha;
        }
        catch (ErrorActualizacion)
        {
            // Sin permiso para leer el código, solo no se sabe si main tiene algo más nuevo.
        }
        return new EstadoVersiones(versiones, main, armandose);
    }

    /// <summary>Lo que trae <paramref name="hasta"/> que no tenía <paramref name="desde"/>: los PR fusionados (o los commits, si no hay).</summary>
    public async Task<CambiosEntre> CambiosAsync(string token, string desde, string hasta, CancellationToken ct = default)
    {
        var comparacion = await LeerAsync<Comparacion>(token, $"repos/{_repositorio}/compare/{desde}...{hasta}", ct);
        var cambios = comparacion.Commits.Select(c => Cambio(c.Commit.Message)).ToList();
        if (cambios.Any(c => c.Pr is not null))
            cambios = cambios.Where(c => c.Pr is not null).ToList();
        cambios.Reverse();
        return new CambiosEntre(cambios, comparacion.Status is "behind" or "diverged");
    }

    /// <summary>«Merge pull request #62 from …» y el título en la siguiente línea; si no, la primera línea del commit.</summary>
    public static CambioIncluido Cambio(string mensaje)
    {
        var lineas = mensaje.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        const string fusion = "Merge pull request #";
        if (lineas.Length > 0 && lineas[0].StartsWith(fusion, StringComparison.Ordinal)
            && int.TryParse(lineas[0][fusion.Length..].Split(' ')[0], out var pr))
            return new CambioIncluido(pr, lineas.Length > 1 ? lineas[1] : lineas[0]);
        return new CambioIncluido(null, lineas.Length > 0 ? lineas[0] : "");
    }

    /// <summary>Arma un instalador con lo último de <paramref name="rama"/> (tarda unos minutos en GitHub).</summary>
    public async Task ArmarAsync(string token, string rama = "main", CancellationToken ct = default)
    {
        using var solicitud = Solicitud(HttpMethod.Post, $"repos/{_repositorio}/actions/workflows/{Flujo}/dispatches", token);
        solicitud.Content = JsonContent.Create(new { @ref = rama });
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteConsulta);
        using var respuesta = await EnviarAsync(solicitud, limite.Token);
        if (respuesta.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new ErrorActualizacion("El token no tiene permiso para armar: dale «Actions: lectura y escritura».");
        await RevisarAsync(respuesta);
    }

    /// <summary>
    /// Baja el instalador de <paramref name="version"/> a <paramref name="carpeta"/>, revisa que llegó completo (su
    /// SHA-256) y devuelve la ruta del .exe. <paramref name="progreso"/> recibe lo bajado y el total.
    /// </summary>
    public async Task<string> DescargarAsync(string token, VersionArmada version, string carpeta, IProgress<(long Bajado, long Total)>? progreso = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(carpeta);
        foreach (var anterior in Directory.EnumerateFiles(carpeta, "PapeleriaPos-Prueba*"))
            BorrarSiSePuede(anterior);
        var zip = Path.Combine(carpeta, $"PapeleriaPos-Prueba-{Limpio(version.Nombre)}.zip");
        var exe = Path.ChangeExtension(zip, ".exe");

        Uri destino;
        using (var solicitud = Solicitud(HttpMethod.Get, $"repos/{_repositorio}/actions/artifacts/{version.Artefacto}/zip", token))
        using (var respuesta = await EnviarAsync(solicitud, ct))
        {
            if (respuesta.StatusCode == HttpStatusCode.Gone)
                throw new ErrorActualizacion($"El instalador {version.Nombre} ya venció en GitHub: arma uno nuevo.");
            if (respuesta.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther)
                || respuesta.Headers.Location is not { } ubicacion)
            {
                await RevisarAsync(respuesta);
                throw new ErrorActualizacion("GitHub no dio de dónde bajar el instalador.");
            }
            destino = ubicacion.IsAbsoluteUri ? ubicacion : new Uri(_http.BaseAddress!, ubicacion);
        }

        // El zip está en otro servidor, con la firma en la dirección: sin el token.
        try
        {
            using var respuesta = await EnviarAsync(new HttpRequestMessage(HttpMethod.Get, destino), ct, HttpCompletionOption.ResponseHeadersRead);
            await RevisarAsync(respuesta);
            var total = respuesta.Content.Headers.ContentLength ?? version.Bytes;
            await using var origen = await respuesta.Content.ReadAsStreamAsync(ct);
            await using var archivo = File.Create(zip);
            var bufer = new byte[1 << 16];
            long bajado = 0;
            int leidos;
            while ((leidos = await origen.ReadAsync(bufer, ct)) > 0)
            {
                await archivo.WriteAsync(bufer.AsMemory(0, leidos), ct);
                bajado += leidos;
                progreso?.Report((bajado, total));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ErrorActualizacion)
        {
            // Cancelada o cortada a la mitad: no queda un zip incompleto.
            BorrarSiSePuede(zip);
            if (ex is IOException && !ct.IsCancellationRequested)
                throw new ErrorActualizacion("La descarga se cortó: revisa el internet y vuelve a intentarlo.");
            throw;
        }

        if (version.Huella is { } huella)
        {
            string calculada;
            await using (var leido = File.OpenRead(zip))
                calculada = Convert.ToHexString(await SHA256.HashDataAsync(leido, ct));
            if (!string.Equals(calculada, huella, StringComparison.OrdinalIgnoreCase))
            {
                BorrarSiSePuede(zip);
                throw new ErrorActualizacion("El instalador no llegó completo (no coincide su huella): vuelve a intentarlo.");
            }
        }
        using (var archivo = ZipFile.OpenRead(zip))
        {
            var entrada = archivo.Entries.FirstOrDefault(e => e.Name.Equals(Instalador, StringComparison.OrdinalIgnoreCase))
                ?? throw new ErrorActualizacion($"El zip de {version.Nombre} no trae {Instalador}.");
            entrada.ExtractToFile(exe, overwrite: true);
        }
        BorrarSiSePuede(zip);
        return exe;
    }

    private static string Limpio(string nombre) => string.Concat(nombre.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-'));

    private static void BorrarSiSePuede(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpRequestMessage Solicitud(HttpMethod metodo, string ruta, string token)
    {
        var solicitud = new HttpRequestMessage(metodo, ruta);
        solicitud.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        solicitud.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        solicitud.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return solicitud;
    }

    private async Task<HttpResponseMessage> EnviarAsync(HttpRequestMessage solicitud, CancellationToken ct,
        HttpCompletionOption completar = HttpCompletionOption.ResponseContentRead)
    {
        // GitHub no contesta sin quién pregunta.
        solicitud.Headers.UserAgent.Add(new ProductInfoHeaderValue("PapeleriaPos-Caja", "1"));
        try
        {
            return await _http.SendAsync(solicitud, completar, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ErrorActualizacion("No se pudo hablar con GitHub: revisa el internet de esta PC.");
        }
    }

    private async Task<T> LeerAsync<T>(string token, string ruta, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(LimiteConsulta);
        using var solicitud = Solicitud(HttpMethod.Get, ruta, token);
        using var respuesta = await EnviarAsync(solicitud, limite.Token);
        await RevisarAsync(respuesta);
        return await respuesta.Content.ReadFromJsonAsync<T>(Json, limite.Token) ?? throw new ErrorActualizacion("GitHub contestó vacío.");
    }

    private static async Task RevisarAsync(HttpResponseMessage respuesta)
    {
        if (respuesta.IsSuccessStatusCode)
            return;
        var limite = respuesta.Headers.TryGetValues("X-RateLimit-Remaining", out var quedan) && quedan.FirstOrDefault() == "0";
        throw new ErrorActualizacion(respuesta.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub no aceptó el token: revisa que esté bien copiado y que no haya vencido.",
            HttpStatusCode.Forbidden when limite => "GitHub pide esperar un rato (demasiadas consultas): vuelve a buscar en unos minutos.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                "El token no tiene acceso a papeleria-pos: dale acceso al repositorio con «Actions: lectura» (y «Contents: lectura»).",
            _ => $"GitHub contestó {(int)respuesta.StatusCode} {respuesta.ReasonPhrase}: {Recortar(await respuesta.Content.ReadAsStringAsync())}",
        });
    }

    private static string Recortar(string texto) => texto.Length <= 200 ? texto : texto[..200] + "…";

    private sealed record Corridas(List<Corrida> WorkflowRuns);

    private sealed record Corrida(long Id, int RunNumber, string HeadBranch, string HeadSha, string Status, string? Conclusion, DateTime CreatedAt, string HtmlUrl);

    private sealed record Artefactos(List<Artefacto> Artifacts);

    private sealed record Artefacto(long Id, long SizeInBytes, bool Expired, DateTime CreatedAt, string? Digest, CorridaDelArtefacto? WorkflowRun);

    private sealed record CorridaDelArtefacto(long Id);

    private sealed record Commit(string Sha);

    private sealed record Comparacion(string Status, List<CommitComparado> Commits);

    private sealed record CommitComparado(DatosCommit Commit);

    private sealed record DatosCommit(string Message);
}

/// <summary>Lo que depende de la PC (Windows): dónde queda el token y cómo se instala.</summary>
public interface IEquipoPrueba
{
    /// <summary>El token de GitHub guardado en esta PC, o null.</summary>
    string? LeerToken();

    /// <summary>Null lo borra.</summary>
    void GuardarToken(string? token);

    /// <summary>Donde se baja el instalador.</summary>
    string CarpetaDescargas { get; }

    /// <summary>
    /// Abre el instalador en silencio. Windows pide permiso de administrador; si se da, el instalador cierra la caja (y su
    /// servicio), la reemplaza y la vuelve a abrir. Si no se da, la caja sigue como estaba.
    /// </summary>
    void Instalar(string instalador);
}

/// <summary>El navegador de desarrollo (Caja:ActualizacionPrueba=true): el token solo en memoria, y no instala.</summary>
public sealed class EquipoDeNavegador : IEquipoPrueba
{
    private string? _token;

    public string? LeerToken() => _token;

    public void GuardarToken(string? token) => _token = token;

    public string CarpetaDescargas => Path.Combine(Path.GetTempPath(), "PapeleriaPos-Prueba-navegador");

    public void Instalar(string instalador) =>
        throw new ErrorActualizacion($"Desde el navegador solo se busca y se baja ({instalador}): se instala en la caja de Windows.");
}

public static class RegistroActualizacionPrueba
{
    /// <summary>Configuración › Actualización: solo en la caja de prueba.</summary>
    public static IServiceCollection AgregarActualizacionPrueba(this IServiceCollection servicios, IEquipoPrueba equipo, string repositorio)
    {
        servicios.AddSingleton(equipo);
        servicios.AddSingleton(new GitHubPrueba(GitHubPrueba.CrearHttp(), repositorio));
        return servicios;
    }
}
