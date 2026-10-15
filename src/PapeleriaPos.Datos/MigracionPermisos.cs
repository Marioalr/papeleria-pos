using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// De los roles a los permisos por usuario (entrega 1, docs/propuesta-interfaz.md «Usuarios y permisos»). Corre al
/// preparar la base (<see cref="BaseDeDatos.PrepararAsync"/>), después de las migraciones, y no repite nada: solo llena lo
/// que falta. Después de ella cada usuario y los de defecto tienen un permiso por acción, y decidir es igual que antes
/// para los usuarios que ya había.
/// </summary>
public static class MigracionPermisos
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Los de defecto, si no están: los del cajero con las reglas generales (la de defecto que falte, igual).</item>
    /// <item>Cada usuario sin permisos (uno de antes, o uno que se dio de alta directo en la base, como los de prueba):
    /// los de su rol en su sucursal (<see cref="PermisosDeRoles.De"/>). El dueño, todos.</item>
    /// <item>Una acción nueva que le falte a alguien: si tiene todos los demás, también esa; si no, la de defecto.</item>
    /// <item>La primera cuenta, si nadie la tiene: el dueño que se dio de alta primero.</item>
    /// </list>
    /// Las reglas por rol (<see cref="ReglaPermiso"/>) se quedan como estaban: después de esto, nada las vuelve a leer.
    /// </summary>
    /// <returns>Cuántos permisos agregó.</returns>
    public static async Task<int> AplicarAsync(PosDbContext db, CancellationToken ct = default)
    {
        var guardados = await db.PermisosUsuario.AsNoTracking().Select(p => new { p.UsuarioId, p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo })
            .ToListAsync(ct);
        var usuarios = await db.Usuarios.AsNoTracking().Select(u => new { u.Id, u.Rol, u.SucursalId, u.PrimeraCuenta }).ToListAsync(ct);
        var porUsuario = guardados.ToLookup(p => p.UsuarioId, p => new Permiso(p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo));
        var reglas = new Lazy<Task<List<ReglaPermiso>>>(() => db.ReglasPermiso.AsNoTracking().ToListAsync(ct));
        var nuevos = new List<PermisoUsuario>();

        // Los de defecto.
        var defecto = porUsuario[null].ToDictionary(p => p.Accion);
        if (defecto.Count < Permisos.Acciones.Count)
        {
            foreach (var permiso in PermisosDeRoles.PorDefecto(await reglas.Value).Where(p => !defecto.ContainsKey(p.Accion)))
            {
                defecto[permiso.Accion] = permiso;
                nuevos.Add(Nuevo(null, permiso));
            }
        }

        // Cada usuario.
        var sucursales = await db.Sucursales.AsNoTracking().Select(s => s.Id).ToListAsync(ct);
        foreach (var usuario in usuarios)
        {
            var propios = porUsuario[usuario.Id].ToDictionary(p => p.Accion);
            if (propios.Count == Permisos.Acciones.Count)
                continue;
            if (propios.Count == 0)
            {
                // Uno de antes de la entrega 1: lo que valía para su rol. Sin sucursal (el dueño), la única que haya; con
                // varias, las reglas generales (los permisos de cada quien ya no cambian de una sucursal a otra).
                var sucursal = usuario.SucursalId ?? (sucursales.Count == 1 ? sucursales[0] : null);
                nuevos.AddRange(PermisosDeRoles.De(usuario.Rol, await reglas.Value, sucursal).Select(p => Nuevo(usuario.Id, p)));
                continue;
            }
            var todos = propios.Values.All(p => p.SinLimite);
            foreach (var accion in Permisos.Acciones.Where(a => !propios.ContainsKey(a)))
                nuevos.Add(Nuevo(usuario.Id, todos ? Permiso.Todo(accion) : defecto[accion]));
        }
        db.PermisosUsuario.AddRange(nuevos);

        // En Administración solo hay «Puede» o «No» (2026-10-15): lo que quedó en «Con autorización» (de una regla de antes)
        // pasa a «No», que es como ya valía (Permisos.Normalizado).
        var administracion = Permisos.Acciones.Where(Permisos.SoloPuedeONo).ToList();
        var corregidos = await db.PermisosUsuario
            .Where(p => p.Nivel == NivelPermiso.RequiereAutorizacion && administracion.Contains(p.Accion)).ToListAsync(ct);
        foreach (var permiso in corregidos)
            permiso.Nivel = NivelPermiso.NoPermitido;

        // La primera cuenta: la del dueño que se dio de alta primero (los Id llevan la fecha de alta).
        var primera = false;
        if (!usuarios.Any(u => u.PrimeraCuenta) && usuarios.Where(u => u.Rol == Rol.Dueno).OrderBy(u => u.Id).FirstOrDefault() is { } dueno
            && await db.Usuarios.FindAsync([dueno.Id], ct) is { } cuenta)
        {
            (cuenta.PrimeraCuenta, primera) = (true, true);
            // Quien prepara la base de prueba puede traer apagada la detección de cambios (GuardadoLote).
            db.Entry(cuenta).Property(u => u.PrimeraCuenta).IsModified = true;
        }

        if (nuevos.Count > 0 || primera || corregidos.Count > 0)
            await db.SaveChangesAsync(ct);
        return nuevos.Count;
    }

    /// <summary>El renglón de un permiso, como vale (en Administración, sin «Con autorización»).</summary>
    private static PermisoUsuario Nuevo(Guid? usuarioId, Permiso permiso)
    {
        permiso = Permisos.Normalizado(permiso);
        return new PermisoUsuario
        {
            UsuarioId = usuarioId,
            Accion = permiso.Accion,
            Nivel = permiso.Nivel,
            UmbralCentavos = permiso.UmbralCentavos,
            PideMotivo = permiso.PideMotivo,
        };
    }
}
