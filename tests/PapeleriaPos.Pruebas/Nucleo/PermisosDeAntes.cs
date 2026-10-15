using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Cómo se decidía antes de la entrega 1, con los roles y las reglas por rol: copia sin cambios de Permisos y FichasEntrada
/// de main (e271860), solo para comparar con los permisos por usuario (la paridad de la migración). Ya nada del POS decide
/// así. Las reglas iniciales y lo que era solo del dueño son los mismos que lee la migración (<see cref="PermisosDeRoles"/>).
/// </summary>
internal static class PermisosDeAntes
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static DecisionPermiso Evaluar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos = null)
    {
        if (rol == Rol.Dueno)
            return new DecisionPermiso(NivelPermiso.Permitido, false);
        if (PermisosDeRoles.SoloDelDueno(accion))
            return new DecisionPermiso(NivelPermiso.NoPermitido, false);

        if (Efectiva(reglas, rol, sucursalId, accion) is not { } regla)
            return new DecisionPermiso(NivelPermiso.RequiereAutorizacion, false);

        var nivel = regla.Nivel == NivelPermiso.Permitido && regla.UmbralCentavos is { } umbral && montoCentavos > umbral
            ? NivelPermiso.RequiereAutorizacion
            : regla.Nivel;
        return new DecisionPermiso(nivel, regla.PideMotivo);
    }

    public static IReadOnlyList<PermisoDeSesion> DeLaSesion(
        IEnumerable<ReglaPermiso> reglas, IEnumerable<(Guid Id, string Usuario, string Nombre, Rol Rol)> usuarios, Guid usuarioId, Rol rol, Guid sucursalId)
    {
        var lista = reglas.ToList();
        var otros = usuarios.Where(u => u.Id != usuarioId).OrderBy(u => u.Rol).ThenBy(u => u.Nombre, StringComparer.CurrentCulture).ToList();
        return Enum.GetValues<AccionControlada>().Select(accion =>
        {
            var decision = Evaluar(lista, rol, sucursalId, accion);
            var umbral = decision.Nivel == NivelPermiso.Permitido && rol != Rol.Dueno ? Efectiva(lista, rol, sucursalId, accion)?.UmbralCentavos : null;
            long? monto = decision.Nivel == NivelPermiso.RequiereAutorizacion ? null : umbral + 1;
            var autorizan = decision.Nivel == NivelPermiso.RequiereAutorizacion || umbral is not null
                ? otros.Where(u => PuedeAutorizar(lista, u.Rol, sucursalId, accion, monto)).Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList()
                : [];
            return new PermisoDeSesion(accion, decision.Nivel, umbral, decision.PideMotivo, autorizan);
        }).ToList();
    }

    private static ReglaPermiso? Efectiva(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion)
    {
        var candidatas = reglas.Where(r => r.Accion == accion && r.Rol == rol).ToList();
        return candidatas.FirstOrDefault(r => r.SucursalId == sucursalId)
            ?? candidatas.FirstOrDefault(r => r.SucursalId is null)
            ?? PermisosDeRoles.Iniciales.FirstOrDefault(r => r.Accion == accion && r.Rol == rol);
    }

    public static List<QuienAutoriza> QuienesPueden(
        IEnumerable<ReglaPermiso> reglas, IEnumerable<(Guid Id, string Usuario, string Nombre, Rol Rol)> usuarios, Guid sucursalId, AccionControlada accion)
    {
        var lista = reglas.ToList();
        return usuarios.Where(u => Evaluar(lista, u.Rol, sucursalId, accion).Nivel == NivelPermiso.Permitido)
            .OrderBy(u => u.Rol).ThenBy(u => u.Nombre, StringComparer.CurrentCulture)
            .Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList();
    }

    public static bool PuedeAutorizar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos) =>
        Evaluar(reglas, rol, sucursalId, accion, montoCentavos).Nivel == NivelPermiso.Permitido;

    // ── Las fichas de la entrada (FichasEntrada) ─────────────────────────────

    private static readonly (AccionControlada Accion, string Texto)[] Notables =
    [
        (AccionControlada.CorteZ, "corte de turno"),
        (AccionControlada.EditarCatalogo, "productos"),
        (AccionControlada.AdministrarClientes, "clientes"),
        (AccionControlada.CostearCompra, "compras"),
        (AccionControlada.ConteoCompleto, "conteo completo"),
        (AccionControlada.ConsultarBitacora, "bitácora"),
    ];

    public static List<FichaUsuarioDto> Fichas(
        IEnumerable<(Guid Id, string Nombre, Rol Rol)> usuarios, IReadOnlyList<ReglaPermiso> reglas, Guid sucursalId, Guid? cajeroDelTurno)
    {
        var lista = usuarios.ToList();
        var teclas = FichasEntrada.Teclas(lista.Select(u => (u.Id, u.Nombre)));
        return lista
            .OrderBy(u => u.Nombre.Trim(), StringComparer.Create(Mexico, ignoreCase: true))
            .ThenBy(u => u.Id)
            .Select(u => new FichaUsuarioDto(u.Id, u.Nombre.Trim(), teclas.GetValueOrDefault(u.Id), Resumen(reglas, u.Rol, sucursalId),
                u.Id == cajeroDelTurno, HaceCorte(reglas, u.Rol, sucursalId)))
            .ToList();
    }

    public static string Resumen(IReadOnlyList<ReglaPermiso> reglas, Rol rol, Guid sucursalId)
    {
        var acciones = Enum.GetValues<AccionControlada>();
        if (acciones.All(a => Evaluar(reglas, rol, sucursalId, a, long.MaxValue).Nivel == NivelPermiso.Permitido))
            return "Todos los permisos";
        if (acciones.All(a => Nivel(reglas, rol, sucursalId, a) == Nivel(reglas, Rol.Cajero, sucursalId, a)))
            return "Permisos por defecto";
        var demas = Notables
            .Where(n => Evaluar(reglas, rol, sucursalId, n.Accion).Nivel == NivelPermiso.Permitido
                && Evaluar(reglas, Rol.Cajero, sucursalId, n.Accion).Nivel != NivelPermiso.Permitido)
            .Select(n => n.Texto)
            .Take(3)
            .ToList();
        if (demas.Count == 0)
            return "Permisos propios";
        var texto = demas.Count == 1 ? demas[0] : $"{string.Join(", ", demas.Take(demas.Count - 1))} y {demas[^1]}";
        return char.ToUpper(texto[0], Mexico) + texto[1..];
    }

    private static (NivelPermiso, NivelPermiso) Nivel(IReadOnlyList<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion) =>
        (Evaluar(reglas, rol, sucursalId, accion).Nivel, Evaluar(reglas, rol, sucursalId, accion, long.MaxValue).Nivel);

    public static bool HaceCorte(IReadOnlyList<ReglaPermiso> reglas, Rol rol, Guid sucursalId) =>
        Evaluar(reglas, rol, sucursalId, AccionControlada.CorteZ).Nivel == NivelPermiso.Permitido;
}
