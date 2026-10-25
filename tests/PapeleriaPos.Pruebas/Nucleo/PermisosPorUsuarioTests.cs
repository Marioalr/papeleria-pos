using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>Los permisos por usuario (entrega 1): cada quien puede lo que dicen sus permisos, y nada más.</summary>
public class PermisosPorUsuarioTests
{
    private static readonly Guid Matriz = Ids.Nuevo();

    private static PermisosDeUsuario Con(params Permiso[] permisos) => new(permisos);

    [Fact]
    public void Con_umbral_lo_que_pasa_pide_autorizacion_y_el_motivo_se_respeta()
    {
        var ana = Con(new Permiso(AccionControlada.Devolucion, NivelPermiso.Permitido, 20000),
            new Permiso(AccionControlada.Reimprimir, NivelPermiso.Permitido, PideMotivo: true));

        Assert.Equal(NivelPermiso.Permitido, Permisos.Evaluar(ana, AccionControlada.Devolucion, 20000).Nivel);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Permisos.Evaluar(ana, AccionControlada.Devolucion, 20001).Nivel);
        // Sin monto (para pintar la pantalla), lo que tiene en «Puede».
        Assert.Equal(NivelPermiso.Permitido, Permisos.Evaluar(ana, AccionControlada.Devolucion).Nivel);
        Assert.True(Permisos.Evaluar(ana, AccionControlada.Reimprimir).PideMotivo);
    }

    [Fact]
    public void Nadie_puede_todo_por_ser_quien_es_lo_que_no_tiene_no_lo_puede()
    {
        var sinNada = Con();

        Assert.All(Permisos.Acciones, a => Assert.Equal(NivelPermiso.NoPermitido, Permisos.Evaluar(sinNada, a, 1).Nivel));
        // Y la primera cuenta solo tiene todo porque así quedaron sus permisos.
        var todos = new PermisosDeUsuario(Permisos.Acciones.Select(Permiso.Todo));
        Assert.True(todos.TieneTodos);
        Assert.All(Permisos.Acciones, a => Assert.Equal(new DecisionPermiso(NivelPermiso.Permitido, false), Permisos.Evaluar(todos, a, long.MaxValue)));
    }

    [Fact]
    public void Quien_autoriza_va_de_quien_puede_menos_a_quien_puede_todo()
    {
        var defecto = PermisosDeRoles.PorDefecto([]);
        var (ana, carmen, luis, beto) = (Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo());
        // Carmen: los de defecto más cancelar, el descuento sin límite y el corte; Beto: solo cancelar de más.
        var deCarmen = new PermisosDeUsuario(defecto.Select(p => p.Accion is AccionControlada.CancelarTicket or AccionControlada.Descuento
            or AccionControlada.CorteZ ? Permiso.Todo(p.Accion) : p));
        var deBeto = new PermisosDeUsuario(defecto.Select(p => p.Accion == AccionControlada.CancelarTicket ? Permiso.Todo(p.Accion) : p));
        UsuarioConPermisos[] usuarios =
        [
            new(luis, "luis", "Luis", new PermisosDeUsuario(Permisos.Acciones.Select(Permiso.Todo))),
            new(ana, "ana", "Ana", new PermisosDeUsuario(defecto)),
            new(carmen, "carmen", "Carmen", deCarmen),
            new(beto, "beto", "Beto", deBeto),
        ];

        var sesion = Permisos.DeLaSesion(new PermisosDeUsuario(defecto), usuarios, ana).ToDictionary(p => p.Accion);

        Assert.Equal(Permisos.Acciones.Count, sesion.Count);
        // Beto puede menos que Carmen, y Luis lo puede todo: va al final. Ana (la de la sesión) no.
        Assert.Equal(["Beto", "Carmen", "Luis"], sesion[AccionControlada.CancelarTicket].Autorizan.Select(a => a.Nombre));
        Assert.Equal(["beto", "carmen", "luis"], sesion[AccionControlada.CancelarTicket].Autorizan.Select(a => a.Usuario));
        // El cambio de precio, solo quien lo puede hacer.
        Assert.Equal(["Luis"], sesion[AccionControlada.CambiarPrecio].Autorizan.Select(a => a.Nombre));
        // Con límite: hasta ahí puede, y lo que pasa lo autoriza quien puede más.
        Assert.Equal((NivelPermiso.Permitido, 1000L), (sesion[AccionControlada.Descuento].Nivel, sesion[AccionControlada.Descuento].UmbralCentavos!.Value));
        Assert.Equal(["Carmen", "Luis"], sesion[AccionControlada.Descuento].Autorizan.Select(a => a.Nombre));
        // Lo que puede sin límite y lo que no puede: nadie autoriza.
        Assert.Empty(sesion[AccionControlada.ArticuloComun].Autorizan);
        Assert.Equal(NivelPermiso.NoPermitido, sesion[AccionControlada.CorteZ].Nivel);
        Assert.Empty(sesion[AccionControlada.CorteZ].Autorizan);
        // Quién puede hacer el corte, en el mismo orden.
        Assert.Equal(["Carmen", "Luis"], Permisos.QuienesPueden(usuarios, AccionControlada.CorteZ).Select(q => q.Nombre));
    }

    [Fact]
    public void La_pantalla_tiene_cada_accion_una_vez_con_su_nombre_menos_el_cajon()
    {
        var enPantalla = Permisos.Grupos.SelectMany(g => g.Acciones).ToList();

        Assert.Equal(enPantalla.Count, enPantalla.Distinct().Count());
        Assert.Equal(Permisos.Acciones.Where(a => a != AccionControlada.AbrirCajonSinVenta).Order(), enPantalla.Order());
        Assert.Equal(["Venta", "Efectivo y corte", "Inventario", "Clientes y crédito", "Reportes", "Administración"], Permisos.Grupos.Select(g => g.Nombre));
        Assert.Equal(Permisos.Acciones.Count, Permisos.Acciones.Select(Permisos.Nombre).Distinct().Count());
        Assert.Equal("Usuarios y permisos", Permisos.Nombre(AccionControlada.EditarReglas));
        Assert.Equal("Ver costos, utilidad y margen", Permisos.Nombre(AccionControlada.VerUtilidad));
    }

    /// <summary>
    /// La caja guarda las acciones por su número (la bandeja sin conexión) y la base por su nombre: ni se reordenan ni se
    /// renombran, y las nuevas van al final.
    /// </summary>
    [Fact]
    public void Las_acciones_conservan_su_numero_y_su_nombre()
    {
        Assert.Equal(0, (int)AccionControlada.QuitarProducto);
        Assert.Equal(13, (int)AccionControlada.EditarReglas);
        Assert.Equal(18, (int)AccionControlada.Descuento);
        Assert.Equal(36, (int)AccionControlada.CorteDelDia);
        Assert.Equal((37, 38, 39), ((int)AccionControlada.VerUtilidad, (int)AccionControlada.VerReportes, (int)AccionControlada.ConfigurarTienda));
        Assert.Equal(40, (int)AccionControlada.CancelarMovimiento);
        Assert.Equal(41, (int)AccionControlada.AgregarInventario);
        Assert.Equal("EditarReglas", AccionControlada.EditarReglas.ToString());
        Assert.Equal(42, Permisos.Acciones.Count);
    }

    // ── De los roles a los permisos (la migración) ───────────────────────────

    [Fact]
    public void El_dueno_queda_con_todo_y_el_cajero_con_las_reglas_iniciales()
    {
        var dueno = new PermisosDeUsuario(PermisosDeRoles.De(Rol.Dueno, [], Matriz));
        var cajero = new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, [], Matriz));

        Assert.True(dueno.TieneTodos);
        Assert.Equal(new Permiso(AccionControlada.Devolucion, NivelPermiso.Permitido, 20000), cajero.De(AccionControlada.Devolucion));
        Assert.Equal(new Permiso(AccionControlada.Descuento, NivelPermiso.Permitido, 1000), cajero.De(AccionControlada.Descuento));
        Assert.Equal(new Permiso(AccionControlada.Reimprimir, NivelPermiso.Permitido, PideMotivo: true), cajero.De(AccionControlada.Reimprimir));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, cajero.De(AccionControlada.CambiarPrecio).Nivel);
        // Los de defecto son los del cajero.
        Assert.Equal(PermisosDeRoles.PorDefecto([]), cajero.Lista());
    }

    [Fact]
    public void Lo_que_era_solo_del_dueno_queda_en_no_para_los_demas_aunque_tuviera_regla()
    {
        ReglaPermiso[] reglas = [new() { Accion = AccionControlada.EditarReglas, Rol = Rol.Encargado, Nivel = NivelPermiso.Permitido }];

        var encargado = new PermisosDeUsuario(PermisosDeRoles.De(Rol.Encargado, reglas, Matriz));

        Assert.All([AccionControlada.EditarReglas, AccionControlada.AdministrarCajas, AccionControlada.ConfigurarTelegram, AccionControlada.ConfigurarImpresora,
                AccionControlada.ConfigurarMargen, AccionControlada.VerUtilidad, AccionControlada.VerReportes, AccionControlada.ConfigurarTienda],
            a => Assert.Equal(NivelPermiso.NoPermitido, encargado.De(a).Nivel));
    }

    [Fact]
    public void La_regla_de_la_sucursal_gana_sobre_la_general_y_los_de_defecto_solo_toman_la_general()
    {
        var otra = Ids.Nuevo();
        ReglaPermiso[] reglas =
        [
            new() { Accion = AccionControlada.Reimprimir, Rol = Rol.Cajero, Nivel = NivelPermiso.RequiereAutorizacion },
            new() { Accion = AccionControlada.Reimprimir, Rol = Rol.Cajero, SucursalId = Matriz, Nivel = NivelPermiso.NoPermitido },
        ];

        Assert.Equal(NivelPermiso.NoPermitido, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, reglas, Matriz)).De(AccionControlada.Reimprimir).Nivel);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, reglas, otra)).De(AccionControlada.Reimprimir).Nivel);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, new PermisosDeUsuario(PermisosDeRoles.PorDefecto(reglas)).De(AccionControlada.Reimprimir).Nivel);
    }

    [Fact]
    public void En_administracion_solo_hay_puede_o_no_y_con_autorizacion_vale_no()
    {
        // Dueño, 2026-10-15: en Administración no hay paso para pedir la autorización; en la venta sigue como siempre. De las
        // nuevas, «Agregar inventario» (2026-10-24) tampoco lo tiene.
        var administracion = Permisos.Grupos.Single(g => g.Nombre == "Administración").Acciones;
        var cajero = Con(
            new Permiso(AccionControlada.ConfigurarTicket, NivelPermiso.RequiereAutorizacion),
            new Permiso(AccionControlada.CambiarPrecio, NivelPermiso.RequiereAutorizacion),
            new Permiso(AccionControlada.AgregarInventario, NivelPermiso.RequiereAutorizacion));

        Assert.Equal(administracion.Append(AccionControlada.AgregarInventario).Order(), Permisos.Acciones.Where(Permisos.SoloPuedeONo).Order());
        Assert.Equal(NivelPermiso.NoPermitido, Permisos.Evaluar(cajero, AccionControlada.ConfigurarTicket).Nivel);
        Assert.Equal(NivelPermiso.NoPermitido, cajero.De(AccionControlada.ConfigurarTicket).Nivel);
        Assert.Equal(NivelPermiso.NoPermitido, cajero.De(AccionControlada.AgregarInventario).Nivel);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Permisos.Evaluar(cajero, AccionControlada.CambiarPrecio).Nivel);
    }

    /// <summary>
    /// «Agregar inventario» (2026-10-24) va junto a «Recibir mercancía a ciegas», en Inventario, y sale de un rol como ella: quien
    /// recibe en «Puede» la tiene en «Puede»; quien recibe con autorización o no recibe, «No».
    /// </summary>
    [Fact]
    public void Agregar_inventario_va_junto_a_recibir_y_sale_de_un_rol_como_recibir()
    {
        var inventario = Permisos.Grupos.Single(g => g.Nombre == "Inventario").Acciones;
        ReglaPermiso[] reglas =
        [
            new() { Accion = AccionControlada.RecibirMercancia, Rol = Rol.Cajero, Nivel = NivelPermiso.RequiereAutorizacion },
            new() { Accion = AccionControlada.RecibirMercancia, Rol = Rol.Encargado, SucursalId = Matriz, Nivel = NivelPermiso.NoPermitido },
        ];

        Assert.Equal([AccionControlada.RecibirMercancia, AccionControlada.AgregarInventario], inventario.Take(2));
        Assert.Equal("Agregar inventario", Permisos.Nombre(AccionControlada.AgregarInventario));
        Assert.Equal(AccionControlada.RecibirMercancia, PermisosDeRoles.Sigue(AccionControlada.AgregarInventario));
        Assert.Null(PermisosDeRoles.Sigue(AccionControlada.RecibirMercancia));
        // Con las reglas iniciales, los dos reciben: los dos agregan.
        Assert.Equal(NivelPermiso.Permitido, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, [], Matriz)).De(AccionControlada.AgregarInventario).Nivel);
        Assert.Equal(NivelPermiso.Permitido, new PermisosDeUsuario(PermisosDeRoles.PorDefecto([])).De(AccionControlada.AgregarInventario).Nivel);
        // Con otras reglas para recibir, la siguen: «Con autorización» o «No» dan «No».
        Assert.Equal(NivelPermiso.NoPermitido, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Cajero, reglas, Matriz)).De(AccionControlada.AgregarInventario).Nivel);
        Assert.Equal(NivelPermiso.NoPermitido, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Encargado, reglas, Matriz)).De(AccionControlada.AgregarInventario).Nivel);
        Assert.Equal(NivelPermiso.Permitido, new PermisosDeUsuario(PermisosDeRoles.De(Rol.Encargado, reglas, Ids.Nuevo())).De(AccionControlada.AgregarInventario).Nivel);
        Assert.True(new PermisosDeUsuario(PermisosDeRoles.De(Rol.Dueno, reglas, Matriz)).De(AccionControlada.AgregarInventario).SinLimite);
    }
}
