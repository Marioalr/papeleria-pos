using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Servicio;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Pruebas.Interfaz;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Auditoría del 2026-11-04: lo que eleventa rechaza siempre (no la red) se reintentaba para siempre y todo lo demás esperaba detrás,
/// sin salida en la pantalla. Y si la hora de la PC regresaba, una cancelación quedaba antes que su venta y atoraba la bandeja sin
/// decir nada. El dueño la deja por revisar desde el panel; lo que sigue pasa.
/// </summary>
public sealed class BandejaAtoradaTests
{
    private static async Task<PuenteEleventaDto> PantallaAsync(Caja c) => (await c.Http.GetFromJsonAsync<PuenteEleventaDto>("api/eleventa/pantalla"))!;

    private static async Task HastaIntentosAsync(Caja c, int intentos)
    {
        for (var i = 0; i < intentos; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
            var vuelta = i + 2;
            await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.First().Intentos >= Math.Min(vuelta, intentos));
        }
    }

    [FactConFirebird]
    public async Task Lo_que_eleventa_rechaza_siempre_se_ve_y_el_dueno_lo_deja_por_revisar_para_que_pase_lo_demas()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        // Una restricción de la base que el puente no conoce: rechaza el ticket de más de $20.
        await e.EjecutarAsync("ALTER TABLE VENTATICKETS ADD CONSTRAINT CK_DE_LA_PRUEBA CHECK (TOTAL IS NULL OR TOTAL < 20)");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var rechazada = await CobrarAsync(c, plumas: 3);
        var detras = await CobrarAsync(c);
        Assert.Null(rechazada.FolioEleventa);

        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado);

        var pantalla = await EsperarAsync(() => PantallaAsync(c), p => p.Atorado is not null);
        Assert.Equal(rechazada.VentaId, await VentaDelEnvioAsync(servicio, pantalla.Atorado!.EnvioId));
        Assert.Contains(pantalla.Avisos, a => a.Contains(LectorEleventa.ContestoConError));
        Assert.DoesNotContain(pantalla.Avisos, a => a.Contains("violation", StringComparison.OrdinalIgnoreCase) || a.Contains("CK_DE_LA_PRUEBA"));

        // Quien vende lo ve, sin el botón: avisa al dueño.
        var delCajero = await RespaldoDelPilotoTests.PanelAsync(servicio, c, c.Sesion);
        var texto = PantallaSinNavegador.Limpio(await delCajero.TextoAsync());
        Assert.Contains("detiene lo que sigue", texto);
        Assert.Contains("avísale al dueño", texto);
        Assert.DoesNotContain("Dejarla por revisar", texto);

        // El dueño la deja por revisar desde el panel, y lo de detrás entra.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var delDueno = await RespaldoDelPilotoTests.PanelAsync(servicio, c, dueno);
        await delDueno.OprimirAsync(await delDueno.BotonAsync("Dejarla por revisar"));
        await delDueno.EsperarAsync(t => t.Contains("por revisar") && !t.Contains("detiene lo que sigue"), "que quede por revisar");
        Assert.Empty(delDueno.Errores);
        // El estado se lee en dos consultas: las dos.
        var estado = await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0 && x.PorAtender.All(e => e.Estado != EstadoEnvioEleventa.Pendiente));
        Assert.Equal(0, estado.Pendientes);
        Assert.Equal(EstadoEnvioEleventa.PorRevisar, estado.PorAtender.Single().Estado);
        Assert.NotNull((await EnvioDeAsync(servicio, detras.VentaId!.Value)).TicketEleventa);
        Assert.Null((await PantallaAsync(c)).Atorado);
    }

    /// <summary>Sin turno abierto en eleventa tampoco: la venta se manda en cuanto lo abran, y el aviso dice qué hacer.</summary>
    [FactConFirebird]
    public async Task Sin_turno_abierto_en_eleventa_no_se_ofrece_dejarla_por_revisar()
    {
        var (e, _) = await EleventaAsync(abierta: false);
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);

        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado + 1);

        var pantalla = await PantallaAsync(c);
        Assert.Null(pantalla.Atorado);
        Assert.Contains(pantalla.Avisos, a => a.Contains(CorteEnElPiloto.AbreLaCajaEnEleventa));
    }

    /// <summary>
    /// Revisión de la auditoría: la clave que cambió o la ruta que ya no existe no son de la venta, son de la configuración y le pasan a
    /// todas. El aviso de pendientes dice la causa; ofrecer «Dejarla por revisar» manda por el camino equivocado.
    /// </summary>
    [FactConFirebird]
    public async Task Con_la_clave_mala_no_se_ofrece_dejarla_por_revisar()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor).Select(a => a.Item1 == "Eleventa:Clave" ? (a.Item1, "no-es-la") : a).ToArray());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);

        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado + 1);

        var pantalla = await EsperarAsync(() => PantallaAsync(c), p => p.Avisos.Any(a => a.Contains("no aceptó el usuario o la clave")));
        Assert.Null(pantalla.Atorado);
    }

    [FactConFirebird]
    public async Task Sin_la_base_en_esa_ruta_no_se_ofrece_dejarla_por_revisar()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor with { Base = "/firebird/data/no-existe.fdb" }));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);

        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado + 1);

        var pantalla = await EsperarAsync(() => PantallaAsync(c), p => p.Avisos.Any(a => a.Contains("no está la base")));
        Assert.Null(pantalla.Atorado);
    }

    /// <summary>Revisión de la auditoría: los intentos que se cortaron por la red no son rechazos: tras tres cortes, un solo rechazo no dice «detiene lo que sigue».</summary>
    [FactConFirebird]
    public async Task Los_intentos_sin_red_no_cuentan_como_rechazos()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await e.EjecutarAsync("ALTER TABLE VENTATICKETS ADD CONSTRAINT CK_DE_LA_PRUEBA CHECK (TOTAL IS NULL OR TOTAL < 1)");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor with { Puerto = 1 }));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);
        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado);

        // Regresó la red (otro puerto es la misma base).
        using var conRed = servicio.Fabrica.WithWebHostBuilder(b =>
        {
            foreach (var (clave, valor) in Ajustes(e.Servidor))
                b.UseSetting(clave, valor);
        });
        var c2 = c with { Http = conRed.CreateClient() };
        async Task<EnvioEleventaDto> RechazadaOtraVezAsync(int intentos)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await c2.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
            return (await EsperarAsync(() => EnviosAsync(c2), x => x.PorAtender.First() is { Error: { } error } p && p.Intentos >= intentos && error.Contains(LectorEleventa.ContestoConError))).PorAtender.First();
        }
        var intentos = (await RechazadaOtraVezAsync(EnvioVentasEleventa.IntentosParaAtorado + 1)).Intentos;
        Assert.Null((await PantallaAsync(c2)).Atorado);

        for (var i = 1; i < EnvioVentasEleventa.IntentosParaAtorado; i++)
            intentos = (await RechazadaOtraVezAsync(intentos + 1)).Intentos;
        Assert.NotNull((await EsperarAsync(() => PantallaAsync(c2), p => p.Atorado is not null)).Atorado);
    }

    [Fact]
    public async Task Sin_red_no_se_ofrece_dejarla_por_revisar_se_reintenta_sola()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);

        await HastaIntentosAsync(c, EnvioVentasEleventa.IntentosParaAtorado + 1);

        var pantalla = await PantallaAsync(c);
        Assert.Null(pantalla.Atorado);
        Assert.Contains(pantalla.Avisos, a => a.Contains("por mandar a eleventa"));
    }

    [Fact]
    public async Task Si_la_hora_regreso_la_cancelacion_espera_a_su_venta_y_la_bandeja_no_se_atora()
    {
        var ajustes = Ajustes().Select(a => a.Item1 == "Eleventa:SegundosAlCobrar" ? (a.Item1, "0") : a).ToArray();
        await using var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Error")));
        // Con el candado: que la vuelta no esté a medias. La venta nunca se intentó y la cancelación quedó antes, como con la hora atrás.
        var envios = servicio.Fabrica.Services.GetRequiredService<EnvioVentasEleventa>();
        using (await envios.CandadoAsync(default))
        {
            await using var db = servicio.Base();
            var venta = await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId);
            var cancelacion = await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Cancelacion && x.VentaId == ticket.VentaId);
            (venta.Estado, venta.Intentos, venta.Error, cancelacion.Estado) = (EstadoEnvioEleventa.Pendiente, 0, null, EstadoEnvioEleventa.Pendiente);
            cancelacion.CreadoUtc = venta.CreadoUtc.AddMinutes(-3);
            await db.SaveChangesAsync();
        }

        var estado = await MandarAsync(c);

        // Se canceló aquí sin haberse intentado: se descarta, y su cancelación con ella.
        Assert.Equal(0, estado.Pendientes);
        Assert.Equal(EstadoEnvioEleventa.Descartada, (await EnvioDeAsync(servicio, ticket.VentaId!.Value)).Estado);
    }

    /// <summary>
    /// Auditoría del 2026-11-04: un apagón justo después de que eleventa confirmó dejaba el envío con Intentos en 0, y cancelar la venta ya
    /// no la buscaba allá. El intento queda guardado antes de escribir: aquí, durante una escritura que se queda sin contestar.
    /// </summary>
    [Fact]
    public async Task El_intento_queda_guardado_antes_de_escribir_en_eleventa()
    {
        // Un eleventa que acepta la conexión y se queda callado.
        using var callado = new TcpListener(IPAddress.Loopback, 0);
        callado.Start();
        var puerto = ((IPEndPoint)callado.LocalEndpoint).Port;
        var ajustes = Ajustes(new ServidorEleventa("127.0.0.1", @"C:\\eleventa\\PDVDATA.FDB", "SYSDBA", "x", puerto))
            .Select(a => a.Item1 == "Eleventa:SegundosAlCobrar" ? (a.Item1, "0.01") : a).ToArray();
        await using var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);

        var intentos = await EsperarAsync(async () => (await EnvioDeAsync(servicio, ticket.VentaId!.Value)).Intentos, n => n > 0);
        Assert.True(intentos > 0, "Mientras escribe, el intento ya está en la base.");
    }

    /// <summary>Auditoría del 2026-11-04: el aviso de un folio repetido no se quitaba nunca, y con algo siempre en el chip se aprende a ignorarlo. Dura lo que poder devolver.</summary>
    [Fact]
    public async Task El_aviso_del_folio_repetido_dura_lo_que_poder_devolver()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c);
        async Task EnviadaHaceAsync(int dias)
        {
            await using var db = servicio.Base();
            var envio = await db.EnviosAEleventa.SingleAsync(x => x.VentaId == ticket.VentaId);
            (envio.Estado, envio.FolioEleventa, envio.TicketEleventa, envio.EnviadaUtc, envio.FolioRepetido) = (EstadoEnvioEleventa.Enviada, 42, 500, DateTime.UtcNow.AddDays(-dias), true);
            await db.SaveChangesAsync();
        }

        await EnviadaHaceAsync(6);
        Assert.Contains((await PantallaAsync(c)).Avisos, a => a.Contains("folio que otra caja"));

        await EnviadaHaceAsync(8);
        Assert.DoesNotContain((await PantallaAsync(c)).Avisos, a => a.Contains("folio que otra caja"));
        Assert.Empty((await EnviosAsync(c)).PorAtender);
    }

    /// <summary>
    /// Auditoría del 2026-11-04: el chip preguntaba una sola vez si es piloto: si <c>Escribir</c> se prendía con la caja abierta, no salía
    /// hasta salir y volver a entrar.
    /// </summary>
    [Fact]
    public async Task El_chip_sale_aunque_la_caja_ya_estuviera_abierta_cuando_se_prendio_el_piloto()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes().Where(a => a.Item1 != "Eleventa:Clave").ToArray());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        // El servicio todavía no contesta que es piloto (ErrorCaja al preguntarle cuenta como que no), y después sí.
        var respondeQueSi = false;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        var clientePiloto = new ClienteCaja(new HttpClient(new AntesFalla(() => respondeQueSi, servicio.Fabrica.Server.CreateHandler())), conexion);
        var servicios = new ServiceCollection()
            .AddSingleton(clientePiloto)
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<PuenteEleventa>(new()
        {
            [nameof(PuenteEleventa.Caja)] = c.Datos, [nameof(PuenteEleventa.Sesion)] = c.Sesion, [nameof(PuenteEleventa.CadaCuanto)] = TimeSpan.FromMilliseconds(50),
        });
        await Task.Delay(200);
        Assert.DoesNotContain("Eleventa", await pantalla.TextoAsync());

        respondeQueSi = true;

        await pantalla.EsperarAsync(t => t.Contains("Eleventa"), "el chip de eleventa");
    }

    /// <summary>El servicio que todavía no contesta (y cuando <paramref name="ya"/>, sí).</summary>
    private sealed class AntesFalla(Func<bool> ya, HttpMessageHandler siguiente) : DelegatingHandler(siguiente)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage solicitud, CancellationToken ct) =>
            ya() ? base.SendAsync(solicitud, ct) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = JsonContent.Create(new ErrorDto("Todavía no.")),
            });
    }

    private static async Task<Guid?> VentaDelEnvioAsync(ServicioDePrueba servicio, Guid envio)
    {
        await using var db = servicio.Base();
        return await db.EnviosAEleventa.Where(x => x.Id == envio).Select(x => x.VentaId).SingleAsync();
    }

    private static async Task<EnvioAEleventa> EnvioDeAsync(ServicioDePrueba servicio, Guid venta)
    {
        await using var db = servicio.Base();
        return await db.EnviosAEleventa.AsNoTracking().SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == venta);
    }
}
