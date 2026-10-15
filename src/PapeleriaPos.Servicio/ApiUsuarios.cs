using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// «Usuarios y permisos» (entrega 1): los usuarios, sus permisos y los de defecto, para quien entró a esa caja con
/// «Usuarios y permisos» (<see cref="RegistroUsuarios"/>). Cada cambio regresa la lista completa, para pintarla otra vez.
/// </summary>
public static class ApiUsuarios
{
    public static void MapearApiUsuarios(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/cajas/{cajaId:guid}/usuarios-y-permisos").ConErrores().SoloSuCaja();

        // usuarioId: quien entró a la caja.
        api.MapGet("/", (Guid cajaId, Guid usuarioId, PosDbContext db) => new RegistroUsuarios(db).ListarAsync(cajaId, usuarioId));

        api.MapGet("/defecto", (Guid cajaId, Guid usuarioId, PosDbContext db) => new RegistroUsuarios(db).PermisosAsync(cajaId, usuarioId, null));

        api.MapGet("/{id:guid}", (Guid cajaId, Guid id, Guid usuarioId, PosDbContext db) => new RegistroUsuarios(db).PermisosAsync(cajaId, usuarioId, id));

        api.MapPut("/defecto", (Guid cajaId, SolicitudPermisoUsuario s, PosDbContext db) => new RegistroUsuarios(db).CambiarAsync(cajaId, null, s));

        api.MapPut("/{id:guid}", (Guid cajaId, Guid id, SolicitudPermisoUsuario s, PosDbContext db) => new RegistroUsuarios(db).CambiarAsync(cajaId, id, s));

        api.MapPost("/{id:guid}/copiar", (Guid cajaId, Guid id, SolicitudCopiarPermisos s, PosDbContext db) =>
            new RegistroUsuarios(db).CopiarAsync(cajaId, id, s));

        api.MapPost("/{id:guid}/todos", (Guid cajaId, Guid id, SolicitudUsuarios s, PosDbContext db) => new RegistroUsuarios(db).TodosAsync(cajaId, id, s));

        api.MapPost("/{id:guid}/defecto", (Guid cajaId, Guid id, SolicitudUsuarios s, PosDbContext db) => new RegistroUsuarios(db).DeDefectoAsync(cajaId, id, s));

        api.MapPost("/usuarios", (Guid cajaId, SolicitudNuevoUsuario s, PosDbContext db) => new RegistroUsuarios(db).NuevoAsync(cajaId, s));

        api.MapPut("/{id:guid}/pin", (Guid cajaId, Guid id, SolicitudPinUsuario s, PosDbContext db) => new RegistroUsuarios(db).CambiarPinAsync(cajaId, id, s));

        api.MapPut("/{id:guid}/activo", (Guid cajaId, Guid id, SolicitudActivarUsuario s, PosDbContext db) => new RegistroUsuarios(db).ActivarAsync(cajaId, id, s));
    }
}
