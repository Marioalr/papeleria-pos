using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Impresion;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La foto del producto desde la app del celular (docs/celular-como-caja.md, paso 12; 2026-11-26): un celular dado de alta la
/// pone y la quita con su llave, sin usuario (queda a nombre del celular) o con quien entró en él (que necesita «Productos,
/// precios y etiquetas»). Lo que contestan las rutas, sus límites y sus errores, tal como están en el documento.
/// </summary>
public sealed class FotosDesdeElCelularTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static int _celulares;

    /// <summary>Lo que ya había en la bitácora al empezar cada prueba.</summary>
    private readonly HashSet<Guid> _deAntes = DeAntes(servicio);

    private static HashSet<Guid> DeAntes(ServicioDePrueba servicio)
    {
        using var db = servicio.Base();
        return db.Bitacora.AsNoTracking().Select(b => b.Id).ToHashSet();
    }

    private sealed record Celular(HttpClient Http, CajaDto Caja, HttpClient Principal, CajaDto Caja1);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> Error(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.True(estado == r.StatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    /// <summary>Un celular más, dado de alta como caja (paso 2 del documento), con su llave en cada llamada.</summary>
    private async Task<Celular> CelularAsync(string nombre = "Celular de Luis")
    {
        var principal = servicio.Fabrica.CreateClient();
        var caja1 = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.OrderBy(c => c.Serie).First();
        var n = Interlocked.Increment(ref _celulares);
        var serie = $"{(char)('C' + n / 26)}{(char)('A' + n % 26)}";
        var http = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await http.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja1.SucursalId, null, nombre, serie, "Pixel 8")));
        http.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        return new Celular(http, alta.Caja, principal, caja1);
    }

    private static async Task<SesionDto> SesionAsync(HttpClient http, string usuario, string pin) =>
        await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));

    /// <summary>Productos de la copia del celular, en un orden fijo, distintos en cada prueba (comparten la base).</summary>
    private static async Task<List<ProductoVentaDto>> ProductosAsync(Celular c, int desde, int cuantos) =>
        (await PaqueteAsync(c.Http, c.Caja.Id)).Productos.OrderBy(p => p.VarianteId).Skip(desde).Take(cuantos).ToList();

    private static async Task<PaqueteContingenciaDto> PaqueteAsync(HttpClient http, Guid cajaId) =>
        await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{cajaId}/paquete"));

    /// <summary>El cuerpo tal como lo manda la app: JSON escrito a mano, no los contratos de .NET.</summary>
    private static Task<HttpResponseMessage> PonerAsync(HttpClient http, Guid varianteId, string json) =>
        http.PutAsync($"api/fotos/{varianteId}", new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PonerAsync(HttpClient http, Guid varianteId, byte[] imagen, Guid? usuarioId = null) =>
        PonerAsync(http, varianteId, usuarioId is { } u
            ? $$"""{"usuarioId": "{{u}}", "imagenBase64": "{{Convert.ToBase64String(imagen)}}"}"""
            : $$"""{"imagenBase64": "{{Convert.ToBase64String(imagen)}}"}""");

    private static Task<HttpResponseMessage> QuitarAsync(HttpClient http, Guid varianteId, string json = "{}") =>
        http.PostAsync($"api/fotos/{varianteId}/quitar", new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>El nombre del producto como se dice en la bitácora: el del producto y, si la tiene, la descripción de su variante.</summary>
    private async Task<string> NombreAsync(ProductoVentaDto producto)
    {
        await using var db = servicio.Base();
        var v = await db.Variantes.AsNoTracking().Where(x => x.Id == producto.VarianteId).Select(x => new { x.Producto!.Nombre, x.Descripcion }).SingleAsync();
        return string.IsNullOrWhiteSpace(v.Descripcion) ? v.Nombre : $"{v.Nombre} {v.Descripcion}";
    }

    /// <summary>
    /// Lo que esta prueba dejó en la bitácora de las fotos de ese producto (otros productos de la base ficticia se llaman igual, y
    /// las pruebas de la clase comparten la base y corren una tras otra).
    /// </summary>
    private async Task<List<Bitacora>> BitacoraDeAsync(string nombre)
    {
        await using var db = servicio.Base();
        return (await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.EditarCatalogo && b.Detalle.Contains($"la foto de «{nombre}»")).ToListAsync())
            .Where(b => !_deAntes.Contains(b.Id)).ToList();
    }

    [Fact]
    public async Task Con_la_llave_del_celular_basta_queda_a_su_nombre_y_llega_a_todas_las_cajas()
    {
        var c = await CelularAsync();
        var producto = (await ProductosAsync(c, 0, 1))[0];
        var nombre = await NombreAsync(producto);
        var antes = await PaqueteAsync(c.Http, c.Caja.Id);
        Assert.DoesNotContain(antes.Fotos!, f => f.VarianteId == producto.VarianteId);

        // Solo la foto: ni usuario ni sucursal.
        var respuesta = await PonerAsync(c.Http, producto.VarianteId, ImagenesDePrueba.Foto(960, 720));

        // Lo que contesta, tal cual: para enseñar la foto ya guardada sin volver a pedirla.
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(["alto", "ancho", "bytes", "huella", "jpegBase64", "varianteId"], json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var guardada = json.RootElement.Deserialize<FotoGuardadaDto>(JsonSerializerOptions.Web)!;
        Assert.Equal((producto.VarianteId, 240, 180, 16), (guardada.VarianteId, guardada.Ancho, guardada.Alto, guardada.Huella.Length));
        var jpeg = Convert.FromBase64String(guardada.JpegBase64);
        Assert.Equal((guardada.Bytes, guardada.Huella), (jpeg.Length, PapeleriaPos.Nucleo.Catalogo.FotoProducto.HuellaDe(jpeg)));
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);

        // La bitácora: del celular (su caja y su sucursal), sin usuario, «desde el celular».
        var renglon = Assert.Single(await BitacoraDeAsync(nombre));
        Assert.Equal($"Puso la foto de «{nombre}» (desde el celular «Celular de Luis»)", renglon.Detalle);
        Assert.Equal((c.Caja.Id, null, c.Caja.SucursalId, ResultadoBitacora.Hecho), (renglon.CajaId, renglon.UsuarioId, renglon.SucursalId, renglon.Resultado));

        // Se baja con la llave del celular: los mismos bytes, la huella en la ETag; con esa huella, 304 sin cuerpo.
        var bajada = await c.Http.GetAsync($"api/fotos/{producto.VarianteId}");
        Assert.Equal("image/jpeg", bajada.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg, await bajada.Content.ReadAsByteArrayAsync());
        Assert.Equal($"\"{guardada.Huella}\"", bajada.Headers.ETag!.Tag);
        var pedido = new HttpRequestMessage(HttpMethod.Get, $"api/fotos/{producto.VarianteId}");
        pedido.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{guardada.Huella}\""));
        var laMisma = await c.Http.SendAsync(pedido);
        Assert.Equal(HttpStatusCode.NotModified, laMisma.StatusCode);
        Assert.Empty(await laMisma.Content.ReadAsByteArrayAsync());
        // Y por su archivo, que con la misma huella no cambia nunca.
        var archivo = await c.Http.GetAsync($"api/fotos/{producto.VarianteId:N}-{guardada.Huella}.jpg");
        Assert.Equal(jpeg, await archivo.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", archivo.Headers.CacheControl!.ToString());

        // Para todas las cajas: lo que cambió, para el celular y para la Caja 1, trae la foto con su huella; la copia completa también.
        await servicio.CatalogoAlDiaAsync();
        foreach (var (http, caja) in new[] { (c.Http, c.Caja.Id), (c.Principal, c.Caja1.Id) })
        {
            var cambios = await Leer<CambiosCopiaDto>(await http.GetAsync(
                $"api/cajas/{caja}/cambios?desde={antes.Version}&existencias={antes.VersionExistencias}&base={antes.Base}&formato={CopiaAlDia.Formato}"
                + $"&promociones={antes.HuellaPromociones}&vendidos={antes.VersionVendidos}&elegidos={antes.VersionElegidos}"));
            Assert.Null(cambios.PaqueteCompleto);
            Assert.Contains(new FotoDeProductoDto(producto.VarianteId, guardada.Huella), cambios.Fotos!);
            Assert.Contains(new FotoDeProductoDto(producto.VarianteId, guardada.Huella), (await PaqueteAsync(http, caja)).Fotos!);
        }
    }

    [Fact]
    public async Task Con_quien_entro_en_el_celular_queda_a_su_nombre_y_necesita_el_permiso()
    {
        var c = await CelularAsync("Celular de Ana");
        var productos = await ProductosAsync(c, 2, 2);
        var encargado = await SesionAsync(c.Http, "encargado", "2222");
        var cajero = await SesionAsync(c.Http, "cajero", "1111");
        var nombre = await NombreAsync(productos[0]);

        // El encargado puede. La sucursal que diga la app no cuenta: es la del celular.
        var foto = Convert.ToBase64String(ImagenesDePrueba.Foto(800, 600));
        await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, productos[0].VarianteId,
            $$"""{"usuarioId": "{{encargado.UsuarioId}}", "sucursalId": "{{Guid.NewGuid()}}", "imagenBase64": "{{foto}}", "desde": "otra cosa"}"""));
        var renglon = Assert.Single(await BitacoraDeAsync(nombre));
        Assert.Equal($"Puso la foto de «{nombre}» (desde el celular «Celular de Ana»)", renglon.Detalle);
        Assert.Equal((c.Caja.Id, encargado.UsuarioId, c.Caja.SucursalId), (renglon.CajaId, renglon.UsuarioId, renglon.SucursalId));

        // El cajero no (no tiene «Productos, precios y etiquetas»): ni ponerla ni quitarla, aunque el celular solo sí podría.
        var error = await Error(await PonerAsync(c.Http, productos[1].VarianteId, ImagenesDePrueba.Foto(800, 600), cajero.UsuarioId), HttpStatusCode.Forbidden);
        Assert.Equal(("Las fotos de los productos las pone y las quita quien tiene «Productos, precios y etiquetas».", "no-permitido"), (error.Mensaje, error.Codigo));
        error = await Error(await QuitarAsync(c.Http, productos[0].VarianteId, $$"""{"usuarioId": "{{cajero.UsuarioId}}"}"""), HttpStatusCode.Forbidden);
        Assert.Equal("no-permitido", error.Codigo);
        // Un usuario que no existe.
        error = await Error(await PonerAsync(c.Http, productos[1].VarianteId, ImagenesDePrueba.Foto(800, 600), Guid.NewGuid()), HttpStatusCode.BadRequest);
        Assert.Equal("El usuario no existe o está inactivo.", error.Mensaje);
        await using var db = servicio.Base();
        Assert.True(await db.FotosProducto.AnyAsync(f => f.VarianteId == productos[0].VarianteId));
        Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == productos[1].VarianteId));
    }

    [Fact]
    public async Task Una_caja_que_abre_turnos_y_la_misma_pc_sin_llave_siguen_pidiendo_quien()
    {
        var c = await CelularAsync();
        var producto = (await ProductosAsync(c, 5, 1))[0];
        var nombre = await NombreAsync(producto);
        var encargado = await SesionAsync(c.Principal, "encargado", "2222");
        // Una caja de Windows: dada de alta igual que el celular, pero abre su turno.
        var pc = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await pc.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", c.Caja1.SucursalId, null, "Caja 9", "Z", "PC-9")));
        pc.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        await Leer<TurnoDto>(await pc.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta.Caja.Id, encargado.UsuarioId, 0)));

        foreach (var http in new[] { pc, c.Principal })
        {
            var error = await Error(await PonerAsync(http, producto.VarianteId, ImagenesDePrueba.Foto(800, 600)), HttpStatusCode.BadRequest);
            Assert.Equal("El usuario no existe o está inactivo.", error.Mensaje);
            Assert.Equal(HttpStatusCode.BadRequest, (await QuitarAsync(http, producto.VarianteId)).StatusCode);
        }
        await using (var db = servicio.Base())
            Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));

        // Con quien entró, como siempre: queda a su nombre, sin caja y con lo que diga de dónde llegó.
        await Leer<FotoGuardadaDto>(await pc.PutAsJsonAsync($"api/fotos/{producto.VarianteId}",
            new SolicitudFoto(encargado.UsuarioId, c.Caja1.SucursalId, Convert.ToBase64String(ImagenesDePrueba.Foto(800, 600)), "la PC")));
        var renglon = Assert.Single(await BitacoraDeAsync(nombre));
        Assert.Equal($"Puso la foto de «{nombre}» (desde la PC)", renglon.Detalle);
        Assert.Equal((null, encargado.UsuarioId), (renglon.CajaId, renglon.UsuarioId));
    }

    [Fact]
    public async Task Los_formatos_que_se_aceptan_y_cada_error_con_su_codigo()
    {
        var c = await CelularAsync();
        var producto = (await ProductosAsync(c, 8, 1))[0];

        async Task RechazoAsync(HttpResponseMessage r, string codigo, string mensaje)
        {
            var error = await Error(r, HttpStatusCode.BadRequest);
            Assert.Equal((mensaje, codigo), (error.Mensaje, error.Codigo));
        }

        await RechazoAsync(await PonerAsync(c.Http, producto.VarianteId, "{}"), "sin-foto", "No llegó ninguna foto.");
        await RechazoAsync(await PonerAsync(c.Http, producto.VarianteId, """{"imagenBase64": ""}"""), "sin-foto", "No llegó ninguna foto.");
        await RechazoAsync(await PonerAsync(c.Http, producto.VarianteId, """{"imagenBase64": "no-es-base64!"}"""), "imagen",
            "La foto no llegó bien (no es base64): mándala otra vez.");
        await RechazoAsync(await PonerAsync(c.Http, producto.VarianteId, "esto no es una foto"u8.ToArray()), "imagen",
            "No se reconoce la imagen: usa una foto JPG, PNG o WebP.");
        // Cortada a la mitad (se fue la red al leerla del disco, por ejemplo).
        var entera = ImagenesDePrueba.Foto(1600, 1200);
        var cortada = await Error(await PonerAsync(c.Http, producto.VarianteId, entera[..(entera.Length / 2)]), HttpStatusCode.BadRequest);
        Assert.Equal(("La foto llegó incompleta: tómala otra vez.", "imagen"), (cortada.Mensaje, cortada.Codigo));
        // Lo que se lee completo y dice medir de más.
        var enorme = await Error(await PonerAsync(c.Http, producto.VarianteId, ImagenesDePrueba.PngQueDiceMedir(10_000, 9_999)), HttpStatusCode.BadRequest);
        Assert.Equal("imagen", enorme.Codigo);
        Assert.StartsWith("Esta imagen se lee completa y es demasiado grande (10,000 × 9,999): usa una de menos de 16 megapíxeles", enorme.Mensaje);
        // Más de 15 MB (20,971,520 letras de base64): ni se lee.
        await RechazoAsync(await PonerAsync(c.Http, producto.VarianteId, $$"""{"imagenBase64": "{{new string('A', 20_971_524)}}"}"""), "muy-grande",
            "La foto pasa de 15 MB: usa una más chica.");
        // Un producto que ya no está.
        await RechazoAsync(await PonerAsync(c.Http, Guid.NewGuid(), ImagenesDePrueba.Foto(400, 300)), "no-esta", "Ese producto ya no está en el catálogo.");
        // Lo que no es un id de producto no es de estas rutas.
        Assert.Equal(HttpStatusCode.NotFound, (await c.Http.GetAsync("api/fotos/no-es-un-id")).StatusCode);
        await using (var db = servicio.Base())
            Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == producto.VarianteId));

        // Sin foto: 404 al bajarla, con su mensaje; quitarla no hace nada (204).
        var sinFoto = await Error(await c.Http.GetAsync($"api/fotos/{producto.VarianteId}"), HttpStatusCode.NotFound);
        Assert.Equal("Ese producto no tiene foto.", sinFoto.Mensaje);
        Assert.Equal(HttpStatusCode.NoContent, (await QuitarAsync(c.Http, producto.VarianteId)).StatusCode);

        // JPG, PNG y WebP entran; siempre se guarda un JPEG de a lo más 240 por lado. La que ya es chica no se agranda.
        foreach (var formato in new[] { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp })
        {
            var guardada = await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, producto.VarianteId, ImagenesDePrueba.Foto(1280, 960, formato)));
            Assert.Equal((240, 180), (guardada.Ancho, guardada.Alto));
            Assert.InRange(guardada.Bytes, 1, PapeleriaPos.Impresion.FotoDelProducto.BytesObjetivo);
            Assert.Equal([0xFF, 0xD8], Convert.FromBase64String(guardada.JpegBase64)[..2]);
        }
        var chica = await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, producto.VarianteId, ImagenesDePrueba.Foto(120, 90)));
        Assert.Equal((120, 90), (chica.Ancho, chica.Alto));
        // De 960 por lado (lo recomendado para la app) pesa una fracción del megabyte.
        Assert.InRange(ImagenesDePrueba.Foto(960, 720, calidad: 85).Length, 1, 400 * 1024);
    }

    [Fact]
    public async Task Si_dos_ponen_foto_al_mismo_producto_queda_la_ultima_y_las_dos_en_la_bitacora()
    {
        var luis = await CelularAsync("Celular de Luis");
        var ana = await CelularAsync("Celular de Ana");
        var productos = await ProductosAsync(luis, 11, 2);
        var producto = productos[0];
        var nombre = await NombreAsync(producto);

        // Una después de la otra: la segunda reemplaza a la primera (una sola por producto).
        var primera = await Leer<FotoGuardadaDto>(await PonerAsync(luis.Http, producto.VarianteId, ImagenesDePrueba.Foto(1000, 750)));
        var segunda = await Leer<FotoGuardadaDto>(await PonerAsync(ana.Http, producto.VarianteId, ImagenesDePrueba.Mitades(750, 1000)));
        Assert.NotEqual(primera.Huella, segunda.Huella);
        await using (var db = servicio.Base())
            Assert.Equal(segunda.Huella, (await db.FotosProducto.SingleAsync(f => f.VarianteId == producto.VarianteId)).Huella);
        Assert.Equal($"\"{segunda.Huella}\"", (await luis.Http.GetAsync($"api/fotos/{producto.VarianteId}")).Headers.ETag!.Tag);
        var renglones = await BitacoraDeAsync(nombre);
        Assert.Equal(2, renglones.Count);
        Assert.Contains(renglones, b => b.Detalle == $"Puso la foto de «{nombre}» (desde el celular «Celular de Luis»)" && b.CajaId == luis.Caja.Id);
        Assert.Contains(renglones, b => b.Detalle == $"Cambió la foto de «{nombre}» (desde el celular «Celular de Ana»)" && b.CajaId == ana.Caja.Id);
        // La misma foto otra vez (se perdió la respuesta): contesta lo mismo y no cambia nada, ni la bitácora.
        Assert.Equal(segunda.Huella, (await Leer<FotoGuardadaDto>(await PonerAsync(luis.Http, producto.VarianteId, ImagenesDePrueba.Mitades(750, 1000)))).Huella);
        Assert.Equal(2, (await BitacoraDeAsync(nombre)).Count);

        // Al mismo tiempo, a un producto sin foto: cada una se guarda o se le dice que lo intente otra vez; queda una sola, de las
        // que se contestaron bien, y nunca un error sin explicar.
        var otro = productos[1];
        var nombreDelOtro = await NombreAsync(otro);
        var imagenes = Enumerable.Range(0, 6).Select(i => ImagenesDePrueba.Foto(800 + 40 * i, 600)).ToList();
        var respuestas = await Task.WhenAll(imagenes.Select((imagen, i) => PonerAsync(i % 2 == 0 ? luis.Http : ana.Http, otro.VarianteId, imagen)));
        var guardadas = new List<string>();
        foreach (var r in respuestas)
        {
            if (r.StatusCode == HttpStatusCode.OK)
            {
                guardadas.Add((await r.Content.ReadFromJsonAsync<FotoGuardadaDto>())!.Huella);
                continue;
            }
            var error = await Error(r, HttpStatusCode.BadRequest);
            Assert.Equal(($"Alguien más acaba de guardar la foto de «{nombreDelOtro}»: vuelve a intentarlo.", "a-la-vez"), (error.Mensaje, error.Codigo));
        }
        Assert.NotEmpty(guardadas);
        await using (var db = servicio.Base())
            Assert.Contains((await db.FotosProducto.SingleAsync(f => f.VarianteId == otro.VarianteId)).Huella, guardadas);
    }

    /// <summary>
    /// Las fotos se preparan de una en una: si la fila no avanza en lo que se espera (30 s en la tienda), a la que esperaba se le
    /// dice que lo intente en un momento, con su código; la que estaba en turno se guarda.
    /// </summary>
    [Fact]
    public async Task Si_la_fila_de_fotos_no_avanza_se_dice_que_se_intente_en_un_momento()
    {
        var c = await CelularAsync();
        var productos = await ProductosAsync(c, 20, 2);
        var preparacion = servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.PreparacionDeFotos>();
        var enTurno = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        preparacion.AlEmpezar = async () =>
        {
            enTurno.TrySetResult();
            await soltar.Task;
        };
        preparacion.Espera = TimeSpan.FromMilliseconds(200);
        try
        {
            var primera = PonerAsync(c.Http, productos[0].VarianteId, ImagenesDePrueba.Foto(800, 600));
            await enTurno.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var error = await Error(await PonerAsync(c.Http, productos[1].VarianteId, ImagenesDePrueba.Foto(800, 600)), HttpStatusCode.BadRequest);

            Assert.Equal(("La caja principal está preparando otras fotos: vuelve a intentarlo en un momento.", "ocupada"), (error.Mensaje, error.Codigo));
            soltar.TrySetResult();
            await Leer<FotoGuardadaDto>(await primera);
        }
        finally
        {
            soltar.TrySetResult();
            (preparacion.AlEmpezar, preparacion.Espera) = (null, PapeleriaPos.Servicio.PreparacionDeFotos.EsperaMaxima);
        }
        await using var db = servicio.Base();
        Assert.True(await db.FotosProducto.AnyAsync(f => f.VarianteId == productos[0].VarianteId));
        Assert.False(await db.FotosProducto.AnyAsync(f => f.VarianteId == productos[1].VarianteId));
    }

    [Fact]
    public async Task El_celular_la_quita_con_su_llave_y_las_cajas_se_enteran()
    {
        var c = await CelularAsync();
        var producto = (await ProductosAsync(c, 14, 1))[0];
        var nombre = await NombreAsync(producto);
        await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, producto.VarianteId, ImagenesDePrueba.Foto(800, 600)));
        await servicio.CatalogoAlDiaAsync();
        var antes = await PaqueteAsync(c.Http, c.Caja.Id);
        Assert.Contains(antes.Fotos!, f => f.VarianteId == producto.VarianteId);

        var quitada = await QuitarAsync(c.Http, producto.VarianteId);

        Assert.Equal(HttpStatusCode.NoContent, quitada.StatusCode);
        Assert.Empty(await quitada.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Http.GetAsync($"api/fotos/{producto.VarianteId}")).StatusCode);
        var renglon = Assert.Single(await BitacoraDeAsync(nombre), b => b.Detalle.StartsWith("Quitó", StringComparison.Ordinal));
        Assert.Equal($"Quitó la foto de «{nombre}» (desde el celular «Celular de Luis»)", renglon.Detalle);
        Assert.Equal((c.Caja.Id, null), (renglon.CajaId, renglon.UsuarioId));
        await servicio.CatalogoAlDiaAsync();
        var cambios = await Leer<CambiosCopiaDto>(await c.Http.GetAsync(
            $"api/cajas/{c.Caja.Id}/cambios?desde={antes.Version}&existencias={antes.VersionExistencias}&base={antes.Base}&formato={CopiaAlDia.Formato}"
            + $"&promociones={antes.HuellaPromociones}&vendidos={antes.VersionVendidos}&elegidos={antes.VersionElegidos}"));
        Assert.Equal([producto.VarianteId], cambios.FotosQuitadas!);
        Assert.DoesNotContain((await PaqueteAsync(c.Http, c.Caja.Id)).Fotos!, f => f.VarianteId == producto.VarianteId);
    }

    /// <summary>
    /// «Productos sin foto» no pide otra ruta: son los productos de la copia que no vienen en sus <c>fotos</c>. La foto es de la
    /// variante: en la copia va solo con su <c>varianteId</c> y su huella, sin presentación.
    /// </summary>
    [Fact]
    public async Task Los_productos_sin_foto_salen_de_la_copia_y_la_foto_es_de_la_variante()
    {
        var c = await CelularAsync();
        var productos = await ProductosAsync(c, 17, 3);
        await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, productos[0].VarianteId, ImagenesDePrueba.Foto(800, 600)));
        await Leer<FotoGuardadaDto>(await PonerAsync(c.Http, productos[2].VarianteId, ImagenesDePrueba.Foto(600, 800)));
        await servicio.CatalogoAlDiaAsync();

        var respuesta = await c.Http.GetAsync($"api/cajas/{c.Caja.Id}/paquete");
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        var copia = json.RootElement.Deserialize<PaqueteContingenciaDto>(JsonSerializerOptions.Web)!;

        // Cada foto de la copia: de qué variante y su huella, nada más.
        var fotos = json.RootElement.GetProperty("fotos").EnumerateArray().ToList();
        Assert.All(fotos, f => Assert.Equal(["huella", "varianteId"], f.EnumerateObject().Select(p => p.Name).Order()));
        // Los que faltan: los de la copia que no están en sus fotos. Lo mismo que dice la base.
        var conFoto = copia.Fotos!.Select(f => f.VarianteId).ToHashSet();
        var sinFoto = copia.Productos.Select(p => p.VarianteId).Where(v => !conFoto.Contains(v)).ToHashSet();
        Assert.Contains(productos[1].VarianteId, sinFoto);
        Assert.DoesNotContain(productos[0].VarianteId, sinFoto);
        Assert.DoesNotContain(productos[2].VarianteId, sinFoto);
        await using var db = servicio.Base();
        var enLaBase = (await db.FotosProducto.Select(f => f.VarianteId).ToListAsync()).ToHashSet();
        Assert.Equal(copia.Productos.Select(p => p.VarianteId).Where(v => !enLaBase.Contains(v)).Order(), sinFoto.Order());
        // Y la lista sola, sin bajar la copia: las mismas, con la versión del catálogo.
        var todas = await Leer<FotosDto>(await c.Http.GetAsync("api/fotos"));
        Assert.Equal(enLaBase.Order(), todas.Fotos.Select(f => f.VarianteId).Order());
    }
}
