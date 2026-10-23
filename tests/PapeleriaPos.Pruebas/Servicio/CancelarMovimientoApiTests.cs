using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Cancelar una entrada o salida equivocada por la API (F7/F8, como en eleventa; 2026-10-22): la lista del turno dice cuáles se
/// cancelan, la cancelación lleva motivo y el permiso (el cajero, de inicio, «No»), regresa la lista al día y el corte Z ya no
/// la cuenta y dice cuántas se cancelaron. En el piloto con eleventa se ven, pero no se cancelan, y lo dice.
/// </summary>
public sealed class CancelarMovimientoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, TurnoDto Turno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> RechazoAsync(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.Equal(estado, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    /// <summary>La caja con el turno del cajero (uno nuevo si el anterior ya tuvo su corte).</summary>
    private static async Task<Caja> CajaAsync(ServicioDePrueba s)
    {
        var http = s.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var cajero = await Entrar("cajero", "1111");
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return new Caja(http, caja, cajero, await Entrar("encargado", "2222"), turno);
    }

    private static async Task<MovimientoDelTurnoDto> RegistrarAsync(Caja c, TipoMovimientoCaja tipo, long monto, string motivo)
    {
        var id = Guid.NewGuid();
        await Leer<ComprobanteMovimientoDto>(await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/movimientos",
            new SolicitudMovimiento(tipo, monto, motivo, c.Cajero.UsuarioId, id)));
        return (await ListaAsync(c)).Movimientos.Single(m => m.Id == id);
    }

    private static async Task<MovimientosDelTurnoDto> ListaAsync(Caja c) =>
        (await c.Http.GetFromJsonAsync<MovimientosDelTurnoDto>($"api/turnos/{c.Turno.Id}/movimientos"))!;

    private static Task<HttpResponseMessage> CancelarAsync(Caja c, MovimientoDelTurnoDto m, SesionDto quien, string motivo) =>
        c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/movimientos/{m.Id}/cancelacion",
            new SolicitudCancelarMovimiento(Guid.NewGuid(), quien.UsuarioId, c.Datos.Id, motivo));

    [Fact]
    public async Task Se_cancela_con_motivo_y_su_permiso_y_el_corte_dice_que_se_cancelo()
    {
        var c = await CajaAsync(servicio);
        var entrada = await RegistrarAsync(c, TipoMovimientoCaja.Entrada, 20000, "Cambio en monedas");
        var salida = await RegistrarAsync(c, TipoMovimientoCaja.Salida, 5000, "Garrafón");
        Assert.Equal((true, true, false), (entrada.SeCancela, salida.SeCancela, (await ListaAsync(c)).Piloto));
        Assert.Equal(("Cajero de prueba", "Cambio en monedas", false), (entrada.Usuario, entrada.Motivo, entrada.Cancelado));

        // El cajero, de inicio, no puede; sin motivo, no se cancela.
        Assert.Equal("no-permitido", (await RechazoAsync(await CancelarAsync(c, entrada, c.Cajero, "Me equivoqué"), HttpStatusCode.Forbidden)).Codigo);
        await RechazoAsync(await CancelarAsync(c, entrada, c.Encargado, " "), HttpStatusCode.BadRequest);

        var lista = await Leer<MovimientosDelTurnoDto>(await CancelarAsync(c, entrada, c.Encargado, "Se registró dos veces"));
        var cancelada = lista.Movimientos.Single(m => m.Id == entrada.Id);
        Assert.Equal((true, false, "Encargado de prueba", "Se registró dos veces"),
            (cancelada.Cancelado, cancelada.SeCancela, cancelada.CanceladoPor, cancelada.MotivoCancelacion));
        Assert.True(lista.Movimientos.Single(m => m.Id == salida.Id).SeCancela);
        Assert.Equal("Esa entrada ya se canceló.",
            (await RechazoAsync(await CancelarAsync(c, entrada, c.Encargado, "Otra vez"), HttpStatusCode.BadRequest)).Mensaje);
        // De otro turno: no es de este.
        var ajena = await c.Http.PostAsJsonAsync($"api/turnos/{Guid.NewGuid()}/movimientos/{salida.Id}/cancelacion",
            new SolicitudCancelarMovimiento(Guid.NewGuid(), c.Encargado.UsuarioId, c.Datos.Id, "x"));
        Assert.NotEqual(HttpStatusCode.OK, ajena.StatusCode);

        // El corte Z: la entrada cancelada no cuenta, y dice cuántas se cancelaron (la pantalla y el papel).
        var esperado = (await c.Http.GetFromJsonAsync<EfectivoCajaDto>($"api/turnos/{c.Turno.Id}/efectivo"))!.EsperadoCentavos;
        var corte = await Leer<CorteDto>(await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/corte-z", new SolicitudCorteZ(c.Encargado.UsuarioId, esperado)));
        Assert.Equal((1, 0, 0L), (corte.EntradasCanceladas, corte.SalidasCanceladas, corte.DiferenciaCentavos ?? 0));
        Assert.Equal(esperado, corte.EsperadoCentavos);

        // Con el corte, ya ninguna se cancela.
        Assert.All((await ListaAsync(c)).Movimientos, m => Assert.False(m.SeCancela));
        var despues = await RechazoAsync(await CancelarAsync(c, salida, c.Encargado, "Me equivoqué"), HttpStatusCode.BadRequest);
        Assert.Equal("Una entrada o salida se cancela en su caja y en su mismo turno, antes de su corte.", despues.Mensaje);
    }

    /// <summary>Lo que corre en la caja 2 del piloto: la entrada va a la bandeja hacia eleventa (que no contesta) y no se cancela.</summary>
    [Fact]
    public async Task En_el_piloto_se_ven_pero_no_se_cancelan_y_dice_por_que()
    {
        await using var piloto = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
            ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"),
            ("Eleventa:RespaldoObligatorio", "false"));
        var c = await CajaAsync(piloto);
        var entrada = await RegistrarAsync(c, TipoMovimientoCaja.Entrada, 20000, "Cambio en monedas");

        Assert.True((await ListaAsync(c)).Piloto);
        Assert.False(entrada.SeCancela);
        var rechazo = await RechazoAsync(await CancelarAsync(c, entrada, c.Encargado, "Me equivoqué"), HttpStatusCode.BadRequest);
        Assert.Equal(PilotoConEleventa.NoSeCancelaMovimiento, rechazo.Mensaje);
        Assert.False((await ListaAsync(c)).Movimientos.Single(m => m.Id == entrada.Id).Cancelado);
    }
}
