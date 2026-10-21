using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La copia de la caja al día con solo lo que cambió (entrega 5, parte 2): la caja de verdad (contingencia y base local)
/// contra el servicio de verdad. Lo que se revisa no depende de lo que tarda la PC: qué se le pidió a la red (cuántas veces
/// la copia completa, cuántas los cambios) y qué quedó en la foto del catálogo, esperando cada cosa con un límite. Donde hace
/// falta que la caja principal conteste, se dan latidos hasta que contesta (<see cref="PermisosSinConexionTests.ConConexionAsync"/>):
/// en una máquina ocupada un latido puede pasar de su plazo, y con él la caja se queda con la copia de antes (pasó una vez
/// en el CI de Windows: la existencia seguía en 44 y no en 41). Un latido que no llegó no pide cambios ni la copia completa.
/// </summary>
public sealed class CopiaAlDiaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-al-dia-{Guid.NewGuid():N}.db");

    /// <summary>Las cajas que abrió la prueba: al terminar se detienen y se espera a su escritor antes de borrar su base.</summary>
    private readonly List<Contingencia> _abiertas = [];

    public void Dispose()
    {
        foreach (var contingencia in _abiertas)
            contingencia.Cerrar(TimeSpan.FromSeconds(10));
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private sealed record Caja(Contingencia Contingencia, RedDePrueba Red, CajaDto Datos, HttpClient Directo, SesionDto Dueno, SesionDto Cajero);

    /// <summary>
    /// Un servicio de antes: su latido no trae la base ni la versión de las existencias, y no tiene los cambios ni el aviso.
    /// </summary>
    private sealed class ServicioDeAntes(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            if (ruta.EndsWith("/cambios", StringComparison.Ordinal) || ruta.EndsWith("/avisos", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var respuesta = await base.SendAsync(request, cancellationToken);
            if (ruta != "/api/estado" && !ruta.EndsWith("/paquete", StringComparison.Ordinal))
                return respuesta;
            var json = JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            foreach (var nuevo in new[] { "base", "versionExistencias", "formato" })
                json.Remove(nuevo);
            respuesta.Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return respuesta;
        }
    }

    /// <summary>Los cambios contestan con un error del servicio.</summary>
    private sealed class CambiosConError(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/cambios", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : base.SendAsync(request, cancellationToken);
    }

    private async Task<Caja> AbrirAsync(Func<HttpMessageHandler, HttpMessageHandler>? enMedio = null)
    {
        var red = new RedDePrueba(enMedio is null ? servicio.Fabrica.Server.CreateHandler() : enMedio(servicio.Fabrica.Server.CreateHandler()));
        var directo = servicio.Fabrica.CreateClient();
        var caja = (await directo.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), conexion);
        _abiertas.Add(contingencia);
        await PermisosSinConexionTests.ConConexionAsync(contingencia);
        Assert.NotNull(contingencia.Foto);
        async Task<SesionDto> Entrar(string usuario, string pin) =>
            (await (await directo.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        return new Caja(contingencia, red, caja, directo, await Entrar("dueno", "3333"), await Entrar("cajero", "1111"));
    }

    private static ProductoVentaDto UnoSinMayoreo(Caja c, int cual) =>
        c.Contingencia.Paquete!.Productos
            .Where(p => p is { Unidad: UnidadBase.Pieza, LlevaInventario: true, Presentacion.EsBase: true, Codigo: not null } && p.Precios.Count == 1
                        && p.Precios[0].PresentacionId is null && c.Contingencia.Paquete.Codigos.Any(x => x.Codigo == p.Codigo && x.VarianteId == p.VarianteId))
            .OrderBy(p => p.VarianteId).ElementAt(cual);

    private static async Task CambiarPrecioAsync(Caja c, ProductoVentaDto producto, long centavos) =>
        Assert.True((await c.Directo.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(c.Dueno.UsuarioId, c.Datos.SucursalId, producto.VarianteId, centavos))).IsSuccessStatusCode);

    private string Paquete(Caja c) => $"/api/cajas/{c.Datos.Id}/paquete";

    private string Cambios(Caja c) => $"/api/cajas/{c.Datos.Id}/cambios";

    [Fact]
    public async Task Un_precio_nuevo_llega_con_el_latido_sin_bajar_la_copia_completa()
    {
        var c = await AbrirAsync();
        var producto = UnoSinMayoreo(c, 0);
        var (copias, antes) = (c.Red.Cuantas(Paquete(c)), c.Contingencia.Foto!);
        var nuevo = producto.Precios[0].PrecioCentavos + 350;

        await CambiarPrecioAsync(c, producto, nuevo);
        await PermisosSinConexionTests.ConConexionAsync(c.Contingencia);

        var despues = c.Contingencia.Foto!;
        Assert.Equal(copias, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(1, c.Red.Cuantas(Cambios(c)));
        Assert.True(despues.Version > antes.Version);
        Assert.Equal(nuevo, despues.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
        // La de antes no cambió: quien la tenía no ve un cambio a medias.
        Assert.Equal(producto.Precios[0].PrecioCentavos, antes.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
        // Y la copia guardada en la base de la caja ya lo trae: al abrir otra vez, no hace falta bajarla.
        var guardada = new AlmacenLocal(_baseLocal).LeerCopia<PaqueteContingenciaDto>()!;
        Assert.Equal(despues.Version, guardada.Version);
        Assert.Equal(nuevo, guardada.Productos.Single(p => p.VarianteId == producto.VarianteId).Precios.Single().PrecioCentavos);
    }

    /// <summary>
    /// Una venta (de otra caja) no sube la versión del catálogo, pero sí la de las existencias: «Quedan…» al escanear se pone
    /// al día con el siguiente latido (antes, hasta media hora después, al bajar la copia completa otra vez).
    /// </summary>
    [Fact]
    public async Task Una_venta_de_otra_caja_pone_al_dia_la_existencia_al_escanear()
    {
        var c = await AbrirAsync();
        var producto = UnoSinMayoreo(c, 1);
        var (copias, version) = (c.Red.Cuantas(Paquete(c)), c.Contingencia.Foto!.Version);
        var abierto = await c.Directo.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        var turno = abierto.StatusCode == HttpStatusCode.NoContent
            ? await (await c.Directo.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Cajero.UsuarioId, 50000))).Content.ReadFromJsonAsync<TurnoDto>()
            : await abierto.Content.ReadFromJsonAsync<TurnoDto>();
        var venta = await c.Directo.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno!.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 3000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]));
        Assert.True(venta.IsSuccessStatusCode, await venta.Content.ReadAsStringAsync());

        await PermisosSinConexionTests.ConConexionAsync(c.Contingencia);

        Assert.Equal(producto.Existencia - 3, c.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Existencia);
        Assert.Equal(version, c.Contingencia.Foto.Version);
        Assert.Equal(copias, c.Red.Cuantas(Paquete(c)));
    }

    /// <summary>
    /// Con el aviso por SSE la copia se pone al día sola, sin esperar el latido: entre el cambio y la foto nueva no hay ningún
    /// latido (api/estado) ni copia completa.
    /// </summary>
    [Fact]
    public async Task Con_el_aviso_se_pone_al_dia_sin_esperar_el_latido()
    {
        var c = await AbrirAsync();
        c.Contingencia.IniciarAvisos();
        try
        {
            await VentaConCopia.EsperarAsync(() => c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/avisos") > 0, "la conexión del aviso");
            var producto = UnoSinMayoreo(c, 2);
            var (latidos, copias) = (c.Red.Cuantas("/api/estado"), c.Red.Cuantas(Paquete(c)));
            var nuevo = producto.Precios[0].PrecioCentavos + 120;

            await CambiarPrecioAsync(c, producto, nuevo);
            await VentaConCopia.EsperarAsync(() => c.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos == nuevo,
                "el precio nuevo por el aviso");
            // La foto cambia antes de que la copia quede en la base de la caja: también eso, antes de terminar.
            var version = c.Contingencia.Foto!.Version;
            await VentaConCopia.EsperarAsync(() => new AlmacenLocal(_baseLocal).LeerCopia<PaqueteContingenciaDto>()?.Version >= version,
                "la copia guardada en la base de la caja");

            Assert.Equal(latidos, c.Red.Cuantas("/api/estado"));
            Assert.Equal(copias, c.Red.Cuantas(Paquete(c)));
            Assert.True(c.Red.Cuantas(Cambios(c)) >= 1);
        }
        finally
        {
            c.Contingencia.Cerrar(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// La búsqueda por nombre de la caja va contra su copia (entrega 5, parte 3). Un producto que se renombra en la caja
    /// principal y un sinónimo nuevo se encuentran en la caja en cuanto llega el aviso, con lo que cambió: sin bajar la copia
    /// completa ni esperar el latido, y en el mismo orden que la búsqueda de la caja principal. Una venta después (solo
    /// existencias) no arma otra vez el índice de la búsqueda, y la lista ya trae la existencia de ahora.
    /// </summary>
    [Fact]
    public async Task Lo_renombrado_y_un_sinonimo_nuevo_se_encuentran_al_llegar_el_aviso()
    {
        var c = await AbrirAsync();
        c.Contingencia.IniciarAvisos();
        try
        {
            await VentaConCopia.EsperarAsync(() => c.Red.Cuantas($"/api/cajas/{c.Datos.Id}/avisos") > 0, "la conexión del aviso");
            var producto = UnoSinMayoreo(c, 6);
            var (latidos, copias) = (c.Red.Cuantas("/api/estado"), c.Red.Cuantas(Paquete(c)));
            Assert.True(c.Contingencia.Foto!.SeBusca);
            Assert.Empty(c.Contingencia.Foto.Buscar("zanfona"));
            Assert.Empty(c.Contingencia.Foto.Buscar("ocarina"));

            // En la caja principal: el producto se renombra y la tienda da de alta un sinónimo para el nombre nuevo.
            await using (var db = servicio.Base())
            {
                (await db.Variantes.SingleAsync(v => v.Id == producto.VarianteId)).Descripcion = "Zanfona";
                await db.SaveChangesAsync();
            }
            Assert.True((await c.Directo.PostAsJsonAsync("api/catalogo/sinonimos",
                new SolicitudSinonimo(c.Dueno.UsuarioId, c.Datos.SucursalId, "ocarina", "zanfona", false))).IsSuccessStatusCode);

            await VentaConCopia.EsperarAsync(() => c.Contingencia.Foto!.Buscar("ocarina").Any(r => r.VarianteId == producto.VarianteId),
                "el producto renombrado y su sinónimo en la búsqueda de la caja");
            var foto = c.Contingencia.Foto!;
            var porNombre = foto.Buscar("zanfona");
            var porSinonimo = foto.Buscar("ocarina");
            Assert.Equal(producto.VarianteId, porNombre[0].VarianteId);
            Assert.Contains("Zanfona", porNombre[0].Descripcion, StringComparison.Ordinal);
            Assert.Equal("ocarina", porSinonimo.Single(r => r.VarianteId == producto.VarianteId).PorSinonimo);
            // Como lo busca la caja principal.
            await servicio.CatalogoAlDiaAsync();
            foreach (var texto in new[] { "zanfona", "ocarina" })
            {
                var enLaPrincipal = (await c.Directo.GetFromJsonAsync<List<ResultadoBusquedaDto>>(
                    $"api/productos/buscar?q={texto}&sucursalId={c.Datos.SucursalId}"))!;
                Assert.Equal(enLaPrincipal.Select(r => (r.VarianteId, r.PorSinonimo)), foto.Buscar(texto, conLoElegido: false).Select(r => (r.VarianteId, r.PorSinonimo)));
            }
            Assert.Equal(latidos, c.Red.Cuantas("/api/estado"));
            Assert.Equal(copias, c.Red.Cuantas(Paquete(c)));
            Assert.True(c.Red.Cuantas(Cambios(c)) >= 1);

            // Una venta (de otra caja): solo existencias. La lista de la búsqueda ya trae la de ahora.
            var abierto = await c.Directo.GetAsync($"api/cajas/{c.Datos.Id}/turno");
            var turno = abierto.StatusCode == HttpStatusCode.NoContent
                ? await (await c.Directo.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Cajero.UsuarioId, 50000))).Content.ReadFromJsonAsync<TurnoDto>()
                : await abierto.Content.ReadFromJsonAsync<TurnoDto>();
            var existencia = porNombre[0].Existencia;
            Assert.True((await c.Directo.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno!.Id, c.Cajero.UsuarioId,
                [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)]))).IsSuccessStatusCode);
            await VentaConCopia.EsperarAsync(() => c.Contingencia.Foto!.Buscar("zanfona")[0].Existencia == existencia - 1, "la existencia de ahora en la búsqueda");
            Assert.True(c.Contingencia.Foto!.MismaBusquedaQue(foto));
            Assert.Equal(copias, c.Red.Cuantas(Paquete(c)));
        }
        finally
        {
            c.Contingencia.Cerrar(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Con una caja principal de antes (sin cambios ni aviso), la caja hace lo de siempre: baja la copia completa.</summary>
    [Fact]
    public async Task Con_una_caja_principal_de_antes_baja_la_copia_completa()
    {
        var c = await AbrirAsync(interno => new ServicioDeAntes(interno));
        var producto = UnoSinMayoreo(c, 3);
        var copias = c.Red.Cuantas(Paquete(c));
        var nuevo = producto.Precios[0].PrecioCentavos + 70;

        await CambiarPrecioAsync(c, producto, nuevo);
        await PermisosSinConexionTests.ConConexionAsync(c.Contingencia);

        Assert.Equal(copias + 1, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(0, c.Red.Cuantas(Cambios(c)));
        Assert.Equal(nuevo, c.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
        Assert.Null(c.Contingencia.Paquete!.Base);
    }

    /// <summary>Si pedir los cambios falla (un error del servicio), la caja baja la copia completa, como antes.</summary>
    [Fact]
    public async Task Si_los_cambios_fallan_baja_la_copia_completa()
    {
        var c = await AbrirAsync(interno => new CambiosConError(interno));
        var producto = UnoSinMayoreo(c, 4);
        var copias = c.Red.Cuantas(Paquete(c));
        var nuevo = producto.Precios[0].PrecioCentavos + 90;

        await CambiarPrecioAsync(c, producto, nuevo);
        await PermisosSinConexionTests.ConConexionAsync(c.Contingencia);

        Assert.Equal(1, c.Red.Cuantas(Cambios(c)));
        Assert.Equal(copias + 1, c.Red.Cuantas(Paquete(c)));
        Assert.Equal(nuevo, c.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
    }

    /// <summary>
    /// Al abrir la caja otra vez, con la caja principal sin reiniciarse, la copia guardada solo pide lo que cambió mientras
    /// estuvo cerrada; si la caja principal se reinició (otra base), la baja completa.
    /// </summary>
    [Fact]
    public async Task Al_abrir_otra_vez_pide_solo_lo_que_cambio()
    {
        var c = await AbrirAsync();
        var producto = UnoSinMayoreo(c, 5);
        c.Contingencia.Cerrar();
        var nuevo = producto.Precios[0].PrecioCentavos + 40;
        await CambiarPrecioAsync(c, producto, nuevo);

        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var otraVez = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id });
        await PermisosSinConexionTests.ConConexionAsync(otraVez);

        Assert.Equal(0, red.Cuantas(Paquete(c)));
        Assert.Equal(1, red.Cuantas(Cambios(c)));
        Assert.Equal(nuevo, otraVez.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);
    }
}
