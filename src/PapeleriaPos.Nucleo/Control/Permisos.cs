using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Nucleo.Control;

/// <summary>
/// Lo que se controla: cada usuario tiene un permiso por acción (entrega 1, docs/propuesta-interfaz.md «Usuarios y
/// permisos»). En la base se guardan por su nombre (la bitácora, las autorizaciones, los permisos) y entre la caja y el
/// servicio viajan por su número (el JSON y la bandeja de la caja sin conexión): nunca se reordenan ni se renombran, y las
/// nuevas van al final.
/// </summary>
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

    /// <summary>No lo usa nada: no hay cajón (2026-10-09). No sale en «Usuarios y permisos».</summary>
    AbrirCajonSinVenta,
    Reimprimir,
    VerificarTransferencia,
    CorteZ,
    ConfigurarTicket,

    /// <summary>
    /// «Usuarios y permisos» (antes, editar las reglas de cada rol): dar de alta usuarios y cambiarles los permisos y el PIN.
    /// Se llama como antes porque la bitácora lo guarda por su nombre. Siempre queda alguien que lo tiene en «Puede».
    /// </summary>
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

    /// <summary>
    /// Ver y cerrar el corte del día: todas las cajas y turnos de la sucursal (docs/propuesta-interfaz.md, «Corte»,
    /// 2026-10-14). Al migrar, solo el dueño; se le puede dar a otro en «Usuarios y permisos».
    /// </summary>
    CorteDelDia,

    /// <summary>
    /// Ver costos, utilidad y margen (entrega 1): el costo de las impresiones, la utilidad y el margen. El costo de lo que se
    /// cuenta y de los productos lo ve también quien captura las compras («Compras: la nota y los costos»), como en el
    /// prototipo. Al migrar, solo el dueño (2026-10-09: la utilidad solo la ve el dueño).
    /// </summary>
    VerUtilidad,

    /// <summary>Ver los reportes de ventas (entrega 9: todavía no hay pantalla). Al migrar, solo el dueño.</summary>
    VerReportes,

    /// <summary>La configuración de la tienda: hoy, el límite de efectivo en caja. Al migrar, solo el dueño.</summary>
    ConfigurarTienda,
}

/// <summary>En pantalla: «Puede», «Con autorización» (con candado y quién autoriza) y «No» (no aparece).</summary>
public enum NivelPermiso
{
    Permitido,
    RequiereAutorizacion,
    NoPermitido,
}

/// <summary>
/// Lo que puede alguien en una acción. En «Puede», con umbral: arriba de ese monto pide autorización (en las de
/// porcentaje, el umbral va en centésimas: 1000 = 10 %). El umbral solo se guarda en «Puede».
/// </summary>
public sealed record Permiso(AccionControlada Accion, NivelPermiso Nivel, long? UmbralCentavos = null, bool PideMotivo = false)
{
    /// <summary>«Puede», sin límite: como los tiene la primera cuenta.</summary>
    public bool SinLimite => Nivel == NivelPermiso.Permitido && UmbralCentavos is null;

    /// <summary>Lo que se ve en «Usuarios y permisos»: el nivel y el límite (el motivo no se ve ahí).</summary>
    public bool SeVeIgual(Permiso otro) => Nivel == otro.Nivel && UmbralCentavos == otro.UmbralCentavos;

    /// <summary>Todo «Puede», sin límite y sin pedir motivo: los de la primera cuenta y los de «Todos».</summary>
    public static Permiso Todo(AccionControlada accion) => new(accion, NivelPermiso.Permitido);
}

/// <summary>Los permisos de un usuario (o los de defecto), uno por acción. La acción que no tiene, no la puede hacer.</summary>
public sealed class PermisosDeUsuario
{
    private readonly Dictionary<AccionControlada, Permiso> _porAccion = [];

    public PermisosDeUsuario(IEnumerable<Permiso> permisos)
    {
        foreach (var permiso in permisos)
            _porAccion[permiso.Accion] = permiso;
    }

    public Permiso De(AccionControlada accion) => _porAccion.TryGetValue(accion, out var p) ? p : new Permiso(accion, NivelPermiso.NoPermitido);

    /// <summary>Todas las acciones, en su orden.</summary>
    public IReadOnlyList<Permiso> Lista() => Permisos.Acciones.Select(De).ToList();

    /// <summary>Cuántas puede hacer: quien autoriza va de quien puede menos a quien puede todo.</summary>
    public int CuantasPuede => Permisos.Acciones.Count(a => De(a).Nivel == NivelPermiso.Permitido);

    /// <summary>«Todos los permisos»: todo lo de «Usuarios y permisos» en «Puede», sin límite.</summary>
    public bool TieneTodos => Permisos.EnPantalla.All(a => De(a).SinLimite);
}

/// <summary>Un usuario con sus permisos: para decir quién autoriza y quién puede, por su nombre y su usuario.</summary>
public sealed record UsuarioConPermisos(Guid Id, string Usuario, string Nombre, PermisosDeUsuario Permisos);

/// <summary>
/// Lo que puede un usuario en una acción, guardado (entrega 1). Cada usuario tiene uno por acción, y los de defecto (sin
/// usuario) son con los que empieza cada usuario nuevo: cambiarlos no toca a los que ya existen.
/// </summary>
public class PermisoUsuario : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Vacío: los permisos por defecto.</summary>
    public Guid? UsuarioId { get; set; }

    public AccionControlada Accion { get; set; }
    public NivelPermiso Nivel { get; set; }
    public long? UmbralCentavos { get; set; }
    public bool PideMotivo { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    public Permiso Permiso => new(Accion, Nivel, Nivel == NivelPermiso.Permitido ? UmbralCentavos : null, PideMotivo);

    /// <summary>Lo pone como <paramref name="permiso"/>; dice si cambió algo.</summary>
    public bool Poner(Permiso permiso)
    {
        var umbral = permiso.Nivel == NivelPermiso.Permitido ? permiso.UmbralCentavos : null;
        if (Nivel == permiso.Nivel && UmbralCentavos == umbral && PideMotivo == permiso.PideMotivo)
            return false;
        (Nivel, UmbralCentavos, PideMotivo) = (permiso.Nivel, umbral, permiso.PideMotivo);
        return true;
    }
}

/// <summary>
/// De antes de la entrega 1: qué podía hacer un rol en una acción, en una sucursal (vacía: todas). Ya nada decide con
/// esto: solo lo lee la migración a los permisos por usuario (<see cref="PermisosDeRoles"/>), una vez, y la tabla se
/// queda como estaba.
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

/// <summary>Un grupo de «Usuarios y permisos» (Venta, Efectivo y corte…) con sus acciones en orden.</summary>
public sealed record GrupoDePermisos(string Nombre, IReadOnlyList<AccionControlada> Acciones);

public static class Permisos
{
    // ── Los permisos por usuario (entrega 1) ────────────────────────────────

    public static IReadOnlyList<AccionControlada> Acciones { get; } = Enum.GetValues<AccionControlada>();

    /// <summary>
    /// «Usuarios y permisos», como en el prototipo (docs/propuesta-interfaz.md): los grupos con sus acciones, en orden. Abrir
    /// el cajón sin venta no sale: no hay cajón y nada lo usa.
    /// </summary>
    public static IReadOnlyList<GrupoDePermisos> Grupos { get; } =
    [
        new("Venta",
        [
            AccionControlada.QuitarProducto, AccionControlada.ArticuloComun, AccionControlada.Descuento, AccionControlada.CambiarPrecio,
            AccionControlada.VenderBajoCosto, AccionControlada.Devolucion, AccionControlada.CancelarTicket, AccionControlada.Reimprimir,
            AccionControlada.AltaRapida,
        ]),
        new("Efectivo y corte",
        [
            AccionControlada.EntradaEfectivo, AccionControlada.SalidaEfectivo, AccionControlada.VerificarTransferencia, AccionControlada.CorteZ,
            AccionControlada.CorteDelDia,
        ]),
        new("Inventario",
        [
            AccionControlada.RecibirMercancia, AccionControlada.ContarInventario, AccionControlada.Merma, AccionControlada.AjustarInventario,
            AccionControlada.AbrirPaquete, AccionControlada.EditarCatalogo, AccionControlada.CostearCompra, AccionControlada.PedirCompra,
            AccionControlada.ConteoCompleto, AccionControlada.ConfigurarMargen,
        ]),
        new("Clientes y crédito",
        [
            AccionControlada.AdministrarClientes, AccionControlada.ExcederLimiteCredito, AccionControlada.CancelarAbono, AccionControlada.DarCredito,
            AccionControlada.AjustarCredito, AccionControlada.DevolverSaldoAFavor,
        ]),
        new("Reportes", [AccionControlada.VerReportes, AccionControlada.VerUtilidad]),
        new("Administración",
        [
            AccionControlada.ConfigurarTienda, AccionControlada.ConfigurarTicket, AccionControlada.ConfigurarImpresora,
            AccionControlada.ConfigurarTelegram, AccionControlada.EditarReglas, AccionControlada.AdministrarCajas, AccionControlada.ConsultarBitacora,
        ]),
    ];

    /// <summary>Las acciones de «Usuarios y permisos», en su orden.</summary>
    public static IReadOnlyList<AccionControlada> EnPantalla { get; } = [.. Grupos.SelectMany(g => g.Acciones)];

    /// <summary>Cómo se llama cada permiso en pantalla, en la bitácora y en los mensajes (los del prototipo).</summary>
    public static string Nombre(AccionControlada accion) => accion switch
    {
        AccionControlada.QuitarProducto => "Quitar un producto ya escaneado",
        AccionControlada.ArticuloComun => "Artículo común (sin inventario)",
        AccionControlada.Descuento => "Descuento",
        AccionControlada.CambiarPrecio => "Cambiar un precio en la venta",
        AccionControlada.VenderBajoCosto => "Vender debajo del costo",
        AccionControlada.Devolucion => "Devolución",
        AccionControlada.CancelarTicket => "Cancelar un ticket cobrado",
        AccionControlada.Reimprimir => "Reimprimir un ticket",
        AccionControlada.AltaRapida => "Dar de alta un código desde la caja",
        AccionControlada.EntradaEfectivo => "Entrada de efectivo",
        AccionControlada.SalidaEfectivo => "Salida de efectivo",
        AccionControlada.AbrirCajonSinVenta => "Abrir el cajón sin venta",
        AccionControlada.VerificarTransferencia => "Verificar transferencias",
        AccionControlada.CorteZ => "Hacer el corte de un turno",
        AccionControlada.CorteDelDia => "Ver y cerrar el corte del día",
        AccionControlada.RecibirMercancia => "Recibir mercancía a ciegas",
        AccionControlada.ContarInventario => "Contar inventario",
        AccionControlada.Merma => "Merma",
        AccionControlada.AjustarInventario => "Ajustar una existencia",
        AccionControlada.AbrirPaquete => "Abrir un paquete",
        AccionControlada.EditarCatalogo => "Productos, precios y etiquetas",
        AccionControlada.CostearCompra => "Compras: la nota y los costos",
        AccionControlada.PedirCompra => "Órdenes y sugerido de compra",
        AccionControlada.ConteoCompleto => "Conteo completo de un departamento",
        AccionControlada.ConfigurarMargen => "Margen mínimo",
        AccionControlada.AdministrarClientes => "Dar de alta y editar clientes",
        AccionControlada.ExcederLimiteCredito => "Fiar arriba del límite",
        AccionControlada.CancelarAbono => "Cancelar un abono",
        AccionControlada.DarCredito => "Dar crédito: límite y plazo",
        AccionControlada.AjustarCredito => "Ajustar el saldo de un cliente",
        AccionControlada.DevolverSaldoAFavor => "Devolver en efectivo un saldo a favor",
        AccionControlada.VerReportes => "Ver reportes de ventas",
        AccionControlada.VerUtilidad => "Ver costos, utilidad y margen",
        AccionControlada.ConfigurarTienda => "Configuración de la tienda",
        AccionControlada.ConfigurarTicket => "Configurar el ticket",
        AccionControlada.ConfigurarImpresora => "Configurar la impresora",
        AccionControlada.ConfigurarTelegram => "Configurar Telegram",
        AccionControlada.EditarReglas => "Usuarios y permisos",
        AccionControlada.AdministrarCajas => "Dar de alta y quitar cajas",
        AccionControlada.ConsultarBitacora => "Consultar la bitácora",
        _ => accion.ToString(),
    };

    /// <summary>
    /// Las que en «Puede» llevan el campo «hasta $…» (o «hasta …%»), como en el prototipo: las que tienen límite en los de
    /// defecto. Un límite que ya tenga otra (de las reglas de antes) también se ve y se cambia.
    /// </summary>
    public static bool LlevaLimite(AccionControlada accion) =>
        accion is AccionControlada.Descuento or AccionControlada.Devolucion or AccionControlada.Merma;

    /// <summary>Con umbral, arriba de ese monto lo que puede pasa a pedir autorización.</summary>
    public static DecisionPermiso Evaluar(Permiso permiso, long? montoCentavos = null)
    {
        var nivel = permiso.Nivel == NivelPermiso.Permitido && permiso.UmbralCentavos is { } umbral && montoCentavos > umbral
            ? NivelPermiso.RequiereAutorizacion
            : permiso.Nivel;
        return new DecisionPermiso(nivel, permiso.PideMotivo);
    }

    /// <summary>Lo que dicen los permisos del usuario, y nada más: nadie puede todo por ser quien es.</summary>
    public static DecisionPermiso Evaluar(PermisosDeUsuario permisos, AccionControlada accion, long? montoCentavos = null) =>
        Evaluar(permisos.De(accion), montoCentavos);

    /// <summary>Puede autorizar quien puede hacerlo por ese monto.</summary>
    public static bool PuedeAutorizar(PermisosDeUsuario permisos, AccionControlada accion, long? montoCentavos) =>
        Evaluar(permisos, accion, montoCentavos).Nivel == NivelPermiso.Permitido;

    /// <summary>
    /// Todas las acciones para quien entró, cada una con quién la autoriza: los demás <paramref name="usuarios"/> (los
    /// activos que entran en esa caja) que la pueden hacer por ese monto, en el orden de <see cref="EnOrden"/>.
    /// </summary>
    public static IReadOnlyList<PermisoDeSesion> DeLaSesion(PermisosDeUsuario propios, IEnumerable<UsuarioConPermisos> usuarios, Guid usuarioId)
    {
        var otros = EnOrden(usuarios.Where(u => u.Id != usuarioId)).ToList();
        return Acciones.Select(accion =>
        {
            var permiso = propios.De(accion);
            var umbral = permiso.Nivel == NivelPermiso.Permitido ? permiso.UmbralCentavos : null;
            long? monto = permiso.Nivel == NivelPermiso.RequiereAutorizacion ? null : umbral + 1;
            var autorizan = permiso.Nivel == NivelPermiso.RequiereAutorizacion || umbral is not null
                ? otros.Where(u => PuedeAutorizar(u.Permisos, accion, monto)).Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList()
                : [];
            return new PermisoDeSesion(accion, permiso.Nivel, umbral, permiso.PideMotivo, autorizan);
        }).ToList();
    }

    /// <summary>Los usuarios que pueden hacer una acción sin pedir autorización (el corte del turno), en el orden de <see cref="EnOrden"/>.</summary>
    public static List<QuienAutoriza> QuienesPueden(IEnumerable<UsuarioConPermisos> usuarios, AccionControlada accion) =>
        EnOrden(usuarios.Where(u => Evaluar(u.Permisos, accion).Nivel == NivelPermiso.Permitido))
            .Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList();

    /// <summary>
    /// Quien autoriza se dice de quien puede menos a quien puede todo, y luego por nombre: primero quien está en el piso
    /// (antes, el encargado) y al final la primera cuenta (antes, el dueño), como lo decían los roles.
    /// </summary>
    public static IEnumerable<UsuarioConPermisos> EnOrden(IEnumerable<UsuarioConPermisos> usuarios) =>
        usuarios.OrderBy(u => u.Permisos.CuantasPuede).ThenBy(u => u.Nombre, StringComparer.CurrentCulture).ThenBy(u => u.Id);

    // ── Lo de antes: reglas por rol ─────────────────────────────────────────

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
        // El corte del día (2026-10-14): la propuesta lo deja a la primera cuenta, que hoy es el dueño.
        .. Ambos(AccionControlada.CorteDelDia, NivelPermiso.NoPermitido),
    ];

    /// <summary>
    /// La regla de la sucursal gana sobre la general, y la general sobre la inicial. Sin ninguna, se pide autorización.
    /// Editar las reglas y dar de alta cajas es siempre solo del dueño.
    /// </summary>
    /// <summary>Acciones que no se pueden dar a otro rol: no aparecen en el editor de reglas.</summary>
    public static bool SoloDelDueno(AccionControlada accion) =>
        accion is AccionControlada.EditarReglas or AccionControlada.AdministrarCajas or AccionControlada.ConfigurarTelegram
            or AccionControlada.ConfigurarImpresora or AccionControlada.ConfigurarMargen
            // Las de la entrega 1: con los roles, eran solo del dueño (el costo de las impresiones, el límite de efectivo).
            or AccionControlada.VerUtilidad or AccionControlada.VerReportes or AccionControlada.ConfigurarTienda;

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

    /// <summary>
    /// Los usuarios activos que pueden hacer una acción sin pedir autorización, primero los encargados y al final el dueño
    /// (como en <see cref="DeLaSesion"/>): para decirle a quien no puede quién sí (el corte del turno).
    /// </summary>
    public static List<QuienAutoriza> QuienesPueden(
        IEnumerable<ReglaPermiso> reglas, IEnumerable<(Guid Id, string Usuario, string Nombre, Rol Rol)> usuarios, Guid sucursalId, AccionControlada accion)
    {
        var lista = reglas.ToList();
        return usuarios.Where(u => Evaluar(lista, u.Rol, sucursalId, accion).Nivel == NivelPermiso.Permitido)
            .OrderBy(u => u.Rol).ThenBy(u => u.Nombre, StringComparer.CurrentCulture)
            .Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList();
    }

    /// <summary>«Luis», «Carmen o Luis», «Ana, Carmen o Luis»: para los mensajes del servicio.</summary>
    public static string Nombres(IReadOnlyList<QuienAutoriza> quienes) => quienes.Count switch
    {
        0 => "quien tenga el permiso",
        1 => quienes[0].Nombre,
        _ => $"{string.Join(", ", quienes.Take(quienes.Count - 1).Select(q => q.Nombre))} o {quienes[^1].Nombre}",
    };

    /// <summary>Puede autorizar quien tiene la acción permitida por ese monto (el encargado cancela; cambiar precio, solo el dueño).</summary>
    public static bool PuedeAutorizar(IEnumerable<ReglaPermiso> reglas, Rol rol, Guid sucursalId, AccionControlada accion, long? montoCentavos) =>
        Evaluar(reglas, rol, sucursalId, accion, montoCentavos).Nivel == NivelPermiso.Permitido;

    private static ReglaPermiso Regla(AccionControlada accion, Rol rol, NivelPermiso nivel, long? umbral = null, bool motivo = false) =>
        new() { Accion = accion, Rol = rol, Nivel = nivel, UmbralCentavos = umbral, PideMotivo = motivo };

    private static ReglaPermiso[] Ambos(AccionControlada accion, NivelPermiso nivel, bool motivo = false) =>
        [Regla(accion, Rol.Cajero, nivel, motivo: motivo), Regla(accion, Rol.Encargado, nivel, motivo: motivo)];
}
