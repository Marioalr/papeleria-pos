using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>Las fichas de la entrada (entrega 6): la letra de cada quien, lo que puede hacer y quién hace el corte.</summary>
public class FichasEntradaTests
{
    private static readonly Guid Matriz = Guid.NewGuid();

    /// <summary>Un Id como los de la base (UUIDv7): lleva la fecha de alta al principio.</summary>
    private static Guid DadoDeAlta(int minuto) => Ids.Version7(new DateTimeOffset(2026, 10, 1, 9, minuto, 0, TimeSpan.Zero), new byte[10]);

    private static readonly (Guid Id, string Nombre)[] Tienda =
    [
        (DadoDeAlta(1), "Carmen"),
        (DadoDeAlta(2), "Carlos Pérez"),
        (DadoDeAlta(3), "Cecilia"),
        (DadoDeAlta(4), "Ana"),
        (DadoDeAlta(5), "Ángel"),
    ];

    [Fact]
    public void Cada_quien_su_inicial_y_si_ya_es_de_otro_la_de_su_apellido_o_la_siguiente_letra_de_su_nombre()
    {
        var teclas = FichasEntrada.Teclas(Tienda);

        // Carmen se dio de alta primero y se queda con la C; Carlos, con la de su apellido; Cecilia, con la siguiente letra de
        // su nombre; Ángel, sin acento, ya no tiene la A (es de Ana) y toma la N.
        Assert.Equal(["C", "P", "E", "A", "N"], Tienda.Select(u => teclas[u.Id]));
    }

    [Fact]
    public void Las_letras_no_cambian_al_dar_de_alta_a_alguien_ni_dependen_del_orden_de_la_lista()
    {
        var antes = FichasEntrada.Teclas(Tienda);
        var alReves = FichasEntrada.Teclas(Enumerable.Reverse(Tienda));
        var conCarla = FichasEntrada.Teclas([.. Tienda, (DadoDeAlta(6), "Carla")]);

        Assert.Equal(antes, alReves);
        Assert.All(Tienda, u => Assert.Equal(antes[u.Id], conCarla[u.Id]));
        // La C y la A ya son de otros: Carla toma la R, la que sigue en su nombre.
        Assert.Equal("R", conCarla[DadoDeAlta(6)]);
        Assert.Equal(conCarla.Count, conCarla.Values.Distinct().Count());
    }

    [Fact]
    public void Solo_son_letras_y_con_mas_de_26_los_que_siguen_se_tocan()
    {
        var usuarios = Enumerable.Range(1, 27).Select(i => (DadoDeAlta(i), $"Usuario {i}")).Append((DadoDeAlta(40), "3D Copias")).ToList();

        var teclas = FichasEntrada.Teclas(usuarios);

        Assert.Equal(26, teclas.Count);
        Assert.Equal(26, teclas.Values.Distinct().Count());
        Assert.All(teclas.Values, t => Assert.Matches("^[A-Z]$", t));
        // Los de las primeras 26 altas tienen letra; los últimos, no.
        Assert.False(teclas.ContainsKey(DadoDeAlta(27)));
        Assert.False(teclas.ContainsKey(DadoDeAlta(40)));
        // Sin tantos, «3D Copias» no toma el 3 (las cifras son del PIN): toma la D.
        Assert.Equal("D", FichasEntrada.Teclas([(DadoDeAlta(40), "3D Copias")])[DadoDeAlta(40)]);
    }

    /// <summary>Los permisos con que queda alguien de ese rol de antes (la migración), con las reglas que se den.</summary>
    private static PermisosDeUsuario De(Rol rol, params ReglaPermiso[] reglas) => new(PermisosDeRoles.De(rol, reglas, Matriz));

    private static readonly PermisosDeUsuario Defecto = new(PermisosDeRoles.PorDefecto([]));

    [Fact]
    public void El_resumen_dice_todos_los_de_defecto_o_lo_que_tiene_de_mas()
    {
        Assert.Equal("Todos los permisos", FichasEntrada.Resumen(De(Rol.Dueno), Defecto));
        Assert.Equal("Permisos por defecto", FichasEntrada.Resumen(De(Rol.Cajero), Defecto));
        Assert.Equal("Corte de turno, productos y clientes", FichasEntrada.Resumen(De(Rol.Encargado), Defecto));

        // Si el encargado ya no hace el corte, se dice lo que sigue.
        var sinCorte = new ReglaPermiso { Accion = AccionControlada.CorteZ, Rol = Rol.Encargado, SucursalId = Matriz, Nivel = NivelPermiso.NoPermitido };
        Assert.Equal("Productos, clientes y compras", FichasEntrada.Resumen(De(Rol.Encargado, sinCorte), Defecto));
        // Con los de defecto, pero con otro límite: ya no son los de defecto.
        var otroLimite = new PermisosDeUsuario(Defecto.Lista().Select(p => p.Accion == AccionControlada.Descuento ? p with { UmbralCentavos = 1500 } : p));
        Assert.Equal("Permisos propios", FichasEntrada.Resumen(otroLimite, Defecto));
        // Fuera de «Puede» el límite que se quedó guardado no cuenta ni se ve: siguen siendo los de defecto.
        var guardado = new PermisosDeUsuario(Defecto.Lista().Select(p => p.Accion == AccionControlada.CambiarPrecio ? p with { UmbralCentavos = 5000 } : p));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Defecto.De(AccionControlada.CambiarPrecio).Nivel);
        Assert.Equal("Permisos por defecto", FichasEntrada.Resumen(guardado, Defecto));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Permisos.Evaluar(guardado, AccionControlada.CambiarPrecio, 100).Nivel);
    }

    [Fact]
    public void Hace_el_corte_quien_lo_tiene_en_puede()
    {
        Assert.False(FichasEntrada.HaceCorte(De(Rol.Cajero)));
        Assert.True(FichasEntrada.HaceCorte(De(Rol.Encargado)));
        Assert.True(FichasEntrada.HaceCorte(De(Rol.Dueno)));

        var cajeroCorta = new ReglaPermiso { Accion = AccionControlada.CorteZ, Rol = Rol.Cajero, SucursalId = Matriz, Nivel = NivelPermiso.Permitido };
        Assert.True(FichasEntrada.HaceCorte(De(Rol.Cajero, cajeroCorta)));
    }

    [Fact]
    public void Las_fichas_van_por_nombre_y_la_del_turno_abierto_lo_dice()
    {
        var (cajero, encargado, dueno) = (DadoDeAlta(1), DadoDeAlta(2), DadoDeAlta(3));

        var fichas = FichasEntrada.Armar(
            [new(dueno, "dueno", "Dueño de prueba", De(Rol.Dueno)), new(cajero, "cajero", "Cajero de prueba", De(Rol.Cajero)),
                new(encargado, "encargado", " Encargado de prueba ", De(Rol.Encargado))],
            [], Defecto, cajeroDelTurno: cajero);

        Assert.Equal(["Cajero de prueba", "Dueño de prueba", "Encargado de prueba"], fichas.Select(f => f.Nombre));
        Assert.Equal(["C", "D", "E"], fichas.Select(f => f.Tecla));
        Assert.Equal([true, false, false], fichas.Select(f => f.TurnoAbierto));
        Assert.Equal([false, true, true], fichas.Select(f => f.HaceCorte));
    }

    /// <summary>Dar de baja a alguien no le da su letra a otro: las letras se reparten también entre los dados de baja.</summary>
    [Fact]
    public void Una_baja_no_le_cambia_la_letra_a_nadie()
    {
        var (carmen, carlos) = (DadoDeAlta(1), DadoDeAlta(2));
        UsuarioConPermisos Activo(Guid id, string nombre) => new(id, nombre.ToLowerInvariant(), nombre, Defecto);

        var antes = FichasEntrada.Armar([Activo(carmen, "Carmen"), Activo(carlos, "Carlos")], [], Defecto, null);
        // Carmen se da de baja: su ficha se va, pero Carlos se queda con la suya.
        var despues = FichasEntrada.Armar([Activo(carlos, "Carlos")], [(carmen, "Carmen"), (carlos, "Carlos")], Defecto, null);

        Assert.Equal(antes.Single(f => f.UsuarioId == carlos).Tecla, Assert.Single(despues).Tecla);
        Assert.Equal("A", Assert.Single(despues).Tecla);
    }

    [Fact]
    public void Un_pin_equivocado_dice_cuantos_intentos_quedan()
    {
        Assert.Equal("PIN incorrecto. Te quedan 4 intentos.", FichasEntrada.PinIncorrecto(4));
        Assert.Equal("PIN incorrecto. Te queda 1 intento.", FichasEntrada.PinIncorrecto(1));
    }
}
