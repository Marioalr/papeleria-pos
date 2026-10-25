using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// F4 Inventario, compras (fase 2, entrega 2): proveedores, recibir a ciegas, la nota, cerrar y el margen mínimo. También lo que
/// mueve existencias sin compra: ajustes, mermas, abrir un paquete y agregar inventario (como en eleventa, 2026-10-24).
/// </summary>
public static class ApiCompras
{
    public static void MapearApiCompras(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Las tarjetas del inventario (entrega 8): lo pendiente de cada tarea, solo cuentas.
        api.MapGet("/sucursales/{sucursalId:guid}/inventario/pendientes", (Guid sucursalId, PosDbContext db) =>
            new RegistroCompras(db).PendientesAsync(sucursalId));

        api.MapGet("/proveedores", (bool? inactivos, PosDbContext db) => new RegistroCompras(db).ProveedoresAsync(inactivos ?? false));

        api.MapPost("/proveedores", (SolicitudGuardarProveedor s, PosDbContext db) => new RegistroCompras(db).GuardarProveedorAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/compras", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroCompras(db).ListaAsync(sucursalId, usuarioId));

        // En el piloto con eleventa no se abre, no se cuenta lo recibido ni se cierra una compra: el inventario se lleva en
        // eleventa (TrastiendaEnElPiloto). Lo demás (la nota, los nuevos, cancelar) sigue igual con lo que quedó abierto.
        api.MapPost("/compras", (SolicitudAbrirCompra s, PosDbContext db, OpcionesEleventa eleventa) => new RegistroCompras(db, eleventa.Activo).AbrirAsync(s));

        // Quien cuenta no recibe la nota ni los costos: solo quien los captura, con el conteo ya cerrado.
        api.MapGet("/compras/{compraId:guid}", async (Guid compraId, Guid usuarioId, PosDbContext db) =>
            await new RegistroCompras(db).DetalleAsync(compraId, usuarioId) is { } compra
                ? Results.Ok(compra)
                : Results.NotFound(new ErrorDto("La compra ya no existe.")));

        api.MapPost("/compras/{compraId:guid}/conteo", (Guid compraId, SolicitudConteo s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroCompras(db, eleventa.Activo).ContarAsync(compraId, s));

        api.MapPost("/compras/{compraId:guid}/terminar-conteo", (Guid compraId, SolicitudCompra s, PosDbContext db) =>
            new RegistroCompras(db).TerminarConteoAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/renglones", (Guid compraId, SolicitudRenglonCompra s, PosDbContext db) =>
            new RegistroCompras(db).RenglonAsync(compraId, s));

        // El XML de la factura o el PDF de la nota llena los renglones (fase 2, entrega 2, parte 2).
        api.MapPost("/compras/{compraId:guid}/documentos", (Guid compraId, SolicitudDocumentoCompra s, PosDbContext db) =>
            new RegistroCompras(db).CargarDocumentoAsync(compraId, s));

        // ── Todo el pedido desde el documento: lo que no está en el catálogo ──

        api.MapPost("/compras/{compraId:guid}/conteo/desconocido", (Guid compraId, SolicitudConteoDesconocido s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroCompras(db, eleventa.Activo).ContarDesconocidoAsync(compraId, s));

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

        api.MapPost("/compras/{compraId:guid}/cerrar", (Guid compraId, SolicitudCompra s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroCompras(db, eleventa.Activo).CerrarAsync(compraId, s.UsuarioId));

        api.MapPost("/compras/{compraId:guid}/cancelar", (Guid compraId, SolicitudCancelarCompra s, PosDbContext db) =>
            new RegistroCompras(db).CancelarAsync(compraId, s));

        // El precio sugerido después de una compra con margen bajo (o otro): como un cambio de precio del catálogo. En el
        // piloto con eleventa, el de un producto de eleventa se cambia allá.
        api.MapPost("/catalogo/precio-menudeo", async (SolicitudPrecioMenudeo s, PosDbContext db, OpcionesEleventa eleventa) =>
        {
            await new RegistroCatalogo(db, eleventa.Activo).CambiarMenudeoAsync(s);
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

        // ── Sugerido de compra (entrega 4, parte 3) ──

        api.MapGet("/sucursales/{sucursalId:guid}/sugerido", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroSugerido(db).CalcularAsync(sucursalId, usuarioId));

        // Cuánto cubre el sugerido: se ve y se cambia en Configuración › Inventario (entrega 7), sin calcular el sugerido.
        api.MapGet("/sucursales/{sucursalId:guid}/sugerido/parametros", (Guid sucursalId, PosDbContext db) =>
            new RegistroSugerido(db).ParametrosAsync(sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/sugerido/parametros", (Guid sucursalId, SolicitudParametrosSugerido s, PosDbContext db) =>
            new RegistroSugerido(db).GuardarParametrosAsync(sucursalId, s));

        api.MapPost("/sucursales/{sucursalId:guid}/sugerido/orden", (Guid sucursalId, SolicitudOrdenSugerida s, PosDbContext db) =>
            new RegistroSugerido(db).OrdenAsync(sucursalId, s));

        api.MapPost("/aperturas", (SolicitudAbrirPaquete s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroAperturas(db, eleventa.Activo).AbrirAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/aperturas", (Guid sucursalId, PosDbContext db) => new RegistroAperturas(db).RecientesAsync(sucursalId));

        api.MapGet("/variantes/{varianteId:guid}/ultima-apertura", async (Guid varianteId, PosDbContext db) =>
            await new RegistroAperturas(db).UltimaAsync(varianteId) is { } ultima ? Results.Ok(ultima) : Results.NoContent());

        // ── Ajustes y mermas (entrega 3); en el piloto con eleventa, no ──

        api.MapPost("/inventario/ajustes", (SolicitudAjuste s, PosDbContext db, OpcionesEleventa eleventa) => new RegistroAjustes(db, eleventa.Activo).AjustarAsync(s));

        api.MapPost("/inventario/mermas", (SolicitudMerma s, PosDbContext db, OpcionesEleventa eleventa) => new RegistroAjustes(db, eleventa.Activo).MermaAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/ajustes", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroAjustes(db).RecientesAsync(sucursalId, usuarioId));

        // ── Agregar inventario, como en eleventa (2026-10-24): un producto, cuántos llegaron y su costo; en el piloto, no ──

        api.MapGet("/sucursales/{sucursalId:guid}/inventario/agregar/{varianteId:guid}", (Guid sucursalId, Guid varianteId, Guid usuarioId, PosDbContext db) =>
            new RegistroAgregarInventario(db).ProductoAsync(sucursalId, varianteId, usuarioId));

        api.MapPost("/inventario/agregar", (SolicitudAgregarInventario s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroAgregarInventario(db, eleventa.Activo).AgregarAsync(s));

        api.MapGet("/sucursales/{sucursalId:guid}/inventario/agregado-hoy", (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
            new RegistroAgregarInventario(db).HoyAsync(sucursalId, usuarioId));

        // ── Conteo cíclico (entrega 3, parte 2); en el piloto con eleventa no hay conteo del día ──

        api.MapGet("/sucursales/{sucursalId:guid}/conteo-del-dia", (Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroConteoCiclico(db, eleventa.Activo).HoyAsync(sucursalId, usuarioId));

        api.MapPost("/conteos/{conteoId:guid}/contar", (Guid conteoId, SolicitudContar s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroConteoCiclico(db, eleventa.Activo).ContarAsync(conteoId, s));

        api.MapGet("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, PosDbContext db) => new RegistroCompras(db).MargenesAsync(sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/margenes", (Guid sucursalId, SolicitudMargenes s, PosDbContext db) =>
            new RegistroCompras(db).GuardarMargenesAsync(sucursalId, s));
    }
}
