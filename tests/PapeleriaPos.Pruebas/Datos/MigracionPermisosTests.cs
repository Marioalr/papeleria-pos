using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La migración de los roles a los permisos por usuario (entrega 1), en la base: corre al prepararla, llena lo que falta y
/// no repite nada; las reglas por rol se quedan como estaban.
/// </summary>
public class MigracionPermisosTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly Sucursal _matriz = new() { Clave = "M", Nombre = "Matriz" };
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _encargado = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private readonly Usuario _dueno = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _cajera.SucursalId = _encargado.SucursalId = _matriz.Id;
        await using var db = _base.Contexto();
        db.Sucursales.Add(_matriz);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    /// <summary>Los de cada usuario; los de defecto, con <see cref="Guid.Empty"/>.</summary>
    private async Task<Dictionary<Guid, PermisosDeUsuario>> PermisosAsync()
    {
        await using var db = _base.Contexto();
        return (await db.PermisosUsuario.AsNoTracking().ToListAsync()).GroupBy(p => p.UsuarioId ?? Guid.Empty)
            .ToDictionary(g => g.Key, g => new PermisosDeUsuario(g.Select(p => p.Permiso)));
    }

    /// <summary>Los usuarios de antes, con sus roles y unas reglas cambiadas, y la migración otra vez (como al arrancar el servicio).</summary>
    private async Task<List<ReglaPermiso>> UsuariosDeAntesAsync()
    {
        List<ReglaPermiso> reglas =
        [
            new() { Accion = AccionControlada.Reimprimir, Rol = Rol.Cajero, Nivel = NivelPermiso.RequiereAutorizacion },
            new() { Accion = AccionControlada.CorteZ, Rol = Rol.Encargado, SucursalId = _matriz.Id, Nivel = NivelPermiso.NoPermitido },
            new() { Accion = AccionControlada.Descuento, Rol = Rol.Encargado, Nivel = NivelPermiso.Permitido, UmbralCentavos = 2500 },
        ];
        await using var db = _base.Contexto();
        db.ReglasPermiso.AddRange(reglas);
        db.Usuarios.AddRange(_cajera, _encargado, _dueno);
        await db.SaveChangesAsync();
        return reglas;
    }

    [Fact]
    public async Task Una_base_nueva_queda_con_los_de_defecto_que_son_los_del_cajero()
    {
        var permisos = await PermisosAsync();

        var defecto = Assert.Single(permisos).Value;
        Assert.Equal(Guid.Empty, permisos.Keys.Single());
        Assert.Equal(PermisosDeRoles.PorDefecto([]), defecto.Lista());
    }

    [Fact]
    public async Task Cada_usuario_de_antes_recibe_lo_que_valia_para_su_rol_en_su_sucursal()
    {
        var reglas = await UsuariosDeAntesAsync();
        await using (var db = _base.Contexto())
            await MigracionPermisos.AplicarAsync(db);

        var permisos = await PermisosAsync();

        Assert.Equal(PermisosDeRoles.De(Rol.Cajero, reglas, _matriz.Id), permisos[_cajera.Id].Lista());
        Assert.Equal(PermisosDeRoles.De(Rol.Encargado, reglas, _matriz.Id), permisos[_encargado.Id].Lista());
        Assert.True(permisos[_dueno.Id].TieneTodos);
        // La regla de la sucursal y la general, con su límite.
        Assert.Equal(NivelPermiso.NoPermitido, permisos[_encargado.Id].De(AccionControlada.CorteZ).Nivel);
        Assert.Equal(2500, permisos[_encargado.Id].De(AccionControlada.Descuento).UmbralCentavos);
        Assert.Equal(NivelPermiso.RequiereAutorizacion, permisos[_cajera.Id].De(AccionControlada.Reimprimir).Nivel);
        // Los de defecto ya estaban (de cuando se preparó la base): no se tocan.
        Assert.Equal(PermisosDeRoles.PorDefecto([]), permisos[Guid.Empty].Lista());
        await using var datos = _base.Contexto();
        Assert.Equal([_dueno.Id], await datos.Usuarios.Where(u => u.PrimeraCuenta).Select(u => u.Id).ToListAsync());
        // Las reglas por rol se quedan como estaban.
        Assert.Equal(3, await datos.ReglasPermiso.CountAsync());
    }

    [Fact]
    public async Task Otra_vez_no_cambia_nada()
    {
        await UsuariosDeAntesAsync();
        await using (var db = _base.Contexto())
            await MigracionPermisos.AplicarAsync(db);
        await using var datos = _base.Contexto();
        var antes = await datos.PermisosUsuario.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Version }).ToListAsync();

        var agregados = await MigracionPermisos.AplicarAsync(datos);
        await BaseDeDatos.PrepararAsync(datos);

        Assert.Equal(0, agregados);
        Assert.Equal(antes, await datos.PermisosUsuario.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Version }).ToListAsync());
        Assert.Equal(1, await datos.Usuarios.CountAsync(u => u.PrimeraCuenta));
    }

    [Fact]
    public async Task Cambiar_los_de_defecto_no_toca_a_los_que_ya_existen()
    {
        await UsuariosDeAntesAsync();
        await using (var db = _base.Contexto())
            await MigracionPermisos.AplicarAsync(db);
        await using (var db = _base.Contexto())
        {
            var merma = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == null && p.Accion == AccionControlada.Merma);
            merma.Poner(new Permiso(AccionControlada.Merma, NivelPermiso.NoPermitido));
            await db.SaveChangesAsync();
            await MigracionPermisos.AplicarAsync(db);
        }

        var permisos = await PermisosAsync();

        Assert.Equal(NivelPermiso.NoPermitido, permisos[Guid.Empty].De(AccionControlada.Merma).Nivel);
        Assert.Equal(new Permiso(AccionControlada.Merma, NivelPermiso.Permitido, 20000), permisos[_cajera.Id].De(AccionControlada.Merma));
    }

    /// <summary>Una acción que llega después (como las de la entrega 9): quien tiene todo la tiene; los demás, como los de defecto.</summary>
    [Fact]
    public async Task Una_accion_nueva_le_llega_a_quien_tiene_todo_y_a_los_demas_como_la_de_defecto()
    {
        await UsuariosDeAntesAsync();
        await using (var db = _base.Contexto())
        {
            await MigracionPermisos.AplicarAsync(db);
            // Como si VerReportes acabara de aparecer: nadie la tiene todavía.
            db.PermisosUsuario.RemoveRange(await db.PermisosUsuario.Where(p => p.Accion == AccionControlada.VerReportes).ToListAsync());
            await db.SaveChangesAsync();
            Assert.Equal(4, await MigracionPermisos.AplicarAsync(db));
        }

        var permisos = await PermisosAsync();

        Assert.True(permisos[_dueno.Id].De(AccionControlada.VerReportes).SinLimite);
        Assert.Equal(NivelPermiso.NoPermitido, permisos[_encargado.Id].De(AccionControlada.VerReportes).Nivel);
        Assert.Equal(NivelPermiso.NoPermitido, permisos[Guid.Empty].De(AccionControlada.VerReportes).Nivel);
        // Uno por acción para cada quien, otra vez.
        await using var datos = _base.Contexto();
        Assert.All(await datos.PermisosUsuario.GroupBy(p => p.UsuarioId).Select(g => g.Count()).ToListAsync(),
            n => Assert.Equal(Permisos.Acciones.Count, n));
    }
}
