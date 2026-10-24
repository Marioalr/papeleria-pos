using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Datos;

public static class BaseDeDatos
{
    public static DbContextOptions<PosDbContext> Opciones(string cadenaConexion) =>
        new DbContextOptionsBuilder<PosDbContext>().UseSqlite(cadenaConexion).Options;

    /// <summary>
    /// Aplica las migraciones pendientes, deja la base en modo WAL y pasa a los permisos por usuario lo que falte
    /// (<see cref="MigracionPermisos"/>: la primera vez, los roles de antes; después, nada o casi nada).
    /// </summary>
    public static async Task PrepararAsync(PosDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await MigracionPermisos.AplicarAsync(db, ct);
    }

    /// <summary>
    /// Pone los ajustes contra apagones (<see cref="AjustesContraApagones"/>: <c>synchronous=FULL</c> y <c>busy_timeout</c>)
    /// en una conexión ya abierta, las que no pasan por EF Core (<see cref="PosDbContext"/> lo hace solo en las suyas).
    /// </summary>
    public static void Ajustar(DbConnection conexion)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = AjustesContraApagones.Pragmas;
        comando.ExecuteNonQuery();
    }

    /// <summary>
    /// Lee de vuelta, en una conexión de la base, <c>synchronous</c> y <c>busy_timeout</c>. Devuelve qué está mal, o nada
    /// si quedaron como en <see cref="AjustesContraApagones.Pragmas"/>. El servicio lo revisa al arrancar.
    /// </summary>
    public static async Task<string?> RevisarAjustesAsync(PosDbContext db, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var conexion = db.Database.GetDbConnection();
            return AjustesContraApagones.Problema(Convert.ToInt32(await EscalarAsync(conexion, "PRAGMA synchronous", ct)),
                Convert.ToInt32(await EscalarAsync(conexion, "PRAGMA busy_timeout", ct)));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// La revisión rápida de SQLite (<c>PRAGMA quick_check</c>): que cada página y cada renglón estén sanos, sin comparar
    /// los índices contra las tablas (por eso es rápida). Devuelve <c>["ok"]</c> si está bien; si no, lo que encontró (los
    /// primeros 20), o el error si la base ni siquiera se puede leer.
    /// </summary>
    public static IReadOnlyList<string> RevisionRapida(string cadenaConexion) => Revision(cadenaConexion, "quick_check");

    /// <summary>
    /// La revisión completa (<c>PRAGMA integrity_check</c>): lo de <see cref="RevisionRapida"/> y además que cada índice tenga lo
    /// mismo que su tabla. Tarda más (con la base de un año de la tienda, un par de segundos); se usa con un respaldo antes de
    /// restaurarlo (2026-10-23, revisión del PR #136): un índice que no cuadra con su tabla da búsquedas y cuentas equivocadas
    /// sin que nada truene.
    /// </summary>
    public static IReadOnlyList<string> RevisionCompleta(string cadenaConexion) => Revision(cadenaConexion, "integrity_check");

    private static IReadOnlyList<string> Revision(string cadenaConexion, string pragma)
    {
        try
        {
            using var conexion = new SqliteConnection(cadenaConexion);
            conexion.Open();
            Ajustar(conexion);
            using var comando = conexion.CreateCommand();
            comando.CommandText = $"PRAGMA {pragma}(20)";
            using var lector = comando.ExecuteReader();
            var renglones = new List<string>();
            while (lector.Read())
                renglones.Add(lector.GetString(0));
            return renglones;
        }
        catch (SqliteException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>
    /// Las migraciones que tiene una base (un respaldo) y esta versión del programa no conoce: la dejó así una versión más nueva
    /// (revisión del PR #136). Restaurarla con esta la dejaría con tablas o columnas que esta no sabe llenar. Vacía si las conoce
    /// todas (o si la base todavía no tiene migraciones).
    /// </summary>
    public static IReadOnlyList<string> MigracionesDesconocidas(string cadenaConexion)
    {
        List<string> aplicadas = [];
        using (var conexion = new SqliteConnection(cadenaConexion))
        {
            conexion.Open();
            using var existe = conexion.CreateCommand();
            existe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
            if (Convert.ToInt64(existe.ExecuteScalar()) > 0)
            {
                using var comando = conexion.CreateCommand();
                comando.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
                using var lector = comando.ExecuteReader();
                while (lector.Read())
                    aplicadas.Add(lector.GetString(0));
            }
        }
        // Las de esta versión salen del programa, sin abrir ninguna base.
        using var db = new PosDbContext(Opciones("Data Source=:memory:"));
        var conocidas = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        return aplicadas.Where(m => !conocidas.Contains(m)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// EF Core pone un renglón en <c>__EFMigrationsLock</c> mientras migra y lo quita al terminar. Si el servicio se cortó a
    /// media migración (un apagón, o una base tan dañada que la migración falló), el renglón se queda y la siguiente
    /// espera para siempre. Solo el servicio migra su base, así que al arrancar después de un corte se quita. Devuelve si
    /// había uno.
    /// </summary>
    public static bool QuitarCandadoDeMigraciones(string cadenaConexion)
    {
        using var conexion = new SqliteConnection(cadenaConexion);
        conexion.Open();
        Ajustar(conexion);
        using var existe = conexion.CreateCommand();
        existe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock'";
        if (Convert.ToInt64(existe.ExecuteScalar()) == 0)
            return false;
        using var quitar = conexion.CreateCommand();
        quitar.CommandText = "DELETE FROM \"__EFMigrationsLock\"";
        return quitar.ExecuteNonQuery() > 0;
    }

    /// <summary>Si <see cref="RevisionRapida"/> no encontró nada.</summary>
    public static bool EstaSana(IReadOnlyList<string> revision) => revision is ["ok"];

    private static async Task<object?> EscalarAsync(DbConnection conexion, string sql, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteScalarAsync(ct);
    }
}

/// <summary>Pone los ajustes contra apagones (<see cref="AjustesContraApagones"/>) en cada conexión que abre EF Core.</summary>
internal sealed class AjustesAlAbrir : DbConnectionInterceptor
{
    public static readonly AjustesAlAbrir Instancia = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => BaseDeDatos.Ajustar(connection);

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var comando = connection.CreateCommand();
        comando.CommandText = AjustesContraApagones.Pragmas;
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Para <c>dotnet ef migrations add</c>: no se conecta a ninguna base real.</summary>
internal sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args) => new(BaseDeDatos.Opciones("Data Source=diseno.db"));
}
