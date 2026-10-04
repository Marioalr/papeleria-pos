using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>El servicio de verdad, sobre una base temporal con 30 días de datos ficticios, la Caja 1 y los usuarios de prueba.</summary>
public sealed class ServicioDePrueba : IAsyncLifetime
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"pos-prueba-{Guid.NewGuid():N}.db");
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={_ruta}")))
        {
            await BaseDeDatos.PrepararAsync(db);
            await GuardadoLote.GuardarAsync(GeneradorFicticios.Generar(new OpcionesFicticios { Dias = 30 }), db);
            await PreparacionCaja.PrepararAsync(db);
        }
        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Pos", $"Data Source={_ruta};Pooling=False").UseSetting("Respaldos:Activo", "false").UseSetting("Telegram:Activo", "false"));
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _ruta, _ruta + "-wal", _ruta + "-shm" })
            File.Delete(archivo);
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

/// <summary>El corte Z cierra el turno: va en su propia base para no estorbar a las demás pruebas.</summary>
public class CorteApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task El_corte_z_pide_el_pin_del_encargado_y_cierra_el_turno()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var turno = (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 30000))).Content.ReadFromJsonAsync<TurnoDto>())!;
        Assert.True((await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(PapeleriaPos.Nucleo.Turnos.TipoMovimientoCaja.Salida, 5000, "Garrafón", cajero.UsuarioId))).IsSuccessStatusCode);
        var conteo = new Dictionary<long, int> { [20000] = 1, [5000] = 1 };

        var conCajero = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ("cajero", "1111", conteo));
        var conPinMalo = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ("encargado", "0000", conteo));
        var corte = await http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ("encargado", "2222", conteo));

        Assert.Equal(HttpStatusCode.BadRequest, conCajero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, conPinMalo.StatusCode);
        var resultado = (await corte.Content.ReadFromJsonAsync<CorteDto>())!;
        Assert.Equal(30000 - 5000, resultado.EsperadoCentavos);
        Assert.Equal(0, resultado.DiferenciaCentavos);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync($"api/cajas/{caja.Id}/turno")).StatusCode);
    }
}
