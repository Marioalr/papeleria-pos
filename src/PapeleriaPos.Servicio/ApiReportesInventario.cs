using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Inventario (F4), los reportes como en eleventa (2026-10-17): lo que vale a costo, los movimientos y lo que está en su
/// mínimo o debajo. Solo leen; cada uno revisa los permisos de quien entró (<see cref="ConsultasInventario"/>).
/// </summary>
public static class ApiReportesInventario
{
    public static void MapearApiReportesInventario(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sucursales/{sucursalId:guid}/inventario").ConErrores().SoloSuCaja();

        // Solo con «Ver costos, utilidad y margen»: el total, cada departamento y una página de los productos del elegido.
        api.MapGet("/valor", (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? filtro, int? pagina, PosDbContext db) =>
            new RegistroReportesInventario(db).ValorAsync(sucursalId, usuarioId, departamentoId,
                Enum.TryParse<FiltroValor>(filtro, ignoreCase: true, out var f) && Enum.IsDefined(f) ? f : FiltroValor.ConExistencia, pagina ?? 0));

        // De unos días (sin ellos, hoy), del más reciente al más viejo, por páginas; el costo, solo con «Ver costos…».
        api.MapGet("/movimientos", (Guid sucursalId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, string? clase, string? q, Guid? departamentoId,
            Guid? quienId, int? pagina, PosDbContext db) =>
        {
            var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
            var filtro = new FiltroMovimientosDto(desde ?? hoy, hasta ?? desde ?? hoy,
                Enum.TryParse<ClaseMovimiento>(clase, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : (ClaseMovimiento?)null, q, departamentoId, quienId);
            return new RegistroReportesInventario(db).MovimientosAsync(sucursalId, usuarioId, filtro, pagina ?? 0);
        });

        api.MapGet("/movimientos/quienes", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroReportesInventario(db).QuienesAsync(sucursalId, usuarioId));

        // En su mínimo o debajo, por departamento, con cuánto pedir (hasta su máximo o lo del sugerido), por páginas.
        api.MapGet("/bajos", (Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? q, bool? conMinimo, int? pagina, PosDbContext db) =>
            new RegistroReportesInventario(db).BajosAsync(sucursalId, usuarioId, departamentoId, q, conMinimo ?? false, pagina ?? 0));
    }
}
