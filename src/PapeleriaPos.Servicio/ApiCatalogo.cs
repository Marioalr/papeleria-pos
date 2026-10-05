using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>F3 Productos (fase 2, entrega 1): buscar, la ficha, guardar, el código interno y el alta rápida desde la caja.</summary>
public static class ApiCatalogo
{
    public static void MapearApiCatalogo(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalogo").ConErrores().SoloSuCaja();

        api.MapGet("/productos", (Guid sucursalId, string? q, Guid? departamentoId, bool? porCompletar, bool? inactivos, PosDbContext db) =>
            new RegistroCatalogo(db).BuscarAsync(sucursalId, q, departamentoId, porCompletar ?? false, inactivos ?? false));

        api.MapGet("/opciones", (PosDbContext db) => new RegistroCatalogo(db).OpcionesAsync());

        // El costo y los proveedores solo van para el dueño y el encargado.
        api.MapGet("/productos/{productoId:guid}", async (Guid productoId, Guid sucursalId, Guid usuarioId, PosDbContext db) =>
        {
            var registro = new RegistroCatalogo(db);
            return await registro.FichaAsync(productoId, sucursalId, await registro.VeCostosAsync(usuarioId)) is { } ficha
                ? Results.Ok(ficha)
                : Results.NotFound(new ErrorDto("El producto ya no existe."));
        });

        api.MapGet("/variantes/{varianteId:guid}/kardex", (Guid varianteId, Guid sucursalId, PosDbContext db) =>
            new RegistroCatalogo(db).KardexAsync(varianteId, sucursalId));

        api.MapPost("/productos", async (SolicitudGuardarProducto s, PosDbContext db) =>
        {
            var registro = new RegistroCatalogo(db);
            var id = await registro.GuardarAsync(s);
            return await registro.FichaAsync(id, s.SucursalId, await registro.VeCostosAsync(s.UsuarioId));
        });

        api.MapPost("/codigo-interno", async (PosDbContext db) => new { Codigo = await new RegistroCatalogo(db).CodigoInternoAsync() });

        // La caja lo agrega al ticket enseguida: regresa el producto como lo vende.
        api.MapPost("/alta-rapida", async (SolicitudAltaRapida s, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            var varianteId = await new RegistroCatalogo(db).AltaRapidaAsync(s);
            return await catalogo.PorVarianteAsync(db, varianteId, s.SucursalId);
        });
    }
}
