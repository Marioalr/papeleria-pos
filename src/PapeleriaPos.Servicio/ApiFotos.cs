using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las fotos de los productos (entrega 13, parte 2). Se suben desde el celular (la página del conteo) o desde la PC (Productos,
/// F3): el servicio revisa que sea una imagen, la endereza, la achica y la guarda chica (<see cref="FotoDelProducto"/>), y
/// cada caja la baja de aquí una sola vez (por su huella, que viaja en la copia) para enseñarla de su base, sin la red. Las
/// pone y las quita quien entró con «Productos, precios y etiquetas»; verlas, cualquier caja.
/// </summary>
public static class ApiFotos
{
    /// <summary>Lo que ocupa en base64 la foto más grande que se acepta (<see cref="FotoDelProducto.BytesMaximos"/>).</summary>
    private const int Base64Maximo = (FotoDelProducto.BytesMaximos + 2) / 3 * 4;

    public static void MapearApiFotos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/fotos").ConErrores().SoloSuCaja();

        // De qué producto es cada foto y su huella: la caja sin copia (el navegador) sabe con esto cuáles enseñar.
        api.MapGet("", (PosDbContext db, CancellationToken ct) => new RegistroFotos(db).LeerAsync(ct));

        // La foto de un producto, como la baja cada caja (con su huella en la ETag).
        api.MapGet("/{varianteId:guid}", async (Guid varianteId, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await new RegistroFotos(db).FotoAsync(varianteId, ct) is not { } foto)
                return Results.NotFound(new ErrorDto("Ese producto no tiene foto."));
            var (datos, huella) = foto;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.File(datos, "image/jpeg", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{huella}\""));
        });

        // La foto como la pide la pantalla de la caja del navegador (el src de su imagen): «{variante}-{huella}.jpg». Con la misma
        // huella no cambia nunca: el navegador la guarda y no la vuelve a pedir. La ventana de la caja la da de su base local.
        api.MapGet("/{archivo}", async (string archivo, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!FotoProducto.LeerArchivo(archivo, out var varianteId, out var pedida)
                || await new RegistroFotos(db).FotoAsync(varianteId, ct) is not { } foto)
                return Results.NotFound(new ErrorDto("Esa foto no está."));
            var (datos, huella) = foto;
            http.Response.Headers.CacheControl = huella == pedida ? "private, max-age=31536000, immutable" : "no-cache";
            return Results.File(datos, "image/jpeg");
        });

        // Una foto nueva o en lugar de la que tenía: primero quién y de qué producto; luego se lee, se endereza y se achica, de
        // una en una (PreparacionDeFotos). El cuerpo no pasa de lo que ocupa en base64 la foto más grande que se acepta.
        api.MapPut("/{varianteId:guid}", async (Guid varianteId, SolicitudFoto s, PosDbContext db, PreparacionDeFotos preparacion, CancellationToken ct) =>
        {
            var registro = new RegistroFotos(db);
            await registro.RevisarAsync(varianteId, s.UsuarioId, s.SucursalId, ct);
            if (string.IsNullOrEmpty(s.ImagenBase64))
                throw new ArgumentException("No llegó ninguna foto.");
            // Antes de convertirla: una más grande que lo que se acepta ni se lee.
            if (s.ImagenBase64.Length > Base64Maximo)
                throw new ArgumentException($"La foto pasa de {FotoDelProducto.BytesMaximos / 1024 / 1024} MB: usa una más chica.");
            var foto = await preparacion.PrepararAsync(Convert.FromBase64String(s.ImagenBase64), ct);
            return await registro.GuardarAsync(varianteId, s, foto.Jpeg, foto.Ancho, foto.Alto, ct);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Base64Maximo + 64 * 1024));

        api.MapPost("/{varianteId:guid}/quitar", async (Guid varianteId, SolicitudQuitarFoto s, PosDbContext db, CancellationToken ct) =>
        {
            await new RegistroFotos(db).QuitarAsync(varianteId, s, ct);
            return Results.NoContent();
        });
    }
}
