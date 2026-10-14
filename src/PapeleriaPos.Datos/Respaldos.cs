using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PapeleriaPos.Datos;

/// <summary>
/// Copias de la base en la caja principal: una al día y otra con cada corte Z, con los últimos días de historia. Se
/// hacen con la copia en línea de SQLite, así que la venta no se detiene.
/// </summary>
public static class Respaldos
{
    private const string Prefijo = "pos-";

    /// <summary>Copia la base a <c>pos-AAAA-MM-DD[-etiqueta].db</c> y devuelve la ruta.</summary>
    public static string Hacer(string cadenaConexion, string carpeta, DateTime fechaLocal, string? etiqueta = null)
    {
        Directory.CreateDirectory(carpeta);
        var destino = Path.Combine(carpeta, $"{Prefijo}{fechaLocal:yyyy-MM-dd}{(etiqueta is null ? "" : "-" + etiqueta)}.db");
        var temporal = destino + ".tmp";
        File.Delete(temporal);
        using (var origen = new SqliteConnection(cadenaConexion))
        using (var copia = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporal, Pooling = false }.ToString()))
        {
            origen.Open();
            copia.Open();
            // También aquí synchronous=FULL: la copia queda en el disco antes de renombrarla (entrega 10).
            BaseDeDatos.Ajustar(origen);
            BaseDeDatos.Ajustar(copia);
            origen.BackupDatabase(copia);
        }
        // Se escribe aparte y se renombra: una copia a medias nunca queda con el nombre bueno.
        File.Move(temporal, destino, overwrite: true);
        return destino;
    }

    /// <summary>La copia más reciente (la diaria o la de un corte Z), o nada si no hay.</summary>
    public static string? Ultima(string carpeta) =>
        Directory.Exists(carpeta)
            ? Directory.EnumerateFiles(carpeta, Prefijo + "*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    /// <summary>Si ya está la copia diaria (sin etiqueta) de esa fecha.</summary>
    public static bool HayDelDia(string carpeta, DateOnly fecha) =>
        File.Exists(Path.Combine(carpeta, $"{Prefijo}{fecha:yyyy-MM-dd}.db"));

    /// <summary>Borra las copias de hace <paramref name="dias"/> días o más. Devuelve cuántas borró.</summary>
    public static int Limpiar(string carpeta, DateOnly hoy, int dias)
    {
        if (!Directory.Exists(carpeta))
            return 0;
        var limite = hoy.AddDays(-dias);
        var borradas = 0;
        foreach (var archivo in Directory.EnumerateFiles(carpeta, Prefijo + "*.db"))
        {
            var nombre = Path.GetFileName(archivo);
            if (nombre.Length >= Prefijo.Length + 10
                && DateOnly.TryParseExact(nombre.AsSpan(Prefijo.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
                && fecha <= limite)
            {
                File.Delete(archivo);
                borradas++;
            }
        }
        return borradas;
    }
}
