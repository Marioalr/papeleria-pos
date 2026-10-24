using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Servicio.RestaurarRespaldoTests;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Restaurar a medias (revisión del PR #136): un corte o el disco lleno en cualquier paso nunca dejan al servicio sin poder arrancar;
/// un pedido que no se puede leer nunca lo hace arrancar con una base vacía; un respaldo dañado (también un índice que no cuadra con
/// su tabla) o de una versión más nueva del programa no se pone, y si ya no se puede poner, la base de antes regresa a su lugar.
/// </summary>
public sealed class RestaurarRespaldoAMediasTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static PedidoDeRestauracion LeerPedido(PrincipalQueSeRestaura p) =>
        JsonSerializer.Deserialize<PedidoDeRestauracion>(File.ReadAllText(p.Ruta + ".restaurar"), Opciones)!;

    private static string Cadena(string ruta) => $"Data Source={ruta};Pooling=False";

    /// <summary>El disco con tantos bytes libres.</summary>
    private sealed class DiscoCon(long libres) : IEspacioEnDisco
    {
        public long? Libre(string carpeta) => libres;
    }

    /// <summary>El disco se llena a media copia del respaldo: lo que la revisa truena como truena escribir en un disco lleno.</summary>
    private sealed class DiscoQueSeLlena : IRevisionCompleta
    {
        public IReadOnlyList<string> Revisar(string cadenaConexion) =>
            new SqliteConnectionStringBuilder(cadenaConexion).DataSource.EndsWith(".restaurando", StringComparison.Ordinal)
                ? throw new IOException("No space left on device")
                : BaseDeDatos.RevisionCompleta(cadenaConexion);
    }

    /// <summary>
    /// La base con una marca de antes del respaldo, el respaldo, y otra de después (en el -wal, si <paramref name="enElWal"/>); y el
    /// pedido de restaurarlo. Devuelve el nombre del respaldo.
    /// </summary>
    private static async Task<string> PedidaAsync(PrincipalQueSeRestaura p, bool enElWal = false)
    {
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        if (enElWal)
            await MarcarSoloEnElWalAsync(p.Ruta, "después del respaldo");
        else
            await MarcarAsync(p.Ruta, "después del respaldo");
        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);
        return archivo;
    }

    /// <summary>Como queda si se corta en «Apartada»: la base de antes (con su -wal) ya en su carpeta y el pedido diciéndolo.</summary>
    private static string Apartar(PrincipalQueSeRestaura p)
    {
        var carpeta = Path.Combine(p.Carpeta, "antes-de-restaurar-2026-10-23_14.32.05");
        Directory.CreateDirectory(carpeta);
        foreach (var final in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(p.Ruta + final))
                File.Move(p.Ruta + final, Path.Combine(carpeta, "pos.db" + final));
        }
        File.WriteAllText(p.Ruta + ".restaurar", JsonSerializer.Serialize(
            LeerPedido(p) with { Paso = PasoDeRestauracion.Apartada, Carpeta = Path.GetFileName(carpeta) }, Opciones));
        return carpeta;
    }

    private static string Respaldo(PrincipalQueSeRestaura p, string archivo) => Path.Combine(p.CarpetaRespaldos, archivo);

    // ── F1: un corte justo después de poner la copia ─────────────────────────

    /// <summary>
    /// Se fue la luz (o el antivirus tenía abierto el pedido) justo después de poner la copia: la copia ya es la base y el pedido sigue
    /// en «Apartada». Antes, cada arranque volvía a copiar el respaldo y chocaba con la base que ya estaba: el servicio no volvía.
    /// </summary>
    [Fact]
    public async Task Si_se_corta_justo_despues_de_poner_la_copia_el_servicio_vuelve_a_arrancar_con_ella()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);
        var carpeta = Apartar(p);
        File.Copy(Respaldo(p, archivo), p.Ruta);

        var alArrancar = Restauracion(p);
        alArrancar.SiSePidio();
        // Y el siguiente arranque, igual (ya dice «Puesta»).
        Restauracion(p).SiSePidio();

        Assert.Equal(PasoDeRestauracion.Puesta, LeerPedido(p).Paso);
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(Path.Combine(carpeta, "pos.db")));
        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Equal(carpeta, alArrancar.Ultima!.BaseDeAntes);
        // La base se revisa al arrancar (la marca de la base dañada) y, como pasa, la revisión quita la marca.
        Assert.True(File.Exists(p.Ruta + ".danada"));
        var revision = new RevisionAlArrancar(Cadena(p.Ruta), p.CarpetaRespaldos, new RevisionRapidaSqlite(), NullLogger<RevisionAlArrancar>.Instance);
        revision.Revisar();
        Assert.NotNull(revision.Revision);
        Assert.False(revision.BaseDanada);
        Assert.False(File.Exists(p.Ruta + ".danada"));
        revision.Detenido();
    }

    /// <summary>
    /// En la prueba con una persona real la base empieza como copia de la de prueba si falta. A media restauración falta (ya está en su
    /// carpeta): la de prueba no ocupa su lugar, porque se pone después de restaurar.
    /// </summary>
    [Fact]
    public async Task En_la_prueba_la_base_de_prueba_no_ocupa_el_lugar_de_la_que_se_restaura()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);
        var carpeta = Apartar(p);
        File.Copy(Respaldo(p, archivo), p.Ruta + ".restaurando");
        var dePrueba = Path.Combine(p.Carpeta, "prueba-inicial.db");
        File.Copy(Path.Combine(carpeta, "pos.db"), dePrueba);
        await MarcarAsync(dePrueba, "la base de la prueba");

        var http = p.Arrancar(("Prueba:Activa", "true"), ("Prueba:BaseInicial", dePrueba));
        (await http.GetAsync("api/estado")).EnsureSuccessStatusCode();
        await p.DetenerAsync();

        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(["pos.db"], Directory.GetFiles(carpeta).Select(Path.GetFileName));
    }

    // ── F2: el disco lleno ───────────────────────────────────────────────────

    [Fact]
    public async Task Si_el_respaldo_no_cabe_en_el_disco_no_se_pide_y_al_arrancar_no_se_toca_nada()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "antes del respaldo");
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        await MarcarAsync(p.Ruta, "después del respaldo");
        var casiLleno = new DiscoCon(RestauracionDeRespaldos.EspacioNecesario(new FileInfo(Respaldo(p, archivo)).Length) - 1);

        var noSePide = Assert.Throws<InvalidOperationException>(() =>
            Restauracion(p, espacio: casiLleno).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true));
        Assert.StartsWith($"No hay espacio en el disco de la caja principal para restaurar el respaldo {archivo}: hacen falta ", noSePide.Message);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));

        // Cabía al pedirlo, pero el disco se llenó antes de que el servicio volviera a arrancar: no se copia nada.
        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);
        var alArrancar = Restauracion(p, espacio: casiLleno);
        alArrancar.SiSePidio();

        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Empty(Directory.GetDirectories(p.Carpeta, "antes-de-restaurar-*"));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(PasoDeRestauracion.Fallida, LeerPedido(p).Paso);
        Assert.StartsWith("No hay espacio en el disco de la caja principal", alArrancar.Ultima!.Fallo);
    }

    /// <summary>
    /// El disco se llena a media copia (el espacio libre no lo vio venir: otro programa escribió mientras). Antes la copia a medias se
    /// quedaba ocupando lo que quedaba del disco, el pedido no se podía escribir y el servicio no arrancaba; la base de ahora tampoco se
    /// podía escribir. Ahora la copia se borra, queda dicho y el servicio arranca con su base. Si ni el pedido se puede escribir, se
    /// quita: no se vuelve a intentar en cada arranque.
    /// </summary>
    [Fact]
    public async Task Si_el_disco_se_llena_a_media_copia_se_borra_la_copia_y_el_servicio_arranca_con_su_base()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);

        var alArrancar = Restauracion(p, new DiscoQueSeLlena());
        alArrancar.SiSePidio();

        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Empty(Directory.GetDirectories(p.Carpeta, "antes-de-restaurar-*"));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(PasoDeRestauracion.Fallida, LeerPedido(p).Paso);
        Assert.Equal("No space left on device", alArrancar.Ultima!.Fallo);

        // Otra vez, y ahora ni el pedido se puede escribir: se quita, y el servicio arranca igual.
        var (dueno, caja, sucursal) = await QuienAsync(p);
        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);
        Directory.CreateDirectory(p.Ruta + ".restaurar.tmp");
        var otraVez = Restauracion(p, new DiscoQueSeLlena());
        otraVez.SiSePidio();

        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Equal("No space left on device", otraVez.Ultima!.Fallo);
        Directory.Delete(p.Ruta + ".restaurar.tmp");
        // La bitácora de la base de siempre lo dice.
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena(p.Ruta))))
            Assert.False(await otraVez.DespuesDeMigrarAsync(db));
        await using var base_ = p.Servicio.Base();
        Assert.True(await base_.Bitacora.AnyAsync(b => b.Detalle.StartsWith("No se restauró") && b.Detalle.EndsWith(": No space left on device")));
    }

    // ── F4: un pedido que no se puede leer, o la base que falta ──────────────

    /// <summary>
    /// A media restauración (la base de antes ya en su carpeta), el antivirus tiene abierto el pedido. Antes se apartaba «como no se
    /// entendió» y el servicio arrancaba sin base: la migración hacía una vacía (sin productos, usuarios ni cajas). Ahora no arranca, y
    /// Windows lo vuelve a arrancar: ya suelto el pedido, sigue donde iba.
    /// </summary>
    [Fact]
    public async Task Si_el_pedido_no_se_puede_leer_el_servicio_no_arranca_y_ya_suelto_sigue_donde_iba()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);
        Apartar(p);
        File.Copy(Respaldo(p, archivo), p.Ruta + ".restaurando");

        Exception? noArranca;
        using (new FileStream(p.Ruta + ".restaurar", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            noArranca = Record.Exception(Restauracion(p).SiSePidio);

        Assert.True(noArranca is IOException or UnauthorizedAccessException, $"Debía no arrancar: {noArranca}");
        Assert.Empty(Directory.GetFiles(p.Carpeta, "pos.db.restaurar.no-se-entendio-*"));
        Assert.False(File.Exists(p.Ruta));

        var alVolver = Restauracion(p);
        alVolver.SiSePidio();
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Equal(PasoDeRestauracion.Puesta, LeerPedido(p).Paso);
    }

    /// <summary>
    /// A media restauración el pedido ya no se entiende (lo dejó así otro programa): se aparta, pero sin la base el servicio no arranca
    /// (antes hacía una vacía), y dice dónde está la base de antes y la copia del respaldo. Ni el servicio entero arranca.
    /// </summary>
    [Fact]
    public async Task Un_pedido_que_no_se_entiende_a_media_restauracion_no_deja_arrancar_con_una_base_vacia()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);
        var carpeta = Apartar(p);
        File.Copy(Respaldo(p, archivo), p.Ruta + ".restaurando");
        File.WriteAllText(p.Ruta + ".restaurar", "{ esto no es un pedido");

        var noArranca = Assert.Throws<InvalidOperationException>(Restauracion(p).SiSePidio);
        Assert.StartsWith($"No está la base ({p.Ruta}) y quedó una restauración a medias: el servicio no arranca", noArranca.Message);
        Assert.Contains($"La base de antes está en {Path.Combine(carpeta, "pos.db")}.", noArranca.Message);
        Assert.Contains($"La copia del respaldo está en {p.Ruta}.restaurando.", noArranca.Message);
        Assert.Single(Directory.GetFiles(p.Carpeta, "pos.db.restaurar.no-se-entendio-*"));

        // Sin el pedido (ya apartado), tampoco: queda la copia. Y el servicio entero no arranca ni hace una base vacía.
        Assert.Throws<InvalidOperationException>(Restauracion(p).SiSePidio);
        Assert.ThrowsAny<Exception>(() => p.Arrancar());
        Assert.False(File.Exists(p.Ruta));
        Assert.True(File.Exists(Path.Combine(carpeta, "pos.db")));
    }

    /// <summary>La primera vez que arranca no hay base ni rastro de una restauración: sigue, y la migración la hace.</summary>
    [Fact]
    public void Sin_base_y_sin_rastro_de_una_restauracion_arranca_como_la_primera_vez()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"restaurar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(carpeta);
        try
        {
            new RestauracionDeRespaldos(Cadena(Path.Combine(carpeta, "pos.db")), Path.Combine(carpeta, "respaldos"), new RevisionCompletaSqlite(),
                new EspacioEnDisco(), NullLogger<RestauracionDeRespaldos>.Instance, TimeProvider.System).SiSePidio();
            Assert.False(File.Exists(Path.Combine(carpeta, "pos.db")));
        }
        finally
        {
            ArchivosDePrueba.BorrarCarpeta(carpeta);
        }
    }

    // ── F5 y F6: el respaldo de una versión más nueva, o con un índice que no cuadra ──

    /// <summary>Como si lo hubiera hecho una versión más nueva: una migración que esta no conoce, con su tabla.</summary>
    private static void DeUnaVersionMasNueva(string ruta)
    {
        using var conexion = new SqliteConnection(Cadena(ruta));
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231235959_DeUnaVersionMasNueva', '99.0.0');"
            + " CREATE TABLE TablaDeLaVersionNueva (Id INTEGER NOT NULL)";
        comando.ExecuteNonQuery();
    }

    [Fact]
    public async Task Un_respaldo_de_una_version_mas_nueva_del_programa_no_se_restaura()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        await MarcarAsync(p.Ruta, "la base de ahora");
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        DeUnaVersionMasNueva(Respaldo(p, archivo));
        Assert.Equal(["29991231235959_DeUnaVersionMasNueva"], BaseDeDatos.MigracionesDesconocidas(Cadena(Respaldo(p, archivo))));
        Assert.Empty(BaseDeDatos.MigracionesDesconocidas(Cadena(p.Ruta)));

        var noSePide = Assert.Throws<InvalidOperationException>(() => Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true));
        Assert.Equal($"El respaldo {archivo} lo hizo una versión más nueva del programa (su base tiene un cambio que esta no conoce): actualiza el "
            + "programa de la caja principal antes de restaurarlo, o elige uno de antes.", noSePide.Message);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));

        // Pedido antes de que lo tuviera: al arrancar se revisa otra vez y tampoco se pone.
        var otro = p.Respaldar(Hace(20), "ahora-0940");
        Restauracion(p).Pedir(otro, dueno, caja, sucursal, reinicioAutomatico: true);
        DeUnaVersionMasNueva(Respaldo(p, otro));
        var alArrancar = Restauracion(p);
        alArrancar.SiSePidio();

        Assert.Equal(PasoDeRestauracion.Fallida, LeerPedido(p).Paso);
        Assert.Contains("lo hizo una versión más nueva del programa", alArrancar.Ultima!.Fallo);
        Assert.Empty(Directory.GetDirectories(p.Carpeta, "antes-de-restaurar-*"));
        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Equal(["la base de ahora"], await MarcasAsync(p.Ruta));
    }

    /// <summary>
    /// restaurar.ps1 no puede revisar la versión del respaldo (PowerShell no abre la base), y el que puso lo hizo una versión más
    /// nueva: el servicio no arranca con él (antes lo migraba y seguía) y el registro dice qué hacer; el script lo enseña.
    /// </summary>
    [Fact]
    public async Task Si_restaurar_ps1_puso_un_respaldo_de_una_version_mas_nueva_el_servicio_no_arranca_con_el()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        ArchivosDePrueba.SoltarBase(p.Ruta);
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        DeUnaVersionMasNueva(Respaldo(p, archivo));
        // Como restaurar.ps1: la base de antes aparte, el respaldo como pos.db y su pedido ya puesto.
        var carpeta = Path.Combine(p.Carpeta, "antes-de-restaurar-2026-10-23_14.32.05");
        Directory.CreateDirectory(carpeta);
        File.Move(p.Ruta, Path.Combine(carpeta, "pos.db"));
        File.Copy(Respaldo(p, archivo), p.Ruta);
        var ahora = DateTime.UtcNow;
        File.WriteAllText(p.Ruta + ".restaurar", JsonSerializer.Serialize(new PedidoDeRestauracion(Guid.NewGuid(), archivo, TipoRespaldo.Ahora, ahora,
            Guid.Empty, Guid.Empty, Guid.Empty, ahora, true, PasoDeRestauracion.Puesta, Path.GetFileName(carpeta), ahora, PorElScript: true), Opciones));

        var noArranca = Assert.Throws<InvalidOperationException>(Restauracion(p).SiSePidio);

        Assert.StartsWith($"restaurar.ps1 puso el respaldo {archivo}, que lo hizo una versión más nueva del programa (su base tiene "
            + "29991231235959_DeUnaVersionMasNueva, que esta no conoce): el servicio no arranca con él.", noArranca.Message);
        Assert.EndsWith($"La base de antes está en {carpeta}.", noArranca.Message);
        Assert.ThrowsAny<Exception>(() => p.Arrancar());
        Assert.True(File.Exists(p.Ruta + ".restaurar"));
        Assert.True(File.Exists(Path.Combine(carpeta, "pos.db")));
    }

    /// <summary>
    /// Dos índices con el contenido cambiado (cada uno con las entradas del otro, las mismas en número): la revisión rápida no lo ve,
    /// la completa sí. Así queda un índice que no cuadra con su tabla, como después de un apagón a media escritura.
    /// </summary>
    private static void IndicesQueNoCuadran(string ruta)
    {
        using var conexion = new SqliteConnection(Cadena(ruta));
        conexion.Open();
        void Ejecutar(string sql)
        {
            using var comando = conexion.CreateCommand();
            comando.CommandText = sql;
            comando.ExecuteNonQuery();
        }
        long Raiz(string indice)
        {
            using var comando = conexion.CreateCommand();
            comando.CommandText = $"SELECT rootpage FROM sqlite_master WHERE name = '{indice}'";
            return Convert.ToInt64(comando.ExecuteScalar());
        }
        Ejecutar("CREATE TABLE PruebaA (x INTEGER); CREATE INDEX IX_PruebaA ON PruebaA (x); CREATE TABLE PruebaB (x INTEGER);"
            + " CREATE INDEX IX_PruebaB ON PruebaB (x); INSERT INTO PruebaA VALUES (1), (2), (3); INSERT INTO PruebaB VALUES (10), (20), (30);");
        var (a, b) = (Raiz("IX_PruebaA"), Raiz("IX_PruebaB"));
        Ejecutar($"PRAGMA writable_schema = ON; UPDATE sqlite_master SET rootpage = {b} WHERE name = 'IX_PruebaA';"
            + $" UPDATE sqlite_master SET rootpage = {a} WHERE name = 'IX_PruebaB'; PRAGMA writable_schema = OFF;");
    }

    [Fact]
    public async Task Un_respaldo_con_un_indice_que_no_cuadra_con_su_tabla_no_se_restaura()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        var archivo = p.Respaldar(Hace(30), "ahora-0930");
        IndicesQueNoCuadran(Respaldo(p, archivo));
        SqliteConnection.ClearAllPools();
        Assert.True(BaseDeDatos.EstaSana(BaseDeDatos.RevisionRapida(Cadena(Respaldo(p, archivo)))));
        Assert.False(BaseDeDatos.EstaSana(BaseDeDatos.RevisionCompleta(Cadena(Respaldo(p, archivo)))));

        var noSePide = Assert.Throws<InvalidOperationException>(() => Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true));
        Assert.StartsWith($"El respaldo {archivo} no pasó la revisión (row ", noSePide.Message);
        Assert.EndsWith("): elige otro.", noSePide.Message);
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
    }

    // ── F8: la copia a medias, o el respaldo que ya no se puede poner ────────

    /// <summary>
    /// En «Apartada» con la copia a medias (se cortó mientras se volvía a copiar). Antes se ponía sin revisarla y, sin las marcas,
    /// tampoco se revisaba al arrancar: el servicio abría una base dañada. Ahora se revisa, y como no pasa se copia otra vez.
    /// </summary>
    [Fact]
    public async Task Una_copia_a_medias_no_se_pone_sin_revisarla_se_copia_otra_vez()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p);
        Apartar(p);
        var bytes = await File.ReadAllBytesAsync(Respaldo(p, archivo));
        await File.WriteAllBytesAsync(p.Ruta + ".restaurando", bytes[..(bytes.Length / 2)]);

        Restauracion(p).SiSePidio();

        Assert.Equal(PasoDeRestauracion.Puesta, LeerPedido(p).Paso);
        Assert.Equal(bytes.Length, new FileInfo(p.Ruta).Length);
        Assert.True(BaseDeDatos.EstaSana(BaseDeDatos.RevisionCompleta(Cadena(p.Ruta))));
        Assert.Equal(["antes del respaldo"], await MarcasAsync(p.Ruta));
    }

    /// <summary>
    /// En «Apartada», la copia a medias y el respaldo ya no está (lo borró la limpieza, o alguien): ya no se puede poner. La base de
    /// antes regresa a su lugar con su -wal (no se pierde lo último) y queda dicho; antes el servicio no arrancaba en ningún arranque.
    /// </summary>
    [Fact]
    public async Task Si_a_media_restauracion_el_respaldo_ya_no_se_puede_poner_la_base_de_antes_regresa_a_su_lugar()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var archivo = await PedidaAsync(p, enElWal: true);
        var carpeta = Apartar(p);
        Assert.True(File.Exists(Path.Combine(carpeta, "pos.db-wal")));
        var bytes = await File.ReadAllBytesAsync(Respaldo(p, archivo));
        await File.WriteAllBytesAsync(p.Ruta + ".restaurando", bytes[..(bytes.Length / 2)]);
        File.Delete(Respaldo(p, archivo));

        var alArrancar = Restauracion(p);
        alArrancar.SiSePidio();

        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
        Assert.Empty(Directory.GetFiles(carpeta));
        Assert.False(File.Exists(p.Ruta + ".restaurando"));
        Assert.Equal(PasoDeRestauracion.Fallida, LeerPedido(p).Paso);
        Assert.Equal($"Ya no está el respaldo {archivo} en {p.CarpetaRespaldos}.", alArrancar.Ultima!.Fallo);
        Assert.Null(alArrancar.Ultima.BaseDeAntes);
        // Y si el pedido no alcanzó a decirlo, el siguiente arranque ve que la base de antes ya regresó: tampoco la toma por el respaldo.
        File.WriteAllText(p.Ruta + ".restaurar", JsonSerializer.Serialize(LeerPedido(p) with { Paso = PasoDeRestauracion.Apartada, Fallo = null }, Opciones));
        var alVolver = Restauracion(p);
        alVolver.SiSePidio();
        Assert.Equal(PasoDeRestauracion.Fallida, LeerPedido(p).Paso);
        Assert.Equal("No se pudo poner el respaldo (el porqué quedó en el registro del servicio).", alVolver.Ultima!.Fallo);
        Assert.False(File.Exists(p.Ruta + ".danada"));
        Assert.Equal(["antes del respaldo", "después del respaldo"], await MarcasAsync(p.Ruta));
    }

    // ── F9: lo de después de migrar ──────────────────────────────────────────

    [Fact]
    public async Task Si_no_se_puede_anotar_despues_de_migrar_el_pedido_se_queda_y_se_anota_al_volver_a_arrancar()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        await PedidaAsync(p);
        var alArrancar = Restauracion(p);
        alArrancar.SiSePidio();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena(p.Ruta))))
            await BaseDeDatos.PrepararAsync(db);

        // La base no se deja escribir en ese momento (el disco lleno, por ejemplo).
        await using (var soloLectura = new PosDbContext(BaseDeDatos.Opciones($"Data Source={p.Ruta};Mode=ReadOnly;Pooling=False")))
            Assert.True(await alArrancar.DespuesDeMigrarAsync(soloLectura));
        Assert.Equal(PasoDeRestauracion.Puesta, LeerPedido(p).Paso);

        // Al volver a arrancar se anota, una sola vez, y se quita el pedido.
        var alVolver = Restauracion(p);
        alVolver.SiSePidio();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(Cadena(p.Ruta))))
            Assert.True(await alVolver.DespuesDeMigrarAsync(db));
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using var base_ = p.Servicio.Base();
        Assert.Equal(1, await base_.Bitacora.CountAsync(b => b.Detalle.StartsWith("Restauró el respaldo")));
    }

    // ── F10: el pedido, lo último que puede fallar ───────────────────────────

    /// <summary>La bitácora no se deja guardar justo al anotar el pedido de restaurar (la base que se va a reemplazar, dañada).</summary>
    private sealed class BitacoraQueNoSeGuarda : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<Bitacora>().Any(e => e.State == EntityState.Added && e.Entity.Detalle.StartsWith("Pidió restaurar"))
                ? throw new InvalidOperationException("La base no se deja escribir.")
                : ValueTask.FromResult(result);
    }

    /// <summary>
    /// Antes, si guardar la bitácora fallaba con algo que no era de la base, la pantalla decía que no se pudo, el pedido se quedaba y
    /// el servicio no se reiniciaba: se restauraba la próxima vez que arrancara, días después. Ahora el pedido es lo último que puede
    /// fallar: se pide, se reinicia, y la base restaurada lo anota al arrancar.
    /// </summary>
    [Fact]
    public async Task Si_la_bitacora_no_se_puede_guardar_igual_se_pide_y_la_caja_principal_se_reinicia()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var http = p.Arrancar(s => s.ConfigureDbContext<PosDbContext>(o => o.AddInterceptors(new BitacoraQueNoSeGuarda())));
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var archivo = p.Respaldar(Hace(30), "ahora-1010");

        var pedida = await Leer<RespaldosDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/configuracion/respaldos/restaurar",
            new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));

        Assert.True(pedida.Restauracion!.Pendiente);
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(File.Exists(p.Ruta + ".restaurar"));
    }
}
