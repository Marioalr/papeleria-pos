using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La caja de verdad con el crédito (fase 3, entrega 2, partes 2.3 y 2.4): lo que fía, los abonos y sus cancelaciones
/// quedan en su cuenta del turno, y si se va la conexión su corte dice lo mismo que el del servicio. Sin los saldos no se
/// fía sin conexión, y una respuesta perdida no fía dos veces.
/// </summary>
public sealed class CajaCreditoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-credito-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task Sin_conexion_el_corte_de_la_caja_cuenta_lo_fiado_y_los_abonos_como_el_servicio()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);

        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var registrado = await cliente.GuardarClienteAsync(new SolicitudGuardarCliente(encargado.UsuarioId, caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, "Caja: Arq. Ramírez", 0, null, true)));
        await cliente.GuardarCondicionesAsync(registrado.Id, new SolicitudCreditoCliente(dueno.UsuarioId, caja.SucursalId, 100000, 30));
        // El turno puede traer lo de otras pruebas de esta clase: se cuenta desde aquí.
        var inicio = (await local.GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;

        SolicitudCobro Venta(long total, params CobroSolicitado[] cobros) => new(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", total)], cobros, Ids.Nuevo(), ClienteId: registrado.Id);
        Task<ComprobanteAbonoDto> Abonar(FormaPago forma, long? monto, long recibido = 0) => cliente.AbonarAsync(new SolicitudAbono(Ids.Nuevo(), caja.Id,
            turno.Id, cajero.UsuarioId, registrado.Id, forma, monto, recibido, forma == FormaPago.Efectivo ? null : "REF-1"));
        var cancelacion = new SolicitudCancelacion(encargado.UsuarioId, caja.Id, "Se arrepintió");

        // Una parte en efectivo y el resto fiado; otra toda fiada y otra con tarjeta y transferencia, que se cancelan.
        var fiado = await cliente.CobrarAsync(Venta(13745, new CobroSolicitado(FormaPago.Efectivo, 10000), new CobroSolicitado(FormaPago.Credito, 3745)));
        var todoFiado = await cliente.CobrarAsync(Venta(5000, new CobroSolicitado(FormaPago.Credito, 5000)));
        await cliente.CancelarAsync(todoFiado.VentaId!.Value, cancelacion);
        var conTarjeta = await cliente.CobrarAsync(Venta(8000, new CobroSolicitado(FormaPago.Tarjeta, 5000, "T-1"),
            new CobroSolicitado(FormaPago.Transferencia, 3000, "R-1")));
        await cliente.CancelarAsync(conTarjeta.VentaId!.Value, cancelacion);

        // Abonos: en efectivo, con tarjeta, uno por transferencia que se cancela, y liquidar al peso ($7.45 → $7).
        await Abonar(FormaPago.Efectivo, 2000, 5000);
        await Abonar(FormaPago.Tarjeta, 1000);
        var equivocado = await Abonar(FormaPago.Transferencia, 500);
        await cliente.CancelarAbonoAsync(equivocado.AbonoId, turno.Id, new SolicitudCancelacion(encargado.UsuarioId, caja.Id, "Era de otro"));
        var liquida = await Abonar(FormaPago.Efectivo, null, 1000);

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var corteLocal = await cliente.CorteXAsync(turno.Id, cajero.UsuarioId);
        interruptor.Caido = false;
        var resumen = (await local.GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;

        Assert.Equal(3745, fiado.CreditoCentavos);
        Assert.Equal((745L, -45L, 0L), (liquida.MontoCentavos, liquida.RedondeoCentavos, liquida.SaldoCentavos));
        Assert.True(corteLocal.EnContingencia);
        Assert.Equal((3745L, 3, 2700L, 1000L, 0L), (resumen.CreditoCentavos - inicio.CreditoCentavos, resumen.Abonos - inicio.Abonos,
            resumen.AbonosEfectivoCentavos - inicio.AbonosEfectivoCentavos, resumen.AbonosTarjetaCentavos - inicio.AbonosTarjetaCentavos,
            resumen.AbonosTransferenciaCentavos - inicio.AbonosTransferenciaCentavos));
        Assert.Equal(
            (resumen.Ventas, resumen.EfectivoVentasCentavos, resumen.TarjetaCentavos, resumen.TransferenciaCentavos, resumen.Cancelaciones,
                resumen.DevolucionesCentavos, resumen.RedondeoCentavos, resumen.EsperadoCentavos),
            (corteLocal.Ventas, corteLocal.EfectivoVentasCentavos, corteLocal.TarjetaCentavos, corteLocal.TransferenciaCentavos, corteLocal.Cancelaciones,
                corteLocal.DevolucionesCentavos, corteLocal.RedondeoCentavos, corteLocal.EsperadoCentavos));
        Assert.Equal(
            (resumen.CreditoCentavos, resumen.Abonos, resumen.AbonosEfectivoCentavos, resumen.AbonosTarjetaCentavos, resumen.AbonosTransferenciaCentavos),
            (corteLocal.CreditoCentavos, corteLocal.Abonos, corteLocal.AbonosEfectivoCentavos, corteLocal.AbonosTarjetaCentavos,
                corteLocal.AbonosTransferenciaCentavos));
    }

    /// <summary>La caja de verdad contra el servicio, con su base local (la de esta prueba) y el interruptor de la red.</summary>
    private (ClienteCaja Cliente, Contingencia Contingencia, AlmacenLocal Almacen, Interruptor Red) Armar(CajaDto caja)
    {
        var red = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(red);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var almacen = new AlmacenLocal(_baseLocal);
        var contingencia = new Contingencia(almacen, http, conexion);
        return (new ClienteCaja(http, conexion, contingencia), contingencia, almacen, red);
    }

    private static bool PideSaldos(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/creditos");

    private static bool CobraEnLinea(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/ventas";

    [Fact]
    public async Task Sin_los_saldos_no_se_fia_ni_se_abona_sin_conexion()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var (cliente, contingencia, _, red) = Armar(caja);
        // Una caja que nunca recibió los saldos (por ejemplo, contra una caja principal de antes).
        red.Rechaza = PideSaldos;
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var registrado = await cliente.GuardarClienteAsync(new SolicitudGuardarCliente((await cliente.EntrarAsync("encargado", "2222")).UsuarioId,
            caja.SucursalId, new ClienteRegistradoDto(Guid.Empty, "Caja: Sin saldos", 0, null, true)));
        await contingencia.ActualizarPaqueteAsync();

        red.Caido = true;
        await contingencia.LatidoAsync();
        var fiar = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", 1000)], [new CobroSolicitado(FormaPago.Credito, 1000)], Ids.Nuevo(), ClienteId: registrado.Id)));
        var abonar = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.AbonarAsync(new SolicitudAbono(Ids.Nuevo(), caja.Id, turno.Id, cajero.UsuarioId,
            registrado.Id, FormaPago.Efectivo, 1000, 1000)));
        var cuentas = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CreditosAsync(caja.SucursalId));

        Assert.Null(contingencia.Saldos);
        Assert.All(new[] { fiar, abonar, cuentas }, e => Assert.StartsWith("Esta caja todavía no conoce los saldos", e.Message));
        Assert.Equal(0, contingencia.Pendientes);
    }

    [Fact]
    public async Task Si_se_pierde_la_respuesta_al_fiar_y_no_conoce_los_saldos_el_ticket_espera_la_conexion()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var (cliente, contingencia, _, red) = Armar(caja);
        red.Rechaza = PideSaldos;
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var registrado = await cliente.GuardarClienteAsync(new SolicitudGuardarCliente(encargado.UsuarioId, caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, "Caja: Respuesta perdida sin saldos", 0, null, true)));
        await cliente.GuardarCondicionesAsync(registrado.Id, new SolicitudCreditoCliente(dueno.UsuarioId, caja.SucursalId, 100000, 30));
        await contingencia.ActualizarPaqueteAsync();
        var ventaId = Ids.Nuevo();
        SolicitudCobro Venta(params CobroSolicitado[] cobros) => new(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", 20000)], cobros, ventaId, ClienteId: registrado.Id);

        // Se fía y se pierde la respuesta: el servicio ya lo guardó, pero la caja no lo sabe ni puede fiar sin conexión.
        red.PierdeRespuesta = CobraEnLinea;
        var perdido = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Venta(new CobroSolicitado(FormaPago.Credito, 20000))));
        red.PierdeRespuesta = null;
        red.Caido = true;
        await contingencia.LatidoAsync();
        // Sin conexión, ese ticket no se cobra de otra forma.
        var espera = await Assert.ThrowsAsync<ErrorCaja>(() =>
            cliente.CobrarAsync(Venta(new CobroSolicitado(FormaPago.Efectivo, 20000)), soloEnLinea: true));
        red.Caido = false;
        await contingencia.LatidoAsync();
        // Vuelve: se manda otra vez con el mismo Id y sale el mismo ticket, sin fiar dos veces.
        var ticket = await cliente.CobrarAsync(Venta(new CobroSolicitado(FormaPago.Credito, 20000)), soloEnLinea: true);

        Assert.Equal("fiado-sin-confirmar", perdido.Codigo);
        Assert.Equal("fiado-sin-confirmar", espera.Codigo);
        Assert.Equal(0, contingencia.Pendientes);
        Assert.Equal((ventaId, 20000L), (ticket.VentaId!.Value, ticket.CreditoCentavos));
        Assert.Equal(20000, (await cliente.CuentaClienteAsync(registrado.Id)).Credito.SaldoCentavos);
    }

    [Fact]
    public async Task Si_se_pierde_la_respuesta_al_fiar_se_hace_sin_conexion_con_el_mismo_Id_y_cuenta_una_vez()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var (cliente, contingencia, almacen, red) = Armar(caja);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var registrado = await cliente.GuardarClienteAsync(new SolicitudGuardarCliente(encargado.UsuarioId, caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, "Caja: Respuesta perdida con saldos", 0, null, true)));
        await cliente.GuardarCondicionesAsync(registrado.Id, new SolicitudCreditoCliente(dueno.UsuarioId, caja.SucursalId, 100000, 30));
        await contingencia.LatidoAsync();
        var ventaId = Ids.Nuevo();

        red.PierdeRespuesta = CobraEnLinea;
        var ticket = await cliente.CobrarAsync(new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", 20000)], [new CobroSolicitado(FormaPago.Credito, 20000)], ventaId, ClienteId: registrado.Id));
        red.PierdeRespuesta = null;
        // Sin conexión, la caja lo hizo con el mismo Id y con el saldo que conocía (sin contar dos veces lo que anotó al mandarlo).
        Assert.Equal(new CreditoTicketDto(20000, 20000, ticket.Credito!.Vence, Estimado: true), ticket.Credito);
        await contingencia.LatidoAsync();
        await contingencia.LatidoAsync();

        Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
        Assert.Equal(ventaId, ticket.VentaId);
        Assert.Equal(20000, (await cliente.CuentaClienteAsync(registrado.Id)).Credito.SaldoCentavos);
        Assert.Empty(almacen.CuentaPropia(registrado.Id));
        Assert.Equal(20000, contingencia.Credito(registrado.Id)!.SaldoCentavos);
    }
}
