using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Revisión independiente de cb5f83f: la migración VariasImpresoras sobre una base en un archivo (WAL, como la de la caja 2), con los casos que no
/// cubre MigracionVariasImpresorasTests, y a la mitad de una falla.
/// </summary>
public sealed class RevisionMigracionVariasImpresorasTests : IDisposable
{
    private const string Anterior = "20261029151304_TrabajosControlPrintCobrados";

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rev-mig-{Guid.NewGuid():N}");

    private string Ruta => Path.Combine(_carpeta, "pos.db");

    public RevisionMigracionVariasImpresorasTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_carpeta, recursive: true);
    }

    private PosDbContext Contexto() => new(BaseDeDatos.Opciones($"Data Source={Ruta}"));

    private static string Texto(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>SQL tal cual: sin el formato de ExecuteSqlRaw (las llaves del JSON).</summary>
    private static void Ejecutar(PosDbContext db, string sql)
    {
        db.Database.OpenConnection();
        using var comando = db.Database.GetDbConnection().CreateCommand();
        comando.CommandText = sql;
        comando.ExecuteNonQuery();
    }

    private static List<string> Filas(PosDbContext db, string sql)
    {
        var conexion = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        using var lector = comando.ExecuteReader();
        var filas = new List<string>();
        while (lector.Read())
            filas.Add(string.Join("|", Enumerable.Range(0, lector.FieldCount).Select(i => lector.IsDBNull(i) ? "∅" : lector.GetValue(i) is byte[] b ? Convert.ToHexString(b) : lector.GetValue(i).ToString())));
        return filas;
    }

    /// <summary>Todas las columnas de una tabla (menos la que agrega la migración), para compararlas antes y después.</summary>
    private static List<string> Tabla(PosDbContext db, string tabla, params string[] sinEstas)
    {
        var columnas = Filas(db, $"SELECT name FROM pragma_table_info('{tabla}')").Where(c => !sinEstas.Contains(c)).Select(c => $"\"{c}\"");
        return Filas(db, $"SELECT {string.Join(", ", columnas)} FROM \"{tabla}\" ORDER BY 1");
    }

    private static string Trabajo(Guid id, Guid sucursal, Guid caja, string documento, long precio, long costo, string cobro) =>
        "INSERT INTO TrabajosImpresion (Id, SucursalId, CajaId, Pc, Cola, SpoolId, Documento, ImpresoUtc, Estado, Paginas, Copias, Caras, CarasColor, "
        + "Hojas, Color, Duplex, Tamano, TipoPapel, Calidad, Medido, CostoTintaCentavos, CostoPapelCentavos, CostoOtrosCentavos, CostoCentavos, "
        + $"MargenPorcentaje, PrecioCentavos, DetalleJson, Cobro, TintaK, Miniatura) VALUES ('{Texto(id)}', '{Texto(sucursal)}', '{Texto(caja)}', 'CAJA2', 'WF-6590', 7, '{documento}', "
        + $"'2026-11-02 18:10:00', 'Impreso', 6, 1, 6, 0, 6, 0, 0, 'Carta', 'Papel normal', 'Standard', 1, 120, 60, 30, {costo}, '100', {precio}, '{{}}', '{cobro}', 4.5, randomblob(4096));";

    /// <summary>Una base como la de la tienda, en la migración de antes, con cada caso.</summary>
    private (Guid ConConfig, Guid ConTrabajos, Guid SinNada, Guid ConfigSinIp, Guid Caja) BaseDeAntes(PosDbContext db)
    {
        db.GetService<IMigrator>().Migrate(Anterior);
        Ejecutar(db, "PRAGMA journal_mode=WAL;");
        var (conConfig, conTrabajos, sinNada, configSinIp, caja) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, clave) in new[] { (conConfig, "A"), (conTrabajos, "B"), (sinNada, "C"), (configSinIp, "D") })
            Ejecutar(db, $"INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ('{Texto(id)}', '{clave}', '{clave}', 1, 1, '2026-10-01 12:00:00')");
        Ejecutar(db, $"INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) VALUES ('{Texto(caja)}', '{Texto(conTrabajos)}', 'Caja 1', 'A', 1, 1, 1, '2026-10-01 12:00:00')");
        var costos = new CostSettings();
        costos.Black.Price = 777;
        costos.Calibration.K = 1.234;
        var json = JsonSerializer.Serialize(costos, CostSettings.Json).Replace("'", "''");
        // Con configuración e IP, sin lecturas ni trabajos: lo de la caja 2 si nunca se ha leído.
        Ejecutar(db, "INSERT INTO ConfiguracionesImpresion (SucursalId, CostosJson, ModificadoUtc, ImpresoraHost, AvisoTintaPorcentaje, MinutosEntreLecturas) "
            + $"VALUES ('{Texto(conConfig)}', '{json}', '2026-10-20 12:00:00', '192.0.2.50', 15, 7)");
        // Configuración sin IP (costos de Control Print): su impresora 1 queda sin IP, con su aviso y sus minutos.
        Ejecutar(db, "INSERT INTO ConfiguracionesImpresion (SucursalId, CostosJson, ModificadoUtc, ImpresoraHost, AvisoTintaPorcentaje, MinutosEntreLecturas) "
            + $"VALUES ('{Texto(configSinIp)}', '{{}}', '2026-10-20 12:00:00', NULL, 20, 9)");
        // El historial de costos de la sucursal.
        Ejecutar(db, "INSERT INTO HistorialCostosImpresion (Id, SucursalId, DesdeUtc, HastaUtc, PorLosDe, CostosJson) "
            + $"VALUES ('{Texto(Guid.NewGuid())}', '{Texto(conConfig)}', '2026-10-01 12:00:00', '2026-10-20 12:00:00', 'Control Print', '{{}}')");
        // Con trabajos pero sin configuración: uno por cobrar, uno cobrado y uno no vendido, con sus precios.
        foreach (var (documento, precio, costo, cobro) in new[] { ("Por cobrar.pdf", 600L, 210L, "PorCobrar"), ("Cobrado.pdf", 900L, 310L, "NoVendida"), ("Tramite.pdf", 0L, 110L, "EnTramite") })
            Ejecutar(db, Trabajo(Guid.NewGuid(), conTrabajos, caja, documento, precio, costo, cobro));
        return (conConfig, conTrabajos, sinNada, configSinIp, caja);
    }

    [Fact]
    public async Task La_base_de_antes_pasa_igual_con_config_sin_lecturas_con_trabajos_sin_config_y_sin_nada()
    {
        using var db = Contexto();
        var (conConfig, conTrabajos, sinNada, configSinIp, _) = BaseDeAntes(db);
        var trabajos = Tabla(db, "TrabajosImpresion");
        var costos = Tabla(db, "ConfiguracionesImpresion", "ImpresoraHost", "AvisoTintaPorcentaje", "MinutosEntreLecturas");
        var historial = Tabla(db, "HistorialCostosImpresion");

        db.GetService<IMigrator>().Migrate();

        // Nada de lo que tenía cambió: precios, costos, cobros, miniaturas y el historial.
        // (Sin lo que agregaron las migraciones de después: lo cotizado de cada impresión, 2026-11-08.)
        Assert.Equal(trabajos, Tabla(db, "TrabajosImpresion", "ImpresoraId", "CobraLoCotizado", "CotizacionId", "PrecioCotizadoCentavos",
            "PrecioImpresoCentavos"));
        Assert.Equal(costos, Tabla(db, "ConfiguracionesImpresion"));
        Assert.Equal(historial, Tabla(db, "HistorialCostosImpresion", "ImpresoraId"));
        Assert.Equal(["ok"], Filas(db, "PRAGMA integrity_check"));
        Assert.Empty(Filas(db, "PRAGMA foreign_key_check"));

        var impresoras = db.Set<Impresora>().AsNoTracking().ToList();
        Assert.Equal(3, impresoras.Count);
        Assert.DoesNotContain(impresoras, i => i.SucursalId == sinNada);
        var a = Assert.Single(impresoras, i => i.SucursalId == conConfig);
        Assert.Equal((1, "192.0.2.50", 15, 7, PerfilImpresora.WF6590, true), (a.Numero, a.Host, a.AvisoTintaPorcentaje, a.MinutosEntreLecturas, a.Perfil, a.Activa));
        var b = Assert.Single(impresoras, i => i.SucursalId == conTrabajos);
        Assert.Equal((1, (string?)null, 10, 5), (b.Numero, b.Host, b.AvisoTintaPorcentaje, b.MinutosEntreLecturas));
        var d = Assert.Single(impresoras, i => i.SucursalId == configSinIp);
        Assert.Equal(((string?)null, 20, 9), (d.Host, d.AvisoTintaPorcentaje, d.MinutosEntreLecturas));
        Assert.All(db.Set<TrabajoImpresion>().AsNoTracking().ToList(), t => Assert.Equal(b.Id, t.ImpresoraId));
        // Los costos de la impresora 1 son los de la sucursal (el negro a $777 y la calibración).
        var costosDeLa1 = await ImpresionesDelPos.Impresora(db).CostosAsync(a);
        Assert.Equal((777m, 1.234), (costosDeLa1.Black.Price, costosDeLa1.Calibration.K));
        // El SQLite que trae el POS tiene DROP COLUMN (3.35 o más).
        var version = Version.Parse(Filas(db, "SELECT sqlite_version()").Single());
        Assert.True(version >= new Version(3, 35), version.ToString());
    }

    /// <summary>Si truena a la mitad (el último paso, DROP COLUMN): todo se deshace, y la siguiente vez sí pasa.</summary>
    [Fact]
    public void Si_falla_a_la_mitad_la_base_queda_como_estaba_y_la_siguiente_vez_pasa()
    {
        using var db = Contexto();
        var (conConfig, _, _, _, _) = BaseDeAntes(db);
        var trabajos = Tabla(db, "TrabajosImpresion");
        // Una vista que usa la columna hace fallar el DROP COLUMN, el último paso de Up.
        Ejecutar(db, "CREATE VIEW Estorbo AS SELECT \"ImpresoraHost\" FROM \"ConfiguracionesImpresion\";");

        var error = Assert.ThrowsAny<Exception>(() => db.GetService<IMigrator>().Migrate());
        Assert.Contains("Estorbo", error.Message);

        Assert.Empty(Filas(db, "SELECT name FROM sqlite_master WHERE name = 'Impresoras'"));
        Assert.Empty(Filas(db, "SELECT name FROM pragma_table_info('TrabajosImpresion') WHERE name = 'ImpresoraId'"));
        Assert.Single(Filas(db, "SELECT name FROM pragma_table_info('ConfiguracionesImpresion') WHERE name = 'ImpresoraHost'"));
        Assert.Empty(Filas(db, "SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%VariasImpresoras'"));
        Assert.Equal(trabajos, Tabla(db, "TrabajosImpresion"));

        Ejecutar(db, "DROP VIEW Estorbo;");
        db.GetService<IMigrator>().Migrate();
        Assert.Equal("192.0.2.50", Assert.Single(db.Set<Impresora>().AsNoTracking().ToList(), i => i.SucursalId == conConfig).Host);
    }

    /// <summary>Down con varias impresoras, la 1 de baja: la activa de número más bajo vuelve a ser la de la sucursal; lo demás no truena.</summary>
    [Fact]
    public async Task Al_regresarla_con_varias_la_primera_activa_vuelve_a_la_sucursal_y_se_puede_volver_a_aplicar()
    {
        using var db = Contexto();
        var (conConfig, conTrabajos, _, _, _) = BaseDeAntes(db);
        db.GetService<IMigrator>().Migrate();
        var servicio = ImpresionesDelPos.Impresora(db);
        var wf1 = (await servicio.PrimeraAsync(conConfig))!;
        var l5590 = await servicio.AgregarAsync(conConfig, "L5590", PerfilImpresora.L5590, "192.0.2.53");
        await servicio.GuardarConsumiblesAsync(l5590, new(Guid.NewGuid(),
            [new("Negro", 1, 4300), new("Cian", 1, 7300), new("Magenta", 1, 7300), new("Amarillo", 1, 7300)]));
        await servicio.CambiarAsync(wf1, null, null, activa: false);
        var precios = Filas(db, "SELECT Id, PrecioCentavos FROM TrabajosImpresion ORDER BY 1");

        db.GetService<IMigrator>().Migrate(Anterior);

        Assert.Contains($"{Texto(conConfig)}|192.0.2.53", Filas(db, "SELECT SucursalId, ImpresoraHost FROM ConfiguracionesImpresion"));
        Assert.Contains(Texto(conTrabajos), Filas(db, "SELECT SucursalId FROM ConfiguracionesImpresion WHERE ImpresoraHost IS NULL").Concat(Filas(db, "SELECT DISTINCT SucursalId FROM TrabajosImpresion")));
        Assert.Equal(precios, Filas(db, "SELECT Id, PrecioCentavos FROM TrabajosImpresion ORDER BY 1"));
        Assert.Empty(Filas(db, "SELECT name FROM sqlite_master WHERE name = 'Impresoras'"));

        db.GetService<IMigrator>().Migrate();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(precios, Filas(db, "SELECT Id, PrecioCentavos FROM TrabajosImpresion ORDER BY 1"));
    }
}

/// <summary>Mediciones de la revisión (con REV_BASES / REV_MEDIR): las bases de desarrollo que hay en esta PC, en una copia, y lo que crece el WAL.</summary>
public sealed class RevisionMigracionMedidasTests(Xunit.Abstractions.ITestOutputHelper salida)
{
    private const string Anterior = "20261029151304_TrabajosControlPrintCobrados";

    private static string Escalar(string ruta, string sql)
    {
        using var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False");
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return Convert.ToString(comando.ExecuteScalar()) ?? "";
    }

    [Fact]
    public async Task Bases_de_desarrollo_en_una_copia()
    {
        if (Environment.GetEnvironmentVariable("REV_BASES") is not { Length: > 0 } rutas)
            return;
        foreach (var ruta in rutas.Split(';'))
        {
            var carpeta = Directory.CreateTempSubdirectory("rev-base-").FullName;
            var copia = Path.Combine(carpeta, "pos.db");
            File.Copy(ruta, copia);
            foreach (var final in new[] { "-wal", "-shm" })
                if (File.Exists(ruta + final))
                    File.Copy(ruta + final, copia + final);
            var precios = Escalar(copia, "SELECT COUNT(*) || ':' || COALESCE(SUM(PrecioCentavos), 0) || ':' || COALESCE(SUM(CostoCentavos), 0) FROM TrabajosImpresion");
            var inicio = System.Diagnostics.Stopwatch.StartNew();
            await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={copia}")))
                await BaseDeDatos.PrepararAsync(db);
            salida.WriteLine($"{ruta}: {inicio.ElapsedMilliseconds} ms; impresoras {Escalar(copia, "SELECT COUNT(*) FROM Impresoras")}; "
                + $"sin ligar {Escalar(copia, "SELECT COUNT(*) FROM TrabajosImpresion WHERE ImpresoraId IS NULL")}; versión SQLite {Escalar(copia, "SELECT sqlite_version()")}; "
                + $"revisión {Escalar(copia, "PRAGMA integrity_check")}");
            Assert.Equal(precios, Escalar(copia, "SELECT COUNT(*) || ':' || COALESCE(SUM(PrecioCentavos), 0) || ':' || COALESCE(SUM(CostoCentavos), 0) FROM TrabajosImpresion"));
            SqliteConnection.ClearAllPools();
            Directory.Delete(carpeta, recursive: true);
        }
    }

    /// <summary>La migración dice «sin rehacer las tablas: así las de los trabajos (con sus miniaturas) no se copian»: el UPDATE de ImpresoraId las reescribe.</summary>
    [Fact]
    public async Task Lo_que_escribe_con_miniaturas()
    {
        if (Environment.GetEnvironmentVariable("REV_MEDIR") is not { Length: > 0 })
            return;
        var carpeta = Directory.CreateTempSubdirectory("rev-wal-").FullName;
        var ruta = Path.Combine(carpeta, "pos.db");
        await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}"));
        await db.GetService<IMigrator>().MigrateAsync(Anterior);
        db.Database.OpenConnection();
        var (sucursal, caja) = (Guid.NewGuid().ToString().ToUpperInvariant(), Guid.NewGuid().ToString().ToUpperInvariant());
        using (var c = db.Database.GetDbConnection().CreateCommand())
        {
            c.CommandText = $"PRAGMA journal_mode=WAL; INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ('{sucursal}', 'M', 'M', 1, 1, '2026-10-01 12:00:00');"
                + $"INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) VALUES ('{caja}', '{sucursal}', 'Caja 1', 'A', 1, 1, 1, '2026-10-01 12:00:00');"
                + "BEGIN; WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) "
                + "INSERT INTO TrabajosImpresion (Id, SucursalId, CajaId, Pc, Cola, SpoolId, Documento, ImpresoUtc, Estado, Paginas, Copias, Caras, CarasColor, "
                + "Hojas, Color, Duplex, Tamano, TipoPapel, Calidad, Medido, CostoTintaCentavos, CostoPapelCentavos, CostoOtrosCentavos, CostoCentavos, "
                + "MargenPorcentaje, PrecioCentavos, DetalleJson, Cobro, TintaK, Miniatura) "
                + $"SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-A' || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))), '{sucursal}', '{caja}', 'CAJA2', 'WF', i, 'Doc.pdf', "
                + "'2026-11-02 18:10:00', 'Impreso', 1, 1, 1, 0, 1, 0, 0, 'Carta', 'Papel', 'Standard', 1, 1, 1, 1, 3, '100', 6, '{}', 'Cobrada', 1, randomblob(50000) FROM n; COMMIT; PRAGMA wal_checkpoint(TRUNCATE);";
            c.ExecuteNonQuery();
        }
        var antes = new FileInfo(ruta).Length;
        // Que no se reinicie el WAL al terminar: una conexión de lectura abierta no deja hacer el checkpoint completo.
        var inicio = System.Diagnostics.Stopwatch.StartNew();
        await db.GetService<IMigrator>().MigrateAsync();
        var tiempo = inicio.ElapsedMilliseconds;
        var wal = new FileInfo(ruta + "-wal").Length;
        salida.WriteLine($"2000 trabajos con miniatura de 50 KB: base {antes / 1_000_000} MB, migración {tiempo} ms, WAL después de migrar {wal / 1_000_000} MB, base {new FileInfo(ruta).Length / 1_000_000} MB.");
        db.Database.CloseConnection();
        SqliteConnection.ClearAllPools();
        Directory.Delete(carpeta, recursive: true);
    }
}
