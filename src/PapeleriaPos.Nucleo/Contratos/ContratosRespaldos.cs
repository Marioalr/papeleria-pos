using System.Globalization;

namespace PapeleriaPos.Nucleo.Contratos;

/// <summary>«Respaldar ahora» (Configuración › Respaldos, 2026-10-22): quien entró a la caja, con «Configuración de la tienda».</summary>
public sealed record SolicitudRespaldo(Guid UsuarioId);

/// <summary>Un respaldo de la base en la caja principal: dónde quedó, cuánto mide y cuándo se hizo (hora de México).</summary>
public sealed record RespaldoDto(string Ruta, long Bytes, DateTime FechaLocal)
{
    /// <summary>«26.3 MB» o «840 KB», como lo dicen la pantalla y la bitácora.</summary>
    public string Tamano => Bytes < 1024 * 1024
        ? $"{Math.Max(1, Math.Round(Bytes / 1024d)).ToString("0", CultureInfo.InvariantCulture)} KB"
        : $"{(Bytes / 1024d / 1024).ToString("0.0", CultureInfo.InvariantCulture)} MB";
}

/// <summary>
/// Los respaldos de la caja principal, para Configuración › Respaldos (2026-10-22): dónde se guardan, cuántos días, el más
/// reciente (el del día, el de un corte Z o el de «Respaldar ahora») y, si ahora no se hacen, por qué.
/// </summary>
public sealed record RespaldosDto(string Carpeta, int Dias, RespaldoDto? Ultimo, string? NoSeHacenPorque);
