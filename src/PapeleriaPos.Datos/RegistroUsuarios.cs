using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// «Usuarios y permisos» (entrega 1, docs/propuesta-interfaz.md): dar de alta usuarios, cambiarles los permisos y el PIN,
/// y darlos de baja. Lo hace quien entró a la caja con «Usuarios y permisos» en «Puede», sin volver a escribir su PIN (como
/// el corte): cada cambio se guarda al momento y queda en la bitácora de la sucursal de esa caja. Siempre queda alguien
/// activo con «Usuarios y permisos».
/// </summary>
public sealed class RegistroUsuarios(PosDbContext db)
{
    public const string SiempreAlguien = "Siempre debe quedar alguien con «Usuarios y permisos». Dáselo a otro usuario primero.";

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Todos los usuarios con sus permisos y su resumen, y los de defecto.</summary>
    public async Task<UsuariosYPermisosDto> ListarAsync(Guid cajaId, Guid sesionId, CancellationToken ct = default)
    {
        await ExigirAsync(cajaId, sesionId, registrar: false, ct);
        return await ListaAsync(null, ct);
    }

    /// <summary>Los permisos de un usuario, o los de defecto (sin usuario), uno por acción.</summary>
    public async Task<IReadOnlyList<Permiso>> PermisosAsync(Guid cajaId, Guid sesionId, Guid? usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(cajaId, sesionId, registrar: false, ct);
        var control = new ServicioControl(db);
        if (usuarioId is not { } id)
            return (await control.DefectoAsync(ct)).Lista();
        if (!await db.Usuarios.AnyAsync(u => u.Id == id, ct))
            throw new InvalidOperationException("Ese usuario no existe.");
        return (await control.PermisosAsync(id, ct)).Lista();
    }

    /// <summary>
    /// Un permiso de un usuario o de los de defecto (sin usuario), al momento. El límite solo vale en «Puede»: al pasar a «Con
    /// autorización» o a «No» se queda guardado, sin contar, y la pantalla lo regresa al volver a «Puede» (como en el
    /// prototipo). Cambiar los de defecto no toca a los usuarios que ya existen.
    /// </summary>
    public async Task<UsuariosYPermisosDto> CambiarAsync(Guid cajaId, Guid? usuarioId, SolicitudPermisoUsuario s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        if (!Enum.IsDefined(s.Accion))
            throw new ArgumentException("Ese permiso no existe.");
        if (!Enum.IsDefined(s.Nivel))
            throw new ArgumentException("Elige «Puede», «Con autorización» o «No».");
        var puede = s.Nivel == NivelPermiso.Permitido;
        if (puede && s.UmbralCentavos <= 0)
            throw new ArgumentException("El límite tiene que ser mayor a cero; para que no tenga límite, déjalo vacío.");
        if (puede && s.UmbralCentavos > 10000 && Permisos.EnPorcentaje(s.Accion))
            throw new ArgumentException("El descuento llega hasta 100 %.");
        await using (var turno = await db.Database.BeginTransactionAsync(ct))
        {
            var usuario = usuarioId is { } id ? await UsuarioAsync(id, ct) : null;
            var filas = await FilasAsync(usuarioId, ct);
            var antes = filas.GetValueOrDefault(s.Accion);
            var nuevo = new Permiso(s.Accion, s.Nivel, puede ? s.UmbralCentavos : antes?.UmbralCentavos, s.PideMotivo ?? antes?.PideMotivo ?? false);
            if (usuario is { Activo: true } && s.Accion == AccionControlada.EditarReglas && s.Nivel != NivelPermiso.Permitido)
                await QuedaAlguienAsync(usuario.Id, ct);
            if (Poner(usuarioId, filas, nuevo))
            {
                Registrar(caja, s.UsuarioId, $"{usuario?.Nombre ?? "Permisos por defecto"}: «{Permisos.Nombre(s.Accion)}» → {Describir(nuevo)}");
                await db.SaveChangesAsync(ct);
            }
            await turno.CommitAsync(ct);
        }
        return await ListaAsync(null, ct);
    }

    /// <summary>«Copiar los permisos de…»: todos los de otro usuario, como los tiene ahora.</summary>
    public async Task<UsuariosYPermisosDto> CopiarAsync(Guid cajaId, Guid usuarioId, SolicitudCopiarPermisos s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        var usuario = await UsuarioAsync(usuarioId, ct);
        if (s.DeUsuarioId == usuarioId)
            throw new ArgumentException("Elige a otro usuario para copiar sus permisos.");
        var de = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(u => u.Id == s.DeUsuarioId, ct)
            ?? throw new InvalidOperationException("El usuario del que se copian no existe.");
        var suyos = await new ServicioControl(db).PermisosAsync(de.Id, ct);
        return await PonerTodosAsync(caja, s.UsuarioId, usuario, suyos.De, $"{usuario.Nombre} ahora tiene los permisos de {de.Nombre}", ct);
    }

    /// <summary>«Todos»: cada permiso en «Puede», sin límite, como la primera cuenta.</summary>
    public async Task<UsuariosYPermisosDto> TodosAsync(Guid cajaId, Guid usuarioId, SolicitudUsuarios s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        var usuario = await UsuarioAsync(usuarioId, ct);
        return await PonerTodosAsync(caja, s.UsuarioId, usuario, Permiso.Todo, $"{usuario.Nombre}: todos los permisos", ct);
    }

    /// <summary>«Los de defecto»: los mismos que tiene un usuario nuevo.</summary>
    public async Task<UsuariosYPermisosDto> DeDefectoAsync(Guid cajaId, Guid usuarioId, SolicitudUsuarios s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        var usuario = await UsuarioAsync(usuarioId, ct);
        var defecto = await new ServicioControl(db).DefectoAsync(ct);
        return await PonerTodosAsync(caja, s.UsuarioId, usuario, defecto.De, $"{usuario.Nombre}: los permisos por defecto", ct);
    }

    /// <summary>
    /// Un usuario nuevo, con su nombre y su PIN de 4 cifras: entra en las cajas de la sucursal de esta caja y empieza con
    /// los permisos por defecto. Su usuario (con el que se entra donde todavía se escribe) sale de su nombre.
    /// </summary>
    public async Task<UsuariosYPermisosDto> NuevoAsync(Guid cajaId, SolicitudNuevoUsuario s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        var nombre = string.Join(' ', (s.Nombre ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (nombre.Length == 0)
            throw new ArgumentException("Escribe su nombre.");
        if (nombre.Length > FichasEntrada.LargoNombre)
            throw new ArgumentException($"El nombre va hasta {FichasEntrada.LargoNombre} letras: con el nombre y una inicial basta.");
        Usuario usuario;
        await using (var turno = await db.Database.BeginTransactionAsync(ct))
        {
            var usuarios = await db.Usuarios.AsNoTracking().Select(u => new { u.Nombre, u.NombreUsuario, u.Activo }).ToListAsync(ct);
            if (usuarios.FirstOrDefault(u => Texto.Normalizar(u.Nombre) == Texto.Normalizar(nombre)) is { } igual)
                throw new InvalidOperationException(igual.Activo
                    ? $"Ya hay un usuario que se llama {igual.Nombre.Trim()}: agrégale el apellido o una inicial."
                    : $"Ya hay un usuario que se llama {igual.Nombre.Trim()}, dado de baja: vuélvelo a dar de alta.");
            ValidarPin(s.Pin);

            usuario = new Usuario { Nombre = nombre, NombreUsuario = NombreUsuario(nombre, usuarios.Select(u => u.NombreUsuario)), SucursalId = caja.SucursalId };
            usuario.EstablecerPin(s.Pin);
            db.Usuarios.Add(usuario);
            var defecto = await new ServicioControl(db).DefectoAsync(ct);
            db.PermisosUsuario.AddRange(Permisos.Acciones.Select(a => Fila(usuario.Id, defecto.De(a))));
            Registrar(caja, s.UsuarioId, $"Dio de alta a {nombre} (usuario «{usuario.NombreUsuario}»), con los permisos por defecto");
            await db.SaveChangesAsync(ct);
            await turno.CommitAsync(ct);
        }
        return await ListaAsync(usuario.Id, ct);
    }

    /// <summary>El PIN nuevo, de 4 cifras: empieza sin intentos fallidos. El PIN nunca queda en la bitácora.</summary>
    public async Task<UsuariosYPermisosDto> CambiarPinAsync(Guid cajaId, Guid usuarioId, SolicitudPinUsuario s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        ValidarPin(s.Pin);
        var usuario = await UsuarioAsync(usuarioId, ct);
        usuario.EstablecerPin(s.Pin);
        Registrar(caja, s.UsuarioId, $"Cambió el PIN de {usuario.Nombre}");
        await db.SaveChangesAsync(ct);
        return await ListaAsync(null, ct);
    }

    /// <summary>
    /// Dar de baja (ya no entra ni autoriza; su ficha se va, y su letra no se le da a nadie más) o volver a dar de alta. Nadie
    /// se da de baja a sí mismo, y siempre queda alguien con «Usuarios y permisos».
    /// </summary>
    public async Task<UsuariosYPermisosDto> ActivarAsync(Guid cajaId, Guid usuarioId, SolicitudActivarUsuario s, CancellationToken ct = default)
    {
        var caja = await ExigirAsync(cajaId, s.UsuarioId, registrar: true, ct);
        await using (var turno = await db.Database.BeginTransactionAsync(ct))
        {
            var usuario = await UsuarioAsync(usuarioId, ct);
            if (usuario.Activo != s.Activo)
            {
                if (!s.Activo)
                {
                    if (usuarioId == s.UsuarioId)
                        throw new InvalidOperationException("No puedes darte de baja tú: pídeselo a otro usuario con «Usuarios y permisos».");
                    await QuedaAlguienAsync(usuarioId, ct);
                }
                usuario.Activo = s.Activo;
                Registrar(caja, s.UsuarioId, s.Activo ? $"Volvió a dar de alta a {usuario.Nombre}" : $"Dio de baja a {usuario.Nombre}");
                await db.SaveChangesAsync(ct);
            }
            await turno.CommitAsync(ct);
        }
        return await ListaAsync(null, ct);
    }

    /// <summary>
    /// El usuario con que se entra donde todavía se escribe: la primera palabra de su nombre, en minúsculas y sin acentos
    /// («Ana López» → «ana»); si ya es de otro, todo el nombre junto («analopez»), y si tampoco, con un número («ana2»).
    /// </summary>
    public static string NombreUsuario(string nombre, IEnumerable<string> ocupados)
    {
        var usados = ocupados.Select(u => u.Trim().ToLowerInvariant()).ToHashSet();
        var palabras = Texto.Normalizar(nombre).Replace('Ñ', 'N').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(p => p.Length > 0).ToList();
        if (palabras.Count == 0)
            palabras = ["usuario"];
        foreach (var candidato in new[] { palabras[0], string.Concat(palabras) })
        {
            if (!usados.Contains(candidato))
                return candidato;
        }
        for (var i = 2; ; i++)
        {
            if (!usados.Contains($"{palabras[0]}{i}"))
                return $"{palabras[0]}{i}";
        }
    }

    // ── Apoyo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quien entró a la caja tiene que tener «Usuarios y permisos» en «Puede»; si no, el error dice quién sí. El intento de
    /// cambiar algo sin él queda en la bitácora.
    /// </summary>
    private async Task<Caja> ExigirAsync(Guid cajaId, Guid sesionId, bool registrar, CancellationToken ct)
    {
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(sesionId, caja.SucursalId, AccionControlada.EditarReglas, ct: ct)).Nivel == NivelPermiso.Permitido)
            return caja;
        if (registrar)
        {
            control.Registrar(caja.SucursalId, caja.Id, sesionId, AccionControlada.EditarReglas, "Intento sin permiso", resultado: ResultadoBitacora.Negado);
            await db.SaveChangesAsync(ct);
        }
        throw new PermisoRequerido(AccionControlada.EditarReglas, NivelPermiso.NoPermitido,
            Permisos.QuienLoHace(AccionControlada.EditarReglas, await control.QuienesPuedenAsync(caja.SucursalId, AccionControlada.EditarReglas, ct)));
    }

    private async Task<Usuario> UsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.FindAsync([usuarioId], ct) ?? throw new InvalidOperationException("Ese usuario no existe.");

    /// <summary>
    /// Si sin <paramref name="sin"/> (o sin su «Usuarios y permisos») no queda nadie activo con él en «Puede», no se puede. Se
    /// revisa dentro del turno de lo que se va a guardar (la transacción, que en SQLite toma la escritura desde el principio):
    /// dos cambios al mismo tiempo en dos cajas no se quitan «Usuarios y permisos» uno al otro, porque el segundo espera y ya
    /// ve el primero.
    /// </summary>
    private async Task QuedaAlguienAsync(Guid sin, CancellationToken ct)
    {
        var otros = await db.Usuarios.AsNoTracking().Where(u => u.Activo && u.Id != sin).Select(u => (Guid?)u.Id).ToListAsync(ct);
        if (!await db.PermisosUsuario.AsNoTracking().AnyAsync(p => otros.Contains(p.UsuarioId) && p.Accion == AccionControlada.EditarReglas
                && p.Nivel == NivelPermiso.Permitido, ct))
            throw new InvalidOperationException(SiempreAlguien);
    }

    private static void ValidarPin(string? pin)
    {
        if (pin is null || pin.Length != FichasEntrada.CifrasPin || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException($"El PIN es de {FichasEntrada.CifrasPin} cifras.");
    }

    /// <summary>Los permisos guardados de un usuario (o los de defecto), para cambiarlos.</summary>
    private async Task<Dictionary<AccionControlada, PermisoUsuario>> FilasAsync(Guid? usuarioId, CancellationToken ct) =>
        await db.PermisosUsuario.Where(p => p.UsuarioId == usuarioId).ToDictionaryAsync(p => p.Accion, ct);

    /// <summary>Lo pone (o lo agrega, si faltaba); dice si cambió algo.</summary>
    private bool Poner(Guid? usuarioId, Dictionary<AccionControlada, PermisoUsuario> filas, Permiso permiso)
    {
        if (filas.TryGetValue(permiso.Accion, out var fila))
            return fila.Poner(permiso);
        filas[permiso.Accion] = fila = Fila(usuarioId, permiso);
        db.PermisosUsuario.Add(fila);
        return true;
    }

    /// <summary>Todos los permisos de un usuario a la vez («Copiar los permisos de…», «Todos», «Los de defecto»).</summary>
    private async Task<UsuariosYPermisosDto> PonerTodosAsync(
        Caja caja, Guid sesionId, Usuario usuario, Func<AccionControlada, Permiso> permiso, string detalle, CancellationToken ct)
    {
        await using (var turno = await db.Database.BeginTransactionAsync(ct))
        {
            if (usuario.Activo && permiso(AccionControlada.EditarReglas).Nivel != NivelPermiso.Permitido)
                await QuedaAlguienAsync(usuario.Id, ct);
            var filas = await FilasAsync(usuario.Id, ct);
            var cambio = false;
            foreach (var accion in Permisos.Acciones)
                cambio |= Poner(usuario.Id, filas, permiso(accion) with { Accion = accion });
            if (cambio)
            {
                Registrar(caja, sesionId, detalle);
                await db.SaveChangesAsync(ct);
            }
            await turno.CommitAsync(ct);
        }
        return await ListaAsync(null, ct);
    }

    private static PermisoUsuario Fila(Guid? usuarioId, Permiso permiso) => new()
    {
        UsuarioId = usuarioId,
        Accion = permiso.Accion,
        Nivel = permiso.Nivel,
        UmbralCentavos = permiso.UmbralCentavos,
        PideMotivo = permiso.PideMotivo,
    };

    private void Registrar(Caja caja, Guid sesionId, string detalle) =>
        new ServicioControl(db).Registrar(caja.SucursalId, caja.Id, sesionId, AccionControlada.EditarReglas, detalle);

    /// <summary>Cómo quedó, para la bitácora: «Puede, hasta $200.00», «Con autorización», «No».</summary>
    private static string Describir(Permiso p) => p.Nivel switch
    {
        NivelPermiso.Permitido when p.UmbralCentavos is { } umbral => $"Puede, hasta {Permisos.TextoMonto(p.Accion, umbral)}",
        NivelPermiso.Permitido => "Puede",
        NivelPermiso.RequiereAutorizacion => "Con autorización",
        _ => "No",
    } + (p.PideMotivo && p.Nivel != NivelPermiso.NoPermitido ? ", con motivo" : "");

    private async Task<UsuariosYPermisosDto> ListaAsync(Guid? elegido, CancellationToken ct)
    {
        var usuarios = await db.Usuarios.AsNoTracking().Select(u => new { u.Id, u.Nombre, u.NombreUsuario, u.Activo, u.PrimeraCuenta }).ToListAsync(ct);
        var filas = (await db.PermisosUsuario.AsNoTracking()
                .Select(p => new { p.UsuarioId, Permiso = new Permiso(p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo) }).ToListAsync(ct))
            .ToLookup(f => f.UsuarioId, f => f.Permiso);
        var defecto = new PermisosDeUsuario(filas[null]);
        var lista = usuarios
            .OrderBy(u => !u.Activo).ThenBy(u => u.Nombre.Trim(), StringComparer.Create(Mexico, ignoreCase: true)).ThenBy(u => u.Id)
            .Select(u =>
            {
                var permisos = new PermisosDeUsuario(filas[u.Id]);
                return new UsuarioYPermisosDto(u.Id, u.Nombre.Trim(), u.NombreUsuario, u.Activo, FichasEntrada.Resumen(permisos, defecto), u.PrimeraCuenta,
                    permisos.Lista());
            })
            .ToList();
        return new UsuariosYPermisosDto(lista, defecto.Lista(), elegido);
    }
}
