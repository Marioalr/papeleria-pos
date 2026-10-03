using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PapeleriaPos.Datos;

public static class BaseDeDatos
{
    public static DbContextOptions<PosDbContext> Opciones(string cadenaConexion) =>
        new DbContextOptionsBuilder<PosDbContext>().UseSqlite(cadenaConexion).Options;

    /// <summary>Aplica las migraciones pendientes y deja la base en modo WAL.</summary>
    public static async Task PrepararAsync(PosDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}

/// <summary>Para <c>dotnet ef migrations add</c>: no se conecta a ninguna base real.</summary>
internal sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => new(BaseDeDatos.Opciones("Data Source=diseno.db"));
}
