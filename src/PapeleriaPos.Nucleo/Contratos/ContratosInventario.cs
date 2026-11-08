using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Los reportes del inventario, como en eleventa (2026-10-17) ──────────────
// Lo que eleventa tiene en F4 y el POS no tenía en pantalla: el «Reporte de inventario» (lo que vale a costo, por
// departamento), los «Movimientos de inventario» (por fecha, cajero, producto, departamento y tipo) y los «Productos bajos
// en inventario» (con un mínimo y un máximo por producto). Son tarjetas de Inventario (F4) que solo leen: en el piloto con
// eleventa se ofrecen igual, porque no mueven existencias.

/// <summary>
/// Quién consulta cada reporte del inventario: quien tiene alguno de sus permisos en «Puede» (consultar no se autoriza: «Con
/// autorización» no basta). Las tarjetas y el servicio usan lo mismo.
/// </summary>
public static class ConsultasInventario
{
    /// <summary>Lo que vale el inventario es costo: solo con «Ver costos, utilidad y margen».</summary>
    public static IReadOnlyList<AccionControlada> Valor { get; } = [AccionControlada.VerUtilidad];

    /// <summary>
    /// Los movimientos: quien lleva el inventario (el catálogo, los ajustes, las compras, las órdenes o el conteo completo) o ve
    /// los costos. Con los permisos de defecto, el dueño y el encargado; el cajero no.
    /// </summary>
    public static IReadOnlyList<AccionControlada> Movimientos { get; } =
    [
        AccionControlada.EditarCatalogo, AccionControlada.AjustarInventario, AccionControlada.CostearCompra, AccionControlada.PedirCompra,
        AccionControlada.ConteoCompleto, AccionControlada.VerUtilidad,
    ];

    /// <summary>Bajos en inventario: quien pide la mercancía (órdenes y sugerido) o pone los mínimos en la ficha del producto.</summary>
    public static IReadOnlyList<AccionControlada> Bajos { get; } = [AccionControlada.PedirCompra, AccionControlada.EditarCatalogo];

    /// <summary>«… lo consulta quien tiene el permiso «X» o «Y».», para el error del servicio.</summary>
    public static string QuienConsulta(string que, IReadOnlyList<AccionControlada> permisos) =>
        permisos.Count == 1
            ? $"{que} lo consulta quien tiene el permiso «{Permisos.Nombre(permisos[0])}»."
            : $"{que} lo consulta quien tiene alguno de estos permisos: "
              + string.Join(", ", permisos.Take(permisos.Count - 1).Select(p => $"«{Permisos.Nombre(p)}»")) + $" o «{Permisos.Nombre(permisos[^1])}».";
}

// ── Lo que vale el inventario ───────────────────────────────────────────────

/// <summary>Qué productos se listan en «Lo que vale el inventario».</summary>
public enum FiltroValor
{
    /// <summary>Los que tienen existencia, de lo que más vale a lo que menos.</summary>
    ConExistencia,

    /// <summary>Los activos que no tienen.</summary>
    EnCero,

    /// <summary>Los que quedaron en negativo: se vendió más de lo que había en el sistema.</summary>
    Negativa,

    /// <summary>Los que tienen existencia sin costo (no suman a lo que vale).</summary>
    SinCosto,
}

/// <summary>Lo que vale el inventario de un departamento (o de toda la sucursal), a costo promedio.</summary>
/// <param name="DepartamentoId">Vacío: toda la sucursal.</param>
/// <param name="ValorCentavos">Costo promedio × existencia de lo que tiene existencia (lo negativo no resta).</param>
/// <param name="ConExistencia">Cuántos productos tienen existencia (cada variante cuenta).</param>
/// <param name="EnCero">Cuántos activos no tienen.</param>
/// <param name="Negativa">Cuántos quedaron en negativo.</param>
/// <param name="SinCosto">De los que tienen existencia, cuántos no tienen costo y no suman.</param>
public sealed record ValorGrupoDto(Guid? DepartamentoId, string Nombre, long ValorCentavos, int ConExistencia, int EnCero, int Negativa, int SinCosto);

/// <summary>Un producto (una variante) en «Lo que vale el inventario».</summary>
/// <param name="CostoPromedioCentavos">Por unidad, sin redondear; vacío si no hay existencia.</param>
/// <param name="ValorCentavos">Lo que vale su existencia a costo (cero si no tiene o es negativa).</param>
public sealed record ProductoValorDto(
    Guid VarianteId, Guid ProductoId, string Nombre, string? Codigo, string Departamento, UnidadBase Unidad, decimal Existencia,
    decimal? CostoPromedioCentavos, long ValorCentavos, bool Activo);

/// <summary>Lo que vale el inventario, en la caja como el «Reporte de inventario» de eleventa (que lo dice solo con el total).</summary>
/// <param name="FechaLocal">Cuándo se calculó: es la existencia de ese momento.</param>
/// <param name="Total">Toda la sucursal.</param>
/// <param name="Departamentos">Cada departamento con algo que contar, de lo que más vale a lo que menos.</param>
/// <param name="DepartamentoId">El de <paramref name="Productos"/>; vacío: todos.</param>
/// <param name="Productos">
/// Una página de los que tienen <paramref name="Filtro"/>: de lo que más vale a lo que menos (los negativos, de lo más
/// negativo).
/// </param>
/// <param name="TotalProductos">Cuántos hay con ese departamento y ese filtro.</param>
/// <param name="Pagina">Cuál es la de <paramref name="Productos"/>, desde cero, de <paramref name="Tamano"/> en <paramref name="Tamano"/>.</param>
public sealed record ValorInventarioDto(
    DateTime FechaLocal, ValorGrupoDto Total, IReadOnlyList<ValorGrupoDto> Departamentos, Guid? DepartamentoId, FiltroValor Filtro,
    IReadOnlyList<ProductoValorDto> Productos, int TotalProductos, int Pagina, int Tamano);

// ── Movimientos ─────────────────────────────────────────────────────────────

/// <summary>
/// El tipo de un movimiento como se filtra y se lee: los del kardex, con la lectura de eleventa aparte (en el piloto deja la
/// existencia en la de allá con un movimiento de conteo o, si el producto es nuevo, de inventario inicial).
/// </summary>
public enum ClaseMovimiento
{
    Venta,

    /// <summary>Lo que regresa de una devolución o de una cancelación.</summary>
    Devolucion,

    Compra,

    /// <summary>Los ajustes (también los de la depuración del catálogo).</summary>
    Ajuste,

    /// <summary>Las mermas (también lo que volvió dañado en una devolución).</summary>
    Merma,

    /// <summary>La diferencia del conteo del día o del conteo completo.</summary>
    Conteo,

    /// <summary>Abrir un paquete: sale el paquete y entran sus piezas.</summary>
    Paquete,

    /// <summary>Lo que había al importar.</summary>
    InventarioInicial,

    /// <summary>En el piloto: la lectura de eleventa deja la existencia en la de allá.</summary>
    LecturaEleventa,

    Traspaso,
    ConsumoServicio,

    /// <summary>«Agregar inventario» (2026-10-24): lo que llegó de un producto, sin proveedor ni nota. Aparte de las compras.</summary>
    AgregarInventario,
}

/// <summary>Los filtros de «Movimientos»: unos días y, si se quiere, el tipo, el producto, el departamento y quién.</summary>
/// <param name="Desde">El primer día, en la hora de México.</param>
/// <param name="Hasta">El último día, completo.</param>
/// <param name="Texto">El nombre o el código del producto: cada palabra en el nombre, la marca, la variante o un código.</param>
/// <param name="QuienId">
/// Quien lo hizo: el cajero de la venta, quien hizo la devolución, quien cerró la compra (o la recibió), quien ajustó o abrió
/// el paquete, quien contó el producto en el conteo del día o quien cerró el conteo completo.
/// </param>
public sealed record FiltroMovimientosDto(
    DateOnly Desde, DateOnly Hasta, ClaseMovimiento? Clase = null, string? Texto = null, Guid? DepartamentoId = null, Guid? QuienId = null);

/// <summary>Un movimiento del kardex en «Movimientos»: cuándo, qué, de qué producto, cuánto, lo que quedó, quién y su documento.</summary>
/// <param name="Producto">Con su variante («Pluma Bic Azul»).</param>
/// <param name="Cantidad">Con signo, en unidad base: + entra, − sale.</param>
/// <param name="Existencia">La que quedó después de él.</param>
/// <param name="Documento">El folio o lo que lo distingue: «A-000123», «C-000041 · Proveedor», «de A-000123», «del día»…</param>
/// <param name="Quien">Quién lo hizo, por su nombre; vacío si no se sabe (la importación, la depuración).</param>
/// <param name="CostoCentavos">Con signo, como el movimiento; solo con «Ver costos, utilidad y margen».</param>
public sealed record MovimientoInventarioDto(
    Guid Id, DateTime FechaLocal, ClaseMovimiento Clase, Guid ProductoId, string Producto, string? Codigo, string Departamento, UnidadBase Unidad,
    decimal Cantidad, decimal Existencia, string? Documento, string? Motivo, string? Quien, long? CostoCentavos);

/// <summary>Una página de «Movimientos», del más reciente al más viejo.</summary>
/// <param name="Total">Cuántos hay con esos filtros.</param>
/// <param name="Pagina">Cuál es esta, desde cero, de <paramref name="Tamano"/> en <paramref name="Tamano"/>.</param>
/// <param name="ConCostos">Si van los costos («Ver costos, utilidad y margen»).</param>
public sealed record MovimientosInventarioDto(IReadOnlyList<MovimientoInventarioDto> Movimientos, int Total, int Pagina, int Tamano, bool ConCostos);

// ── Bajos en inventario ─────────────────────────────────────────────────────

/// <summary>Un producto (una variante) en su mínimo o debajo, con cuánto pedir.</summary>
/// <param name="Minimo">El suyo, o <see cref="PocaExistencia.Hasta"/> si no tiene (<paramref name="MinimoPropio"/>).</param>
/// <param name="Maximo">El suyo; vacío si no tiene.</param>
/// <param name="MaximoSugerido">Sin máximo propio: el que calcula el sugerido de compra con lo que se vende; vacío si no se vende.</param>
/// <param name="EnCamino">Lo que ya viene en órdenes de compra abiertas.</param>
/// <param name="Pedir">
/// Cuánto pedir, en unidad base, contando lo que ya viene: lo que falta para su máximo o, sin máximo, lo que pide el sugerido;
/// con su mínimo propio, al menos lo que falta para llegar a él (el sugerido no lo conoce). Vacío si no hace falta o no se
/// sabe.
/// </param>
/// <param name="PedirEn">
/// Lo del sugerido va en la presentación en que se compra («2 Caja 12»); vacío si va por unidad (también lo que falta para el
/// mínimo).
/// </param>
public sealed record BajoInventarioDto(
    Guid VarianteId, Guid ProductoId, string Nombre, string? Codigo, Guid DepartamentoId, string Departamento, UnidadBase Unidad, decimal Existencia,
    decimal Minimo, bool MinimoPropio, decimal? Maximo, decimal? MaximoSugerido, decimal EnCamino, decimal? Pedir, string? PedirEn);

/// <summary>Lo que está en su mínimo o debajo, por departamento y por nombre: una página, con cuántos hay.</summary>
/// <param name="Total">Cuántos hay con esos filtros.</param>
/// <param name="Todos">Con el departamento y el texto, sin importar si tienen su mínimo propio.</param>
/// <param name="ConMinimoPropio">De esos, los que tienen su mínimo propio.</param>
/// <param name="Pagina">Cuál es esta, desde cero, de <paramref name="Tamano"/> en <paramref name="Tamano"/>.</param>
public sealed record BajosInventarioDto(IReadOnlyList<BajoInventarioDto> Productos, int Total, int Todos, int ConMinimoPropio, int Pagina, int Tamano);

// ── Agregar inventario, como en eleventa (2026-10-24) ───────────────────────
// Lo que llegó de un producto: cuántos y el costo de cada uno, sin proveedor ni nota (Inventario › Agregar inventario). Junto
// a «Recibir mercancía», que sigue igual para las compras de un proveedor.

/// <summary>Agregar a la existencia lo que llegó de un producto.</summary>
/// <param name="Id">Lo pone la caja: si la misma llega dos veces (se perdió la respuesta), se agrega una.</param>
/// <param name="CantidadMilesimas">En unidad base: más de cero, sin fracciones en lo que se cuenta por pieza.</param>
/// <param name="CostoUnitarioCentavos">
/// El costo de cada uno, por unidad base. Vacío: el de ahora. Solo lo manda quien ve los costos; de otro, el servicio lo rechaza.
/// </param>
/// <param name="CajaId">La caja donde se agregó (para la bitácora).</param>
public sealed record SolicitudAgregarInventario(
    Guid Id, Guid UsuarioId, Guid SucursalId, Guid VarianteId, long CantidadMilesimas, long? CostoUnitarioCentavos = null, Guid? CajaId = null);

/// <summary>El producto que se va a agregar, como está ahora en la sucursal.</summary>
/// <param name="Producto">El producto con su marca («Cuaderno profesional Scribe»).</param>
/// <param name="Variante">Su variante («Raya»); vacía si no tiene nombre (el producto es de una sola).</param>
/// <param name="Existencia">La de ahora, en su unidad: lo que se agrega va encima (no es un conteo a ciegas).</param>
/// <param name="CostoCentavos">
/// El costo de cada uno de ahora (el promedio de lo que hay; sin existencia, el último que se conoce), redondeado al centavo.
/// Solo para quien ve los costos; vacío también si todavía no tiene.
/// </param>
/// <param name="Promedio">Si ese costo es el promedio de lo que hay; si no, es el último que se conoce.</param>
/// <param name="ConCostos">Quien pregunta ve los costos: puede cambiar el de lo que llegó.</param>
public sealed record ProductoParaAgregarDto(
    Guid VarianteId, string Producto, string? Variante, string? Codigo, UnidadBase Unidad, decimal Existencia, long? CostoCentavos, bool Promedio,
    bool ConCostos);

/// <summary>Una de «Lo agregado hoy»: cuándo, qué, cuántos, quién y, para quien ve los costos, a cuánto cada uno.</summary>
/// <param name="Producto">Con su marca y su variante.</param>
/// <param name="Cantidad">Lo que llegó, en su unidad.</param>
/// <param name="CostoUnitarioCentavos">Solo para quien ve los costos.</param>
public sealed record EntradaInventarioDto(
    Guid Id, DateTime FechaLocal, Guid VarianteId, string Producto, string? Codigo, UnidadBase Unidad, decimal Cantidad, string Usuario,
    long? CostoUnitarioCentavos);

/// <summary>Lo que se agregó, con la existencia que quedó.</summary>
/// <param name="Existencia">La de ahora, ya con lo agregado.</param>
/// <param name="YaEstaba">La misma solicitud ya se había guardado (se perdió la respuesta): no se agregó otra vez.</param>
public sealed record AgregadoDto(EntradaInventarioDto Entrada, decimal Existencia, bool YaEstaba);

/// <summary>Lo agregado hoy en la sucursal, de lo más reciente a lo más viejo.</summary>
/// <param name="ConCostos">Si quien pregunta ve los costos (y puede cambiar el de lo que llega).</param>
public sealed record AgregadoHoyDto(IReadOnlyList<EntradaInventarioDto> Entradas, bool ConCostos);
