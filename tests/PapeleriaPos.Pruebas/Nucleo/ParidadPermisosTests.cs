using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Pasar de los roles a los permisos por usuario (entrega 1) no cambia ninguna decisión: para el cajero, el encargado y el
/// dueño, cada acción da lo mismo con las reglas por rol de antes (<see cref="Antes"/>) que con los permisos que les deja la
/// migración (<see cref="PermisosDeRoles"/>), con cualquier monto; también quién autoriza, quién puede, y lo que dicen las
/// fichas de la entrada. Con las reglas iniciales (las de la base de prueba) y con reglas cambiadas, generales y de la
/// sucursal.
/// </summary>
public class ParidadPermisosTests
{
    private static readonly Guid Matriz = Ids.Nuevo();

    /// <summary>Sin monto, cero, alrededor de los umbrales de hoy (10 %, $200) y uno muy grande.</summary>
    private static readonly long?[] Montos = [null, 0, 999, 1000, 1001, 19999, 20000, 20001, 50000, 99_999_999];

    private static readonly (Guid Id, string Usuario, string Nombre, Rol Rol)[] Usuarios =
    [
        (Ids.Nuevo(), "cajero", "Cajero de prueba", Rol.Cajero),
        (Ids.Nuevo(), "encargado", "Encargado de prueba", Rol.Encargado),
        (Ids.Nuevo(), "dueno", "Dueño de prueba", Rol.Dueno),
        // Otro cajero y otro encargado: quién autoriza va en orden, y el orden sale de los permisos, no del rol.
        (Ids.Nuevo(), "beto", "Beto", Rol.Cajero),
        (Ids.Nuevo(), "carmen", "Carmen", Rol.Encargado),
    ];

    public static TheoryData<string> Casos => ["iniciales", "generales", "de la sucursal"];

    /// <summary>Las reglas de cada caso: las iniciales; unas cambiadas para todas las sucursales; y unas solo de esta.</summary>
    private static List<ReglaPermiso> Reglas(string caso) => caso switch
    {
        "generales" =>
        [
            Regla(AccionControlada.Reimprimir, Rol.Cajero, NivelPermiso.NoPermitido),
            Regla(AccionControlada.Descuento, Rol.Cajero, NivelPermiso.Permitido, umbral: 1500),
            Regla(AccionControlada.CorteZ, Rol.Cajero, NivelPermiso.Permitido),
            Regla(AccionControlada.Devolucion, Rol.Encargado, NivelPermiso.Permitido, umbral: 50000, motivo: true),
            Regla(AccionControlada.CambiarPrecio, Rol.Encargado, NivelPermiso.Permitido),
            Regla(AccionControlada.CorteDelDia, Rol.Encargado, NivelPermiso.Permitido),
            // Lo que era solo del dueño no se podía dar, aunque hubiera regla.
            Regla(AccionControlada.EditarReglas, Rol.Encargado, NivelPermiso.Permitido),
        ],
        "de la sucursal" =>
        [
            Regla(AccionControlada.Reimprimir, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
            Regla(AccionControlada.Reimprimir, Rol.Cajero, NivelPermiso.NoPermitido, sucursal: Matriz),
            Regla(AccionControlada.CorteZ, Rol.Encargado, NivelPermiso.NoPermitido, sucursal: Matriz),
            Regla(AccionControlada.Merma, Rol.Encargado, NivelPermiso.Permitido, umbral: 30000, sucursal: Matriz),
            Regla(AccionControlada.CancelarTicket, Rol.Encargado, NivelPermiso.RequiereAutorizacion, sucursal: Matriz),
            // De otra sucursal: aquí no cuenta.
            Regla(AccionControlada.SalidaEfectivo, Rol.Cajero, NivelPermiso.NoPermitido, sucursal: Ids.Nuevo()),
        ],
        _ => [],
    };

    /// <summary>Lo que deja la migración a cada usuario de la sucursal.</summary>
    private static Dictionary<Guid, PermisosDeUsuario> Migrados(List<ReglaPermiso> reglas) =>
        Usuarios.ToDictionary(u => u.Id, u => new PermisosDeUsuario(PermisosDeRoles.De(u.Rol, reglas, Matriz)));

    private static List<UsuarioConPermisos> ConPermisos(Dictionary<Guid, PermisosDeUsuario> migrados) =>
        Usuarios.Select(u => new UsuarioConPermisos(u.Id, u.Usuario, u.Nombre, migrados[u.Id])).ToList();

    [Theory]
    [MemberData(nameof(Casos))]
    public void Cada_accion_con_cualquier_monto_da_lo_mismo_que_con_los_roles(string caso)
    {
        var reglas = Reglas(caso);
        var migrados = Migrados(reglas);

        foreach (var u in Usuarios)
        foreach (var accion in Permisos.Acciones)
        foreach (var monto in Montos)
        {
            var antes = Antes.Evaluar(reglas, u.Rol, accion, monto);
            var ahora = Permisos.Evaluar(migrados[u.Id], accion, monto);
            Assert.True(antes == ahora, $"{caso}: {u.Nombre}, {accion}, {monto}: antes {antes}, ahora {ahora}");
            Assert.Equal(Antes.PuedeAutorizar(reglas, u.Rol, accion, monto), Permisos.PuedeAutorizar(migrados[u.Id], accion, monto));
        }
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Lo_de_la_sesion_y_quien_autoriza_salen_igual_y_en_el_mismo_orden(string caso)
    {
        var reglas = Reglas(caso);
        var migrados = Migrados(reglas);
        var usuarios = ConPermisos(migrados);

        foreach (var u in Usuarios)
        {
            var antes = Antes.DeLaSesion(reglas, u.Id, u.Rol);
            var ahora = Permisos.DeLaSesion(migrados[u.Id], usuarios, u.Id);
            Assert.Equal(antes.Count, ahora.Count);
            foreach (var (a, b) in antes.Zip(ahora))
            {
                var donde = $"{caso}: {u.Nombre}, {a.Accion}";
                Assert.True((a.Accion, a.Nivel, a.UmbralCentavos, a.PideMotivo) == (b.Accion, b.Nivel, b.UmbralCentavos, b.PideMotivo), donde);
                Assert.True(a.Autorizan.SequenceEqual(b.Autorizan),
                    $"{donde}: antes {string.Join(", ", a.Autorizan.Select(x => x.Nombre))}; ahora {string.Join(", ", b.Autorizan.Select(x => x.Nombre))}");
            }
        }
        foreach (var accion in Permisos.Acciones)
            Assert.Equal(Antes.QuienesPueden(reglas, accion), Permisos.QuienesPueden(usuarios, accion));
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Las_fichas_de_la_entrada_dicen_lo_mismo(string caso)
    {
        var reglas = Reglas(caso);
        var migrados = Migrados(reglas);
        // Los de defecto, como los deja la migración: los del cajero con las reglas generales.
        var defecto = new PermisosDeUsuario(PermisosDeRoles.PorDefecto(reglas));
        var cajero = Usuarios[0].Id;

        var antes = Antes.Fichas(reglas, cajero);
        var ahora = FichasEntrada.Armar(ConPermisos(migrados), [], defecto, cajero);

        Assert.Equal(antes.Select(f => (f.UsuarioId, f.Nombre, f.Tecla, f.TurnoAbierto, f.HaceCorte)),
            ahora.Select(f => (f.UsuarioId, f.Nombre, f.Tecla, f.TurnoAbierto, f.HaceCorte)));
        if (caso == "de la sucursal")
        {
            // Con una regla del cajero solo para esta sucursal, el cajero ya no tiene los de defecto (que son de todas): el
            // resumen lo dice. Lo que puede hacer no cambia.
            Assert.Equal("Permisos propios", ahora.Single(f => f.UsuarioId == cajero).Resumen);
            Assert.Equal(antes.Where(f => f.UsuarioId != Usuarios[0].Id && f.UsuarioId != Usuarios[3].Id).Select(f => f.Resumen),
                ahora.Where(f => f.UsuarioId != Usuarios[0].Id && f.UsuarioId != Usuarios[3].Id).Select(f => f.Resumen));
        }
        else
        {
            Assert.Equal(antes.Select(f => f.Resumen), ahora.Select(f => f.Resumen));
        }
    }

    [Fact]
    public void Con_las_reglas_iniciales_los_tres_de_prueba_quedan_como_se_ven_hoy()
    {
        var migrados = Migrados([]);
        var defecto = new PermisosDeUsuario(PermisosDeRoles.PorDefecto([]));

        Assert.Equal("Permisos por defecto", FichasEntrada.Resumen(migrados[Usuarios[0].Id], defecto));
        Assert.Equal("Corte de turno, productos y clientes", FichasEntrada.Resumen(migrados[Usuarios[1].Id], defecto));
        Assert.Equal("Todos los permisos", FichasEntrada.Resumen(migrados[Usuarios[2].Id], defecto));
        // El cajero tiene exactamente los de defecto.
        Assert.All(Permisos.Acciones, a => Assert.Equal(defecto.De(a), migrados[Usuarios[0].Id].De(a)));
    }

    private static ReglaPermiso Regla(AccionControlada accion, Rol rol, NivelPermiso nivel, long? umbral = null, bool motivo = false, Guid? sucursal = null) =>
        new() { Accion = accion, Rol = rol, Nivel = nivel, UmbralCentavos = umbral, PideMotivo = motivo, SucursalId = sucursal };

    /// <summary>Las decisiones de antes de la entrega 1 (<see cref="PermisosDeAntes"/>): con los roles y las reglas por rol, en la matriz.</summary>
    private static class Antes
    {
        private static IEnumerable<(Guid Id, string Usuario, string Nombre, Rol Rol)> Activos => Usuarios;

        public static DecisionPermiso Evaluar(List<ReglaPermiso> reglas, Rol rol, AccionControlada accion, long? monto) =>
            PermisosDeAntes.Evaluar(reglas, rol, Matriz, accion, monto);

        public static bool PuedeAutorizar(List<ReglaPermiso> reglas, Rol rol, AccionControlada accion, long? monto) =>
            PermisosDeAntes.PuedeAutorizar(reglas, rol, Matriz, accion, monto);

        public static IReadOnlyList<PermisoDeSesion> DeLaSesion(List<ReglaPermiso> reglas, Guid usuarioId, Rol rol) =>
            PermisosDeAntes.DeLaSesion(reglas, Activos, usuarioId, rol, Matriz);

        public static List<QuienAutoriza> QuienesPueden(List<ReglaPermiso> reglas, AccionControlada accion) =>
            PermisosDeAntes.QuienesPueden(reglas, Activos, Matriz, accion);

        public static List<PapeleriaPos.Nucleo.Contratos.FichaUsuarioDto> Fichas(List<ReglaPermiso> reglas, Guid cajeroDelTurno) =>
            PermisosDeAntes.Fichas(Usuarios.Select(u => (u.Id, u.Nombre, u.Rol)), reglas, Matriz, cajeroDelTurno);
    }
}
