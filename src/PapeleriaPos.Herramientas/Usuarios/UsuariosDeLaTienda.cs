using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Herramientas.Usuarios;

/// <summary>
/// Los usuarios al instalar (el piloto alfa, 2026-10-25), desde la línea de comandos: el dueño que se elige al importar eleventa
/// (<c>importar-eleventa --duenio</c>). De eleventa solo llegan el número y el nombre de cada usuario; el PIN es del POS, y aquí
/// se pide sin que se vea, dos veces, con la misma regla que «Usuarios y permisos» (<see cref="PinUsuario"/>, que también pone
/// el de alguien con <c>pin-usuario</c>).
/// </summary>
public static class UsuariosDeLaTienda
{
    /// <summary>
    /// Por qué una base sin dueño no se importa sin <c>--duenio</c>: nadie podría entrar a dar de alta la caja ni a ponerles PIN a
    /// los demás.
    /// </summary>
    public const string FaltaElDuenio =
        "Falta el dueño: --duenio <su usuario o su número en eleventa>. Los usuarios de eleventa llegan sin PIN (de eleventa solo se "
        + "traen el número y el nombre, nunca la clave), y alguien tiene que poder entrar: el dueño, con todos los permisos y el PIN que "
        + "se le pide aquí. Él les pone su PIN a los demás en Configuración › Usuarios y permisos.";

    /// <summary>
    /// Si la base ya tiene dueño: alguien activo, con PIN y con «Usuarios y permisos» en «Puede» (quien da los PIN y los permisos).
    /// Solo lee columnas que ya tenía una base de antes de esto: se puede preguntar sin prepararla.
    /// </summary>
    public static Task<bool> TieneDuenioAsync(PosDbContext db, CancellationToken ct = default) => RegistroUsuarios.QuedaAlguienAsync(db, null, ct);

    /// <summary>Si ese usuario es dueño: activo, con PIN y con «Usuarios y permisos» en «Puede».</summary>
    public static async Task<bool> EsDuenioAsync(PosDbContext db, Guid usuarioId, CancellationToken ct = default) =>
        await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Activo && u.PinHash != "", ct)
        && await db.PermisosUsuario.AnyAsync(p => p.UsuarioId == usuarioId && p.Accion == AccionControlada.EditarReglas && p.Nivel == NivelPermiso.Permitido, ct);

    /// <summary>
    /// <c>--duenio</c> en una base que ya tiene dueño (la importación final): solo puede ser el mismo, y entonces no hace nada (los
    /// permisos se cambian en «Usuarios y permisos» y un PIN olvidado se pone con <c>pin-usuario</c>). Vacío si es él; si no, por qué
    /// no se sigue.
    /// </summary>
    public static async Task<string?> OtroDuenioAsync(PosDbContext db, Guid sucursalId, FilaUsuario duenio, CancellationToken ct = default)
    {
        var suyo = await db.Usuarios.AsNoTracking().Where(u => u.EleventaId == duenio.Id && (u.SucursalId == sucursalId || u.SucursalId == null))
            .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        return suyo is { } id && await EsDuenioAsync(db, id, ct)
            ? null
            : $"Esta base ya tiene dueño, y no es «{UsuariosDeEleventa.NombreEnElPos(duenio)}»: --duenio solo hace falta en una base sin dueño. Los "
                + "permisos se cambian en Configuración › Usuarios y permisos, y un PIN olvidado se pone con pin-usuario.";
    }

    /// <summary>
    /// Los usuarios en una importación (la base nueva o la final): los de eleventa (<see cref="UsuariosDeEleventa"/>) y, si se dice,
    /// el dueño (<see cref="HacerDuenioAsync"/>). Guarda.
    /// </summary>
    public static async Task<(ResultadoUsuarios Resultado, Usuario? Duenio)> ImportarAsync(
        PosDbContext db, Guid sucursalId, IReadOnlyList<FilaUsuario> filas, FilaUsuario? duenio, string? pin, CancellationToken ct = default)
    {
        var resultado = await UsuariosDeEleventa.AplicarAsync(db, filas, sucursalId, ct);
        await db.SaveChangesAsync(ct);
        if (duenio is null)
            return (resultado, null);
        var elDuenio = await HacerDuenioAsync(db, sucursalId, duenio.Id, pin, ct);
        await db.SaveChangesAsync(ct);
        return (resultado, elDuenio);
    }

    /// <summary>
    /// El usuario de eleventa que dice <c>--duenio</c>: por su número (<c>USUARIOS.ID</c>) o por su usuario (<c>USUARIO</c>, sin
    /// importar mayúsculas), de entre los activos y sin borrar. Si no es uno solo, por qué.
    /// </summary>
    public static (FilaUsuario? Fila, string? Error) Elegir(IReadOnlyList<FilaUsuario> filas, string texto)
    {
        var buscado = texto.Trim();
        var candidatos = filas.Where(f => int.TryParse(buscado, out var numero) && f.Id == numero
                || string.Equals(f.Usuario.Trim(), buscado, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var vigentes = candidatos.Where(f => f.Vigente).ToList();
        return vigentes.Count switch
        {
            1 => (vigentes[0], null),
            > 1 => (null, $"«{buscado}» es el número de uno y el usuario de otro en eleventa: di cuál con el otro dato."),
            _ => (null, candidatos.Count > 0
                ? $"En eleventa, «{buscado}» está dado de baja o borrado: el dueño tiene que ser un usuario activo."
                : $"En eleventa no hay ningún usuario «{buscado}»."),
        };
    }

    /// <summary>Los usuarios activos de eleventa, para elegir al dueño: su número, su usuario y su nombre (nunca su clave).</summary>
    public static IEnumerable<string> Lista(IReadOnlyList<FilaUsuario> filas) =>
        filas.Where(f => f.Vigente).OrderBy(f => f.Id)
            .Select(f => $"  {f.Id,5}  {f.Usuario.Trim(),-20}  {UsuariosDeEleventa.NombreEnElPos(f)}");

    /// <summary>
    /// El dueño (<c>--duenio</c>): el usuario del POS ligado a ese número de eleventa queda con todos los permisos, activo, como
    /// la primera cuenta (si nadie lo es) y con su PIN, si se da (si ya tenía uno y no se da, se queda). Queda en la bitácora.
    /// Quien llama guarda. Regresa el usuario.
    /// </summary>
    public static async Task<Usuario> HacerDuenioAsync(PosDbContext db, Guid sucursalId, int eleventaId, string? pin, CancellationToken ct = default)
    {
        var usuario = await db.Usuarios.Where(u => u.EleventaId == eleventaId && (u.SucursalId == sucursalId || u.SucursalId == null))
            .OrderBy(u => u.SucursalId == null).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"El usuario {eleventaId} de eleventa no llegó a esta base.");
        var filas = await db.PermisosUsuario.Where(p => p.UsuarioId == usuario.Id).ToDictionaryAsync(p => p.Accion, ct);
        foreach (var accion in Permisos.Acciones)
        {
            if (filas.TryGetValue(accion, out var fila))
                fila.Poner(Permiso.Todo(accion));
            else
                db.PermisosUsuario.Add(new PermisoUsuario { UsuarioId = usuario.Id, Accion = accion, Nivel = NivelPermiso.Permitido });
        }
        (usuario.Rol, usuario.Activo) = (Rol.Dueno, true);
        if (!await db.Usuarios.AnyAsync(u => u.PrimeraCuenta && u.Id != usuario.Id, ct))
            usuario.PrimeraCuenta = true;
        if (pin is not null)
            usuario.EstablecerPin(pin);
        new ServicioControl(db).Registrar(sucursalId, null, null, AccionControlada.EditarReglas,
            $"Dueño de la tienda al importar eleventa (--duenio): {usuario.Nombre}, con todos los permisos{(pin is null ? "" : " y su PIN")}");
        return usuario;
    }
}
