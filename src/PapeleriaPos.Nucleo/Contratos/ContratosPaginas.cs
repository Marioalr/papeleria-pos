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

    /// <summary>Lo que lleva inventario y está en su mínimo o debajo (<see cref="PocaExistencia"/>).</summary>
    PocaExistencia,

    /// <summary>Lo que deja menos que el margen mínimo (el de su departamento o el general): solo con «Ver costos, utilidad y margen».</summary>
    MargenBajo,
}

/// <summary>
/// Cuándo queda poca existencia: «Poca existencia» en Productos (F3) y «Bajos en inventario» en Inventario (F4). Con su
/// mínimo de inventario o menos, el que se le pone en su ficha, como en eleventa (2026-10-17); sin él, con
/// <see cref="Hasta"/> o menos, la misma cuenta con que la venta dice «Quedan 2». El sugerido calcula aparte el suyo con lo
/// que se vende (es caro para una lista).
/// </summary>
public static class PocaExistencia
{
    /// <summary>Sin un mínimo propio: con esto o menos.</summary>
    public const int Hasta = 3;

    /// <summary>El mínimo con que se revisa: el propio o, sin él, <see cref="Hasta"/>.</summary>
    public static Cantidad Minimo(Cantidad? propio) => propio ?? Cantidad.Piezas(Hasta);

    /// <summary>Si está en su mínimo o debajo (también lo que quedó en negativo).</summary>
    public static bool Es(Cantidad existencia, Cantidad? minimoPropio) => existencia <= Minimo(minimoPropio);
}

/// <summary>Un renglón de la lista de Productos: lo de su primera variante (las activas primero, por nombre).</summary>
/// <param name="PrecioCentavos">El de menudeo; vacío si no tiene.</param>
/// <param name="Existencia">En la sucursal; vacía en un servicio.</param>
/// <param name="MargenCentesimas">Solo para quien ve la utilidad, y si se sabe su costo (hay existencia).</param>
/// <param name="MargenMinimoCentesimas">El de su departamento o el general, junto con el margen.</param>
/// <param name="Minimo">
/// El mínimo de inventario propio de su primera variante, en unidades; vacío: no tiene (cuenta <see cref="PocaExistencia.Hasta"/>).
/// Vacío también en un servicio de antes.
/// </param>
public sealed record ProductoEnListaDto(
    Guid ProductoId, string Nombre, string? Marca, string Departamento, int Variantes, string? Codigo, long? PrecioCentavos, decimal? Existencia,
    bool Servicio, bool Activo, bool PorCompletar, int? MargenCentesimas = null, int? MargenMinimoCentesimas = null, decimal? Minimo = null);

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
/// <param name="BajosEnInventario">
/// Lo que está en su mínimo o debajo (<see cref="PocaExistencia"/>): lo de «Bajos en inventario». Cero en un servicio de antes.
/// </param>
public sealed record PendientesInventarioDto(
    int ComprasContando, int ComprasSinNota, int OrdenesAbiertas, int OrdenesEnCamino, int ConteosCompletosAbiertos, int BajosEnInventario = 0);

/// <summary>
/// Productos e Inventario en el piloto con eleventa, con su lectura prendida (decisión del dueño, 2026-10-10: las altas y
/// los precios, en eleventa): lo que dicen esas páginas. Vacíos: fuera del piloto, todo se hace aquí.
/// </summary>
/// <param name="Productos">De los productos de eleventa, lo que pone la lectura no se cambia aquí (la ficha dice qué).</param>
/// <param name="Inventario">Las tareas que mueven existencias no se ofrecen: el inventario se lleva en eleventa.</param>
public sealed record TrastiendaEnElPilotoDto(string? Productos, string? Inventario);
