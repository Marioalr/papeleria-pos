using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Trae de Control Print solo su configuración: costos (tinta, papel, mantenimiento, luz, depreciación, márgenes y
/// calibración) y la IP de la impresora; sus trabajos se quedan allá (docs/fase-1-impresion.md). La base de Control
/// Print solo se lee. Lo usa <c>importar-control-print</c> de PapeleriaPos.Herramientas.
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

    /// <summary>La IP (o nombre) de la impresora que lee Control Print; vacío si no la tiene.</summary>
    public static string? LeerImpresora(string ruta)
    {
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT value FROM settings WHERE key = 'app'";
        if (comando.ExecuteScalar() is not string json)
            return null;
        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.TryGetProperty("PrinterHost", out var host) && host.GetString() is { Length: > 0 } valor ? valor.Trim() : null;
    }
}
