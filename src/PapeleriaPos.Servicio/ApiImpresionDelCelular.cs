using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Imprimir el ticket de lo que cobra el celular (docs/celular-como-caja.md, paso 11): la ruta con que el celular lo pide después del
/// cobro (no salió la primera vez, o lo quieren otra vez) y las dos con que la ventana de una caja recoge lo que le toca imprimir y dice
/// si salió. Al cobrar se pide en el mismo cobro (<see cref="ApiCobroDelCelular"/>, <c>imprimir</c>).
/// </summary>
public static class ApiImpresionDelCelular
{
    public static void MapearApiImpresionDelCelular(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // El celular: imprimir el ticket de una venta ya cobrada. La primera vez no pide motivo ni permiso; si ya salió, es una reimpresión.
        api.MapPost("/cajas/{cajaId:guid}/ventas/{ventaId:guid}/imprimir", async (Guid cajaId, Guid ventaId, SolicitudImprimirDelCelular? s,
            HttpContext http, PosDbContext db, ImpresionDelCelular impresion, IOptions<OpcionesTicket> ticket, CancellationToken ct) =>
        {
            var celular = await ApiCobroDelCelular.ElCelularAsync(http, cajaId, db, ct, "Solo un celular dado de alta pide tickets a la impresora de una caja.");
            var venta = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros)
                    .SingleOrDefaultAsync(v => v.Id == ventaId && v.SucursalId == celular.SucursalId && v.Folio != null, ct)
                ?? throw new RechazoConCodigo("Esa venta no está en la caja principal: revisa que se haya cobrado.", "no-esta", StatusCodes.Status404NotFound);
            var quien = s?.CajeroId ?? venta.CajeroId;
            if (!await db.Usuarios.AnyAsync(u => u.Id == quien && u.Activo && (u.SucursalId == null || u.SucursalId == celular.SucursalId), ct))
                throw new RechazoConCodigo("Ese usuario ya no cobra en esta sucursal: entra otra vez en el celular.", "cajero");
            return await impresion.ImprimirAsync(db, venta, celular, quien, ticket.Value, alCobrar: false, s?.Id, s?.Motivo, ct);
        });

        // La ventana de una caja pregunta (cada 2 s) qué imprimir de lo que cobró el celular, y dice qué impresora de tickets tiene.
        api.MapGet("/cajas/{cajaId:guid}/por-imprimir", (Guid cajaId, PosDbContext db, ImpresionDelCelular impresion, CancellationToken ct,
            string? impresora = null) => impresion.PreguntaAsync(db, cajaId, impresora, ct));

        // Y dice si salió de su impresora (o por qué no).
        api.MapPost("/cajas/{cajaId:guid}/por-imprimir", async (Guid cajaId, SolicitudTicketImpreso s, PosDbContext db, ImpresionDelCelular impresion,
            CancellationToken ct) =>
        {
            await impresion.ResultadoAsync(db, cajaId, s, ct);
            return Results.NoContent();
        });
    }
}
