using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Empezar y terminar la temporada, su hoja, las tarjetas de precio y lo más preguntado (entrega 13, parte 3) ──
// docs/propuesta-interfaz.md, «Lo que se señala: exhibidos y temporada», puntos 3 a 6. Todo en Productos (F3), con la caja
// principal: no viaja en la copia de las cajas. Lo único que hace la venta es contar lo que se pregunta en el verificador.

/// <summary>Una temporada en Productos › Temporadas: sus fechas, cuántos productos tiene y cómo va.</summary>
/// <param name="Fechas">«del 20 de noviembre al 1 de enero»; vacío si todavía no tiene (no corre).</param>
/// <param name="Inicio">La que sigue: la que corre hoy o, si no, la siguiente; vacío sin fechas.</param>
/// <param name="SinFoto">Cuántos de sus productos todavía no tienen foto.</param>
/// <param name="PreciosConfirmados">De la que sigue: «Luis confirmó los precios el 10/10/2026»; vacío si no.</param>
/// <param name="Terminada">De la última que empezó: «Luis la terminó el 6/11/2026»; vacío si no.</param>
public sealed record TemporadaResumenDto(
    Guid ExhibidorId, string Nombre, string? Fechas, bool Corre, DateOnly? Inicio, DateOnly? Fin, int Productos, int SinFoto,
    string? PreciosConfirmados, string? Terminada);

/// <summary>Las temporadas activas: primero la que corre, luego la que sigue; las que no tienen fechas, al final.</summary>
/// <param name="Hoy">El día de hoy en la tienda, como lo cuenta la caja principal (con él se sabe cuánto falta para la que sigue).</param>
public sealed record TemporadasDto(DateOnly Hoy, IReadOnlyList<TemporadaResumenDto> Temporadas);

/// <summary>Un producto de la temporada al empezarla: la lista del año pasado, con los precios.</summary>
/// <param name="Lugar">El lugar de la mesa donde está (el primero, si está en dos).</param>
/// <param name="AnteriorCentavos">El precio que tuvo en la temporada del año pasado; vacío si no se sabe.</param>
/// <param name="DeDonde">De dónde salió el del año pasado: «historial» (el de sus precios) o «ventas» (lo que se cobró).</param>
/// <param name="PrecioCentavos">El de menudeo de hoy; vacío si no tiene.</param>
/// <param name="PisoCentavos">Lo más bajo que puede quedar su menudeo para que su mayoreo siga más barato (cero sin mayoreo).</param>
/// <param name="Mayoreo">Su mayoreo como se lee («desde 12: $5.50»), para decir por qué; vacío sin mayoreo.</param>
/// <param name="EnEleventa">En el piloto con eleventa: su menudeo se cambia en eleventa y llega con la lectura.</param>
/// <param name="CostoCentavos">Solo para quien ve la utilidad: el costo de hoy (el de su última compra; sin compras, el promedio).</param>
/// <param name="MargenCentesimas">El que deja su precio de hoy con ese costo.</param>
/// <param name="MargenMinimoCentesimas">El de su departamento o el general.</param>
/// <param name="SugeridoCentavos"><see cref="PreciosDeTemporada.Sugerido"/>; vacío sin costo.</param>
public sealed record ProductoDeTemporadaDto(
    Guid VarianteId, Guid ProductoId, string Nombre, string? Codigo, string Lugar, bool Activo, bool TieneFoto,
    long? AnteriorCentavos, string? DeDonde, long? PrecioCentavos, long PisoCentavos, string? Mayoreo, bool EnEleventa,
    long? CostoCentavos = null, int? MargenCentesimas = null, int? MargenMinimoCentesimas = null, long? SugeridoCentavos = null);

/// <summary>
/// Empezar la temporada (docs/propuesta-interfaz.md, «Lo que se señala», punto 3): la que sigue (la que corre o la siguiente)
/// con la lista del año pasado, en el orden de la mesa.
/// </summary>
/// <param name="InicioAnterior">Las fechas de la del año pasado: de ahí salen los precios de entonces.</param>
/// <param name="PreciosConfirmados">«Luis confirmó los precios el 10/10/2026»; vacío si no se han confirmado.</param>
/// <param name="VeCostos">Si trae el costo, el margen y el sugerido («Ver costos, utilidad y margen»).</param>
/// <param name="EnEleventa">En el piloto con eleventa: que los precios de lo de eleventa se cambian allá.</param>
public sealed record TemporadaParaEmpezarDto(
    Guid ExhibidorId, string Nombre, string? Fechas, DateOnly Inicio, DateOnly Fin, DateOnly InicioAnterior, DateOnly FinAnterior, bool Corre,
    string? PreciosConfirmados, bool VeCostos, string? EnEleventa, IReadOnlyList<ProductoDeTemporadaDto> Productos);

/// <summary>El precio que se confirma de un producto.</summary>
/// <param name="AntesCentavos">El de hoy que se vio en la lista: si cambió mientras tanto, nada se confirma.</param>
public sealed record PrecioDeTemporadaDto(Guid VarianteId, long AntesCentavos, long PrecioCentavos);

/// <summary>Los precios de la temporada, todos juntos: los que cambian, cambian; los demás quedan confirmados como están.</summary>
public sealed record SolicitudConfirmarPrecios(Guid UsuarioId, Guid SucursalId, IReadOnlyList<PrecioDeTemporadaDto> Precios);

/// <summary>Cómo quedó: cuántos precios cambiaron y, de esos, cuántos tienen tarjeta de precio (hay que volver a imprimirlas).</summary>
public sealed record PreciosConfirmadosDto(int Cambiaron, int ConTarjeta, TemporadaParaEmpezarDto Temporada);

/// <summary>Un producto en la hoja de la temporada: su foto (JPEG en base64; vacía, sus iniciales), su nombre, su precio y su código.</summary>
public sealed record ProductoEnHojaDto(Guid VarianteId, string Nombre, long? PrecioCentavos, string Codigo, string? FotoBase64);

/// <summary>Un lugar de la mesa con sus productos, en su orden.</summary>
public sealed record LugarEnHojaDto(string Nombre, IReadOnlyList<ProductoEnHojaDto> Productos);

/// <summary>La hoja de la temporada lista para imprimir. A lo que no tenía código se le dio uno interno (<paramref name="CodigosNuevos"/>).</summary>
public sealed record HojaDeTemporadaDto(string Temporada, string? Fechas, string Sucursal, IReadOnlyList<LugarEnHojaDto> Lugares, int CodigosNuevos);

/// <summary>Quien la imprime: a lo que no tiene código se le da uno con «Productos, precios y etiquetas».</summary>
public sealed record SolicitudHojaDeTemporada(Guid UsuarioId, Guid SucursalId);

/// <summary>Un producto en el resumen de la temporada, en unidad base.</summary>
/// <param name="Inicial">Lo que había al empezar, más lo que llegó durante la temporada.</param>
/// <param name="Vendido">Lo que se vendió en la temporada, menos lo que se devolvió.</param>
/// <param name="Quedo">Lo que quedó (con lo contado, si ya se terminó).</param>
/// <param name="Contado">Mientras no se termina: lo que ya se contó de él (se guarda al escribirlo); vacío si no se ha contado.</param>
/// <param name="Esperado">Lo que decía el sistema cuando se contó: al terminarla entra la diferencia, y lo vendido después sigue contando.</param>
public sealed record ResumenDeProductoDto(
    Guid VarianteId, string Nombre, string? Codigo, string Lugar, UnidadBase Unidad, bool LlevaInventario, decimal Inicial, decimal Vendido, decimal Quedo,
    decimal? Contado = null, decimal? Esperado = null);

/// <summary>
/// Terminar la temporada (docs/propuesta-interfaz.md, «Lo que se señala», punto 4): la última que empezó, con el resumen de qué
/// se vendió y qué no. Mientras no se termina, el resumen es hasta hoy; al terminarla queda guardado.
/// </summary>
/// <param name="Desde">Desde qué día va el resumen: el primero de la temporada o, si la tienda empezó a usar este POS después, ese día.</param>
/// <param name="Hasta">Hasta qué día va el resumen.</param>
/// <param name="Terminada">«Luis la terminó el 6/11/2026»; vacío mientras no se termina.</param>
/// <param name="Contada">Si al terminarla se contó lo que sobró.</param>
/// <param name="AntesDelPos">Terminó antes de que la tienda usara este POS: no hay con qué armar el resumen.</param>
/// <param name="SeCuenta">Si lo que sobró se cuenta al terminarla: no en el piloto con eleventa (el inventario se lleva allá).</param>
/// <param name="EnEleventa">En el piloto: por qué no se cuenta y de dónde sale el resumen.</param>
public sealed record TemporadaParaTerminarDto(
    Guid ExhibidorId, string Nombre, DateOnly Inicio, DateOnly Fin, DateOnly Desde, DateOnly Hasta, bool Corre, string? Terminada, bool Contada, bool AntesDelPos,
    bool SeCuenta, string? EnEleventa, IReadOnlyList<ResumenDeProductoDto> Productos);

/// <summary>
/// Lo que se contó de un producto de la temporada, en milésimas de su unidad base, al momento de contarlo: el servicio lo guarda
/// con lo que dice el sistema en ese momento (como una captura del conteo completo). Vacío: ya no está contado.
/// </summary>
public sealed record SolicitudContado(Guid UsuarioId, Guid SucursalId, Guid VarianteId, long? CantidadMilesimas);

/// <summary>
/// Terminar la temporada: de lo que se contó entra al kardex la diferencia con lo que decía el sistema al contarlo (lo que no se
/// contó se queda como está), y queda guardado el resumen.
/// </summary>
public sealed record SolicitudTerminarTemporada(Guid UsuarioId, Guid SucursalId);

/// <summary>Un producto con su tarjeta de precio (o para dársela).</summary>
/// <param name="PrecioCentavos">El de menudeo de hoy.</param>
/// <param name="TarjetaCentavos">El precio con que salió su tarjeta; vacío si no tiene.</param>
/// <param name="ImpresaLocal">Cuándo se imprimió su tarjeta.</param>
/// <param name="PorQue">Por qué hay que volver a imprimirla («cambió el precio»); vacío si está bien o no tiene.</param>
/// <param name="Donde">En qué exhibidor o temporada está («Mostrador · Repisa de arriba»); vacío si en ninguno.</param>
/// <param name="Veces">En lo más preguntado: cuántas veces se preguntó en el verificador.</param>
public sealed record ProductoConTarjetaDto(
    Guid VarianteId, string Nombre, string? Codigo, long? PrecioCentavos, long? TarjetaCentavos, DateTime? ImpresaLocal, string? PorQue,
    string? Donde, int Veces = 0);

/// <summary>Las tarjetas por volver a imprimir (su precio o su código cambió) y cuántas tarjetas hay.</summary>
public sealed record TarjetasDto(IReadOnlyList<ProductoConTarjetaDto> PorReimprimir, int ConTarjeta);

/// <summary>Las tarjetas que se van a imprimir: a lo que no tiene código se le da uno interno.</summary>
public sealed record SolicitudPrepararTarjetas(Guid UsuarioId, Guid SucursalId, IReadOnlyList<Guid> Variantes);

/// <summary>Una tarjeta como sale: el nombre, el precio de hoy y el código.</summary>
public sealed record TarjetaParaImprimirDto(Guid VarianteId, string Nombre, long PrecioCentavos, string Codigo);

/// <summary>Las tarjetas listas para la hoja; las de lo que no tiene precio no salen (<paramref name="SinPrecio"/>).</summary>
public sealed record TarjetasParaImprimirDto(IReadOnlyList<TarjetaParaImprimirDto> Tarjetas, int CodigosNuevos, IReadOnlyList<string> SinPrecio);

/// <summary>Las que se mandaron a imprimir, con el precio y el código con que salieron: dejan de estar por reimprimir.</summary>
public sealed record SolicitudTarjetasImpresas(Guid UsuarioId, Guid SucursalId, IReadOnlyList<TarjetaParaImprimirDto> Tarjetas);

/// <summary>Ya no tiene tarjeta (se quitó del exhibidor, se tiró): deja de estar por reimprimir.</summary>
public sealed record SolicitudQuitarTarjeta(Guid UsuarioId, Guid SucursalId);

/// <summary>Una consulta del verificador (F9): qué producto se preguntó y en qué caja.</summary>
public sealed record SolicitudConsulta(Guid SucursalId, Guid VarianteId, Guid? CajaId = null);
