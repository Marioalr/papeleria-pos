using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Impresiones.Servicio;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Las impresoras de la prueba: cada dirección contesta lo suyo (las de la matriz son tres, 2026-11-03).</summary>
public sealed class ImpresorasDePrueba : ILectorImpresora
{
    private readonly ConcurrentDictionary<string, PrinterStatus> _estados = new(StringComparer.OrdinalIgnoreCase);

    public PrinterStatus this[string host]
    {
        get => _estados.GetOrAdd(host, _ => new PrinterStatus { InkK = 80, InkC = 80, InkM = 80, InkY = 80, LifeCount = 1000, ColorCount = 400, MonoCount = 600 });
        set => _estados[host] = value;
    }

    public void Contar(string host, int caras) => this[host] = this[host] with { LifeCount = this[host].LifeCount + caras, MonoCount = this[host].MonoCount + caras };

    public Task<PrinterStatus> LeerAsync(string host, CancellationToken ct) => Task.FromResult(this[host]);
}

/// <summary>El servicio con varias impresoras y un reloj de prueba (la vigilancia no corre sola: la prueba pide cada lectura).</summary>
public sealed class ServicioConVariasImpresoras : IAsyncLifetime
{
    private readonly ServicioDePrueba _base = new();
    public RelojDePrueba Reloj { get; } = new(DateTime.UtcNow);
    public ImpresorasDePrueba Impresoras { get; } = new();
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public PapeleriaPos.Datos.PosDbContext Base() => _base.Base();

    public async Task InitializeAsync()
    {
        await _base.InitializeAsync();
        Fabrica = _base.Fabrica.WithWebHostBuilder(b => b.UseSetting("Impresora:Vigilar", "false").ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Reloj);
            s.RemoveAll<ILectorImpresora>();
            s.AddSingleton<ILectorImpresora>(Impresoras);
        }));
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await _base.DisposeAsync();
    }
}

/// <summary>Lo que necesitan las pruebas de varias impresoras por la API.</summary>
internal static class ApiDeImpresoras
{
    public static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    public static async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Dueno)> AbrirAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> fabrica)
    {
        var http = fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        return (http, caja, cajero, dueno);
    }

    public static string Ruta(CajaDto caja) => $"api/sucursales/{caja.SucursalId}/impresoras";

    /// <summary>Lo que mandaría el agente de la cola de esa impresora: hojas carta en B/N.</summary>
    public static TrabajoMedidoDto Trabajo(CajaDto caja, string documento, int paginas, DateTime enviadoUtc, int? impresora) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", $"Cola de la {impresora}", 9, documento, "cajero", enviadoUtc.AddSeconds(20), EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), true, null, null, enviadoUtc,
            Impresora: impresora);
}

/// <summary>
/// Cada impresora se lee con su dirección y verifica las hojas de lo suyo con su contador (2026-11-03): un atasco en la L5590 no se
/// confunde con lo de la WF-6590, y F2 dice de cuál salió cada impresión.
/// </summary>
public sealed class VariasImpresorasApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Cada_impresora_se_lee_y_verifica_sus_hojas_con_su_contador_y_F2_dice_de_cual_salio()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        // La de antes (la 1) con su IP por la API de siempre; la L5590, nueva.
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("wf-de-prueba", 10, 5))));
        var l5590 = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
            new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590", PerfilImpresora.L5590, "l5590-de-prueba")));
        Assert.Equal((2, PerfilImpresora.L5590, true), (l5590.Numero, l5590.Perfil, l5590.ConsumiblesPropios));
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(3));
        var enviado = servicio.Reloj.AhoraUtc;
        var enLaWf = ApiDeImpresoras.Trabajo(caja, "Tarea completa.pdf", 6, enviado, 1);
        var enLaL5590 = ApiDeImpresoras.Trabajo(caja, "Atorada.pdf", 4, enviado, l5590.Numero);

        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", enLaWf));
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", enLaL5590));
        // La WF-6590 sacó sus 6; la L5590 se atoró después de la primera.
        servicio.Impresoras.Contar("wf-de-prueba", 6);
        servicio.Impresoras.Contar("l5590-de-prueba", 1);
        foreach (var espera in new[] { 90, 40 })
        {
            servicio.Reloj.Avanzar(TimeSpan.FromSeconds(espera));
            foreach (var numero in new[] { 1, 2 })
                await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsync($"{ruta}/{numero}/leer", null));
        }

        var porCobrar = (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={cajero.UsuarioId}"))!;
        var wf = porCobrar.Single(t => t.Id == enLaWf.Id);
        var atorada = porCobrar.Single(t => t.Id == enLaL5590.Id);
        Assert.Equal((6, "WF-6590"), (wf.CarasContadas, wf.Impresora));
        Assert.Equal((1, "L5590"), (atorada.CarasContadas, atorada.Impresora));
        Assert.NotNull(atorada.PrecioSoloLoQueSalioCentavos);
        var impresoras = (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!;
        Assert.Equal([1006L, 1001L], impresoras.Select(i => i.Estado.Ultima!.Contador!.Value));
        Assert.Equal([6, 1], impresoras.Select(i => i.Estado.Hoy!.CarasImpresas));
        Assert.All(impresoras, i => Assert.Equal(0, i.Estado.Hoy!.SinRegistrar));
    }
}

/// <summary>Dar de alta, cambiar y dar de baja una impresora: quien tiene «Configurar la impresora», y queda en la bitácora.</summary>
public sealed class AdministrarImpresorasApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Las_agrega_cambia_y_da_de_baja_quien_configura_la_impresora_y_la_ultima_activa_no_se_da_de_baja()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);

        var delCajero = await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(cajero.UsuarioId, "WF-6590 2", PerfilImpresora.WF6590));
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);

        // La 1, la de antes, por la API de siempre (la base de la prueba no tenía ninguna); luego la 2.
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto(null, 10, 5))));
        var segunda = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
            new SolicitudNuevaImpresora(dueno.UsuarioId, "WF-6590 2", PerfilImpresora.WF6590)));
        var repetida = await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "wf-6590 2", PerfilImpresora.L5590));
        var renombrada = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PutAsJsonAsync($"{ruta}/{segunda.Numero}",
            new SolicitudCambioImpresora(dueno.UsuarioId, Nombre: "WF-6590 de la entrada")));
        var deBaja = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PutAsJsonAsync($"{ruta}/{segunda.Numero}",
            new SolicitudCambioImpresora(dueno.UsuarioId, Activa: false)));
        var laUltima = await http.PutAsJsonAsync($"{ruta}/1", new SolicitudCambioImpresora(dueno.UsuarioId, Activa: false));
        var queNoExiste = await http.PutAsJsonAsync($"{ruta}/9", new SolicitudCambioImpresora(dueno.UsuarioId, Nombre: "Otra"));

        Assert.Equal(2, segunda.Numero);
        Assert.Equal(HttpStatusCode.BadRequest, repetida.StatusCode);
        Assert.Equal("WF-6590 de la entrada", renombrada.Nombre);
        Assert.False(deBaja.Activa);
        Assert.Equal(HttpStatusCode.BadRequest, laUltima.StatusCode);
        Assert.Contains("única impresora activa", await laUltima.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, queNoExiste.StatusCode);
        var todas = (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!;
        Assert.Equal([(1, true), (2, false)], todas.Select(i => (i.Numero, i.Activa)));
        await using var db = servicio.Base();
        var bitacora = await db.Bitacora.Where(b => b.Accion == AccionControlada.ConfigurarImpresora).Select(b => b.Detalle).ToListAsync();
        Assert.Contains("Agregó la impresora 2, «WF-6590 2» (WF-6590)", bitacora);
        Assert.Contains("Cambió el nombre de la impresora 2: «WF-6590 2» → «WF-6590 de la entrada»", bitacora);
        Assert.Contains("Dio de baja la impresora 2, «WF-6590 de la entrada»", bitacora);
    }
}

/// <summary>Una tienda que se instala desde cero no trae impresoras: la primera que se agregue es la 1, sea la que sea.</summary>
public sealed class LaPrimeraImpresoraApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Sin_ninguna_la_primera_que_se_agrega_es_la_1_aunque_sea_la_L5590()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);

        var primera = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ApiDeImpresoras.Ruta(caja),
            new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590", PerfilImpresora.L5590)));

        Assert.Equal((1, true), (primera.Numero, primera.ConsumiblesPropios));
    }
}

/// <summary>
/// Los consumibles propios de la L5590 (sus botellas) en los costos (2026-11-03): los ve quien ve los costos, los cambia quien además
/// configura la impresora, y la bitácora dice qué cambió sin los montos.
/// </summary>
public sealed class ConsumiblesDeImpresoraApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Las_botellas_de_la_L5590_se_ven_y_se_cambian_en_los_costos_y_las_WF_6590_usan_los_de_la_sucursal()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        var costos = $"api/sucursales/{caja.SucursalId}/impresiones/costos";
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto(null, 10, 5))));
        var l5590 = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta,
            new SolicitudNuevaImpresora(dueno.UsuarioId, "L5590", PerfilImpresora.L5590)));

        var antes = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.GetAsync($"{costos}?usuarioId={dueno.UsuarioId}"));
        var suyas = Assert.Single(antes.Impresoras!);
        Assert.Equal((l5590.Numero, (decimal?)null), (suyas.Numero, suyas.CajaMantenimiento));
        Assert.All(suyas.Tintas, t => Assert.Equal(0, t.Precio));
        Assert.Contains("Botella negra de la L5590", antes.Faltan);

        var botellas = new SolicitudConsumiblesImpresora(dueno.UsuarioId,
            [new("Negro", 259, 4300), new("Cian", 259, 7300), new("Magenta", 259, 7300), new("Amarillo", 259, 7300)]);
        var delCajero = await http.PutAsJsonAsync($"{ruta}/{l5590.Numero}/consumibles", botellas with { UsuarioId = cajero.UsuarioId });
        var despues = await ApiDeImpresoras.Leer<EstadoCostosImpresionDto>(await http.PutAsJsonAsync($"{ruta}/{l5590.Numero}/consumibles", botellas));
        var deUnaWf = await http.PutAsJsonAsync($"{ruta}/1/consumibles", botellas);

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.All(Assert.Single(despues.Impresoras!).Tintas, t => Assert.Equal(259, t.Precio));
        Assert.DoesNotContain(despues.Faltan, f => f.Contains("L5590"));
        Assert.Equal(HttpStatusCode.BadRequest, deUnaWf.StatusCode);
        Assert.Contains("usa los cartuchos de la sucursal", await deUnaWf.Content.ReadAsStringAsync());
        await using var db = servicio.Base();
        var cambio = Assert.Single(await db.Bitacora.Where(b => b.Detalle.StartsWith("Cambió los costos")).Select(b => b.Detalle).ToListAsync());
        Assert.Contains("el precio de la botella negra de la L5590", cambio);
        Assert.DoesNotContain("259", cambio);
    }
}
