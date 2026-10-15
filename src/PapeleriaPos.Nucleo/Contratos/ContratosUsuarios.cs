using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Usuarios y permisos (entrega 1) ──────────────────────────────────────────
// Todo lo hace quien entró a la caja (UsuarioId: el de la sesión) con «Usuarios y permisos» en «Puede», sin volver a
// escribir su PIN, como el corte. Cada cambio se guarda al momento y queda en la bitácora.

/// <summary>
/// La pantalla «Usuarios y permisos»: los usuarios de la tienda (los activos por nombre y al final los dados de baja), cada
/// uno con sus permisos, y los de defecto.
/// </summary>
/// <param name="Elegido">El que se acaba de dar de alta, para enseñarlo.</param>
public sealed record UsuariosYPermisosDto(IReadOnlyList<UsuarioYPermisosDto> Usuarios, IReadOnlyList<Permiso> Defecto, Guid? Elegido = null);

/// <param name="Usuario">Con el que se entra donde todavía se escribe (el celular, dar de alta una caja, autorizar sin las fichas).</param>
/// <param name="Resumen">«Todos los permisos», «Permisos por defecto» o lo que tiene de más.</param>
/// <param name="PrimeraCuenta">La que se creó al instalar con todos los permisos (al migrar, el dueño).</param>
/// <param name="Permisos">Uno por acción, en su orden.</param>
public sealed record UsuarioYPermisosDto(
    Guid Id, string Nombre, string Usuario, bool Activo, string Resumen, bool PrimeraCuenta, IReadOnlyList<Permiso> Permisos);

/// <summary>Un permiso de un usuario o de los de defecto, como quedó: «Puede» (con su límite o sin él), «Con autorización» o «No».</summary>
/// <param name="UmbralCentavos">Solo en «Puede»: hasta dónde (en el descuento, centésimas de porcentaje: 1000 = 10 %). Vacío: sin límite.</param>
/// <param name="PideMotivo">Vacío: el que ya tenía.</param>
public sealed record SolicitudPermisoUsuario(Guid UsuarioId, AccionControlada Accion, NivelPermiso Nivel, long? UmbralCentavos = null, bool? PideMotivo = null);

/// <summary>«Copiar los permisos de…»: todos los de <paramref name="DeUsuarioId"/>.</summary>
public sealed record SolicitudCopiarPermisos(Guid UsuarioId, Guid DeUsuarioId);

/// <summary>«Todos» y «Los de defecto»: solo quién lo hace.</summary>
public sealed record SolicitudUsuarios(Guid UsuarioId);

/// <summary>Un usuario nuevo: su nombre y su PIN de 4 cifras. Empieza con los permisos por defecto.</summary>
public sealed record SolicitudNuevoUsuario(Guid UsuarioId, string Nombre, string Pin);

/// <summary>El PIN nuevo de un usuario (la pantalla lo pide dos veces).</summary>
public sealed record SolicitudPinUsuario(Guid UsuarioId, string Pin);

/// <summary>Dar de baja a un usuario (ya no entra ni autoriza) o volverlo a dar de alta.</summary>
public sealed record SolicitudActivarUsuario(Guid UsuarioId, bool Activo);
