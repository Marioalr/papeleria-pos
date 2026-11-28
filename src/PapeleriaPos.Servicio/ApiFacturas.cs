using PapeleriaPos.Datos;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Inventario › «Recibir con factura» (2026-11-27; docs/recibir-con-factura.md): la mercancía de un proveedor se recibe con el XML de
/// su factura, como un traspaso con su archivo. Todo lo decide <see cref="RegistroFacturas"/>, con los permisos de quien entró. En
/// el piloto con eleventa es solo el documento: no mueve existencias.
/// </summary>
public static class ApiFacturas
{
    /// <summary>Lo más que ocupa la solicitud de cargar: el documento más grande que se acepta, en base64, y lo demás.</summary>
    internal const int CuerpoMaximo = (LectorDocumentos.BytesMaximos + 2) / 3 * 4 + 16 * 1024;

    public static void MapearApiFacturas(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/facturas", (Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroFacturas(db, eleventa.Activo).ListaAsync(sucursalId, usuarioId));

        api.MapGet("/sucursales/{sucursalId:guid}/facturas/{id:guid}", (Guid sucursalId, Guid id, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroFacturas(db, eleventa.Activo).VerAsync(id, sucursalId, usuarioId));

        api.MapPost("/facturas", (SolicitudImportarFactura s, PosDbContext db, OpcionesEleventa eleventa) => new RegistroFacturas(db, eleventa.Activo).ImportarAsync(s))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(CuerpoMaximo));

        // Lo que se revisó: guardarlo, dejarlo marcado para revisión o recibirla (entra la mercancía).
        api.MapPost("/facturas/{id:guid}/recibir", (Guid id, SolicitudRecibirFactura s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroFacturas(db, eleventa.Activo).RecibirAsync(id, s));

        api.MapPost("/facturas/{id:guid}/descartar", async (Guid id, SolicitudDeFactura s, PosDbContext db) =>
        {
            await new RegistroFacturas(db).DescartarAsync(id, s);
            return Results.NoContent();
        });
    }
}
