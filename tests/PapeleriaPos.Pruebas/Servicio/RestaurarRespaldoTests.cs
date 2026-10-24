using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>El reinicio de mentiras: dice cuándo se pidió, sin detener nada (la prueba detiene el servicio y lo vuelve a arrancar).</summary>
internal sealed class ReinicioDePrueba(bool automatico = true) : IReinicioDelServicio
{
    private readonly TaskCompletionSource _pedido = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _veces;

    public bool Automatico => automatico;

    public int Veces => Volatile.Read(ref _veces);

    /// <summary>Termina cuando se pidió: después de que salió la respuesta.</summary>
    public Task Pedido => _pedido.Task;

    public void Reiniciar()
    {
        Interlocked.Increment(ref _veces);
        _pedido.TrySetResult();
    }
}

/// <summary>
/// Por donde las cajas le hablan a la caja principal: la prueba lo conecta al servicio que esté en marcha (el de antes de restaurar o
/// el que arrancó después, sobre la misma base). Mientras no hay ninguno no contesta, como mientras Windows lo vuelve a arrancar.
/// </summary>
internal sealed class PrincipalQueSeReinicia : HttpMessageHandler
{
    private volatile HttpMessageInvoker? _actual;

    public void Conectar(HttpMessageHandler manejador) => _actual = new HttpMessageInvoker(manejador, disposeHandler: false);

    public void Apagar() => _actual = null;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _actual is { } actual
            ? actual.SendAsync(request, cancellationToken)
            : Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "La caja principal se está reiniciando."));
}

/// <summary>
/// La caja principal con su base en su propia carpeta (pos.db y respaldos\, como en la tienda) y el reinicio de mentiras: se
/// detiene y se vuelve a arrancar sobre la misma base, como cuando Windows la vuelve a arrancar. Sin la copia diaria de fondo, así lo
/// que se revisa no depende de cuándo corre.
/// </summary>
internal sealed class PrincipalQueSeRestaura : IAsyncDisposable
{
    private PrincipalQueSeRestaura(string carpeta, ServicioDePrueba servicio, ReinicioDePrueba reinicio) =>
        (Carpeta, Servicio, Reinicio) = (carpeta, servicio, reinicio);

    public string Carpeta { get; }
    public string CarpetaRespaldos => Path.Combine(Carpeta, "respaldos");
    public ServicioDePrueba Servicio { get; }
    public string Ruta => Servicio.Ruta;
    public ReinicioDePrueba Reinicio { get; }

    /// <summary>Para el HttpClient de las cajas: siempre la que está en marcha.</summary>
    public PrincipalQueSeReinicia ParaLasCajas { get; } = new();

    public WebApplicationFactory<Program>? EnMarcha { get; private set; }

    public static async Task<PrincipalQueSeRestaura> CrearAsync(bool automatico = true, params (string Clave, string Valor)[] ajustes)
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"restaurar-{Guid.NewGuid():N}");
        var servicio = await ServicioDePrueba.CrearEnCarpetaAsync(carpeta, SinLaDiaria,
            [("Respaldos:Activo", "true"), ("Respaldos:Carpeta", Path.Combine(carpeta, "respaldos")), .. ajustes]);
        return new PrincipalQueSeRestaura(carpeta, servicio, new ReinicioDePrueba(automatico));
    }

    private static void SinLaDiaria(IServiceCollection s) =>
        s.Remove(s.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RespaldoDiario)));

    /// <summary>
    /// Arranca (la primera vez, o después de detenerse): si hay un pedido de restaurar, se restaura antes de abrir la base. Con
    /// <paramref name="ajustes"/>, solo esta vez con esa configuración.
    /// </summary>
    public HttpClient Arrancar(params (string Clave, string Valor)[] ajustes) => Arrancar(null, ajustes);

    /// <summary>Como <see cref="Arrancar(ValueTuple{string, string}[])"/>, y solo esta vez con lo que cambie <paramref name="servicios"/>.</summary>
    public HttpClient Arrancar(Action<IServiceCollection>? servicios, params (string Clave, string Valor)[] ajustes)
    {
        var fabrica = Servicio.Fabrica.WithWebHostBuilder(b =>
        {
            foreach (var (clave, valor) in ajustes)
                b.UseSetting(clave, valor);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IReinicioDelServicio>();
                s.AddSingleton<IReinicioDelServicio>(Reinicio);
                servicios?.Invoke(s);
            });
        });
        EnMarcha = fabrica;
        var http = fabrica.CreateClient();
        ParaLasCajas.Conectar(fabrica.Server.CreateHandler());
        return http;
    }

    /// <summary>Se detiene (como al pedir el reinicio): las cajas dejan de verla y se suelta la base.</summary>
    public async Task DetenerAsync()
    {
        ParaLasCajas.Apagar();
        if (EnMarcha is { } enMarcha)
            await enMarcha.DisposeAsync();
        EnMarcha = null;
        // La conexión guardada con que se preparó la base (ServicioDePrueba): en Windows no dejaría apartarla.
        ArchivosDePrueba.SoltarBase(Ruta);
    }

    /// <summary>Un respaldo como los del servicio (<see cref="Respaldos.Hacer"/>), con su hora; devuelve su nombre.</summary>
    public string Respaldar(DateTime local, string? etiqueta = null)
    {
        var ruta = Respaldos.Hacer($"Data Source={Ruta};Pooling=False", CarpetaRespaldos, local, etiqueta);
        File.SetLastWriteTimeUtc(ruta, HoraMexico.AUtc(local));
        return Path.GetFileName(ruta);
    }

    /// <summary>La carpeta donde quedó la base de antes (la única <c>antes-de-restaurar-…</c>).</summary>
    public string BaseDeAntes() => Assert.Single(Directory.GetDirectories(Carpeta, "antes-de-restaurar-*"));

    public async ValueTask DisposeAsync()
    {
        ParaLasCajas.Apagar();
        await Servicio.DisposeAsync();
        ArchivosDePrueba.BorrarCarpeta(Carpeta);
    }
}

/// <summary>
/// Restaurar un respaldo (Configuración › Respaldos, 2026-10-23): el servicio deja el pedido, se detiene y, al volver a arrancar y
/// antes de abrir la base, revisa el respaldo, aparta la base de antes (con su -wal, su -shm y sus marcas, sin borrarla), pone el
/// respaldo y migra. Las cajas notan la base nueva y bajan su copia completa; lo que tenían por subir entra en la restaurada.
/// </summary>
public sealed class RestaurarRespaldoTests
{
    /// <summary>Una tabla propia de la prueba (el servicio no la toca): lo que había antes y después del respaldo.</summary>
    internal static async Task MarcarAsync(string ruta, string texto)
    {
        await using var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False");
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "CREATE TABLE IF NOT EXISTS MarcaDeLaPrueba (Texto TEXT NOT NULL); INSERT INTO MarcaDeLaPrueba (Texto) VALUES ($texto)";
        comando.Parameters.AddWithValue("$texto", texto);
        await comando.ExecuteNonQueryAsync();
    }

    internal static async Task<List<string>> MarcasAsync(string ruta)
    {
        await using var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False");
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Texto FROM MarcaDeLaPrueba ORDER BY rowid";
        await using var lector = await comando.ExecuteReaderAsync();
        var marcas = new List<string>();
        while (await lector.ReadAsync())
            marcas.Add(lector.GetString(0));
        return marcas;
    }

    /// <summary>
    /// Lo último que se escribió, todavía en el -wal y no en la base: como queda si el servicio se detuvo de golpe. (Al cerrar la
    /// última conexión SQLite lo pasa a la base: se copian los dos archivos con la conexión abierta y se regresan así.)
    /// </summary>
    internal static async Task MarcarSoloEnElWalAsync(string ruta, string texto)
    {
        await using (var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False"))
        {
            await conexion.OpenAsync();
            await using (var comando = conexion.CreateCommand())
            {
                comando.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO MarcaDeLaPrueba (Texto) VALUES ($texto)";
                comando.Parameters.AddWithValue("$texto", texto);
                await comando.ExecuteNonQueryAsync();
            }
            CopiarAbierto(ruta, ruta + ".como-estaba");
            CopiarAbierto(ruta + "-wal", ruta + "-wal.como-estaba");
        }
        File.Move(ruta + ".como-estaba", ruta, overwrite: true);
        File.Move(ruta + "-wal.como-estaba", ruta + "-wal", overwrite: true);
        File.Delete(ruta + "-shm");
    }

    /// <summary>Copia un archivo que SQLite tiene abierto (en Windows, File.Copy no deja con un escritor abierto).</summary>
    private static void CopiarAbierto(string origen, string destino)
    {
        using var de = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var a = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        de.CopyTo(a);
    }

    /// <summary>Como la arma el servicio al arrancar: con la revisión completa y el espacio libre de verdad, si la prueba no pone otros.</summary>
    internal static RestauracionDeRespaldos Restauracion(PrincipalQueSeRestaura p, IRevisionCompleta? revision = null, IEspacioEnDisco? espacio = null) =>
        new($"Data Source={p.Ruta};Pooling=False", p.CarpetaRespaldos, revision ?? new RevisionCompletaSqlite(), espacio ?? new EspacioEnDisco(),
            NullLogger<RestauracionDeRespaldos>.Instance, TimeProvider.System);

    internal static async Task<(Guid Dueno, Guid Caja, Guid Sucursal)> QuienAsync(PrincipalQueSeRestaura p)
    {
        await using var db = p.Servicio.Base();
        var caja = await db.Cajas.AsNoTracking().SingleAsync();
        return (await db.Usuarios.Where(u => u.NombreUsuario == "dueno").Select(u => u.Id).SingleAsync(), caja.Id, caja.SucursalId);
    }

    internal static DateTime Hace(int minutos) => HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-minutos);

    internal static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    internal static async Task<string> RechazoAsync(HttpResponseMessage r, HttpStatusCode codigo = HttpStatusCode.BadRequest)
    {
        Assert.Equal(codigo, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    /// <summary>Una revisión completa que no pasa con lo que diga <paramref name="falla"/> (la ruta de la base que revisa).</summary>
    internal sealed class RevisionQueFalla(Func<string, bool> falla) : IRevisionCompleta
    {
        public IReadOnlyList<string> Revisar(string cadenaConexion) =>
            falla(new SqliteConnectionStringBuilder(cadenaConexion).DataSource) ? ["*** in database main ***\nPage 3: btreeInitPage() returns error code 11"]
                : BaseDeDatos.RevisionCompleta(cadenaConexion);
    }

    // ── Al arrancar: el respaldo en su lugar y la base de antes aparte ─────────────

    [Fact]
    public async Task Al_arrancar_pone_el_respaldo_y_aparta_la_base_de_antes_con_su_wal_y_sus_marcas_sin_borrarla()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "z-0930");
        // Después del respaldo: lo último, todavía en el -wal, y las marcas de un apagón con la base dañada.
        await MarcarSoloEnElWalAsync(p.Ruta, "después del respaldo");
        await File.WriteAllTextAsync(p.Ruta + ".en-uso", "arrancó en la mañana");
        await File.WriteAllTextAsync(p.Ruta + ".danada", "no pasó la revisión");

        var pedida = Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);
        Assert.True(pedida.Pendiente);
        Assert.Equal(TipoRespaldo.CorteZ, pedida.Tipo);
        Assert.True(File.Exists(p.Ruta + ".restaurar"));
        // Nada se tocó todavía (la base sigue con su -wal): se restaura al arrancar.
        Assert.True(File.Exists(p.Ruta + "-wal"));
        Assert.Empty(Directory.GetDirectories(p.Carpeta, "antes-de-restaurar-*"));

        // Vuelve a arrancar (otro proceso): antes de revisar la base y de migrarla.
        var alArrancar = Restauracion(p);
        alArrancar.SiSePidio();

        var deAntes = p.BaseDeAntes();
        Assert.StartsWith("antes-de-restaurar-", Path.GetFileName(deAntes));
        Assert.Equal(["pos.db", "pos.db-wal", "pos.db.danada", "pos.db.en-uso"], Directory.GetFiles(deAntes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var final in new[] { "-wal", "-shm", ".en-uso", ".danada", ".restaurando" })
            Assert.False(File.Exists(p.Ruta + final), $"Quedó {p.Ruta + final}");
        // La base es el respaldo; la de antes, con su -wal, tiene también lo último: no se perdió nada.
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(Path.Combine(deAntes, "pos.db")));
        var hecha = alArrancar.Ultima!;
        Assert.False(hecha.Pendiente);
        Assert.NotNull(hecha.HechaLocal);
        Assert.Equal(deAntes, hecha.BaseDeAntes);
        Assert.Null(hecha.Fallo);

        // La marca de la base dañada se fue con la de antes: la restaurada pasó la revisión, y al arrancar ya no se revisa ni avisa.
        var revision = new RevisionAlArrancar($"Data Source={p.Ruta};Pooling=False", p.CarpetaRespaldos, new RevisionDanada(),
            NullLogger<RevisionAlArrancar>.Instance);
        revision.Revisar();
        Assert.False(revision.SeApagoDeGolpe);
        Assert.False(revision.SeguiaDanada);
        Assert.Null(revision.Aviso);
        revision.Detenido();

        // Migra como siempre y, ya migrada, lo anota en su bitácora (con quien lo pidió) y quita el pedido.
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={p.Ruta};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db);
            await alArrancar.DespuesDeMigrarAsync(db);
        }
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using var base_ = p.Servicio.Base();
        var anotada = await base_.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("Restauró el respaldo"));
        Assert.Equal((dueno, caja, AccionControlada.ConfigurarTienda, ResultadoBitacora.Hecho), (anotada.UsuarioId!.Value, anotada.CajaId!.Value, anotada.Accion, anotada.Resultado));
        Assert.Contains($"(corte z). La base de antes quedó en {deAntes}", anotada.Detalle);
    }

    [Fact]
    public async Task Si_el_respaldo_no_pasa_la_revision_no_se_toca_la_base_y_la_marca_de_danada_se_queda()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        await MarcarAsync(p.Ruta, "después del respaldo");
        await File.WriteAllTextAsync(p.Ruta + ".danada", "no pasó la revisión");

        // Un respaldo que no pasa la revisión ni se pide.
        var noSePide = Assert.Throws<InvalidOperationException>(() =>
            Restauracion(p, new RevisionQueFalla(_ => true)).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true));
        Assert.Contains($"El respaldo {archivo} no pasó la revisión", noSePide.Message);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));

        // Pasó al pedirlo, pero no al copiarlo para restaurarlo (se dañó el disco, por ejemplo): la base se queda como estaba.
        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);
        var alArrancar = Restauracion(p, new RevisionQueFalla(ruta => ruta.EndsWith(".restaurando", StringComparison.Ordinal)));
        alArrancar.SiSePidio();

        Assert.Empty(Directory.GetDirectories(p.Carpeta, "antes-de-restaurar-*"));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
        Assert.True(File.Exists(p.Ruta + ".danada"));
        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.True(File.Exists(Path.Combine(p.CarpetaRespaldos, archivo)));
        var fallida = alArrancar.Ultima!;
        Assert.False(fallida.Pendiente);
        Assert.Null(fallida.HechaLocal);
        Assert.Null(fallida.BaseDeAntes);
        Assert.Equal($"El respaldo {archivo} no pasó la revisión (*** in database main ***\nPage 3: btreeInitPage() returns error code 11): elige otro.", fallida.Fallo);

        // Ya arrancado con la base de siempre: queda en su bitácora como negado, y se quita el pedido.
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={p.Ruta};Pooling=False")))
            await alArrancar.DespuesDeMigrarAsync(db);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using var base_ = p.Servicio.Base();
        var anotada = await base_.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("No se restauró"));
        Assert.Equal((ResultadoBitacora.Negado, (Guid?)dueno), (anotada.Resultado, anotada.UsuarioId));
    }

    [Fact]
    public async Task Si_se_corta_a_media_restauracion_al_volver_a_arrancar_sigue_en_la_misma_carpeta()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        await MarcarSoloEnElWalAsync(p.Ruta, "después del respaldo");
        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);

        // Se fue la luz justo después de apartar la base (sin su -wal todavía): el pedido dice que la copia ya estaba revisada.
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var pedido = JsonSerializer.Deserialize<PedidoDeRestauracion>(await File.ReadAllTextAsync(p.Ruta + ".restaurar"), opciones)!;
        var carpeta = Path.Combine(p.Carpeta, "antes-de-restaurar-2026-10-23_14.32.05");
        Directory.CreateDirectory(carpeta);
        File.Copy(Path.Combine(p.CarpetaRespaldos, archivo), p.Ruta + ".restaurando");
        File.Move(p.Ruta, Path.Combine(carpeta, "pos.db"));
        await File.WriteAllTextAsync(p.Ruta + ".restaurar",
            JsonSerializer.Serialize(pedido with { Paso = PasoDeRestauracion.Copiada, Carpeta = Path.GetFileName(carpeta) }, opciones));

        var alVolver = Restauracion(p);
        alVolver.SiSePidio();

        Assert.Equal(carpeta, p.BaseDeAntes());
        Assert.Equal(["pos.db", "pos.db-wal"], Directory.GetFiles(carpeta).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(Path.Combine(carpeta, "pos.db")));
        Assert.Equal(carpeta, alVolver.Ultima!.BaseDeAntes);
    }

    // ── El pedido de restaurar.ps1 ────────────────────────────────────────────

    /// <summary>
    /// Lo que deja restaurar.ps1 cuando el servicio ni arrancaba (instalacion/restaurar.ps1): la base de antes en su carpeta, el
    /// respaldo como pos.db, la marca para revisarla al arrancar y su pedido ya puesto, escrito como lo escribe PowerShell
    /// (ConvertTo-Json). El servicio la revisa, la migra, lo anota en la bitácora y deja lo pendiente del puente como «ya se intentó».
    /// </summary>
    [Fact]
    public async Task El_pedido_que_deja_restaurar_ps1_lo_termina_el_servicio_al_arrancar()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        // Dos ventas con su envío a eleventa en la bandeja del puente: una que nunca se intentó y otra que ya se intentó tres veces.
        var ventas = await DosVentasAsync(p);
        await p.DetenerAsync();
        Guid pendiente, yaIntentada;
        await using (var db = p.Servicio.Base())
        {
            var (uno, otro) = (new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventas[1], CreadoUtc = DateTime.UtcNow, Intentos = 3 });
            db.EnviosAEleventa.AddRange(uno, otro);
            await db.SaveChangesAsync();
            (pendiente, yaIntentada) = (uno.Id, otro.Id);
        }
        var archivo = p.Respaldar(Hace(45), "z-2131");
        var respaldo = Path.Combine(p.CarpetaRespaldos, archivo);

        // Como restaurar.ps1: aparta, copia, deja la marca y el pedido.
        var carpeta = Path.Combine(p.Carpeta, "antes-de-restaurar-2026-10-23_14.32.05");
        Directory.CreateDirectory(carpeta);
        File.Move(p.Ruta, Path.Combine(carpeta, "pos.db"));
        File.Copy(respaldo, p.Ruta);
        await File.WriteAllTextAsync(p.Ruta + ".danada", $"restaurar.ps1 puso el respaldo del 23/10/2026 a las 21:31 (corte Z) ({archivo}): se revisa al arrancar.");
        var ahora = DateTime.UtcNow.ToString("o");
        await File.WriteAllTextAsync(p.Ruta + ".restaurar", $$"""
            {
                "id":  "{{Guid.NewGuid()}}",
                "archivo":  "{{archivo}}",
                "tipo":  "CorteZ",
                "fechaUtc":  "{{File.GetLastWriteTimeUtc(respaldo):o}}",
                "usuarioId":  "00000000-0000-0000-0000-000000000000",
                "cajaId":  "00000000-0000-0000-0000-000000000000",
                "sucursalId":  "00000000-0000-0000-0000-000000000000",
                "pedidaUtc":  "{{ahora}}",
                "reinicioAutomatico":  true,
                "paso":  "Puesta",
                "carpeta":  "{{Path.GetFileName(carpeta)}}",
                "hechaUtc":  "{{ahora}}",
                "fallo":  null,
                "porElScript":  true
            }
            """);

        var http = p.Arrancar();
        var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var cajaId = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single().Id;
        var respaldos = await Leer<RespaldosDto>(await http.GetAsync($"api/cajas/{cajaId}/configuracion/respaldos?usuarioId={dueno.UsuarioId}"));

        // La revisó (pasó: se quitó la marca y no hay aviso), lo anotó y quitó el pedido.
        Assert.Null(estado.AvisoBase);
        Assert.False(File.Exists(p.Ruta + ".danada"));
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        Assert.Equal((archivo, false, Path.Combine(p.Carpeta, Path.GetFileName(carpeta))),
            (respaldos.Restauracion!.Archivo, respaldos.Restauracion.Pendiente, respaldos.Restauracion.BaseDeAntes));
        await using var base_ = p.Servicio.Base();
        var anotada = await base_.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("Con restaurar.ps1"));
        Assert.Null(anotada.UsuarioId);
        Assert.Equal(await base_.Sucursales.Select(s => s.Id).SingleAsync(), anotada.SucursalId);
        // Lo del puente que nunca se intentó, como «ya se intentó»: antes de mandarlo o descartarlo se busca en eleventa.
        var envios = await base_.EnviosAEleventa.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Intentos);
        Assert.Equal(1, envios[pendiente]);
        Assert.Equal(3, envios[yaIntentada]);
    }

    // ── Desde la pantalla (la API) ───────────────────────────────────────────

    [Fact]
    public async Task Desde_la_pantalla_la_caja_principal_se_reinicia_y_al_volver_ya_tiene_el_respaldo_y_otra_base_de_la_copia()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var (dueno, cajero) = (await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))),
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))));
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";
        var corte = p.Respaldar(Hace(120), "z-2131");
        var diario = p.Respaldar(Hace(60));
        var ahora = p.Respaldar(Hace(30), "ahora-1010");

        // La lista: el más reciente primero, de qué es cada uno y cuánto mide.
        var lista = await Leer<RestaurablesDto>(await http.GetAsync($"{url}/lista?usuarioId={dueno.UsuarioId}"));
        Assert.Equal([(ahora, TipoRespaldo.Ahora), (diario, TipoRespaldo.Diario), (corte, TipoRespaldo.CorteZ)],
            lista.Respaldos.Select(r => (r.Archivo, r.Tipo)));
        Assert.All(lista.Respaldos, r => Assert.Equal(new FileInfo(Path.Combine(p.CarpetaRespaldos, r.Archivo)).Length, r.Bytes));
        Assert.True(lista.ReinicioAutomatico);
        Assert.False(lista.PilotoEleventa);

        // Lo que pasó después del respaldo: un turno y una entrada de efectivo.
        var abierto = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = abierto.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(abierto);
        var entrada = Guid.NewGuid();
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Entrada, 12300, "Después del respaldo", cajero.UsuarioId, entrada)));
        var baseDeLaCopia = (await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"))).Base;

        // Se pide con el PIN: contesta enseguida, con la restauración pedida, y después de la respuesta pide el reinicio.
        var pedida = await Leer<RespaldosDto>(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", ahora)));
        Assert.Equal((ahora, true, true), (pedida.Restauracion!.Archivo, pedida.Restauracion.Pendiente, pedida.Restauracion.ReinicioAutomatico));
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, p.Reinicio.Veces);
        Assert.True(File.Exists(p.Ruta + ".restaurar"));
        await using (var db = p.Servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.StartsWith("Pidió restaurar el respaldo del") && b.UsuarioId == dueno.UsuarioId));

        // Windows lo vuelve a arrancar.
        await p.DetenerAsync();
        http = p.Arrancar();

        var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
        var despues = await Leer<RespaldosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"));
        var deAntes = p.BaseDeAntes();
        Assert.NotEqual(baseDeLaCopia, estado.Base);
        Assert.Null(estado.AvisoBase);
        Assert.Equal((ahora, false, deAntes), (despues.Restauracion!.Archivo, despues.Restauracion.Pendiente, despues.Restauracion.BaseDeAntes));
        Assert.NotNull(despues.Restauracion.HechaLocal);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using (var db = p.Servicio.Base())
        {
            // La entrada de después del respaldo ya no está; la base de antes, completa, sí la tiene.
            Assert.False(await db.MovimientosCaja.AnyAsync(m => m.Id == entrada));
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.StartsWith("Restauró el respaldo del") && b.UsuarioId == dueno.UsuarioId));
        }
        await using (var antes = new PosDbContext(BaseDeDatos.Opciones($"Data Source={Path.Combine(deAntes, "pos.db")};Pooling=False")))
            Assert.True(await antes.MovimientosCaja.AnyAsync(m => m.Id == entrada));
        // Y su respaldo, ya con lo anotado: lo más reciente de la carpeta es de la base restaurada, no de la que se dejó (revisión del
        // PR #136).
        var conElSuyo = await Leer<RestaurablesDto>(await http.GetAsync($"{url}/lista?usuarioId={dueno.UsuarioId}"));
        var alRestaurar = conElSuyo.Respaldos[0];
        Assert.Equal((TipoRespaldo.Restaurado, "Al restaurar"), (alRestaurar.Tipo, alRestaurar.Cual));
        Assert.Matches(@"^pos-\d{4}-\d{2}-\d{2}-restaurado-\d{4}\.db$", alRestaurar.Archivo);
        Assert.Equal([ahora, diario, corte], conElSuyo.Respaldos.Skip(1).Select(r => r.Archivo));
        await using (var suyo = new PosDbContext(BaseDeDatos.Opciones($"Data Source={Path.Combine(p.CarpetaRespaldos, alRestaurar.Archivo)};Pooling=False")))
        {
            Assert.True(await suyo.Bitacora.AnyAsync(b => b.Detalle.StartsWith("Restauró el respaldo del")));
            Assert.False(await suyo.MovimientosCaja.AnyAsync(m => m.Id == entrada));
        }
        // No se reinició otra vez: solo cuando se pide.
        Assert.Equal(1, p.Reinicio.Veces);
    }

    [Fact]
    public async Task Restaurar_lo_puede_quien_tiene_la_configuracion_de_la_tienda_y_vuelve_a_escribir_su_PIN()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";
        var archivo = p.Respaldar(Hace(30), "ahora-1010");
        async Task<SesionDto> EntrarAsync(string usuario, string pin) =>
            await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));

        // Sin «Configuración de la tienda» (el encargado y el cajero de prueba) ni se ve la lista ni se restaura, aunque sepa su PIN.
        foreach (var (usuario, pin) in new[] { ("encargado", "2222"), ("cajero", "1111") })
        {
            var sesion = await EntrarAsync(usuario, pin);
            await RechazoAsync(await http.GetAsync($"{url}/lista?usuarioId={sesion.UsuarioId}"), HttpStatusCode.Forbidden);
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(sesion.UsuarioId, pin, archivo)), HttpStatusCode.Forbidden);
        }

        // El dueño, con otro PIN: no, y queda en la bitácora como negado; a los cinco, espera.
        var dueno = await EntrarAsync("dueno", "3333");
        Assert.Equal("Ese no es tu PIN.", await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "1111", archivo))));
        for (var i = 2; i <= 4; i++)
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "", archivo)));
        Assert.Equal("Demasiados intentos: espera unos minutos.",
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "0000", archivo))));
        // Ya bloqueado, ni el bueno pasa (como al entrar).
        Assert.Equal("Demasiados intentos: espera unos minutos.",
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo))));

        await using (var db = p.Servicio.Base())
        {
            // Cuatro equivocados; el quinto ya bloquea, y el bueno mientras sigue bloqueado tampoco pasa.
            Assert.Equal(4, await db.Bitacora.CountAsync(b => b.UsuarioId == dueno.UsuarioId && b.Resultado == ResultadoBitacora.Negado
                && b.Detalle == $"PIN incorrecto al restaurar el respaldo {archivo}"));
            Assert.Equal(2, await db.Bitacora.CountAsync(b => b.UsuarioId == dueno.UsuarioId && b.Resultado == ResultadoBitacora.Negado
                && b.Detalle == $"Demasiados intentos de PIN al restaurar el respaldo {archivo}"));
            Assert.Equal(2, await db.Bitacora.CountAsync(b => b.Detalle == "Intento sin permiso" && b.Accion == AccionControlada.ConfigurarTienda));
            // Con el PIN bueno, pasado el bloqueo (como si hubieran pasado los cinco minutos).
            var usuario = await db.Usuarios.SingleAsync(u => u.Id == dueno.UsuarioId);
            usuario.EstablecerPin("3333");
            await db.SaveChangesAsync();
        }

        // Solo un respaldo de la carpeta, por su nombre.
        Assert.Contains("no es un respaldo de la carpeta de respaldos",
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", "../pos.db"))));
        Assert.Equal($"Ya no está el respaldo pos-2020-01-01.db en {p.CarpetaRespaldos}.",
            await RechazoAsync(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", "pos-2020-01-01.db"))));
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        Assert.Equal(0, p.Reinicio.Veces);

        // Y ya con su PIN, sí.
        var pedida = await Leer<RespaldosDto>(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        Assert.True(pedida.Restauracion!.Pendiente);
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// La caja del navegador (desarrollo) no corre como servicio de Windows: nadie la vuelve a arrancar. Lo dice, se restaura la
    /// próxima vez que se arranque y, mientras, se puede cancelar.
    /// </summary>
    [Fact]
    public async Task Sin_servicio_de_Windows_se_restaura_al_volver_a_arrancar_y_mientras_se_puede_cancelar()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync(automatico: false);
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "ahora-1010");
        await MarcarAsync(p.Ruta, "después del respaldo");

        Assert.False((await Leer<RestaurablesDto>(await http.GetAsync($"{url}/lista?usuarioId={dueno.UsuarioId}"))).ReinicioAutomatico);
        var pedida = await Leer<RespaldosDto>(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        Assert.Equal((true, false), (pedida.Restauracion!.Pendiente, pedida.Restauracion.ReinicioAutomatico));
        // Lo dice al volver a abrir la Configuración; nadie lo reinicia.
        Assert.True((await Leer<RespaldosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"))).Restauracion!.Pendiente);

        var cancelada = await Leer<RespaldosDto>(await http.PostAsJsonAsync($"{url}/restaurar/cancelar", new SolicitudRespaldo(dueno.UsuarioId)));
        Assert.Null(cancelada.Restauracion);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using (var db = p.Servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.StartsWith("Ya no se restaura el respaldo del") && b.UsuarioId == dueno.UsuarioId));

        // Se pide otra vez y se cierra la caja del navegador: al abrirla, ya restaurado.
        await Leer<RespaldosDto>(await http.PostAsJsonAsync($"{url}/restaurar", new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        await p.DetenerAsync();
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
        http = p.Arrancar();
        Assert.False((await Leer<RespaldosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"))).Restauracion!.Pendiente);
        await p.DetenerAsync();
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(0, p.Reinicio.Veces);
    }

    // ── Las cajas después de restaurar ───────────────────────────────────────

    /// <summary>
    /// El turno de la caja se abrió después del respaldo, vendió en línea, la caja principal se reinició para restaurar y mientras la
    /// caja siguió vendiendo sin conexión. Al volver: la caja nota la base nueva, baja su copia completa, vuelve a dar de alta su
    /// turno (con su mismo Id) y su último folio, y lo que vendió sin conexión entra en ese turno, una sola vez. La venta en línea de
    /// después del respaldo se perdió (lo dice la advertencia); su folio no se vuelve a dar.
    /// </summary>
    [Fact]
    public async Task Despues_de_restaurar_las_cajas_bajan_la_copia_completa_y_lo_que_tenian_por_subir_entra_en_su_turno()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var datos = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        // Sin turno abierto en el respaldo (como el diario, al prender la PC).
        if (await http.GetAsync($"api/cajas/{datos.Id}/turno") is { StatusCode: HttpStatusCode.OK } yaHabia)
        {
            var abierto = await Leer<TurnoDto>(yaHabia);
            await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{abierto.Id}/corte-z", new SolicitudCorteZ(dueno.UsuarioId, 0)));
        }
        var archivo = p.Respaldar(Hace(10), "ahora-1010");

        var baseLocal = Path.Combine(p.Carpeta, "caja-local.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = datos.Id };
        var deLaCaja = new HttpClient(p.ParaLasCajas, disposeHandler: false);
        var contingencia = new Contingencia(new AlmacenLocal(baseLocal), deLaCaja, conexion);
        try
        {
            var cliente = new ClienteCaja(deLaCaja, conexion, contingencia);
            await contingencia.LatidoAsync();
            var baseAntes = contingencia.Paquete!.Base;
            var cajero = await cliente.EntrarAsync("cajero", "1111");
            var turno = await cliente.AbrirTurnoAsync(datos.Id, cajero.UsuarioId, 50000);
            var busqueda = (await cliente.BuscarAsync("pluma punto fino", datos.SucursalId)).First(r => r.Codigo is not null);
            var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, datos.SucursalId))!;
            SolicitudCobro Cobro(int piezas) => new(datos.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, piezas * 1000L, pluma.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 100000)]);
            var enLinea = await cliente.CobrarAsync(Cobro(1));

            // El dueño restaura el respaldo de antes del turno; la caja principal se detiene.
            await Leer<RespaldosDto>(await http.PostAsJsonAsync($"api/cajas/{datos.Id}/configuracion/respaldos/restaurar",
                new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
            await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
            await p.DetenerAsync();

            // Mientras se reinicia, la caja sigue vendiendo sin conexión.
            await contingencia.LatidoAsync();
            Assert.False(contingencia.EnLinea);
            var sinConexion = await cliente.CobrarAsync(Cobro(2));
            Assert.Equal(1, contingencia.Pendientes);

            // Vuelve, ya con el respaldo: un latido basta.
            http = p.Arrancar();
            var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
            await contingencia.LatidoAsync();

            Assert.True(contingencia.EnLinea);
            Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
            // La copia completa, de la base nueva (la de antes ya no sirve para pedir cambios).
            Assert.NotEqual(baseAntes, estado.Base);
            Assert.Equal(estado.Base, contingencia.Paquete!.Base);
            await using (var db = p.Servicio.Base())
            {
                // El turno otra vez, con su Id y su fondo; adentro, lo vendido sin conexión, y no la venta en línea perdida.
                var otraVez = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == turno.Id);
                Assert.Equal((50000L, true, (DateTime?)null), (otraVez.FondoCentavos, otraVez.AbiertoEnContingencia, otraVez.CerradoUtc));
                var ventas = await db.Ventas.AsNoTracking().Where(v => v.TurnoId == turno.Id).Select(v => v.Id).ToListAsync();
                Assert.Equal([sinConexion.VentaId!.Value], ventas);
                Assert.False(await db.Ventas.AnyAsync(v => v.Id == enLinea.VentaId));
            }
            // Lo que subió, otra vez (si se hubiera perdido la respuesta, la caja lo volvería a mandar): entra una sola vez.
            var (ruta, json) = await DeLaBandejaAsync(baseLocal, sinConexion.VentaId!.Value);
            Assert.Equal(HttpStatusCode.NoContent,
                (await http.PostAsync(ruta, new StringContent(json, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            await using (var db = p.Servicio.Base())
                Assert.Equal(1, await db.Ventas.CountAsync(v => v.Id == sinConexion.VentaId));

            // La serie sigue después de lo que ya imprimió la caja: el folio de la venta perdida no se vuelve a dar.
            var siguiente = await cliente.CobrarAsync(Cobro(1));
            Assert.Equal(Folio(sinConexion) + 1, Folio(siguiente));
            Assert.True(Folio(enLinea) < Folio(sinConexion));
            Assert.Equal(turno.Id, (await cliente.TurnoAsync(datos.Id))!.Id);
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>Por donde pasa lo que la caja le manda a la caja principal: la prueba ve cada llamada al salir.</summary>
    private sealed class Mirilla(HttpMessageHandler interior) : DelegatingHandler(interior)
    {
        public Action<HttpRequestMessage>? AlMandar { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AlMandar?.Invoke(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Revisión del PR #136. La caja no cobró nada mientras la caja principal se reiniciaba (nada por subir): al volver, le dice su último
    /// folio antes de volver a cobrar en línea. Antes se lo decía ya en línea, y un cobro en ese momento podía llevar el folio de un ticket
    /// que ya salió impreso: la serie del respaldo va atrás.
    /// </summary>
    [Fact]
    public async Task Al_volver_la_caja_dice_su_ultimo_folio_antes_de_cobrar_en_linea()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar();
        var datos = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        if (await http.GetAsync($"api/cajas/{datos.Id}/turno") is { StatusCode: HttpStatusCode.OK } yaHabia)
            await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{(await Leer<TurnoDto>(yaHabia)).Id}/corte-z", new SolicitudCorteZ(dueno.UsuarioId, 0)));
        var archivo = p.Respaldar(Hace(10), "ahora-1010");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = datos.Id };
        var mirilla = new Mirilla(p.ParaLasCajas);
        var deLaCaja = new HttpClient(mirilla, disposeHandler: false);
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(p.Carpeta, "caja-local.db")), deLaCaja, conexion);
        try
        {
            var cliente = new ClienteCaja(deLaCaja, conexion, contingencia);
            await contingencia.LatidoAsync();
            var cajero = await cliente.EntrarAsync("cajero", "1111");
            var turno = await cliente.AbrirTurnoAsync(datos.Id, cajero.UsuarioId, 50000);
            var busqueda = (await cliente.BuscarAsync("pluma punto fino", datos.SucursalId)).First(r => r.Codigo is not null);
            var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, datos.SucursalId))!;
            SolicitudCobro Cobro() => new(datos.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, 1000L, pluma.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 100000)]);
            var impreso = await cliente.CobrarAsync(Cobro());
            await Leer<RespaldosDto>(await http.PostAsJsonAsync($"api/cajas/{datos.Id}/configuracion/respaldos/restaurar",
                new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
            await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
            await p.DetenerAsync();
            await contingencia.LatidoAsync();
            Assert.False(contingencia.EnLinea);

            await Leer<EstadoServicioDto>(await p.Arrancar().GetAsync("api/estado"));
            var enLineaAlAvisar = new List<bool>();
            mirilla.AlMandar = r =>
            {
                if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/folio-usado", StringComparison.Ordinal))
                    lock (enLineaAlAvisar)
                        enLineaAlAvisar.Add(contingencia.EnLinea);
            };
            await contingencia.LatidoAsync();

            Assert.True(contingencia.EnLinea);
            Assert.False(Assert.Single(enLineaAlAvisar));
            Assert.Equal(Folio(impreso) + 1, Folio(await cliente.CobrarAsync(Cobro())));
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>Lo que pasó en el escenario del turno de la mañana y el de la tarde (ver <see cref="MananaYTardeAsync"/>).</summary>
    private sealed record MananaYTarde(CajaDto Caja, SesionDto Dueno, TurnoDto Manana, TurnoDto Tarde, TicketDto SinConexion, Func<Guid, int, SolicitudCobro> Cobro);

    /// <summary>
    /// El turno de la mañana se abre antes del respaldo y vende; después del respaldo se hace su corte Z y se abre el de la tarde, que
    /// vende en línea. Se restaura el respaldo y, mientras la caja principal se reinicia, la caja cobra sin conexión en el turno de la
    /// tarde. La caja principal vuelve (en el respaldo sigue abierto el de la mañana) y la caja da un latido.
    /// </summary>
    private static async Task<MananaYTarde> MananaYTardeAsync(PrincipalQueSeRestaura p, ConexionCaja conexion, Contingencia contingencia, ClienteCaja cliente)
    {
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        conexion.CajaId = caja.Id;
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        if (await http.GetAsync($"api/cajas/{caja.Id}/turno") is { StatusCode: HttpStatusCode.OK } yaHabia)
            await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{(await Leer<TurnoDto>(yaHabia)).Id}/corte-z", new SolicitudCorteZ(dueno.UsuarioId, 0)));
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var manana = await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
        var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
        SolicitudCobro Cobro(Guid turnoId, int piezas) => new(caja.Id, turnoId, cajero.UsuarioId,
            [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, piezas * 1000L, pluma.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 100000)]);
        await cliente.CobrarAsync(Cobro(manana.Id, 1));
        var archivo = p.Respaldar(Hace(10), "ahora-1010");

        await cliente.CorteZAsync(manana.Id, new SolicitudCorteZ(dueno.UsuarioId, 51000));
        var tarde = await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 30000);
        await cliente.CobrarAsync(Cobro(tarde.Id, 1));

        await Leer<RespaldosDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/configuracion/respaldos/restaurar",
            new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
        await p.DetenerAsync();
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);
        var sinConexion = await cliente.CobrarAsync(Cobro(tarde.Id, 2));

        // Vuelve (lo primero que contesta ya está listo: con la PC ocupada, el latido no se pasa de su plazo) y la caja da un latido.
        await Leer<EstadoServicioDto>(await p.Arrancar().GetAsync("api/estado"));
        await contingencia.LatidoAsync();
        return new MananaYTarde(caja, dueno, manana, tarde, sinConexion, Cobro);
    }

    /// <summary>
    /// Revisión del PR #136. En el respaldo sigue abierto el turno de la mañana: la caja principal no deja volver a dar de alta el de la
    /// tarde. Antes la caja se pasaba al de la mañana y el de la tarde se olvidaba, con lo que se cobró en él «con error» para siempre.
    /// Ahora la caja lo guarda aparte y la franja dice qué hacer; con el corte Z de la mañana (otra vez), el de la tarde se da de alta
    /// solo, con su mismo Id y su fondo, sube lo que se cobró en él sin conexión y se sigue cobrando en él.
    /// </summary>
    [Fact]
    public async Task El_turno_abierto_despues_del_respaldo_no_se_olvida_aunque_en_el_respaldo_siga_abierto_el_de_antes()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var deLaCaja = new HttpClient(p.ParaLasCajas, disposeHandler: false);
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(p.Carpeta, "caja-local.db")), deLaCaja, conexion);
        try
        {
            var cliente = new ClienteCaja(deLaCaja, conexion, contingencia);
            var e = await MananaYTardeAsync(p, conexion, contingencia, cliente);

            var sinAlta = contingencia.TurnoPorDarDeAlta(e.Caja.Id);
            Assert.Equal(e.Tarde.Id, sinAlta?.Turno.Id);
            Assert.Contains("ya tiene otro turno abierto", sinAlta!.Porque);
            Assert.Contains($"Se restauró un respaldo de antes del turno de las {e.Tarde.AbiertoLocal:HH:mm}: la caja principal no lo tiene", contingencia.AvisoTurnoSinAlta);
            Assert.EndsWith($"Haz el corte Z de ese otro turno (el de antes del respaldo): después, el de las {e.Tarde.AbiertoLocal:HH:mm} se da de alta solo "
                + "y sube lo que se cobró en él.", contingencia.AvisoTurnoSinAlta);
            Assert.Equal(1, contingencia.ConError);
            // En el piloto, ese otro turno lo cierra el corte de eleventa: la franja lo dice así.
            await contingencia.RecordarPilotoEleventaAsync(true);
            Assert.Contains("En el piloto ese otro turno lo cierra el corte de eleventa: en cuanto la caja principal lo lea", contingencia.AvisoTurnoSinAlta);
            await contingencia.RecordarPilotoEleventaAsync(false);

            // La pantalla ve el turno que tiene la caja principal (el de la mañana, sin su corte, como en el respaldo); el de la tarde sigue
            // esperando.
            Assert.Equal(e.Manana.Id, (await cliente.TurnoAsync(e.Caja.Id))?.Id);
            Assert.Equal(e.Tarde.Id, contingencia.TurnoPorDarDeAlta(e.Caja.Id)?.Turno.Id);

            // El corte Z de la mañana, otra vez: el de la tarde se da de alta solo y sube lo que se cobró en él.
            await cliente.CorteZAsync(e.Manana.Id, new SolicitudCorteZ(e.Dueno.UsuarioId, 51000));
            Assert.Equal(e.Tarde.Id, (await cliente.TurnoAsync(e.Caja.Id))?.Id);
            Assert.Null(contingencia.TurnoPorDarDeAlta(e.Caja.Id));
            Assert.Null(contingencia.AvisoTurnoSinAlta);
            Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
            await using (var db = p.Servicio.Base())
            {
                var deLaTarde = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == e.Tarde.Id);
                Assert.Equal((30000L, true, (DateTime?)null), (deLaTarde.FondoCentavos, deLaTarde.AbiertoEnContingencia, deLaTarde.CerradoUtc));
                Assert.Equal(e.Tarde.Id, (await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == e.SinConexion.VentaId)).TurnoId);
            }
            await cliente.CobrarAsync(e.Cobro(e.Tarde.Id, 1));
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Igual, pero el turno de antes lo cierra la caja principal sola (en el piloto, la lectura del corte de eleventa): el latido vuelve a
    /// intentar dar de alta el de la tarde, una vez por minuto, y en cuanto puede la caja sigue en él con lo que se cobró.
    /// </summary>
    [Fact]
    public async Task Cuando_la_caja_principal_cierra_sola_el_turno_de_antes_el_latido_da_de_alta_el_de_despues()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var reloj = new PapeleriaPos.Pruebas.Interfaz.RelojDeLaRed(DateTimeOffset.UtcNow);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var deLaCaja = new HttpClient(p.ParaLasCajas, disposeHandler: false);
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(p.Carpeta, "caja-local.db")), deLaCaja, conexion, reloj);
        try
        {
            var cliente = new ClienteCaja(deLaCaja, conexion, contingencia);
            var e = await MananaYTardeAsync(p, conexion, contingencia, cliente);
            Assert.Equal(e.Manana.Id, (await cliente.TurnoAsync(e.Caja.Id))?.Id);
            Assert.Equal(e.Tarde.Id, contingencia.TurnoPorDarDeAlta(e.Caja.Id)?.Turno.Id);

            // La caja principal cierra sola el de la mañana.
            await using (var db = p.Servicio.Base())
                await db.Turnos.Where(t => t.Id == e.Manana.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CerradoUtc, DateTime.UtcNow));
            // Antes del minuto no lo vuelve a intentar.
            await contingencia.LatidoAsync();
            Assert.NotNull(contingencia.TurnoPorDarDeAlta(e.Caja.Id));
            reloj.Avanzar(TimeSpan.FromMinutes(1));
            await contingencia.LatidoAsync();

            Assert.Null(contingencia.TurnoPorDarDeAlta(e.Caja.Id));
            Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
            Assert.Equal(e.Tarde.Id, contingencia.Turno(e.Caja.Id)?.Id);
            await using (var db = p.Servicio.Base())
                Assert.Equal(e.Tarde.Id, (await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == e.SinConexion.VentaId)).TurnoId);
            Assert.Equal(e.Tarde.Id, (await cliente.TurnoAsync(e.Caja.Id))?.Id);
        }
        finally
        {
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
        }
    }

    internal static int Folio(TicketDto t) => int.Parse(t.Folio[(t.Folio.IndexOf('-') + 1)..]);

    /// <summary>Dos ventas de una pluma en la Caja 1, con su turno (la base de prueba trae el historial, no ventas de la caja).</summary>
    internal static async Task<List<Guid>> DosVentasAsync(PrincipalQueSeRestaura p)
    {
        var http = p.Arrancar();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var abierto = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = abierto.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(abierto);
        Guid variante;
        await using (var db = p.Servicio.Base())
            variante = await db.Variantes.Where(v => db.Productos.Any(x => x.Id == v.ProductoId && x.Nombre.StartsWith("Pluma punto fino")))
                .OrderBy(v => v.Id).Select(v => v.Id).FirstAsync();
        var ventas = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
                [new LineaSolicitada(variante, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 100000)])));
            ventas.Add(ticket.VentaId!.Value);
        }
        return ventas;
    }

    /// <summary>Lo que la caja mandó de la bandeja (su ruta y su cuerpo), leído de su base local mientras la caja la tiene abierta.</summary>
    internal static async Task<(string Ruta, string Json)> DeLaBandejaAsync(string baseLocal, Guid id)
    {
        await using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = baseLocal, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Ruta, Json FROM Bandeja WHERE Id = $id";
        comando.Parameters.AddWithValue("$id", id.ToString());
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync(), "No está en la bandeja.");
        return (lector.GetString(0), lector.GetString(1));
    }
}
