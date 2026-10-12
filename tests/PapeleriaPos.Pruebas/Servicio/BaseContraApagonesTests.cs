using System.IO.Pipelines;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Una revisión rápida que encuentra una página dañada, como la que deja un apagón a media escritura.</summary>
internal sealed class RevisionDanada : IRevisionRapida
{
    public int Veces { get; private set; }

    public IReadOnlyList<string> Revisar(string cadenaConexion)
    {
        Veces++;
        return ["*** in database main ***\nPage 7: btreeInitPage() returns error code 11"];
    }
}

/// <summary>Recién vuelta la luz: el módem todavía no tiene internet.</summary>
internal sealed class TelegramSinInternet : ITelegram
{
    public int Intentos { get; private set; }

    public bool Configurado => true;

    public Task<string?> NombreBotAsync(CancellationToken ct) => throw new TelegramException("Sin conexión con Telegram", permanente: false);

    public Task<long> EnviarAsync(long chatId, string texto, IReadOnlyList<BotonTelegram>? botones, CancellationToken ct)
    {
        Intentos++;
        throw new TelegramException("Sin conexión con Telegram", permanente: false);
    }

    public Task EditarAsync(long chatId, long mensajeId, string texto, CancellationToken ct) => throw new TelegramException("Sin conexión con Telegram", permanente: false);

    public Task ContestarBotonAsync(string callbackId, string texto, CancellationToken ct) => throw new TelegramException("Sin conexión con Telegram", permanente: false);

    public Task<IReadOnlyList<ActualizacionTelegram>> LeerAsync(long desde, int esperaSegundos, CancellationToken ct) =>
        throw new TelegramException("Sin conexión con Telegram", permanente: false);
}

/// <summary>Lo que se escribió en el registro, para revisarlo.</summary>
internal sealed class RegistroDePrueba<T> : ILogger<T>
{
    public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entradas.Add((logLevel, formatter(state, exception)));

    public IEnumerable<string> De(LogLevel nivel) => Entradas.Where(e => e.Nivel == nivel).Select(e => e.Mensaje);
}

/// <summary>La base contra apagones (entrega 10): los ajustes de cada conexión y la revisión al arrancar.</summary>
public sealed class BaseContraApagonesTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"pos-apagones-{Guid.NewGuid():N}");

    public BaseContraApagonesTests() => Directory.CreateDirectory(_carpeta);

    private string Ruta => Path.Combine(_carpeta, "pos.db");
    private string Cadena => $"Data Source={Ruta};Pooling=False";
    private string CarpetaRespaldos => Path.Combine(_carpeta, "respaldos");

    /// <summary>Si una migración se queda esperando el candado, la prueba falla en lugar de quedarse colgada.</summary>
    private static readonly TimeSpan Plazo = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_carpeta, recursive: true);
    }

    private async Task CrearBaseAsync()
    {
        await using var db = new PosDbContext(BaseDeDatos.Opciones(Cadena));
        await BaseDeDatos.PrepararAsync(db);
    }

    private RevisionAlArrancar Revision(IRevisionRapida? revision = null, ILogger<RevisionAlArrancar>? registro = null, ITelegram? telegram = null) =>
        new(Cadena, CarpetaRespaldos, revision ?? new RevisionRapidaSqlite(), registro ?? NullLogger<RevisionAlArrancar>.Instance, telegram);

    /// <summary>Basura donde estaba una página de la base (un apagón a media escritura).</summary>
    private static void DanarPagina(string ruta, long pagina)
    {
        using var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Write);
        archivo.Seek((pagina - 1) * 4096, SeekOrigin.Begin);
        archivo.Write(Enumerable.Repeat((byte)0xFF, 4096).ToArray());
    }

    [Fact]
    public async Task Cada_conexion_de_la_base_del_servicio_guarda_completo_y_espera_si_otra_escribe()
    {
        await CrearBaseAsync();

        // Una conexión nueva (sin el pool), abierta por EF Core como cualquier consulta.
        await using var db = new PosDbContext(BaseDeDatos.Opciones(Cadena));
        await db.Database.OpenConnectionAsync();
        var conexion = db.Database.GetDbConnection();
        await using var sincronizacion = conexion.CreateCommand();
        sincronizacion.CommandText = "PRAGMA synchronous";
        await using var espera = conexion.CreateCommand();
        espera.CommandText = "PRAGMA busy_timeout";

        Assert.Equal(AjustesContraApagones.SincronizacionCompleta, Convert.ToInt32(await sincronizacion.ExecuteScalarAsync()));
        Assert.Equal(AjustesContraApagones.EsperaSiEstaOcupadaMs, Convert.ToInt32(await espera.ExecuteScalarAsync()));
        Assert.Null(await BaseDeDatos.RevisarAjustesAsync(db));
    }

    [Fact]
    public async Task Si_una_conexion_no_quedo_en_full_la_revision_lo_dice()
    {
        // Una conexión que EF Core no abrió (como la de las pruebas en memoria) se queda como venga.
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "PRAGMA synchronous=NORMAL";
            await comando.ExecuteNonQueryAsync();
        }
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);

        var problema = await BaseDeDatos.RevisarAjustesAsync(db);

        Assert.NotNull(problema);
        Assert.Contains("synchronous quedó en 1 y no en FULL", problema);
        Assert.Contains("busy_timeout quedó en 0 ms", problema);
    }

    [Fact]
    public async Task Si_los_ajustes_no_quedaron_al_arrancar_va_un_error_al_registro()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "PRAGMA synchronous=NORMAL";
            await comando.ExecuteNonQueryAsync();
        }
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var registro = new RegistroDePrueba<RevisionAlArrancar>();
        var revision = new RevisionAlArrancar("Data Source=:memory:", CarpetaRespaldos, new RevisionRapidaSqlite(), registro);

        revision.Revisar();
        await revision.PrepararAsync(db);

        Assert.Null(revision.Marca);
        Assert.Contains(registro.De(LogLevel.Error), m => m.Contains("no quedó con los ajustes contra apagones") && m.Contains("synchronous quedó en 1"));
    }

    [Fact]
    public void La_base_local_de_la_caja_usa_los_mismos_ajustes()
    {
        var almacen = new AlmacenLocal(Path.Combine(_carpeta, "caja-local.db"));
        var contingencia = new Contingencia(almacen, new HttpClient(), new ConexionCaja { Servicio = new Uri("http://localhost/") });
        SqliteConnection.ClearAllPools();

        Assert.Equal((AjustesContraApagones.SincronizacionCompleta, AjustesContraApagones.EsperaSiEstaOcupadaMs), almacen.Ajustes());
        Assert.Null(almacen.ProblemaDeAjustes);
        Assert.Null(contingencia.AvisoBaseLocal);
        Assert.Null(AjustesContraApagones.Problema(AjustesContraApagones.SincronizacionCompleta, AjustesContraApagones.EsperaSiEstaOcupadaMs));
        Assert.Contains("synchronous quedó en 1", AjustesContraApagones.Problema(1, AjustesContraApagones.EsperaSiEstaOcupadaMs));
    }

    [Fact]
    public async Task Si_se_detuvo_bien_no_revisa_y_la_marca_solo_esta_mientras_corre()
    {
        await CrearBaseAsync();
        var danada = new RevisionDanada();

        var primera = Revision(danada);
        primera.Revisar();
        Assert.True(File.Exists(primera.Marca));
        primera.Detenido();
        var segunda = Revision(danada);
        segunda.Revisar();

        Assert.Equal(Ruta + ".en-uso", segunda.Marca);
        Assert.False(segunda.SeApagoDeGolpe);
        Assert.Null(segunda.Revision);
        Assert.Equal(0, danada.Veces);
        Assert.Null(segunda.Aviso);
        segunda.Detenido();
        Assert.False(File.Exists(segunda.Marca));
    }

    [Fact]
    public async Task Despues_de_apagarse_de_golpe_revisa_la_base_y_si_esta_sana_sigue_sin_aviso()
    {
        await CrearBaseAsync();
        // Arrancó y se fue la luz: nunca se detuvo.
        Revision().Revisar();

        var despues = Revision();
        despues.Revisar();

        Assert.True(despues.SeApagoDeGolpe);
        Assert.Equal(["ok"], despues.Revision);
        Assert.False(despues.BaseDanada);
        Assert.Null(despues.Aviso);
        Assert.True(File.Exists(despues.Marca));
        Assert.False(File.Exists(despues.MarcaDanada));
    }

    [Fact]
    public async Task Si_la_base_sigue_danada_vuelve_a_revisar_y_avisar_aunque_se_haya_detenido_bien_hasta_que_se_restaura()
    {
        await CrearBaseAsync();
        var danada = new RevisionDanada();
        // Arrancó y se fue la luz; al volver, la base no pasó la revisión.
        Revision().Revisar();
        var alVolver = Revision(danada);
        alVolver.Revisar();
        Assert.True(alVolver.BaseDanada);
        Assert.True(File.Exists(alVolver.MarcaDanada));

        // En la noche se apagó la PC como siempre (se detuvo bien) y a la mañana siguiente vuelve a arrancar.
        alVolver.Detenido();
        Assert.False(File.Exists(alVolver.Marca));
        var otroDia = Revision(danada);
        otroDia.Revisar();

        Assert.False(otroDia.SeApagoDeGolpe);
        Assert.True(otroDia.SeguiaDanada);
        Assert.True(otroDia.BaseDanada);
        Assert.StartsWith(RevisionAlArrancar.AvisoBaseDanada, otroDia.Aviso);
        Assert.Equal(2, danada.Veces);

        // Se restauró el respaldo: la revisión sale bien, se quita la marca y el aviso, y ya no se revisa al arrancar.
        otroDia.Detenido();
        var restaurada = Revision();
        restaurada.Revisar();
        Assert.True(restaurada.SeguiaDanada);
        Assert.Equal(["ok"], restaurada.Revision);
        Assert.Null(restaurada.Aviso);
        Assert.False(File.Exists(restaurada.MarcaDanada));
        restaurada.Detenido();
        var siguiente = Revision(danada);
        siguiente.Revisar();
        Assert.Null(siguiente.Revision);
        Assert.Equal(2, danada.Veces);
    }

    [Fact]
    public async Task Si_se_corto_a_media_migracion_el_candado_que_quedo_no_detiene_el_arranque()
    {
        await CrearBaseAsync();
        // Arrancó, empezó a migrar y se fue la luz: quedó la marca y el candado de las migraciones de EF Core.
        Revision().Revisar();
        await using (var conexion = new SqliteConnection(Cadena))
        {
            await conexion.OpenAsync();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-10-11 21:00:00+00:00')";
            await comando.ExecuteNonQueryAsync();
        }
        var registro = new RegistroDePrueba<RevisionAlArrancar>();
        var revision = Revision(registro: registro);

        revision.Revisar();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena)))
            await revision.PrepararAsync(db).WaitAsync(Plazo);

        Assert.False(revision.BaseDanada);
        Assert.Contains(registro.De(LogLevel.Warning), m => m.Contains("candado de las migraciones"));
        Assert.False(BaseDeDatos.QuitarCandadoDeMigraciones(Cadena));
    }

    [Fact]
    public async Task Con_la_base_danada_no_se_hacen_respaldos_ni_del_dia_ni_del_corte_y_se_guarda_el_de_antes_del_apagon()
    {
        await CrearBaseAsync();
        var deAntes = Respaldos.Hacer(Cadena, CarpetaRespaldos, new DateTime(2026, 10, 11, 21, 30, 0), "z-2130");
        File.SetLastWriteTimeUtc(deAntes, DateTime.UtcNow.AddDays(-1));
        Revision().Revisar();
        var revision = Revision(new RevisionDanada());
        revision.Revisar();
        var respaldos = new ServicioRespaldos(Cadena, CarpetaRespaldos, 30, NullLogger<ServicioRespaldos>.Instance) { Revision = revision };

        // La copia del día al arrancar y la del corte Z.
        respaldos.DiarioSiFalta();
        respaldos.Hacer("z-2105");

        Assert.Equal([deAntes], Directory.GetFiles(CarpetaRespaldos));
        Assert.Equal(deAntes, revision.Respaldo);
        Assert.Contains("restaurar el respaldo de antes del apagón (pos-2026-10-11-z-2130.db)", revision.Aviso);

        // Con la base sana (o ya restaurada), las copias siguen como siempre.
        var sana = new ServicioRespaldos(Cadena, CarpetaRespaldos, 30, NullLogger<ServicioRespaldos>.Instance) { Revision = Revision() };
        sana.Hacer("z-2105");
        Assert.Equal(2, Directory.GetFiles(CarpetaRespaldos).Length);
    }

    [Fact]
    public void Una_base_danada_no_pasa_la_revision_rapida()
    {
        var ruta = Path.Combine(_carpeta, "danada.db");
        var cadena = $"Data Source={ruta};Pooling=False";
        using (var conexion = new SqliteConnection(cadena))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = """
                CREATE TABLE Ventas (Id INTEGER PRIMARY KEY, Detalle TEXT NOT NULL);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
                INSERT INTO Ventas (Detalle) SELECT printf('Venta %d con su detalle para llenar varias páginas de la base', i) FROM n;
                """;
            comando.ExecuteNonQuery();
        }
        Assert.Equal(["ok"], BaseDeDatos.RevisionRapida(cadena));
        var original = File.ReadAllBytes(ruta);

        // Una página de renglones con un apuntador a medio escribir: la revisión contesta con lo que encontró.
        var bytes = (byte[])original.Clone();
        var hoja = Enumerable.Range(1, bytes.Length / 4096 - 1).Select(p => p * 4096).First(inicio => bytes[inicio] == 0x0D);
        bytes[hoja + 8] = 0xFF;
        bytes[hoja + 9] = 0xF0;
        File.WriteAllBytes(ruta, bytes);
        var apuntador = BaseDeDatos.RevisionRapida(cadena);
        // Una página entera de basura: SQLite ni siquiera termina la revisión y contesta con el error.
        File.WriteAllBytes(ruta, original);
        DanarPagina(ruta, 5);
        var pagina = BaseDeDatos.RevisionRapida(cadena);
        // Y un archivo que ni siquiera es una base.
        var basura = Path.Combine(_carpeta, "basura.db");
        File.WriteAllBytes(basura, Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray());
        var deBasura = BaseDeDatos.RevisionRapida($"Data Source={basura};Pooling=False");

        Assert.False(BaseDeDatos.EstaSana(apuntador));
        Assert.Contains(apuntador, r => r.Contains("out of range"));
        Assert.False(BaseDeDatos.EstaSana(pagina));
        Assert.NotEmpty(pagina);
        Assert.False(BaseDeDatos.EstaSana(deBasura));
        Assert.Contains("not a database", Assert.Single(deBasura));
    }

    [Fact]
    public async Task Si_ni_se_puede_migrar_no_arranca_y_avisa_directo_por_telegram_una_vez_al_dia()
    {
        await CrearBaseAsync();
        long historial;
        await using (var conexion = new SqliteConnection(Cadena))
        {
            await conexion.OpenAsync();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = $"""
                PRAGMA foreign_keys=OFF;
                INSERT INTO ChatsTelegram (Id, Activo, ChatId, Nombre, UsuarioId, VinculadoUtc)
                VALUES ('{Guid.NewGuid()}', 1, 7001, 'Dueño', '{Guid.NewGuid()}', '2026-10-01 10:00:00');
                SELECT rootpage FROM sqlite_master WHERE name = '__EFMigrationsHistory';
                """;
            historial = Convert.ToInt64(await comando.ExecuteScalarAsync());
        }
        // Arrancó y se fue la luz a media escritura del historial de migraciones: la base ya no se puede migrar.
        Revision().Revisar();
        DanarPagina(Ruta, historial);

        // Recién vuelta la luz no hay internet: no se puede avisar y no se anota.
        var sinInternet = new TelegramSinInternet();
        var registro = new RegistroDePrueba<RevisionAlArrancar>();
        var primera = Revision(registro: registro, telegram: sinInternet);
        primera.Revisar();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena)))
            await Assert.ThrowsAsync<SqliteException>(() => primera.PrepararAsync(db).WaitAsync(Plazo));
        Assert.True(primera.BaseDanada);
        Assert.Contains(registro.De(LogLevel.Critical), m => m.Contains("no puede arrancar"));
        Assert.Contains(registro.De(LogLevel.Warning), m => m.Contains("No se pudo avisar por Telegram al chat 7001"));
        Assert.Equal(1, sinInternet.Intentos);
        Assert.DoesNotContain("avisado por Telegram", await File.ReadAllTextAsync(primera.MarcaDanada!));

        // Windows lo vuelve a arrancar en un minuto, ya con internet: avisa directo, sin la cola. (La migración fallida dejó
        // puesto el candado de EF Core: sin quitarlo, esta esperaría para siempre.)
        var telegram = new TelegramFalso();
        var segunda = Revision(telegram: telegram);
        segunda.Revisar();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena)))
            await Assert.ThrowsAsync<SqliteException>(() => segunda.PrepararAsync(db).WaitAsync(Plazo));
        var (chat, _, texto, _) = Assert.Single(telegram.Enviados);
        Assert.Equal(7001, chat);
        Assert.StartsWith("La base no pasó la revisión", texto);
        Assert.Contains("no puede arrancar", texto);
        Assert.Contains("avisado por Telegram", await File.ReadAllTextAsync(segunda.MarcaDanada!));

        // Y otra vez en un minuto: ese día ya se avisó.
        var tercera = Revision(telegram: telegram);
        tercera.Revisar();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena)))
            await Assert.ThrowsAsync<SqliteException>(() => tercera.PrepararAsync(db).WaitAsync(Plazo));
        Assert.Single(telegram.Enviados);
    }

    [Fact]
    public async Task Si_ni_se_puede_migrar_ni_leer_los_chats_queda_en_el_registro_y_no_arranca()
    {
        File.WriteAllBytes(Ruta, Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray());
        await File.WriteAllTextAsync(Ruta + ".en-uso", "arrancó ayer");
        var telegram = new TelegramFalso();
        var registro = new RegistroDePrueba<RevisionAlArrancar>();
        var revision = Revision(registro: registro, telegram: telegram);

        revision.Revisar();
        await using var db = new PosDbContext(BaseDeDatos.Opciones(Cadena));
        var error = await Assert.ThrowsAsync<SqliteException>(() => revision.PrepararAsync(db));

        Assert.Contains("not a database", error.Message);
        Assert.Contains(registro.De(LogLevel.Critical), m => m.Contains("no puede arrancar") && m.Contains($"no hay ninguno en {CarpetaRespaldos}"));
        Assert.Contains(registro.De(LogLevel.Warning), m => m.Contains("Tampoco se pudieron leer los chats"));
        Assert.Empty(telegram.Enviados);
    }

    [Fact]
    public async Task El_servicio_de_la_prueba_se_detiene_bien_cuando_la_caja_le_cierra_la_entrada()
    {
        var tubo = new Pipe();
        using var detenido = new ManualResetEventSlim();
        var hilo = ModoPrueba.DetenerAlCerrarEntrada(tubo.Reader.AsStream(), detenido.Set);

        // Lo que llegue por la entrada no lo detiene.
        await tubo.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.False(detenido.Wait(TimeSpan.FromMilliseconds(200)));
        // La caja se cerró.
        await tubo.Writer.CompleteAsync();

        Assert.True(hilo.Join(TimeSpan.FromSeconds(10)));
        Assert.True(detenido.IsSet);
    }

    [Fact]
    public async Task Si_la_base_no_paso_la_revision_avisa_al_dueno_por_telegram_lo_dice_el_estado_y_la_franja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using (var db = servicio.Base())
        {
            var dueno = await db.Usuarios.SingleAsync(u => u.NombreUsuario == "dueno");
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.Id, ChatId = 7001, VinculadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // La caja principal se apagó de golpe: quedó la marca de su último arranque.
        var marca = servicio.Ruta + ".en-uso";
        await File.WriteAllTextAsync(marca, "arrancó ayer");
        var danada = new RevisionDanada();
        var telegram = new TelegramFalso();
        var fabrica = servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IRevisionRapida>();
            s.AddSingleton<IRevisionRapida>(danada);
            s.AddSingleton<ITelegram>(telegram);
        }));
        var cajaLocal = Path.Combine(_carpeta, "caja-local.db");

        try
        {
            var http = fabrica.CreateClient();
            var estado = await http.GetFromJsonAsync<EstadoServicioDto>("api/estado");
            await fabrica.Services.GetRequiredService<MotorTelegram>().EnviarAsync();
            var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
            var contingencia = new Contingencia(new AlmacenLocal(cajaLocal), new HttpClient(fabrica.Server.CreateHandler()),
                new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
            await contingencia.LatidoAsync();
            string? ajustes;
            using (var alcance = fabrica.Services.CreateScope())
                ajustes = await BaseDeDatos.RevisarAjustesAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>());

            Assert.Null(ajustes);
            Assert.Equal(1, danada.Veces);
            Assert.StartsWith(RevisionAlArrancar.AvisoBaseDanada, estado!.AvisoBase);
            Assert.Equal(estado.AvisoBase, contingencia.AvisoBase);
            var (chat, _, texto, _) = Assert.Single(telegram.Enviados);
            Assert.Equal(7001, chat);
            Assert.StartsWith("La base no pasó la revisión", texto);
            Assert.Contains("Page 7: btreeInitPage() returns error code 11", texto);
            Assert.Contains("Hay que restaurar", texto);
            Assert.Contains($"mueve {Path.GetFileName(servicio.Ruta)}, {Path.GetFileName(servicio.Ruta)}-wal y", texto);
            await using var db = servicio.Base();
            Assert.Equal(TipoAlerta.BaseDanada, (await db.MensajesTelegram.SingleAsync()).Tipo);
        }
        finally
        {
            await fabrica.DisposeAsync();
        }
        // Se detuvo bien: la marca se quita, pero la de la base dañada se queda y el siguiente arranque vuelve a revisar.
        Assert.False(File.Exists(marca));
        Assert.True(File.Exists(servicio.Ruta + ".danada"));
    }
}
