using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>La migración de la fase 3, entrega 2: los clientes especiales pasan a ser clientes registrados sin perder nada.</summary>
public class MigracionClientesTests
{
    private const string Anterior = "20261008003559_MayoreoPorEscalas";

    /// <summary>Una base en la migración anterior con un cliente especial y una venta que lo nombra (las llaves quedan activas).</summary>
    private static async Task<(SqliteConnection Conexion, PosDbContext Db, IMigrator Migrador, string Cliente, string Nota)> BaseAnteriorAsync()
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);

        var cliente = Guid.NewGuid().ToString().ToUpperInvariant();
        var otro = Guid.NewGuid().ToString().ToUpperInvariant();
        var nota = new string('n', 250);
        // Sin la caja, el turno ni el cajero de verdad: aquí solo cuenta la liga de la venta con el cliente.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ClientesEspeciales (Id, SucursalId, Nombre, DescuentoCentesimas, Nota, Activo, Version, ModificadoUtc) " +
            "VALUES ({0}, NULL, 'Escuela Benito Juárez', 1000, {1}, 1, 7, '2026-10-01 12:00:00')", cliente, nota);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Ventas (Id, CajaId, CajeroId, ClienteEspecialId, CreadaUtc, EnContingencia, Estado, Folio, IvaCentavos, PagadaUtc, Serie, " +
            "SucursalId, TotalCentavos, TurnoId, RedondeoCentavos) VALUES ({0}, {1}, {1}, {2}, '2026-10-01 12:00:00', 0, 'Pagada', 1, 0, " +
            "'2026-10-01 12:00:00', 'A', {1}, 9000, {1}, 0)", Guid.NewGuid().ToString().ToUpperInvariant(), otro, cliente);
        // Como al arrancar el servicio: la conexión trae las llaves activas.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return (conexion, db, migrador, cliente, nota);
    }

    private static async Task<long> LlavesAsync(PosDbContext db) =>
        (await db.Database.SqlQueryRaw<long>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").ToListAsync()).Single();

    private static async Task<List<string>> TablasAsync(PosDbContext db) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();

    [Fact]
    public async Task Los_clientes_especiales_pasan_a_registrados_con_su_Id_y_sus_ventas_los_siguen_nombrando()
    {
        var (conexion, db, migrador, cliente, nota) = await BaseAnteriorAsync();
        await using var _ = conexion;
        await using var __ = db;

        await migrador.MigrateAsync();

        var registrado = await db.ClientesRegistrados.AsNoTracking().SingleAsync();
        Assert.Equal((Guid.Parse(cliente), "Escuela Benito Juárez", 1000, nota, true, true, false, (string?)null),
            (registrado.Id, registrado.Nombre, registrado.DescuentoCentesimas, registrado.Nota, registrado.Activo, registrado.PorRevisar,
                registrado.MayoreoSiempre, registrado.Telefono));
        Assert.Equal(Guid.Parse(cliente), (await db.Ventas.AsNoTracking().SingleAsync()).ClienteId);
        Assert.DoesNotContain("ClientesEspeciales", await TablasAsync(db));
        Assert.Equal(1, await LlavesAsync(db));
    }

    [Fact]
    public async Task Al_regresar_la_migracion_vuelven_los_clientes_especiales()
    {
        var (conexion, db, migrador, cliente, _) = await BaseAnteriorAsync();
        await using var __ = conexion;
        await using var ___ = db;
        await migrador.MigrateAsync();

        await migrador.MigrateAsync(Anterior);

        var especial = await db.Database.SqlQueryRaw<string>("SELECT Id AS Value FROM ClientesEspeciales").SingleAsync();
        var deLaVenta = await db.Database.SqlQueryRaw<string>("SELECT ClienteEspecialId AS Value FROM Ventas").SingleAsync();
        Assert.Equal((cliente, cliente), (especial.ToUpperInvariant(), deLaVenta.ToUpperInvariant()));
        Assert.DoesNotContain("ClientesRegistrados", await TablasAsync(db));
        Assert.Equal(1, await LlavesAsync(db));
    }
}
