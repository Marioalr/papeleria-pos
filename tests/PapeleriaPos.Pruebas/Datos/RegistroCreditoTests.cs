using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
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
}
