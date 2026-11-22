using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las ventas de otros días y el historial de cortes (paridad con eleventa, 2026-10-17): solo se leen, un día a la vez, con
/// el usuario que entró a la caja (como la bitácora y el corte del día). El servicio revisa su permiso: con «Ver reportes de
/// ventas», las de todos, de cualquier día y caja; sin él, solo las suyas (2026-11-22), también al buscar un folio y al abrir
/// un ticket; el historial de cortes, quien hace el corte de un turno o ve el del día. Los errores van en palabras de la tienda (ErroresApi): 403 sin el permiso, 400 lo demás.
/// </summary>
public static class ApiOtrosDias
{
    public static void MapearApiOtrosDias(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Las ventas de un día (sin fecha: hoy) de una caja (sin caja: todas) y de un cajero (sin cajero: todos), vistas desde
        // esta caja; con folio, las de ese folio en cualquier día (solo el número, sin los reportes, en esta caja).
        api.MapGet("/cajas/{cajaId:guid}/ventas", async (Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? caja, Guid? cajero, string? folio,
            PosDbContext db) =>
        {
            var ventas = new VentasDeOtrosDias(db);
            return string.IsNullOrWhiteSpace(folio)
                ? await ventas.DelDiaAsync(cajaId, usuarioId, fecha, caja, cajero)
                : await ventas.PorFolioAsync(cajaId, usuarioId, folio);
        });

        // Un ticket de la lista, con su detalle (2026-11-22): el de otro cajero, solo con «Ver reportes de ventas».
        api.MapGet("/cajas/{cajaId:guid}/ventas/{ventaId:guid}", async (Guid cajaId, Guid ventaId, Guid usuarioId, PosDbContext db,
            IOptions<OpcionesTicket> opciones) =>
        {
            var una = await new VentasDeOtrosDias(db).UnaAsync(cajaId, usuarioId, ventaId);
            return new VentaDelDiaDetalleDto(una.Fila, await ApiCaja.TicketAsync(db, una.Venta, opciones.Value), una.Devoluciones);
        });

        // Los turnos de un día (sin fecha: hoy) con su corte, de una caja (sin caja: todas) y de un cajero (sin cajero: todos).
        // En el piloto con eleventa, el corte de un turno abierto se hace allá.
        api.MapGet("/cajas/{cajaId:guid}/cortes", async (Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? caja, Guid? cajero, PosDbContext db,
            OpcionesEleventa opcionesEleventa) =>
            await new HistorialDeCortes(db).DelDiaAsync(cajaId, usuarioId, fecha, caja, cajero, opcionesEleventa.CorteEnEleventa));
    }
}
