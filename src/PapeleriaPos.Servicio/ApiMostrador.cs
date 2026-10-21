using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Lo que se señala (entrega 13): los exhibidores y las temporadas, sus lugares y lo acomodado en ellos. La caja los pinta
/// de su copia (Ctrl+M, sin la red); esto es para la Configuración, para Ctrl+M › Acomodar y para la caja sin copia (el
/// navegador). Cada cambio regresa todos, como quedaron. Los cambia quien entró con «Productos, precios y etiquetas».
/// </summary>
public static class ApiMostrador
{
    public static void MapearApiMostrador(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/mostrador").ConErrores().SoloSuCaja();

        api.MapGet("", (PosDbContext db, VersionesDeLaCopia versiones) => ConSuBase(new RegistroMostrador(db).LeerAsync(), versiones));

        // Un exhibidor o una temporada nuevo; su nombre o sus fechas; quitarlo (se desactiva).
        api.MapPost("", (SolicitudNuevoExhibidor s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).NuevoAsync(s), versiones));
        api.MapPut("/{id:guid}", (Guid id, SolicitudCambiarExhibidor s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).CambiarAsync(id, s), versiones));
        api.MapPost("/{id:guid}/quitar", (Guid id, SolicitudMostrador s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).QuitarAsync(id, s), versiones));

        // Sus lugares: uno nuevo (abajo), su nombre, subirlo o bajarlo, quitarlo (lo acomodado en él pasa al de al lado).
        api.MapPost("/{id:guid}/lugares", (Guid id, SolicitudLugar s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).AgregarLugarAsync(id, s), versiones));
        api.MapPut("/lugares/{lugarId:guid}", (Guid lugarId, SolicitudLugar s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).CambiarLugarAsync(lugarId, s), versiones));
        api.MapPost("/lugares/{lugarId:guid}/mover", (Guid lugarId, SolicitudMoverLugar s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).MoverLugarAsync(lugarId, s), versiones));
        api.MapPost("/lugares/{lugarId:guid}/quitar", (Guid lugarId, SolicitudMostrador s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).QuitarLugarAsync(lugarId, s), versiones));

        // Ctrl+M › Acomodar: cómo quedan los productos de uno o más lugares.
        api.MapPut("/acomodo", (SolicitudAcomodo s, PosDbContext db, VersionesDeLaCopia versiones) =>
            ConSuBase(new RegistroMostrador(db).AcomodarAsync(s), versiones));
    }

    /// <summary>
    /// Con la base de su versión (la de la copia): la caja la compara con la de su copia solo si son de la misma base (después de
    /// restaurar un respaldo las versiones vuelven a empezar).
    /// </summary>
    private static async Task<MostradorDto> ConSuBase(Task<MostradorDto> mostrador, VersionesDeLaCopia versiones) =>
        (await mostrador) with { Base = versiones.Base };
}
