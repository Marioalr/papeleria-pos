using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Servicio;

/// <summary>Impresiones (entrega 5): lo que mandan los agentes, lo que falta cobrar y las no vendidas.</summary>
public static class ApiImpresiones
{
    public static void MapearApiImpresiones(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // El agente de la caja manda cada trabajo impreso; el servicio le pone costo y precio.
        api.MapPost("/impresiones", async (TrabajoMedidoDto t, PosDbContext db) =>
        {
            var trabajo = await new RegistroImpresiones(db).RegistrarAsync(t);
            return new { trabajo.Id, trabajo.PrecioCentavos };
        });

        // Lo que falta cobrar en la sucursal, de todas las PC. El costo solo va si quien pregunta es el dueño.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/por-cobrar", async (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
        {
            var esDueno = await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Rol == Rol.Dueno && u.Activo);
            var trabajos = await new RegistroImpresiones(db).PorCobrarAsync(sucursalId);
            var ids = trabajos.Where(t => t.AtendioId != null).Select(t => t.AtendioId!.Value).Distinct().ToList();
            var nombres = await db.Usuarios.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre);
            return trabajos.Select(t => new ImpresionPorCobrarDto(
                t.Id, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Paginas, t.Caras, t.Color, t.Tamano, t.PrecioCentavos,
                t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null,
                t.Miniatura is { } m ? Convert.ToBase64String(m) : null, t.Nota,
                esDueno ? new CostoImpresionDto(t.CostoTintaCentavos, t.CostoPapelCentavos, t.CostoOtrosCentavos, t.CostoCentavos, t.MargenPorcentaje) : null))
                .ToList();
        });

        api.MapPost("/impresiones/{id:guid}/no-vendida", async (Guid id, SolicitudNoVendida s, PosDbContext db) =>
        {
            await new RegistroImpresiones(db).NoVendidaAsync(id, s);
            return Results.NoContent();
        });
    }
}
