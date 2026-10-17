using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// F3 Productos (fase 2, entrega 1): buscar, la ficha, guardar, el código interno y el alta rápida desde la caja; y sus
/// departamentos.
/// </summary>
public static class ApiCatalogo
{
    public static void MapearApiCatalogo(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalogo").ConErrores().SoloSuCaja();

        api.MapGet("/productos", (Guid sucursalId, string? q, Guid? departamentoId, bool? porCompletar, bool? inactivos, PosDbContext db) =>
            new RegistroCatalogo(db).BuscarAsync(sucursalId, q, departamentoId, porCompletar ?? false, inactivos ?? false));

        api.MapGet("/opciones", (PosDbContext db) => new RegistroCatalogo(db).OpcionesAsync());

        // El piloto con eleventa (su lectura prendida): lo que dicen Productos e Inventario; vacíos, fuera del piloto.
        api.MapGet("/piloto", (OpcionesEleventa eleventa) => eleventa.Activo
            ? new TrastiendaEnElPilotoDto(TrastiendaEnElPiloto.ProductosEnEleventa, TrastiendaEnElPiloto.InventarioEnEleventa)
            : new TrastiendaEnElPilotoDto(null, null));

        // Productos › Departamentos: todos, con cuántos productos activos tiene cada uno. Los da de alta, les cambia el nombre,
        // los desactiva y los reactiva quien entró a la caja con «Productos, precios y etiquetas»; nada se borra. En el piloto
        // con eleventa (su lectura prendida) solo se consultan: se cambian allá y llegan con la lectura.
        api.MapGet("/departamentos", (PosDbContext db, OpcionesEleventa eleventa) => new RegistroDepartamentos(db, eleventa.Activo).ListarAsync());

        api.MapPost("/departamentos", (SolicitudDepartamento s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroDepartamentos(db, eleventa.Activo).DarDeAltaAsync(s));

        api.MapPut("/departamentos/{id:guid}/nombre", (Guid id, SolicitudDepartamento s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroDepartamentos(db, eleventa.Activo).CambiarNombreAsync(id, s));

        // Si tiene productos activos, pasan antes a otro departamento (PasarA): sin él no se desactiva.
        api.MapPost("/departamentos/{id:guid}/desactivar", (Guid id, SolicitudDesactivarDepartamento s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroDepartamentos(db, eleventa.Activo).DesactivarAsync(id, s));

        api.MapPost("/departamentos/{id:guid}/reactivar", (Guid id, SolicitudReactivarDepartamento s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroDepartamentos(db, eleventa.Activo).ReactivarAsync(id, s));

        // Los sinónimos de la tienda para la búsqueda: los dan de alta el dueño y el encargado.
        api.MapGet("/sinonimos", (PosDbContext db) => new RegistroSinonimos(db).ListarAsync());
        api.MapPost("/sinonimos", (SolicitudSinonimo s, PosDbContext db) => new RegistroSinonimos(db).AgregarAsync(s));
        api.MapPost("/sinonimos/{id:guid}/quitar", (Guid id, SolicitudQuitarSinonimo s, PosDbContext db) => new RegistroSinonimos(db).QuitarAsync(id, s));

        // El costo y los proveedores solo van para el dueño y el encargado. En el piloto con eleventa, la ficha dice qué pone
        // la lectura en los productos de eleventa: eso no se cambia aquí (TrastiendaEnElPiloto).
        api.MapGet("/productos/{productoId:guid}", async (Guid productoId, Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            var registro = new RegistroCatalogo(db, eleventa.Activo);
            return await registro.FichaAsync(productoId, sucursalId, await registro.VeCostosAsync(usuarioId)) is { } ficha
                ? Results.Ok(ficha)
                : Results.NotFound(new ErrorDto("El producto ya no existe."));
        });

        api.MapGet("/variantes/{varianteId:guid}/kardex", (Guid varianteId, Guid sucursalId, PosDbContext db) =>
            new RegistroCatalogo(db).KardexAsync(varianteId, sucursalId));

        // La página de Productos (entrega 8): la lista que abre llena, con lo más vendido primero, y los últimos movimientos
        // del producto elegido.
        api.MapGet("/lista", async (Guid sucursalId, Guid usuarioId, string? q, string? filtro, Guid? departamentoId, bool? inactivos, int? maximo,
            PosDbContext db, CatalogoEnMemoria catalogo) =>
            await new RegistroCatalogo(db).ListaAsync(sucursalId, usuarioId, q,
                Enum.TryParse<FiltroProductos>(filtro, ignoreCase: true, out var f) ? f : FiltroProductos.Todos, departamentoId, inactivos ?? false,
                await catalogo.VecesVendidoAsync(db), maximo ?? 50));

        api.MapGet("/variantes/{varianteId:guid}/movimientos", (Guid varianteId, Guid sucursalId, int? maximo, PosDbContext db) =>
            new RegistroCatalogo(db).UltimosMovimientosAsync(varianteId, sucursalId, maximo ?? 8));

        api.MapPost("/productos", async (SolicitudGuardarProducto s, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            var registro = new RegistroCatalogo(db, eleventa.Activo);
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

        // En el piloto con eleventa, lo de eleventa no se da de baja, no se junta ni se le cambia la unidad aquí.
        api.MapPost("/depuracion/baja", async (SolicitudBaja s, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            await new RegistroDepuracion(db, eleventa.Activo).DarDeBajaAsync(s.UsuarioId, s.SucursalId, s.VarianteId);
            return Results.NoContent();
        });

        api.MapPost("/depuracion/juntar", async (SolicitudJuntar s, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            await new RegistroDepuracion(db, eleventa.Activo).JuntarAsync(s.UsuarioId, s.SucursalId, s.OrigenId, s.DestinoId);
            return Results.NoContent();
        });

        api.MapPost("/depuracion/unidad", async (SolicitudCambiarUnidad s, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            await new RegistroDepuracion(db, eleventa.Activo).CambiarUnidadAsync(s.UsuarioId, s.SucursalId, s.VarianteId, s.Unidad, s.FactorMilesimas, s.PrecioCentavos);
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
