using Microsoft.Data.Sqlite;

namespace PapeleriaPos.Pruebas;

/// <summary>
/// Las bases de SQLite que una prueba deja en la carpeta temporal (la caja-local.db de una caja, o la de un servicio). En
/// Windows el archivo puede seguir abierto un instante después de la prueba (el escritor de la caja terminaba algo de fondo,
/// o una conexión seguía en la alberca): se reintenta, y si no, se queda en la carpeta temporal. Una limpieza fallida no
/// tumba la corrida (como <c>ServicioDePrueba.BorrarAsync</c>).
/// </summary>
internal static class BasesDePrueba
{
    /// <summary>La base y sus archivos -wal y -shm.</summary>
    /// <param name="cadena">La cadena con que se abrió (la de su alberca de conexiones); sin ella, la de la caja: la ruta.</param>
    public static void Borrar(string ruta, string? cadena = null)
    {
        // Solo la alberca de esta base: vaciarlas todas (ClearAllPools) le quitaría su conexión a otra prueba que corre a la
        // par, a media escritura.
        using var deLaBase = new SqliteConnection(cadena ?? new SqliteConnectionStringBuilder { DataSource = ruta }.ToString());
        foreach (var archivo in new[] { ruta, ruta + "-wal", ruta + "-shm" })
            BorrarArchivo(archivo, deLaBase);
    }

    private static void BorrarArchivo(string archivo, SqliteConnection deLaBase)
    {
        for (var intento = 1; ; intento++)
        {
            // Lo que regresó a la alberca después del intento anterior también se cierra.
            SqliteConnection.ClearPool(deLaBase);
            try
            {
                File.Delete(archivo);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (intento >= 30)
                    return;
                Thread.Sleep(100);
            }
        }
    }
}
