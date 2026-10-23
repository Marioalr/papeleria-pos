using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El historial de eleventa que ya se importó, al migrar la base (revisión del PR #131, 2026-10-22): sus artículos comunes quedan
/// marcados, como los marca ahora la importación, para que los reportes los cuenten como el de aquí (aparte y sin utilidad).
/// Son los que la importación creó para un código que no era del catálogo de eleventa: los de código COMUN-… y los del
/// departamento «Productos Comunes». Lo demás, igual.
/// </summary>
public class MigracionComunesDelHistorialTests
{
    private const string Anterior = "20261018020907_ContadoDeLaTemporada";

    [Fact]
    public async Task Los_comunes_que_ya_se_importaron_quedan_marcados()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        string Id() => Guid.NewGuid().ToString().ToUpperInvariant();
        const string Fecha = "'2026-10-01 12:00:00'";
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        var (sucursal, escritura, comunes, venta) = (Id(), Id(), Id(), Id());
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({{0}}, 'M', 'Matriz', 1, 1, {Fecha})", sucursal);
        foreach (var (id, nombre) in new[] { (escritura, "Escritura"), (comunes, "Productos Comunes") })
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO Departamentos (Id, Nombre, Activo, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, 1, 1, {Fecha})", id, nombre);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO VentasHistoricas (Id, SucursalId, FechaUtc, FolioOrigen, TotalCentavos, Cancelada) VALUES ({0}, {1}, '2025-10-22 16:00:00', '100', 0, 0)",
            venta, sucursal);
        // Como los dejó la importación: el Id de eleventa de cada producto del catálogo, o «codigo:» y el código de lo que no estaba.
        (string Nombre, string Departamento, string Origen, string Mapeo)[] importados =
        [
            ("Pluma", escritura, "eleventa", "1"),
            ("Recuerdo", comunes, "eleventa", "codigo:COMUN-4821-1"),
            ("Tarjeta", comunes, "eleventa", "codigo:VIEJO-1"),
            ("Bolsa de regalo", comunes, "eleventa", "2"),
            ("Folder viejo", escritura, "eleventa", "codigo:F-OLD"),
            ("Regalo", escritura, "eleventa", "codigo:comun-9-9"),
            ("De los ficticios", comunes, "ficticios", "codigo:COMUN-1-1"),
        ];
        foreach (var (nombre, departamento, origen, mapeo) in importados)
        {
            var (producto, variante) = (Id(), Id());
            foreach (var (sql, parametros) in new (string, object[])[]
                     {
                         ("INSERT INTO Productos (Id, Nombre, DepartamentoId, Tipo, Impuesto, Activo, PorCompletar, Version, ModificadoUtc) " +
                          $"VALUES ({{0}}, {{1}}, {{2}}, 'Mercancia', 'Iva16', 0, 0, 1, {Fecha})", [producto, nombre, departamento]),
                         ("INSERT INTO Variantes (Id, ProductoId, Descripcion, UnidadBase, Activa, ManejaCaducidad, Version, ModificadoUtc) " +
                          $"VALUES ({{0}}, {{1}}, '', 'Pieza', 0, 0, 1, {Fecha})", [variante, producto]),
                         ("INSERT INTO MapeosImportacion (Id, Origen, Entidad, IdOrigen, IdNuevo) VALUES ({0}, {1}, 'Variante', {2}, {3})",
                             [Id(), origen, mapeo, variante]),
                         ("INSERT INTO VentasHistoricasLineas (Id, VentaHistoricaId, VarianteId, Cantidad, PrecioCentavos, ImporteCentavos, CostoCentavos) " +
                          "VALUES ({0}, {1}, {2}, 1000, 1500, 1500, 0)", [Id(), venta, variante]),
                     })
                await db.Database.ExecuteSqlRawAsync(sql, parametros);
        }

        await migrador.MigrateAsync();

        var marcadas = await (from l in db.VentasHistoricasLineas.AsNoTracking()
                              join v in db.Variantes on l.VarianteId equals v.Id
                              join p in db.Productos on v.ProductoId equals p.Id
                              select new { p.Nombre, l.EsArticuloComun }).ToDictionaryAsync(x => x.Nombre, x => x.EsArticuloComun);
        Assert.Equal(new Dictionary<string, bool>
        {
            ["Pluma"] = false,
            ["Recuerdo"] = true,
            ["Tarjeta"] = true,
            ["Bolsa de regalo"] = false,
            ["Folder viejo"] = false,
            ["Regalo"] = true,
            ["De los ficticios"] = false,
        }, marcadas);
    }
}
