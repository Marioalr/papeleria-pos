using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las fotos de los productos (entrega 13, parte 2). Se suben desde el celular (la página del conteo) o desde la PC (Productos,
/// F3): el servicio revisa que sea una imagen, la endereza, la achica y la guarda chica (<see cref="FotoDelProducto"/>), y
/// cada caja la baja de aquí una sola vez (por su huella, que viaja en la copia) para enseñarla de su base, sin la red. Las
/// pone y las quita quien entró con «Productos, precios y etiquetas»; verlas, cualquier caja. Desde la app del celular
/// (docs/celular-como-caja.md, paso 12): con la llave de un celular dado de alta basta, y queda a nombre del celular.
/// </summary>
public static class ApiFotos
{
    /// <summary>Lo que ocupa en base64 la foto más grande que se acepta (<see cref="FotoDelProducto.BytesMaximos"/>).</summary>
    private const int Base64Maximo = (FotoDelProducto.BytesMaximos + 2) / 3 * 4;

    public static void MapearApiFotos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/fotos").ConErrores().SoloSuCaja();
        // Lo que el registro no guarda, con su código (el producto ya no está, dos a la vez).
        api.AddEndpointFilter(async (contexto, siguiente) =>
        {
            try
            {
                return await siguiente(contexto);
            }
            catch (FotoRechazada ex)
            {
                throw new RechazoConCodigo(ex.Message, ex.Codigo);
            }
        });

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
        api.MapPut("/{varianteId:guid}", async (Guid varianteId, SolicitudFoto s, HttpContext http, PosDbContext db, PreparacionDeFotos preparacion,
            CancellationToken ct) =>
        {
            var registro = new RegistroFotos(db);
            var celular = await CelularAsync(http, db, ct);
            await registro.RevisarAsync(varianteId, s.UsuarioId, s.SucursalId, ct, celular);
            if (string.IsNullOrEmpty(s.ImagenBase64))
                throw new RechazoConCodigo("No llegó ninguna foto.", "sin-foto");
            // Antes de convertirla: una más grande que lo que se acepta ni se lee.
            if (s.ImagenBase64.Length > Base64Maximo)
                throw new RechazoConCodigo($"La foto pasa de {FotoDelProducto.BytesMaximos / 1024 / 1024} MB: usa una más chica.", "muy-grande");
            var foto = await PrepararAsync(preparacion, s.ImagenBase64, ct);
            return await registro.GuardarAsync(varianteId, s, foto.Jpeg, foto.Ancho, foto.Alto, ct, celular);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Base64Maximo + 64 * 1024));

        api.MapPost("/{varianteId:guid}/quitar", async (Guid varianteId, SolicitudQuitarFoto s, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            await new RegistroFotos(db).QuitarAsync(varianteId, s, ct, await CelularAsync(http, db, ct));
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Si quien llama, con su llave, es un celular dado de alta (docs/celular-como-caja.md, paso 12): una caja activa que nunca ha
    /// abierto un turno, como en el cobro del celular. Pone y quita fotos con su llave; una caja que abre turnos, con quien entró.
    /// </summary>
    private static async Task<CelularDeLaFoto?> CelularAsync(HttpContext http, PosDbContext db, CancellationToken ct)
    {
        if (!http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var deLaLlave) || deLaLlave is not Guid cajaId)
            return null;
        return await db.Cajas.AsNoTracking().Where(c => c.Id == cajaId && c.Activa && !db.Turnos.Any(t => t.CajaId == c.Id))
            .Select(c => new CelularDeLaFoto(c.Id, c.Nombre, c.SucursalId)).SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// La foto que llegó, lista para guardarse, o por qué no con su código: <c>imagen</c> (no se pudo leer: otra foto) u
    /// <c>ocupada</c> (la fila no avanzó: la misma, en un momento).
    /// </summary>
    private static async Task<FotoPreparada> PrepararAsync(PreparacionDeFotos preparacion, string base64, CancellationToken ct)
    {
        byte[] original;
        try
        {
            original = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new RechazoConCodigo("La foto no llegó bien (no es base64): mándala otra vez.", "imagen");
        }
        try
        {
            return await preparacion.PrepararAsync(original, ct);
        }
        catch (ArgumentException ex) when (EsNuestro(ex))
        {
            throw new RechazoConCodigo(ErroresApi.ParaLaCaja(ex, null), "imagen");
        }
        catch (InvalidOperationException ex) when (EsNuestro(ex))
        {
            throw new RechazoConCodigo(ex.Message, "ocupada");
        }
    }

    /// <summary>Un «no» de los nuestros (una regla de las fotos); los del marco siguen su camino al registro (<see cref="ErroresApi.ParaLaCaja"/>).</summary>
    private static bool EsNuestro(Exception ex) => ex.Source?.StartsWith("PapeleriaPos", StringComparison.Ordinal) == true;
}
