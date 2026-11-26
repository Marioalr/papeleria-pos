using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Al migrar la base (2026-11-22, <see cref="Folios"/>): la caja principal se queda sin letra; las secundarias y el celular, con la suya; lo que
/// ya se cobró, igual. Se puede correr otra vez sin que cambie nada, y con dos cajas marcadas como principal en una sucursal (no debería
/// pasar) solo una se queda sin letra: la serie es única en la sucursal.
/// </summary>
public class MigracionFoliosSinLetraTests
{
    private const string Anterior = "20261121182255_FormatoDelTicket";

    [Fact]
    public async Task La_principal_se_queda_sin_letra_y_lo_demas_igual()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);

        string Id() => Guid.NewGuid().ToString().ToUpperInvariant();
        const string Fecha = "'2026-11-01 12:00:00'";
        var (matriz, sucursal2, rara) = (Id(), Id(), Id());
        foreach (var (sucursal, clave) in new[] { (matriz, "M"), (sucursal2, "S2"), (rara, "R") })
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, {{1}}, 1, 1, {Fecha})", sucursal, clave);
        var cajas = new List<(string Id, string Sucursal, string Serie, bool Principal, string Nombre)>
        {
            (Id(), matriz, "A", true, "Caja 1"),
            (Id(), matriz, "B", false, "Caja 2"),
            (Id(), matriz, "CEL", false, "Celular"),
            (Id(), sucursal2, "A", true, "Caja principal"),
            (Id(), rara, "A", true, "Una"),
            (Id(), rara, "C", true, "Otra"),
        };
        foreach (var c in cajas)
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, 1, 1, {Fecha})",
                c.Id, c.Sucursal, c.Nombre, c.Serie, c.Principal ? 1 : 0);
        // Un ticket de antes de la principal, A-004812 (sin las llaves: solo importa que no cambie).
        var venta = Id();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Ventas (Id, SucursalId, CajaId, TurnoId, CajeroId, Serie, Folio, Estado, CreadaUtc, PagadaUtc, EnContingencia, IvaCentavos, RedondeoCentavos, " +
            $"TicketImpresiones, TicketSinImprimir, TotalCentavos) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, 'A', 4812, 'Pagada', {Fecha}, {Fecha}, 0, 0, 0, 1, 0, 1000)",
            venta, matriz, cajas[0].Id, Id(), Id());

        await migrador.MigrateAsync();
        var despues = await Series();
        Assert.Equal(["", "B", "CEL"], despues.Where(c => c.SucursalId == Guid.Parse(matriz)).OrderBy(c => c.Serie).Select(c => c.Serie));
        Assert.Equal("", despues.Single(c => c.SucursalId == Guid.Parse(sucursal2)).Serie);
        Assert.Single(despues, c => c.SucursalId == Guid.Parse(rara) && c.Serie == "");
        Assert.Single(despues, c => c.SucursalId == Guid.Parse(rara) && c.Serie != "");
        var vieja = await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == Guid.Parse(venta));
        Assert.Equal("A-4812", vieja.FolioTexto);
        // El sin letra de la matriz empieza después de lo de antes.
        Assert.Equal(4812, await new RegistroCaja(db).UltimoFolioAsync(Guid.Parse(matriz), Folios.SinLetra));

        // Otra vez (la de antes y de vuelta): nada cambia.
        await migrador.MigrateAsync(Anterior);
        await migrador.MigrateAsync();
        Assert.Equal(despues.OrderBy(c => c.Id).ToList(), (await Series()).OrderBy(c => c.Id).ToList());

        async Task<List<(Guid Id, Guid SucursalId, string Serie)>> Series() =>
            (await db.Cajas.AsNoTracking().Select(c => new { c.Id, c.SucursalId, c.Serie }).ToListAsync()).Select(c => (c.Id, c.SucursalId, c.Serie)).ToList();
    }
}
