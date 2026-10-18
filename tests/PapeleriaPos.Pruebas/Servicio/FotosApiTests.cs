using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Impresion;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las fotos de los productos en el servicio (entrega 13, parte 2): llegan grandes (la del celular) y se guardan chicas, una por
/// producto; otra la reemplaza y se puede quitar; las pone quien tiene «Productos, precios y etiquetas» y queda en la bitácora.
/// Cada cambio sube la versión del catálogo, y la copia de las cajas lleva solo de qué producto es cada foto y su huella.
/// </summary>
public sealed class FotosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Caja> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        return new Caja(http, caja, cajero, encargado);
    }

    /// <summary>Productos de la copia, en un orden fijo, distintos en cada prueba (comparten la base).</summary>
    private static async Task<List<ProductoVentaDto>> ProductosAsync(Caja c, int desde, int cuantos) =>
        (await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"))).Productos
        .OrderBy(p => p.VarianteId).Skip(desde).Take(cuantos).ToList();

    private static Task<HttpResponseMessage> SubirAsync(Caja c, Guid varianteId, byte[] imagen, SesionDto? quien = null) =>
        c.Http.PutAsJsonAsync($"api/fotos/{varianteId}",
            new SolicitudFoto((quien ?? c.Encargado).UsuarioId, c.Datos.SucursalId, Convert.ToBase64String(imagen), "el celular"));

    private async Task<long> VersionAsync()
    {
        await using var db = servicio.Base();
        return await db.Secuencias.Where(s => s.Nombre == PapeleriaPos.Datos.Secuencia.Catalogo).Select(s => s.Valor).SingleAsync();
    }

    [Fact]
    public async Task La_foto_del_celular_se_guarda_chica_y_se_baja_tal_cual_con_su_huella()
    {
        var c = await CajaAsync();
        var producto = (await ProductosAsync(c, 0, 1))[0];
        var original = ImagenesDePrueba.Foto(4000, 3000);
        var antes = await VersionAsync();

        var guardada = await Leer<FotoGuardadaDto>(await SubirAsync(c, producto.VarianteId, original));

        // De 240 y chica: la original (cientos de KB) nunca se guarda.
        Assert.Equal((producto.VarianteId, 240, 180), (guardada.VarianteId, guardada.Ancho, guardada.Alto));
        Assert.InRange(guardada.Bytes, 1, PapeleriaPos.Impresion.FotoDelProducto.BytesObjetivo);
        Assert.True(original.Length > 10 * guardada.Bytes);
        await using (var db = servicio.Base())
        {
            var enLaBase = await db.FotosProducto.SingleAsync(f => f.VarianteId == producto.VarianteId);
            Assert.Equal((guardada.Bytes, guardada.Huella), (enLaBase.Datos.Length, enLaBase.Huella));
            Assert.True(enLaBase.Version > antes);
            Assert.Contains(await db.Bitacora.Where(b => b.Accion == AccionControlada.EditarCatalogo && b.UsuarioId == c.Encargado.UsuarioId).ToListAsync(),
                b => b.Detalle.StartsWith("Puso la foto de «", StringComparison.Ordinal) && b.Detalle.EndsWith("(desde el celular)", StringComparison.Ordinal));
        }
        // Como la baja cada caja: los mismos bytes, con su huella.
        var bajada = await c.Http.GetAsync($"api/fotos/{producto.VarianteId}");
        Assert.Equal("image/jpeg", bajada.Content.Headers.ContentType!.MediaType);
        var bytes = await bajada.Content.ReadAsByteArrayAsync();
        Assert.Equal(Convert.FromBase64String(guardada.JpegBase64), bytes);
        Assert.Equal($"\"{guardada.Huella}\"", bajada.Headers.ETag!.Tag);
        // Como la pide la pantalla de la caja del navegador: por su archivo, y con la misma huella no cambia nunca.
        var archivo = await c.Http.GetAsync(PapeleriaPos.Nucleo.Catalogo.FotoProducto.Archivo(producto.VarianteId, guardada.Huella));
        Assert.Equal(bytes, await archivo.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", archivo.Headers.CacheControl!.ToString());
        // Y en la lista de todas.
        var todas = await Leer<FotosDto>(await c.Http.GetAsync("api/fotos"));
        Assert.Contains(new FotoDeProductoDto(producto.VarianteId, guardada.Huella), todas.Fotos);
        Assert.True(todas.Version > antes);
    }

    [Fact]
    public async Task Otra_foto_la_reemplaza_la_misma_no_cambia_nada_y_se_puede_quitar()
    {
        var c = await CajaAsync();
        var producto = (await ProductosAsync(c, 3, 1))[0];
        var primera = await Leer<FotoGuardadaDto>(await SubirAsync(c, producto.VarianteId, ImagenesDePrueba.Foto(1200, 900)));
        var version = await VersionAsync();

        // La misma otra vez: nada cambia, ni la versión (las cajas no bajan nada).
        var otraVez = await Leer<FotoGuardadaDto>(await SubirAsync(c, producto.VarianteId, ImagenesDePrueba.Foto(1200, 900)));
        Assert.Equal(primera.Huella, otraVez.Huella);
        Assert.Equal(version, await VersionAsync());

        // Otra la reemplaza: una sola por producto.
        var segunda = await Leer<FotoGuardadaDto>(await SubirAsync(c, producto.VarianteId, ImagenesDePrueba.Mitades(900, 1200)));
        Assert.NotEqual(primera.Huella, segunda.Huella);
        Assert.Equal((180, 240), (segunda.Ancho, segunda.Alto));
        Assert.True(await VersionAsync() > version);
        await using (var db = servicio.Base())
            Assert.Equal(segunda.Huella, (await db.FotosProducto.SingleAsync(f => f.VarianteId == producto.VarianteId)).Huella);

        // Quitarla: ya no está, y queda anotado lo quitado (las cajas se enteran con lo que cambió).
        version = await VersionAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.Http.PostAsJsonAsync($"api/fotos/{producto.VarianteId}/quitar", new SolicitudQuitarFoto(c.Encargado.UsuarioId, c.Datos.SucursalId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Http.GetAsync($"api/fotos/{producto.VarianteId}")).StatusCode);
        Assert.DoesNotContain((await Leer<FotosDto>(await c.Http.GetAsync("api/fotos"))).Fotos, f => f.VarianteId == producto.VarianteId);
        await using (var db = servicio.Base())
        {
            Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));
            Assert.True(await db.Borrados.AnyAsync(b => b.Entidad == "FotoProducto" && b.EntidadId == producto.VarianteId && b.Version > version));
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.StartsWith("Quitó la foto de «")));
        }
        // Quitar la de un producto sin foto no hace nada.
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.Http.PostAsJsonAsync($"api/fotos/{producto.VarianteId}/quitar", new SolicitudQuitarFoto(c.Encargado.UsuarioId, c.Datos.SucursalId))).StatusCode);
    }

    [Fact]
    public async Task El_cajero_no_pone_ni_quita_fotos()
    {
        var c = await CajaAsync();
        var producto = (await ProductosAsync(c, 6, 1))[0];

        var subida = await SubirAsync(c, producto.VarianteId, ImagenesDePrueba.Foto(800, 600), c.Cajero);
        var quitada = await c.Http.PostAsJsonAsync($"api/fotos/{producto.VarianteId}/quitar", new SolicitudQuitarFoto(c.Cajero.UsuarioId, c.Datos.SucursalId));

        Assert.Equal(HttpStatusCode.Forbidden, subida.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, quitada.StatusCode);
        var error = (await subida.Content.ReadFromJsonAsync<ErrorDto>())!;
        Assert.Equal("no-permitido", error.Codigo);
        Assert.Contains("«Productos, precios y etiquetas»", error.Mensaje);
        await using var db = servicio.Base();
        Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));
    }

    [Fact]
    public async Task Lo_que_no_es_una_foto_un_producto_que_no_existe_o_una_foto_enorme_no_se_guardan()
    {
        var c = await CajaAsync();
        var producto = (await ProductosAsync(c, 9, 1))[0];

        async Task<string> RechazoAsync(HttpResponseMessage r)
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
        }

        Assert.Contains("No se reconoce", await RechazoAsync(await SubirAsync(c, producto.VarianteId, "esto no es una foto"u8.ToArray())));
        Assert.Contains("No llegó", await RechazoAsync(await SubirAsync(c, producto.VarianteId, [])));
        Assert.Contains("ya no está en el catálogo", await RechazoAsync(await SubirAsync(c, Guid.NewGuid(), ImagenesDePrueba.Foto(400, 300))));
        Assert.Contains("15 MB", await RechazoAsync(await c.Http.PutAsJsonAsync($"api/fotos/{producto.VarianteId}",
            new SolicitudFoto(c.Encargado.UsuarioId, c.Datos.SucursalId, new string('A', 21 * 1024 * 1024)))));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Http.PutAsJsonAsync($"api/fotos/{producto.VarianteId}",
            new SolicitudFoto(c.Encargado.UsuarioId, c.Datos.SucursalId, "no-es-base64!"))).StatusCode);
        await using var db = servicio.Base();
        Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));
    }

    /// <summary>
    /// Lo que se lee completo y dice medir de más (como el PNG de la revisión, que ocupaba 900 MB) se rechaza antes de leerlo, y no
    /// se guarda nada.
    /// </summary>
    [Fact]
    public async Task Una_imagen_que_dice_medir_de_mas_se_rechaza_sin_leerla()
    {
        var c = await CajaAsync();
        var producto = (await ProductosAsync(c, 17, 1))[0];

        var respuesta = await SubirAsync(c, producto.VarianteId, ImagenesDePrueba.PngQueDiceMedir(10_000, 9_999));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("se lee completa", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        await using var db = servicio.Base();
        Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));
    }

    /// <summary>
    /// Las fotos se preparan de una en una (revisión de la entrega 13): si llegan tres a la vez (dos celulares y la PC), las otras
    /// esperan su turno y nunca se leen dos juntas; las tres quedan.
    /// </summary>
    [Fact]
    public async Task Las_fotos_se_preparan_de_una_en_una()
    {
        var c = await CajaAsync();
        var productos = await ProductosAsync(c, 20, 3);
        var preparacion = servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.PreparacionDeFotos>();
        var primera = 0;
        // La primera, ya con su turno, espera a que las otras dos estén formadas: así llegan a la vez de verdad.
        preparacion.AlEmpezar = async () =>
        {
            if (Interlocked.Exchange(ref primera, 1) == 0)
                await PapeleriaPos.Pruebas.Interfaz.VentaConCopia.EsperarAsync(() => preparacion.Esperando == 2, "las otras dos en la fila");
        };
        try
        {
            var respuestas = await Task.WhenAll(productos.Select((p, i) => SubirAsync(c, p.VarianteId, ImagenesDePrueba.Foto(1200 + 100 * i, 900))));
            Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }
        finally
        {
            preparacion.AlEmpezar = null;
        }
        Assert.Equal(1, preparacion.MasALaVez);
        await using var db = servicio.Base();
        Assert.Equal(3, await db.FotosProducto.CountAsync(f => productos.Select(p => p.VarianteId).Contains(f.VarianteId)));
    }

    /// <summary>
    /// La copia de las cajas lleva de qué producto es cada foto y su huella, nada más: la copia completa no crece con las fotos
    /// (unos 80 bytes por foto). Lo que cambió trae las nuevas, las cambiadas y las quitadas, y con ello encima queda
    /// igual que la completa.
    /// </summary>
    [Fact]
    public async Task La_copia_lleva_solo_la_huella_y_lo_que_cambio_de_las_fotos_queda_igual_que_la_completa()
    {
        var c = await CajaAsync();
        var productos = await ProductosAsync(c, 12, 4);
        await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[0].VarianteId, ImagenesDePrueba.Foto(1000, 800)));
        await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[1].VarianteId, ImagenesDePrueba.Foto(800, 1000)));
        var antes = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));

        // Una nueva, una cambiada y una quitada.
        var nueva = await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[2].VarianteId, ImagenesDePrueba.Foto(2000, 1500)));
        var cambiada = await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[0].VarianteId, ImagenesDePrueba.Mitades(1000, 800)));
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PostAsJsonAsync($"api/fotos/{productos[1].VarianteId}/quitar",
            new SolicitudQuitarFoto(c.Encargado.UsuarioId, c.Datos.SucursalId))).StatusCode);
        // Y una que se quitó y se volvió a poner: va como puesta.
        await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[3].VarianteId, ImagenesDePrueba.Foto(600, 600)));
        await c.Http.PostAsJsonAsync($"api/fotos/{productos[3].VarianteId}/quitar", new SolicitudQuitarFoto(c.Encargado.UsuarioId, c.Datos.SucursalId));
        var otraVez = await Leer<FotoGuardadaDto>(await SubirAsync(c, productos[3].VarianteId, ImagenesDePrueba.Mitades(600, 600)));
        await servicio.CatalogoAlDiaAsync();

        var cambios = await Leer<CambiosCopiaDto>(await c.Http.GetAsync(
            $"api/cajas/{c.Datos.Id}/cambios?desde={antes.Version}&existencias={antes.VersionExistencias}&base={antes.Base}&formato={CopiaAlDia.Formato}"
            + $"&vendidos={antes.VersionVendidos}&elegidos={antes.VersionElegidos}"));
        var despues = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));

        Assert.Null(cambios.PaqueteCompleto);
        // Solo las fotos: ningún producto, ni se arma otra vez su búsqueda ni se guarda la copia entera por ellas.
        Assert.Empty(cambios.Productos);
        Assert.False(cambios.CambiaCatalogo);
        Assert.True(cambios.CambianLasFotos);
        Assert.Equal(new[]
            {
                new FotoDeProductoDto(productos[0].VarianteId, cambiada.Huella), new FotoDeProductoDto(productos[2].VarianteId, nueva.Huella),
                new FotoDeProductoDto(productos[3].VarianteId, otraVez.Huella),
            }.OrderBy(f => f.VarianteId), cambios.Fotos!);
        Assert.Equal([productos[1].VarianteId], cambios.FotosQuitadas!);
        var puesta = CopiaAlDia.Aplicar(antes, cambios);
        Assert.Equal(despues.Fotos!, puesta.Fotos!);
        Assert.Equal(despues.Fotos!.OrderBy(f => f.VarianteId), despues.Fotos!);
        // La copia completa lleva unos 80 bytes por foto (de qué producto y su huella), pese lo que pese la foto, y nada de las
        // fotos mismas: cada caja las baja aparte.
        var copia = JsonSerializer.Serialize(despues, JsonSerializerOptions.Web);
        var sinFotos = JsonSerializer.Serialize(despues with { Fotos = [] }, JsonSerializerOptions.Web);
        Assert.InRange((copia.Length - sinFotos.Length) / (double)despues.Fotos!.Count, 60, 90);
        Assert.All(new[] { nueva, cambiada, otraVez }, f => Assert.DoesNotContain(f.JpegBase64[..40], copia, StringComparison.Ordinal));
        Assert.All(despues.Fotos!, f => Assert.Equal(PapeleriaPos.Nucleo.Catalogo.FotoProducto.LargoHuella, f.Huella.Length));
    }
}
