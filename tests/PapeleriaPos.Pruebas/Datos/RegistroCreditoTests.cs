using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>La cuenta con el reloj fijo y con lecturas cruzadas entre dos peticiones (fase 3, entrega 2, parte 2.2).</summary>
public class RegistroCreditoTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _dueno = new() { Nombre = "Don", NombreUsuario = "don", Rol = Rol.Dueno };
    private Caja _caja = null!;
    private ClienteRegistrado _cliente = null!;
    private Turno _turno = null!;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        // 09:00 en México (UTC−6) del 8 de octubre de 2026.
        _base.Reloj.Ahora = new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _cajera.EstablecerPin("1111");
        _dueno.EstablecerPin("3333");
        _cliente = new ClienteRegistrado { Nombre = "Arq. Ramírez", LimiteCreditoCentavos = 100000, PlazoCreditoDias = 30 };
        await using (var db = _base.Contexto())
        {
            _catalogo.Agregar(db);
            db.AddRange(_caja, _cajera, _dueno, _cliente);
            await db.SaveChangesAsync();
            // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
            await MigracionPermisos.AplicarAsync(db);
        }
        await using (var db = _base.Contexto())
            _turno = await new RegistroCaja(db).AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(_base.Reloj.Ahora.UtcDateTime));

    private async Task AjustarAsync(long monto, DateOnly? desde = null)
    {
        await using var db = _base.Contexto();
        await new RegistroCredito(db).AjustarAsync(_cliente.Id,
            new SolicitudAjusteCredito(Ids.Nuevo(), _dueno.Id, _catalogo.Matriz.Id, monto, "Saldo inicial de la libreta", desde));
    }

    private async Task<Abono> AbonarAsync(FormaPago forma, long monto)
    {
        await using var db = _base.Contexto();
        return await new RegistroCredito(db).AbonarAsync(new SolicitudAbono(Ids.Nuevo(), _caja.Id, _turno.Id, _cajera.Id, _cliente.Id, forma, monto,
            forma == FormaPago.Efectivo ? monto : 0, forma == FormaPago.Efectivo ? null : "REF-1"));
    }

    private async Task<long> SumaDelLibroAsync()
    {
        await using var db = _base.Contexto();
        return await db.MovimientosCredito.Where(m => m.ClienteId == _cliente.Id).SumAsync(m => m.MontoCentavos);
    }

    [Fact]
    public async Task Un_ajuste_de_hoy_hecho_en_la_manana_queda_antes_de_lo_que_pasa_despues()
    {
        await AjustarAsync(35000, Hoy);
        _base.Reloj.Ahora = _base.Reloj.Ahora.AddHours(1);
        await AbonarAsync(FormaPago.Efectivo, 10000);

        CuentaClienteDto cuenta;
        await using (var db = _base.Contexto())
            cuenta = await new RegistroCredito(db).MovimientosAsync(_cliente.Id);

        Assert.Equal([(TipoMovimientoCredito.Abono, 25000L), (TipoMovimientoCredito.Ajuste, 35000L)],
            cuenta.Movimientos.Select(m => (m.Tipo, m.SaldoCentavos)));
        Assert.Equal(Hoy.AddDays(30), cuenta.Credito.VenceMasAntiguo);
    }

    [Fact]
    public async Task Un_ajuste_de_un_dia_pasado_queda_a_mediodia_de_ese_dia()
    {
        await AjustarAsync(35000, Hoy.AddDays(-40));

        await using var db = _base.Contexto();
        var ajuste = await db.MovimientosCredito.SingleAsync(m => m.ClienteId == _cliente.Id);
        Assert.Equal(new DateTime(2026, 8, 29, 12, 0, 0), HoraMexico.ALocal(ajuste.FechaUtc));
        Assert.Equal(Hoy.AddDays(-10), ajuste.Vence);
    }

    [Fact]
    public async Task Si_se_cancela_el_abono_mientras_se_revisa_la_transferencia_el_rechazo_ya_no_pasa()
    {
        await AjustarAsync(80000);
        var abono = await AbonarAsync(FormaPago.Transferencia, 50000);

        // El encargado pulsa «Rechazar»: su petición ya leyó el abono...
        await using var revision = _base.Contexto();
        await revision.Abonos.FindAsync(abono.Id);
        // ...y mientras revisa su PIN, en la caja se cancela.
        await using (var db = _base.Contexto())
            await new RegistroCredito(db).CancelarAbonoAsync(abono.Id, new SolicitudCancelacion(_dueno.Id, _caja.Id, "Era de otro"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RegistroCredito(revision).VerificarAbonoAsync(abono.Id, "don", "3333", llego: false));

        Assert.Equal("Esa transferencia ya se revisó.", error.Message);
        Assert.Equal(80000, await SumaDelLibroAsync());
    }

    [Fact]
    public async Task Si_se_verifica_la_transferencia_mientras_se_cancela_el_abono_la_cancelacion_ya_no_pasa()
    {
        await AjustarAsync(80000);
        var abono = await AbonarAsync(FormaPago.Transferencia, 50000);

        await using var cancelacion = _base.Contexto();
        await cancelacion.Abonos.FindAsync(abono.Id);
        await using (var db = _base.Contexto())
            await new RegistroCredito(db).VerificarAbonoAsync(abono.Id, "don", "3333", llego: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RegistroCredito(cancelacion).CancelarAbonoAsync(abono.Id, new SolicitudCancelacion(_dueno.Id, _caja.Id, "Era de otro")));

        Assert.StartsWith("Esa transferencia ya se verificó", error.Message);
        Assert.Equal(30000, await SumaDelLibroAsync());
    }

    [Fact]
    public async Task El_comprobante_del_abono_guarda_cuando_vence_lo_que_queda()
    {
        await AjustarAsync(50000, Hoy.AddDays(-5));
        var primero = await AbonarAsync(FormaPago.Tarjeta, 20000);
        var liquida = await AbonarAsync(FormaPago.Tarjeta, 30000);

        await using var db = _base.Contexto();
        Assert.Equal(Hoy.AddDays(25), (await db.Abonos.FindAsync(primero.Id))!.Vence);
        Assert.Null((await db.Abonos.FindAsync(liquida.Id))!.Vence);
    }

    private async Task<long> VersionAsync(string secuencia)
    {
        await using var db = _base.Contexto();
        return await db.Secuencias.Where(x => x.Nombre == secuencia).Select(x => x.Valor).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Lo_del_credito_sube_su_version_y_no_la_del_catalogo()
    {
        var (catalogo, credito) = (await VersionAsync(Secuencia.Catalogo), await VersionAsync(Secuencia.Credito));
        await AjustarAsync(10000);
        var trasAjuste = (await VersionAsync(Secuencia.Catalogo), await VersionAsync(Secuencia.Credito));
        var abono = await AbonarAsync(FormaPago.Transferencia, 5000);
        var trasAbono = await VersionAsync(Secuencia.Credito);
        await using (var db = _base.Contexto())
            await new RegistroCredito(db).VerificarAbonoAsync(abono.Id, "don", "3333", llego: true);
        var trasVerificar = await VersionAsync(Secuencia.Credito);
        await using (var db = _base.Contexto())
            await new RegistroCredito(db).GuardarCondicionesAsync(_cliente.Id, new SolicitudCreditoCliente(_dueno.Id, _catalogo.Matriz.Id, 200000, 15));
        var trasCondiciones = (await VersionAsync(Secuencia.Catalogo), await VersionAsync(Secuencia.Credito));

        Assert.Equal((catalogo, credito + 1), trasAjuste);
        Assert.Equal(trasAjuste.Item2 + 1, trasAbono);
        Assert.Equal(trasAbono + 1, trasVerificar);
        // El límite es del cliente (lo baja la copia de la caja) y también de sus saldos.
        Assert.True(trasCondiciones.Item1 > catalogo);
        Assert.Equal(trasVerificar + 1, trasCondiciones.Item2);
    }

    [Fact]
    public async Task Los_saldos_dicen_cuales_de_los_pendientes_de_la_caja_ya_estan_sumados()
    {
        await AjustarAsync(30000);
        var abono = await AbonarAsync(FormaPago.Efectivo, 10000);
        var noLlego = Ids.Nuevo();

        SaldosCreditoDto saldos;
        await using (var db = _base.Contexto())
            saldos = await new RegistroCredito(db).SaldosAsync(_catalogo.Matriz.Id, [abono.Id, noLlego]);

        Assert.Equal([abono.Id], saldos.Incluidos);
        Assert.Equal(20000, saldos.Cuentas.Single(c => c.ClienteId == _cliente.Id).SaldoCentavos);
        Assert.Equal(await VersionAsync(Secuencia.Credito), saldos.Version);
    }

    // ── Devolver el saldo a favor (parte 3) ──────────────────────────────────

    private async Task<Abono> DevolverAsync(Usuario quien, long? monto = null, long? visto = null, Guid? id = null)
    {
        await using var db = _base.Contexto();
        return await new RegistroCredito(db).DevolverSaldoAsync(_cliente.Id,
            new SolicitudDevolverSaldo(id ?? Ids.Nuevo(), _caja.Id, _turno.Id, quien.Id, monto, visto));
    }

    [Fact]
    public async Task Devolver_el_saldo_a_favor_lo_deja_en_cero_sale_de_la_caja_y_queda_en_la_bitacora()
    {
        await AjustarAsync(-15040);
        var id = Ids.Nuevo();

        var devuelto = await DevolverAsync(_dueno, visto: -15040, id: id);
        var otraVez = await DevolverAsync(_dueno, visto: -15040, id: id);

        Assert.Equal((TipoAbono.SaldoDevuelto, 15040L, -40L, 15000L), (devuelto.Tipo, devuelto.MontoCentavos, devuelto.RedondeoCentavos, devuelto.CobradoCentavos));
        Assert.Equal((-15040L, 0L), (devuelto.SaldoAnteriorCentavos, devuelto.SaldoCentavos));
        Assert.StartsWith("DS-", devuelto.Folio);
        Assert.Equal(devuelto.Id, otraVez.Id);
        Assert.Equal(0, await SumaDelLibroAsync());
        await using var db = _base.Contexto();
        var movimiento = await db.MovimientosCredito.SingleAsync(m => m.Id == devuelto.Id);
        Assert.Equal((TipoMovimientoCredito.SaldoDevuelto, Hoy.AddDays(30)), (movimiento.Tipo, movimiento.Vence));
        Assert.Single(await db.AperturasCajon.Where(a => a.TurnoId == _turno.Id && a.Motivo == MotivoApertura.Abono).ToListAsync());
        Assert.Contains("Devolvió $150.00 en efectivo del saldo a favor de «Arq. Ramírez» (tenía $150.40 a favor)",
            await db.Bitacora.Where(b => b.Accion == AccionControlada.DevolverSaldoAFavor).Select(b => b.Detalle).ToListAsync());
        var corte = await new RegistroCaja(db).CorteXAsync(_turno.Id, _dueno.Id);
        Assert.Equal((15000L, 50000L - 15000L, 0), (corte.SaldoDevueltoCentavos, corte.EsperadoCentavos, corte.Abonos));
    }

    [Fact]
    public async Task El_cajero_necesita_la_autorizacion_del_encargado_para_devolver_el_saldo()
    {
        await AjustarAsync(-15000);

        var error = await Assert.ThrowsAsync<PermisoRequerido>(() => DevolverAsync(_cajera));

        Assert.Equal(NivelPermiso.RequiereAutorizacion, error.Nivel);
        Assert.Equal(-15000, await SumaDelLibroAsync());
    }

    [Fact]
    public async Task No_se_devuelve_lo_que_viene_de_una_transferencia_por_verificar_ni_si_la_cuenta_cambio()
    {
        await AjustarAsync(5000);
        await AbonarAsync(FormaPago.Transferencia, 5000);
        await AjustarAsync(-3000);

        var porVerificar = await Assert.ThrowsAsync<InvalidOperationException>(() => DevolverAsync(_dueno));
        var cambio = await Assert.ThrowsAsync<InvalidOperationException>(() => DevolverAsync(_dueno, visto: -1000));

        Assert.Equal("Su saldo a favor viene de transferencias por verificar ($50.00): se le devuelve cuando se verifiquen.", porVerificar.Message);
        Assert.Equal("La cuenta cambió mientras tanto: ahora tiene $30.00 a favor. Revísalo y devuelve otra vez.", cambio.Message);
        Assert.Equal(-3000, await SumaDelLibroAsync());
    }

    [Fact]
    public async Task Lo_devuelto_de_un_saldo_a_favor_no_se_cancela()
    {
        await AjustarAsync(-15000);
        var devuelto = await DevolverAsync(_dueno);

        await using var db = _base.Contexto();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RegistroCredito(db).CancelarAbonoAsync(devuelto.Id, new SolicitudCancelacion(_dueno.Id, _caja.Id, "Error")));

        Assert.StartsWith("Lo que se devolvió de un saldo a favor no se cancela", error.Message);
        Assert.Equal(0, await SumaDelLibroAsync());
    }

    // ── Estado de cuenta (parte 3) ───────────────────────────────────────────

    private async Task<EstadoCuentaDto> EstadoAsync(DateOnly? desde = null)
    {
        await using var db = _base.Contexto();
        return await new RegistroCredito(db).EstadoDeCuentaAsync(_cliente.Id, desde);
    }

    [Fact]
    public async Task El_estado_de_cuenta_empieza_en_lo_mas_viejo_que_debe_y_dice_lo_que_falta_de_cada_cosa()
    {
        await AjustarAsync(35000, Hoy.AddDays(-40));
        await AbonarAsync(FormaPago.Efectivo, 10000);

        var estado = await EstadoAsync();

        Assert.Equal((Hoy.AddDays(-40), 0L, 25000L, 25000L), (estado.Desde, estado.SaldoAnteriorCentavos, estado.SaldoCentavos, estado.VencidoCentavos));
        Assert.Equal([(TipoMovimientoCredito.Ajuste, 35000L), (TipoMovimientoCredito.Abono, 25000L)],
            estado.Movimientos.Select(m => (m.Tipo, m.SaldoCentavos)));
        var pendiente = Assert.Single(estado.Pendientes);
        Assert.Equal(new PendienteEstadoDto(Hoy.AddDays(-40), "Ajuste: Saldo inicial de la libreta", null, 35000, 25000, Hoy.AddDays(-10)), pendiente);
        Assert.Equal(("Arq. Ramírez", 100000L, 30), (estado.Cliente, estado.LimiteCentavos, estado.PlazoDias));
    }

    [Fact]
    public async Task Al_corriente_el_estado_de_cuenta_trae_los_ultimos_30_dias_con_lo_que_debia_antes()
    {
        await AjustarAsync(10000, Hoy.AddDays(-60));
        await AbonarAsync(FormaPago.Efectivo, 10000);

        var reciente = await EstadoAsync();
        var desdeAntes = await EstadoAsync(Hoy.AddDays(-90));

        Assert.Equal((Hoy.AddDays(-30), 10000L, 0L), (reciente.Desde, reciente.SaldoAnteriorCentavos, reciente.SaldoCentavos));
        Assert.Equal([TipoMovimientoCredito.Abono], reciente.Movimientos.Select(m => m.Tipo));
        Assert.Empty(reciente.Pendientes);
        Assert.Equal((0L, 2), (desdeAntes.SaldoAnteriorCentavos, desdeAntes.Movimientos.Count));
        await Assert.ThrowsAsync<ArgumentException>(() => EstadoAsync(Hoy.AddDays(1)));
    }

    // ── Lo vencido por Telegram (parte 3.2) ──────────────────────────────────

    private async Task<List<MensajeTelegram>> AvisosDeVencidoAsync()
    {
        await using var db = _base.Contexto();
        if (!await db.ChatsTelegram.AnyAsync())
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = _dueno.Id, ChatId = 9696, VinculadoUtc = _base.Reloj.Ahora.UtcDateTime });
        await AlertasTelegram.RevisarVencidosAsync(db);
        await db.SaveChangesAsync();
        return await db.MensajesTelegram.Where(m => m.Tipo == TipoAlerta.CreditoVencido).OrderBy(m => m.CreadoUtc).ToListAsync();
    }

    [Fact]
    public async Task Lo_vencido_se_avisa_una_vez_por_cargo_en_horario_y_solo_lo_de_la_ultima_semana()
    {
        _cliente.Telefono = "5522334455";
        await using (var db = _base.Contexto())
        {
            db.Update(_cliente);
            await db.SaveChangesAsync();
        }
        await AjustarAsync(35000, Hoy.AddDays(-35));
        await AjustarAsync(20000, Hoy.AddDays(-60));
        await AjustarAsync(10000, Hoy.AddDays(-30));
        // Paga parte de lo más viejo: el de hace 60 días sigue debiéndose, pero venció hace un mes (lo dice el resumen).
        await AbonarAsync(FormaPago.Efectivo, 5000);
        // A las 6 de la mañana la tienda no ha abierto: se avisa al abrir.
        var ahora = _base.Reloj.Ahora;
        _base.Reloj.Ahora = ahora.AddHours(-3);
        var temprano = await AvisosDeVencidoAsync();
        _base.Reloj.Ahora = ahora;

        var avisos = await AvisosDeVencidoAsync();
        var otraVez = await AvisosDeVencidoAsync();

        Assert.Empty(temprano);
        // El de hace 30 días vence hoy: todavía no.
        var aviso = Assert.Single(avisos);
        Assert.Equal(aviso.Id, Assert.Single(otraVez).Id);
        Assert.Equal($"Crédito vencido · «Arq. Ramírez»\nLo que debía (Saldo inicial de la libreta) del {Hoy.AddDays(-35):dd/MM/yyyy}: faltan $350.00 "
            + $"y vencía el {Hoy.AddDays(-5):dd/MM/yyyy}.\nDebe en total $600.00, vencido $500.00. Tel. 5522334455", aviso.Texto);
    }

    [Fact]
    public async Task Por_cobrar_en_el_resumen_dice_si_junta_a_los_clientes_de_varias_sucursales()
    {
        await AjustarAsync(35000, Hoy.AddDays(-40));
        string resumen;
        await using (var db = _base.Contexto())
            resumen = await AlertasTelegram.ResumenAsync(db, _catalogo.Matriz.Id, Hoy);
        await using (var db = _base.Contexto())
        {
            db.Sucursales.Add(new PapeleriaPos.Nucleo.Catalogo.Sucursal { Clave = "NOR", Nombre = "Norte" });
            await db.SaveChangesAsync();
        }
        string conVarias;
        await using (var db = _base.Contexto())
            conVarias = await AlertasTelegram.ResumenAsync(db, _catalogo.Matriz.Id, Hoy);

        Assert.Contains("Por cobrar: $350.00 de 1 cliente · vencido $350.00 de 1 cliente", resumen);
        Assert.Contains("Por cobrar (con los clientes de todas las sucursales): $350.00 de 1 cliente", conVarias);
    }
}
