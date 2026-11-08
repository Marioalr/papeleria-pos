using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La migración de varias impresoras (2026-11-03): la impresora que tenía cada sucursal pasa a ser su impresora 1, con su IP, su aviso,
/// cada cuánto se lee, sus lecturas y lo que salió de ella, y sus costos no cambian. Al regresarla, vuelve a ser la de la sucursal.
/// </summary>
public class MigracionVariasImpresorasTests
{
    private const string Anterior = "20261029151304_TrabajosControlPrintCobrados";

    private static string Texto(Guid id) => id.ToString().ToUpperInvariant();

    [Fact]
    public async Task La_impresora_de_cada_sucursal_pasa_a_ser_la_1_con_sus_lecturas_sus_trabajos_y_sus_costos()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);

        // La matriz: su impresora con IP, aviso al 15 % y lecturas cada 7 minutos, sus costos (el negro a $999), dos lecturas y una impresión.
        // La sucursal 2: nunca se configuró, pero tiene una lectura.
        var (matriz, sucursal2, caja, trabajo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var costos = new CostSettings();
        costos.Black.Price = 999;
        var costosJson = JsonSerializer.Serialize(costos, CostSettings.Json);
        foreach (var (id, clave) in new[] { (matriz, "M"), (sucursal2, "S2") })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({0}, {1}, {1}, 1, 1, '2026-10-01 12:00:00')",
                Texto(id), clave);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) "
            + "VALUES ({0}, {1}, 'Caja 1', 'A', 1, 1, 1, '2026-10-01 12:00:00')", Texto(caja), Texto(matriz));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ConfiguracionesImpresion (SucursalId, CostosJson, ModificadoUtc, ImpresoraHost, AvisoTintaPorcentaje, MinutosEntreLecturas) "
            + "VALUES ({0}, {1}, '2026-10-20 12:00:00', '192.0.2.50', 15, 7)", Texto(matriz), costosJson);
        foreach (var (sucursal, minuto, contador) in new[] { (matriz, 0, 1000L), (matriz, 30, 1006L), (sucursal2, 0, 500L) })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO LecturasImpresora (Id, SucursalId, TomadaUtc, TintaK, TintaC, TintaM, TintaY, Mantenimiento, Contador) "
                + "VALUES ({0}, {1}, {2}, 50, 40, 30, 20, 60, {3})",
                Texto(Guid.NewGuid()), Texto(sucursal), $"2026-11-02 18:{minuto:00}:00", contador);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO TrabajosImpresion (Id, SucursalId, CajaId, Pc, Cola, SpoolId, Documento, ImpresoUtc, Estado, Paginas, Copias, Caras, CarasColor, "
            + "Hojas, Color, Duplex, Tamano, TipoPapel, Calidad, Medido, CostoTintaCentavos, CostoPapelCentavos, CostoOtrosCentavos, CostoCentavos, "
            + "MargenPorcentaje, PrecioCentavos, DetalleJson, Cobro, TintaK) VALUES ({0}, {1}, {2}, 'CAJA2', 'WF-6590', 7, 'Tarea.pdf', "
            + "'2026-11-02 18:10:00', 'Impreso', 6, 1, 6, 0, 6, 0, 0, 'Carta', 'Papel normal', 'Standard', 1, 120, 60, 30, 210, '100', 600, '{{}}', "
            + "'PorCobrar', 4.5)", Texto(trabajo), Texto(matriz), Texto(caja));

        await migrador.MigrateAsync();

        var impresoras = await db.Set<Impresora>().AsNoTracking().OrderBy(i => i.Numero).ToListAsync();
        var deLaMatriz = Assert.Single(impresoras, i => i.SucursalId == matriz);
        Assert.Equal((1, "WF-6590", PerfilImpresora.WF6590, "192.0.2.50", 15, 7, true, (string?)null),
            (deLaMatriz.Numero, deLaMatriz.Nombre, deLaMatriz.Perfil, deLaMatriz.Host, deLaMatriz.AvisoTintaPorcentaje, deLaMatriz.MinutosEntreLecturas,
                deLaMatriz.Activa, deLaMatriz.CostosJson));
        var deLa2 = Assert.Single(impresoras, i => i.SucursalId == sucursal2);
        Assert.Equal((1, (string?)null, Impresora.AvisoTintaPorOmision, Impresora.MinutosEntreLecturasPorOmision),
            (deLa2.Numero, deLa2.Host, deLa2.AvisoTintaPorcentaje, deLa2.MinutosEntreLecturas));
        Assert.NotEqual(deLaMatriz.Id, deLa2.Id);
        // Un GUID como los guarda el POS: se encuentra por su Id.
        Assert.NotNull(await db.Set<Impresora>().FindAsync(deLaMatriz.Id));

        // Sus lecturas y lo que salió de ella, ligados a ella; nada sin impresora.
        Assert.All(await db.Set<LecturaImpresora>().AsNoTracking().ToListAsync(),
            l => Assert.Equal(l.SucursalId == matriz ? deLaMatriz.Id : deLa2.Id, l.ImpresoraId));
        Assert.Equal(deLaMatriz.Id, (await db.Set<TrabajoImpresion>().AsNoTracking().SingleAsync()).ImpresoraId);

        // Los servicios del módulo la ven igual que antes: su última lectura, su IP y sus costos (los de la sucursal, sin nada propio).
        var servicio = ImpresionesDelPos.Impresora(db);
        var primera = (await servicio.PrimeraAsync(matriz))!;
        Assert.Equal(1006, (await servicio.UltimaAsync(primera))!.Contador);
        Assert.Equal("192.0.2.50", (await servicio.EstadoAsync(matriz, null)).Configuracion.Host);
        Assert.Equal(999, (await servicio.CostosAsync(primera)).Black.Price);
        Assert.Equal(costosJson, (await db.Set<ConfiguracionImpresion>().AsNoTracking().SingleAsync()).CostosJson);
        // La configuración de la sucursal ya no dice cómo se lee la impresora: eso es de cada una.
        Assert.DoesNotContain("ImpresoraHost", await Columnas(db, "ConfiguracionesImpresion"));

        // Al regresarla, la impresora 1 vuelve a ser la de la sucursal, y sus lecturas y trabajos siguen ahí.
        await migrador.MigrateAsync(Anterior);
        Assert.Equal(["192.0.2.50|15|7"], await db.Database.SqlQueryRaw<string>(
            "SELECT ImpresoraHost || '|' || AvisoTintaPorcentaje || '|' || MinutosEntreLecturas AS Value FROM ConfiguracionesImpresion").ToListAsync());
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM LecturasImpresora").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM TrabajosImpresion").SingleAsync());
        Assert.DoesNotContain("ImpresoraId", await Columnas(db, "TrabajosImpresion"));
    }

    private static async Task<List<string>> Columnas(PosDbContext db, string tabla) =>
        await db.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({tabla})").ToListAsync();
}
