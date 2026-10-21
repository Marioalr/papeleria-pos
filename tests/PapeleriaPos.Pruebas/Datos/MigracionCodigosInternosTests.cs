using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Los EAN-13 que empiezan con 2 que importar o leer eleventa dejó como de fábrica pasan a internos al migrar la base,
/// como los da de alta el catálogo (<see cref="CodigoBarras.OrigenDe"/>). Los demás códigos se quedan como estaban.
/// </summary>
public class MigracionCodigosInternosTests
{
    private const string Anterior = "20261015170548_ConfiguracionNueva";

    [Fact]
    public async Task Los_EAN_13_que_empiezan_con_2_que_quedaron_de_fabrica_pasan_a_internos()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        // Como los dejaba la importación de eleventa: un producto por código, con su departamento, variante y pieza de verdad
        // (las llaves están activas, como en el servicio).
        (string Codigo, string Origen)[] antes =
        [
            ("2000000000015", "Fabrica"),
            ("2501234567898", "Fabrica"),
            ("7501035102446", "Fabrica"),
            ("200000000001", "Fabrica"),
            ("20000000000150", "Fabrica"),
            ("2000000000022", "Interno"),
            ("IMP", "Interno"),
        ];
        string Id() => Guid.NewGuid().ToString().ToUpperInvariant();
        const string Fecha = "'2026-10-01 12:00:00'";
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        var departamento = Id();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO Departamentos (Id, Nombre, Activo, Version, ModificadoUtc) VALUES ({{0}}, 'Papelería', 1, 1, {Fecha})", departamento);
        foreach (var (codigo, origen) in antes)
        {
            var (producto, variante, pieza) = (Id(), Id(), Id());
            foreach (var (sql, parametros) in new (string, object[])[]
                     {
                         ("INSERT INTO Productos (Id, Nombre, DepartamentoId, Tipo, Impuesto, Activo, PorCompletar, Version, ModificadoUtc) " +
                          $"VALUES ({{0}}, {{1}}, {{2}}, 'Mercancia', 'Iva16', 1, 0, 1, {Fecha})", [producto, $"Producto {codigo}", departamento]),
                         ("INSERT INTO Variantes (Id, ProductoId, Descripcion, UnidadBase, Activa, ManejaCaducidad, Version, ModificadoUtc) " +
                          $"VALUES ({{0}}, {{1}}, '', 'Pieza', 1, 0, 1, {Fecha})", [variante, producto]),
                         ("INSERT INTO Presentaciones (Id, VarianteId, Nombre, Factor, EsBase, Version, ModificadoUtc) " +
                          $"VALUES ({{0}}, {{1}}, 'Pieza', 1000, 1, 1, {Fecha})", [pieza, variante]),
                         ($"INSERT INTO CodigosBarras (Id, Codigo, PresentacionId, Origen, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, 1, {Fecha})",
                             [Id(), codigo, pieza, origen]),
                     })
                await db.Database.ExecuteSqlRawAsync(sql, parametros);
        }

        await migrador.MigrateAsync();

        Assert.Equal(new Dictionary<string, OrigenCodigo>
        {
            ["2000000000015"] = OrigenCodigo.Interno,
            ["2501234567898"] = OrigenCodigo.Interno,
            ["7501035102446"] = OrigenCodigo.Fabrica,
            ["200000000001"] = OrigenCodigo.Fabrica,
            ["20000000000150"] = OrigenCodigo.Fabrica,
            ["2000000000022"] = OrigenCodigo.Interno,
            ["IMP"] = OrigenCodigo.Interno,
        }, await db.CodigosBarras.AsNoTracking().ToDictionaryAsync(c => c.Codigo, c => c.Origen));
    }
}
