using Microsoft.Data.Sqlite;

namespace PapeleriaPos.Pruebas;

/// <summary>
/// Borra lo que una prueba dejó en la carpeta temporal (una base SQLite, o su carpeta) sin tocar lo de las demás pruebas y sin
/// que la limpieza tumbe la corrida.
/// </summary>
/// <remarks>
/// Una conexión que se cierra se queda guardada (el pool de Microsoft.Data.Sqlite) con el archivo abierto, y en Windows no se
/// deja borrar. Antes de borrar se sueltan las de las bases de la prueba, nunca las de todas
/// (<see cref="SqliteConnection.ClearAllPools"/>): xUnit corre las clases de pruebas a la par, y soltar las de todas le movía
/// las suyas a otra prueba que las estaba usando (una vez, a media escritura: «Cannot access a disposed object. Object name:
/// 'SQLitePCL.sqlite3'»). Quien usaba la base ya se cerró (<see cref="PapeleriaPos.Interfaz.Contingencia.Cerrar"/>), pero un
/// hilo que terminaba de leer o escribir puede tenerla abierta un instante: en Windows el archivo no se deja borrar («being used
/// by another process») y en Linux la carpeta no queda vacía (la conexión vuelve a crear el -wal). Se reintenta un rato, y si
/// no, se queda en la carpeta temporal: una limpieza fallida no tumba la corrida.
/// </remarks>
internal static class ArchivosDePrueba
{
    private const int Intentos = 30;
    private static readonly TimeSpan EntreIntentos = TimeSpan.FromMilliseconds(100);

    /// <summary>Suelta las conexiones guardadas con esa cadena, y solo esas: el pool es por cadena de conexión.</summary>
    public static void Soltar(string cadena)
    {
        using var conexion = new SqliteConnection(cadena);
        SqliteConnection.ClearPool(conexion);
    }

    /// <summary>
    /// La cadena de una base sin más opciones, como la arma la base local de la caja
    /// (<see cref="PapeleriaPos.Interfaz.AlmacenLocal"/>). Una prueba que abre así una base usa esta, para que
    /// <see cref="SoltarBase"/> suelte sus conexiones: armada a mano («Data Source=» y la ruta) sale otra cadena si la ruta
    /// lleva espacios.
    /// </summary>
    public static string Cadena(string ruta) => new SqliteConnectionStringBuilder { DataSource = ruta }.ToString();

    /// <summary>
    /// Las conexiones guardadas de una base abierta con <see cref="Cadena"/>. Las que se abren de otra forma (de solo lectura,
    /// por ejemplo) se sueltan con su cadena (<see cref="Soltar"/>).
    /// </summary>
    public static void SoltarBase(string ruta) => Soltar(Cadena(ruta));

    /// <summary>Una base y lo que la acompaña: -wal, -shm, -journal y las marcas que se den (como la de un apagón, «.en-uso»).</summary>
    public static void BorrarBase(string ruta, params string[] marcas)
    {
        foreach (var archivo in new[] { "", "-wal", "-shm", "-journal" }.Concat(marcas).Select(final => ruta + final))
            Reintentar(() => File.Delete(archivo), () => SoltarBase(ruta));
    }

    /// <summary>Un archivo que no es una base (la llave del servicio, por ejemplo), con la misma paciencia.</summary>
    public static void BorrarArchivo(string archivo) => Reintentar(() => File.Delete(archivo), () => { });

    /// <summary>
    /// Una carpeta con lo que tenga (en Linux, un archivo que se crea a media limpieza deja la carpeta «no vacía»; en Windows,
    /// uno abierto no se deja borrar). Antes de cada intento se sueltan las conexiones guardadas de sus bases.
    /// </summary>
    public static void BorrarCarpeta(string carpeta) => Reintentar(() =>
    {
        if (Directory.Exists(carpeta))
            Directory.Delete(carpeta, recursive: true);
    }, () =>
    {
        if (Directory.Exists(carpeta))
            foreach (var ruta in Directory.EnumerateFiles(carpeta, "*.db", SearchOption.AllDirectories))
                SoltarBase(ruta);
    });

    private static void Reintentar(Action borrar, Action soltar)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                soltar();
                borrar();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                // Ya no estaba.
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && intento < Intentos)
            {
                Thread.Sleep(EntreIntentos);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
