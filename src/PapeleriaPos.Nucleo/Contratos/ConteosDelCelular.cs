using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// La lista contada con el celular, mandada a una caja (docs/celular-como-caja.md, «Mandar un conteo a una caja»; dueño, 2026-11-22).
// El celular solo dice qué y cuánto hay; la existencia la cambia la caja, cuando quien puede acepta la lista.

// ── Lo que usa el celular ────────────────────────────────────────────────────────────────────────────────────

/// <summary>Una caja a la que se le puede mandar un conteo ahora, o por qué no.</summary>
/// <param name="Recibe">Tiene un turno abierto de alguien con «Conteo completo de un departamento» (y no es el piloto con eleventa).</param>
/// <param name="Atiende">Quien tiene el turno abierto ahí (el que puede aceptarla, si recibe).</param>
/// <param name="Codigo">Por qué no recibe: <c>sin-turno</c>, <c>sin-permiso</c> o <c>piloto-eleventa</c>; vacío si recibe.</param>
/// <param name="PorQue">Lo mismo, para enseñarlo tal cual.</param>
/// <param name="Pendientes">Las listas que ya esperan ahí a que las revisen.</param>
public sealed record CajaParaContarDto(
    Guid Id, string Nombre, string Serie, bool Recibe, string? Atiende, string? Codigo, string? PorQue, int Pendientes);

/// <param name="Id">Lo pone el celular, uno por lista: si la respuesta no llega y la manda otra vez, no se duplica.</param>
/// <param name="Destino">La caja a la que va.</param>
/// <param name="ContadoUtc">Cuándo se terminó de contar; sin él, cuando llega. Desde ahí cuenta lo que se vende antes de aceptarla.</param>
/// <param name="UsuarioId">Quien contó, si entró en el celular con su usuario (<c>POST /api/sesion</c>); si no, va vacío.</param>
/// <param name="Renglones">
/// Cada producto con lo que hay en total, por su variante y presentación de la copia o, sin ellas, por su código (como en «mandar»).
/// La caja de 12 contada como caja se convierte a piezas; varios renglones del mismo producto se suman.
/// </param>
public sealed record SolicitudMandarConteo(Guid Id, Guid Destino, DateTime? ContadoUtc, Guid? UsuarioId, IReadOnlyList<RenglonMandado> Renglones);

/// <summary>Un producto de la lista, como quedó.</summary>
/// <param name="Contado">Lo que mandó el celular, en unidad base (piezas, metros, kilos).</param>
/// <param name="Aceptado">Lo que se aplicó (cambiado o no en la caja); vacío si se quitó, si se rechazó la lista o si todavía no se revisa.</param>
/// <param name="Aplicado">Entró al inventario.</param>
/// <param name="Nota">Por qué no se aplicó o qué cambió («Lo quitaron en la caja», «Ya se contó después…»).</param>
public sealed record RenglonDeConteoDto(Guid VarianteId, string Descripcion, decimal Contado, decimal? Aceptado, bool Aplicado, string? Nota);

/// <summary>Cómo va una lista: lo contesta al mandarla y al preguntar después.</summary>
/// <param name="Estado"><c>recibida</c>, <c>aceptada</c>, <c>aceptada-con-cambios</c> o <c>rechazada</c>.</param>
/// <param name="Resolvio">Quien la aceptó o la rechazó.</param>
/// <param name="Motivo">Por qué se rechazó, si lo escribieron.</param>
/// <param name="Productos">Cuántos productos trae.</param>
/// <param name="Aplicados">Cuántos entraron al inventario.</param>
public sealed record EstadoConteoDto(
    Guid Id, Guid Destino, string Caja, string Estado, DateTime ContadoUtc, DateTime RecibidoUtc, DateTime? ResueltoUtc, string? Resolvio,
    string? Motivo, int Productos, int Aplicados, IReadOnlyList<RenglonDeConteoDto> Renglones);

// ── Lo que usa la caja que la recibe ─────────────────────────────────────────────────────────────────────────

/// <summary>Una lista que espera en esta caja (para el aviso y la lista de Inventario › Conteos del celular).</summary>
/// <param name="De">El celular («Celular de Luis»).</param>
/// <param name="Conto">Quien contó, si se sabe.</param>
public sealed record ConteoRecibidoDto(Guid Id, string De, string? Conto, DateTime ContadoLocal, DateTime RecibidoLocal, int Productos);

/// <summary>Un producto de la lista por revisar, con lo que hay ahora.</summary>
/// <param name="Detalle">Cómo lo mandó el celular, si no fue solo en su unidad: «2 Caja 12 + 5 Pieza».</param>
/// <param name="Contado">Lo que contó el celular, en unidad base.</param>
/// <param name="Desde">Lo que se movió desde que se contó (lo vendido, −; lo que entró, +): al aplicarlo queda <c>Contado + Desde</c>.</param>
/// <param name="Hoy">Lo que dice el sistema ahora.</param>
/// <param name="Superado">Si ya se contó después (otra lista más nueva, el conteo del día, un ajuste…): no se aplica, y por qué.</param>
public sealed record RenglonPorRevisarDto(
    Guid VarianteId, string Descripcion, string? Codigo, UnidadBase Unidad, string? Detalle, decimal Contado, decimal Desde, decimal Hoy, string? Superado);

public sealed record ConteoPorRevisarDto(ConteoRecibidoDto Lista, IReadOnlyList<RenglonPorRevisarDto> Renglones);

/// <summary>Lo que queda de un producto al aceptar: lo contado, o lo que se corrigió en la caja (en unidad base).</summary>
public sealed record RenglonAceptado(Guid VarianteId, long CantidadMilesimas);

/// <param name="Renglones">Los que se aplican; los que no vienen se quitaron en la caja (no se tocan).</param>
public sealed record SolicitudAceptarConteo(Guid UsuarioId, IReadOnlyList<RenglonAceptado> Renglones);

public sealed record SolicitudRechazarConteo(Guid UsuarioId, string? Motivo);
