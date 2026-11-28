using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Avisos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los avisos de la caja (el «!» de arriba, 2026-11-28): los que tiene puestos cada módulo (<see cref="RegistroAvisos"/>), para la caja que
/// pregunta. Los pone y los quita cada módulo: aquí solo se leen.
/// </summary>
public static class ApiAvisos
{
    public static void MapearApiAvisos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/avisos", async (Guid sucursalId, Guid? cajaId, PosDbContext db) =>
            (await new RegistroAvisos(db).VigentesAsync(sucursalId, cajaId)).Select(AvisoDto.De).ToList());
    }
}
