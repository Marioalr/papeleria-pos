using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Pruebas.Interfaz;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los respaldos de eleventa en la caja 2 (docs/migracion-eleventa.md, «Los respaldos de eleventa»), contra un eleventa que
/// no contesta y con un gbak de mentiras: el respaldo del día antes de lo primero que se manda (sin él, por omisión, no se
/// manda nada y la pantalla dice por qué), y el puente detenido, que solo se reanuda a mano.
/// </summary>
public sealed class RespaldoDelPilotoTests
{
    private static (string, string)[] ConRespaldo(GbakDePrueba gbak, bool obligatorio = true, ServidorEleventa? e = null, bool leer = false) =>
    [
        .. Ajustes(e, leer), ("Eleventa:Gbak", gbak.Ruta), ("Eleventa:CarpetaRespaldos", gbak.Respaldos),
        ("Eleventa:RespaldoObligatorio", obligatorio ? "true" : "false"),
    ];

    private static Task<ServicioDePrueba> ServicioAsync(GbakDePrueba gbak, (string, string)[] ajustes) =>
        ServicioDePrueba.CrearAsync(s => s.AddSingleton<IEjecutorGbak>(gbak), ajustes);

    private static async Task<PuenteEleventaDto> PantallaAsync(Caja c) => await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));

    [Fact]
    public async Task El_respaldo_del_dia_va_antes_de_lo_primero_que_se_manda_y_una_sola_vez()
    {
        using var gbak = new GbakDePrueba();
        await using var servicio = await ServicioAsync(gbak, ConRespaldo(gbak));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        int? intentosAlRespaldar = null;
        gbak.AlRespaldar = async () =>
        {
            await using var db = servicio.Base();
            intentosAlRespaldar = await db.EnviosAEleventa.SumAsync(e => e.Intentos);
        };

        // El cobro no espera a gbak: el ticket sale con el folio de la caja, y la bandeja respalda y después manda.
        var ticket = await CobrarAsync(c);
        var envios = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(e => e.Intentos > 0));

        Assert.Null(ticket.FolioEleventa);
        Assert.Equal(0, intentosAlRespaldar);
        Assert.Single(gbak.Llamadas);
        Assert.True(File.Exists(Path.Combine(gbak.Respaldos, $"eleventa-{HoraLocal(0):yyyy-MM-dd}.fbk")));
        // Lo que sigue en el día ya no respalda otra vez.
        Assert.Equal(System.Net.HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Single().Intentos >= 2);
        Assert.Single(gbak.Llamadas);
        Assert.Null((await PantallaAsync(c)).Respaldo);
        Assert.Equal(EstadoEnvioEleventa.Pendiente, envios.PorAtender.Single().Estado);
    }

    [Fact]
    public async Task Sin_el_respaldo_del_dia_no_se_manda_nada_a_eleventa_y_la_caja_dice_por_que()
    {
        using var gbak = new GbakDePrueba { Bien = false };
        await using var servicio = await ServicioAsync(gbak, ConRespaldo(gbak));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);
        var pantalla = await EsperarAsync(() => PantallaAsync(c), p => p.Respaldo?.StartsWith("No se manda nada a eleventa hasta respaldarla") == true);

        Assert.Null(ticket.FolioEleventa);
        Assert.Equal("No se manda nada a eleventa hasta respaldarla: gbak no pudo respaldar eleventa (código 1): gbak: ERROR:Unable to complete network "
            + "request to host \"10.0.0.10\". Se vuelve a intentar cada 5 minutos.", pantalla.Respaldo);
        // La venta espera en la bandeja sin que se haya intentado escribir.
        Assert.Equal((EstadoEnvioEleventa.Pendiente, 0), (await EnviosAsync(c)).PorAtender.Select(e => (e.Estado, e.Intentos)).Single());
        Assert.Contains("No se manda nada a eleventa hasta respaldarla", PantallaSinNavegador.Limpio(await (await PanelAsync(servicio, c, c.Sesion)).TextoAsync()));
    }

    [Fact]
    public async Task Si_el_respaldo_no_es_obligatorio_se_manda_aunque_no_se_haya_podido_y_la_caja_lo_avisa()
    {
        using var gbak = new GbakDePrueba { Bien = false };
        await using var servicio = await ServicioAsync(gbak, ConRespaldo(gbak, obligatorio: false));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        await CobrarAsync(c);
        await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(e => e.Intentos > 0));

        Assert.Single(gbak.Llamadas);
        Assert.StartsWith("Hoy no se pudo respaldar eleventa y se le manda sin respaldo: gbak no pudo respaldar eleventa (código 1)", (await PantallaAsync(c)).Respaldo);
    }

    [Fact]
    public async Task Sin_la_ruta_de_gbak_y_con_el_respaldo_obligatorio_no_se_manda_nada()
    {
        using var gbak = new GbakDePrueba();
        await using var servicio = await ServicioAsync(gbak, [.. Ajustes(), ("Eleventa:RespaldoObligatorio", "true")]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        Assert.Equal("No se manda nada a eleventa: falta la ruta de gbak.exe para respaldarla antes (Eleventa:Gbak).", (await PantallaAsync(c)).Respaldo);
        Assert.Empty(gbak.Llamadas);
    }

    [Fact]
    public async Task Con_el_puente_detenido_no_se_manda_nada_y_solo_se_reanuda_a_mano_con_eleventa_contestando()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await using (var db = servicio.Base())
        {
            db.Set<DetencionDelPuente>().Add(new DetencionDelPuente
            {
                DetectadaUtc = DateTime.UtcNow.AddMinutes(-5), Pruebas = "ya no está el ticket #4521 que mandó el 16/10 10:05",
            });
            await db.SaveChangesAsync();
        }

        var ticket = await CobrarAsync(c);
        var pantalla = await PantallaAsync(c);

        // No se intentó mandar: el ticket sale con el folio de la caja y la venta espera.
        Assert.Null(ticket.FolioEleventa);
        Assert.Equal(0, (await EnviosAsync(c)).PorAtender.Single().Intentos);
        Assert.Contains("eleventa ya no tiene lo que le mandó la caja 2 (¿se restauró un respaldo?): ya no está el ticket #4521 que mandó el 16/10 10:05",
            pantalla.Detenido);
        // El cajero no lo reanuda; el dueño sí puede, pero sin eleventa no se sabe qué se perdió.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        Assert.False((await c.Http.PostAsJsonAsync("api/eleventa/reanudar", new SolicitudReanudarPuente(c.Sesion.UsuarioId, c.Datos.Id, null))).IsSuccessStatusCode);
        var sinEleventa = await RechazoAsync(await c.Http.PostAsJsonAsync("api/eleventa/reanudar", new SolicitudReanudarPuente(dueno.UsuarioId, c.Datos.Id, null)));
        Assert.EndsWith("Sin eleventa no se sabe qué se perdió: reanuda cuando conteste.", sinEleventa);
        Assert.NotNull((await PantallaAsync(c)).Detenido);
        await using (var db = servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Resultado == ResultadoBitacora.Negado));

        // En la pantalla de la caja 2: el cajero lo ve; quien tiene «Configuración de la tienda», también el botón.
        var delCajero = PantallaSinNavegador.Limpio(await (await PanelAsync(servicio, c, c.Sesion)).TextoAsync());
        var delDueno = PantallaSinNavegador.Limpio(await (await PanelAsync(servicio, c, dueno)).TextoAsync());
        Assert.Contains("Eleventa · detenido", delCajero);
        Assert.Contains("ya no está el ticket #4521", delCajero);
        Assert.DoesNotContain("Ya revisé eleventa: reanudar", delCajero);
        Assert.Contains("Ya revisé eleventa: reanudar", delDueno);
    }

    /// <summary>El aviso de eleventa en el estado de arriba de la caja 2, abierto.</summary>
    internal static async Task<PantallaSinNavegador> PanelAsync(ServicioDePrueba servicio, Caja c, SesionDto sesion)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<PuenteEleventa>(new() { [nameof(PuenteEleventa.Caja)] = c.Datos, [nameof(PuenteEleventa.Sesion)] = sesion });
        await pantalla.EsperarAsync(t => t.Contains("Eleventa"), "el aviso de eleventa");
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Eleventa"));
        Assert.Empty(pantalla.Errores);
        return pantalla;
    }
}
