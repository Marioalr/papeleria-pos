using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La caja de prueba se actualiza con los instaladores que arma GitHub (Configuración › Actualización), sin GitHub de verdad.</summary>
public sealed class GitHubPruebaTests : IDisposable
{
    private const string Repo = "dueno/tienda";
    private const string Token = "token-de-prueba";
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"actualizacion-{Guid.NewGuid():N}");
    private readonly GitHubFalso _github = new();

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private GitHubPrueba Cliente() => new(new HttpClient(_github) { BaseAddress = new Uri("https://api.github.com/") }, Repo);

    private const string Corridas = """
        { "total_count": 4, "workflow_runs": [
          { "id": 9, "run_number": 9, "head_branch": "main", "head_sha": "cccccccccccccccccccccccccccccccccccccccc", "status": "in_progress", "conclusion": null, "created_at": "2026-10-10T21:00:00Z", "html_url": "https://github.com/dueno/tienda/actions/runs/9" },
          { "id": 8, "run_number": 8, "head_branch": "main", "head_sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "status": "completed", "conclusion": "success", "created_at": "2026-10-10T20:00:00Z", "html_url": "https://github.com/dueno/tienda/actions/runs/8" },
          { "id": 7, "run_number": 7, "head_branch": "prueba/ajustes", "head_sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "status": "completed", "conclusion": "success", "created_at": "2026-10-10T18:00:00Z", "html_url": "https://github.com/dueno/tienda/actions/runs/7" },
          { "id": 6, "run_number": 6, "head_branch": "main", "head_sha": "dddddddddddddddddddddddddddddddddddddddd", "status": "completed", "conclusion": "failure", "created_at": "2026-10-09T18:00:00Z", "html_url": "https://github.com/dueno/tienda/actions/runs/6" }
        ] }
        """;

    private const string Artefactos = """
        { "total_count": 4, "artifacts": [
          { "id": 107, "size_in_bytes": 1000, "expired": false, "created_at": "2026-10-10T18:20:00Z", "digest": "sha256:ABC", "workflow_run": { "id": 7 } },
          { "id": 108, "size_in_bytes": 2000, "expired": false, "created_at": "2026-10-10T20:20:00Z", "digest": "sha256:DEF", "workflow_run": { "id": 8 } },
          { "id": 106, "size_in_bytes": 3000, "expired": false, "created_at": "2026-10-09T18:20:00Z", "digest": null, "workflow_run": { "id": 6 } },
          { "id": 105, "size_in_bytes": 4000, "expired": true, "created_at": "2026-09-01T18:20:00Z", "digest": null, "workflow_run": { "id": 5 } }
        ] }
        """;

    private void ConCorridasYArtefactos()
    {
        _github.Responder("GET /repos/dueno/tienda/actions/workflows/prueba.yml/runs?per_page=30", HttpStatusCode.OK, Corridas);
        _github.Responder("GET /repos/dueno/tienda/actions/artifacts?name=PapeleriaPos-Prueba&per_page=30", HttpStatusCode.OK, Artefactos);
    }

    [Fact]
    public async Task Busca_los_instaladores_armados_lo_ultimo_de_main_y_lo_que_se_esta_armando()
    {
        ConCorridasYArtefactos();
        _github.Responder("GET /repos/dueno/tienda/commits/main", HttpStatusCode.OK, """{ "sha": "cccccccccccccccccccccccccccccccccccccccc" }""");

        var estado = await Cliente().BuscarAsync(Token);

        // La que falló y la vencida no salen; la más nueva primero.
        Assert.Equal(["main.8", "prueba/ajustes.7"], estado.Versiones.Select(v => v.Nombre));
        Assert.Equal((108L, 2000L, "DEF", "aaaaaaa"), (estado.Versiones[0].Artefacto, estado.Versiones[0].Bytes, estado.Versiones[0].Huella, estado.Versiones[0].CommitCorto));
        Assert.Equal(new DateTime(2026, 10, 10, 20, 20, 0, DateTimeKind.Utc), estado.Versiones[0].ArmadaUtc.ToUniversalTime());
        Assert.Equal("main", Assert.Single(estado.Armandose).Rama);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", estado.UltimoDeMain);
        Assert.All(_github.Recibidas, r =>
        {
            Assert.Equal($"Bearer {Token}", r.Autorizacion);
            Assert.Contains("PapeleriaPos-Caja", r.Agente);
        });
    }

    [Fact]
    public async Task Sin_permiso_para_leer_el_codigo_igual_lista_las_versiones()
    {
        ConCorridasYArtefactos();
        _github.Responder("GET /repos/dueno/tienda/commits/main", HttpStatusCode.Forbidden, """{ "message": "Resource not accessible by personal access token" }""");

        var estado = await Cliente().BuscarAsync(Token);

        Assert.Equal(2, estado.Versiones.Count);
        Assert.Null(estado.UltimoDeMain);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "no aceptó el token")]
    [InlineData(HttpStatusCode.NotFound, "no tiene acceso a papeleria-pos")]
    [InlineData(HttpStatusCode.Forbidden, "no tiene acceso a papeleria-pos")]
    public async Task Los_errores_de_GitHub_dicen_que_hacer(HttpStatusCode codigo, string dice)
    {
        _github.Responder("GET /repos/dueno/tienda/actions/workflows/prueba.yml/runs?per_page=30", codigo, """{ "message": "no" }""");

        var error = await Assert.ThrowsAsync<ErrorActualizacion>(() => Cliente().BuscarAsync(Token));

        Assert.Contains(dice, error.Message);
    }

    [Fact]
    public async Task Sin_internet_lo_dice()
    {
        _github.SinInternet = true;

        var error = await Assert.ThrowsAsync<ErrorActualizacion>(() => Cliente().BuscarAsync(Token));

        Assert.Contains("internet", error.Message);
    }

    [Fact]
    public async Task Los_cambios_son_los_PR_fusionados_el_mas_nuevo_primero()
    {
        _github.Responder("GET /repos/dueno/tienda/compare/aaaa...cccc", HttpStatusCode.OK, """
            { "status": "ahead", "commits": [
              { "commit": { "message": "Venta (1): la pantalla nueva\n\nDetalle" } },
              { "commit": { "message": "Merge pull request #60 from dueno/venta-1\n\nVenta (1): la pantalla nueva" } },
              { "commit": { "message": "Merge pull request #61 from dueno/venta-2\n\nVenta (2): la búsqueda" } }
            ] }
            """);
        _github.Responder("GET /repos/dueno/tienda/compare/cccc...aaaa", HttpStatusCode.OK, """{ "status": "behind", "commits": [] }""");

        var adelante = await Cliente().CambiosAsync(Token, "aaaa", "cccc");
        var atras = await Cliente().CambiosAsync(Token, "cccc", "aaaa");

        Assert.Equal([new CambioIncluido(61, "Venta (2): la búsqueda"), new CambioIncluido(60, "Venta (1): la pantalla nueva")], adelante.Cambios);
        Assert.False(adelante.Anterior);
        Assert.True(atras.Anterior);
        Assert.Equal(new CambioIncluido(null, "Arreglo"), GitHubPrueba.Cambio("Arreglo\n\nmás detalle"));
    }

    [Fact]
    public async Task Armar_pide_una_corrida_con_lo_ultimo_de_main()
    {
        _github.Responder("POST /repos/dueno/tienda/actions/workflows/prueba.yml/dispatches", HttpStatusCode.NoContent, "");

        await Cliente().ArmarAsync(Token);

        Assert.Equal("""{"ref":"main"}""", Assert.Single(_github.Recibidas).Cuerpo);

        _github.Responder("POST /repos/dueno/tienda/actions/workflows/prueba.yml/dispatches", HttpStatusCode.Forbidden, """{ "message": "no" }""");
        var error = await Assert.ThrowsAsync<ErrorActualizacion>(() => Cliente().ArmarAsync(Token));
        Assert.Contains("lectura y escritura", error.Message);
    }

    private static byte[] ZipConInstalador(string contenido)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var escritor = new StreamWriter(zip.CreateEntry(GitHubPrueba.Instalador).Open());
            escritor.Write(contenido);
        }
        return memoria.ToArray();
    }

    private static VersionArmada Version(byte[] zip, string? huella = null) =>
        new(8, "main.8", "main", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", DateTime.UtcNow, 108, zip.Length, huella ?? Convert.ToHexString(SHA256.HashData(zip)));

    [Fact]
    public async Task Baja_el_instalador_sin_mandarle_el_token_al_otro_servidor_y_revisa_su_huella()
    {
        var zip = ZipConInstalador("MZ instalador de prueba");
        _github.Redirigir("GET /repos/dueno/tienda/actions/artifacts/108/zip", "https://descargas.ejemplo/zip?firma=xyz");
        _github.Responder("GET /zip?firma=xyz", HttpStatusCode.OK, zip);
        var avance = new List<(long, long)>();

        var exe = await Cliente().DescargarAsync(Token, Version(zip), _carpeta, new ProgresoInmediato(avance));

        Assert.Equal("MZ instalador de prueba", await File.ReadAllTextAsync(exe));
        Assert.Equal("PapeleriaPos-Prueba-main.8.exe", Path.GetFileName(exe));
        Assert.Equal([Path.GetFileName(exe)], Directory.GetFiles(_carpeta).Select(Path.GetFileName));
        Assert.Equal((zip.Length, (long)zip.Length), avance[^1]);
        Assert.Equal([$"Bearer {Token}", null], _github.Recibidas.Select(r => r.Autorizacion));
        Assert.Equal("descargas.ejemplo", _github.Recibidas[1].Servidor);
    }

    [Fact]
    public async Task Una_descarga_que_no_coincide_con_su_huella_no_se_instala()
    {
        var zip = ZipConInstalador("MZ incompleto");
        _github.Redirigir("GET /repos/dueno/tienda/actions/artifacts/108/zip", "https://descargas.ejemplo/zip?firma=xyz");
        _github.Responder("GET /zip?firma=xyz", HttpStatusCode.OK, zip);

        var error = await Assert.ThrowsAsync<ErrorActualizacion>(() => Cliente().DescargarAsync(Token, Version(zip, huella: "00"), _carpeta));

        Assert.Contains("no llegó completo", error.Message);
        Assert.Empty(Directory.GetFiles(_carpeta));
    }

    [Fact]
    public async Task Un_instalador_vencido_pide_armar_otro()
    {
        _github.Responder("GET /repos/dueno/tienda/actions/artifacts/108/zip", HttpStatusCode.Gone, """{ "message": "Artifact has expired" }""");

        var error = await Assert.ThrowsAsync<ErrorActualizacion>(() => Cliente().DescargarAsync(Token, Version([1, 2, 3]), _carpeta));

        Assert.Contains("venció", error.Message);
    }

    /// <summary>Progress&lt;T&gt; avisa después, en otro hilo: este, en el momento.</summary>
    private sealed class ProgresoInmediato(List<(long, long)> avance) : IProgress<(long Bajado, long Total)>
    {
        public void Report((long Bajado, long Total) valor) => avance.Add(valor);
    }

    private sealed record Recibida(string Servidor, string Ruta, string? Autorizacion, string Agente, string? Cuerpo);

    /// <summary>Contesta lo que se le dijo por «MÉTODO /ruta?consulta», y anota lo que recibió.</summary>
    private sealed class GitHubFalso : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _respuestas = [];

        public List<Recibida> Recibidas { get; } = [];

        public bool SinInternet { get; set; }

        public void Responder(string clave, HttpStatusCode codigo, string json) =>
            _respuestas[clave] = () => new HttpResponseMessage(codigo) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public void Responder(string clave, HttpStatusCode codigo, byte[] bytes) =>
            _respuestas[clave] = () => new HttpResponseMessage(codigo) { Content = new ByteArrayContent(bytes) };

        public void Redirigir(string clave, string destino) =>
            _respuestas[clave] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(destino) } };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage solicitud, CancellationToken ct)
        {
            if (SinInternet)
                throw new HttpRequestException("No such host is known.");
            var ruta = solicitud.RequestUri!.PathAndQuery;
            Recibidas.Add(new Recibida(solicitud.RequestUri.Host, ruta, solicitud.Headers.Authorization?.ToString(), solicitud.Headers.UserAgent.ToString(),
                solicitud.Content is null ? null : await solicitud.Content.ReadAsStringAsync(ct)));
            return _respuestas.TryGetValue($"{solicitud.Method} {ruta}", out var respuesta)
                ? respuesta()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($$"""{ "message": "sin respuesta para {{ruta}}" }""") };
        }
    }
}
