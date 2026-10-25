using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Herramientas.Usuarios;

/// <summary>
/// Los usuarios al instalar y para recuperar la entrada (el piloto alfa, 2026-10-25), desde la línea de comandos de la PC del
/// servicio: el dueño que se elige al importar eleventa (<c>importar-eleventa --duenio</c>) y el PIN que se pone a alguien
/// (<c>pin-usuario</c>). De eleventa solo llegan el número y el nombre de cada usuario; el PIN es del POS, y aquí se pide sin
/// que se vea, dos veces, con la misma regla que «Usuarios y permisos».
/// </summary>
public static class UsuariosDeLaTienda
{
    /// <summary>El PIN en una variable de entorno: solo para pruebas o para automatizar (queda en el entorno). Si no, se pide.</summary>
    public const string VariablePin = "POS_PIN";

    public const string SinTeclado =
        $"Falta el PIN: escríbelo al correrlo (sin que se vea). Solo para pruebas o para automatizar, va en la variable {VariablePin}.";

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
    /// El PIN nuevo de alguien, dos veces y sin que se vea (<paramref name="pedir"/> recibe lo que se pregunta y regresa lo
    /// escrito, o vacío si no hay teclado), con la regla de «Usuarios y permisos» (<see cref="FichasEntrada.RevisarPinNuevo"/>).
    /// Si no está bien, lo dice y lo vuelve a pedir, hasta <paramref name="intentos"/> veces. Vacío si no se pudo.
    /// </summary>
    public static string? PedirPin(string nombre, Func<string, string?> pedir, TextWriter errores, int intentos = 3)
    {
        for (var vez = 1; vez <= intentos; vez++)
        {
            if (pedir($"PIN de {nombre} ({FichasEntrada.CifrasPin} cifras): ") is not { } pin || pedir("El PIN otra vez: ") is not { } otraVez)
            {
                errores.WriteLine(SinTeclado);
                return null;
            }
            if (FichasEntrada.RevisarPinNuevo(pin, otraVez) is not { } mal)
                return pin;
            errores.WriteLine(mal);
        }
        errores.WriteLine("No se puso el PIN.");
        return null;
    }

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

    /// <summary>
    /// El usuario de <c>pin-usuario --usuario</c>: por su número de eleventa, por su usuario (con el que se entra escribiéndolo) o
    /// por su nombre completo, sin acentos ni mayúsculas. Solo si es uno: si no, por qué.
    /// </summary>
    public static async Task<(Usuario? Usuario, string? Error)> BuscarAsync(PosDbContext db, string texto, CancellationToken ct = default)
    {
        var buscado = texto.Trim();
        var usuarios = await db.Usuarios.ToListAsync(ct);
        var porNumero = int.TryParse(buscado, out var numero) ? usuarios.Where(u => u.EleventaId == numero).ToList() : [];
        var encontrados = porNumero.Count > 0 ? porNumero
            : usuarios.Where(u => string.Equals(u.NombreUsuario, buscado, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } porUsuario ? porUsuario
            : usuarios.Where(u => Texto.Normalizar(u.Nombre) == Texto.Normalizar(buscado)).ToList();
        return encontrados.Count switch
        {
            1 => (encontrados[0], null),
            0 => (null, $"No hay ningún usuario «{buscado}»."),
            _ => (null, $"Hay {encontrados.Count} usuarios «{buscado}»: di cuál con su usuario o su número de eleventa."),
        };
    }

    /// <summary>Los usuarios de la base, para elegir: su nombre, su usuario, su número de eleventa y si tiene PIN (nunca el PIN).</summary>
    public static async Task<List<string>> ListaAsync(PosDbContext db, CancellationToken ct = default) =>
        (await db.Usuarios.AsNoTracking().OrderBy(u => u.Nombre).ToListAsync(ct))
            .Select(u => $"  {u.Nombre,-40}  «{u.NombreUsuario}»{(u.EleventaId is { } n ? $", número {n} de eleventa" : "")}"
                + $"{(u.TienePin ? "" : ", sin PIN")}{(u.Activo ? "" : ", dado de baja")}")
            .ToList();

    /// <summary>
    /// <c>pin-usuario</c>, ya con la base abierta y preparada: busca al usuario, pide su PIN dos veces (<paramref name="pedir"/>) con
    /// la regla de «Usuarios y permisos» y lo pone, en la bitácora sin el PIN. Lo que pasa lo dice en <paramref name="salida"/> y
    /// <paramref name="errores"/>. Regresa el código de salida: 0 si quedó, 1 si no.
    /// </summary>
    public static async Task<int> PinUsuarioAsync(PosDbContext db, string buscado, Func<string, string?> pedir, int intentos, TextWriter salida,
        TextWriter errores, CancellationToken ct = default)
    {
        var (usuario, noEs) = await BuscarAsync(db, buscado, ct);
        if (usuario is null)
        {
            errores.WriteLine(noEs);
            errores.WriteLine("Los usuarios de la base:");
            foreach (var linea in await ListaAsync(db, ct))
                errores.WriteLine(linea);
            return 1;
        }
        salida.WriteLine($"{usuario.Nombre} («{usuario.NombreUsuario}»{(usuario.EleventaId is { } n ? $", número {n} de eleventa" : "")}): "
            + (usuario.TienePin ? "ya tiene PIN; se le cambia." : "todavía no tiene PIN."));
        if (PedirPin(usuario.Nombre, pedir, errores, intentos) is not { } pin)
            return 1;
        await PonerPinAsync(db, usuario, pin, ct);
        await db.SaveChangesAsync(ct);
        salida.WriteLine($"Listo: {usuario.Nombre} entra con su ficha y ese PIN; donde se escribe el usuario, el suyo es «{usuario.NombreUsuario}».");
        if (!usuario.Activo)
            salida.WriteLine("Está dado de baja: no entra hasta que lo vuelvan a dar de alta en Configuración › Usuarios y permisos.");
        return 0;
    }

    /// <summary>
    /// El PIN que se pone desde la línea de comandos (al instalar, o si el dueño olvidó el suyo): empieza sin intentos fallidos,
    /// y queda en la bitácora (sin el PIN). Quien llama guarda.
    /// </summary>
    public static async Task PonerPinAsync(PosDbContext db, Usuario usuario, string pin, CancellationToken ct = default)
    {
        var primero = !usuario.TienePin;
        usuario.EstablecerPin(pin);
        var sucursalId = usuario.SucursalId ?? await db.Sucursales.OrderBy(s => s.Clave != "M").ThenBy(s => s.Nombre).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (sucursalId is { } sucursal)
            new ServicioControl(db).Registrar(sucursal, null, null, AccionControlada.EditarReglas,
                $"{(primero ? "Se le puso PIN" : "Se le cambió el PIN")} a {usuario.Nombre} con pin-usuario (en la PC del servicio)");
    }
}
