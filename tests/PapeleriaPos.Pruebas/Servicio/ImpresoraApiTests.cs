using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Datos;
using PapeleriaPos.Medicion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Un reloj que solo avanza cuando la prueba lo dice.</summary>
public sealed class RelojDePrueba(DateTime inicioUtc) : TimeProvider
{
    public DateTime AhoraUtc { get; private set; } = inicioUtc;

    public override DateTimeOffset GetUtcNow() => new(AhoraUtc, TimeSpan.Zero);

    public void Avanzar(TimeSpan cuanto) => AhoraUtc += cuanto;
}

/// <summary>La impresora de la prueba: contesta lo que la prueba le ponga.</summary>
public sealed class ImpresoraDePrueba : ILectorImpresora
{
    public PrinterStatus Estado { get; set; } = new() { InkK = 80, InkC = 80, InkM = 80, InkY = 80, Maintenance = 60, LifeCount = 1000, ColorCount = 400, MonoCount = 600 };
    public bool Responde { get; set; } = true;

    public Task<PrinterStatus> LeerAsync(string host, CancellationToken ct) =>
        Responde ? Task.FromResult(Estado) : throw new TimeoutException($"SNMP sin respuesta de {host}");
}

/// <summary>El servicio con una impresora y un reloj de prueba (la vigilancia no corre sola: la prueba pide cada lectura).</summary>
public sealed class ServicioConImpresora : IAsyncLifetime
{
    private readonly ServicioDePrueba _base = new();
    public RelojDePrueba Reloj { get; } = new(DateTime.UtcNow);
    public ImpresoraDePrueba Impresora { get; } = new();
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _base.InitializeAsync();
        Fabrica = _base.Fabrica.WithWebHostBuilder(b => b.UseSetting("Impresora:Vigilar", "false").ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Reloj);
            s.RemoveAll<ILectorImpresora>();
            s.AddSingleton<ILectorImpresora>(Impresora);
        }));
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await _base.DisposeAsync();
    }
}

public class ImpresoraApiTests(ServicioConImpresora servicio) : IClassFixture<ServicioConImpresora>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Dueno)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora("dueno", "3333", new ConfiguracionImpresoraDto("impresora-de-prueba", 10, 5))));
        return (http, caja, cajero, dueno);
    }

    private static TrabajoMedidoDto Trabajo(CajaDto caja, string documento, int paginas, DateTime enviadoUtc) =>
        new(Guid.NewGuid(), caja.Id, null, "PC-PRUEBA", "ControlPrint Lab (WF-6590)", 9, documento, "cajero", enviadoUtc.AddSeconds(20), EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)).ToList(), true, null, null, enviadoUtc);

    private Task<EstadoImpresoraDto> LeerImpresoraAsync(HttpClient http, CajaDto caja) =>
        http.PostAsync($"api/sucursales/{caja.SucursalId}/impresora/leer", null).ContinueWith(r => Leer<EstadoImpresoraDto>(r.Result)).Unwrap();

    [Fact]
    public async Task Con_un_atasco_la_impresora_cuenta_menos_caras_y_se_puede_cobrar_solo_lo_que_salio()
    {
        var (http, caja, cajero, _) = await PrepararAsync();
        var contador = servicio.Impresora.Estado.LifeCount!.Value;
        var inicio = (await LeerImpresoraAsync(http, caja)).Hoy;

        // Se manda un trabajo de 6 páginas; la impresora se atasca después de la primera.
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(3));
        var trabajo = Trabajo(caja, "Prueba atasco.pdf", 6, servicio.Reloj.AhoraUtc);
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        servicio.Impresora.Estado = servicio.Impresora.Estado with { LifeCount = contador + 1 };
        servicio.Reloj.Avanzar(TimeSpan.FromSeconds(90));
        await LeerImpresoraAsync(http, caja);
        var mientras = (await PorCobrarAsync(http, caja, cajero)).Single(t => t.Id == trabajo.Id);
        servicio.Reloj.Avanzar(TimeSpan.FromSeconds(40));
        var estado = await LeerImpresoraAsync(http, caja);
        var verificada = (await PorCobrarAsync(http, caja, cajero)).Single(t => t.Id == trabajo.Id);

        Assert.Null(mientras.CarasContadas);                 // aún no se asienta el contador
        Assert.Equal(1, verificada.CarasContadas);
        Assert.NotNull(verificada.PrecioSoloLoQueSalioCentavos);
        Assert.True(verificada.PrecioSoloLoQueSalioCentavos < verificada.PrecioCentavos);
        Assert.Equal(1, estado.Hoy!.Contadas - (inicio?.Contadas ?? 0));
        Assert.Equal(1, estado.Hoy.CarasImpresas - (inicio?.CarasImpresas ?? 0));     // lo que salió, no lo que se mandó
        Assert.Equal(inicio?.SinRegistrar ?? 0, estado.Hoy.SinRegistrar);

        var parcial = await Leer<ImpresionPorCobrarDto>(await http.PostAsJsonAsync($"api/impresiones/{trabajo.Id}/solo-lo-que-salio",
            new SolicitudSoloLoQueSalio(cajero.UsuarioId)));
        Assert.Equal(verificada.PrecioSoloLoQueSalioCentavos, parcial.PrecioCentavos);
        Assert.Equal(verificada.PrecioCentavos, parcial.PrecioCompletoCentavos);
        Assert.Null(parcial.PrecioSoloLoQueSalioCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"api/impresiones/{trabajo.Id}/solo-lo-que-salio",
            new SolicitudSoloLoQueSalio(cajero.UsuarioId))).StatusCode);
    }

    [Fact]
    public async Task Un_trabajo_mandado_antes_de_la_lectura_base_no_se_verifica()
    {
        var (http, caja, cajero, _) = await PrepararAsync();
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(10));
        await LeerImpresoraAsync(http, caja);
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(3));
        var reciente = Trabajo(caja, "Reciente.pdf", 2, servicio.Reloj.AhoraUtc);
        // El agente tardó en mandar este: salió antes de la lectura, así que ya está en el contador base.
        var atrasado = Trabajo(caja, "Atrasado.pdf", 3, servicio.Reloj.AhoraUtc.AddMinutes(-6));
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", reciente));
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", atrasado));
        servicio.Impresora.Estado = servicio.Impresora.Estado with { LifeCount = servicio.Impresora.Estado.LifeCount + 2 };
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(2));
        await LeerImpresoraAsync(http, caja);
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(1));
        await LeerImpresoraAsync(http, caja);

        var porCobrar = await PorCobrarAsync(http, caja, cajero);
        Assert.Null(porCobrar.Single(t => t.Id == reciente.Id).CarasContadas);
        Assert.Null(porCobrar.Single(t => t.Id == atrasado.Id).CarasContadas);
    }

    [Fact]
    public async Task Las_copias_del_panel_cuentan_contra_el_contador()
    {
        var (http, caja, cajero, _) = await PrepararAsync();
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(20));
        var antes = await LeerImpresoraAsync(http, caja);

        var precio = await Leer<PrecioCopiasDto>(await http.PostAsJsonAsync("api/impresiones/copias/precio",
            new SolicitudPrecioCopias(caja.SucursalId, 3, false, "Carta", false)));
        var copias = await Leer<ImpresionPorCobrarDto>(await http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), caja.Id, cajero.UsuarioId, 3, false, "Carta", false, null, null)));
        var otroPrecio = await Leer<ImpresionPorCobrarDto>(await http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), caja.Id, cajero.UsuarioId, 1, false, "Carta", true, 500, null)));
        // Contó 3 copias + 2 caras de la de doble cara + 4 que nadie registró.
        servicio.Impresora.Estado = servicio.Impresora.Estado with { LifeCount = servicio.Impresora.Estado.LifeCount + 9 };
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(5));
        var despues = await LeerImpresoraAsync(http, caja);

        Assert.Equal("3 copias en B/N", precio.Descripcion);
        Assert.Equal(precio.PrecioCentavos, copias.PrecioCentavos);
        Assert.Equal(TipoTrabajoImpresion.Copia, copias.Tipo);
        Assert.Equal(500, otroPrecio.PrecioCentavos);
        Assert.Equal(2, otroPrecio.Caras);
        // Sin el tope en cero: lo que contó la impresora menos lo registrado.
        static long Diferencia(ComparacionContadorDto? c) => c is null ? 0 : c.Contadas - c.CarasImpresas - c.CarasCopias;
        Assert.Equal(4, Diferencia(despues.Hoy) - Diferencia(antes.Hoy));
        Assert.True(despues.Hoy!.CarasCopias >= 5);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), caja.Id, cajero.UsuarioId, 0, false, "Carta", false, null, null))).StatusCode);
    }

    [Fact]
    public async Task Sin_respuesta_se_dice_por_que_y_solo_el_dueno_configura()
    {
        var (http, caja, _, _) = await PrepararAsync();
        servicio.Impresora.Responde = false;
        try
        {
            var estado = await LeerImpresoraAsync(http, caja);
            Assert.Contains("sin respuesta", estado.Error);
        }
        finally
        {
            servicio.Impresora.Responde = true;
        }
        var cajero = await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora("cajero", "1111", new ConfiguracionImpresoraDto("otra", 10, 5)));
        Assert.Equal(HttpStatusCode.BadRequest, cajero.StatusCode);
        Assert.Contains("«Configurar la impresora»: Dueño de prueba", await cajero.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cotizar_le_pone_precio_como_a_una_impresion_y_solo_el_dueno_ve_el_costo()
    {
        var (http, caja, cajero, dueno) = await PrepararAsync();
        var paginas = new List<CoberturaPaginaDto>
        {
            new(0.3, 0.3, 0.3, 0.1, 0.4, 93.5, true),
            new(0, 0, 0, 0.05, 0.05, 93.5, false),
            new(0, 0, 0, 0.05, 0.05, 93.5, false),
        };
        var opciones = await http.GetFromJsonAsync<OpcionesImpresionDto>($"api/sucursales/{caja.SucursalId}/impresiones/opciones");

        var paraCajero = await Leer<CotizacionDto>(await http.PostAsJsonAsync("api/impresiones/cotizar",
            new SolicitudCotizacion(caja.SucursalId, cajero.UsuarioId, paginas, new OpcionesCotizacionDto(Copias: 2))));
        var paraDueno = await Leer<CotizacionDto>(await http.PostAsJsonAsync("api/impresiones/cotizar",
            new SolicitudCotizacion(caja.SucursalId, dueno.UsuarioId, paginas, new OpcionesCotizacionDto(Copias: 2, Rango: "2-3"))));
        var malRango = await http.PostAsJsonAsync("api/impresiones/cotizar",
            new SolicitudCotizacion(caja.SucursalId, cajero.UsuarioId, paginas, new OpcionesCotizacionDto(Rango: "5")));

        Assert.Contains(opciones!.TiposPapel, o => o.Clave == "Plain");
        Assert.Equal(["Carta", "Oficio", "A4"], opciones.Tamanos);
        Assert.Equal(6, paraCajero.Caras);
        Assert.Equal(2, paraCajero.CarasColor);
        Assert.Equal(3, paraCajero.PorPagina.Count);
        Assert.Equal("Color", paraCajero.PorPagina[0].Tipo);
        Assert.Null(paraCajero.Costo);
        Assert.All(paraCajero.PorPagina, p => Assert.Null(p.CostoCentavos));
        Assert.True(paraCajero.PrecioCentavos > 0 && paraCajero.PrecioCentavos % 100 == 0);
        Assert.Equal(4, paraDueno.Caras);
        Assert.Equal([2, 3], paraDueno.PorPagina.Select(p => p.Pagina));
        Assert.NotNull(paraDueno.Costo);
        Assert.True(paraDueno.PrecioCentavos <= paraCajero.PrecioCentavos);
        Assert.Equal(HttpStatusCode.BadRequest, malRango.StatusCode);
        Assert.Contains("no existe la 5", await malRango.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task El_desglose_del_dia_lo_ven_el_dueno_y_el_encargado_y_el_costo_solo_el_dueno()
    {
        var (http, caja, cajero, dueno) = await PrepararAsync();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var trabajo = Trabajo(caja, "Desglose.pdf", 2, servicio.Reloj.AhoraUtc);
        await Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        var fecha = DateOnly.FromDateTime(PapeleriaPos.Nucleo.HoraMexico.ALocal(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        string Ruta(SesionDto quien) => $"api/sucursales/{caja.SucursalId}/impresiones?fecha={fecha}&usuarioId={quien.UsuarioId}";

        var paraDueno = await Leer<List<ImpresionDelDiaDto>>(await http.GetAsync(Ruta(dueno)));
        var paraEncargado = await Leer<List<ImpresionDelDiaDto>>(await http.GetAsync(Ruta(encargado)));
        var paraCajero = await http.GetAsync(Ruta(cajero));

        Assert.NotNull(paraDueno.Single(t => t.Id == trabajo.Id).Costo);
        Assert.Null(paraEncargado.Single(t => t.Id == trabajo.Id).Costo);
        Assert.Equal(HttpStatusCode.Forbidden, paraCajero.StatusCode);
    }

    [Fact]
    public async Task Calibrar_pide_datos_suficientes_y_al_dueno()
    {
        var (http, caja, _, _) = await PrepararAsync();
        var calibracion = await http.GetFromJsonAsync<CalibracionDto>($"api/sucursales/{caja.SucursalId}/impresora/calibracion");
        var sinDatos = await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora/calibracion", new SolicitudCredenciales("dueno", "3333"));
        var cajero = await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora/calibracion", new SolicitudCredenciales("cajero", "1111"));

        Assert.Equal(4, calibracion!.Canales.Count);
        Assert.Equal(HttpStatusCode.BadRequest, sinDatos.StatusCode);
        Assert.Contains("datos suficientes", await sinDatos.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, cajero.StatusCode);
    }

    private static Task<List<ImpresionPorCobrarDto>> PorCobrarAsync(HttpClient http, CajaDto caja, SesionDto quien) =>
        http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>($"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={quien.UsuarioId}")!;
}

/// <summary>Los avisos de consumibles, con su propia base: sus lecturas no se mezclan con las del contador.</summary>
public class AvisosImpresoraTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task La_tinta_baja_y_el_cartucho_nuevo_avisan_por_telegram_una_vez()
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var caja = db.Cajas.Single();
        var dueno = db.Usuarios.Single(u => u.NombreUsuario == "dueno");
        db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.Id, ChatId = 777, VinculadoUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var impresora = new ServicioImpresora(db);
        var t0 = DateTime.UtcNow;
        PrinterStatus Nivel(int minutos, int cian) => new() { TakenUtc = t0.AddMinutes(minutos), InkK = 50, InkC = cian, InkM = 50, InkY = 50, LifeCount = 5000 };

        await impresora.GuardarLecturaAsync(caja.SucursalId, Nivel(0, 12));
        await impresora.GuardarLecturaAsync(caja.SucursalId, Nivel(5, 9));
        await impresora.GuardarLecturaAsync(caja.SucursalId, Nivel(10, 8));
        await impresora.GuardarLecturaAsync(caja.SucursalId, Nivel(15, 100));

        var mensajes = db.MensajesTelegram.Where(m => m.ChatId == 777).Select(m => m.Texto).ToList();
        Assert.Single(mensajes, m => m.Contains("cartucho cian bajo") && m.Contains("Queda 9 %"));
        Assert.Single(mensajes, m => m.Contains("cartucho cian nuevo"));
        Assert.Equal(2, mensajes.Count);
    }
}
