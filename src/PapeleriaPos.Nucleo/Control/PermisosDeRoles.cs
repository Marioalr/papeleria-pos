using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Nucleo.Control;

/// <summary>
/// El paso de los roles (dueño, encargado y cajero, con sus reglas por sucursal) a los permisos por usuario (entrega 1).
/// Solo lo usan la migración del servicio, al arrancar, y la caja que todavía tiene una copia de antes (con reglas)
/// mientras baja la nueva: nada decide con los roles.
/// </summary>
public static class PermisosDeRoles
{
    /// <summary>
    /// Lo que valía para alguien de ese rol en su sucursal, acción por acción, con su límite y si pide motivo: el dueño,
    /// todo; los demás, la regla de su sucursal, si no la general y si no la inicial. Lo que era solo del dueño, «No»; lo
    /// que no tenía regla, «Con autorización».
    /// </summary>
    /// <param name="sucursalId">La sucursal del usuario. Vacía: las reglas generales.</param>
    public static List<Permiso> De(Rol rol, IEnumerable<ReglaPermiso> reglas, Guid? sucursalId)
    {
        var lista = reglas.ToList();
        return Permisos.Acciones.Select(accion => De(rol, lista, sucursalId, accion)).ToList();
    }

    /// <summary>Los de defecto: los del cajero con las reglas generales (las de una sucursal no cuentan).</summary>
    public static List<Permiso> PorDefecto(IEnumerable<ReglaPermiso> reglas) => De(Rol.Cajero, reglas.Where(r => r.SucursalId is null), null);

    private static Permiso De(Rol rol, List<ReglaPermiso> reglas, Guid? sucursalId, AccionControlada accion)
    {
        if (rol == Rol.Dueno)
            return Permiso.Todo(accion);
        if (Permisos.SoloDelDueno(accion))
            return new Permiso(accion, NivelPermiso.NoPermitido);
        if (Efectiva(reglas, rol, sucursalId, accion) is not { } regla)
            return new Permiso(accion, NivelPermiso.RequiereAutorizacion);
        return new Permiso(accion, regla.Nivel, regla.Nivel == NivelPermiso.Permitido ? regla.UmbralCentavos : null, regla.PideMotivo);
    }

    /// <summary>La regla que valía: la de la sucursal gana sobre la general, y la general sobre la inicial.</summary>
    private static ReglaPermiso? Efectiva(List<ReglaPermiso> reglas, Rol rol, Guid? sucursalId, AccionControlada accion)
    {
        var candidatas = reglas.Where(r => r.Accion == accion && r.Rol == rol).ToList();
        return candidatas.FirstOrDefault(r => r.SucursalId == sucursalId)
            ?? candidatas.FirstOrDefault(r => r.SucursalId is null)
            ?? Permisos.Iniciales.FirstOrDefault(r => r.Accion == accion && r.Rol == rol);
    }
}
