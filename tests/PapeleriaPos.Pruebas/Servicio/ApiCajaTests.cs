using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El servicio de verdad con la configuración de una prueba. Es una clase propia y no
/// <c>new WebApplicationFactory&lt;Program&gt;().WithWebHostBuilder(…)</c> para que lo que una prueba arma encima con
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/> (el piloto, otro Telegram…) se detenga con este:
/// WebApplicationFactory lleva la cuenta de lo que se deriva de una fábrica así, pero no de lo que se deriva de un derivado.
/// Ese servicio seguía corriendo hasta el final de la corrida, y su vigilancia de la impresora volvía a abrir la base ya
/// borrada (se quedaba vacía en la carpeta temporal).
/// </summary>
internal sealed class FabricaDePrueba(Action<IWebHostBuilder> configurar) : WebApplicationFactory<Program>
{
    /// <summary>
    /// Las pruebas no leen la configuración de la PC donde corren (servicio.json, en ProgramData): en una caja de verdad traería el
    /// puente con eleventa. La prueba que la necesita pone la suya con «ConfiguracionDeLaPc».
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConfiguracionDeLaPc", "");
        // Ni su registro en archivos (registros\servicio-….log): iría junto a la base, en la carpeta temporal que comparten todas.
        // La prueba que lo revisa lo prende con su propia carpeta (RegistroDelServicioTests).
        builder.UseSetting("Registro:Activo", "false");
        configurar(builder);
    }
}

/// <summary>El servicio de verdad, sobre una base temporal con 30 días de datos ficticios, la Caja 1 y los usuarios de prueba.</summary>
public sealed class ServicioDePrueba : IAsyncLifetime, IAsyncDisposable
{
    private readonly string _ruta;
    private readonly (string Clave, string Valor)[] _ajustes;
    private readonly Action<IServiceCollection>? _servicios;
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public ServicioDePrueba() : this([])
    {
    }

    private ServicioDePrueba((string Clave, string Valor)[] ajustes, Action<IServiceCollection>? servicios = null, string? ruta = null) =>
        (_ajustes, _servicios, _ruta) = (ajustes, servicios, ruta ?? Path.Combine(Path.GetTempPath(), $"pos-prueba-{Guid.NewGuid():N}.db"));

    /// <summary>
    /// Uno propio con su base en una carpeta suya (<c>pos.db</c>), como en la tienda: para lo que deja cosas junto a la base
    /// (restaurar un respaldo aparta la de antes en una carpeta a su lado). La carpeta la borra quien la da.
    /// </summary>
    public static async Task<ServicioDePrueba> CrearEnCarpetaAsync(string carpeta, Action<IServiceCollection>? servicios, params (string Clave, string Valor)[] ajustes)
    {
        Directory.CreateDirectory(carpeta);
        var servicio = new ServicioDePrueba(ajustes, servicios, Path.Combine(carpeta, "pos.db"));
        await servicio.InitializeAsync();
        return servicio;
    }

    /// <summary>Uno propio para una prueba, con su configuración (por ejemplo, la de eleventa).</summary>
    public static async Task<ServicioDePrueba> CrearAsync(params (string Clave, string Valor)[] ajustes)
    {
        var servicio = new ServicioDePrueba(ajustes);
        await servicio.InitializeAsync();
        return servicio;
    }

    /// <summary>Uno propio con su configuración y algo de mentiras en lugar de lo de verdad (por ejemplo, el gbak de las pruebas).</summary>
    public static async Task<ServicioDePrueba> CrearAsync(Action<IServiceCollection> servicios, params (string Clave, string Valor)[] ajustes)
    {
        var servicio = new ServicioDePrueba(ajustes, servicios);
        await servicio.InitializeAsync();
        return servicio;
    }

    /// <summary>La base del servicio, para preparar o revisar lo que la API no muestra.</summary>
    public PosDbContext Base() => new(BaseDeDatos.Opciones($"Data Source={_ruta};Pooling=False"));

    /// <summary>El archivo de la base (junto a él quedan sus marcas, como la de un apagón).</summary>
    public string Ruta => _ruta;

    /// <summary>
    /// Espera a que el catálogo en memoria del servicio tenga lo último que se guardó: se recarga en segundo plano y mientras
    /// contesta con el vigente (entrega 5, parte 2). Para lo que se revisa justo después de cambiar el catálogo.
    /// </summary>
    public async Task CatalogoAlDiaAsync()
    {
        await using var db = Base();
        await Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().AlDiaAsync(db);
    }

    /// <summary>
    /// La base se prepara con el pool, a propósito: la conexión que se queda guardada la mantiene abierta mientras corre la
    /// prueba. El servicio la abre sin pool, y sin esa conexión cada petición la cerraría del todo (vaciando el -wal en la base
    /// y volviéndolo a crear en la siguiente): la corrida tardaba casi el doble. Al final se suelta solo esa
    /// (<see cref="DisposeAsync"/>).
    /// </summary>
    public async Task InitializeAsync()
    {
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(ArchivosDePrueba.Cadena(_ruta))))
        {
            await BaseDeDatos.PrepararAsync(db);
            await GuardadoLote.GuardarAsync(GeneradorFicticios.Generar(new OpcionesFicticios { Dias = 30 }), db);
            await PreparacionCaja.PrepararAsync(db);
        }
        Fabrica = new FabricaDePrueba(b =>
        {
            b.UseSetting("ConnectionStrings:Pos", $"Data Source={_ruta};Pooling=False").UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false");
            foreach (var (clave, valor) in _ajustes)
                b.UseSetting(clave, valor);
            if (_servicios is { } servicios)
                b.ConfigureTestServices(servicios);
        });
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    /// <summary>
    /// Detiene el servicio (y los que las pruebas armaron encima), suelta la conexión guardada de su base y la borra con sus
    /// marcas. Un trabajo de fondo que terminaba puede tener el archivo abierto un instante: se reintenta, y si no, se queda en
    /// la carpeta temporal.
    /// </summary>
    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        ArchivosDePrueba.BorrarBase(_ruta, ".en-uso", ".danada");
        // También la llave propia del servicio, que se hace junto a la base la primera vez que se usa.
        ArchivosDePrueba.BorrarArchivo(Path.ChangeExtension(_ruta, ".llave"));
    }
}

public class ApiCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private HttpClient Cliente() => servicio.Fabrica.CreateClient();

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Entra como cajero, abre turno si hace falta y devuelve todo lo necesario para vender.</summary>
    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Sesion, TurnoDto Turno)> PrepararVentaAsync()
    {
        var http = Cliente();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return (http, caja, sesion, turno);
    }

    private static async Task<ResultadoBusquedaDto> UnProductoConCodigoAsync(HttpClient http, Guid sucursalId) =>
        (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={sucursalId}"))!
            .First(r => r.Codigo is not null);

    [Fact]
    public async Task Los_rapidos_de_la_base_de_prueba_son_la_copia_y_la_curp_que_vende_diez_impresiones()
    {
        var http = Cliente();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();

        var rapidos = (await http.GetFromJsonAsync<List<Rapido>>($"api/sucursales/{caja.SucursalId}/rapidos"))!;

        Assert.Equal(["Copia B/N", "CURP"], rapidos.Select(r => r.Nombre));
        Assert.Equal([1m, 10m], rapidos.Select(r => r.Cantidad));
        var impresion = (await http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{rapidos[1].VarianteId}?sucursalId={caja.SucursalId}"))!;
        Assert.StartsWith("Impresión blanco y negro", impresion.Descripcion);
        // Una sucursal sin rápidos configurados: la fila queda vacía.
        Assert.Empty((await http.GetFromJsonAsync<List<Rapido>>($"api/sucursales/{Guid.NewGuid()}/rapidos"))!);
    }

    [Fact]
    public async Task Un_pin_incorrecto_no_entra()
    {
        var respuesta = await Cliente().PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "9999"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("Usuario o PIN incorrecto.", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Busca_sin_importar_acentos_ni_el_orden_de_las_palabras()
    {
        var http = Cliente();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();

        var resultados = await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=liston%20ROJO&sucursalId={caja.SucursalId}");

        Assert.NotEmpty(resultados!);
        Assert.All(resultados!, r => Assert.Contains("Listón", r.Descripcion));
    }

    [Fact]
    public async Task El_servicio_cobra_con_los_precios_del_catalogo_y_devuelve_el_ticket()
    {
        var (http, caja, sesion, turno) = await PrepararVentaAsync();
        var buscado = await UnProductoConCodigoAsync(http, caja.SucursalId);
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{buscado.Codigo}?sucursalId={caja.SucursalId}"));
        var esperado = 2 * buscado.PrecioCentavos + 1500;

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, sesion.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 2000), new LineaSolicitada(null, null, 1000, "Recuerdo", 1500)],
            [new CobroSolicitado(FormaPago.Efectivo, 100000)])));

        Assert.StartsWith("A-", ticket.Folio);
        Assert.Equal(esperado, ticket.TotalCentavos);
        Assert.Equal(100000 - esperado, ticket.CambioCentavos);
        Assert.Contains(ticket.Lineas, l => l.Descripcion == "Recuerdo");

        var delDia = await http.GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{caja.Id}/ventas-del-dia");
        Assert.Contains(delDia!, v => v.Folio == ticket.Folio);
        var reimpreso = await Leer<TicketDto>(await http.GetAsync($"api/ventas/{delDia!.First(v => v.Folio == ticket.Folio).Id}/ticket"));
        Assert.Equal(ticket.TotalCentavos, reimpreso.TotalCentavos);
    }

    [Fact]
    public async Task Un_cobro_que_no_alcanza_regresa_el_motivo()
    {
        var (http, caja, sesion, turno) = await PrepararVentaAsync();
        var buscado = await UnProductoConCodigoAsync(http, caja.SucursalId);

        var respuesta = await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, sesion.UsuarioId,
            [new LineaSolicitada(buscado.VarianteId, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 1)]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.StartsWith("Faltan", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Un_codigo_que_no_existe_da_404()
    {
        var http = Cliente();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();

        var respuesta = await http.GetAsync($"api/productos/codigo/NOEXISTE?sucursalId={caja.SucursalId}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
