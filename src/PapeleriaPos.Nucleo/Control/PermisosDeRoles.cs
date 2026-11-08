using PapeleriaPos.Nucleo.Contratos;
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
    /// Las reglas con que arrancaba el sistema (docs/fase-1-control.md), por rol: valen para lo que no tenía regla propia. El
    /// dueño podía todo.
    /// </summary>
    public static readonly IReadOnlyList<ReglaPermiso> Iniciales =
    [
        .. Ambos(AccionControlada.QuitarProducto, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.ArticuloComun, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.CambiarPrecio, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.VenderBajoCosto, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.VenderBajoCosto, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.CancelarTicket, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.CancelarTicket, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.Devolucion, Rol.Cajero, NivelPermiso.Permitido, umbral: 20000),
        Regla(AccionControlada.Devolucion, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.SalidaEfectivo, NivelPermiso.Permitido, motivo: true),
        .. Ambos(AccionControlada.EntradaEfectivo, NivelPermiso.Permitido, motivo: true),
        .. Ambos(AccionControlada.AbrirCajonSinVenta, NivelPermiso.Permitido, motivo: true),
        Regla(AccionControlada.Reimprimir, Rol.Cajero, NivelPermiso.Permitido, motivo: true),
        Regla(AccionControlada.Reimprimir, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.VerificarTransferencia, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.VerificarTransferencia, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.CorteZ, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.CorteZ, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.ConfigurarTicket, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.ConfigurarTicket, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.EditarReglas, NivelPermiso.NoPermitido),
        .. Ambos(AccionControlada.AdministrarCajas, NivelPermiso.NoPermitido),
        .. Ambos(AccionControlada.ConfigurarTelegram, NivelPermiso.NoPermitido),
        .. Ambos(AccionControlada.ConfigurarImpresora, NivelPermiso.NoPermitido),
        // Los clientes registrados (fase 3): el encargado también los da de alta; lo que toca a sus privilegios se le avisa al dueño.
        Regla(AccionControlada.AdministrarClientes, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.AdministrarClientes, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.Descuento, Rol.Cajero, NivelPermiso.Permitido, umbral: 1000),
        Regla(AccionControlada.Descuento, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.EditarCatalogo, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.EditarCatalogo, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.AltaRapida, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.RecibirMercancia, NivelPermiso.Permitido),
        Regla(AccionControlada.CostearCompra, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.CostearCompra, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.PedirCompra, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.PedirCompra, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.AbrirPaquete, NivelPermiso.Permitido),
        Regla(AccionControlada.AjustarInventario, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.AjustarInventario, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.Merma, Rol.Cajero, NivelPermiso.Permitido, umbral: 20000),
        Regla(AccionControlada.Merma, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.ContarInventario, NivelPermiso.Permitido),
        Regla(AccionControlada.ConteoCompleto, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.ConteoCompleto, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.ConsultarBitacora, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.ConsultarBitacora, Rol.Encargado, NivelPermiso.Permitido),
        // El crédito (fase 3): el límite y los ajustes, al empezar, solo el dueño (se pueden dar desde el editor de reglas);
        // pasar el límite lo autoriza el encargado.
        .. Ambos(AccionControlada.DarCredito, NivelPermiso.NoPermitido),
        Regla(AccionControlada.ExcederLimiteCredito, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.ExcederLimiteCredito, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.AjustarCredito, NivelPermiso.NoPermitido),
        Regla(AccionControlada.CancelarAbono, Rol.Cajero, NivelPermiso.RequiereAutorizacion, motivo: true),
        Regla(AccionControlada.CancelarAbono, Rol.Encargado, NivelPermiso.Permitido, motivo: true),
        Regla(AccionControlada.DevolverSaldoAFavor, Rol.Cajero, NivelPermiso.RequiereAutorizacion),
        Regla(AccionControlada.DevolverSaldoAFavor, Rol.Encargado, NivelPermiso.Permitido),
        // El corte del día (2026-10-14): la propuesta lo deja a la primera cuenta, que hoy es el dueño.
        .. Ambos(AccionControlada.CorteDelDia, NivelPermiso.NoPermitido),
        // Cancelar una entrada o salida (2026-10-22), como las otras cancelaciones: su permiso propio y con motivo, el
        // encargado sí. El cajero, «No» en lugar de «Con autorización» (dueño, 2026-10-15: «o puede o no puede»): que entre
        // quien puede (Ctrl+U), o la compensa con la contraria, como hasta hoy. Con los permisos por usuario que ya hay, la
        // acción nueva le llega a cada quien como en MigracionPermisos: a quien tiene todo (el dueño), «Puede»; a los demás,
        // la de defecto, que es la del cajero («No»), hasta que el dueño se la dé en «Usuarios y permisos».
        Regla(AccionControlada.CancelarMovimiento, Rol.Cajero, NivelPermiso.NoPermitido, motivo: true),
        Regla(AccionControlada.CancelarMovimiento, Rol.Encargado, NivelPermiso.Permitido, motivo: true),
        // «Agregar inventario» (2026-10-24), como recibir la mercancía: los dos. Con otra regla de recibir, la sigue (Sigue).
        .. Ambos(AccionControlada.AgregarInventario, NivelPermiso.Permitido),
    ];

    /// <summary>
    /// La acción que ya había y a la que sigue una nueva al llegar: quien tiene esa en «Puede» recibe la nueva en «Puede»; los
    /// demás, «No». «Agregar inventario» (2026-10-24) sigue a «Recibir mercancía a ciegas»: quien recibe lo que llega también lo
    /// agrega. Lo usan los permisos que salen de un rol (<see cref="De(Rol, IEnumerable{ReglaPermiso}, Guid?)"/>) y la migración,
    /// para los usuarios y los de defecto que ya tienen sus permisos. Vacío si la acción no sigue a otra.
    /// </summary>
    public static AccionControlada? Sigue(AccionControlada accion) =>
        accion == AccionControlada.AgregarInventario ? AccionControlada.RecibirMercancia : null;

    /// <summary>La acción nueva como la que sigue (<see cref="Sigue"/>): «Puede» si esa está en «Puede»; si no, «No».</summary>
    public static Permiso ComoLaQueSigue(AccionControlada accion, Permiso aLaQueSigue) =>
        new(accion, aLaQueSigue.Nivel == NivelPermiso.Permitido ? NivelPermiso.Permitido : NivelPermiso.NoPermitido);

    /// <summary>Lo que solo podía hacer el dueño, aunque hubiera una regla para otro rol.</summary>
    public static bool SoloDelDueno(AccionControlada accion) =>
        accion is AccionControlada.EditarReglas or AccionControlada.AdministrarCajas or AccionControlada.ConfigurarTelegram
            or AccionControlada.ConfigurarImpresora or AccionControlada.ConfigurarMargen
            // Las de la entrega 1: con los roles, eran solo del dueño (el costo de las impresiones, el límite de efectivo).
            or AccionControlada.VerUtilidad or AccionControlada.VerReportes or AccionControlada.ConfigurarTienda;

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
        if (SoloDelDueno(accion))
            return new Permiso(accion, NivelPermiso.NoPermitido);
        if (Sigue(accion) is { } otra)
            return ComoLaQueSigue(accion, De(rol, reglas, sucursalId, otra));
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
            ?? Iniciales.FirstOrDefault(r => r.Accion == accion && r.Rol == rol);
    }

    /// <summary>
    /// Las reglas que valían en la sucursal para el cajero y el encargado, como las mandaba la copia de la caja antes de la
    /// entrega 1: van en la copia de ahora solo para una caja que todavía no se actualiza (la de ahora usa los permisos).
    /// </summary>
    public static List<ReglaDto> Efectivas(IEnumerable<ReglaPermiso> reglas, Guid sucursalId)
    {
        var lista = reglas.ToList();
        return Permisos.Acciones.Where(a => !SoloDelDueno(a))
            .SelectMany(a => new[] { Rol.Cajero, Rol.Encargado }.Select(rol => Efectiva(lista, rol, sucursalId, a) is { } regla
                ? new ReglaDto(a, rol, regla.Nivel, regla.UmbralCentavos, regla.PideMotivo)
                : new ReglaDto(a, rol, NivelPermiso.RequiereAutorizacion, null, false)))
            .ToList();
    }

    private static ReglaPermiso Regla(AccionControlada accion, Rol rol, NivelPermiso nivel, long? umbral = null, bool motivo = false) =>
        new() { Accion = accion, Rol = rol, Nivel = nivel, UmbralCentavos = umbral, PideMotivo = motivo };

    private static ReglaPermiso[] Ambos(AccionControlada accion, NivelPermiso nivel, bool motivo = false) =>
        [Regla(accion, Rol.Cajero, nivel, motivo: motivo), Regla(accion, Rol.Encargado, nivel, motivo: motivo)];
}
