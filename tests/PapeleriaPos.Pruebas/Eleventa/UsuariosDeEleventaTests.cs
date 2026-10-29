using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Los usuarios de eleventa en el POS (el piloto alfa, decisión del dueño del 2026-10-25): de eleventa llegan solo su número y su
/// nombre, sin PIN y con los permisos por defecto; la importación no duplica ni toca el PIN o los permisos; y la lectura de cada
/// pocos minutos trae a los nuevos, les cambia el nombre y da de baja a los que eleventa dio de baja, sin borrar a nadie.
/// </summary>
public sealed class UsuariosDeEleventaTests : IAsyncDisposable
{
    private BaseEnMemoria _base = null!;
    private Guid _matriz;
    private Guid _caja;

    public async ValueTask DisposeAsync() => await _base.DisposeAsync();

    private async Task PrepararAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        var matriz = new Sucursal { Clave = "M", Nombre = "Matriz" };
        var caja = new Caja { SucursalId = matriz.Id, Nombre = "Caja 2", Serie = "B" };
        db.Sucursales.Add(matriz);
        db.Cajas.Add(caja);
        await db.SaveChangesAsync();
        (_matriz, _caja) = (matriz.Id, caja.Id);
    }

    private static FilaUsuario Fila(int id, string usuario, string nombre, bool activo = true, bool eliminado = false) =>
        new(id, usuario, nombre, activo, eliminado);

    private async Task<ResultadoUsuarios> AplicarAsync(params FilaUsuario[] filas)
    {
        await using var db = _base.Contexto();
        var r = await UsuariosDeEleventa.AplicarAsync(db, filas, _matriz);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<List<Usuario>> UsuariosAsync()
    {
        await using var db = _base.Contexto();
        return await db.Usuarios.AsNoTracking().OrderBy(u => u.EleventaId).ToListAsync();
    }

    /// <summary>
    /// Un usuario dado de alta aquí (en «Usuarios y permisos» o al instalar), con PIN y, si se pide, todos los permisos (como el dueño)
    /// o como la primera cuenta.
    /// </summary>
    private async Task<Usuario> DeAquiAsync(string nombre, string usuario, string pin, bool todos = false, int? eleventa = null, bool primeraCuenta = false)
    {
        await using var db = _base.Contexto();
        var nuevo = new Usuario
        {
            Nombre = nombre, NombreUsuario = usuario, SucursalId = _matriz, EleventaId = eleventa, PrimeraCuenta = primeraCuenta,
            Rol = primeraCuenta ? Rol.Dueno : Rol.Cajero,
        };
        nuevo.EstablecerPin(pin);
        db.Usuarios.Add(nuevo);
        var defecto = await new ServicioControl(db).DefectoAsync();
        db.PermisosUsuario.AddRange(Permisos.Acciones.Select(a => todos ? Permiso.Todo(a) : defecto.De(a)).Select(p => new PermisoUsuario
        {
            UsuarioId = nuevo.Id, Accion = p.Accion, Nivel = p.Nivel, UmbralCentavos = p.UmbralCentavos, PideMotivo = p.PideMotivo,
        }));
        await db.SaveChangesAsync();
        return nuevo;
    }

    [Fact]
    public async Task Los_activos_de_eleventa_llegan_sin_pin_ligados_a_su_numero_y_con_los_permisos_por_defecto()
    {
        await PrepararAsync();

        // Como en la tienda: activos con '1', dados de baja con '0', borrados, y uno con ACTIVO vacío que sigue (eleventa lo deja entrar).
        var r = await AplicarAsync(
            Fila(3, "cajera2", "María López"), Fila(7, "luis", ""), Fila(4, "pedro", "Pedro Viejo", activo: false, eliminado: true),
            Fila(5, "temporal", "De Baja", activo: false), Fila(8, "rosa", "Rosa Nula"));

        Assert.Equal((3, 0, 0, 0), (r.Nuevos, r.Ligados, r.Renombrados, r.DadosDeBaja));
        var usuarios = await UsuariosAsync();
        // Su nombre completo o, si no tiene, su usuario de eleventa; los de baja y los borrados no llegan.
        Assert.Equal([(3, "María López"), (7, "luis"), (8, "Rosa Nula")], usuarios.Select(u => (u.EleventaId!.Value, u.Nombre)));
        Assert.All(usuarios, u => Assert.False(u.TienePin));
        Assert.All(usuarios, u => Assert.True(u.Activo));
        Assert.All(usuarios, u => Assert.Equal(_matriz, u.SucursalId));
        // Su usuario del POS sale de su nombre, como el de los que se dan de alta aquí: «María López» → «maria».
        Assert.Equal(["maria", "luis", "rosa"], usuarios.Select(u => u.NombreUsuario));

        await using var db = _base.Contexto();
        var control = new ServicioControl(db);
        var defecto = await control.DefectoAsync();
        foreach (var u in usuarios)
        {
            var suyos = await control.PermisosAsync(u.Id);
            Assert.All(Permisos.Acciones, a => Assert.Equal(defecto.De(a), suyos.De(a)));
        }
        // Queda en la bitácora de dónde salió cada uno.
        Assert.Equal(3, await db.Bitacora.CountAsync(b => b.Detalle.StartsWith("Llegó de eleventa")));
    }

    [Fact]
    public async Task Repetirla_no_duplica_ni_le_toca_el_pin_ni_los_permisos()
    {
        await PrepararAsync();
        await AplicarAsync(Fila(3, "cajera2", "María López"), Fila(7, "luis", "Luis Pérez"));

        // El dueño le pone PIN y le da el corte a María.
        await using (var db = _base.Contexto())
        {
            var maria = await db.Usuarios.SingleAsync(u => u.EleventaId == 3);
            maria.EstablecerPin("4821");
            (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == maria.Id && p.Accion == AccionControlada.CorteZ)).Nivel = NivelPermiso.Permitido;
            await db.SaveChangesAsync();
        }

        var otraVez = await AplicarAsync(Fila(3, "cajera2", "María López"), Fila(7, "luis", "Luis Pérez"));

        Assert.False(otraVez.HuboCambios);
        var usuarios = await UsuariosAsync();
        Assert.Equal(2, usuarios.Count);
        var deMaria = usuarios.Single(u => u.EleventaId == 3);
        Assert.Equal(ResultadoPin.Correcto, deMaria.VerificarPin("4821", DateTime.UtcNow));
        await using var datos = _base.Contexto();
        Assert.Equal(NivelPermiso.Permitido, (await new ServicioControl(datos).PermisosAsync(deMaria.Id)).De(AccionControlada.CorteZ).Nivel);
    }

    [Fact]
    public async Task La_lectura_trae_a_los_nuevos_les_cambia_el_nombre_y_da_de_baja_sin_borrar_a_nadie()
    {
        await PrepararAsync();
        await AplicarAsync(Fila(3, "cajera2", "María López"), Fila(7, "luis", "Luis Pérez"), Fila(8, "rosa", "Rosa Ruiz"), Fila(9, "beto", "Beto"));
        await using (var db = _base.Contexto())
        {
            (await db.Usuarios.SingleAsync(u => u.EleventaId == 7)).EstablecerPin("1357");
            await db.SaveChangesAsync();
        }

        // En eleventa: María cambió de nombre, a Luis lo dieron de baja, a Rosa la borraron, Beto ya no está y llegó Carla.
        await using (var db = _base.Contexto())
        {
            var r = await SincronizacionEleventa.AplicarAsync(db, new DatosEleventa
            {
                Usuarios = [Fila(3, "cajera2", "María López Ruiz"), Fila(7, "luis", "Luis Pérez", activo: false),
                    Fila(8, "rosa", "Rosa Ruiz", activo: false, eliminado: true), Fila(10, "carla", "Carla Díaz")],
            }, DateTime.UtcNow);
            Assert.Equal((1, 1, 3), (r.Usuarios.Nuevos, r.Usuarios.Renombrados, r.Usuarios.DadosDeBaja));
            Assert.True(r.HuboCambios);
            Assert.Contains("usuarios:", r.ToString());
        }

        var usuarios = await UsuariosAsync();
        // Nadie se borra: los dados de baja se quedan, con su PIN.
        Assert.Equal([3, 7, 8, 9, 10], usuarios.Select(u => u.EleventaId!.Value));
        Assert.Equal("María López Ruiz", usuarios.Single(u => u.EleventaId == 3).Nombre);
        Assert.Equal([3, 10], usuarios.Where(u => u.Activo).Select(u => u.EleventaId!.Value));
        Assert.True(usuarios.Single(u => u.EleventaId == 7).TienePin);
        Assert.False(usuarios.Single(u => u.EleventaId == 10).TienePin);
    }

    /// <summary>
    /// Volver a darlo de alta en eleventa no le regresa la entrada aquí (decidido, por revisar): un PIN de antes no vuelve a entrar
    /// sin que el dueño lo vea. Lo regresa él en «Usuarios y permisos».
    /// </summary>
    [Fact]
    public async Task Volver_a_darlo_de_alta_en_eleventa_no_lo_regresa_solo()
    {
        await PrepararAsync();
        await AplicarAsync(Fila(7, "luis", "Luis Pérez"));
        await AplicarAsync(Fila(7, "luis", "Luis Pérez", activo: false));

        var r = await AplicarAsync(Fila(7, "luis", "Luis Pérez"));

        Assert.False(r.HuboCambios);
        Assert.False((await UsuariosAsync()).Single().Activo);
    }

    [Fact]
    public async Task Los_dados_de_alta_aqui_siguen_y_el_que_se_llama_igual_que_uno_de_eleventa_se_liga_con_su_pin()
    {
        await PrepararAsync();
        var ana = await DeAquiAsync("Ana Ruiz", "ana", "2468");
        var beto = await DeAquiAsync("Beto Solo Aquí", "beto", "1122");

        var r = await AplicarAsync(Fila(7, "cajera2", "ANA RUIZ"), Fila(9, "carla", "Carla"));

        Assert.Equal((1, 1), (r.Ligados, r.Nuevos));
        var usuarios = await UsuariosAsync();
        // Ana es la misma persona: se liga a su número, con su PIN y sus permisos; el nombre, como en eleventa.
        var deAna = usuarios.Single(u => u.Id == ana.Id);
        Assert.Equal((7, "ANA RUIZ"), (deAna.EleventaId, deAna.Nombre));
        Assert.Equal(ResultadoPin.Correcto, deAna.VerificarPin("2468", DateTime.UtcNow));
        // Beto no está en eleventa: sigue como estaba, activo y sin número (lo suyo va a eleventa a nombre del cajero de la configuración).
        var deBeto = usuarios.Single(u => u.Id == beto.Id);
        Assert.Equal((null, true), (deBeto.EleventaId, deBeto.Activo));
        Assert.Equal(3, usuarios.Count);
    }

    [Fact]
    public async Task Nunca_deja_a_la_tienda_sin_quien_de_los_pin()
    {
        await PrepararAsync();
        var duenio = await DeAquiAsync("Luis Pérez", "luis", "9999", todos: true, eleventa: 7);

        // Eleventa lo borra: aquí es el único que da los PIN y los permisos, así que se queda (con un aviso).
        await using (var db = _base.Contexto())
        {
            var r = await SincronizacionEleventa.AplicarAsync(db, new DatosEleventa { Usuarios = [Fila(7, "luis", "Luis Pérez", activo: false, eliminado: true)] },
                DateTime.UtcNow);
            Assert.Equal(0, r.Usuarios.DadosDeBaja);
            Assert.Contains(UsuariosDeEleventa.AvisoSeQuedaActivo, r.Avisos.Keys);
        }
        Assert.True((await UsuariosAsync()).Single(u => u.Id == duenio.Id).Activo);

        // Con otro que también los da, sí se le da de baja.
        await DeAquiAsync("Encargada", "encargada", "8888", todos: true);
        var despues = await AplicarAsync(Fila(7, "luis", "Luis Pérez", activo: false, eliminado: true));
        Assert.Equal(1, despues.DadosDeBaja);
        Assert.False((await UsuariosAsync()).Single(u => u.Id == duenio.Id).Activo);
    }

    /// <summary>
    /// Revisión del PR #144 (MEDIA). Ana se fue y el dueño la dio de baja aquí; llega a eleventa otra «Ana Ruiz». Antes se ligaba a la
    /// cuenta de la que se fue: la nueva no tenía ficha (la cuenta seguía de baja) y, si el dueño «la» volvía a dar de alta, entraba
    /// con el PIN de la de antes, justo lo que la regla de las bajas quiere evitar. Ahora llega con su propia ficha, sin PIN.
    /// </summary>
    [Fact]
    public async Task Uno_dado_de_baja_aqui_no_se_liga_al_nuevo_de_eleventa_que_se_llama_igual()
    {
        await PrepararAsync();
        var anterior = await DeAquiAsync("Ana Ruiz", "ana", "2468");
        await using (var db = _base.Contexto())
        {
            (await db.Usuarios.SingleAsync(u => u.Id == anterior.Id)).Activo = false;
            await db.SaveChangesAsync();
        }

        var r = await AplicarAsync(Fila(20, "ana.ruiz", "Ana Ruiz"));

        Assert.Equal((0, 1), (r.Ligados, r.Nuevos));
        await using var datos = _base.Contexto();
        var nueva = await datos.Usuarios.AsNoTracking().SingleAsync(u => u.EleventaId == 20);
        Assert.True(nueva.Activo, "La de eleventa quedó ligada a la cuenta dada de baja de la que se fue: no tiene ficha");
        Assert.False(nueva.TienePin, "La de eleventa quedó con el PIN de la que se fue");
        Assert.NotEqual(anterior.Id, nueva.Id);
        Assert.Equal("Ana Ruiz (ana.ruiz)", nueva.Nombre);
        Assert.False((await datos.Usuarios.AsNoTracking().SingleAsync(u => u.Id == anterior.Id)).Activo);
    }

    /// <summary>
    /// Revisión del PR #144 (MEDIA). El dueño se dio de alta aquí como «Mario» (la primera cuenta, con todos los permisos) y en eleventa
    /// hay otro «MARIO», un cajero. Antes la lectura ligaba al dueño con ese cajero: lo que vendía el dueño iba a eleventa a nombre
    /// del cajero y, cuando el cajero se iba y eleventa lo daba de baja, el dueño quedaba dado de baja aquí (había otra que daba los
    /// PIN), sin forma de deshacerlo en la pantalla. Ahora el cajero llega con su propia ficha.
    /// </summary>
    [Fact]
    public async Task La_primera_cuenta_no_se_liga_sola_a_otro_de_eleventa_que_se_llama_igual()
    {
        await PrepararAsync();
        var duenio = await DeAquiAsync("Mario", "mario", "9999", todos: true, primeraCuenta: true);
        await DeAquiAsync("Encargada", "encargada", "8888", todos: true);

        // En eleventa, «MARIO» es otro: un cajero, el número 12. Después se va y eleventa lo da de baja.
        await AplicarAsync(Fila(12, "cajero12", "MARIO"));
        await AplicarAsync(Fila(12, "cajero12", "MARIO", activo: false));

        await using var db = _base.Contexto();
        var suyo = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == duenio.Id);
        Assert.True(suyo.Activo, "La baja del cajero 12 en eleventa dio de baja al dueño (la primera cuenta, con todos los permisos)");
        Assert.Null(await CajeroDeEleventa.DeAsync(db, duenio.Id));
        var cajero = await db.Usuarios.AsNoTracking().SingleAsync(u => u.EleventaId == 12);
        Assert.Equal(("MARIO (cajero12)", false, false), (cajero.Nombre, cajero.Activo, cajero.TienePin));
    }

    /// <summary>
    /// Revisión del PR #144: solo se liga por nombre quien no deja duda de ser la misma persona y a quien ligarlo no le cambia nada
    /// importante (decidido, por revisar). Ni quien tiene «Usuarios y permisos» (aunque no sea la primera cuenta), ni uno que ya tiene
    /// su número de eleventa, ni cuando el nombre es de dos aquí o de dos en eleventa: ahí cada uno de eleventa llega con su ficha.
    /// </summary>
    [Fact]
    public async Task Solo_se_liga_por_nombre_sin_duda_y_sin_usuarios_y_permisos()
    {
        await PrepararAsync();
        var encargada = await DeAquiAsync("Rosa Díaz", "rosa", "8888", todos: true);
        var conNumero = await DeAquiAsync("Beto Gómez", "beto", "1122", eleventa: 30);
        await DeAquiAsync("Carla", "carla", "3344");
        await DeAquiAsync("CARLA", "carla2", "5566");
        var lupe = await DeAquiAsync("Lupe", "lupe", "7788");
        var pablo = await DeAquiAsync("Pablo", "pablo", "9900");

        var r = await AplicarAsync(Fila(30, "beto", "Beto Gómez"), Fila(21, "rosa", "Rosa Díaz"), Fila(22, "beto2", "Beto Gómez"),
            Fila(23, "carla", "Carla"), Fila(24, "lupe", "Lupe"), Fila(25, "lupe2", "LUPE"), Fila(26, "pablo", "PABLO"));

        // Solo Pablo, un cajero sin duda: se liga con su PIN.
        Assert.Equal((1, 5), (r.Ligados, r.Nuevos));
        await using var db = _base.Contexto();
        var usuarios = await db.Usuarios.AsNoTracking().ToListAsync();
        Assert.Equal(26, usuarios.Single(u => u.Id == pablo.Id).EleventaId);
        Assert.Equal([null, 30, null], new[] { encargada.Id, conNumero.Id, lupe.Id }.Select(id => usuarios.Single(u => u.Id == id).EleventaId));
        Assert.All(usuarios.Where(u => u.EleventaId is 21 or 22 or 23 or 24 or 25), u => Assert.False(u.TienePin));
    }

    /// <summary>
    /// Revisión del PR #144 (BAJA). La lectura decidía a quién dar de baja con lo que leyó al empezar, fuera de la transacción con que
    /// «Usuarios y permisos» cuida que siempre quede alguien: si mientras tanto el dueño le quitaba «Usuarios y permisos» a la otra
    /// (todavía quedaba él), la lectura lo daba de baja de todos modos y ya nadie podía dar los PIN ni los permisos. Ahora se decide
    /// al guardar, con el candado de escritura.
    /// </summary>
    [Fact]
    public async Task La_lectura_y_usuarios_y_permisos_al_mismo_tiempo_no_dejan_a_la_tienda_sin_quien_de_los_pin()
    {
        await PrepararAsync();
        var duenio = await DeAquiAsync("Luis Pérez", "luis", "9999", todos: true, eleventa: 7);
        var encargada = await DeAquiAsync("Encargada", "encargada", "8888", todos: true);

        // La lectura empieza: eleventa dio de baja a Luis, y con la encargada todavía hay otra que da los PIN.
        await using var lectura = _base.Contexto();
        var r = await UsuariosDeEleventa.AplicarAsync(lectura, [Fila(7, "luis", "Luis Pérez", activo: false)], _matriz);

        // Mientras, en «Usuarios y permisos», Luis le quita «Usuarios y permisos» a la encargada: se puede, porque queda él.
        await using (var pantalla = _base.Contexto())
            await new RegistroUsuarios(pantalla).CambiarAsync(_caja, encargada.Id,
                new SolicitudPermisoUsuario(duenio.Id, AccionControlada.EditarReglas, NivelPermiso.NoPermitido));

        // La lectura guarda lo que decidió.
        await lectura.SaveChangesAsync();

        await using var db = _base.Contexto();
        Assert.True(await RegistroUsuarios.QuedaAlguienAsync(db, null), "Nadie quedó activo, con PIN y con «Usuarios y permisos»");
        Assert.True((await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == duenio.Id)).Activo);
        Assert.Equal(0, r.DadosDeBaja);
        Assert.Contains(UsuariosDeEleventa.AvisoSeQuedaActivo, r.Avisos);
    }

    /// <summary>
    /// Revisión del PR #144: quien da los PIN se cuenta en la sucursal de ese eleventa (los suyos y los de todas). Antes contaba a los
    /// de todas las sucursales: con alguien que daba los PIN en otra, se daba de baja al único de esta.
    /// </summary>
    [Fact]
    public async Task Quien_da_los_pin_se_cuenta_en_la_sucursal_de_ese_eleventa()
    {
        await PrepararAsync();
        var luis = await DeAquiAsync("Luis Pérez", "luis", "9999", todos: true, eleventa: 7);
        await using (var db = _base.Contexto())
        {
            var otra = new Sucursal { Clave = "S2", Nombre = "Sucursal 2" };
            db.Sucursales.Add(otra);
            await db.SaveChangesAsync();
            var deLaOtra = new Usuario { Nombre = "Encargado de la 2", NombreUsuario = "encargado2", SucursalId = otra.Id };
            deLaOtra.EstablecerPin("4444");
            db.Usuarios.Add(deLaOtra);
            db.PermisosUsuario.AddRange(Permisos.Acciones.Select(a => new PermisoUsuario { UsuarioId = deLaOtra.Id, Accion = a, Nivel = NivelPermiso.Permitido }));
            await db.SaveChangesAsync();
        }

        var r = await AplicarAsync(Fila(7, "luis", "Luis Pérez", activo: false));

        Assert.Equal(0, r.DadosDeBaja);
        Assert.Contains(UsuariosDeEleventa.AvisoSeQuedaActivo, r.Avisos);
        Assert.True((await UsuariosAsync()).Single(u => u.Id == luis.Id).Activo);
    }

    [Fact]
    public async Task Dos_de_eleventa_con_el_mismo_nombre_no_salen_iguales_y_un_nombre_largo_cabe_en_su_ficha()
    {
        await PrepararAsync();

        await AplicarAsync(Fila(3, "ana", "Ana"), Fila(9, "ana2", "Ana"),
            Fila(11, "larga", "Guadalupe Esperanza de los Ángeles Martínez Hernández"));

        var usuarios = await UsuariosAsync();
        Assert.Equal(["Ana", "Ana (ana2)"], usuarios.Where(u => u.EleventaId is 3 or 9).Select(u => u.Nombre));
        var larga = usuarios.Single(u => u.EleventaId == 11).Nombre;
        Assert.True(larga.Length <= FichasEntrada.LargoNombre, larga);
        Assert.StartsWith("Guadalupe Esperanza", larga);
        // Los usuarios con que se entra escribiéndolo no se repiten.
        Assert.Equal(usuarios.Count, usuarios.Select(u => u.NombreUsuario).Distinct().Count());
    }

    [Fact]
    public async Task Sin_los_usuarios_de_eleventa_no_se_toca_a_nadie()
    {
        await PrepararAsync();
        await AplicarAsync(Fila(7, "luis", "Luis Pérez"));

        // Una lectura que no trajo usuarios (null) o que no vio ninguno: eleventa sin usuarios no deja entrar a nadie, no es eso.
        await using (var db = _base.Contexto())
            Assert.False((await SincronizacionEleventa.AplicarAsync(db, new DatosEleventa(), DateTime.UtcNow)).Usuarios.HuboCambios);
        Assert.False((await AplicarAsync()).HuboCambios);

        Assert.True((await UsuariosAsync()).Single().Activo);
    }

    /// <summary>De eleventa solo se traen el número y el nombre: la consulta no pide la clave ni los permisos, y la fila no tiene dónde.</summary>
    [Fact]
    public void De_eleventa_no_se_lee_la_clave_ni_los_permisos()
    {
        Assert.DoesNotContain("CLAVE", LectorEleventa.ConsultaUsuarios, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PERMISOS", LectorEleventa.ConsultaUsuarios, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", LectorEleventa.ConsultaUsuarios);
        Assert.Equal(["Id", "Usuario", "Nombre", "Activo", "Eliminado"], typeof(FilaUsuario).GetConstructors().Single().GetParameters().Select(p => p.Name));
    }
}
