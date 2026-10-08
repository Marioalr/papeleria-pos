using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>El crédito en línea (fase 3, entrega 2, parte 2.2): fiar con límite, abonar, devolver lo fiado y las condiciones.</summary>
public sealed class CreditoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno, TurnoDto Turno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> Error(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;

    private async Task<Caja> CajaAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var cajero = await Entrar("cajero", "1111");
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return new Caja(http, caja, cajero, await Entrar("encargado", "2222"), await Entrar("dueno", "3333"), turno);
    }

    /// <summary>Un cliente nuevo; con límite, se lo da el dueño.</summary>
    private static async Task<ClienteRegistradoDto> ClienteAsync(Caja c, string nombre, long limite = 0, int plazo = 30)
    {
        var cliente = await Leer<ClienteRegistradoDto>(await c.Http.PostAsJsonAsync("api/clientes",
            new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, new ClienteRegistradoDto(Guid.Empty, nombre, 0, null, true))));
        return limite == 0 && plazo == 30 ? cliente
            : await Leer<ClienteRegistradoDto>(await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
                new SolicitudCreditoCliente(c.Dueno.UsuarioId, c.Datos.SucursalId, limite, plazo)));
    }

    private static Task<HttpResponseMessage> CobrarAsync(Caja c, ClienteRegistradoDto cliente, long total, CobroSolicitado[] cobros, Guid? autorizacion = null,
        int piezas = 1) =>
        c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(null, null, piezas * 1000L, "Mercancía", total / piezas)], cobros, Ids.Nuevo(), ClienteId: cliente.Id,
            AutorizacionCreditoId: autorizacion));

    private static Task<CuentaClienteDto> CuentaAsync(Caja c, ClienteRegistradoDto cliente) =>
        c.Http.GetFromJsonAsync<CuentaClienteDto>($"api/clientes/{cliente.Id}/credito")!;

    private static Task<HttpResponseMessage> AjustarAsync(Caja c, ClienteRegistradoDto cliente, long monto, SesionDto? quien = null, DateOnly? desde = null,
        Guid? id = null) =>
        c.Http.PostAsJsonAsync($"api/clientes/{cliente.Id}/ajustes",
            new SolicitudAjusteCredito(id ?? Ids.Nuevo(), (quien ?? c.Dueno).UsuarioId, c.Datos.SucursalId, monto, "Saldo inicial de la libreta", desde));

    private static Task<HttpResponseMessage> AbonarAsync(Caja c, ClienteRegistradoDto cliente, FormaPago forma, long? monto, long recibido = 0, Guid? id = null) =>
        c.Http.PostAsJsonAsync("api/abonos", new SolicitudAbono(id ?? Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, cliente.Id, forma, monto,
            recibido, forma == FormaPago.Efectivo ? null : "REF-1"));

    private T EnLaBase<T>(Func<PosDbContext, T> consulta)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return consulta(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    [Fact]
    public void Las_reglas_iniciales_se_cumplen_en_el_servicio() =>
        Assert.False(Permisos.SoloDelDueno(AccionControlada.DarCredito));

    [Fact]
    public async Task El_credito_lo_da_el_dueno_y_guardar_la_ficha_no_se_lo_quita()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Arq. Ramírez");

        var delEncargado = await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
            new SolicitudCreditoCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, 100000, 30));
        var conCredito = await Leer<ClienteRegistradoDto>(await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
            new SolicitudCreditoCliente(c.Dueno.UsuarioId, c.Datos.SucursalId, 100000, 15)));
        // Una caja que manda la ficha completa sin el crédito (o una de antes) no se lo quita.
        var editado = await Leer<ClienteRegistradoDto>(await c.Http.PostAsJsonAsync("api/clientes",
            new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, conCredito with { Nota = "Paga los viernes", LimiteCreditoCentavos = 0 })));

        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
        Assert.Equal((100000L, 15, "Paga los viernes"), (editado.LimiteCreditoCentavos, editado.PlazoCreditoDias, editado.Nota));
        Assert.Contains("crédito", editado.TextoPrivilegios());
        Assert.Contains(EnLaBase(db => db.Bitacora.Where(b => b.Accion == AccionControlada.DarCredito).Select(b => b.Detalle).ToList()),
            d => d == "Privilegios de «Crédito: Arq. Ramírez»: crédito $0.00 → $1,000.00, plazo 30 → 15 días");
    }

    [Fact]
    public async Task Fiar_deja_el_cargo_y_el_ticket_dice_lo_fiado_el_saldo_y_cuando_paga()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Escuela", 100000);

        var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 13745, [new CobroSolicitado(FormaPago.Efectivo, 10000), new CobroSolicitado(FormaPago.Credito, 3745)]));
        await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Tarjeta, 3745));
        var reimpreso = await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"));
        var cuenta = await CuentaAsync(c, cliente);

        Assert.Equal(new CreditoTicketDto(3745, 3745, Hoy.AddDays(30)), ticket.Credito);
        Assert.Equal(ticket.Credito, reimpreso.Credito);
        Assert.Equal((10000L, 3745L), (ticket.PagoConCentavos, ticket.CreditoCentavos));
        Assert.Equal(0, cuenta.Credito.SaldoCentavos);
        Assert.Equal([TipoMovimientoCredito.Abono, TipoMovimientoCredito.Cargo], cuenta.Movimientos.Select(m => m.Tipo));
        Assert.Equal(ticket.Folio, cuenta.Movimientos[1].Folio);
    }

    [Fact]
    public async Task Pasar_el_limite_lo_autoriza_el_encargado_y_queda_en_la_bitacora()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Taller", 5000);
        CobroSolicitado[] fiado = [new CobroSolicitado(FormaPago.Credito, 8000)];

        var sinAutorizacion = await CobrarAsync(c, cliente, 8000, fiado);
        var autorizacion = await Leer<AutorizacionDto>(await c.Http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(
            AccionControlada.ExcederLimiteCredito, c.Datos.SucursalId, c.Datos.Id, c.Cajero.UsuarioId, "encargado", "2222", 3000, "Fiar")));
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 8000, fiado, autorizacion.Id));

        Assert.Equal(HttpStatusCode.Forbidden, sinAutorizacion.StatusCode);
        Assert.Equal("requiere-autorizacion", (await sinAutorizacion.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.Equal(8000, ticket.Credito!.SaldoCentavos);
        var registro = EnLaBase(db => db.Bitacora.Single(b => b.VentaId == ticket.VentaId && b.Accion == AccionControlada.ExcederLimiteCredito));
        Assert.Equal(ResultadoBitacora.Autorizado, registro.Resultado);
        Assert.Contains("pasando el límite por $30.00", registro.Detalle);
    }

    [Fact]
    public async Task Sin_credito_solo_se_usa_el_saldo_a_favor()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: A favor");
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, -5000));

        await Leer<TicketDto>(await CobrarAsync(c, cliente, 3000, [new CobroSolicitado(FormaPago.Credito, 3000)]));
        var demas = await CobrarAsync(c, cliente, 3000, [new CobroSolicitado(FormaPago.Credito, 3000)]);

        Assert.Equal(HttpStatusCode.BadRequest, demas.StatusCode);
        Assert.Contains("solo puede usar su saldo a favor ($20.00)", await Error(demas));
    }

    [Fact]
    public async Task El_saldo_inicial_fechado_queda_vencido_y_el_ajuste_se_guarda_una_vez()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Libreta", 100000);
        var id = Ids.Nuevo();

        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 35000, desde: Hoy.AddDays(-40), id: id));
        var cuenta = await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 35000, desde: Hoy.AddDays(-40), id: id));
        var delEncargado = await AjustarAsync(c, cliente, 1000, c.Encargado);

        Assert.Equal((35000L, 35000L), (cuenta.Credito.SaldoCentavos, cuenta.Credito.Vencido(Hoy)));
        Assert.Single(cuenta.Movimientos);
        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
    }

    [Fact]
    public async Task Quien_debe_no_se_deja_inactivo()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Debe", 100000);
        await Leer<TicketDto>(await CobrarAsync(c, cliente, 2000, [new CobroSolicitado(FormaPago.Credito, 2000)]));

        var inactivo = await c.Http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId,
            cliente with { Activo = false }));

        Assert.Equal(HttpStatusCode.BadRequest, inactivo.StatusCode);
        Assert.Contains("todavía debe $20.00", await Error(inactivo));
    }

    [Fact]
    public async Task Dos_cobros_a_la_vez_no_pasan_juntos_el_limite()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: A la vez", 10000);
        CobroSolicitado[] fiado = [new CobroSolicitado(FormaPago.Credito, 6000)];

        var respuestas = await Task.WhenAll(CobrarAsync(c, cliente, 6000, fiado), CobrarAsync(c, cliente, 6000, fiado));

        Assert.Single(respuestas, r => r.IsSuccessStatusCode);
        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Forbidden);
        Assert.Equal(6000, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
    }

    [Fact]
    public async Task Abonar_en_efectivo_liquida_al_peso_entra_al_corte_y_no_se_guarda_dos_veces()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Abona", 100000);
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 23745));
        var antes = await Leer<CorteDto>(await c.Http.GetAsync($"api/turnos/{c.Turno.Id}/resumen"));
        var id = Ids.Nuevo();

        var mucho = await AbonarAsync(c, cliente, FormaPago.Tarjeta, 30000);
        var abono = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Efectivo, null, 30000, id));
        var otraVez = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Efectivo, null, 30000, id));
        var despues = await Leer<CorteDto>(await c.Http.GetAsync($"api/turnos/{c.Turno.Id}/resumen"));

        Assert.Contains("La tarjeta no da cambio: abona hasta $237.45", await Error(mucho));
        Assert.Equal((23745L, -45L, 6300L, 23745L, 0L, true), (abono.MontoCentavos, abono.RedondeoCentavos, abono.CambioCentavos, abono.SaldoAnteriorCentavos,
            abono.SaldoCentavos, abono.AbreCajon));
        Assert.Equal((abono.AbonoId, abono.FechaLocal, abono.MontoCentavos, abono.SaldoCentavos),
            (otraVez.AbonoId, otraVez.FechaLocal, otraVez.MontoCentavos, otraVez.SaldoCentavos));
        Assert.Equal((antes.Abonos + 1, antes.AbonosEfectivoCentavos + 23700, antes.EsperadoCentavos + 23700, antes.RedondeoCentavos - 45),
            (despues.Abonos, despues.AbonosEfectivoCentavos, despues.EsperadoCentavos, despues.RedondeoCentavos));
        Assert.Equal(0, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
        Assert.Equal(1, EnLaBase(db => db.MovimientosCredito.Count(m => m.AbonoId == id)));
    }

    [Fact]
    public async Task Si_la_transferencia_del_abono_no_llega_vuelve_a_deberse()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Transfiere", 100000);
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 80000));

        var abono = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Transferencia, 50000));
        var porVerificar = (await CuentaAsync(c, cliente)).Credito;
        var lista = (await c.Http.GetFromJsonAsync<List<TransferenciaDto>>($"api/sucursales/{c.Datos.SucursalId}/transferencias"))!;
        await Leer<object>(await c.Http.PostAsJsonAsync($"api/abonos/{abono.AbonoId}/verificacion", new SolicitudVerificacion("encargado", "2222", false)));
        var rechazada = (await CuentaAsync(c, cliente)).Credito;

        Assert.Equal((30000L, 50000L, 20000L), (porVerificar.SaldoCentavos, porVerificar.PorVerificarCentavos, porVerificar.DisponibleCentavos));
        Assert.Contains(lista, t => t.EsAbono && t.CobroId == abono.AbonoId && t.Cliente == cliente.Nombre && t.Verificacion == Verificacion.PorVerificar);
        Assert.Equal((80000L, 0L), (rechazada.SaldoCentavos, rechazada.PorVerificarCentavos));
        Assert.Contains(EnLaBase(db => db.Bitacora.Where(b => b.Accion == AccionControlada.VerificarTransferencia).Select(b => b.Detalle).ToList()),
            d => d.StartsWith($"Rechazó la transferencia del abono {abono.Folio}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelar_un_abono_en_su_turno_vuelve_a_deberse_y_sale_del_corte()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Se equivocó", 100000);
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 50000));
        var abono = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Efectivo, 20000, 20000));
        var conAbono = await Leer<CorteDto>(await c.Http.GetAsync($"api/turnos/{c.Turno.Id}/resumen"));

        var delCajero = await c.Http.PostAsJsonAsync($"api/abonos/{abono.AbonoId}/cancelacion", new SolicitudCancelacion(c.Cajero.UsuarioId, c.Datos.Id, "Era de otro"));
        var cancelado = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync($"api/abonos/{abono.AbonoId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Era de otro")));
        var sinAbono = await Leer<CorteDto>(await c.Http.GetAsync($"api/turnos/{c.Turno.Id}/resumen"));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.True(cancelado.Cancelado);
        Assert.Equal((30000L, 50000L), (cancelado.SaldoCentavos, cancelado.SaldoTrasCancelarCentavos));
        Assert.Equal(50000, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
        Assert.Equal((conAbono.Abonos - 1, conAbono.EsperadoCentavos - 20000), (sinAbono.Abonos, sinAbono.EsperadoCentavos));
    }

    [Fact]
    public async Task Devolver_y_cancelar_lo_fiado_baja_la_cuenta_sin_sacar_efectivo()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Devuelve", 100000);
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 10000, [new CobroSolicitado(FormaPago.Credito, 10000)], piezas: 2));
        var otro = await Leer<TicketDto>(await CobrarAsync(c, cliente, 3000, [new CobroSolicitado(FormaPago.Credito, 3000)]));

        var venta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        var devolucion = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No le quedó", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)])));
        var cancelacion = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{otro.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));

        Assert.Equal((10000L, cliente.Nombre), (venta.CreditoCentavos, venta.Cliente));
        Assert.Equal((5000L, 0L, 8000L), (devolucion.AlCreditoCentavos, devolucion.ReembolsoEfectivoCentavos, devolucion.SaldoClienteCentavos));
        Assert.Equal((3000L, 0L, 0L, 5000L), (cancelacion.AlCreditoCentavos, cancelacion.ReembolsoEfectivoCentavos, cancelacion.CobradoSinEfectivoCentavos,
            cancelacion.SaldoClienteCentavos));
        var cuenta = await CuentaAsync(c, cliente);
        Assert.Equal(5000, cuenta.Credito.SaldoCentavos);
        Assert.Equal([TipoMovimientoCredito.Cancelacion, TipoMovimientoCredito.Devolucion, TipoMovimientoCredito.Cargo, TipoMovimientoCredito.Cargo],
            cuenta.Movimientos.Select(m => m.Tipo));

        // En toda la base: lo fiado de cada venta es su cargo, y lo que cada devolución mandó a la cuenta es su movimiento.
        EnLaBase(db =>
        {
            var cargos = db.MovimientosCredito.Where(m => m.Tipo == TipoMovimientoCredito.Cargo).ToDictionary(m => m.Id, m => m.MontoCentavos);
            foreach (var v in db.Ventas.Include(x => x.Cobros).Where(x => x.Cobros.Any(k => k.Forma == FormaPago.Credito)).ToList())
                Assert.Equal(v.CreditoCentavos, cargos[v.Id]);
            var revertidos = db.MovimientosCredito.Where(m => m.DevolucionId != null).ToDictionary(m => m.DevolucionId!.Value, m => -m.MontoCentavos);
            foreach (var d in db.Devoluciones.Where(x => x.AlCreditoCentavos > 0).ToList())
                Assert.Equal(d.AlCreditoCentavos, revertidos[d.Id]);
            return 0;
        });
    }

    [Fact]
    public async Task Sin_credito_lo_que_tiene_a_favor_por_verificar_no_se_usa_y_el_mensaje_lo_dice()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: A favor por verificar", 100000);
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 10000, [new CobroSolicitado(FormaPago.Credito, 10000)]));
        await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Transferencia, 10000));
        var venta = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Encargado.UsuarioId, c.Datos.Id, "No le quedó", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)])));
        await Leer<ClienteRegistradoDto>(await c.Http.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito",
            new SolicitudCreditoCliente(c.Dueno.UsuarioId, c.Datos.SucursalId, 0, 30)));

        var r = await CobrarAsync(c, cliente, 100, [new CobroSolicitado(FormaPago.Credito, 100)]);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("«Crédito: A favor por verificar» no tiene crédito y su saldo a favor está por verificar (viene de una transferencia).", await Error(r));
    }

    [Fact]
    public async Task No_se_deja_inactivo_a_quien_tiene_abonos_por_verificar()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Pagó por transferencia", 100000);
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 20000));
        await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Transferencia, null));

        var r = await c.Http.PostAsJsonAsync("api/clientes",
            new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, cliente with { Activo = false }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("«Crédito: Pagó por transferencia» tiene $200.00 en transferencias por verificar: si no llegan, vuelve a deber.", await Error(r));
    }

    [Fact]
    public async Task Dejarlo_inactivo_y_fiarle_a_la_vez_nunca_deja_inactivo_a_quien_debe()
    {
        var c = await CajaAsync();
        for (var i = 0; i < 5; i++)
        {
            var cliente = await ClienteAsync(c, $"Crédito: Se va {i}", 100000);
            var r = await Task.WhenAll(
                c.Http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Datos.SucursalId, cliente with { Activo = false })),
                CobrarAsync(c with { Http = servicio.Fabrica.CreateClient() }, cliente, 2000, [new CobroSolicitado(FormaPago.Credito, 2000)]));

            var activo = EnLaBase(db => db.ClientesRegistrados.Single(x => x.Id == cliente.Id).Activo);
            var saldo = (await CuentaAsync(c, cliente)).Credito.SaldoCentavos;
            Assert.Single(r, x => x.IsSuccessStatusCode);
            Assert.True(activo ? saldo == 2000 : saldo == 0, $"activo={activo}, saldo={saldo}");
        }
    }

    [Fact]
    public async Task Dos_cancelaciones_a_la_vez_de_un_ticket_fiado_bajan_la_cuenta_una_vez()
    {
        var c = await CajaAsync();
        for (var i = 0; i < 3; i++)
        {
            var cliente = await ClienteAsync(c, $"Crédito: Cancela dos veces {i}", 100000);
            var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 10000, [new CobroSolicitado(FormaPago.Credito, 10000)]));
            var cancelacion = new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió");

            var r = await Task.WhenAll(
                c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion", cancelacion),
                servicio.Fabrica.CreateClient().PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion", cancelacion));

            Assert.Single(r, x => x.IsSuccessStatusCode);
            Assert.Equal(0, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
        }
    }

    [Fact]
    public async Task Rechazar_la_transferencia_y_cancelar_el_abono_a_la_vez_pasa_solo_uno()
    {
        var c = await CajaAsync();
        for (var i = 0; i < 3; i++)
        {
            var cliente = await ClienteAsync(c, $"Crédito: Rechazo y cancelación {i}", 100000);
            await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 80000));
            var abono = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Transferencia, 50000));

            var r = await Task.WhenAll(
                c.Http.PostAsJsonAsync($"api/abonos/{abono.AbonoId}/verificacion", new SolicitudVerificacion("encargado", "2222", false)),
                servicio.Fabrica.CreateClient().PostAsJsonAsync($"api/abonos/{abono.AbonoId}/cancelacion",
                    new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Era de otro")));

            Assert.Single(r, x => x.IsSuccessStatusCode);
            Assert.Equal(80000, EnLaBase(db => db.MovimientosCredito.Where(m => m.ClienteId == cliente.Id).Sum(m => m.MontoCentavos)));
            Assert.Equal(80000, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
        }
    }

    [Fact]
    public async Task El_mismo_ajuste_mandado_varias_veces_a_la_vez_se_guarda_una()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Reintento", 100000);
        var id = Ids.Nuevo();

        var r = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AjustarAsync(c with { Http = servicio.Fabrica.CreateClient() }, cliente, 35000, id: id)));

        Assert.All(r, x => Assert.True(x.IsSuccessStatusCode));
        Assert.Equal(35000, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
    }

    [Fact]
    public async Task La_reimpresion_del_abono_dice_lo_mismo_aunque_despues_haya_pagado()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Reimprime", 100000);
        await Leer<TicketDto>(await CobrarAsync(c, cliente, 50000, [new CobroSolicitado(FormaPago.Credito, 50000)]));
        var primero = await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Tarjeta, 20000));
        await Leer<ComprobanteAbonoDto>(await AbonarAsync(c, cliente, FormaPago.Tarjeta, 30000));

        var reimpreso = await Leer<ComprobanteAbonoDto>(await c.Http.PostAsJsonAsync($"api/abonos/{primero.AbonoId}/reimpresion",
            new SolicitudReimpresion(c.Dueno.UsuarioId, c.Datos.Id, "Lo perdió")));

        Assert.Equal((Hoy.AddDays(30), 30000L), (primero.Vence, primero.SaldoCentavos));
        Assert.Equal((primero.Vence, primero.SaldoCentavos), (reimpreso.Vence, reimpreso.SaldoCentavos));
    }

    [Fact]
    public async Task La_cancelacion_dice_cuanto_fue_con_tarjeta_y_cuanto_con_transferencia()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Paga con tarjeta");
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, cliente, 8000,
            [new CobroSolicitado(FormaPago.Tarjeta, 5000, "T-1"), new CobroSolicitado(FormaPago.Transferencia, 3000, "R-1")]));

        var cancelacion = await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));

        Assert.Equal((5000L, 3000L, 8000L), (cancelacion.TarjetaCentavos, cancelacion.TransferenciaCentavos, cancelacion.CobradoSinEfectivoCentavos));
    }

    [Fact]
    public async Task Si_la_cuenta_cambio_mientras_se_cobraba_el_abono_no_se_guarda()
    {
        var c = await CajaAsync();
        var cliente = await ClienteAsync(c, "Crédito: Le fiaron mientras tanto", 100000);
        await Leer<CuentaClienteDto>(await AjustarAsync(c, cliente, 23745));
        var visto = (await CuentaAsync(c, cliente)).Credito.SaldoCentavos;
        // Mientras el cajero cobra «liquidar» con tarjeta, otra caja le fía $100.
        await Leer<TicketDto>(await CobrarAsync(c, cliente, 10000, [new CobroSolicitado(FormaPago.Credito, 10000)]));

        var r = await c.Http.PostAsJsonAsync("api/abonos", new SolicitudAbono(Ids.Nuevo(), c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, cliente.Id,
            FormaPago.Tarjeta, null, Referencia: "T-1", SaldoVistoCentavos: visto));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("La cuenta cambió mientras tanto: ahora debe $337.45. Revísalo y abona otra vez.", await Error(r));
        Assert.Equal(33745, (await CuentaAsync(c, cliente)).Credito.SaldoCentavos);
    }
}
