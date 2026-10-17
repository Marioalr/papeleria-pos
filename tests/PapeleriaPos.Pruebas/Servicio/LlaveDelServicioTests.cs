using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La llave propia del servicio (fuera de la base y de sus respaldos) y lo que hace la caja con ella: la recuerda al darse de alta o
/// la primera vez que su caja principal prueba quién es, y después de una caída no le vuelve a mandar su llave a nadie hasta que la
/// dirección guardada pruebe otra vez que es su caja principal. Contra el servicio de verdad, sin red de verdad.
/// </summary>
public sealed class LlaveDelServicioTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaTienda = [(IPAddress.Parse("192.168.1.10"), 24)];
    private static readonly Uri Principal = new("http://192.168.1.10:5080/");
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private WebApplicationFactory<Program> EnLaTienda() =>
        servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new EnLaRedDeLaTienda());
            s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
        }));

    private async Task<AltaCajaDto> DarDeAltaAsync(WebApplicationFactory<Program> fabrica, string serie)
    {
        var sucursal = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))![0].SucursalId;
        var respuesta = await fabrica.CreateClient().PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", sucursal, null, $"Caja {serie}", serie, $"PC-{serie}"));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<AltaCajaDto>())!;
    }

    [Fact]
    public async Task La_caja_principal_firma_con_su_propia_llave_y_la_da_al_dar_de_alta()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "L");
        var reto = Descubrimiento.NuevoReto();

        var identidad = (await tienda.CreateClient().GetFromJsonAsync<IdentidadServicioDto>($"api/identidad?caja={alta.Caja.Id}&reto={reto}"))!;

        Assert.NotNull(alta.LlaveDelServicio);
        Assert.Equal(alta.LlaveDelServicio, identidad.LlaveDelServicio);
        Assert.True(Descubrimiento.FirmaValida(identidad.LlaveDelServicio,
            Descubrimiento.MensajeFirmado(alta.Caja.Id, reto, "192.168.1.10:5080", identidad.Prueba), identidad.Firma));
        // La firma es de ese reto y esa dirección: para otra dirección no sirve.
        Assert.False(Descubrimiento.FirmaValida(identidad.LlaveDelServicio,
            Descubrimiento.MensajeFirmado(alta.Caja.Id, reto, "192.168.1.66:5080", identidad.Prueba), identidad.Firma));
    }

    /// <summary>
    /// La llave se guarda en su propio archivo junto a la base (no en ella): al volver a arrancar es la misma; fuera de Windows,
    /// solo la lee su dueño. Una que no se puede leer (viene de otra PC) se aparta y se hace otra.
    /// </summary>
    [Fact]
    public void La_llave_del_servicio_va_aparte_y_es_la_misma_al_volver_a_arrancar()
    {
        var archivo = LlaveDelServicio.Archivo(Path.Combine(_carpeta.Ruta, "pos.db"));
        Assert.Equal(Path.Combine(_carpeta.Ruta, "pos.llave"), archivo);

        string primera;
        using (var llave = LlaveDelServicio.CargarOCrear(archivo))
            primera = llave.Publica;
        using (var otraVez = LlaveDelServicio.CargarOCrear(archivo))
            Assert.Equal(primera, otraVez.Publica);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archivo));

        File.WriteAllBytes(archivo, [1, 2, 3]);
        Exception? noSeLeyo = null;
        using (var nueva = LlaveDelServicio.CargarOCrear(archivo, ex => noSeLeyo = ex))
            Assert.NotEqual(primera, nueva.Publica);
        Assert.NotNull(noSeLeyo);
        Assert.Single(Directory.EnumerateFiles(_carpeta.Ruta, "pos.llave.no-sirve-*"));
    }

    /// <summary>
    /// Después de una caída, la llave de la caja no sale hasta que la dirección guardada prueba otra vez que es su caja principal: lo
    /// primero que se le pide es la prueba (sin la llave), y solo después el latido con la llave.
    /// </summary>
    [Fact]
    public async Task Despues_de_una_caida_la_llave_no_sale_hasta_que_la_principal_prueba_quien_es()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "M");
        await using var caja = Caja.Abrir(tienda, alta, _carpeta, conocida: alta.LlaveDelServicio);

        await caja.Contingencia.LatidoAsync();
        Assert.Equal(EstadoRed.Buena, caja.Contingencia.EstadoDeLaRed);
        caja.Red.Caida = true;
        await caja.Contingencia.LatidoAsync();
        Assert.True(caja.Conexion.SinComprobar);
        // Mientras, lo de la venta va con la copia: no se le pregunta a nadie.
        Assert.False(caja.Contingencia.EnLinea);
        caja.Red.Caida = false;
        var desde = caja.Red.Pedidas.Count;
        await caja.Contingencia.LatidoAsync();

        var despues = caja.Red.Pedidas.Skip(desde).ToList();
        Assert.StartsWith("/api/identidad", despues[0].Ruta);
        Assert.False(despues[0].ConLlave);
        Assert.Contains(despues.Skip(1), p => p.Ruta.StartsWith("/api/estado", StringComparison.Ordinal) && p.ConLlave);
        Assert.False(caja.Conexion.SinComprobar);
        Assert.Equal(EstadoRed.Buena, caja.Contingencia.EstadoDeLaRed);
    }

    /// <summary>
    /// La dirección guardada sabe de esta caja pero firma con otra llave (la PC se reinstaló, o es una copia de su base): la caja no le
    /// manda su llave, sigue con su copia y la franja ofrece darla de alta otra vez.
    /// </summary>
    [Fact]
    public async Task Con_otra_llave_del_servicio_la_caja_no_manda_su_llave_y_sigue_con_su_copia()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "N");
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var caja = Caja.Abrir(tienda, alta, _carpeta, conocida: Convert.ToBase64String(otra.ExportSubjectPublicKeyInfo()));
        caja.Conexion.SinComprobar = true;

        await caja.Contingencia.LatidoAsync();
        await caja.Contingencia.LatidoAsync();

        Assert.DoesNotContain(caja.Red.Pedidas, p => p.ConLlave);
        Assert.Equal(EstadoRed.Caida, caja.Contingencia.EstadoDeLaRed);
        Assert.True(caja.Reencuentro.OtraIdentidad);
        Assert.Equal(ReencuentroDeLaPrincipal.OtraCajaPrincipal, caja.Reencuentro.Resultado);
    }

    /// <summary>
    /// Un servicio de antes (sin la prueba de quién es): si la caja no conoce su llave, se sigue como antes (con la llave); si ya la
    /// conoce, no le basta.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Con_un_servicio_de_antes_se_sigue_como_antes_si_no_se_conoce_su_llave(bool conoceLaLlave)
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, conoceLaLlave ? "P" : "O");
        await using var caja = Caja.Abrir(tienda, alta, _carpeta, conocida: conoceLaLlave ? alta.LlaveDelServicio : null);
        caja.Red.SinLaPrueba = true;
        caja.Conexion.SinComprobar = true;

        await caja.Contingencia.LatidoAsync();

        Assert.Equal(conoceLaLlave ? EstadoRed.Caida : EstadoRed.Buena, caja.Contingencia.EstadoDeLaRed);
        Assert.Equal(!conoceLaLlave, caja.Red.Pedidas.Any(p => p.ConLlave));
    }

    /// <summary>Si la caja principal ya no conoce a esta caja (la quitaron), se le pide a la pantalla darla de alta otra vez, sin mandar la llave.</summary>
    [Fact]
    public async Task Si_la_principal_ya_no_conoce_a_la_caja_se_da_de_alta_otra_vez()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "Q");
        var dueno = (await (await servicio.Fabrica.CreateClient().PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        (await servicio.Fabrica.CreateClient().PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/quitar-equipo", new SolicitudQuitarEquipo(dueno.UsuarioId))).EnsureSuccessStatusCode();
        await using var caja = Caja.Abrir(tienda, alta, _carpeta, conocida: alta.LlaveDelServicio);
        caja.Conexion.SinComprobar = true;
        string? porque = null;
        caja.Contingencia.SinAlta += texto => porque = texto;

        await caja.Contingencia.LatidoAsync();

        Assert.StartsWith("La caja principal ya no reconoce a esta PC", porque);
        Assert.DoesNotContain(caja.Red.Pedidas, p => p.ConLlave);
    }

    /// <summary>
    /// Una caja de antes (sin conocer la llave de su caja principal) la aprende al abrir, la primera vez que su dirección de siempre
    /// prueba quién es, y la guarda.
    /// </summary>
    [Fact]
    public async Task Una_caja_de_antes_aprende_la_llave_de_su_caja_principal_al_abrir()
    {
        await using var tienda = EnLaTienda();
        var alta = await DarDeAltaAsync(tienda, "R");
        await using var caja = Caja.Abrir(tienda, alta, _carpeta, conocida: null);

        caja.Contingencia.Iniciar();
        await VentaConCopia.EsperarAsync(() => caja.Conexion.LlaveDelServicio is not null, "la llave de la caja principal");

        Assert.Equal(alta.LlaveDelServicio, caja.Conexion.LlaveDelServicio);
        await VentaConCopia.EsperarAsync(() => caja.Guardadas.Any(c => c == alta.LlaveDelServicio), "la llave guardada en caja.json");
        await VentaConCopia.EsperarAsync(() => caja.Contingencia.EstadoDeLaRed == EstadoRed.Buena && !caja.Conexion.SinComprobar, "el latido con la llave");
    }

    /// <summary>Lo que va por la red de la caja: cada petición con su ruta y si llevaba la llave de la caja.</summary>
    private sealed class Grabadora(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        private readonly ConcurrentQueue<(string Ruta, bool ConLlave)> _pedidas = new();

        public IReadOnlyList<(string Ruta, bool ConLlave)> Pedidas => [.. _pedidas];

        /// <summary>La red caída: no se puede conectar.</summary>
        public bool Caida { get; set; }

        /// <summary>Un servicio de antes: no tiene la ruta de la prueba.</summary>
        public bool SinLaPrueba { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.PathAndQuery;
            _pedidas.Enqueue((ruta, request.Headers.Contains(ClienteCaja.EncabezadoLlave)));
            if (Caida)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "La red está caída.");
            if (SinLaPrueba && ruta.StartsWith("/api/identidad", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Una caja 2 dada de alta, con su contingencia y su red grabada, contra la caja principal de prueba en 192.168.1.10.</summary>
    private sealed record Caja(ConexionCaja Conexion, Contingencia Contingencia, Grabadora Red, ReencuentroDeLaPrincipal Reencuentro, List<string?> Guardadas)
        : IAsyncDisposable
    {
        /// <param name="conocida">La llave del servicio que la caja ya conoce; vacía: una caja de antes.</param>
        public static Caja Abrir(WebApplicationFactory<Program> tienda, AltaCajaDto alta, CarpetaDePrueba carpeta, string? conocida)
        {
            var guardadas = new List<string?>();
            var conexion = new ConexionCaja
            {
                Servicio = Principal,
                CajaId = alta.Caja.Id,
                Llave = alta.Llave,
                LlaveDelServicio = conocida,
                PideAlta = true,
                AlGuardar = c =>
                {
                    lock (guardadas)
                        guardadas.Add(c.LlaveDelServicio);
                    return Task.CompletedTask;
                },
            };
            var red = new Grabadora(tienda.Server.CreateHandler());
            var http = new HttpClient(red);
            var bitacora = new BitacoraDeRed(carpeta.Ruta, "red-caja");
            var reencuentro = new ReencuentroDeLaPrincipal(conexion, new NadieContesta(), http, bitacora: bitacora, redes: () => RedDeLaTienda);
            var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(carpeta.Ruta, $"caja-{Guid.NewGuid():N}.db")), http, conexion,
                redDeLaCaja: new RedDeLaCaja(conexion, bitacora, reencuentro: reencuentro));
            return new Caja(conexion, contingencia, red, reencuentro, guardadas);
        }

        public ValueTask DisposeAsync()
        {
            Contingencia.Cerrar(TimeSpan.FromSeconds(20));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NadieContesta : IBuscadorDeLaPrincipal
    {
        public Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>([]);
    }
}
