using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que cambió en los usuarios del POS con los de eleventa.</summary>
public sealed class ResultadoUsuarios
{
    /// <summary>Usuarios de eleventa que llegaron al POS, sin PIN y con los permisos por defecto.</summary>
    public int Nuevos { get; internal set; }

    /// <summary>Usuarios dados de alta aquí con el mismo nombre que uno de eleventa: se ligaron a él, con su PIN y sus permisos.</summary>
    public int Ligados { get; internal set; }

    /// <summary>Usuarios a los que eleventa les cambió el nombre.</summary>
    public int Renombrados { get; internal set; }

    /// <summary>Usuarios dados de baja (o borrados) en eleventa: aquí quedaron dados de baja.</summary>
    public int DadosDeBaja { get; internal set; }

    /// <summary>Lo que no se hizo (o se hizo distinto) y conviene revisar.</summary>
    public List<string> Avisos { get; } = [];

    public bool HuboCambios => Nuevos + Ligados + Renombrados + DadosDeBaja > 0;

    public override string ToString() =>
        HuboCambios
            ? string.Join(", ", new[] { (Nuevos, "nuevos"), (Ligados, "ligados"), (Renombrados, "con otro nombre"), (DadosDeBaja, "dados de baja") }
                .Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"))
            : "sin cambios";
}

/// <summary>
/// Los usuarios de eleventa en el POS (el piloto alfa; decisión del dueño del 2026-10-25): de eleventa solo se traen su número
/// y su nombre, nunca su clave; el PIN de 4 cifras es del POS y lo pone el dueño; y cada operación de la caja 2 se anota en
/// eleventa a nombre de quien la hizo (<see cref="EnvioAEleventa.CajeroEleventa"/>). Lo usan la importación (la base nueva y la
/// importación final) y la lectura de cada pocos minutos:
/// <list type="bullet">
/// <item>cada usuario de eleventa activo y sin borrar es un usuario del POS ligado a su número (<see cref="Usuario.EleventaId"/>),
/// con su nombre completo (o su usuario, si no tiene), sin PIN y con los permisos por defecto: tiene su ficha, pero no entra
/// hasta que el dueño le pone PIN;</item>
/// <item>se busca por su número: nunca se duplica. Uno dado de alta aquí con el mismo nombre (y sin número de eleventa) se liga
/// a él, con su PIN y sus permisos (decidido, por revisar: si no, saldrían dos fichas de la misma persona);</item>
/// <item>si eleventa le cambia el nombre, aquí también; si lo da de baja o lo borra, aquí queda dado de baja (nunca se borra),
/// salvo que sea el único que puede dar los PIN y los permisos. Volver a darlo de alta en eleventa no lo regresa solo: lo
/// regresa el dueño en «Usuarios y permisos» (decidido, por revisar: así un PIN de antes no vuelve a entrar sin que el dueño
/// lo vea, ni se le regresa a quien el dueño dio de baja aquí);</item>
/// <item>nunca se tocan su PIN ni sus permisos, y los dados de alta aquí (sin número de eleventa) siguen como están.</item>
/// </list>
/// Lo que cambia queda en la bitácora de la sucursal. Quien llama guarda. Solo del piloto y de la importación.
/// </summary>
public static class UsuariosDeEleventa
{
    public const string AvisoSeQuedaActivo =
        "Usuario dado de baja en eleventa que aquí es el único que da los PIN y los permisos: se quedó activo (dale «Usuarios y permisos» a otro)";

    /// <param name="sucursalId">La sucursal de ese eleventa: ahí entran sus usuarios.</param>
    public static async Task<ResultadoUsuarios> AplicarAsync(PosDbContext db, IReadOnlyList<FilaUsuario> filas, Guid sucursalId, CancellationToken ct = default)
    {
        var r = new ResultadoUsuarios();
        // Una lectura sin ningún usuario no es un eleventa sin usuarios (no entraría nadie): no se da de baja a nadie por no verlo.
        if (filas.Count == 0)
            return r;
        var usuarios = await db.Usuarios.Where(u => u.SucursalId == sucursalId || u.SucursalId == null).ToListAsync(ct);
        var porNumero = usuarios.Where(u => u.EleventaId is not null).GroupBy(u => u.EleventaId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(u => u.SucursalId is null).First());
        // Los usuarios con que se entra escribiéndolo son únicos en toda la base.
        var ocupados = await db.Usuarios.AsNoTracking().Select(u => u.NombreUsuario).ToListAsync(ct);
        // Quienes dan los PIN y los permisos (activos, con PIN y con «Usuarios y permisos»): siempre queda uno.
        var conPin = await db.Usuarios.AsNoTracking().Where(u => u.Activo && u.PinHash != "").Select(u => (Guid?)u.Id).ToListAsync(ct);
        var quienesDan = (await db.PermisosUsuario.AsNoTracking()
                .Where(p => conPin.Contains(p.UsuarioId) && p.Accion == AccionControlada.EditarReglas && p.Nivel == NivelPermiso.Permitido)
                .Select(p => p.UsuarioId!.Value).ToListAsync(ct))
            .ToHashSet();
        var control = new ServicioControl(db);
        PermisosDeUsuario? defecto = null;

        foreach (var fila in filas.OrderBy(f => f.Id))
        {
            var nombre = NombreEnElPos(fila);
            if (porNumero.TryGetValue(fila.Id, out var usuario))
            {
                var nuevo = NombreLibre(nombre, fila, usuarios, usuario.Id);
                if (usuario.Nombre != nuevo)
                {
                    Anotar($"Eleventa le cambió el nombre: {usuario.Nombre} → {nuevo}");
                    usuario.Nombre = nuevo;
                    r.Renombrados++;
                }
                if (!fila.Vigente)
                    DarDeBaja(usuario, fila.Eliminado ? "Borrado en eleventa" : "Dado de baja en eleventa");
                continue;
            }
            // Lo dado de baja o borrado antes de llegar aquí no llega.
            if (!fila.Vigente)
                continue;

            var mismos = usuarios.Where(u => u.EleventaId is null && Texto.Normalizar(u.Nombre) == Texto.Normalizar(nombre)).ToList();
            if (mismos.Count == 1)
            {
                var suyo = mismos[0];
                suyo.EleventaId = fila.Id;
                porNumero[fila.Id] = suyo;
                Anotar($"Se ligó a su usuario de eleventa (número {fila.Id}): {suyo.Nombre}, con su PIN y sus permisos");
                if (suyo.Nombre != nombre)
                    (suyo.Nombre, r.Renombrados) = (nombre, r.Renombrados + 1);
                r.Ligados++;
                continue;
            }

            defecto ??= await control.DefectoAsync(ct);
            var libre = NombreLibre(nombre, fila, usuarios, null);
            var alta = new Usuario
            {
                Nombre = libre,
                NombreUsuario = RegistroUsuarios.NombreUsuario(libre, ocupados),
                SucursalId = sucursalId,
                EleventaId = fila.Id,
                Rol = Rol.Cajero,
            };
            ocupados.Add(alta.NombreUsuario);
            usuarios.Add(alta);
            porNumero[fila.Id] = alta;
            db.Usuarios.Add(alta);
            db.PermisosUsuario.AddRange(Permisos.Acciones.Select(a => Fila(alta.Id, defecto.De(a))));
            Anotar($"Llegó de eleventa: {alta.Nombre} (número {fila.Id} de eleventa; donde se escribe, «{alta.NombreUsuario}»), sin PIN y con los permisos por defecto");
            r.Nuevos++;
        }

        // Ligado a un usuario que ya no está en eleventa (eleventa los marca como borrados, no los quita; un respaldo restaurado
        // de antes de darlo de alta, sí): como uno borrado.
        var enEleventa = filas.Select(f => f.Id).ToHashSet();
        foreach (var usuario in porNumero.Where(x => !enEleventa.Contains(x.Key)).Select(x => x.Value))
            DarDeBaja(usuario, "Ya no está en eleventa");
        return r;

        void Anotar(string detalle) => control.Registrar(sucursalId, null, null, AccionControlada.EditarReglas, detalle);

        // Dado de baja aquí (nunca se borra; su PIN y sus permisos se quedan), salvo que sea el único que da los PIN y los permisos.
        void DarDeBaja(Usuario usuario, string porque)
        {
            if (!usuario.Activo)
                return;
            if (quienesDan.Contains(usuario.Id) && quienesDan.Count == 1)
            {
                r.Avisos.Add(AvisoSeQuedaActivo);
                return;
            }
            quienesDan.Remove(usuario.Id);
            usuario.Activo = false;
            Anotar($"{porque}: {usuario.Nombre} queda dado de baja (su PIN y sus permisos se quedan)");
            r.DadosDeBaja++;
        }
    }

    /// <summary>
    /// Como se llama en el POS: su nombre completo o, si no tiene, su usuario de eleventa, sin espacios de más y de hasta
    /// <see cref="FichasEntrada.LargoNombre"/> letras (lo que cabe en su ficha).
    /// </summary>
    public static string NombreEnElPos(FilaUsuario fila)
    {
        var nombre = SinEspaciosDeMas(fila.Nombre) is { Length: > 0 } completo ? completo
            : SinEspaciosDeMas(fila.Usuario) is { Length: > 0 } usuario ? usuario
            : $"Usuario {fila.Id} de eleventa";
        return Recortar(nombre, FichasEntrada.LargoNombre);
    }

    /// <summary>
    /// El nombre, si nadie más de la sucursal se llama así; si no, con su usuario de eleventa (o su número) entre paréntesis: dos
    /// fichas no se llaman igual. Pasa con dos usuarios de eleventa del mismo nombre, o con uno dado de alta aquí que ya tiene su
    /// número de eleventa.
    /// </summary>
    private static string NombreLibre(string nombre, FilaUsuario fila, List<Usuario> usuarios, Guid? propio)
    {
        if (!usuarios.Any(u => u.Id != propio && Texto.Normalizar(u.Nombre) == Texto.Normalizar(nombre)))
            return nombre;
        var cual = SinEspaciosDeMas(fila.Usuario) is { Length: > 0 and <= 15 } usuario ? usuario : $"#{fila.Id}";
        return $"{Recortar(nombre, FichasEntrada.LargoNombre - cual.Length - 3)} ({cual})";
    }

    private static string SinEspaciosDeMas(string texto) =>
        string.Join(' ', texto.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Hasta <paramref name="largo"/> letras, cortando en el último espacio si lo hay.</summary>
    private static string Recortar(string texto, int largo)
    {
        if (texto.Length <= largo)
            return texto;
        var corte = texto.LastIndexOf(' ', largo);
        return (corte > largo / 2 ? texto[..corte] : texto[..largo]).Trim();
    }

    private static PermisoUsuario Fila(Guid usuarioId, Permiso permiso) => new()
    {
        UsuarioId = usuarioId,
        Accion = permiso.Accion,
        Nivel = permiso.Nivel,
        UmbralCentavos = permiso.UmbralCentavos,
        PideMotivo = permiso.PideMotivo,
    };
}
