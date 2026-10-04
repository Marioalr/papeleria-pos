using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

public class ServicioControlTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private readonly Usuario _dueno = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };
    private readonly Caja _caja = new() { Nombre = "Caja 1", Serie = "A" };

    private Guid Matriz => _catalogo.Matriz.Id;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja.SucursalId = _catalogo.Matriz.Id;
        _cajera.EstablecerPin("1111");
        _encargado.EstablecerPin("2222");
        _dueno.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _encargado, _dueno);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    [Fact]
    public async Task El_encargado_autoriza_una_cancelacion_y_se_gasta_al_usarla()
    {
        await using var db = _base.Contexto();
        var control = new ServicioControl(db);

        await Assert.ThrowsAsync<PermisoRequerido>(() =>
            control.RequerirAsync(_cajera.Id, Matriz, _caja.Id, AccionControlada.CancelarTicket, 3000, "Cancelar A-000001"));
        var autorizacion = await control.AutorizarAsync(AccionControlada.CancelarTicket, Matriz, _caja.Id, _cajera.Id, "luis", "2222", 3000, "Cancelar A-000001");
        await control.RequerirAsync(_cajera.Id, Matriz, _caja.Id, AccionControlada.CancelarTicket, 3000, "Cancelar A-000001", autorizacion.Id);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.RequerirAsync(_cajera.Id, Matriz, _caja.Id, AccionControlada.CancelarTicket, 3000, "Otra vez", autorizacion.Id));
        var registro = await db.Bitacora.SingleAsync(b => b.Resultado == ResultadoBitacora.Autorizado);
        Assert.Equal((_cajera.Id, _encargado.Id), (registro.UsuarioId, registro.AutorizoId));
    }

    [Fact]
    public async Task Un_pin_incorrecto_o_quien_no_puede_autorizar_queda_como_negado()
    {
        await using var db = _base.Contexto();
        var control = new ServicioControl(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.AutorizarAsync(AccionControlada.CancelarTicket, Matriz, _caja.Id, _cajera.Id, "luis", "0000", null, "Cancelar"));
        var porCajera = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.AutorizarAsync(AccionControlada.CancelarTicket, Matriz, _caja.Id, _cajera.Id, "ana", "1111", null, "Cancelar"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.AutorizarAsync(AccionControlada.CambiarPrecio, Matriz, _caja.Id, _cajera.Id, "luis", "2222", null, "Precio"));
        var conDueno = await control.AutorizarAsync(AccionControlada.CambiarPrecio, Matriz, _caja.Id, _cajera.Id, "mario", "3333", null, "Precio");

        Assert.Contains("no puede autorizar", porCajera.Message);
        Assert.Equal(_dueno.Id, conDueno.AutorizadorId);
        Assert.Equal(3, await db.Bitacora.CountAsync(b => b.Resultado == ResultadoBitacora.Negado));
    }

    [Fact]
    public async Task Las_acciones_con_motivo_lo_exigen()
    {
        await using var db = _base.Contexto();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ServicioControl(db).RequerirAsync(_cajera.Id, Matriz, _caja.Id, AccionControlada.Reimprimir, 1000, "Reimpresión"));
    }

    [Fact]
    public async Task Lo_no_permitido_se_rechaza_aunque_traiga_autorizacion()
    {
        await using var db = _base.Contexto();

        var error = await Assert.ThrowsAsync<PermisoRequerido>(() =>
            new ServicioControl(db).RequerirAsync(_cajera.Id, Matriz, _caja.Id, AccionControlada.VerificarTransferencia, 1000, "Verificar", Ids.Nuevo()));
        Assert.Equal(NivelPermiso.NoPermitido, error.Nivel);
    }

    [Fact]
    public async Task La_bitacora_no_se_edita()
    {
        await using var db = _base.Contexto();
        var control = new ServicioControl(db);
        control.Registrar(Matriz, _caja.Id, _cajera.Id, AccionControlada.QuitarProducto, "Quitó una goma");
        await db.SaveChangesAsync();

        db.Bitacora.Remove(await db.Bitacora.SingleAsync());
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Salidas_y_articulo_comun_quedan_en_la_bitacora_y_el_efectivo_avisa_al_limite()
    {
        await using var db = _base.Contexto();
        db.ConfiguracionesSucursal.Add(new ConfiguracionSucursal { SucursalId = Matriz, LimiteEfectivoCentavos = 60000 });
        await db.SaveChangesAsync();
        var registro = new RegistroCaja(db);
        var turno = await registro.AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);

        var venta = new Venta { SucursalId = Matriz, CajaId = _caja.Id, TurnoId = turno.Id, CajeroId = _cajera.Id, Serie = "A" };
        venta.AgregarArticuloComun("Recuerdo", 15000, Cantidad.Piezas(1));
        await registro.CobrarAsync(venta, [new CobroSolicitado(FormaPago.Efectivo, 15000)]);
        var alLimite = await registro.EfectivoAsync(turno.Id);
        await registro.RegistrarMovimientoAsync(turno.Id, TipoMovimientoCaja.Salida, 30000, "Depósito al banco", _cajera.Id);
        var despues = await registro.EfectivoAsync(turno.Id);

        Assert.Equal((65000L, 60000L), (alLimite.Esperado, alLimite.Limite!.Value));
        Assert.Equal(35000, despues.Esperado);
        Assert.Equal(1, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.ArticuloComun && b.VentaId == venta.Id));
        Assert.Contains("Depósito al banco", (await db.Bitacora.SingleAsync(b => b.Accion == AccionControlada.SalidaEfectivo)).Detalle);
    }
}
