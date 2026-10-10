using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>F4 Inventario, compras (fase 2, entrega 2): proveedores, recibir a ciegas, la nota, cerrar y el margen mínimo.</summary>
public static class ApiCompras
{
    public static void MapearApiCompras(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/proveedores", (bool? inactivos, PosDbContext db) => new RegistroCompras(db).ProveedoresAsync(inactivos ?? false));

        api.MapPost("/proveedores", (SolicitudGuardarProveedor s, PosDbContext db) => new RegistroCompras(db).GuardarProveedorAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/compras", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroCompras(db).ListaAsync(sucursalId, usuarioId));

        api.MapPost("/compras", (SolicitudAbrirCompra s, PosDbContext db) => new RegistroCompras(db).AbrirAsync(s));

        // Quien cuenta no recibe la nota ni los costos: solo quien los captura, con el conteo ya cerrado.
        api.MapGet("/compras/{compraId:guid}", async (Guid compraId, Guid usuarioId, PosDbContext db) =>
            await new RegistroCompras(db).DetalleAsync(compraId, usuarioId) is { } compra
                ? Results.Ok(compra)
                : Results.NotFound(new ErrorDto("La compra ya no existe.")));

        api.MapPost("/compras/{compraId:guid}/conteo", (Guid compraId, SolicitudConteo s, PosDbContext db) =>
            new RegistroCompras(db).ContarAsync(compraId, s));

        api.MapPost("/compras/{compraId:guid}/terminar-conteo", (Guid compraId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).TerminarConteoAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/renglones", (Guid compraId, SolicitudRenglonCompra s, PosDbContext db) =>
            new RegistroCompras(db).RenglonAsync(compraId, s));

        // El XML de la factura o el PDF de la nota llena los renglones (fase 2, entrega 2, parte 2).
        api.MapPost("/compras/{compraId:guid}/documentos", (Guid compraId, SolicitudDocumentoCompra s, PosDbContext db) =>
            new RegistroCompras(db).CargarDocumentoAsync(compraId, s));

        // ── Todo el pedido desde el documento: lo que no está en el catálogo ──

        api.MapPost("/compras/{compraId:guid}/conteo/desconocido", (Guid compraId, SolicitudConteoDesconocido s, PosDbContext db) =>
            new RegistroCompras(db).ContarDesconocidoAsync(compraId, s));

        api.MapPost("/compras/{compraId:guid}/renglones/{renglonId:guid}/corregir", (Guid compraId, Guid renglonId, SolicitudCorregirRenglon s, PosDbContext db) =>
            new RegistroCompras(db).CorregirRenglonAsync(compraId, renglonId, s));

        api.MapPost("/compras/{compraId:guid}/renglones/{renglonId:guid}/nuevo", (Guid compraId, Guid renglonId, SolicitudProductoNuevo s, PosDbContext db) =>
            new RegistroCompras(db).ProponerNuevoAsync(compraId, renglonId, s));

        api.MapPost("/compras/{compraId:guid}/renglones/{renglonId:guid}/no-es-nuevo", (Guid compraId, Guid renglonId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).NoEsNuevoAsync(compraId, renglonId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/nuevos/proponer", (Guid compraId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).ProponerNuevosAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/nuevos/alta", (Guid compraId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).DarDeAltaNuevosAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/renglones/{renglonId:guid}/quitar", (Guid compraId, Guid renglonId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).QuitarRenglonAsync(compraId, renglonId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/cerrar", (Guid compraId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).CerrarAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/cancelar", (Guid compraId, SolicitudCancelarCompra s, PosDbContext db) =>
            new RegistroCompras(db).CancelarAsync(compraId, s));

        // El precio sugerido después de una compra con margen bajo (o otro): como un cambio de precio del catálogo.
        api.MapPost("/catalogo/precio-menudeo", async (SolicitudPrecioMenudeo s, PosDbContext db) =>
        {
            await new RegistroCatalogo(db).CambiarMenudeoAsync(s);
            return Results.NoContent();
        });

        // ── Órdenes de compra y abrir paquete (parte 3) ──

        api.MapPost("/compras/{compraId:guid}/orden", (Guid compraId, SolicitudOrdenDeCompra s, PosDbContext db) =>
            new RegistroCompras(db).OrdenAsync(compraId, s));

        api.MapGet("/sucursales/{sucursalId:guid}/ordenes", (Guid sucursalId, PosDbContext db) => new RegistroOrdenes(db).ListaAsync(sucursalId));

        api.MapPost("/ordenes", (SolicitudAbrirOrden s, PosDbContext db) => new RegistroOrdenes(db).AbrirAsync(s));

        api.MapGet("/ordenes/{ordenId:guid}", async (Guid ordenId, PosDbContext db) =>
            await new RegistroOrdenes(db).DetalleAsync(ordenId) is { } orden ? Results.Ok(orden) : Results.NotFound(new ErrorDto("La orden ya no existe.")));

        api.MapPost("/ordenes/{ordenId:guid}/renglones", (Guid ordenId, SolicitudPedido s, PosDbContext db) => new RegistroOrdenes(db).PedirAsync(ordenId, s));

        api.MapPost("/ordenes/{ordenId:guid}/cancelar", (Guid ordenId, SolicitudCompra s, PosDbContext db) =>
            new RegistroOrdenes(db).CancelarAsync(ordenId, s.UsuarioId));

        api.MapPost("/aperturas", (SolicitudAbrirPaquete s, PosDbContext db) => new RegistroAperturas(db).AbrirAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/aperturas", (Guid sucursalId, PosDbContext db) => new RegistroAperturas(db).RecientesAsync(sucursalId));

        api.MapGet("/variantes/{varianteId:guid}/ultima-apertura", async (Guid varianteId, PosDbContext db) =>
            await new RegistroAperturas(db).UltimaAsync(varianteId) is { } ultima ? Results.Ok(ultima) : Results.NoContent());

        // ── Ajustes y mermas (entrega 3) ──

        api.MapPost("/inventario/ajustes", (SolicitudAjuste s, PosDbContext db) => new RegistroAjustes(db).AjustarAsync(s));

        api.MapPost("/inventario/mermas", (SolicitudMerma s, PosDbContext db) => new RegistroAjustes(db).MermaAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/ajustes", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroAjustes(db).RecientesAsync(sucursalId, usuarioId));

        api.MapGet("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, PosDbContext db) => new RegistroCompras(db).MargenesAsync(sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, SolicitudMargenes s, PosDbContext db) =>
            new RegistroCompras(db).GuardarMargenesAsync(sucursalId, s));
    }
}
