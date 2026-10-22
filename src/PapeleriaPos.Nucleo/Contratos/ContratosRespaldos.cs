using System.Globalization;

namespace PapeleriaPos.Nucleo.Contratos;

/// <summary>«Respaldar ahora» (Configuración › Respaldos, 2026-10-22): quien entró a la caja, con «Configuración de la tienda».</summary>
public sealed record SolicitudRespaldo(Guid UsuarioId);

/// <summary>Un respaldo de la base en la caja principal: dónde quedó, cuánto mide y cuándo se hizo (hora de México).</summary>
/// <param name="AvisoLimpieza">
/// La copia salió, pero no se pudieron borrar los de hace más días (un archivo ocupado): se dice aparte, no como un respaldo
/// fallido (revisión del PR #133).
/// </param>
public sealed record RespaldoDto(string Ruta, long Bytes, DateTime FechaLocal, string? AvisoLimpieza = null)
{
    /// <summary>«26.3 MB» o «840 KB», como lo dicen la pantalla y la bitácora.</summary>
    public string Tamano => Bytes < 1024 * 1024
        ? $"{Math.Max(1, Math.Round(Bytes / 1024d)).ToString("0", CultureInfo.InvariantCulture)} KB"
        : $"{(Bytes / 1024d / 1024).ToString("0.0", CultureInfo.InvariantCulture)} MB";
}

/// <summary>
/// «Respaldar ahora» se hace en segundo plano (revisión del PR #133): la pantalla pregunta cómo va hasta que termina.
/// </summary>
/// <param name="Haciendo">Todavía se está copiando: no empieza otro.</param>
/// <param name="DesdeLocal">Cuándo se pidió.</param>
/// <param name="Hecho">Ya salió: dónde quedó y cuánto mide.</param>
/// <param name="Fallo">No salió, y por qué.</param>
public sealed record RespaldoAhoraDto(bool Haciendo, DateTime DesdeLocal, RespaldoDto? Hecho = null, string? Fallo = null);

/// <summary>
/// Los respaldos de la caja principal, para Configuración › Respaldos (2026-10-22): dónde se guardan, cuántos días, el más
/// reciente (el del día, el de un corte Z o el de «Respaldar ahora»), si ahora no se hacen, por qué, y cómo va el último
/// «Respaldar ahora».
/// </summary>
public sealed record RespaldosDto(string Carpeta, int Dias, RespaldoDto? Ultimo, string? NoSeHacenPorque, RespaldoAhoraDto? Ahora = null);
