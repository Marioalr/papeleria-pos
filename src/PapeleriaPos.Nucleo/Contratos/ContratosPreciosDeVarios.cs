using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Cambiar precios de varios a la vez (Productos › Cambiar precios) ─────────
// Como «Actualizar varios» de eleventa: se eligen los productos y la regla, se ve cómo quedan y se aplica todo junto. Lo
// hace quien entró con «Productos, precios y etiquetas»; el margen solo lo ve quien tiene «Ver costos, utilidad y margen».

/// <summary>Cuáles productos cambian: los activos de un departamento, de una marca, los que coinciden con un texto o los marcados.</summary>
public enum CualesProductos
{
    Departamento,
    Marca,

    /// <summary>
    /// Los que coinciden con un texto, como la búsqueda de la lista: parte del nombre, la marca, el departamento o un código; y,
    /// si se da, solo los de un departamento, como el filtro de la lista.
    /// </summary>
    Busqueda,

    /// <summary>Los marcados en la lista de Productos.</summary>
    Marcados,
}

/// <summary>Ver cómo quedan o aplicar: lo mismo, para que lo que se aplica sea lo que se vio.</summary>
/// <param name="UsuarioId">Quien entró a la caja: con «Productos, precios y etiquetas» en «Puede».</param>
/// <param name="DepartamentoId">
/// Con <see cref="CualesProductos.Departamento"/>, cuál; con <see cref="CualesProductos.Busqueda"/>, si se da, solo los de ese.
/// </param>
/// <param name="Marca">Con <see cref="CualesProductos.Marca"/>, por su nombre.</param>
/// <param name="Texto">Con <see cref="CualesProductos.Busqueda"/>.</param>
/// <param name="Productos">Con <see cref="CualesProductos.Marcados"/>: los productos marcados.</param>
/// <param name="Valor">El porcentaje en centésimas (1000 = 10 %) o la cantidad en centavos, según <paramref name="Como"/>.</param>
/// <param name="ConMayoreo">También las escalas de mayoreo y los precios por paquete, en la misma proporción.</param>
/// <param name="Huella">Al aplicar, la de la vista que se vio: si algo cambió desde entonces, no se aplica.</param>
/// <param name="Id">
/// Al aplicar, el de esta vez (lo pone la caja con la vista): si la respuesta se pierde y se vuelve a pedir, no se aplica dos
/// veces.
/// </param>
public sealed record SolicitudPreciosDeVarios(
    Guid UsuarioId, Guid SucursalId, CualesProductos Cuales, Guid? DepartamentoId, string? Marca, string? Texto, IReadOnlyList<Guid>? Productos,
    ComoCambiarPrecios Como, long Valor, bool ConMayoreo, RedondeoDePrecios Redondeo, string? Huella = null, Guid? Id = null);

/// <summary>Un precio de mayoreo que cambia junto con el menudeo.</summary>
/// <param name="Cual">«desde 12», o el paquete con su precio propio: «Caja 12».</param>
public sealed record MayoreoQueCambiaDto(string Cual, long AntesCentavos, long DespuesCentavos);

/// <summary>Un producto (una de sus variantes) con su precio de menudeo de ahora y el que queda.</summary>
/// <param name="Mayoreo">Lo de su mayoreo que cambia (si se pidió).</param>
/// <param name="MenudeoEnEleventa">
/// En el piloto con eleventa, su menudeo lo pone la lectura de eleventa: aquí no cambia, solo su mayoreo de aquí.
/// </param>
/// <param name="MargenAntesCentesimas">El margen con el menudeo de ahora: solo para quien ve la utilidad, y si se sabe su costo.</param>
/// <param name="MargenDespuesCentesimas">El margen con el menudeo que queda.</param>
/// <param name="MargenMinimoCentesimas">El de su departamento o el general: si el que queda es menor, se avisa.</param>
/// <param name="MayoreoAjustado">
/// Algo de su mayoreo no quedó en su proporción redondeada: para seguir más barato que el menudeo, se redondeó hacia abajo o
/// se quedó como estaba (<see cref="CambioDePrecios.Variante"/>).
/// </param>
public sealed record PrecioQueCambiaDto(
    Guid ProductoId, Guid VarianteId, string Producto, long AntesCentavos, long DespuesCentavos, IReadOnlyList<MayoreoQueCambiaDto> Mayoreo,
    bool MenudeoEnEleventa = false, int? MargenAntesCentesimas = null, int? MargenDespuesCentesimas = null, int? MargenMinimoCentesimas = null,
    bool MayoreoAjustado = false);

public sealed record PrecioQueNoCambiaDto(Guid ProductoId, Guid VarianteId, string Producto, PorQueNoCambia PorQue);

/// <summary>Cómo quedan, antes de aplicar.</summary>
/// <param name="Cuales">«la marca «Bic»», «los marcados en la lista»…</param>
/// <param name="Regla">«subir 10 %, con el mayoreo en la misma proporción, redondeado a los 50 centavos más cercanos».</param>
/// <param name="EnEleventa">En el piloto con eleventa, si alguno es de eleventa: por qué su menudeo no cambia aquí.</param>
/// <param name="ConMargen">Trae el margen: quien pidió la vista ve la utilidad.</param>
/// <param name="Huella">Lo que se vio: al aplicar, el servicio revisa que siga igual.</param>
public sealed record VistaPreciosDeVariosDto(
    string Cuales, string Regla, IReadOnlyList<PrecioQueCambiaDto> Cambian, IReadOnlyList<PrecioQueNoCambiaDto> NoCambian, string? EnEleventa,
    bool ConMargen, string Huella);

/// <summary>Lo que se aplicó.</summary>
/// <param name="Productos">Cuántos productos (variantes) cambiaron.</param>
/// <param name="Precios">Cuántos precios cambiaron, con los de su mayoreo.</param>
/// <param name="Bitacora">Lo que quedó en la bitácora.</param>
/// <param name="YaSeHabiaAplicado">Se pidió otra vez (la respuesta se había perdido): no se aplicó dos veces.</param>
public sealed record PreciosDeVariosAplicadosDto(int Productos, int Precios, string Bitacora, bool YaSeHabiaAplicado = false);
