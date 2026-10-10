using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
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

        // ── Depuración ───────────────────────────────────────────────────────

        api.MapPost("/depuracion/baja", async (SolicitudBaja s, PosDbContext db) =>
        {
            await new RegistroDepuracion(db).DarDeBajaAsync(s.UsuarioId, s.SucursalId, s.VarianteId);
            return Results.NoContent();
        });

        api.MapPost("/depuracion/juntar", async (SolicitudJuntar s, PosDbContext db) =>
        {
            await new RegistroDepuracion(db).JuntarAsync(s.UsuarioId, s.SucursalId, s.OrigenId, s.DestinoId);
            return Results.NoContent();
        });

        api.MapPost("/depuracion/unidad", async (SolicitudCambiarUnidad s, PosDbContext db) =>
        {
            await new RegistroDepuracion(db).CambiarUnidadAsync(s.UsuarioId, s.SucursalId, s.VarianteId, s.Unidad, s.FactorMilesimas, s.PrecioCentavos);
            return Results.NoContent();
        });

        // El mayoreo de eleventa que la importación no pudo pasar a escala (fase 3): se captura o se descarta.
        api.MapGet("/depuracion/mayoreo", (PosDbContext db) => new RegistroDepuracion(db).MayoreoPorRevisarAsync());

        api.MapPost("/depuracion/mayoreo", async (SolicitudMayoreo s, PosDbContext db) =>
        {
            await new RegistroDepuracion(db).MayoreoAsync(s.UsuarioId, s.SucursalId, s.VarianteId, s.DesdeMilesimas, s.PrecioCentavos);
            return Results.NoContent();
        });

        api.MapGet("/depuracion", async (PosDbContext db) =>
            (await new RegistroDepuracion(db).DecisionesAsync())
            .Select(d => new DecisionDepuracionDto(HoraMexico.ALocal(d.FechaUtc), d.Tipo, d.Descripcion, d.OrigenEleventa is not null)).ToList());

        // Para elegir con cuál juntar: las variantes activas que se parecen al texto (por nombre o código).
        api.MapGet("/variantes", async (string q, Guid sucursalId, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            var encontrados = await catalogo.BuscarAsync(db, q, sucursalId, 20);
            var ids = encontrados.Select(e => e.VarianteId).ToList();
            var unidades = await db.Variantes.Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.UnidadBase);
            return encontrados.Select(e => new VarianteParaJuntarDto(e.VarianteId, e.Descripcion, e.Codigo, e.PrecioCentavos, unidades.GetValueOrDefault(e.VarianteId)))
                .ToList();
        });
    }
}
