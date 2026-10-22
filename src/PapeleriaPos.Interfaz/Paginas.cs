using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se ve bajo la barra de arriba (entrega 8, como el prototipo): la venta o una de las pantallas de atrás. Las de
/// atrás ya no son ventanas encima del ticket: son páginas, y «Venta» (F1) regresa a la venta como estaba.
/// </summary>
public enum Pagina
{
    Venta,
    Productos,
    Inventario,
    Clientes,

    /// <summary>Los reportes de ventas (entrega 9), para quien tiene «Ver reportes de ventas».</summary>
    Reportes,
}

/// <summary>
/// Las tareas del inventario, cada una con su tarjeta (htmlInventario del prototipo, lo que ya había y los reportes de
/// eleventa: los bajos en inventario, los movimientos y lo que vale).
/// </summary>
public enum TareaInventario
{
    Recibir,
    ConteoDelDia,
    Mermas,
    Ajustes,
    Paquete,
    Compras,
    Ordenes,
    Sugerido,
    Etiquetas,
    HojaCodigos,
    ConteoCompleto,
    Proveedores,
    Bajos,
    Movimientos,
    Valor,
}

/// <summary>Una página de atrás: lo que la caja le pide desde sus teclas.</summary>
public interface IPagina
{
    /// <summary>Esc: cierra lo que está abierto adentro (la ficha, una tarea, el estado de cuenta). Devuelve si cerró algo.</summary>
    Task<bool> CerrarAsync();

    /// <summary>El cursor a donde se empieza: la búsqueda, o la primera tarjeta.</summary>
    Task EnfocarAsync();
}

/// <summary>
/// Lo que puede quien entró, para pintar las páginas: lo que no puede no aparece. Con los permisos que tiene la venta (que
/// pueden llegar aparte de la sesión) o, si no, los de la sesión. Sin ninguno (sin conexión ni copia), todo se ve y el
/// servicio decide, como en la venta.
/// </summary>
public sealed class PermisosDePagina(SesionDto sesion, IReadOnlyDictionary<AccionControlada, PermisoDeSesion>? permisos)
{
    private bool SinPermisos => permisos is null && sesion.Permisos is null;

    public PermisoDeSesion? De(AccionControlada accion) => permisos is not null ? permisos.GetValueOrDefault(accion) : sesion.Permiso(accion);

    /// <summary>Lo puede hacer o lo puede pedir: se le enseña.</summary>
    public bool Ve(AccionControlada accion) => SinPermisos || De(accion) is { Nivel: not NivelPermiso.NoPermitido };

    /// <summary>Lo puede hacer sin pedir autorización.</summary>
    public bool Puede(AccionControlada accion) => SinPermisos || De(accion) is { Nivel: NivelPermiso.Permitido };

    /// <summary>Ve alguna tarea del inventario: la pestaña Inventario (F4).</summary>
    public bool VeInventario => PaginasDeAtras.TareasDe(this).Any();
}

/// <summary>Lo que se recuerda de Productos al salir de ella y volver: la búsqueda, el filtro y el elegido.</summary>
public sealed class ProductosVistos
{
    public string Texto { get; set; } = "";
    public FiltroProductos Filtro { get; set; }
    public Guid? Departamento { get; set; }
    public bool Inactivos { get; set; }
    public Guid? Elegido { get; set; }
}

/// <summary>Lo que se recuerda de Clientes al salir de ella y volver.</summary>
public sealed class ClientesVistos
{
    public string Texto { get; set; } = "";
    public FiltroClientes Filtro { get; set; }
    public bool Inactivos { get; set; }
    public Guid? Elegido { get; set; }
}

public enum FiltroClientes
{
    Todos,

    /// <summary>Solo los que deben.</summary>
    Deben,

    /// <summary>Los que venían de los clientes especiales y lo del crédito sin conexión que no cuadró.</summary>
    PorRevisar,
}

/// <summary>Un producto en la lista de etiquetas por imprimir.</summary>
public sealed class RenglonEtiqueta(Guid varianteId, string producto, string? codigo, int cantidad)
{
    public Guid VarianteId { get; } = varianteId;
    public string Producto { get; } = producto;
    public string? Codigo { get; set; } = codigo;
    public int Cantidad { get; set; } = cantidad;
}

/// <summary>
/// Las etiquetas por imprimir (Inventario › Etiquetas). Viven en la caja, no en la pantalla: «Etiqueta» en Productos
/// agrega aquí, y la lista sigue al ir y venir entre la venta y las páginas.
/// </summary>
public sealed class EtiquetasPorImprimir
{
    public const int Maximo = 2000;

    public List<RenglonEtiqueta> Renglones { get; } = [];

    public int Total => Renglones.Sum(r => r.Cantidad);

    public void Agregar(Guid varianteId, string producto, string? codigo, int cantidad)
    {
        if (Renglones.FirstOrDefault(r => r.VarianteId == varianteId) is { } existente)
            existente.Cantidad = Math.Min(Maximo, existente.Cantidad + cantidad);
        else
            Renglones.Add(new RenglonEtiqueta(varianteId, producto, codigo, Math.Min(Maximo, cantidad)));
    }
}

/// <summary>Lo que se recuerda de los reportes al salir de ellos y volver: el periodo, el día y si la gráfica va como tabla.</summary>
public sealed class ReportesVistos
{
    public PeriodoReporte Periodo { get; set; }

    /// <summary>Un día del periodo que se ve; vacío: el de hoy.</summary>
    public DateOnly? Fecha { get; set; }

    /// <summary>«Ver como tabla»: la gráfica va como tabla.</summary>
    public bool Tabla { get; set; }

    /// <summary>«Ventas por período» (entrega 9, parte 2): si se ve, y sus días, su caja y su página.</summary>
    public VentasPorPeriodoVistas PorPeriodo { get; } = new();
}

/// <summary>Lo que se recuerda de «Ventas por período» al salir de los reportes y volver.</summary>
public sealed class VentasPorPeriodoVistas
{
    /// <summary>Se ve en lugar del resumen de los reportes.</summary>
    public bool Abierta { get; set; }

    /// <summary>Los de un toque que se eligieron: al volver otro día, son los de ese día. Vacío si se escribieron las fechas.</summary>
    public DiasElegidos? Dias { get; set; } = DiasElegidos.Hoy;

    /// <summary>Las fechas que se escribieron (o a las que se llegó con ← →), si no son de un toque.</summary>
    public DateOnly? Desde { get; set; }

    public DateOnly? Hasta { get; set; }

    /// <summary>La caja; vacía: todas.</summary>
    public Guid? CajaId { get; set; }

    public int Pagina { get; set; }
}

/// <summary>Lo que la caja recuerda de sus páginas mientras alguien está adentro.</summary>
public sealed class EstadoDePaginas
{
    public ProductosVistos Productos { get; } = new();
    public ClientesVistos Clientes { get; } = new();
    public ReportesVistos Reportes { get; } = new();
    public EtiquetasPorImprimir Etiquetas { get; } = new();
}

/// <summary>Las tarjetas del inventario: cuál es de quién, y qué dice (como htmlInventario del prototipo).</summary>
public static class PaginasDeAtras
{
    /// <param name="Regla">El permiso de la tarea: sin él (en «No») la tarjeta no aparece.</param>
    /// <param name="Reglas">Otras con las que también se ve (los proveedores, para quien compra, recibe o edita el catálogo).</param>
    /// <param name="Consulta">
    /// Solo lee (los reportes como en eleventa): se ve con alguna de sus reglas en «Puede», porque consultar no se autoriza.
    /// </param>
    public sealed record Tarea(
        TareaInventario Id, string Icono, string Nombre, string Descripcion, AccionControlada? Regla, IReadOnlyList<AccionControlada>? Reglas = null,
        bool Consulta = false);

    /// <summary>
    /// En el orden del prototipo; luego lo que ya había y él no tiene (la hoja de códigos y los proveedores) y los reportes de
    /// eleventa (2026-10-17): «Bajos en inventario» junto al sugerido, y al final los movimientos y lo que vale.
    /// </summary>
    public static IReadOnlyList<Tarea> Tareas { get; } =
    [
        new(TareaInventario.Recibir, "recibir", "Recibir mercancía", "Contar a ciegas lo que llegó; la nota del proveedor se compara después.",
            AccionControlada.RecibirMercancia),
        new(TareaInventario.ConteoDelDia, "contar", "Conteo del día", "Los productos que tocan hoy. Se cuenta cuando haya calma.",
            AccionControlada.ContarInventario),
        new(TareaInventario.Mermas, "alerta", "Mermas", "Lo dañado, caducado o perdido sale del inventario.", AccionControlada.Merma),
        new(TareaInventario.Ajustes, "capas", "Ajustes", "Corregir una existencia a la que se encontró.", AccionControlada.AjustarInventario),
        new(TareaInventario.Paquete, "cajas", "Abrir un paquete", "Sale el paquete y entran sus piezas.", AccionControlada.AbrirPaquete),
        new(TareaInventario.Compras, "ticket", "Compras y notas", "XML o PDF del proveedor, costos y cierre de la compra.", AccionControlada.CostearCompra),
        new(TareaInventario.Ordenes, "lista", "Órdenes de compra", "Lo que se le pide a cada proveedor.", AccionControlada.PedirCompra),
        new(TareaInventario.Sugerido, "buscar", "Sugerido de compra", "Qué pedir según lo que se vende.", AccionControlada.PedirCompra),
        new(TareaInventario.Bajos, "abajo", "Bajos en inventario", "Lo que está en su mínimo o debajo, con cuánto pedir.", null,
            ConsultasInventario.Bajos, Consulta: true),
        new(TareaInventario.Etiquetas, "etiqueta", "Etiquetas", "Hojas de etiquetas con su código de barras para imprimir.", AccionControlada.EditarCatalogo),
        new(TareaInventario.HojaCodigos, "escaner", "Hoja de códigos", "La hoja del mostrador con lo diminuto que no lleva etiqueta, para cobrarlo escaneando.",
            AccionControlada.EditarCatalogo),
        new(TareaInventario.ConteoCompleto, "contar", "Conteo completo", "Un departamento entero, también desde el celular.", AccionControlada.ConteoCompleto),
        new(TareaInventario.Proveedores, "tienda", "Proveedores", "A quién se le compra: teléfono, RFC y notas.", null,
            [AccionControlada.RecibirMercancia, AccionControlada.CostearCompra, AccionControlada.PedirCompra, AccionControlada.EditarCatalogo]),
        new(TareaInventario.Movimientos, "transferir", "Movimientos", "Lo que entró y salió, por día, tipo, producto y quién.", null,
            ConsultasInventario.Movimientos, Consulta: true),
        new(TareaInventario.Valor, "efectivo", "Lo que vale el inventario", "A costo promedio, por departamento y por producto.", null,
            ConsultasInventario.Valor, Consulta: true),
    ];

    /// <summary>
    /// Las que tiene quien entró, en «Puede» o «Con autorización»; las de consulta, solo en «Puede». Las demás no aparecen.
    /// </summary>
    public static IEnumerable<Tarea> TareasDe(PermisosDePagina permisos) =>
        Tareas.Where(t => t.Consulta ? t.Reglas?.Any(permisos.Puede) == true
            : t.Regla is { } regla ? permisos.Ve(regla) : t.Reglas?.Any(permisos.Ve) == true);

    /// <summary>
    /// Las que mueven existencias. En el piloto con eleventa (con su lectura prendida) no se ofrecen: el inventario se lleva
    /// allá y la lectura deja la existencia en la de eleventa. Las órdenes, el sugerido, las etiquetas, la hoja de códigos,
    /// los proveedores y los reportes (bajos en inventario, movimientos y lo que vale), sí.
    /// </summary>
    public static IReadOnlySet<TareaInventario> MuevenExistencias { get; } = new HashSet<TareaInventario>
    {
        TareaInventario.Recibir, TareaInventario.ConteoDelDia, TareaInventario.Mermas, TareaInventario.Ajustes, TareaInventario.Paquete,
        TareaInventario.Compras, TareaInventario.ConteoCompleto,
    };

    public static Tarea De(TareaInventario id) => Tareas.Single(t => t.Id == id);
}
