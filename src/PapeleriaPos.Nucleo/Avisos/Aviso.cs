namespace PapeleriaPos.Nucleo.Avisos;

/// <summary>
/// Qué tan pronto hay que atender un aviso: lo que se ve en el «!» de arriba de la caja (2026-11-28). Con <see cref="Urgente"/> el «!» se
/// pone rojo; con <see cref="Atencion"/>, ámbar; con solo <see cref="Informativo"/> se queda atenuado y sin cuenta, pero el aviso está en
/// su menú.
/// </summary>
public enum NivelAviso
{
    /// <summary>Algo que conviene saber o hacer cuando se pueda («anota cuántas hojas pones la próxima vez»).</summary>
    Informativo,

    /// <summary>Hay que atenderlo pronto («quedan unas 40 hojas: alcanzan para una hora»).</summary>
    Atencion,

    /// <summary>Ya estorba o va a estorbar enseguida («la charola se quedó sin papel»).</summary>
    Urgente,
}

/// <summary>
/// Un aviso de un módulo en la caja (el «!» de arriba, 2026-11-28): lo pone el módulo que lo sabe (las impresiones, el conteo, el
/// piloto con eleventa…) por <see cref="IAvisos"/> y lo quita él mismo cuando deja de valer. Cada uno dice de quién es
/// (<see cref="Modulo"/>, con su nombre y su icono en <see cref="ModulosDeAvisos"/>), qué tan pronto (<see cref="Nivel"/>), qué pasa
/// (<see cref="Titulo"/> y <see cref="Texto"/>), desde cuándo (<see cref="DesdeUtc"/>) y, si la hay, qué se puede hacer desde la caja
/// (<see cref="Accion"/>). No es Telegram, que está en pausa: es el puerto genérico de los avisos nuevos.
/// <para>
/// Un módulo identifica cada aviso con su <see cref="Clave"/> («papel:…:1»): ponerlo otra vez con la misma clave lo cambia sin
/// moverle el «desde» (sigue siendo el mismo aviso); quitarlo le pone <see cref="RetiradoUtc"/> y se queda en la base como historia.
/// No viaja a la copia de las cajas: las cajas lo piden a la caja principal.
/// </para>
/// </summary>
public class Aviso
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>Solo para esa caja (la que imprimió, por ejemplo); vacío: lo ven todas las cajas de la sucursal.</summary>
    public Guid? CajaId { get; set; }

    /// <summary>De quién es: la clave del módulo en <see cref="ModulosDeAvisos"/> («impresiones»).</summary>
    public required string Modulo { get; set; }

    /// <summary>Cómo lo reconoce su módulo para cambiarlo o quitarlo; única entre los vigentes del módulo en la sucursal.</summary>
    public required string Clave { get; set; }

    public NivelAviso Nivel { get; set; }

    /// <summary>Lo primero que se lee, corto («WF-6590 2 · quedan ~40 hojas»).</summary>
    public required string Titulo { get; set; }

    /// <summary>El porqué y qué hacer, en una o dos frases (puede ir vacío).</summary>
    public string Texto { get; set; } = "";

    /// <summary>
    /// Lo que se puede hacer desde el aviso, como lo entiende la caja («impresiones.papel»); vacío: solo se lee. Una caja que no conoce
    /// la acción enseña el aviso sin botón.
    /// </summary>
    public string? Accion { get; set; }

    /// <summary>El texto del botón de la acción («Anotar el papel que pusiste»).</summary>
    public string? AccionTexto { get; set; }

    /// <summary>Lo que la acción necesita para abrirse donde va (el número de la impresora y la charola: «2:1»).</summary>
    public string? AccionDatos { get; set; }

    /// <summary>Desde cuándo vale: la primera vez que se puso (no cambia al ponerlo otra vez).</summary>
    public DateTime DesdeUtc { get; set; }

    /// <summary>La última vez que su módulo lo puso o le cambió algo.</summary>
    public DateTime ActualizadoUtc { get; set; }

    /// <summary>Cuándo lo quitó su módulo; vacío mientras vale.</summary>
    public DateTime? RetiradoUtc { get; set; }
}

/// <summary>Un módulo que avisa en la caja: su clave, cómo se le dice y su icono (de los de la caja, <c>Iconos.razor</c>).</summary>
public sealed record ModuloDeAvisos(string Clave, string Nombre, string Icono);

/// <summary>
/// Los módulos que avisan en la caja (2026-11-28). Hoy solo las impresiones; los demás ya tienen su lugar para cuando lleguen
/// («no se ha contado nada», el piloto automático con eleventa). Un módulo que no está aquí se enseña con su clave y el icono de alerta.
/// </summary>
public static class ModulosDeAvisos
{
    public static readonly ModuloDeAvisos Impresiones = new("impresiones", "Impresiones", "impresora");
    public static readonly ModuloDeAvisos Inventario = new("inventario", "Inventario", "cajas");
    public static readonly ModuloDeAvisos Eleventa = new("eleventa", "Piloto con eleventa", "alerta");

    public static IReadOnlyList<ModuloDeAvisos> Todos { get; } = [Impresiones, Inventario, Eleventa];

    public static ModuloDeAvisos De(string clave) =>
        Todos.FirstOrDefault(m => string.Equals(m.Clave, clave, StringComparison.OrdinalIgnoreCase)) ?? new(clave, clave, "alerta");
}

/// <summary>Lo que un módulo pone: todo el aviso menos lo que lleva la cuenta (el «desde», la última vez y si se quitó).</summary>
public sealed record AvisoNuevo(
    Guid SucursalId, string Modulo, string Clave, NivelAviso Nivel, string Titulo, string Texto = "",
    string? Accion = null, string? AccionTexto = null, string? AccionDatos = null, Guid? CajaId = null);

/// <summary>
/// El registro de los avisos de la caja (2026-11-28): donde cada módulo pone y quita los suyos. No guarda: lo puesto o quitado se guarda
/// con lo siguiente que guarde quien lo usa, en la misma transacción que lo que lo causó (como los avisos del módulo de impresiones).
/// </summary>
public interface IAvisos
{
    /// <summary>
    /// Pone el aviso, o lo cambia si su módulo ya tiene uno vigente con esa clave (sin moverle el «desde»). Regresa verdadero si es nuevo.
    /// </summary>
    Task<bool> PonerAsync(AvisoNuevo aviso, CancellationToken ct = default);

    /// <summary>
    /// Quita los avisos vigentes del módulo cuya clave empieza con <paramref name="prefijo"/> (una clave entera, o «papel:2:» para los de esa
    /// impresora), menos los de <paramref name="excepto"/>. Regresa cuántos quitó.
    /// </summary>
    Task<int> QuitarAsync(Guid sucursalId, string modulo, string prefijo, IReadOnlyCollection<string>? excepto = null, CancellationToken ct = default);

    /// <summary>Los vigentes que ve esa caja de la sucursal (los de todas y los suyos), del más urgente al más tranquilo y del más viejo al más nuevo.</summary>
    Task<IReadOnlyList<Aviso>> VigentesAsync(Guid sucursalId, Guid? cajaId, CancellationToken ct = default);
}

/// <summary>Un aviso como lo ve la caja: de quién es (su módulo, con nombre e icono), su nivel, qué dice, desde cuándo (hora de México) y su acción.</summary>
public sealed record AvisoDto(
    Guid Id, string Modulo, string ModuloNombre, string Icono, string Clave, NivelAviso Nivel, string Titulo, string Texto,
    string? Accion, string? AccionTexto, string? AccionDatos, DateTime DesdeLocal)
{
    public static AvisoDto De(Aviso a)
    {
        var modulo = ModulosDeAvisos.De(a.Modulo);
        return new(a.Id, a.Modulo, modulo.Nombre, modulo.Icono, a.Clave, a.Nivel, a.Titulo, a.Texto, a.Accion, a.AccionTexto, a.AccionDatos,
            HoraMexico.ALocal(a.DesdeUtc));
    }
}
