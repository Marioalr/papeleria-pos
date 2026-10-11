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

    /// <summary>Hacer órdenes de compra a los proveedores.</summary>
    PedirCompra,

    /// <summary>Abrir un paquete: sale el paquete y entran sus piezas (cuando son productos distintos).</summary>
    AbrirPaquete,

    /// <summary>Corregir una existencia a la que se encontró (entrega 3 de la fase 2). El umbral es el costo de la diferencia.</summary>
    AjustarInventario,

    /// <summary>Sacar del inventario lo dañado, caducado o perdido. El umbral es su costo.</summary>
    Merma,

    /// <summary>
    /// Contar a ciegas: los productos del conteo cíclico del día (la diferencia entra al inventario) y lo que se captura
    /// en el conteo completo de un departamento.
    /// </summary>
    ContarInventario,

    /// <summary>Abrir, cancelar y cerrar el conteo completo de un departamento: al cerrarlo, lo contado reemplaza la existencia.</summary>
    ConteoCompleto,

    /// <summary>Darle crédito a un cliente registrado: su límite y su plazo (fase 3, entrega 2).</summary>
    DarCredito,

    /// <summary>Fiar arriba del límite del cliente. El monto que se revisa es lo que pasa del límite.</summary>
    ExcederLimiteCredito,

    /// <summary>Ajustar el saldo de un cliente (el saldo inicial de la libreta, una corrección). El monto es el del ajuste.</summary>
    AjustarCredito,

    /// <summary>Cancelar un abono, solo en el turno en que se recibió. El monto es el del abono.</summary>
    CancelarAbono,

    /// <summary>Devolverle en efectivo a un cliente lo que tiene a favor (parte 3). El monto es lo que se devuelve.</summary>
    DevolverSaldoAFavor,
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

/// <summary>
/// Lo que puede hacer quien entró, para que la caja pinte sus pestañas, teclas y candados sin preguntar en cada clic. El
/// servicio vuelve a revisar cada operación.
/// </summary>
/// <param name="UmbralCentavos">Hasta dónde puede sin autorización (en las de porcentaje, centésimas de porcentaje).</param>
/// <param name="Autorizan">Quién lo autoriza: lo que pide autorización, o lo que pasa del umbral.</param>
public sealed record PermisoDeSesion(AccionControlada Accion, NivelPermiso Nivel, long? UmbralCentavos, bool PideMotivo, IReadOnlyList<QuienAutoriza> Autorizan);

/// <summary>Quien puede autorizar: su nombre, para decirlo, y su usuario, para pedirle solo el PIN.</summary>
public sealed record QuienAutoriza(string Usuario, string Nombre);

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
    ];

    /// <summary>
    /// La regla de la sucursal gana sobre la general, y la general sobre la inicial. Sin ninguna, se pide autorización.
    /// Editar las reglas y dar de alta cajas es siempre solo del dueño.
    /// </summary>
    /// <summary>Acciones que no se pueden dar a otro rol: no aparecen en el editor de reglas.</summary>
    public static bool SoloDelDueno(AccionControlada accion) =>
        accion is AccionControlada.EditarReglas or AccionControlada.AdministrarCajas or AccionControlada.ConfigurarTelegram
            or AccionControlada.ConfigurarImpresora or AccionControlada.ConfigurarMargen;

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

        if (Efectiva(reglas, rol, sucursalId, accion) is not { } regla)
            return new DecisionPermiso(NivelPermiso.RequiereAutorizacion, false);

        var nivel = regla.Nivel == NivelPermiso.Permitido && regla.UmbralCentavos is { } umbral && montoCentavos > umbral
            ? NivelPermiso.RequiereAutorizacion
            : regla.Nivel;
        return new DecisionPermiso(nivel, regla.PideMotivo);
    }

    /// <summary>
    /// Todas las acciones para quien entró, cada una con quién la autoriza: los demás usuarios activos que la tienen
    /// permitida (por encima del umbral, si lo hay), primero los encargados y al final el dueño.
    /// </summary>
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

    /// <summary>La regla que vale: la de la sucursal gana sobre la general, y la general sobre la inicial.</summary>
    private static ReglaPermiso? Efectiva(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion)
    {
        var candidatas = reglas.Where(r => r.Accion == accion && r.Rol == rol).ToList();
        return candidatas.FirstOrDefault(r => r.SucursalId == sucursalId)
            ?? candidatas.FirstOrDefault(r => r.SucursalId is null)
            ?? Iniciales.FirstOrDefault(r => r.Accion == accion && r.Rol == rol);
    }

    /// <summary>Puede autorizar quien tiene la acción permitida por ese monto (el encargado cancela; cambiar precio, solo el dueño).</summary>
    public static bool PuedeAutorizar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos) =>
        Evaluar(reglas, rol, sucursalId, accion, montoCentavos).Nivel == NivelPermiso.Permitido;

    private static ReglaPermiso Regla(AccionControlada accion, Rol rol, NivelPermiso nivel, long? umbral = null, bool motivo = false) =>
        new() { Accion = accion, Rol = rol, Nivel = nivel, UmbralCentavos = umbral, PideMotivo = motivo };

    private static ReglaPermiso[] Ambos(AccionControlada accion, NivelPermiso nivel, bool motivo = false) =>
        [Regla(accion, Rol.Cajero, nivel, motivo: motivo), Regla(accion, Rol.Encargado, nivel, motivo: motivo)];
}
