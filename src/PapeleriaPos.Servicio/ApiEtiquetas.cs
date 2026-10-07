using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>Las etiquetas de código de barras (fase 2, entrega 4, parte 1). La hoja la arma y la imprime la caja.</summary>
public static class ApiEtiquetas
{
    public static void MapearApiEtiquetas(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/etiquetas/configuracion", (Guid sucursalId, PosDbContext db) =>
            new RegistroEtiquetas(db).ConfiguracionAsync(sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/etiquetas/configuracion", (Guid sucursalId, SolicitudConfiguracionEtiquetas s, PosDbContext db) =>
            new RegistroEtiquetas(db).GuardarConfiguracionAsync(sucursalId, s));

        api.MapGet("/compras/{compraId:guid}/etiquetas", (Guid compraId, PosDbContext db) => new RegistroEtiquetas(db).DeCompraAsync(compraId));

        api.MapGet("/etiquetas/sin-codigo", (Guid departamentoId, PosDbContext db) => new RegistroEtiquetas(db).SinCodigoAsync(departamentoId));

        api.MapPost("/etiquetas", (SolicitudEtiquetas s, PosDbContext db) => new RegistroEtiquetas(db).PrepararAsync(s));
    }
}
