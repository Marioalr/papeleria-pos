using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Solo en la prueba con una persona real: los avisos que le habrían llegado al dueño, las respuestas de la guía y
/// empezar de cero. En la tienda estas rutas no existen.
/// </summary>
public static class ApiPrueba
{
    public static void MapearApiPrueba(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/prueba").ConErrores().AddEndpointFilter(async (contexto, siguiente) =>
            contexto.HttpContext.RequestServices.GetRequiredService<ModoPrueba>().Activo
                ? await siguiente(contexto)
                : Results.NotFound(new ErrorDto("Esto solo existe en la prueba.")));

        api.MapGet("/avisos", async (PosDbContext db) =>
            (await db.MensajesTelegram.AsNoTracking().Where(m => m.ChatId == ChatTelegram.Simulado).OrderByDescending(m => m.CreadoUtc).Take(100).ToListAsync())
            .Select(m => new AvisoPruebaDto(HoraMexico.ALocal(m.CreadoUtc), m.Tipo, m.Texto)).ToList());

        api.MapGet("/respuestas", (ModoPrueba prueba) => prueba.Respuestas());

        api.MapPut("/respuestas", (RespuestasPruebaDto respuestas, ModoPrueba prueba) =>
        {
            prueba.Guardar(respuestas);
            return respuestas;
        });

        api.MapPost("/respuestas/exportar", (SolicitudExportarRespuestas s, ModoPrueba prueba) => new ArchivoGuardadoDto(prueba.Exportar(s.Texto)));

        // Vuelve a la base con que se instaló: lo hecho en la prueba se pierde, las respuestas de la guía se quedan.
        api.MapPost("/empezar-de-cero", (ModoPrueba prueba, CatalogoEnMemoria catalogo) =>
        {
            prueba.EmpezarDeCero();
            catalogo.Invalidar();
            return Results.NoContent();
        });
    }
}
