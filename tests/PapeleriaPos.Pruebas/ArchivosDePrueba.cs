using Microsoft.Data.Sqlite;

namespace PapeleriaPos.Pruebas;

/// <summary>
/// Borra lo que una prueba dejó en la carpeta temporal (la base local de una caja, o su carpeta) sin que la limpieza tumbe
/// la corrida. Quien usaba la base ya se cerró (<see cref="PapeleriaPos.Interfaz.Contingencia.Cerrar"/>), pero un hilo que
/// terminaba de leer o escribir puede tenerla abierta un instante: en Windows el archivo no se deja borrar («being used by
/// another process») y en Linux la carpeta no queda vacía (la conexión vuelve a crear el -wal). Antes de cada intento se
/// sueltan las conexiones guardadas; se reintenta un rato y, si no se puede, se queda en la carpeta temporal, como en
/// <see cref="Servicio.ServicioDePrueba"/>.
/// </summary>
internal static class ArchivosDePrueba
{
    private const int Intentos = 30;
    private static readonly TimeSpan EntreIntentos = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Una base SQLite y los archivos que la acompañan (-wal, -shm, -journal). Se sueltan solo las conexiones de esa base (la
    /// cadena es la de la caja: su ruta): soltar las de todas (<see cref="SqliteConnection.ClearAllPools"/>) le cerraba la suya
    /// a otra prueba que corría a la par, a media escritura.
    /// </summary>
    public static void BorrarBase(string ruta)
    {
        using var deLaBase = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta }.ToString());
        foreach (var archivo in new[] { ruta, ruta + "-wal", ruta + "-shm", ruta + "-journal" })
            Reintentar(() => File.Delete(archivo), () => SqliteConnection.ClearPool(deLaBase));
    }

    /// <summary>Una carpeta con lo que tenga (sus bases pueden ser de cualquier cadena: se sueltan las conexiones de todas).</summary>
    public static void BorrarCarpeta(string carpeta) => Reintentar(() =>
    {
        if (Directory.Exists(carpeta))
            Directory.Delete(carpeta, recursive: true);
    }, SqliteConnection.ClearAllPools);

    private static void Reintentar(Action borrar, Action soltar)
    {
        for (var intento = 1; ; intento++)
        {
            soltar();
            try
            {
                borrar();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (intento == Intentos)
                    return;
                Thread.Sleep(EntreIntentos);
            }
        }
    }
}
