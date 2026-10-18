using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Lo que se señala: el mostrador y las temporadas (entrega 13) ─────────────
// Ctrl+M en la venta: cada exhibidor y cada temporada con sus productos en el mismo acomodo que en la tienda. Viajan a las
// cajas con la copia (Ctrl+M se pinta sin la red); se dan de alta y se cambian en Configuración › Mostrador y temporadas, y
// sus productos se acomodan en el mismo Ctrl+M («Acomodar»), con «Productos, precios y etiquetas».

/// <summary>Un lugar de un exhibidor o de una temporada, con sus productos (las variantes) en orden, de izquierda a derecha.</summary>
public sealed record LugarExhibidorDto(Guid Id, string Nombre, IReadOnlyList<Guid> Productos);

/// <summary>
/// Un exhibidor o una temporada activo, como va en la copia de cada caja y en la Configuración, con sus lugares en orden.
/// </summary>
/// <param name="Desde">De una temporada: cuándo empieza cada año. Vacío sin fechas (no corre).</param>
/// <param name="Hasta">Cuándo termina (ese día todavía corre); puede ser del año siguiente.</param>
public sealed record ExhibidorDto(Guid Id, string Nombre, TipoExhibidor Tipo, DiaDelAnio? Desde, DiaDelAnio? Hasta, IReadOnlyList<LugarExhibidorDto> Lugares)
{
    /// <summary>Si es una temporada que corre ese día (el de la tienda).</summary>
    public bool CorreEl(DateOnly fecha) => Temporadas.Corre(Tipo, Desde, Hasta, fecha);
}

/// <summary>
/// Los exhibidores y las temporadas activos: primero los exhibidores y después las temporadas, cada uno en su orden. Con la
/// versión del catálogo con que se leyeron: la caja usa lo más nuevo entre esto y su copia.
/// </summary>
/// <param name="Base">
/// De qué base es la versión (la de la copia, <see cref="VersionesCopiaDto.Base"/>: cambia al arrancar la caja principal y al
/// empezar de cero la prueba). La caja solo compara la versión con la de una copia de la misma base: después de restaurar un
/// respaldo, las versiones vuelven a empezar. Vacía en lo que la caja arma con su copia.
/// </param>
public sealed record MostradorDto(long Version, IReadOnlyList<ExhibidorDto> Exhibidores, Guid? Base = null);

/// <summary>Un exhibidor o una temporada nuevo: empieza sin lugares; se le agregan en la Configuración.</summary>
public sealed record SolicitudNuevoExhibidor(Guid UsuarioId, Guid SucursalId, string Nombre, TipoExhibidor Tipo);

/// <summary>Las fechas de una temporada: las dos o ninguna (sin fechas no corre).</summary>
public sealed record FechasTemporadaDto(DiaDelAnio? Desde, DiaDelAnio? Hasta);

/// <summary>Cambiar el nombre o las fechas: lo que viene vacío se queda como está.</summary>
public sealed record SolicitudCambiarExhibidor(Guid UsuarioId, Guid SucursalId, string? Nombre = null, FechasTemporadaDto? Fechas = null);

/// <summary>Quitar un exhibidor, una temporada o un lugar: quien entró a la caja.</summary>
public sealed record SolicitudMostrador(Guid UsuarioId, Guid SucursalId);

/// <summary>Un lugar nuevo (va al final) o su nombre nuevo.</summary>
public sealed record SolicitudLugar(Guid UsuarioId, Guid SucursalId, string Nombre);

/// <summary>Subir (−1) o bajar (+1) un lugar entre los de su exhibidor.</summary>
public sealed record SolicitudMoverLugar(Guid UsuarioId, Guid SucursalId, int Paso);

/// <summary>Cómo quedan los productos de un lugar, en orden: los que no vienen salen de él.</summary>
public sealed record LugarConProductosDto(Guid LugarId, IReadOnlyList<Guid> Productos);

/// <summary>
/// «Acomodar» en Ctrl+M: cómo quedan los productos de uno o más lugares (pasar un producto de una repisa a otra cambia las
/// dos), todo junto.
/// </summary>
public sealed record SolicitudAcomodo(Guid UsuarioId, Guid SucursalId, IReadOnlyList<LugarConProductosDto> Lugares);
