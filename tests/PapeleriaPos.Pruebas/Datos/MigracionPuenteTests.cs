using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La migración del puente con eleventa de la tercera revisión (2026-10-17): lo que quedó por revisar antes de que se guardara
/// por qué y desde cuándo lo recupera del texto que se guardó, y las devoluciones de eleventa que esperaban la pregunta
/// «¿Es la misma?» se quitan para que la lectura las vuelva a traer (ya no se adivina: se elige al marcar «Ya lo anoté»).
/// </summary>
public class MigracionPuenteTests
{
    private const string Anterior = "20261017154737_PuenteVueltaConPregunta";

    private static string Id() => Guid.NewGuid().ToString().ToUpperInvariant();

    [Fact]
    public async Task Lo_que_quedo_por_revisar_antes_recupera_por_que_y_desde_cuando()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        // Los textos que guardaba la bandeja, como quedaron en la base.
        var casos = new (string Error, string Estado, string? Antes, MotivoPorRevisar? Despues)[]
        {
            ("El ticket es de otra caja o de otro turno de eleventa: todavía no se ha visto cómo anota eleventa esa devolución. Anótala a mano en eleventa.",
                "PorRevisar", null, MotivoPorRevisar.AnotarAMano),
            ("Se hizo antes del corte de eleventa (16/10 21:00) y no alcanzó a llegar: su efectivo ya se contó allá. Si hace falta, anótalo a mano en eleventa.",
                "PorRevisar", null, MotivoPorRevisar.AnotarAMano),
            ("Se dejó para revisar a mano. El eleventa de 10.0.0.10 no terminó de contestar a tiempo: se vuelve a intentar.", "PorRevisar", null,
                MotivoPorRevisar.AnotarAMano),
            ("Eleventa ya no lo tiene: se perdió al restaurar un respaldo (se había mandado el 16/10 10:05). Anótalo a mano en eleventa.", "PorRevisar", null,
                MotivoPorRevisar.AnotarAMano),
            ("En eleventa el ticket #4521 ya está cancelado.", "PorRevisar", null, MotivoPorRevisar.EleventaYaLoTenia),
            ("En el ticket de eleventa ya no está «Pluma punto fino» con 2 por devolver.", "PorRevisar", null, MotivoPorRevisar.EleventaYaLoTenia),
            ("Su venta se anotó a mano en eleventa: no se sabe su ticket allá, también va a mano.", "PorRevisar", null, MotivoPorRevisar.Otro),
            ("Fue con tarjeta: a eleventa solo pasan solas las ventas en efectivo.", "PorRevisar", null, MotivoPorRevisar.Otro),
            ("En eleventa ya no está el ticket 4521.", "PorRevisar", null, MotivoPorRevisar.Otro),
            // Lo que ya tenía su porqué, o no está por revisar, no se toca.
            ("Se dejó para revisar a mano.", "PorRevisar", "EleventaYaLoTenia", MotivoPorRevisar.EleventaYaLoTenia),
            ("Se anotó a mano en eleventa. Se dejó para revisar a mano.", "AMano", null, null),
        };
        var ids = new List<string>();
        foreach (var (i, (error, estado, antes, _)) in casos.Index())
        {
            ids.Add(Id());
            // El último intento, en los de número par; los demás nunca se intentaron.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO EnviosAEleventa (Id, Tipo, Estado, CreadoUtc, Intentos, FolioRepetido, Error, PorRevisarPorque, UltimoIntentoUtc) "
                + "VALUES (@id, 'Devolucion', @estado, '2026-10-16 18:00:00', @intentos, 0, @error, @antes, @intento)",
                new SqliteParameter("@id", ids[^1]), new SqliteParameter("@estado", estado), new SqliteParameter("@intentos", i % 2 == 0 ? 1 : 0),
                new SqliteParameter("@error", error), new SqliteParameter("@antes", (object?)antes ?? DBNull.Value),
                new SqliteParameter("@intento", i % 2 == 0 ? "2026-10-16 18:05:00" : DBNull.Value));
        }

        await migrador.MigrateAsync();

        var despues = await db.EnviosAEleventa.AsNoTracking().ToDictionaryAsync(e => e.Id.ToString().ToUpperInvariant());
        foreach (var (i, (_, estado, antes, esperado)) in casos.Index())
        {
            var envio = despues[ids[i]];
            Assert.Equal(esperado, envio.PorRevisarPorque);
            // Desde el último intento o, si no hubo, desde que se creó; lo que no está por revisar no lleva.
            DateTime? desde = estado != "PorRevisar" ? null
                : i % 2 == 0 ? new DateTime(2026, 10, 16, 18, 5, 0) : new DateTime(2026, 10, 16, 18, 0, 0);
            Assert.Equal(desde, envio.PorRevisarUtc);
        }
    }

    [Fact]
    public async Task Lo_de_eleventa_que_esperaba_la_pregunta_se_quita_para_que_la_lectura_lo_vuelva_a_traer()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        // La venta de la caja 2 con dos devoluciones de aquí, con su sucursal, caja, turno y cajero de verdad (las llaves están
        // activas, como en el servicio).
        var (sucursal, cajero, caja, turno, venta, propia, pasada) = (Id(), Id(), Id(), Id(), Id(), Id(), Id());
        const string Fecha = "'2026-10-16 12:00:00'";
        foreach (var (sql, parametros) in new (string, object[])[]
                 {
                     ($"INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({{0}}, 'M', 'Matriz', 1, 1, {Fecha})", [sucursal]),
                     ("INSERT INTO Usuarios (Id, Nombre, NombreUsuario, PinHash, Rol, SucursalId, Activo, IntentosFallidos, Version, ModificadoUtc) " +
                      $"VALUES ({{0}}, 'Ana', 'ana', 'x', 'Cajero', {{1}}, 1, 0, 1, {Fecha})", [cajero, sucursal]),
                     ($"INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, 'Caja 2', 'B', 0, 1, 1, {Fecha})",
                         [caja, sucursal]),
                     ($"INSERT INTO Turnos (Id, SucursalId, CajaId, CajeroId, AbiertoUtc, FondoCentavos) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {Fecha}, 30000)",
                         [turno, sucursal, caja, cajero]),
                     ("INSERT INTO Ventas (Id, CajaId, CajeroId, CreadaUtc, EnContingencia, Estado, Folio, IvaCentavos, PagadaUtc, Serie, " +
                      $"SucursalId, TotalCentavos, TurnoId, RedondeoCentavos) VALUES ({{0}}, {{1}}, {{2}}, {Fecha}, 0, 'Pagada', 1, 0, {Fecha}, 'B', {{3}}, 2700, {{4}}, 0)",
                         [venta, caja, cajero, sucursal, turno]),
                 })
            await db.Database.ExecuteSqlRawAsync(sql, parametros);
        foreach (var devolucion in new[] { propia, pasada })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Devoluciones (Id, AlCreditoCentavos, CajaId, EsCancelacion, FechaUtc, Motivo, RedondeoCentavos, ReembolsoEfectivoCentavos, " +
                $"SucursalId, TotalCentavos, TurnoId, UsuarioId, VentaId) VALUES ({{0}}, 0, {{1}}, 0, {Fecha}, 'No pinta', 0, 900, {{2}}, 900, {{3}}, {{4}}, {{5}})",
                devolucion, caja, sucursal, turno, cajero, venta);
        // Una de eleventa que esperaba la pregunta (no se había pasado a su venta) y otra que ya se había pasado.
        const string Columnas = "Id, Articulos, DevolucionEleventa, DevueltaUtc, EsCancelacion, Propia, TicketEleventa, TotalCentavos, VentaId, VistaUtc";
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO DevolucionesDesdeEleventa ({Columnas}, QuizaEsDevolucionId, PorRevisar) "
            + "VALUES ({0}, '[]', 901, '2026-10-16 18:00:00', 0, 0, 500, 900, {1}, '2026-10-16 18:05:00', {2}, 'Puede ser la de aquí')",
            Id(), venta, propia);
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO DevolucionesDesdeEleventa ({Columnas}, DevolucionId) VALUES ({{0}}, '[]', 902, '2026-10-16 18:00:00', 0, 0, 500, 900, {{1}}, '2026-10-16 18:05:00', {{2}})",
            Id(), venta, pasada);

        await migrador.MigrateAsync();

        Assert.Equal([902], await db.Set<DevolucionDesdeEleventa>().AsNoTracking().Select(d => d.DevolucionEleventa).ToListAsync());
    }

    [Fact]
    public async Task Lo_de_eleventa_que_esperaba_la_pregunta_de_una_ya_anotada_a_mano_queda_ligado_con_ella()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        var (sucursal, cajero, caja, turno, venta, anotada, esperando) = (Id(), Id(), Id(), Id(), Id(), Id(), Id());
        const string Fecha = "'2026-10-16 12:00:00'";
        foreach (var (sql, parametros) in new (string, object[])[]
                 {
                     ($"INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({{0}}, 'M', 'Matriz', 1, 1, {Fecha})", [sucursal]),
                     ("INSERT INTO Usuarios (Id, Nombre, NombreUsuario, PinHash, Rol, SucursalId, Activo, IntentosFallidos, Version, ModificadoUtc) " +
                      $"VALUES ({{0}}, 'Ana', 'ana', 'x', 'Cajero', {{1}}, 1, 0, 1, {Fecha})", [cajero, sucursal]),
                     ($"INSERT INTO Cajas (Id, SucursalId, Nombre, Serie, EsPrincipal, Activa, Version, ModificadoUtc) VALUES ({{0}}, {{1}}, 'Caja 2', 'B', 0, 1, 1, {Fecha})",
                         [caja, sucursal]),
                     ($"INSERT INTO Turnos (Id, SucursalId, CajaId, CajeroId, AbiertoUtc, FondoCentavos) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {Fecha}, 30000)",
                         [turno, sucursal, caja, cajero]),
                     ("INSERT INTO Ventas (Id, CajaId, CajeroId, CreadaUtc, EnContingencia, Estado, Folio, IvaCentavos, PagadaUtc, Serie, " +
                      $"SucursalId, TotalCentavos, TurnoId, RedondeoCentavos) VALUES ({{0}}, {{1}}, {{2}}, {Fecha}, 0, 'Pagada', 1, 0, {Fecha}, 'B', {{3}}, 2700, {{4}}, 0)",
                         [venta, caja, cajero, sucursal, turno]),
                 })
            await db.Database.ExecuteSqlRawAsync(sql, parametros);
        // Dos devoluciones de aquí: una ya marcada «Ya lo anoté en eleventa» (la anotó allá en dos partes) y otra que todavía
        // espera su anotación a mano.
        foreach (var (devolucion, estado) in new[] { (anotada, "AMano"), (esperando, "PorRevisar") })
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Devoluciones (Id, AlCreditoCentavos, CajaId, EsCancelacion, FechaUtc, Motivo, RedondeoCentavos, ReembolsoEfectivoCentavos, " +
                $"SucursalId, TotalCentavos, TurnoId, UsuarioId, VentaId) VALUES ({{0}}, 0, {{1}}, 0, {Fecha}, 'No pinta', 0, 1800, {{2}}, 1800, {{3}}, {{4}}, {{5}})",
                devolucion, caja, sucursal, turno, cajero, venta);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO EnviosAEleventa (Id, Tipo, Estado, CreadoUtc, Intentos, FolioRepetido, DevolucionId, VentaId, PorRevisarPorque) " +
                $"VALUES ({{0}}, 'Devolucion', {{1}}, {Fecha}, 1, 0, {{2}}, {{3}}, 'AnotarAMano')", Id(), estado, devolucion, venta);
        }
        // En eleventa: las dos partes de la anotada, y otra que esperaba la pregunta de la que todavía espera.
        const string Columnas = "Id, Articulos, DevolucionEleventa, DevueltaUtc, EsCancelacion, Propia, TicketEleventa, TotalCentavos, VentaId, VistaUtc";
        foreach (var (enEleventa, quizaEs) in new[] { (901, anotada), (902, anotada), (903, esperando) })
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO DevolucionesDesdeEleventa ({Columnas}, QuizaEsDevolucionId, PorRevisar) "
                + "VALUES ({0}, '[]', {1}, '2026-10-16 18:00:00', 0, 0, 500, 900, {2}, '2026-10-16 18:05:00', {3}, 'Puede ser la de aquí')",
                Id(), enEleventa, venta, quizaEs);

        await migrador.MigrateAsync();

        // Las dos partes quedan ligadas con la anotada (no se vuelven a pasar a la venta); la otra se quita y la lectura la
        // vuelve a traer, para retenerla mientras la de aquí espere su anotación.
        var quedan = await db.Set<DevolucionDesdeEleventa>().AsNoTracking().OrderBy(d => d.DevolucionEleventa).ToListAsync();
        Assert.Equal([(901, true, (Guid?)Guid.Parse(anotada), (string?)null), (902, true, Guid.Parse(anotada), null)],
            quedan.Select(d => (d.DevolucionEleventa, d.Propia, d.DevolucionId, d.PorRevisar)));
    }
}
