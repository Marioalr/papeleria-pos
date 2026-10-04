using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

public class PermisosTests
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private static readonly Guid Sucursal2 = Ids.Nuevo();
    private static readonly DateTime Ahora = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    private static NivelPermiso Nivel(Rol rol, AccionControlada accion, long? monto = null, params ReglaPermiso[] reglas) =>
        Permisos.Evaluar(reglas, rol, Matriz, accion, monto).Nivel;

    [Fact]
    public void Las_reglas_iniciales_son_las_del_plan()
    {
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Nivel(Rol.Cajero, AccionControlada.CancelarTicket));
        Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Encargado, AccionControlada.CancelarTicket));
        Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Cajero, AccionControlada.ArticuloComun));
        Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Cajero, AccionControlada.SalidaEfectivo, 1_000_000));
        Assert.Equal(NivelPermiso.NoPermitido, Nivel(Rol.Cajero, AccionControlada.CorteZ));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Nivel(Rol.Encargado, AccionControlada.CambiarPrecio));
    }

    [Fact]
    public void La_devolucion_arriba_de_200_pide_encargado()
    {
        Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Cajero, AccionControlada.Devolucion, 20000));
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Nivel(Rol.Cajero, AccionControlada.Devolucion, 20001));
        Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Encargado, AccionControlada.Devolucion, 500000));
    }

    [Fact]
    public void El_dueno_puede_todo_y_solo_el_edita_las_reglas()
    {
        Assert.All(Enum.GetValues<AccionControlada>(), a => Assert.Equal(NivelPermiso.Permitido, Nivel(Rol.Dueno, a, 99_999_999)));
        var abierta = new ReglaPermiso { Accion = AccionControlada.EditarReglas, Rol = Rol.Encargado, Nivel = NivelPermiso.Permitido };
        Assert.Equal(NivelPermiso.NoPermitido, Nivel(Rol.Encargado, AccionControlada.EditarReglas, null, abierta));
    }

    [Fact]
    public void La_regla_de_la_sucursal_gana_sobre_la_general_y_la_inicial()
    {
        var general = new ReglaPermiso { Accion = AccionControlada.Reimprimir, Rol = Rol.Cajero, Nivel = NivelPermiso.RequiereAutorizacion };
        var deMatriz = new ReglaPermiso { Accion = AccionControlada.Reimprimir, Rol = Rol.Cajero, SucursalId = Matriz, Nivel = NivelPermiso.NoPermitido };

        Assert.Equal(NivelPermiso.NoPermitido, Permisos.Evaluar([general, deMatriz], Rol.Cajero, Matriz, AccionControlada.Reimprimir).Nivel);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, Permisos.Evaluar([general, deMatriz], Rol.Cajero, Sucursal2, AccionControlada.Reimprimir).Nivel);
        Assert.Equal(NivelPermiso.Permitido, Permisos.Evaluar([], Rol.Cajero, Sucursal2, AccionControlada.Reimprimir).Nivel);
    }

    [Fact]
    public void Reimprimir_y_salidas_piden_motivo()
    {
        Assert.True(Permisos.Evaluar([], Rol.Cajero, Matriz, AccionControlada.Reimprimir).PideMotivo);
        Assert.True(Permisos.Evaluar([], Rol.Cajero, Matriz, AccionControlada.SalidaEfectivo).PideMotivo);
        Assert.False(Permisos.Evaluar([], Rol.Cajero, Matriz, AccionControlada.QuitarProducto).PideMotivo);
    }

    [Fact]
    public void Cambiar_precio_solo_lo_autoriza_el_dueno()
    {
        Assert.False(Permisos.PuedeAutorizar([], Rol.Encargado, Matriz, AccionControlada.CambiarPrecio, null));
        Assert.True(Permisos.PuedeAutorizar([], Rol.Dueno, Matriz, AccionControlada.CambiarPrecio, null));
        Assert.True(Permisos.PuedeAutorizar([], Rol.Encargado, Matriz, AccionControlada.CancelarTicket, null));
    }

    private static Autorizacion Autorizacion(long? monto = 50000) => new()
    {
        Accion = AccionControlada.CancelarTicket,
        SucursalId = Matriz,
        MontoCentavos = monto,
        Detalle = "Cancelar A-000001",
        CreadaUtc = Ahora,
        VenceUtc = Ahora + PapeleriaPos.Nucleo.Control.Autorizacion.Vigencia,
    };

    [Fact]
    public void Una_autorizacion_sirve_una_sola_vez()
    {
        var a = Autorizacion();

        a.Usar(AccionControlada.CancelarTicket, Matriz, 30000, Ahora.AddSeconds(30));

        Assert.Throws<InvalidOperationException>(() => a.Usar(AccionControlada.CancelarTicket, Matriz, 30000, Ahora.AddSeconds(40)));
    }

    [Fact]
    public void Una_autorizacion_no_sirve_para_otra_cosa_ni_vencida_ni_por_mas_dinero()
    {
        Assert.Throws<InvalidOperationException>(() => Autorizacion().Usar(AccionControlada.Devolucion, Matriz, 100, Ahora));
        Assert.Throws<InvalidOperationException>(() => Autorizacion().Usar(AccionControlada.CancelarTicket, Sucursal2, 100, Ahora));
        Assert.Throws<InvalidOperationException>(() => Autorizacion().Usar(AccionControlada.CancelarTicket, Matriz, 100, Ahora.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => Autorizacion().Usar(AccionControlada.CancelarTicket, Matriz, 50001, Ahora));
    }
}
