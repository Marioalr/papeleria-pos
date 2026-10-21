using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El modelo de la base (lo que dicen las clases y su mapeo) es el mismo que dejó la última migración. Si alguien cambia
/// una tabla y olvida su migración, esto falla aquí y no en la base de la tienda, que se quedaría sin el cambio. Mover
/// clases de proyecto o de espacio de nombres no cambia el esquema: esto también lo comprueba.
/// </summary>
public class MigracionesTests
{
    [Fact]
    public void El_modelo_no_tiene_cambios_sin_su_migracion()
    {
        using var db = new PosDbContext(BaseDeDatos.Opciones("Data Source=:memory:"));

        Assert.False(db.Database.HasPendingModelChanges(),
            "El modelo cambió y falta su migración: dotnet ef migrations add <Nombre> -p src/PapeleriaPos.Datos -s src/PapeleriaPos.Datos -o Migraciones");
    }

    /// <summary>La última migración de main antes de juntar las ramas del 16 y 17 de octubre de 2026.</summary>
    private const string AntesDeLasRamas = "20261015170548_ConfiguracionNueva";

    /// <summary>
    /// Las migraciones de las ramas que se juntaron van intercaladas por su fecha (el puente, #109, entre las del inventario,
    /// #114, y «al cobrar», #118). Regresarlas hasta la de antes deja la base igual que subir desde cero hasta ahí, y volver a
    /// aplicarlas, igual que aplicarlas de una vez. Al regresar una migración, SQLite rehace la tabla con el modelo de la
    /// migración de antes: si ese modelo es el de otra rama y no trae la tabla, regresar truena; si la trae sin lo que le
    /// agregó otra rama, se lleva esas columnas. Por eso el modelo de una migración trae también lo de las ramas que van
    /// antes que ella.
    /// </summary>
    [Fact]
    public void Las_migraciones_de_las_ramas_se_regresan_y_se_vuelven_a_aplicar_igual()
    {
        using var conexion = Abierta();
        using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        migrador.Migrate();
        var completa = Esquema(conexion);

        migrador.Migrate(AntesDeLasRamas);
        using (var limpia = Abierta())
        {
            using var otra = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(limpia).Options);
            otra.GetService<IMigrator>().Migrate(AntesDeLasRamas);
            Assert.Equal(Esquema(limpia), Esquema(conexion));
        }

        migrador.Migrate();
        Assert.Equal(completa, Esquema(conexion));
    }

    private static SqliteConnection Abierta()
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        conexion.Open();
        return conexion;
    }

    /// <summary>Cada tabla con sus columnas, sus llaves foráneas y sus índices, sin importar el orden.</summary>
    private static List<string> Esquema(SqliteConnection conexion)
    {
        var renglones = new List<string>();
        foreach (var tabla in Filas(conexion, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")
                     .Select(f => (string)f[0]!))
        {
            renglones.Add($"tabla {tabla}");
            renglones.AddRange(Filas(conexion, $"PRAGMA table_info(\"{tabla}\")")
                .Select(c => $"{tabla}.{c[1]} {c[2]} notnull={c[3]} default={c[4]} pk={c[5]}"));
            renglones.AddRange(Filas(conexion, $"PRAGMA foreign_key_list(\"{tabla}\")")
                .Select(f => $"{tabla} llave {f[3]} -> {f[2]}.{f[4]} al borrar {f[6]}"));
            renglones.AddRange(Filas(conexion, $"PRAGMA index_list(\"{tabla}\")")
                .Select(i => $"{tabla} índice {i[1]} único={i[2]} ({string.Join(",", Filas(conexion, $"PRAGMA index_info(\"{i[1]}\")").Select(x => x[2]))})"));
        }
        renglones.Sort(StringComparer.Ordinal);
        return renglones;
    }

    private static List<object?[]> Filas(SqliteConnection conexion, string sql)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        using var lector = comando.ExecuteReader();
        var filas = new List<object?[]>();
        while (lector.Read())
        {
            var fila = new object?[lector.FieldCount];
            for (var i = 0; i < lector.FieldCount; i++)
                fila[i] = lector.IsDBNull(i) ? null : lector.GetValue(i);
            filas.Add(fila);
        }
        return filas;
    }
}
