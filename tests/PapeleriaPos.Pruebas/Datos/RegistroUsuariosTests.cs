using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// «Usuarios y permisos» (entrega 1): lo hace quien entró con ese permiso, sin volver a escribir su PIN; cada cambio se
/// guarda al momento y queda en la bitácora; siempre queda alguien con «Usuarios y permisos».
/// </summary>
public class RegistroUsuariosTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _ana = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _luis = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private readonly Usuario _mario = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };
    private readonly Caja _caja = new() { Nombre = "Caja 1", Serie = "A" };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja.SucursalId = _catalogo.Matriz.Id;
        _ana.SucursalId = _luis.SucursalId = _catalogo.Matriz.Id;
        _ana.EstablecerPin("1111");
        _luis.EstablecerPin("2222");
        _mario.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _ana, _luis, _mario);
        await db.SaveChangesAsync();
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
        await MigracionPermisos.AplicarAsync(db);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Una operación de la pantalla con su propio contexto, como en el servicio.</summary>
    private async Task<T> ConAsync<T>(Func<RegistroUsuarios, Task<T>> hacer)
    {
        await using var db = _base.Contexto();
        return await hacer(new RegistroUsuarios(db));
    }

    private Task<UsuariosYPermisosDto> CambiarAsync(Guid quien, Guid? de, AccionControlada accion, NivelPermiso nivel, long? umbral = null) =>
        ConAsync(r => r.CambiarAsync(_caja.Id, de, new SolicitudPermisoUsuario(quien, accion, nivel, umbral)));

    private static Permiso De(UsuariosYPermisosDto lista, Guid usuarioId, AccionControlada accion) =>
        lista.Usuarios.Single(u => u.Id == usuarioId).Permisos.Single(p => p.Accion == accion);

    private async Task<List<string>> BitacoraAsync()
    {
        await using var db = _base.Contexto();
        return await db.Bitacora.Where(b => b.Accion == AccionControlada.EditarReglas).OrderBy(b => b.FechaUtc).Select(b => b.Detalle).ToListAsync();
    }

    [Fact]
    public async Task Quien_tiene_el_permiso_ve_a_todos_con_sus_permisos_y_los_de_defecto()
    {
        var lista = await ConAsync(r => r.ListarAsync(_caja.Id, _mario.Id));

        Assert.Equal(["Ana", "Luis", "Mario"], lista.Usuarios.Select(u => u.Nombre));
        Assert.Equal(["Permisos por defecto", "Corte de turno, productos y clientes", "Todos los permisos"], lista.Usuarios.Select(u => u.Resumen));
        Assert.Equal([false, false, true], lista.Usuarios.Select(u => u.PrimeraCuenta));
        Assert.Equal(["ana", "luis", "mario"], lista.Usuarios.Select(u => u.Usuario));
        Assert.Equal(PermisosDeRoles.PorDefecto([]), lista.Defecto);
        Assert.All(lista.Usuarios, u => Assert.Equal(Permisos.Acciones.Count, u.Permisos.Count));
    }

    [Fact]
    public async Task Sin_usuarios_y_permisos_no_se_ve_ni_se_cambia_nada_y_el_intento_queda()
    {
        var verlo = await Assert.ThrowsAsync<PermisoRequerido>(() => ConAsync(r => r.ListarAsync(_caja.Id, _luis.Id)));
        var cambiarlo = await Assert.ThrowsAsync<PermisoRequerido>(() => CambiarAsync(_luis.Id, _luis.Id, AccionControlada.CambiarPrecio, NivelPermiso.Permitido));

        Assert.Equal("Esto lo hace quien tiene el permiso «Usuarios y permisos»: Mario.", verlo.Message);
        Assert.Equal(NivelPermiso.NoPermitido, cambiarlo.Nivel);
        await using var db = _base.Contexto();
        Assert.Equal(1, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.EditarReglas && b.Resultado == ResultadoBitacora.Negado
            && b.UsuarioId == _luis.Id));
        Assert.Equal(NivelPermiso.RequiereAutorizacion,
            (await new ServicioControl(db).EvaluarAsync(_luis.Id, _catalogo.Matriz.Id, AccionControlada.CambiarPrecio)).Nivel);
    }

    [Fact]
    public async Task Un_cambio_vale_al_momento_y_queda_en_la_bitacora()
    {
        var lista = await CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Devolucion, NivelPermiso.Permitido, 50000);
        // Lo mismo otra vez: no cambia nada ni se anota.
        await CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Devolucion, NivelPermiso.Permitido, 50000);
        // En «Con autorización» el límite no cuenta (ni el que se mande): el que tenía, $200, se queda sin contar.
        var conAutorizacion = await CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Merma, NivelPermiso.RequiereAutorizacion, 30000);

        Assert.Equal(new Permiso(AccionControlada.Devolucion, NivelPermiso.Permitido, 50000), De(lista, _ana.Id, AccionControlada.Devolucion));
        Assert.Equal(new Permiso(AccionControlada.Merma, NivelPermiso.RequiereAutorizacion, 20000), De(conAutorizacion, _ana.Id, AccionControlada.Merma));
        Assert.Equal("Permisos propios", lista.Usuarios.Single(u => u.Id == _ana.Id).Resumen);
        await using (var db = _base.Contexto())
        {
            var control = new ServicioControl(db);
            Assert.Equal(NivelPermiso.Permitido, (await control.EvaluarAsync(_ana.Id, _catalogo.Matriz.Id, AccionControlada.Devolucion, 50000)).Nivel);
            Assert.Equal(NivelPermiso.RequiereAutorizacion, (await control.EvaluarAsync(_ana.Id, _catalogo.Matriz.Id, AccionControlada.Devolucion, 50001)).Nivel);
            Assert.Equal(NivelPermiso.RequiereAutorizacion, (await control.EvaluarAsync(_ana.Id, _catalogo.Matriz.Id, AccionControlada.Merma, 100)).Nivel);
        }

        // De vuelta en «Puede», con el límite que tenía (la pantalla lo manda): no queda sin límite.
        var otraVez = await CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Merma, NivelPermiso.Permitido,
            De(conAutorizacion, _ana.Id, AccionControlada.Merma).UmbralCentavos);

        Assert.Equal(new Permiso(AccionControlada.Merma, NivelPermiso.Permitido, 20000), De(otraVez, _ana.Id, AccionControlada.Merma));
        Assert.Equal(["Ana: «Devolución» → Puede, hasta $500.00", "Ana: «Merma» → Con autorización", "Ana: «Merma» → Puede, hasta $200.00"],
            await BitacoraAsync());
    }

    [Fact]
    public async Task Los_limites_se_revisan()
    {
        var cero = await Assert.ThrowsAsync<ArgumentException>(() => CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Devolucion, NivelPermiso.Permitido, 0));
        var demasiado = await Assert.ThrowsAsync<ArgumentException>(() => CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Descuento, NivelPermiso.Permitido, 10001));
        var descuento = await CambiarAsync(_mario.Id, _ana.Id, AccionControlada.Descuento, NivelPermiso.Permitido, 1500);

        Assert.Equal("El límite tiene que ser mayor a cero; para que no tenga límite, déjalo vacío.", cero.Message);
        Assert.Equal("El descuento llega hasta 100 %.", demasiado.Message);
        Assert.Equal(1500, De(descuento, _ana.Id, AccionControlada.Descuento).UmbralCentavos);
        Assert.Equal("Ana: «Descuento» → Puede, hasta 15 %", (await BitacoraAsync()).Single());
    }

    [Fact]
    public async Task Siempre_queda_alguien_con_usuarios_y_permisos()
    {
        // Mario es el único: no se lo puede quitar, ni con «Los de defecto» o copiando los de Ana, ni darse de baja.
        var quitarselo = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CambiarAsync(_mario.Id, _mario.Id, AccionControlada.EditarReglas, NivelPermiso.RequiereAutorizacion));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConAsync(r => r.DeDefectoAsync(_caja.Id, _mario.Id, new SolicitudUsuarios(_mario.Id))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConAsync(r => r.CopiarAsync(_caja.Id, _mario.Id, new SolicitudCopiarPermisos(_mario.Id, _ana.Id))));
        var darseDeBaja = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConAsync(r => r.ActivarAsync(_caja.Id, _mario.Id, new SolicitudActivarUsuario(_mario.Id, false))));
        Assert.Equal(RegistroUsuarios.SiempreAlguien, quitarselo.Message);
        Assert.Equal("Siempre debe quedar alguien con «Usuarios y permisos». Dáselo a otro usuario primero.", quitarselo.Message);
        Assert.Equal("No puedes darte de baja tú: pídeselo a otro usuario con «Usuarios y permisos».", darseDeBaja.Message);

        // Con Luis, sí.
        await CambiarAsync(_mario.Id, _luis.Id, AccionControlada.EditarReglas, NivelPermiso.Permitido);
        var sinEl = await CambiarAsync(_mario.Id, _mario.Id, AccionControlada.EditarReglas, NivelPermiso.NoPermitido);
        Assert.Equal(NivelPermiso.NoPermitido, De(sinEl, _mario.Id, AccionControlada.EditarReglas).Nivel);
        // Y ahora Luis es el único: dar de baja a Mario se puede; a Luis, nadie más podría.
        var sinMario = await ConAsync(r => r.ActivarAsync(_caja.Id, _mario.Id, new SolicitudActivarUsuario(_luis.Id, false)));
        Assert.False(sinMario.Usuarios.Single(u => u.Id == _mario.Id).Activo);
        Assert.Equal("Mario", sinMario.Usuarios[^1].Nombre);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CambiarAsync(_luis.Id, _luis.Id, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));
        // Un dado de baja con el permiso no cuenta: Mario lo recupera, pero sigue de baja.
        await CambiarAsync(_luis.Id, _mario.Id, AccionControlada.EditarReglas, NivelPermiso.Permitido);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CambiarAsync(_luis.Id, _luis.Id, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));
    }

    [Fact]
    public async Task Copiar_los_permisos_de_otro_todos_y_los_de_defecto()
    {
        var copiados = await ConAsync(r => r.CopiarAsync(_caja.Id, _ana.Id, new SolicitudCopiarPermisos(_mario.Id, _luis.Id)));
        Assert.Equal(copiados.Usuarios.Single(u => u.Id == _luis.Id).Permisos, copiados.Usuarios.Single(u => u.Id == _ana.Id).Permisos);
        Assert.Equal("Corte de turno, productos y clientes", copiados.Usuarios.Single(u => u.Id == _ana.Id).Resumen);

        var todos = await ConAsync(r => r.TodosAsync(_caja.Id, _ana.Id, new SolicitudUsuarios(_mario.Id)));
        Assert.Equal("Todos los permisos", todos.Usuarios.Single(u => u.Id == _ana.Id).Resumen);
        Assert.All(todos.Usuarios.Single(u => u.Id == _ana.Id).Permisos, p => Assert.True(p.SinLimite));

        var deDefecto = await ConAsync(r => r.DeDefectoAsync(_caja.Id, _ana.Id, new SolicitudUsuarios(_mario.Id)));
        Assert.Equal(deDefecto.Defecto, deDefecto.Usuarios.Single(u => u.Id == _ana.Id).Permisos);
        Assert.Equal("Permisos por defecto", deDefecto.Usuarios.Single(u => u.Id == _ana.Id).Resumen);

        await Assert.ThrowsAsync<ArgumentException>(() => ConAsync(r => r.CopiarAsync(_caja.Id, _ana.Id, new SolicitudCopiarPermisos(_mario.Id, _ana.Id))));
        Assert.Equal(["Ana ahora tiene los permisos de Luis", "Ana: todos los permisos", "Ana: los permisos por defecto"], await BitacoraAsync());
    }

    [Fact]
    public async Task Cambiar_los_de_defecto_no_toca_a_nadie_y_el_usuario_nuevo_empieza_con_ellos()
    {
        var defecto = await CambiarAsync(_mario.Id, null, AccionControlada.Merma, NivelPermiso.NoPermitido);
        Assert.Equal(NivelPermiso.NoPermitido, defecto.Defecto.Single(p => p.Accion == AccionControlada.Merma).Nivel);
        Assert.Equal(new Permiso(AccionControlada.Merma, NivelPermiso.Permitido, 20000), De(defecto, _ana.Id, AccionControlada.Merma));
        // Ana ya no tiene los de defecto: los suyos se quedaron como estaban.
        Assert.Equal("Permisos propios", defecto.Usuarios.Single(u => u.Id == _ana.Id).Resumen);

        var conBeto = await ConAsync(r => r.NuevoAsync(_caja.Id, new SolicitudNuevoUsuario(_mario.Id, "  Beto   Ruiz ", "4444")));

        var beto = conBeto.Usuarios.Single(u => u.Nombre == "Beto Ruiz");
        Assert.Equal(beto.Id, conBeto.Elegido);
        Assert.Equal(("beto", true, false, "Permisos por defecto"), (beto.Usuario, beto.Activo, beto.PrimeraCuenta, beto.Resumen));
        Assert.Equal(conBeto.Defecto, beto.Permisos);
        await using var db = _base.Contexto();
        var guardado = await db.Usuarios.SingleAsync(u => u.Id == beto.Id);
        Assert.Equal(_catalogo.Matriz.Id, guardado.SucursalId);
        Assert.Equal(ResultadoPin.Correcto, guardado.VerificarPin("4444", _base.Reloj.Ahora.UtcDateTime));
        Assert.Contains("Dio de alta a Beto Ruiz (usuario «beto»), con los permisos por defecto", await BitacoraAsync());
        Assert.DoesNotContain(await BitacoraAsync(), d => d.Contains("4444"));
    }

    [Fact]
    public async Task Un_usuario_nuevo_necesita_su_nombre_y_un_pin_de_4_cifras()
    {
        Task<UsuariosYPermisosDto> Nuevo(string nombre, string pin) =>
            ConAsync(r => r.NuevoAsync(_caja.Id, new SolicitudNuevoUsuario(_mario.Id, nombre, pin)));

        Assert.Equal("Escribe su nombre.", (await Assert.ThrowsAsync<ArgumentException>(() => Nuevo("  ", "4444"))).Message);
        Assert.Equal("Ya hay un usuario que se llama Ana: agrégale el apellido o una inicial.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Nuevo("ÁNA", "4444"))).Message);
        foreach (var pin in new[] { "123", "12a4", "12345", "" })
            Assert.Equal("El PIN es de 4 cifras.", (await Assert.ThrowsAsync<ArgumentException>(() => Nuevo("Beto", pin))).Message);
        Assert.Contains("hasta 40 letras", (await Assert.ThrowsAsync<ArgumentException>(() => Nuevo(new string('B', 41), "4444"))).Message);

        await ConAsync(r => r.ActivarAsync(_caja.Id, _ana.Id, new SolicitudActivarUsuario(_mario.Id, false)));
        Assert.Equal("Ya hay un usuario que se llama Ana, dado de baja: vuélvelo a dar de alta.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Nuevo("Ana", "4444"))).Message);
    }

    [Fact]
    public void El_usuario_sale_del_nombre_sin_repetirse()
    {
        Assert.Equal("ana", RegistroUsuarios.NombreUsuario("Ana López", []));
        Assert.Equal("analopez", RegistroUsuarios.NombreUsuario("Ana López", ["ana"]));
        Assert.Equal("ana2", RegistroUsuarios.NombreUsuario("Ana López", ["ANA", "analopez"]));
        Assert.Equal("nono", RegistroUsuarios.NombreUsuario("Ñoño", []));
        Assert.Equal("3d", RegistroUsuarios.NombreUsuario("3D Copias", []));
        Assert.Equal("usuario", RegistroUsuarios.NombreUsuario("¡!", []));
    }

    [Fact]
    public async Task Cambiar_el_pin_lo_deja_entrar_sin_esperar_y_el_pin_no_queda_en_la_bitacora()
    {
        await using (var db = _base.Contexto())
        {
            var ana = await db.Usuarios.SingleAsync(u => u.Id == _ana.Id);
            for (var i = 0; i < Usuario.IntentosPermitidos; i++)
                ana.VerificarPin("0000", _base.Reloj.Ahora.UtcDateTime);
            await db.SaveChangesAsync();
        }

        await ConAsync(r => r.CambiarPinAsync(_caja.Id, _ana.Id, new SolicitudPinUsuario(_mario.Id, "5678")));

        await using var datos = _base.Contexto();
        var conPinNuevo = await datos.Usuarios.SingleAsync(u => u.Id == _ana.Id);
        Assert.Equal(ResultadoPin.Correcto, conPinNuevo.VerificarPin("5678", _base.Reloj.Ahora.UtcDateTime));
        Assert.Equal(ResultadoPin.Incorrecto, conPinNuevo.VerificarPin("1111", _base.Reloj.Ahora.UtcDateTime));
        Assert.Equal(["Cambió el PIN de Ana"], await BitacoraAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => ConAsync(r => r.CambiarPinAsync(_caja.Id, _ana.Id, new SolicitudPinUsuario(_mario.Id, "12"))));
    }

    [Fact]
    public async Task Dado_de_baja_ya_no_hace_nada_y_al_volver_tiene_lo_que_tenia()
    {
        var sinAna = await ConAsync(r => r.ActivarAsync(_caja.Id, _ana.Id, new SolicitudActivarUsuario(_mario.Id, false)));
        Assert.False(sinAna.Usuarios.Single(u => u.Id == _ana.Id).Activo);
        await using (var db = _base.Contexto())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ServicioControl(db).EvaluarAsync(_ana.Id, _catalogo.Matriz.Id, AccionControlada.ArticuloComun));
            Assert.DoesNotContain(await new ServicioControl(db).UsuariosQueEntranAsync(_catalogo.Matriz.Id), u => u.Id == _ana.Id);
        }

        var deVuelta = await ConAsync(r => r.ActivarAsync(_caja.Id, _ana.Id, new SolicitudActivarUsuario(_mario.Id, true)));

        Assert.True(deVuelta.Usuarios.Single(u => u.Id == _ana.Id).Activo);
        Assert.Equal("Permisos por defecto", deVuelta.Usuarios.Single(u => u.Id == _ana.Id).Resumen);
        Assert.Equal(["Dio de baja a Ana", "Volvió a dar de alta a Ana"], await BitacoraAsync());
    }
}
