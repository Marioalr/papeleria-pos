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

        api.MapGet("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, PosDbContext db) => new RegistroCompras(db).MargenesAsync(sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, SolicitudMargenes s, PosDbContext db) =>
            new RegistroCompras(db).GuardarMargenesAsync(sucursalId, s));
    }
}
