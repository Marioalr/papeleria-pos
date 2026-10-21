using System.Net.Http.Json;
using System.Text.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las fotos de los productos en la caja (entrega 13, parte 2): la caja de verdad (contingencia y base local) contra el
/// servicio de verdad. La copia solo trae de qué producto es cada foto y su huella; la caja baja aparte las que le faltan, una
/// sola vez, las guarda en su base y las enseña de ahí, también sin conexión. Una foto que cambia o se quita llega con lo que
/// cambió, sin bajar la copia completa. Lo que se revisa es qué se le pidió a la red y qué quedó en la caja, esperando cada
/// cosa con un límite: no depende de lo que tarda la PC.
/// </summary>
public sealed class FotosEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-fotos-{Guid.NewGuid():N}.db");

    /// <summary>Las cajas que abrió la prueba: al terminar se cierran (y sueltan su base) antes de borrarla.</summary>
    private readonly List<Contingencia> _abiertas = [];

    public void Dispose()
    {
        foreach (var contingencia in _abiertas)
            contingencia.Cerrar(TimeSpan.FromSeconds(10));
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private sealed record Caja(Contingencia Contingencia, RedDePrueba Red, CajaDto Datos);

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Encargado)> DirectoAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var encargado = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))).Content.ReadFromJsonAsync<SesionDto>())!;
        return (http, caja, encargado);
    }

    /// <summary>Productos de la copia, en un orden fijo, distintos en cada prueba (comparten la base del servicio).</summary>
    private async Task<List<Guid>> ProductosAsync(int desde, int cuantos)
    {
        var (http, caja, _) = await DirectoAsync();
        var copia = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        return copia.Productos.Select(p => p.VarianteId).Order().Skip(desde).Take(cuantos).ToList();
    }

    private async Task<FotoGuardadaDto> FotoAsync(Guid varianteId, byte[] imagen)
    {
        var (http, caja, encargado) = await DirectoAsync();
        var respuesta = await http.PutAsJsonAsync($"api/fotos/{varianteId}", new SolicitudFoto(encargado.UsuarioId, caja.SucursalId, Convert.ToBase64String(imagen)));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<FotoGuardadaDto>())!;
    }

    private async Task QuitarAsync(Guid varianteId)
    {
        var (http, caja, encargado) = await DirectoAsync();
        Assert.True((await http.PostAsJsonAsync($"api/fotos/{varianteId}/quitar", new SolicitudQuitarFoto(encargado.UsuarioId, caja.SucursalId))).IsSuccessStatusCode);
    }

    private async Task<Caja> AbrirAsync(RedDePrueba? red = null)
    {
        red ??= new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
        _abiertas.Add(contingencia);
        return new Caja(contingencia, red, caja);
    }

    private static string Foto(Guid varianteId) => $"/api/fotos/{varianteId}";

    /// <summary>Espera a que la caja tenga esa foto (con esa huella) o, sin huella, a que no tenga ninguna de ese producto.</summary>
    private static Task EsperarAsync(Caja c, Guid varianteId, string? huella) =>
        VentaConCopia.EsperarAsync(() => c.Contingencia.Fotos.Direccion(varianteId) == (huella is null ? null : FotoProducto.Archivo(varianteId, huella)),
            huella is null ? "que se quite la foto" : "la foto en la caja");

    [Fact]
    public async Task Al_abrir_la_caja_baja_las_fotos_aparte_una_sola_vez_y_las_guarda_en_su_base()
    {
        var productos = await ProductosAsync(0, 3);
        var fotos = new List<FotoGuardadaDto>();
        foreach (var (producto, i) in productos.Select((p, i) => (p, i)))
            fotos.Add(await FotoAsync(producto, ImagenesDePrueba.Foto(1200 + 100 * i, 900)));
        var c = await AbrirAsync();

        await c.Contingencia.LatidoAsync();
        foreach (var foto in fotos)
            await EsperarAsync(c, foto.VarianteId, foto.Huella);

        // La copia trae de qué producto es cada una y su huella; la foto, no: cada una se bajó aparte, una vez.
        Assert.Equal(1, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/paquete"));
        Assert.All(fotos, f => Assert.Contains(new FotoDeProductoDto(f.VarianteId, f.Huella), c.Contingencia.Paquete!.Fotos!));
        Assert.All(fotos, f => Assert.Equal(1, c.Red.Cuantas(Foto(f.VarianteId))));
        var copia = JsonSerializer.Serialize(c.Contingencia.Paquete, AlmacenLocal.Json);
        Assert.All(fotos, f => Assert.DoesNotContain(f.JpegBase64[..40], copia, StringComparison.Ordinal));
        // La pantalla la pide por su archivo, y la caja se la da de lo que bajó: los mismos bytes que guardó el servicio.
        Assert.All(fotos, f => Assert.Equal(Convert.FromBase64String(f.JpegBase64), c.Contingencia.Fotos.Archivo(FotoProducto.Archivo(f.VarianteId, f.Huella))));
        Assert.Null(c.Contingencia.Fotos.Archivo("_framework/blazor.webview.js"));
        // Y quedaron en su base (caja-local.db).
        var enLaBase = new AlmacenLocal(_baseLocal).LeerFotos();
        Assert.All(fotos, f => Assert.Contains(enLaBase, g => g.VarianteId == f.VarianteId && g.Huella == f.Huella));
        Assert.Equal(0, c.Contingencia.Fotos.Faltan);

        // Más latidos no vuelven a bajar ninguna.
        await c.Contingencia.LatidoAsync();
        await c.Contingencia.LatidoAsync();
        Assert.All(fotos, f => Assert.Equal(1, c.Red.Cuantas(Foto(f.VarianteId))));
        // Un producto sin foto: sus iniciales (ninguna dirección).
        var sinFoto = (await ProductosAsync(40, 1))[0];
        Assert.Null(c.Contingencia.Fotos.Direccion(sinFoto));
    }

    [Fact]
    public async Task Una_foto_que_cambia_o_se_quita_llega_con_lo_que_cambio_sin_bajar_la_copia_completa()
    {
        var productos = await ProductosAsync(5, 3);
        var (cambia, seQuita, nueva) = (productos[0], productos[1], productos[2]);
        var antes = await FotoAsync(cambia, ImagenesDePrueba.Foto(1000, 800));
        var quitada = await FotoAsync(seQuita, ImagenesDePrueba.Foto(800, 1000));
        var c = await AbrirAsync();
        await c.Contingencia.LatidoAsync();
        await EsperarAsync(c, cambia, antes.Huella);
        await EsperarAsync(c, seQuita, quitada.Huella);
        var copias = c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/paquete");

        var despues = await FotoAsync(cambia, ImagenesDePrueba.Mitades(1000, 800));
        await QuitarAsync(seQuita);
        var otra = await FotoAsync(nueva, ImagenesDePrueba.Foto(700, 700));
        await servicio.CatalogoAlDiaAsync();
        await c.Contingencia.LatidoAsync();
        await EsperarAsync(c, cambia, despues.Huella);
        await EsperarAsync(c, seQuita, null);
        await EsperarAsync(c, nueva, otra.Huella);

        // Con lo que cambió, sin la copia completa: la cambiada y la nueva se bajaron, la quitada no se volvió a pedir.
        Assert.Equal(copias, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/paquete"));
        Assert.True(c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/cambios") >= 1);
        Assert.Equal(2, c.Red.Cuantas(Foto(cambia)));
        Assert.Equal(1, c.Red.Cuantas(Foto(seQuita)));
        Assert.Equal(1, c.Red.Cuantas(Foto(nueva)));
        Assert.Equal(Convert.FromBase64String(despues.JpegBase64), c.Contingencia.Fotos.Datos(cambia));
        // En su base, la de ahora; la quitada ya no está.
        await VentaConCopia.EsperarAsync(() => new AlmacenLocal(_baseLocal).LeerFotos() is var g
                && g.Any(f => f.VarianteId == cambia && f.Huella == despues.Huella) && g.All(f => f.VarianteId != seQuita),
            "la base de la caja al día");
    }

    [Fact]
    public async Task Con_el_aviso_la_foto_nueva_llega_sola()
    {
        var producto = (await ProductosAsync(10, 1))[0];
        var c = await AbrirAsync();
        await c.Contingencia.LatidoAsync();
        c.Contingencia.IniciarAvisos();
        await VentaConCopia.EsperarAsync(() => c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/avisos") > 0, "la conexión del aviso");
        var latidos = c.Red.Cuantas("/api/estado");

        var foto = await FotoAsync(producto, ImagenesDePrueba.Foto(900, 900));

        await EsperarAsync(c, producto, foto.Huella);
        Assert.Equal(latidos, c.Red.Cuantas("/api/estado"));
        Assert.Equal(1, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/paquete"));
    }

    [Fact]
    public async Task Sin_conexion_enseña_las_fotos_de_su_base()
    {
        var producto = (await ProductosAsync(15, 1))[0];
        var foto = await FotoAsync(producto, ImagenesDePrueba.Foto(1000, 750));
        var primera = await AbrirAsync();
        await primera.Contingencia.LatidoAsync();
        await EsperarAsync(primera, producto, foto.Huella);
        primera.Contingencia.Cerrar(TimeSpan.FromSeconds(10));

        // Otra vez, sin la caja principal: la copia y las fotos, de su base.
        var c = await AbrirAsync(new RedDePrueba(servicio.Fabrica.Server.CreateHandler()) { Caida = true });
        await c.Contingencia.CopiaLeida;
        await c.Contingencia.Fotos.Leidas;
        await c.Contingencia.LatidoAsync();

        Assert.Equal(FotoProducto.Archivo(producto, foto.Huella), c.Contingencia.Fotos.Direccion(producto));
        Assert.Equal(Convert.FromBase64String(foto.JpegBase64), c.Contingencia.Fotos.Archivo(FotoProducto.Archivo(producto, foto.Huella)));
        Assert.Equal(0, c.Contingencia.Fotos.Faltan);
        Assert.Equal(0, c.Red.Cuantas(Foto(producto)));
    }

    [Fact]
    public async Task Sin_base_local_la_caja_del_navegador_enseña_las_del_servicio()
    {
        var producto = (await ProductosAsync(20, 1))[0];
        var foto = await FotoAsync(producto, ImagenesDePrueba.Foto(640, 480));
        var (http, _, _) = await DirectoAsync();
        var cliente = new ClienteCaja(servicio.Fabrica.CreateClient(), new ConexionCaja { Servicio = new Uri("http://localhost/") });
        var cambios = 0;
        cliente.Fotos.Cambio += () => Interlocked.Increment(ref cambios);

        // La primera vez pregunta cuáles hay; mientras, las iniciales.
        _ = cliente.Fotos.Direccion(producto);
        await VentaConCopia.EsperarAsync(() => cliente.Fotos.Direccion(producto) is not null, "las fotos del servicio");

        var direccion = cliente.Fotos.Direccion(producto)!;
        Assert.Equal(FotoProducto.Archivo(producto, foto.Huella), direccion);
        // El aviso sale justo después de guardarlas: se espera, porque la dirección puede verse un momento antes.
        await VentaConCopia.EsperarAsync(() => Volatile.Read(ref cambios) >= 1, "el aviso de que llegaron las fotos");
        // La página del navegador la pide al servicio de su misma PC.
        Assert.Equal(Convert.FromBase64String(foto.JpegBase64), await http.GetByteArrayAsync(direccion));
    }
}
