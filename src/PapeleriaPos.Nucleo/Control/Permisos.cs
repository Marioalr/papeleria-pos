using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Nucleo.Control;

/// <summary>Lo que se controla con reglas. Los descuentos llegan con el módulo de impresión (entrega 5).</summary>
public enum AccionControlada
{
    QuitarProducto,
    ArticuloComun,
    CambiarPrecio,
    VenderBajoCosto,
    CancelarTicket,
    Devolucion,
    SalidaEfectivo,
    EntradaEfectivo,
    AbrirCajonSinVenta,
    Reimprimir,
    VerificarTransferencia,
    CorteZ,
    ConfigurarTicket,
    EditarReglas,
    ConsultarBitacora,
    AdministrarCajas,
    ConfigurarTelegram,
    ConfigurarImpresora,

    /// <summary>Descuento en una línea o en todo el ticket. Su umbral es en centésimas de porcentaje (1000 = 10 %).</summary>
    Descuento,
    AdministrarClientes,

    /// <summary>Dar de alta y editar productos, códigos, presentaciones y precios del catálogo (F3).</summary>
    EditarCatalogo,

    /// <summary>Dar de alta desde la caja un código escaneado que no existe (queda por completar).</summary>
    AltaRapida,

    /// <summary>Contar a ciegas la mercancía que llegó (F4, entrega 2 de la fase 2).</summary>
    RecibirMercancia,

    /// <summary>Capturar la nota del proveedor, ver los costos y cerrar la compra (entra al inventario).</summary>
    CostearCompra,

    /// <summary>El margen mínimo general y por departamento.</summary>
    ConfigurarMargen,
}

public enum NivelPermiso
{
    Permitido,
    RequiereAutorizacion,
    NoPermitido,
}

/// <summary>
/// Qué puede hacer un rol en una acción, en una sucursal (vacía: todas). Con umbral, arriba de ese monto pasa de
/// permitido a requerir autorización (devoluciones de más de $200).
/// </summary>
public class ReglaPermiso : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public AccionControlada Accion { get; set; }
    public Rol Rol { get; set; }
    public Guid? SucursalId { get; set; }
    public NivelPermiso Nivel { get; set; }
    public long? UmbralCentavos { get; set; }
    public bool PideMotivo { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}

public sealed record DecisionPermiso(NivelPermiso Nivel, bool PideMotivo);

public static class Permisos
{
    /// <summary>Las reglas con que arranca el sistema (docs/fase-1-control.md). El dueño siempre puede todo.</summary>
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
        .. Ambos(AccionControlada.AdministrarClientes, NivelPermiso.NoPermitido),
        Regla(AccionControlada.Descuento, Rol.Cajero, NivelPermiso.Permitido, umbral: 1000),
        Regla(AccionControlada.Descuento, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.EditarCatalogo, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.EditarCatalogo, Rol.Encargado, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.AltaRapida, NivelPermiso.Permitido),
        .. Ambos(AccionControlada.RecibirMercancia, NivelPermiso.Permitido),
        Regla(AccionControlada.CostearCompra, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.CostearCompra, Rol.Encargado, NivelPermiso.Permitido),
        Regla(AccionControlada.ConsultarBitacora, Rol.Cajero, NivelPermiso.NoPermitido),
        Regla(AccionControlada.ConsultarBitacora, Rol.Encargado, NivelPermiso.Permitido),
    ];

    /// <summary>
    /// La regla de la sucursal gana sobre la general, y la general sobre la inicial. Sin ninguna, se pide autorización.
    /// Editar las reglas y dar de alta cajas es siempre solo del dueño.
    /// </summary>
    /// <summary>Acciones que no se pueden dar a otro rol: no aparecen en el editor de reglas.</summary>
    public static bool SoloDelDueno(AccionControlada accion) =>
        accion is AccionControlada.EditarReglas or AccionControlada.AdministrarCajas or AccionControlada.ConfigurarTelegram
            or AccionControlada.ConfigurarImpresora or AccionControlada.AdministrarClientes or AccionControlada.ConfigurarMargen;

    /// <summary>Acciones cuyo umbral (y el monto que se revisa) es un porcentaje en centésimas, no dinero.</summary>
    public static bool EnPorcentaje(AccionControlada accion) => accion == AccionControlada.Descuento;

    /// <summary>El monto de una acción como se lee: "$200.00", o "12.5 %" en las de porcentaje.</summary>
    public static string TextoMonto(AccionControlada accion, long monto) =>
        EnPorcentaje(accion)
            ? $"{(monto / 100m).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))} %"
            : (monto / 100m).ToString("$#,##0.00", System.Globalization.CultureInfo.GetCultureInfo("es-MX"));

    /// <summary>Lo que va como monto en la bitácora: dinero; en las de porcentaje, nada (el porcentaje va en el detalle).</summary>
    public static long? MontoParaBitacora(AccionControlada accion, long? monto) => EnPorcentaje(accion) ? null : monto;

    public static DecisionPermiso Evaluar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos = null)
    {
        if (rol == Rol.Dueno)
            return new DecisionPermiso(NivelPermiso.Permitido, false);
        if (SoloDelDueno(accion))
            return new DecisionPermiso(NivelPermiso.NoPermitido, false);

        var candidatas = reglas.Where(r => r.Accion == accion && r.Rol == rol).ToList();
        var regla = candidatas.FirstOrDefault(r => r.SucursalId == sucursalId)
            ?? candidatas.FirstOrDefault(r => r.SucursalId is null)
            ?? Iniciales.FirstOrDefault(r => r.Accion == accion && r.Rol == rol);
        if (regla is null)
            return new DecisionPermiso(NivelPermiso.RequiereAutorizacion, false);

        var nivel = regla.Nivel == NivelPermiso.Permitido && regla.UmbralCentavos is { } umbral && montoCentavos > umbral
            ? NivelPermiso.RequiereAutorizacion
            : regla.Nivel;
        return new DecisionPermiso(nivel, regla.PideMotivo);
    }

    /// <summary>Puede autorizar quien tiene la acción permitida por ese monto (el encargado cancela; cambiar precio, solo el dueño).</summary>
    public static bool PuedeAutorizar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos) =>
        Evaluar(reglas, rol, sucursalId, accion, montoCentavos).Nivel == NivelPermiso.Permitido;

    private static ReglaPermiso Regla(AccionControlada accion, Rol rol, NivelPermiso nivel, long? umbral = null, bool motivo = false) =>
        new() { Accion = accion, Rol = rol, Nivel = nivel, UmbralCentavos = umbral, PideMotivo = motivo };

    private static ReglaPermiso[] Ambos(AccionControlada accion, NivelPermiso nivel, bool motivo = false) =>
        [Regla(accion, Rol.Cajero, nivel, motivo: motivo), Regla(accion, Rol.Encargado, nivel, motivo: motivo)];
}
