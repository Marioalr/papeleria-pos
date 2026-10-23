using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Solo corre con la variable <c>MEDIR_REPORTES</c>: mide, no prueba (y tarda).</summary>
public sealed class FactDeMedicionDeReportesAttribute : FactAttribute
{
    public FactDeMedicionDeReportesAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIR_REPORTES")))
            Skip = "Solo mide con la variable MEDIR_REPORTES.";
    }
}

/// <summary>
/// Cuánto tardan los reportes de ventas (entrega 9) con la papelería ficticia de siempre (<c>GeneradorFicticios</c>, unos 58
/// tickets al día), dos años: el año pasado se queda como historial de eleventa y este año pasa a ventas de este POS (con un
/// turno por día y su corte, cobros con tarjeta y alguna devolución), como quedará la base al dejar eleventa. Mide la consulta
/// sola y por la API (con el JSON), en esta PC y sin red de por medio, y anota qué índice usa cada suma. Para el plan; no
/// revisa nada.
/// </summary>
public sealed class MedicionReportesTests(ITestOutputHelper salida) : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"pos-medicion-reportes-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_ruta);

    /// <summary>Hoy en la medición: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde, con un año en cada lado.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private PosDbContext Base() => new(BaseDeDatos.Opciones(ArchivosDePrueba.Cadena(_ruta))) { Reloj = new RelojFijo(HoraMexico.AUtc(Ahora)) };

    /// <summary>
    /// La papelería ficticia de dos años hasta hoy; lo del último año pasa del historial a ventas de este POS, por SQL (como lo
    /// habría guardado la caja, sin pasar por el cobro).
    /// </summary>
    private async Task<(Guid Caja, Guid Dueno, int Ventas, int Historial)> PrepararAsync()
    {
        await using var db = Base();
        await BaseDeDatos.PrepararAsync(db);
        await GuardadoLote.GuardarAsync(GeneradorFicticios.Generar(new OpcionesFicticios { Dias = 730, UltimoDia = DateOnly.FromDateTime(Ahora) }), db);
        await PreparacionCaja.PrepararAsync(db);
        var caja = await db.Cajas.SingleAsync();
        var cajero = await db.Usuarios.SingleAsync(u => u.NombreUsuario == "cajero");
        var dueno = await db.Usuarios.SingleAsync(u => u.NombreUsuario == "dueno");
        var desde = HoraMexico.AUtc(new DateTime(2025, 10, 22));

        // Un turno por día del último año, con su corte (las diferencias, de −$20 a $10).
        var dias = (await db.VentasHistoricas.Where(v => v.FechaUtc >= desde).Select(v => v.FechaUtc).ToListAsync())
            .GroupBy(f => DateOnly.FromDateTime(HoraMexico.ALocal(f))).ToList();
        var azar = new Random(9);
        var turnos = new Dictionary<DateOnly, Guid>();
        foreach (var dia in dias)
        {
            var turno = new Turno
            {
                SucursalId = caja.SucursalId, CajaId = caja.Id, CajeroId = cajero.Id, AbiertoUtc = dia.Min().AddHours(-1), CerradoUtc = dia.Max().AddHours(1),
                FondoCentavos = 50000,
            };
            var diferencia = azar.Next(-20, 11) * 100L;
            db.Turnos.Add(turno);
            db.Cortes.Add(new Corte
            {
                TurnoId = turno.Id, Tipo = TipoCorte.Z, FechaUtc = turno.CerradoUtc.Value, RealizadoPorId = dueno.Id, EsperadoCentavos = 50000,
                ContadoCentavos = 50000 + diferencia, DiferenciaCentavos = diferencia,
            });
            turnos[dia.Key] = turno.Id;
        }
        await db.SaveChangesAsync();

        var conexion = (SqliteConnection)db.Database.GetDbConnection();
        await conexion.OpenAsync();
        await using var tx = await conexion.BeginTransactionAsync();
        async Task Sql(string sql, params (string Nombre, object Valor)[] parametros)
        {
            await using var c = conexion.CreateCommand();
            c.CommandText = sql;
            foreach (var (nombre, valor) in parametros)
                c.Parameters.AddWithValue(nombre, valor);
            await c.ExecuteNonQueryAsync();
        }
        await Sql("CREATE TEMP TABLE TurnoDelDia (Dia TEXT PRIMARY KEY, TurnoId TEXT NOT NULL)");
        foreach (var (dia, turno) in turnos)
            await Sql("INSERT INTO TurnoDelDia VALUES ($dia, $turno)", ("$dia", dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$turno", turno.ToString().ToUpperInvariant()));
        var desdeTexto = desde.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        // Desde el 22 de octubre de 2025 la tienda está en UTC−6 todo el año.
        await Sql("""
            INSERT INTO Ventas (Id, SucursalId, CajaId, CajeroId, TurnoId, Serie, Folio, Estado, CreadaUtc, PagadaUtc, CanceladaUtc, CanceladaPorId,
                                TotalCentavos, RedondeoCentavos, IvaCentavos, EnContingencia)
            SELECT h.Id, h.SucursalId, $caja, $cajero, t.TurnoId, 'A', CAST(h.FolioOrigen AS INTEGER), CASE WHEN h.Cancelada THEN 'Cancelada' ELSE 'Pagada' END,
                   h.FechaUtc, h.FechaUtc, CASE WHEN h.Cancelada THEN h.FechaUtc END, CASE WHEN h.Cancelada THEN $cajero END, h.TotalCentavos, 0, 0, 0
            FROM VentasHistoricas AS h INNER JOIN TurnoDelDia AS t ON t.Dia = date(h.FechaUtc, '-6 hours')
            WHERE h.FechaUtc >= $desde
            """, ("$caja", caja.Id.ToString().ToUpperInvariant()), ("$cajero", cajero.Id.ToString().ToUpperInvariant()), ("$desde", desdeTexto));
        await Sql("""
            INSERT INTO VentasLineas (Id, VentaId, VarianteId, Descripcion, Impuesto, Unidad, LlevaInventario, Cantidad, CantidadBase, PrecioCentavos,
                                      ImporteCentavos, IvaCentavos, CostoCentavos)
            SELECT hl.Id, hl.VentaHistoricaId, hl.VarianteId, 'Producto', 'Iva16', v.UnidadBase, 1, hl.Cantidad, hl.Cantidad, hl.PrecioCentavos,
                   hl.ImporteCentavos, 0, hl.CostoCentavos
            FROM VentasHistoricasLineas AS hl INNER JOIN Ventas AS x ON x.Id = hl.VentaHistoricaId INNER JOIN Variantes AS v ON v.Id = hl.VarianteId
            """);
        // Uno de cada diez con tarjeta; lo demás en efectivo exacto.
        await Sql("""
            INSERT INTO Cobros (Id, VentaId, Forma, MontoCentavos, RecibidoCentavos, CambioCentavos, Verificacion)
            SELECT lower(hex(randomblob(16))), x.Id, CASE WHEN abs(random()) % 10 = 0 THEN 'Tarjeta' ELSE 'Efectivo' END, x.TotalCentavos,
                   x.TotalCentavos, 0, 'NoAplica'
            FROM Ventas AS x
            """);
        // Una devolución por cada cien tickets: su primer renglón completo, al día siguiente.
        await Sql("""
            CREATE TEMP TABLE PorDevolver AS
            SELECT x.Id AS VentaId, x.TurnoId, x.PagadaUtc,
                   (SELECT l.Id FROM VentasLineas AS l WHERE l.VentaId = x.Id ORDER BY l.Id LIMIT 1) AS LineaId, lower(hex(randomblob(16))) AS DevolucionId
            FROM Ventas AS x WHERE x.Estado = 'Pagada' AND x.Folio % 100 = 0
            """);
        await Sql("""
            INSERT INTO Devoluciones (Id, VentaId, SucursalId, CajaId, TurnoId, UsuarioId, EsCancelacion, Motivo, TotalCentavos, ReembolsoEfectivoCentavos,
                                      FechaUtc, RedondeoCentavos, AlCreditoCentavos)
            SELECT p.DevolucionId, p.VentaId, $sucursal, $caja, p.TurnoId, $cajero, 0, 'No era', l.ImporteCentavos, l.ImporteCentavos,
                   datetime(p.PagadaUtc, '+1 day'), 0, 0
            FROM PorDevolver AS p INNER JOIN VentasLineas AS l ON l.Id = p.LineaId
            """, ("$sucursal", caja.SucursalId.ToString().ToUpperInvariant()), ("$caja", caja.Id.ToString().ToUpperInvariant()),
            ("$cajero", cajero.Id.ToString().ToUpperInvariant()));
        await Sql("""
            INSERT INTO DevolucionesLineas (Id, DevolucionId, VentaLineaId, Cantidad, ImporteCentavos, CostoCentavos, RegresaAlInventario)
            SELECT lower(hex(randomblob(16))), p.DevolucionId, l.Id, l.Cantidad, l.ImporteCentavos, l.CostoCentavos, 1
            FROM PorDevolver AS p INNER JOIN VentasLineas AS l ON l.Id = p.LineaId
            """);
        await Sql("DELETE FROM VentasHistoricasLineas WHERE VentaHistoricaId IN (SELECT Id FROM Ventas)");
        await Sql("DELETE FROM VentasHistoricas WHERE Id IN (SELECT Id FROM Ventas)");
        await tx.CommitAsync();
        return (caja.Id, dueno.Id, await db.Ventas.CountAsync(), await db.VentasHistoricas.CountAsync());
    }

    private static double Mediana(List<double> valores) => valores.Order().ToList() is var l ? l[l.Count / 2] : 0;

    [FactDeMedicionDeReportes]
    public async Task Cada_periodo_contra_el_anio_pasado_con_dos_anios_de_la_papeleria_ficticia()
    {
        var reloj = Stopwatch.StartNew();
        var (caja, dueno, ventas, historial) = await PrepararAsync();
        salida.WriteLine($"Base: {ventas:N0} ventas de este POS (el último año) y {historial:N0} tickets del historial (el anterior), armada en {reloj.Elapsed.TotalSeconds:N1} s; "
            + $"{new FileInfo(_ruta).Length / 1048576.0:N1} MB.");

        // Lo que usa cada suma (sin estadísticas de la base, como en la tienda).
        await using (var db = Base())
        {
            var conexion = (SqliteConnection)db.Database.GetDbConnection();
            await conexion.OpenAsync();
            foreach (var tabla in new[] { "Ventas", "VentasHistoricas", "Devoluciones", "Cortes" })
            {
                await using var c = conexion.CreateCommand();
                c.CommandText = $"""
                    EXPLAIN QUERY PLAN SELECT COUNT(*) FROM "{tabla}" AS x
                    WHERE +x."{(tabla == "Cortes" ? "TurnoId" : "SucursalId")}" = '0' AND x."{(tabla == "Ventas" ? "PagadaUtc" : "FechaUtc")}" >= '2026-10-01'
                    """;
                await using var lector = await c.ExecuteReaderAsync();
                while (await lector.ReadAsync())
                    salida.WriteLine($"  {tabla}: {lector.GetString(3)}");
            }
        }

        // La consulta sola (como la llama el servicio), cinco veces cada periodo después de una para calentar.
        foreach (var periodo in Enum.GetValues<PeriodoReporte>())
        {
            var tiempos = new List<double>();
            ReporteVentasDto? reporte = null;
            for (var i = 0; i < 6; i++)
            {
                await using var db = Base();
                var cuanto = Stopwatch.StartNew();
                reporte = await new ReportesDeVentas(db).ArmarAsync(caja, dueno, periodo);
                if (i > 0)
                    tiempos.Add(cuanto.Elapsed.TotalMilliseconds);
            }
            salida.WriteLine($"{periodo}: mediana {Mediana(tiempos):N0} ms, la más lenta {tiempos.Max():N0} ms; venta neta {reporte!.Actual.VentaCentavos / 100m:N2} "
                + $"({reporte.Actual.Tickets:N0} tickets) contra {reporte.Anterior?.VentaCentavos / 100m:N2} ({reporte.Anterior?.Tickets:N0}).");
        }

        // Por la API, con el JSON: como lo pide la caja.
        await using var fabrica = new FabricaDePrueba(b => b.UseSetting("ConnectionStrings:Pos", $"Data Source={_ruta};Pooling=False")
            .UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false"));
        var http = fabrica.CreateClient();
        foreach (var periodo in Enum.GetValues<PeriodoReporte>())
        {
            var tiempos = new List<double>();
            long bytes = 0;
            for (var i = 0; i < 6; i++)
            {
                var cuanto = Stopwatch.StartNew();
                var respuesta = await http.GetAsync($"api/cajas/{caja}/reportes/ventas?usuarioId={dueno}&periodo={periodo}&fecha=2026-10-21");
                var cuerpo = await respuesta.Content.ReadAsByteArrayAsync();
                Assert.True(respuesta.IsSuccessStatusCode, System.Text.Encoding.UTF8.GetString(cuerpo));
                if (i > 0)
                    tiempos.Add(cuanto.Elapsed.TotalMilliseconds);
                bytes = cuerpo.Length;
            }
            salida.WriteLine($"API {periodo}: mediana {Mediana(tiempos):N0} ms, la más lenta {tiempos.Max():N0} ms, {bytes / 1024.0:N1} KB.");
        }
    }

    /// <summary>
    /// «Ventas por período» y los Excel (entrega 9, parte 2) con la misma papelería de dos años: la pantalla (una página de 100 y
    /// los totales) y el Excel de un mes completo (septiembre, unos 1,800 tickets) y del año; el Excel de la pestaña Reportes; y el
    /// de un mes de movimientos del inventario (aquí se hace un movimiento por cada renglón vendido en septiembre y octubre, como
    /// los deja la venta). La consulta sola y por la API, con el archivo; anota cuánto mide cada Excel.
    /// </summary>
    [FactDeMedicionDeReportes]
    public async Task Ventas_por_periodo_y_los_excel_con_dos_anios_de_la_papeleria_ficticia()
    {
        var (caja, dueno, ventas, _) = await PrepararAsync();
        await using (var db = Base())
        {
            // Un movimiento del kardex por renglón vendido desde septiembre (la papelería ficticia solo trae el inventario inicial).
            var conexion = (SqliteConnection)db.Database.GetDbConnection();
            await conexion.OpenAsync();
            await using var c = conexion.CreateCommand();
            c.CommandText = """
                INSERT INTO MovimientosInventario (Id, SucursalId, VarianteId, Tipo, Cantidad, CostoCentavos, FechaUtc, DocumentoTipo, DocumentoId)
                SELECT lower(hex(randomblob(16))), v.SucursalId, l.VarianteId, 'Venta', -l.CantidadBase, -l.CostoCentavos, v.PagadaUtc, 'Venta', v.Id
                FROM VentasLineas AS l INNER JOIN Ventas AS v ON v.Id = l.VentaId
                WHERE v.PagadaUtc >= '2026-09-01' AND l.VarianteId IS NOT NULL
                """;
            salida.WriteLine($"Movimientos de venta agregados: {await c.ExecuteNonQueryAsync():N0} (de {ventas:N0} ventas de este POS en el año).");
        }

        async Task<(double Mediana, double Maxima, T Resultado)> Medir<T>(Func<Task<T>> que)
        {
            var tiempos = new List<double>();
            T resultado = default!;
            for (var i = 0; i < 6; i++)
            {
                var cuanto = Stopwatch.StartNew();
                resultado = await que();
                if (i > 0)
                    tiempos.Add(cuanto.Elapsed.TotalMilliseconds);
            }
            return (Mediana(tiempos), tiempos.Max(), resultado);
        }

        var septiembre = (Desde: new DateOnly(2026, 9, 1), Hasta: new DateOnly(2026, 9, 30));
        var anio = (Desde: new DateOnly(2025, 10, 22), Hasta: new DateOnly(2026, 10, 21));
        foreach (var (nombre, dias) in new[] { ("septiembre", septiembre), ("el año", anio) })
        {
            var pagina = await Medir(async () =>
            {
                await using var db = Base();
                return await new ReporteVentasPorPeriodo(db).ArmarAsync(caja, dueno, dias.Desde, dias.Hasta, null, pagina: 5);
            });
            salida.WriteLine($"Ventas por período, {nombre}, una página: mediana {pagina.Mediana:N0} ms, la más lenta {pagina.Maxima:N0} ms; "
                + $"{pagina.Resultado.Total:N0} tickets, {pagina.Resultado.Dias.Count} días.");
            var leidos = await Medir(async () =>
            {
                await using var db = Base();
                return await new ReporteVentasPorPeriodo(db).TodoAsync(caja, dueno, dias.Desde, dias.Hasta, null);
            });
            var libro = await Medir(() => Task.FromResult(
                PapeleriaPos.Servicio.Excel.ExcelDeVentasPorPeriodo.Armar(leidos.Resultado, new PapeleriaPos.Servicio.Excel.QuienExporta("Matriz", "Dueño", Ahora))));
            salida.WriteLine($"Ventas por período, {nombre}, todos los tickets: leerlos, mediana {leidos.Mediana:N0} ms (la más lenta {leidos.Maxima:N0} ms); "
                + $"el Excel, mediana {libro.Mediana:N0} ms (la más lenta {libro.Maxima:N0} ms), {libro.Resultado.Length / 1024.0:N0} KB.");
        }
        var movimientos = await Medir(async () =>
        {
            await using var db = Base();
            return await new RegistroReportesInventario(db).TodosLosMovimientosAsync(
                (await db.Cajas.SingleAsync()).SucursalId, dueno, new FiltroMovimientosDto(septiembre.Desde, septiembre.Hasta));
        });
        salida.WriteLine($"Movimientos de septiembre, todos (con lo que quedó): mediana {movimientos.Mediana:N0} ms, la más lenta {movimientos.Maxima:N0} ms; "
            + $"{movimientos.Resultado.Total:N0} movimientos.");

        // Por la API, con el archivo: como lo pide la caja.
        await using var fabrica = new FabricaDePrueba(b => b.UseSetting("ConnectionStrings:Pos", $"Data Source={_ruta};Pooling=False")
            .UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false"));
        var http = fabrica.CreateClient();
        var sucursal = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single().SucursalId;
        foreach (var (que, url) in new[]
                 {
                     ("Ventas por período, septiembre, una página", $"api/cajas/{caja}/reportes/ventas-por-periodo?usuarioId={dueno}&desde=2026-09-01&hasta=2026-09-30"),
                     ("Ventas por período, septiembre, Excel", $"api/cajas/{caja}/reportes/ventas-por-periodo/excel?usuarioId={dueno}&desde=2026-09-01&hasta=2026-09-30"),
                     ("Ventas por período, el año, Excel", $"api/cajas/{caja}/reportes/ventas-por-periodo/excel?usuarioId={dueno}&desde=2025-10-22&hasta=2026-10-21"),
                     ("Reporte del año, Excel", $"api/cajas/{caja}/reportes/ventas/excel?usuarioId={dueno}&periodo=Anio&fecha=2026-10-21"),
                     ("Movimientos de septiembre, Excel", $"api/sucursales/{sucursal}/inventario/movimientos/excel?usuarioId={dueno}&desde=2026-09-01&hasta=2026-09-30"),
                     ("Lo que vale el inventario, Excel", $"api/sucursales/{sucursal}/inventario/valor/excel?usuarioId={dueno}"),
                     ("Bajos en inventario, Excel", $"api/sucursales/{sucursal}/inventario/bajos/excel?usuarioId={dueno}"),
                 })
        {
            var (mediana, maxima, bytes) = await Medir(async () =>
            {
                var respuesta = await http.GetAsync(url);
                var cuerpo = await respuesta.Content.ReadAsByteArrayAsync();
                Assert.True(respuesta.IsSuccessStatusCode, System.Text.Encoding.UTF8.GetString(cuerpo));
                return cuerpo.Length;
            });
            salida.WriteLine($"API {que}: mediana {mediana:N0} ms, la más lenta {maxima:N0} ms, {bytes / 1024.0:N0} KB.");
        }
    }
}
