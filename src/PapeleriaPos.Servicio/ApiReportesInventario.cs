using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Inventario (F4), los reportes como en eleventa (2026-10-17): lo que vale a costo, los movimientos y lo que está en su
/// mínimo o debajo. Solo leen; cada uno revisa los permisos de quien entró (<see cref="ConsultasInventario"/>). Cada uno también
/// en Excel (entrega 9, parte 2, 2026-10-22): lo que se ve, con sus filtros y sin las páginas, con los mismos permisos (y el costo
/// de los movimientos, solo para quien ve costos).
/// </summary>
public static class ApiReportesInventario
{
    public static void MapearApiReportesInventario(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sucursales/{sucursalId:guid}/inventario").ConErrores().SoloSuCaja();

        // Solo con «Ver costos, utilidad y margen»: el total, cada departamento y una página de los productos del elegido.
        api.MapGet("/valor", (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? filtro, int? pagina, PosDbContext db) =>
            new RegistroReportesInventario(db).ValorAsync(sucursalId, usuarioId, departamentoId, FiltroDeValor(filtro), pagina ?? 0));

        // Lo mismo en Excel: cada departamento y todos los productos del elegido con ese filtro.
        api.MapGet("/valor/excel", async (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? filtro, PosDbContext db, CancellationToken ct) =>
        {
            var valor = await new RegistroReportesInventario(db).TodoElValorAsync(sucursalId, usuarioId, departamentoId, FiltroDeValor(filtro), ct);
            var quien = await QuienExporta.DeAsync(db, sucursalId, usuarioId, ct);
            var de = departamentoId is { } d ? valor.Departamentos.FirstOrDefault(g => g.DepartamentoId == d)?.Nombre ?? "ese departamento" : null;
            var productos = $"{(de is null ? "Todos los productos" : $"Productos de «{de}»")}, {valor.Filtro switch
            {
                FiltroValor.EnCero => "los que están en cero",
                FiltroValor.Negativa => "los que quedaron en negativo",
                FiltroValor.SinCosto => "los que tienen existencia sin costo",
                _ => "los que tienen existencia, de lo que más vale a lo que menos",
            }}.";
            return ApiReportes.Excel(ExcelDelInventario.Valor(valor, productos, quien), ArchivosDeReportes.Valor(DateOnly.FromDateTime(quien.CuandoLocal)));
        });

        // De unos días (sin ellos, hoy), del más reciente al más viejo, por páginas; el costo, solo con «Ver costos…».
        api.MapGet("/movimientos", (Guid sucursalId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, string? clase, string? q, Guid? departamentoId,
            Guid? quienId, int? pagina, PosDbContext db) =>
            new RegistroReportesInventario(db).MovimientosAsync(sucursalId, usuarioId, FiltroDeMovimientos(db, desde, hasta, clase, q, departamentoId, quienId),
                pagina ?? 0));

        // Todos los de esos filtros en Excel.
        api.MapGet("/movimientos/excel", async (Guid sucursalId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, string? clase, string? q,
            Guid? departamentoId, Guid? quienId, PosDbContext db, CancellationToken ct) =>
        {
            var filtro = FiltroDeMovimientos(db, desde, hasta, clase, q, departamentoId, quienId);
            var movimientos = await new RegistroReportesInventario(db).TodosLosMovimientosAsync(sucursalId, usuarioId, filtro, ct);
            var quien = await QuienExporta.DeAsync(db, sucursalId, usuarioId, ct);
            var filtros = new List<string>();
            if (filtro.Clase is { } tipo)
                filtros.Add($"tipo: {Textos.Clase(tipo)}");
            if (!string.IsNullOrWhiteSpace(filtro.Texto))
                filtros.Add($"producto: «{filtro.Texto.Trim()}»");
            if (filtro.DepartamentoId is { } departamento)
                filtros.Add($"departamento: {await db.Departamentos.Where(x => x.Id == departamento).Select(x => x.Nombre).SingleOrDefaultAsync() ?? "—"}");
            if (filtro.QuienId is { } persona)
                filtros.Add($"quién: {await db.Usuarios.Where(x => x.Id == persona).Select(x => x.Nombre).SingleOrDefaultAsync() ?? "—"}");
            return ApiReportes.Excel(ExcelDelInventario.Movimientos(movimientos, filtro, filtros, quien), ArchivosDeReportes.Movimientos(filtro.Desde, filtro.Hasta));
        });

        api.MapGet("/movimientos/quienes", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroReportesInventario(db).QuienesAsync(sucursalId, usuarioId));

        // En su mínimo o debajo, por departamento, con cuánto pedir (hasta su máximo o lo del sugerido), por páginas.
        api.MapGet("/bajos", (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? q, bool? conMinimo, int? pagina, PosDbContext db) =>
            new RegistroReportesInventario(db).BajosAsync(sucursalId, usuarioId, departamentoId, q, conMinimo ?? false, pagina ?? 0));

        // Todos los de esos filtros en Excel.
        api.MapGet("/bajos/excel", async (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? q, bool? conMinimo, PosDbContext db,
            CancellationToken ct) =>
        {
            var bajos = await new RegistroReportesInventario(db).TodosLosBajosAsync(sucursalId, usuarioId, departamentoId, q, conMinimo ?? false, ct);
            var quien = await QuienExporta.DeAsync(db, sucursalId, usuarioId, ct);
            var filtros = new List<string>();
            if (departamentoId is { } departamento)
                filtros.Add($"departamento: {await db.Departamentos.Where(x => x.Id == departamento).Select(x => x.Nombre).SingleOrDefaultAsync() ?? "—"}");
            if (!string.IsNullOrWhiteSpace(q))
                filtros.Add($"producto: «{q.Trim()}»");
            if (conMinimo == true)
                filtros.Add("los que tienen su mínimo propio");
            return ApiReportes.Excel(ExcelDelInventario.Bajos(bajos, filtros, quien), ArchivosDeReportes.Bajos(DateOnly.FromDateTime(quien.CuandoLocal)));
        });
    }

    private static FiltroValor FiltroDeValor(string? filtro) =>
        Enum.TryParse<FiltroValor>(filtro, ignoreCase: true, out var f) && Enum.IsDefined(f) ? f : FiltroValor.ConExistencia;

    private static FiltroMovimientosDto FiltroDeMovimientos(PosDbContext db, DateOnly? desde, DateOnly? hasta, string? clase, string? q, Guid? departamentoId,
        Guid? quienId)
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
        return new FiltroMovimientosDto(desde ?? hoy, hasta ?? desde ?? hoy,
            Enum.TryParse<ClaseMovimiento>(clase, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : (ClaseMovimiento?)null, q, departamentoId, quienId);
    }
}
