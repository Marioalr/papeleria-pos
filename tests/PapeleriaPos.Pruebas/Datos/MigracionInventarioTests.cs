using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La migración del mínimo y el máximo de inventario (2026-10-17): lo que ya estaba queda sin mínimo ni máximo propio (cuenta
/// como antes, 3 o menos), se les puede poner, y los movimientos de unos días tienen su índice por fecha.
/// </summary>
public class MigracionInventarioTests
{
    private const string Anterior = "20261015170548_ConfiguracionNueva";

    [Fact]
    public async Task Lo_que_ya_estaba_queda_sin_minimo_ni_maximo_se_les_puede_poner_y_los_movimientos_tienen_su_indice_por_fecha()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        var (departamento, producto, variante) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string Texto(Guid id) => id.ToString().ToUpperInvariant();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Departamentos (Id, Nombre, Activo, Version, ModificadoUtc) VALUES ({0}, 'Útiles', 1, 1, '2026-10-01 12:00:00')", Texto(departamento));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Productos (Id, Nombre, DepartamentoId, Tipo, Impuesto, Activo, PorCompletar, Version, ModificadoUtc) "
            + "VALUES ({0}, 'Cuaderno', {1}, 'Mercancia', 'Iva16', 1, 0, 1, '2026-10-01 12:00:00')", Texto(producto), Texto(departamento));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Variantes (Id, ProductoId, Descripcion, UnidadBase, ManejaCaducidad, Activa, Version, ModificadoUtc) "
            + "VALUES ({0}, {1}, '', 'Pieza', 0, 1, 1, '2026-10-01 12:00:00')", Texto(variante), Texto(producto));

        await migrador.MigrateAsync();

        var antes = await db.Variantes.SingleAsync(v => v.Id == variante);
        Assert.Equal(((Cantidad?)null, (Cantidad?)null), (antes.InventarioMinimo, antes.InventarioMaximo));
        (antes.InventarioMinimo, antes.InventarioMaximo) = (Cantidad.Piezas(5), Cantidad.Piezas(20));
        await db.SaveChangesAsync();
        // En milésimas, como las demás cantidades.
        Assert.Equal([5000L, 20000L], await db.Database.SqlQueryRaw<long>(
            "SELECT InventarioMinimo AS Value FROM Variantes UNION ALL SELECT InventarioMaximo FROM Variantes").ToListAsync());
        Assert.Equal(["IX_MovimientosInventario_SucursalId_FechaUtc"], await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_MovimientosInventario_SucursalId_FechaUtc'").ToListAsync());
    }
}
