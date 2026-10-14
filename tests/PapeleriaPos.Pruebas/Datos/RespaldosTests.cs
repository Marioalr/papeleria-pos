using Microsoft.Data.Sqlite;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Pruebas.Datos;

public sealed class RespaldosTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"pos-respaldos-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    [Fact]
    public async Task La_copia_es_una_base_completa_que_se_puede_abrir()
    {
        Directory.CreateDirectory(_carpeta);
        var original = $"Data Source={Path.Combine(_carpeta, "pos.db")};Pooling=False";
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(original)))
        {
            await BaseDeDatos.PrepararAsync(db);
            db.Sucursales.Add(new PapeleriaPos.Nucleo.Catalogo.Sucursal { Clave = "M", Nombre = "Matriz" });
            await db.SaveChangesAsync();
        }

        var ruta = Respaldos.Hacer(original, Path.Combine(_carpeta, "respaldos"), new DateTime(2026, 10, 4, 21, 30, 0), "z-2130");

        Assert.EndsWith("pos-2026-10-04-z-2130.db", ruta);
        Assert.False(File.Exists(ruta + ".tmp"));
        await using var copia = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta};Pooling=False"));
        Assert.Equal("Matriz", copia.Sucursales.Single().Nombre);
    }

    [Fact]
    public void Se_quedan_los_ultimos_30_dias()
    {
        Directory.CreateDirectory(_carpeta);
        foreach (var nombre in new[] { "pos-2026-09-03.db", "pos-2026-09-04.db", "pos-2026-09-05-z-2100.db", "pos-2026-10-04.db", "otro.db" })
            File.WriteAllText(Path.Combine(_carpeta, nombre), "");

        var borradas = Respaldos.Limpiar(_carpeta, new DateOnly(2026, 10, 4), dias: 30);

        Assert.Equal(2, borradas);
        Assert.Equal(["otro.db", "pos-2026-09-05-z-2100.db", "pos-2026-10-04.db"],
            Directory.GetFiles(_carpeta).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(Respaldos.HayDelDia(_carpeta, new DateOnly(2026, 10, 4)));
        Assert.False(Respaldos.HayDelDia(_carpeta, new DateOnly(2026, 9, 5)));
    }

    [Fact]
    public void La_ultima_copia_es_la_mas_reciente_aunque_sea_de_un_corte()
    {
        Assert.Null(Respaldos.Ultima(_carpeta));
        Directory.CreateDirectory(_carpeta);
        foreach (var (nombre, hora) in new[] { ("pos-2026-10-11.db", 9), ("pos-2026-10-11-z-2105.db", 21), ("pos-2026-10-12.db", 8), ("otro.db", 23) })
        {
            var ruta = Path.Combine(_carpeta, nombre);
            File.WriteAllText(ruta, "");
            File.SetLastWriteTimeUtc(ruta, new DateTime(2026, 10, nombre.Contains("10-12") ? 12 : 11, hora, 0, 0, DateTimeKind.Utc));
        }

        Assert.Equal("pos-2026-10-12.db", Path.GetFileName(Respaldos.Ultima(_carpeta)));
    }

    [Fact]
    public void La_llave_de_la_caja_solo_se_guarda_como_huella()
    {
        var caja = new Caja { Nombre = "Caja 2", Serie = "B" };

        var llave = caja.DarDeAlta("PC-CAJA2", new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc));
        var huella = caja.LlaveHuella;
        var otra = caja.DarDeAlta("PC-NUEVA", new DateTime(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc));

        Assert.Equal(LlaveCaja.Huella(llave), huella);
        Assert.DoesNotContain(llave, huella!);
        Assert.NotEqual(llave, otra);
        Assert.Equal("PC-NUEVA", caja.Equipo);
        caja.QuitarEquipo();
        Assert.False(caja.TieneEquipo);
        Assert.Null(caja.Equipo);
    }
}
