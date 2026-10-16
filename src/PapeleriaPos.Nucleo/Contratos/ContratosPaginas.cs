using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Las pantallas de atrás con lista y detalle (entrega 8) ───────────────────
// Productos, Inventario y Clientes ya no son ventanas encima de la venta: son páginas, como en el prototipo. Lo que
// necesitan y no había: la lista de Productos que abre llena, los últimos movimientos de un producto y lo pendiente de
// cada tarea del inventario. Solo leen.

/// <summary>Los filtros de la lista de Productos, como en el prototipo.</summary>
public enum FiltroProductos
{
    Todos,

    /// <summary>Lo que un cajero dio de alta desde la caja y falta completar.</summary>
    PorCompletar,

    /// <summary>Lo que lleva inventario y tiene <see cref="PocaExistencia.Hasta"/> o menos.</summary>
    PocaExistencia,

    /// <summary>Lo que deja menos que el margen mínimo (el de su departamento o el general): solo con «Ver costos, utilidad y margen».</summary>
    MargenBajo,
}

/// <summary>
/// Cuándo queda poca existencia. El catálogo no tiene un mínimo por producto (el sugerido calcula el suyo con lo que se
/// vende, y es caro para una lista), así que es la misma cuenta con que la venta dice «Quedan 2».
/// </summary>
public static class PocaExistencia
{
    public const int Hasta = 3;
}

/// <summary>Un renglón de la lista de Productos: lo de su primera variante (las activas primero, por nombre).</summary>
/// <param name="PrecioCentavos">El de menudeo; vacío si no tiene.</param>
/// <param name="Existencia">En la sucursal; vacía en un servicio.</param>
/// <param name="MargenCentesimas">Solo para quien ve la utilidad, y si se sabe su costo (hay existencia).</param>
/// <param name="MargenMinimoCentesimas">El de su departamento o el general, junto con el margen.</param>
public sealed record ProductoEnListaDto(
    Guid ProductoId, string Nombre, string? Marca, string Departamento, int Variantes, string? Codigo, long? PrecioCentavos, decimal? Existencia,
    bool Servicio, bool Activo, bool PorCompletar, int? MargenCentesimas = null, int? MargenMinimoCentesimas = null);

/// <summary>La lista de Productos: los primeros, con cuántos hay en total.</summary>
/// <param name="Total">Cuántos hay con esa búsqueda y ese filtro.</param>
/// <param name="PorCompletar">Cuántos faltan completar en todo el catálogo (para su filtro).</param>
/// <param name="PorLoMasVendido">Van primero los más vendidos en 90 días; si no hay ventas, por nombre.</param>
public sealed record ListaProductosDto(IReadOnlyList<ProductoEnListaDto> Productos, int Total, int PorCompletar, bool PorLoMasVendido);

/// <summary>Un movimiento del kardex de un producto, como se lee en su detalle: «Venta A-000123», con lo que quedó.</summary>
/// <param name="Cantidad">Con signo, en unidad base: + entra, − sale.</param>
/// <param name="Existencia">Lo que quedó después de ese movimiento.</param>
/// <param name="Documento">El folio o lo que lo distingue («A-000123», «C-000041 · Proveedor», «del día»); vacío si no hay.</param>
public sealed record MovimientoProductoDto(DateTime FechaLocal, TipoMovimiento Tipo, decimal Cantidad, decimal Existencia, string? Documento, string? Motivo);

/// <summary>Lo pendiente de las tareas del inventario, para sus tarjetas: solo cuentas, que se leen rápido.</summary>
/// <param name="ComprasContando">Compras que alguien está recibiendo (contando a ciegas).</param>
/// <param name="ComprasSinNota">Compras contadas a las que les falta la nota del proveedor.</param>
/// <param name="OrdenesAbiertas">Órdenes de compra abiertas: armándose o pedidas.</param>
/// <param name="OrdenesEnCamino">De esas, las que ya llevan productos: lo que se espera que llegue.</param>
/// <param name="ConteosCompletosAbiertos">Departamentos que se están contando completos.</param>
public sealed record PendientesInventarioDto(int ComprasContando, int ComprasSinNota, int OrdenesAbiertas, int OrdenesEnCamino, int ConteosCompletosAbiertos);
