using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El piloto en la caja 2 solo cobra en efectivo (decisión del dueño, 2026-10-12): mientras la caja manda sus ventas a
/// eleventa (Eleventa:Escribir), el servicio rechaza la tarjeta, la transferencia, el crédito y el pago mixto, lo devuelto
/// que no sale en efectivo, los abonos y devolver saldos a favor (no lo que llega otra vez y ya estaba); la caja lo sabe
/// por /api/estado, también sin conexión, y dice lo mismo que el servicio. Sin el piloto, todo sigue igual. Solo para
/// convivir con eleventa: se quita al dejarlo.
/// </summary>
public sealed class SoloEfectivoEnElPilotoTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    /// <summary>La caja manda a un eleventa que no contesta (nadie escucha en ese puerto): las ventas esperan en la bandeja.</summary>
    private static readonly (string, string)[] Piloto =
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
        ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"),
        // Sin respaldo propio de eleventa (lo prueban RespaldoDeEleventaTests y RespaldoDelPilotoTests).
        ("Eleventa:RespaldoObligatorio", "false"),
    ];

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, TurnoDto Turno);

    private static async Task<Caja> AbrirAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var cajero = await Entrar("cajero", "1111");
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)));
        return new Caja(http, caja, cajero, await Entrar("encargado", "2222"), await Entrar("dueno", "3333"), turno);
    }

    /// <summary>Un cliente con lo que el dueño le anota en su cuenta (positivo: debe; negativo: a favor) y, si se pide, crédito.</summary>
    private static async Task<ClienteRegistradoDto> ClienteAsync(Caja c, string nombre, long saldo = 0, long limite = 0)
    {
        var cliente = await Leer<ClienteRegistradoDto>(await c.Http.PostAsJsonAsync("api/clientes",
            new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, new ClienteRegistradoDto(Guid.Empty, nombre, 0, null, true))));
        if (limite > 0)
            await Leer<ClienteRegistradoDto>(await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
                new SolicitudCreditoCliente(c.Dueno.UsuarioId, c.Datos.SucursalId, limite, 30)));
        if (saldo != 0)
            await Leer<CuentaClienteDto>(await c.Http.PostAsJsonAsync($"api/clientes/{cliente.Id}/ajustes",
                new SolicitudAjusteCredito(Ids.Nuevo(), c.Dueno.UsuarioId, c.Datos.SucursalId, saldo, "Saldo inicial de la libreta")));
        return cliente;
    }

    private static async Task<long> SaldoAsync(Caja c, ClienteRegistradoDto cliente) =>
        (await c.Http.GetFromJsonAsync<CuentaClienteDto>($"api/clientes/{cliente.Id}/credito"))!.Credito.SaldoCentavos;

    /// <summary>Un artículo común de $25 con los cobros que se pidan.</summary>
    private static Task<HttpResponseMessage> CobrarAsync(Caja c, params CobroSolicitado[] cobros) =>
        c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)], cobros));

    private static async Task<TicketDto> EnEfectivoAsync(Caja c) => await Leer<TicketDto>(await CobrarAsync(c, new CobroSolicitado(FormaPago.Efectivo, 5000)));

    /// <summary>Lo que se cobró sin conexión con tarjeta (antes de saber del piloto) y sube con la bandeja: ya pasó, así que se guarda.</summary>
    private static async Task<Guid> ConTarjetaSinConexionAsync(Caja c)
    {
        var paquete = (await c.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{c.Datos.Id}/paquete"))!;
        var venta = new VentaContingenciaDto(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(null, null, "Recuerdo", 1000, 2500)], [new CobroSolicitado(FormaPago.Tarjeta, 2500, "4821")]);
        var subida = await c.Http.PostAsJsonAsync("api/bandeja/ventas", venta);
        Assert.True(subida.IsSuccessStatusCode, await subida.Content.ReadAsStringAsync());
        return venta.Id;
    }

    /// <summary>Lo que ve la ventana de devoluciones al buscar el folio del ticket.</summary>
    private static async Task<VentaParaDevolverDto> PorFolioAsync(Caja c, Guid ventaId)
    {
        var ticket = (await c.Http.GetFromJsonAsync<TicketDto>($"api/ventas/{ventaId}/ticket"))!;
        return (await c.Http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"))!;
    }

    /// <summary>Devuelve todo el ticket (su única línea), como lo hace el encargado.</summary>
    private static async Task<HttpResponseMessage> DevolverAsync(Caja c, Guid ventaId)
    {
        var venta = await PorFolioAsync(c, ventaId);
        return await c.Http.PostAsJsonAsync($"api/ventas/{ventaId}/devoluciones", new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id,
            "No le sirvió", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)]));
    }

    private static Task<HttpResponseMessage> CancelarAsync(Caja c, Guid ventaId) =>
        c.Http.PostAsJsonAsync($"api/ventas/{ventaId}/cancelacion", new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió"));

    [Fact]
    public async Task En_el_piloto_solo_se_cobra_en_efectivo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);

        var tarjeta = await CobrarAsync(c, new CobroSolicitado(FormaPago.Tarjeta, 2500, "4821"));
        var transferencia = await CobrarAsync(c, new CobroSolicitado(FormaPago.Transferencia, 2500, "REF9"));
        var credito = await CobrarAsync(c, new CobroSolicitado(FormaPago.Credito, 2500));
        var mixto = await CobrarAsync(c, new CobroSolicitado(FormaPago.Tarjeta, 1000, "4821"), new CobroSolicitado(FormaPago.Efectivo, 2000));
        var efectivo = await EnEfectivoAsync(c);

        foreach (var rechazado in new[] { tarjeta, transferencia, credito, mixto })
            Assert.Equal(SoloEfectivoEnElPiloto.NoSeCobra, await RechazoAsync(rechazado));
        Assert.True((await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.PilotoEleventa);
        // Solo la de efectivo quedó, y va a eleventa.
        await using (var db = servicio.Base())
            Assert.Equal([efectivo.VentaId!.Value], db.Ventas.Where(v => v.TurnoId == c.Turno.Id).Select(v => v.Id).ToList());
        var envios = (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
        Assert.Equal((1, 0), (envios.Pendientes, envios.PorRevisar));
    }

    [Fact]
    public async Task En_el_piloto_solo_se_devuelve_en_efectivo_lo_cobrado_en_efectivo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var paraDevolver = await EnEfectivoAsync(c);
        var paraCancelar = await EnEfectivoAsync(c);
        var conTarjeta = await ConTarjetaSinConexionAsync(c);

        var enLaVentana = (await PorFolioAsync(c, paraDevolver.VentaId!.Value), await PorFolioAsync(c, conTarjeta));
        var devolucion = await Leer<ComprobanteDevolucionDto>(await DevolverAsync(c, paraDevolver.VentaId!.Value));
        var cancelacion = await Leer<ComprobanteDevolucionDto>(await CancelarAsync(c, paraCancelar.VentaId!.Value));
        var devolverTarjeta = await RechazoAsync(await DevolverAsync(c, conTarjeta));
        var cancelarTarjeta = await RechazoAsync(await CancelarAsync(c, conTarjeta));

        Assert.Equal((2500L, 0L), (devolucion.ReembolsoEfectivoCentavos, devolucion.AlCreditoCentavos));
        Assert.Equal((2500L, 0L), (cancelacion.ReembolsoEfectivoCentavos, cancelacion.TarjetaCentavos));
        Assert.All(new[] { devolverTarjeta, cancelarTarjeta }, error => Assert.Equal(
            "Mientras dure el piloto con eleventa, esta caja solo devuelve en efectivo lo que se cobró en efectivo, y este ticket se cobró con tarjeta: "
            + "no se devuelve aquí.", error));
        // La ventana de devoluciones lo sabe al buscar el folio, con el mismo texto: no ofrece cancelar ni devolver.
        Assert.Null(enLaVentana.Item1.NoSeDevuelveEnElPiloto);
        Assert.Equal(devolverTarjeta, enLaVentana.Item2.NoSeDevuelveEnElPiloto);
        var paraEleventa = (await c.Http.GetFromJsonAsync<TicketDto>($"api/ventas/{conTarjeta}/ticket"))!;
        Assert.Equal(EstadoVenta.Pagada, (await c.Http.GetFromJsonAsync<VentaParaDevolverDto>(
            $"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{paraEleventa.Folio}"))!.Estado);
    }

    [Fact]
    public async Task En_el_piloto_un_ticket_fiado_o_por_transferencia_no_se_devuelve_y_su_cuenta_no_se_mueve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var cliente = await ClienteAsync(c, "Piloto: fiado", limite: 100000);
        // Lo que se cobró sin conexión (antes de saber del piloto): una parte por transferencia y lo demás fiado.
        var paquete = (await c.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{c.Datos.Id}/paquete"))!;
        var venta = new VentaContingenciaDto(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(null, null, "Recuerdo", 1000, 2500)],
            [new CobroSolicitado(FormaPago.Transferencia, 1000, "REF9"), new CobroSolicitado(FormaPago.Credito, 1500)],
            ClienteId: cliente.Id, SaldoConocidoCentavos: 0);
        var subida = await c.Http.PostAsJsonAsync("api/bandeja/ventas", venta);
        Assert.True(subida.IsSuccessStatusCode, await subida.Content.ReadAsStringAsync());
        Assert.Equal(1500, await SaldoAsync(c, cliente));

        var enLaVentana = await PorFolioAsync(c, venta.Id);
        var devolver = await RechazoAsync(await DevolverAsync(c, venta.Id));
        var cancelar = await RechazoAsync(await CancelarAsync(c, venta.Id));

        const string Esperado = "Mientras dure el piloto con eleventa, esta caja solo devuelve en efectivo lo que se cobró en efectivo, "
            + "y este ticket se cobró por transferencia y a crédito o con saldo a favor: no se devuelve aquí.";
        Assert.Equal((Esperado, Esperado, Esperado), (enLaVentana.NoSeDevuelveEnElPiloto, devolver, cancelar));
        Assert.Equal(1500, await SaldoAsync(c, cliente));
        Assert.Equal(EstadoVenta.Pagada, (await PorFolioAsync(c, venta.Id)).Estado);
    }

    [Fact]
    public async Task Lo_que_llega_otra_vez_ya_en_el_piloto_regresa_y_un_abono_en_efectivo_se_cancela()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);
        var debe = await ClienteAsync(c, "Piloto: debe", saldo: 30000);
        var aFavor = await ClienteAsync(c, "Piloto: a favor", saldo: -15000);
        var enEfectivo = new SolicitudAbono(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, debe.Id, FormaPago.Efectivo, 10000, 10000);
        var conTarjeta = new SolicitudAbono(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, debe.Id, FormaPago.Tarjeta, 10000, 0, "4821");
        var devuelto = new SolicitudDevolverSaldo(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Encargado.UsuarioId, null);
        var abono = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync("api/abonos", enEfectivo));
        var abonoConTarjeta = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync("api/abonos", conTarjeta));
        var saldo = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync($"api/clientes/{aFavor.Id}/saldo-devuelto", devuelto));

        // Como si el servicio se reiniciara con Eleventa:Escribir y la caja no hubiera recibido esas respuestas: las manda
        // otra vez con el mismo Id. Ya pasaron: sale el mismo comprobante (si se rechazaran, la caja creería que no quedaron).
        servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>().Escribir = true;
        var otraVez = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync("api/abonos", enEfectivo));
        var otraVezSaldo = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync($"api/clientes/{aFavor.Id}/saldo-devuelto", devuelto));
        var nuevo = await RechazoAsync(await c.Http.PostAsJsonAsync("api/abonos", enEfectivo with { Id = Ids.Nuevo() }));
        var nuevoSaldo = await RechazoAsync(await c.Http.PostAsJsonAsync($"api/clientes/{aFavor.Id}/saldo-devuelto", devuelto with { Id = Ids.Nuevo() }));
        // Cancelar el abono de este turno: el de efectivo sí (sale lo que entró); el de tarjeta se regresaría por su medio.
        Task<HttpResponseMessage> Cancelar(ComprobanteAbonoDto a) => c.Http.PostAsJsonAsync($"api/abonos/{a.AbonoId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Era de otro"));
        var cancelarTarjeta = await RechazoAsync(await Cancelar(abonoConTarjeta));
        var cancelado = await Leer<ComprobanteAbonoDto>(await Cancelar(abono));

        Assert.Equal((abono.AbonoId, abono.Folio), (otraVez.AbonoId, otraVez.Folio));
        Assert.Equal((saldo.AbonoId, saldo.Folio), (otraVezSaldo.AbonoId, otraVezSaldo.Folio));
        Assert.Equal((SoloEfectivoEnElPiloto.NoSeAbona, SoloEfectivoEnElPiloto.NoSeDevuelveSaldo), (nuevo, nuevoSaldo));
        Assert.Equal("Mientras dure el piloto con eleventa, esta caja solo devuelve en efectivo lo que se cobró en efectivo, "
            + "y este abono se cobró con tarjeta: no se cancela aquí.", cancelarTarjeta);
        Assert.True(cancelado.Cancelado);
        Assert.Equal((20000L, 0L), (await SaldoAsync(c, debe), await SaldoAsync(c, aFavor)));
    }

    [Fact]
    public async Task En_el_piloto_no_se_abona_ni_se_devuelve_saldo_a_favor()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var cliente = Ids.Nuevo();

        var abono = await c.Http.PostAsJsonAsync("api/abonos", new SolicitudAbono(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, cliente,
            FormaPago.Efectivo, 10000, 10000));
        var saldo = await c.Http.PostAsJsonAsync($"api/clientes/{cliente}/saldo-devuelto",
            new SolicitudDevolverSaldo(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Encargado.UsuarioId, null));

        Assert.Equal(SoloEfectivoEnElPiloto.NoSeAbona, await RechazoAsync(abono));
        Assert.Equal(SoloEfectivoEnElPiloto.NoSeDevuelveSaldo, await RechazoAsync(saldo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_el_piloto_se_cobra_y_se_devuelve_como_siempre(bool soloLeeEleventa)
    {
        // Solo leer el catálogo de eleventa (Eleventa:Activo) no es el piloto: no manda ventas.
        await using var servicio = soloLeeEleventa
            ? await ServicioDePrueba.CrearAsync(("Eleventa:Activo", "true"))
            : await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);

        var tarjeta = await Leer<TicketDto>(await CobrarAsync(c, new CobroSolicitado(FormaPago.Tarjeta, 2500, "4821")));
        var mixto = await Leer<TicketDto>(await CobrarAsync(c, new CobroSolicitado(FormaPago.Transferencia, 1000, "REF9"), new CobroSolicitado(FormaPago.Efectivo, 2000)));
        var devolucion = await Leer<ComprobanteDevolucionDto>(await DevolverAsync(c, mixto.VentaId!.Value));
        var cancelacion = await Leer<ComprobanteDevolucionDto>(await CancelarAsync(c, tarjeta.VentaId!.Value));

        Assert.False((await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.PilotoEleventa);
        Assert.Null((await PorFolioAsync(c, mixto.VentaId!.Value)).NoSeDevuelveEnElPiloto);
        Assert.Equal(2500L, cancelacion.TarjetaCentavos);
        Assert.True(devolucion.ReembolsoEfectivoCentavos > 0);
    }

    [Fact]
    public async Task La_caja_sabe_del_piloto_y_sin_conexion_tambien_cobra_solo_en_efectivo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        Assert.False(contingencia.PilotoEleventa);

        await contingencia.LatidoAsync();
        // La caja conoce su turno (sin conexión, cobra en él).
        Assert.Equal(c.Turno.Id, (await cliente.TurnoAsync(c.Datos.Id))!.Id);
        var enLinea = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Solicitud(c, new CobroSolicitado(FormaPago.Tarjeta, 2500, "4821"))));

        // Se apaga la caja principal: la caja sigue sabiendo del piloto, también si arranca otra vez sin ella.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var otraVez = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var tarjeta = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Solicitud(c, new CobroSolicitado(FormaPago.Tarjeta, 2500, "4821"))));
        var mixto = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(
            Solicitud(c, new CobroSolicitado(FormaPago.Transferencia, 1000, "REF9"), new CobroSolicitado(FormaPago.Efectivo, 2000))));
        var abono = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.AbonarAsync(
            new SolicitudAbono(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, Ids.Nuevo(), FormaPago.Efectivo, 10000, 10000)));
        var efectivo = await cliente.CobrarAsync(Solicitud(c, new CobroSolicitado(FormaPago.Efectivo, 5000)));

        Assert.True(await cliente.PilotoEleventaAsync());
        Assert.True(otraVez.PilotoEleventa);
        Assert.Equal(SoloEfectivoEnElPiloto.NoSeCobra, enLinea.Message);
        Assert.Equal((PilotoConEleventa.NoSeCobra, PilotoConEleventa.NoSeCobra), (tarjeta.Message, mixto.Message));
        Assert.Equal(PilotoConEleventa.NoSeAbona, abono.Message);
        Assert.Equal(2500, efectivo.TotalCobradoCentavos);
    }

    [Fact]
    public void La_caja_dice_lo_mismo_que_el_servicio()
    {
        // La interfaz no conoce el puente: copia sus textos. Con o sin conexión, la cajera lee lo mismo.
        Assert.Equal(SoloEfectivoEnElPiloto.NoSeCobra, PilotoConEleventa.NoSeCobra);
        Assert.Equal(SoloEfectivoEnElPiloto.NoSeAbona, PilotoConEleventa.NoSeAbona);
        Assert.Equal(SoloEfectivoEnElPiloto.NoSeDevuelveSaldo, PilotoConEleventa.NoSeDevuelveSaldo);
        Assert.Equal(SoloEfectivoEnElPiloto.SoloSeDevuelveEfectivo, PilotoConEleventa.SoloSeDevuelveEfectivo);
    }

    private static SolicitudCobro Solicitud(Caja c, params CobroSolicitado[] cobros) =>
        new(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)], cobros);
}
