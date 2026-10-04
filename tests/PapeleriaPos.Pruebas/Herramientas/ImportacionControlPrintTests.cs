using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Pruebas.Herramientas;

public sealed class ImportacionControlPrintTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"controlprint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_ruta);
    }

    /// <summary>Una base como la de Control Print: la tabla settings con la configuración en JSON (enums como texto).</summary>
    private void CrearBase(string? json)
    {
        using var conexion = new SqliteConnection($"Data Source={_ruta};Pooling=False");
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        comando.ExecuteNonQuery();
        if (json is null)
            return;
        comando.CommandText = "INSERT INTO settings (key, value) VALUES ('cost', $v)";
        comando.Parameters.AddWithValue("$v", json);
        comando.ExecuteNonQuery();
    }

    [Fact]
    public void Lee_la_configuracion_de_costos_tal_como_la_guarda_control_print()
    {
        var original = new CostSettings { MarginPercent = 85, PaperMarginPercent = 30, MinimumPrice = 3, Scaling = ScalingMode.BySheetCost };
        CrearBase(JsonSerializer.Serialize(original, CostSettings.Json));

        var leida = ImportacionControlPrint.LeerCostos(_ruta);

        Assert.Equal(85, leida.MarginPercent);
        Assert.Equal(30, leida.PaperMarginPercent);
        Assert.Equal(3, leida.MinimumPrice);
        Assert.Equal(ScalingMode.BySheetCost, leida.Scaling);
    }

    [Fact]
    public void Sin_configuracion_avisa()
    {
        CrearBase(null);

        Assert.Throws<InvalidOperationException>(() => ImportacionControlPrint.LeerCostos(_ruta));
    }
}
