using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Inventario › «Traspasos» (2026-11-26): mandar mercancía a otra sucursal con un archivo, provisional mientras no exista el
/// servidor central (docs/traspasos.md). Todo lo decide <see cref="RegistroTraspasos"/>, con los permisos de quien entró. En el
/// piloto con eleventa el traspaso es solo el documento: no mueve existencias. Al llegar el servidor, esto se quita o se cambia
/// por su versión.
/// </summary>
public static class ApiTraspasos
{
    /// <summary>Lo más que ocupa la solicitud de importar: el archivo más grande que se acepta, en base64, y lo demás.</summary>
    internal const int CuerpoMaximo = (ArchivoDeTraspaso.BytesMaximos + 2) / 3 * 4 + 16 * 1024;

    public static void MapearApiTraspasos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/traspasos", (Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroTraspasos(db, eleventa.Activo).ListaAsync(sucursalId, usuarioId));

        api.MapGet("/sucursales/{sucursalId:guid}/traspasos/{id:guid}", (Guid sucursalId, Guid id, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroTraspasos(db, eleventa.Activo).VerAsync(id, sucursalId, usuarioId));

        // ── La sucursal que lo tramita: el borrador (la lista completa cada vez), exportarlo (sale la mercancía) y su archivo ──

        api.MapPost("/traspasos", (SolicitudGuardarTraspaso s, PosDbContext db, OpcionesEleventa eleventa) => new RegistroTraspasos(db, eleventa.Activo).GuardarAsync(s));

        api.MapPost("/traspasos/{id:guid}/exportar", (Guid id, SolicitudDeTraspaso s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroTraspasos(db, eleventa.Activo).ExportarAsync(id, s));

        api.MapPost("/traspasos/{id:guid}/descartar", async (Guid id, SolicitudDeTraspaso s, PosDbContext db) =>
        {
            await new RegistroTraspasos(db).DescartarAsync(id, s);
            return Results.NoContent();
        });

        api.MapGet("/sucursales/{sucursalId:guid}/traspasos/{id:guid}/archivo", async (Guid sucursalId, Guid id, Guid usuarioId, PosDbContext db) =>
        {
            var (nombre, datos) = await new RegistroTraspasos(db).ArchivoAsync(id, sucursalId, usuarioId);
            return new ArchivoDeTraspasoDto(nombre, Convert.ToBase64String(datos));
        });

        // ── La sucursal que recibe: importar el archivo, y lo que se revisó (guardar, marcar para revisión o recibir) ──

        api.MapPost("/traspasos/importar", (SolicitudImportarTraspaso s, PosDbContext db, OpcionesEleventa eleventa) =>
                new RegistroTraspasos(db, eleventa.Activo).ImportarAsync(s))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(CuerpoMaximo));

        api.MapPost("/traspasos/{id:guid}/recibir", (Guid id, SolicitudRecepcion s, PosDbContext db, OpcionesEleventa eleventa) =>
            new RegistroTraspasos(db, eleventa.Activo).RecibirAsync(id, s));
    }
}
