using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// <c>PapeleriaPos.Servicio.exe pin-usuario --usuario …</c> (revisión del PR #144): le pone (o le cambia) el PIN a alguien en la base de
/// la configuración de esta PC (la misma que usa el servicio), sin arrancar el servicio. Es lo que queda instalado en la caja 2 (el
/// paquete de la tienda no trae las herramientas), para cuando al dueño se le olvide su PIN, y siempre corresponde a la versión
/// instalada de la base (<see cref="PinUsuario"/>, igual que <c>pin-usuario</c> de las herramientas). No pone la base al día: si le
/// faltan migraciones, las pone el servicio al arrancar, con su respaldo.
/// </summary>
public static class PinUsuarioDelServicio
{
    public const string Uso =
        "Uso: PapeleriaPos.Servicio.exe pin-usuario --usuario <su nombre, su usuario o su número de eleventa>. La base es la del servicio "
        + "(la de su configuración).";

    /// <param name="cadena">La conexión a la base del servicio, como la arma su configuración.</param>
    /// <param name="args">Lo que viene después de <c>pin-usuario</c>.</param>
    /// <param name="pedir">Lo que se teclea sin que se vea (<see cref="PinUsuario.Leer"/>).</param>
    /// <returns>El código de salida: 0 si quedó, 1 si no.</returns>
    public static async Task<int> CorrerAsync(string cadena, string[] args, Func<string, string?> pedir, int intentos, TextWriter salida,
        TextWriter errores)
    {
        if (args is not ["--usuario", var buscado] || buscado.Trim().Length == 0)
        {
            errores.WriteLine(Uso);
            return 1;
        }
        var ruta = new SqliteConnectionStringBuilder(cadena).DataSource;
        if (!File.Exists(ruta))
        {
            errores.WriteLine($"No está la base del servicio ({ruta}): se arma al instalarlo (docs/pruebas-en-tienda.md).");
            return 1;
        }
        await using var db = new PosDbContext(BaseDeDatos.Opciones(cadena));
        if ((await db.Database.GetPendingMigrationsAsync()).Any())
        {
            errores.WriteLine($"La base {ruta} todavía no está al día con esta versión: arranca el servicio (la pone al día, con su respaldo) "
                + "y vuelve a correr pin-usuario.");
            return 1;
        }
        salida.WriteLine($"Base del servicio: {ruta}");
        return await PinUsuario.PonerAsync(db, buscado, pedir, intentos, salida, errores);
    }
}
