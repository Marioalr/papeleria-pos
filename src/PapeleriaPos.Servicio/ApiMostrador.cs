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

        api.MapGet("", (PosDbContext db) => new RegistroMostrador(db).LeerAsync());

        // Un exhibidor o una temporada nuevo; su nombre o sus fechas; quitarlo (se desactiva).
        api.MapPost("", (SolicitudNuevoExhibidor s, PosDbContext db) => new RegistroMostrador(db).NuevoAsync(s));
        api.MapPut("/{id:guid}", (Guid id, SolicitudCambiarExhibidor s, PosDbContext db) => new RegistroMostrador(db).CambiarAsync(id, s));
        api.MapPost("/{id:guid}/quitar", (Guid id, SolicitudMostrador s, PosDbContext db) => new RegistroMostrador(db).QuitarAsync(id, s));

        // Sus lugares: uno nuevo (abajo), su nombre, subirlo o bajarlo, quitarlo (lo acomodado en él pasa al de al lado).
        api.MapPost("/{id:guid}/lugares", (Guid id, SolicitudLugar s, PosDbContext db) => new RegistroMostrador(db).AgregarLugarAsync(id, s));
        api.MapPut("/lugares/{lugarId:guid}", (Guid lugarId, SolicitudLugar s, PosDbContext db) => new RegistroMostrador(db).CambiarLugarAsync(lugarId, s));
        api.MapPost("/lugares/{lugarId:guid}/mover", (Guid lugarId, SolicitudMoverLugar s, PosDbContext db) =>
            new RegistroMostrador(db).MoverLugarAsync(lugarId, s));
        api.MapPost("/lugares/{lugarId:guid}/quitar", (Guid lugarId, SolicitudMostrador s, PosDbContext db) =>
            new RegistroMostrador(db).QuitarLugarAsync(lugarId, s));

        // Ctrl+M › Acomodar: cómo quedan los productos de uno o más lugares.
        api.MapPut("/acomodo", (SolicitudAcomodo s, PosDbContext db) => new RegistroMostrador(db).AcomodarAsync(s));
    }
}
