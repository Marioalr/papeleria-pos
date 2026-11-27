using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Nucleo.Contratos;

// Inventario › «Traspasos», provisional mientras no exista el servidor (dueño, 2026-11-26; docs/traspasos.md): la sucursal que
// lo tramita arma la lista y exporta un archivo; la que recibe lo importa, lo revisa y lo recibe.

/// <summary>Los traspasos de la sucursal, de lo más reciente a lo más viejo, y lo que puede quien pregunta.</summary>
/// <param name="PuedeModificar">Tiene «Traspasos: modificar lo que llegó»: cambia lo recibido y lo recibe así.</param>
/// <param name="Piloto">En el piloto con eleventa: por qué el traspaso no mueve las existencias aquí. Vacío fuera del piloto.</param>
public sealed record TraspasosDto(IReadOnlyList<TraspasoResumenDto> Traspasos, bool PuedeModificar, string? Piloto);

/// <param name="OtraSucursal">A dónde va (salida) o de dónde vino (entrada).</param>
/// <param name="Clave">La de su archivo («4F2A-91C3»); vacía en un borrador.</param>
/// <param name="FechaLocal">Lo último que le pasó: cuándo se armó, se exportó, se importó, se marcó o se recibió.</param>
/// <param name="Usuario">Quién hizo eso último.</param>
/// <param name="Cambios">Del lado que recibe: cuántos renglones cambiaron contra el archivo.</param>
public sealed record TraspasoResumenDto(
    Guid Id, SentidoTraspaso Sentido, EstadoTraspaso Estado, string OtraSucursal, string? Clave, DateTime FechaLocal, string Usuario, int Renglones,
    int Cambios);

/// <summary>Un traspaso con sus renglones.</summary>
/// <param name="Creo">Quién armó la lista o importó el archivo; <paramref name="Cerro"/>, quién lo exportó o lo recibió.</param>
/// <param name="Marco">Quién marcó los cambios para revisión.</param>
/// <param name="MovioExistencias">Al exportar o al recibir se movieron las existencias (en el piloto con eleventa, no).</param>
/// <param name="YaEstaba">Al importar: ese archivo ya se había importado; es el que ya estaba, sin cambiarle nada.</param>
public sealed record TraspasoDto(
    Guid Id, SentidoTraspaso Sentido, EstadoTraspaso Estado, string OtraSucursal, string? Nota, string? Clave, DateTime CreadoLocal, string Creo,
    DateTime? CerradoLocal, string? Cerro, DateTime? MarcadoLocal, string? Marco, bool MovioExistencias, bool PuedeModificar, string? Piloto,
    IReadOnlyList<TraspasoRenglonDto> Renglones, bool YaEstaba = false);

/// <param name="VarianteId">El producto en esta sucursal; vacío si no se empató («sin empatar»).</param>
/// <param name="Producto">Como se llama aquí; vacío si no se empató.</param>
/// <param name="Nombre">Como lo dice el archivo (del lado que manda, como estaba al armarlo).</param>
/// <param name="Enviada">Lo que dice el documento, en su unidad.</param>
/// <param name="Recibida">Del lado que recibe: lo que llegó.</param>
/// <param name="Agregado">Se agregó al recibir: no venía en el archivo.</param>
/// <param name="Cambio">Del lado que recibe: cambió contra el archivo.</param>
/// <param name="Existencia">Lo que hay ahora en esta sucursal; vacío si no se empató.</param>
/// <param name="VarianteDelArchivoId">Del lado que recibe: con qué producto se empató solo al importar; vacío si no se empató.</param>
public sealed record TraspasoRenglonDto(
    Guid Id, Guid? VarianteId, string? Producto, string Nombre, string? Codigo, UnidadBase Unidad, decimal Enviada, decimal Recibida, string? Nota,
    bool Agregado, bool Cambio, decimal? Existencia, Guid? VarianteDelArchivoId = null);

/// <summary>Un renglón de la lista que se arma: el producto y cuántos se mandan, en su unidad base.</summary>
public sealed record RenglonPorEnviar(Guid VarianteId, long CantidadMilesimas);

/// <summary>
/// Guarda el borrador del traspaso que se arma: la lista completa, como está (reemplaza la que hubiera). Con el mismo Id (lo pone
/// la caja), el mismo borrador.
/// </summary>
/// <param name="Destino">A qué sucursal va, por su nombre. Se puede guardar vacío; para exportar hace falta.</param>
public sealed record SolicitudGuardarTraspaso(
    Guid Id, Guid UsuarioId, Guid SucursalId, string? Destino, string? Nota, IReadOnlyList<RenglonPorEnviar> Renglones, Guid? CajaId = null);

/// <summary>Exportar (la mercancía sale y queda el archivo) o descartar el borrador.</summary>
public sealed record SolicitudDeTraspaso(Guid UsuarioId, Guid SucursalId, Guid? CajaId = null);

/// <summary>El archivo de un traspaso exportado, para guardarlo en la memoria: su nombre y sus bytes.</summary>
public sealed record ArchivoDeTraspasoDto(string Nombre, string ContenidoBase64);

/// <summary>El archivo que llegó de la otra sucursal.</summary>
/// <param name="Archivo">Cómo se llama (para la bitácora).</param>
/// <param name="ContenidoBase64">Sus bytes.</param>
public sealed record SolicitudImportarTraspaso(Guid UsuarioId, Guid SucursalId, string Archivo, string ContenidoBase64, Guid? CajaId = null);

/// <summary>Lo que quien revisa dice de un renglón.</summary>
/// <param name="Id">El renglón del archivo; vacío: uno que se agrega (llegó y no venía).</param>
/// <param name="VarianteId">Con qué producto de aquí va; vacío: sin empatar.</param>
/// <param name="RecibidaMilesimas">Lo que llegó, en su unidad base; cero: no llegó (o se quita uno agregado).</param>
public sealed record RenglonRecibido(Guid? Id, Guid? VarianteId, long RecibidaMilesimas, string? Nota);

/// <summary>Qué se hace con lo revisado.</summary>
public enum PasoDeRecepcion
{
    /// <summary>Solo guardarlo: se sigue revisando después.</summary>
    Guardar,

    /// <summary>Quien no puede modificar: deja marcados los cambios para que los revise quien sí.</summary>
    MandarARevision,

    /// <summary>La mercancía entra como quedó. Con cambios contra el archivo, solo quien puede modificar.</summary>
    Recibir,
}

/// <summary>Lo que se revisó de un traspaso que llegó: todos sus renglones, como quedan.</summary>
public sealed record SolicitudRecepcion(Guid UsuarioId, Guid SucursalId, IReadOnlyList<RenglonRecibido> Renglones, PasoDeRecepcion Paso, Guid? CajaId = null);
