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
    public string Tamano => TamanoDe(Bytes);

    /// <summary>«26.3 MB» o «840 KB».</summary>
    public static string TamanoDe(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, Math.Round(bytes / 1024d)).ToString("0", CultureInfo.InvariantCulture)} KB"
        : $"{(bytes / 1024d / 1024).ToString("0.0", CultureInfo.InvariantCulture)} MB";
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
/// <param name="Restauracion">
/// Restaurar un respaldo (2026-10-23): la que se pidió y espera a que la caja principal vuelva a arrancar, o la que se hizo (o
/// no se pudo hacer) al arrancar esta vez. Vacía si no hay ninguna.
/// </param>
public sealed record RespaldosDto(
    string Carpeta, int Dias, RespaldoDto? Ultimo, string? NoSeHacenPorque, RespaldoAhoraDto? Ahora = null, RestauracionDto? Restauracion = null);

// ── Restaurar un respaldo (Configuración › Respaldos, 2026-10-23) ──

/// <summary>De qué es un respaldo, por su nombre (<c>pos-AAAA-MM-DD[-etiqueta].db</c>).</summary>
public enum TipoRespaldo
{
    /// <summary>La copia del día (<c>pos-AAAA-MM-DD.db</c>).</summary>
    Diario,

    /// <summary>La de un corte Z (<c>pos-AAAA-MM-DD-z-HHmm.db</c>).</summary>
    CorteZ,

    /// <summary>La de «Respaldar ahora» (<c>pos-AAAA-MM-DD-ahora-HHmm.db</c>).</summary>
    Ahora,

    /// <summary>Otra que alguien dejó en la carpeta con el mismo principio (<c>pos-…</c>).</summary>
    Otro,
}

/// <summary>Un respaldo de la carpeta de la caja principal, para elegir cuál restaurar.</summary>
/// <param name="Archivo">Su nombre, sin la carpeta: es lo que se manda para restaurarlo.</param>
/// <param name="FechaLocal">Cuándo se hizo (hora de México).</param>
/// <param name="DeAntesDelApagon">
/// La base no pasó la revisión al arrancar y este es el respaldo de antes del apagón, el que dice el aviso de la franja.
/// </param>
public sealed record RespaldoGuardadoDto(string Archivo, TipoRespaldo Tipo, DateTime FechaLocal, long Bytes, bool DeAntesDelApagon = false)
{
    public string Tamano => RespaldoDto.TamanoDe(Bytes);

    /// <summary>«Diario», «Corte Z», «Respaldar ahora» u «Otro», como lo dice la lista.</summary>
    public string Cual => Nombre(Tipo);

    public static string Nombre(TipoRespaldo tipo) => tipo switch
    {
        TipoRespaldo.Diario => "Diario",
        TipoRespaldo.CorteZ => "Corte Z",
        TipoRespaldo.Ahora => "Respaldar ahora",
        _ => "Otro",
    };
}

/// <summary>Lo que se puede restaurar, con lo que dice la advertencia.</summary>
/// <param name="Respaldos">Los de la carpeta de respaldos, el más reciente primero.</param>
/// <param name="ReinicioAutomatico">
/// La caja principal corre como servicio de Windows: se detiene y Windows la vuelve a arrancar sola, ya con el respaldo. Si no (la
/// caja del navegador, en desarrollo), se restaura la próxima vez que se arranque.
/// </param>
/// <param name="PilotoEleventa">El puente escribe en eleventa (el piloto): la advertencia dice qué pasa con eleventa.</param>
public sealed record RestaurablesDto(IReadOnlyList<RespaldoGuardadoDto> Respaldos, bool ReinicioAutomatico, bool PilotoEleventa);

/// <summary>Restaurar un respaldo: quien entró a la caja, con «Configuración de la tienda», y otra vez su PIN.</summary>
/// <param name="Archivo">El nombre del respaldo, como lo da la lista (<see cref="RespaldoGuardadoDto.Archivo"/>).</param>
public sealed record SolicitudRestaurar(Guid UsuarioId, string Pin, string Archivo);

/// <summary>Una restauración: pedida (espera a que la caja principal vuelva a arrancar), hecha o que no se pudo hacer.</summary>
/// <param name="Archivo">El respaldo.</param>
/// <param name="Tipo">De qué es el respaldo.</param>
/// <param name="FechaLocal">Cuándo se hizo el respaldo.</param>
/// <param name="PedidaLocal">Cuándo se pidió restaurarlo.</param>
/// <param name="Pendiente">Pedida, y la caja principal todavía no vuelve a arrancar para restaurarlo.</param>
/// <param name="ReinicioAutomatico">Windows la vuelve a arrancar sola; si no, se restaura la próxima vez que se arranque.</param>
/// <param name="HechaLocal">Cuándo quedó.</param>
/// <param name="BaseDeAntes">La carpeta donde quedó la base que había (<c>antes-de-restaurar-…</c>): no se borra.</param>
/// <param name="Fallo">Por qué no se restauró: la base de ahora se quedó como estaba.</param>
public sealed record RestauracionDto(
    string Archivo, TipoRespaldo Tipo, DateTime FechaLocal, DateTime PedidaLocal, bool Pendiente, bool ReinicioAutomatico,
    DateTime? HechaLocal = null, string? BaseDeAntes = null, string? Fallo = null)
{
    /// <summary>«el respaldo del 22 de octubre a las 21:31 (corte Z)».</summary>
    public string Descripcion =>
        $"el respaldo del {FechaLocal.ToString("d 'de' MMMM 'a las' HH:mm", Cultura)} ({RespaldoGuardadoDto.Nombre(Tipo).ToLowerInvariant()})";

    private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("es-MX");
}
