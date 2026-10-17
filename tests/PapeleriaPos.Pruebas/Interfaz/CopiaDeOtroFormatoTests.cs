using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Una caja y una caja principal que arman la copia en formatos distintos (entrega 5, partes 2 y 3): pasa mientras se
/// actualizan, una antes que la otra (la caja 2 antes que la principal, o al revés). La caja no le pide cambios y baja la
/// copia completa solo cuando cambia la versión del catálogo (o cada media hora), como con una caja principal de antes. Antes
/// la bajaba completa con cada latido y con cada aviso: la que llegaba nunca era de su formato (4 latidos, 4 copias de más).
/// Lo que se revisa es qué se le pidió a la red, esperando cada cosa con un límite: no depende de lo que tarda la PC.
/// </summary>
public sealed class CopiaDeOtroFormatoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-formato-{Guid.NewGuid():N}.db");
    private readonly List<Contingencia> _abiertas = [];

    public void Dispose()
    {
        foreach (var contingencia in _abiertas)
            contingencia.Cerrar(TimeSpan.FromSeconds(10));
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    /// <summary>
    /// La caja principal en otro formato: la de la entrega 5, parte 2 (el 1: no lo dice en el latido ni en el aviso, y su copia
    /// no trae lo de la búsqueda), la de la parte 3 (el 2: sin los exhibidores ni las temporadas de la entrega 13), la de la
    /// entrega 13, parte 1 (el 3: sin las fotos de la parte 2) o una más nueva que esta caja (el 5, que sí lo dice).
    /// </summary>
    private sealed class CajaPrincipalDeOtroFormato(int formato, HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            var respuesta = await base.SendAsync(request, cancellationToken);
            if (ruta == "/api/estado")
                await ReescribirAsync(respuesta, json => DecirFormato(json, "formatoCopia"), cancellationToken);
            else if (ruta.EndsWith("/paquete", StringComparison.Ordinal))
            {
                await ReescribirAsync(respuesta, json =>
                {
                    json["formato"] = formato;
                    if (formato == 1)
                    {
                        foreach (var nuevo in new[] { "vendidos", "elegidos", "versionVendidos", "versionElegidos" })
                            json.Remove(nuevo);
                    }
                    if (formato < 3)
                        json.Remove("exhibidores");
                    if (formato < 4)
                        json.Remove("fotos");
                }, cancellationToken);
            }
            else if (ruta.EndsWith("/avisos", StringComparison.Ordinal) && respuesta.IsSuccessStatusCode)
                ReescribirAviso(respuesta, linea => linea.StartsWith("data: ", StringComparison.Ordinal)
                    ? "data: " + Cambiado(linea["data: ".Length..], json => DecirFormato(json, "formato"))
                    : linea);
            return respuesta;
        }

        /// <summary>La de la parte 2 no lo dice; una más nueva dice el suyo.</summary>
        private void DecirFormato(JsonObject json, string campo)
        {
            if (formato == 1)
                json.Remove(campo);
            else
                json[campo] = formato;
        }

        private static string Cambiado(string texto, Action<JsonObject> cambiar)
        {
            var json = JsonNode.Parse(texto)!.AsObject();
            cambiar(json);
            return json.ToJsonString();
        }

        private static async Task ReescribirAsync(HttpResponseMessage respuesta, Action<JsonObject> cambiar, CancellationToken ct)
        {
            if (!respuesta.IsSuccessStatusCode)
                return;
            var texto = Cambiado(await respuesta.Content.ReadAsStringAsync(ct), cambiar);
            respuesta.Content = new StringContent(texto, Encoding.UTF8, "application/json");
        }

        /// <summary>El aviso (un flujo que no termina), renglón por renglón, con cada uno como se pida.</summary>
        private static void ReescribirAviso(HttpResponseMessage respuesta, Func<string, string> renglon)
        {
            var original = respuesta.Content;
            var tubo = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var lector = new StreamReader(await original.ReadAsStreamAsync(), Encoding.UTF8);
                    await using var escritor = tubo.Writer.AsStream();
                    while (await lector.ReadLineAsync() is { } linea)
                    {
                        await escritor.WriteAsync(Encoding.UTF8.GetBytes(renglon(linea) + "\n"));
                        await escritor.FlushAsync();
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // La caja dejó de escuchar (se cerró): el aviso de la caja principal se suelta.
                }
                finally
                {
                    original.Dispose();
                }
            });
            var rehecho = new StreamContent(tubo.Reader.AsStream());
            foreach (var encabezado in original.Headers)
                rehecho.Headers.TryAddWithoutValidation(encabezado.Key, encabezado.Value);
            respuesta.Content = rehecho;
        }
    }

    private sealed record Caja(Contingencia Contingencia, RedDePrueba Red, CajaDto Datos, HttpClient Directo);

    private async Task<Caja> AbrirAsync(int formato)
    {
        var red = new RedDePrueba(new CajaPrincipalDeOtroFormato(formato, servicio.Fabrica.Server.CreateHandler()));
        var directo = servicio.Fabrica.CreateClient();
        var caja = (await directo.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red),
            new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
        _abiertas.Add(contingencia);
        await contingencia.LatidoAsync();
        return new Caja(contingencia, red, caja, directo);
    }

    private static string Paquete(Caja c) => $"/api/cajas/{c.Datos.Id}/paquete";

    private static async Task<ProductoVentaDto> CambiarUnPrecioAsync(Caja c, int cual)
    {
        var producto = c.Contingencia.Paquete!.Productos
            .Where(p => p is { Presentacion.EsBase: true, Codigo: not null } && p.Precios.Count == 1 && p.Precios[0].PresentacionId is null
                        && c.Contingencia.Paquete.Codigos.Any(x => x.Codigo == p.Codigo && x.VarianteId == p.VarianteId))
            .OrderBy(p => p.VarianteId).ElementAt(cual);
        var dueno = (await (await c.Directo.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var nuevo = producto.Precios[0].PrecioCentavos + 130;
        Assert.True((await c.Directo.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(dueno.UsuarioId, c.Datos.SucursalId, producto.VarianteId, nuevo))).IsSuccessStatusCode);
        return producto with { Precios = [producto.Precios[0] with { PrecioCentavos = nuevo }] };
    }

    /// <summary>
    /// Con una caja principal de antes (los formatos 1, 2 y 3) o una más nueva (el 5): la copia completa baja una vez, y los
    /// latidos sin cambios ya no la vuelven a bajar ni piden cambios. Cuando cambia un precio (sube la versión del catálogo), la
    /// baja otra vez, una sola, con el precio nuevo.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Con_otro_formato_la_copia_completa_baja_solo_cuando_cambia_el_catalogo(int formato)
    {
        var c = await AbrirAsync(formato);
        Assert.Equal(1, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(formato, c.Contingencia.Paquete!.Formato);

        for (var i = 0; i < 4; i++)
            await c.Contingencia.LatidoAsync();
        Assert.Equal(1, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(0, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/cambios"));

        var producto = await CambiarUnPrecioAsync(c, formato);
        await c.Contingencia.LatidoAsync();
        await c.Contingencia.LatidoAsync();
        Assert.Equal(2, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(producto.Precios[0].PrecioCentavos, c.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
        Assert.Equal(0, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/cambios"));
    }

    /// <summary>
    /// Con el aviso, igual: cada venta (las existencias) es un aviso, y ninguno baja la copia completa con otro formato. Antes
    /// cada aviso la bajaba entera.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Con_otro_formato_los_avisos_no_bajan_la_copia_completa(int formato)
    {
        var c = await AbrirAsync(formato);
        c.Contingencia.IniciarAvisos();
        await VentaConCopia.EsperarAsync(() => c.Contingencia.VersionesDeLaPrincipal is not null, "las versiones del aviso");
        Assert.Equal(formato == 1 ? null : formato, c.Contingencia.VersionesDeLaPrincipal!.Formato);
        var cajero = (await (await c.Directo.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var abierto = await c.Directo.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        var turno = abierto.StatusCode == System.Net.HttpStatusCode.NoContent
            ? await (await c.Directo.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, cajero.UsuarioId, 50000))).Content.ReadFromJsonAsync<TurnoDto>()
            : await abierto.Content.ReadFromJsonAsync<TurnoDto>();
        var producto = c.Contingencia.Paquete!.Productos.Where(p => p is { LlevaInventario: true, Presentacion.EsBase: true }).OrderBy(p => p.VarianteId)
            .ElementAt(10 + formato);

        for (var venta = 0; venta < 2; venta++)
        {
            var antes = c.Contingencia.VersionesDeLaPrincipal!.Existencias;
            Assert.True((await c.Directo.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno!.Id, cajero.UsuarioId,
                [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]))).IsSuccessStatusCode);
            await VentaConCopia.EsperarAsync(() => c.Contingencia.VersionesDeLaPrincipal!.Existencias > antes, "el aviso de la venta");
            // Más vueltas con lo que dijo el aviso (cada una espera a la que iba, o la deja para después de ella).
            for (var vuelta = 0; vuelta < 3; vuelta++)
                await c.Contingencia.PonerAlDiaAsync();
        }

        Assert.Equal(1, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(0, c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/cambios"));
    }

    /// <summary>
    /// Con la caja principal del mismo formato, una copia guardada en el de antes (la que dejó una caja de la parte 2) escanea
    /// al abrir, sin búsqueda propia, y se cambia por una completa una sola vez; los latidos siguientes ya no la bajan.
    /// </summary>
    [Fact]
    public async Task Una_copia_guardada_en_el_formato_de_antes_se_cambia_una_sola_vez()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var primera = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(new RedDePrueba(servicio.Fabrica.Server.CreateHandler())), conexion);
        await primera.LatidoAsync();
        var paquete = primera.Paquete!;
        primera.Cerrar(TimeSpan.FromSeconds(10));
        var vieja = paquete with { Formato = 1, Vendidos = null, Elegidos = null, VersionVendidos = null, VersionElegidos = null };
        await new AlmacenLocal(_baseLocal).GuardarCopiaAsync(JsonSerializer.SerializeToUtf8Bytes(vieja, AlmacenLocal.Json));

        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var otra = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
        _abiertas.Add(otra);
        await otra.CopiaLeida;
        var codigo = paquete.Codigos.First(x => paquete.Productos.Any(p => p.VarianteId == x.VarianteId)).Codigo;
        Assert.NotNull(otra.Foto!.PorCodigo(codigo));
        Assert.False(otra.Foto.SeBusca);

        await otra.LatidoAsync();
        await otra.LatidoAsync();
        await otra.LatidoAsync();

        Assert.Equal(1, red.Cuantas($"/api/cajas/{caja.Id}/paquete"));
        Assert.Equal(CopiaAlDia.Formato, otra.Paquete!.Formato);
        Assert.True(otra.Foto!.SeBusca);
        // La caja principal dice su formato en el latido.
        Assert.Equal(CopiaAlDia.Formato, (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.FormatoCopia);
    }

    /// <summary>
    /// Una copia del formato de ahora (el 3) sin los exhibidores la guardó una versión de esta caja que todavía no los conocía,
    /// con una caja principal ya actualizada (los cambios solo los traen cuando cambian): se baja completa una sola vez, y
    /// desde ahí Ctrl+M y el chip de la temporada los tienen (entrega 13).
    /// </summary>
    [Fact]
    public async Task Una_copia_de_este_formato_sin_los_exhibidores_se_cambia_una_sola_vez()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var primera = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(new RedDePrueba(servicio.Fabrica.Server.CreateHandler())), conexion);
        await primera.LatidoAsync();
        var paquete = primera.Paquete!;
        primera.Cerrar(TimeSpan.FromSeconds(10));
        Assert.NotNull(paquete.Exhibidores);
        await new AlmacenLocal(_baseLocal).GuardarCopiaAsync(JsonSerializer.SerializeToUtf8Bytes(paquete with { Exhibidores = null }, AlmacenLocal.Json));

        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var otra = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id });
        _abiertas.Add(otra);
        var cliente = new ClienteCaja(new HttpClient(red), conexion, otra);
        await otra.CopiaLeida;
        Assert.Null(cliente.ExhibidoresDeLaCopia());

        await otra.LatidoAsync();
        await otra.LatidoAsync();
        await otra.LatidoAsync();

        Assert.Equal(1, red.Cuantas($"/api/cajas/{caja.Id}/paquete"));
        Assert.Equal(paquete.Exhibidores!.Select(e => e.Nombre), cliente.ExhibidoresDeLaCopia()!.Select(e => e.Nombre));
    }

    /// <summary>
    /// Una copia del formato de ahora (el 4) sin las fotos la guardó una versión de esta caja que todavía no las conocía (los
    /// cambios solo las traen cuando cambian): se baja completa una sola vez, y desde ahí la caja sabe qué fotos hay (entrega 13,
    /// parte 2).
    /// </summary>
    [Fact]
    public async Task Una_copia_de_este_formato_sin_las_fotos_se_cambia_una_sola_vez()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var primera = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(new RedDePrueba(servicio.Fabrica.Server.CreateHandler())), conexion);
        await primera.LatidoAsync();
        var paquete = primera.Paquete!;
        primera.Cerrar(TimeSpan.FromSeconds(10));
        Assert.NotNull(paquete.Fotos);
        await new AlmacenLocal(_baseLocal).GuardarCopiaAsync(JsonSerializer.SerializeToUtf8Bytes(paquete with { Fotos = null }, AlmacenLocal.Json));

        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var otra = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), conexion);
        _abiertas.Add(otra);
        await otra.CopiaLeida;
        Assert.Null(otra.Paquete!.Fotos);

        await otra.LatidoAsync();
        await otra.LatidoAsync();
        await otra.LatidoAsync();

        Assert.Equal(1, red.Cuantas($"/api/cajas/{caja.Id}/paquete"));
        Assert.NotNull(otra.Paquete!.Fotos);
    }
}
