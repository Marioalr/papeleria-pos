using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Herramientas;

/// <summary>
/// Trae de Control Print solo su configuración de costos (tinta, papel, mantenimiento, luz, depreciación, márgenes y
/// calibración); sus trabajos se quedan allá (docs/fase-1-impresion.md). La base de Control Print solo se lee.
/// </summary>
public static class ImportacionControlPrint
{
    public static string RutaPorOmision =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControlPrint", "controlprint.db");

    public static CostSettings LeerCostos(string ruta)
    {
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT value FROM settings WHERE key = 'cost'";
        return comando.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<CostSettings>(json, CostSettings.Json) ?? throw new InvalidOperationException("La configuración de Control Print está vacía.")
            : throw new InvalidOperationException("Esa base de Control Print no tiene configuración de costos.");
    }
}
