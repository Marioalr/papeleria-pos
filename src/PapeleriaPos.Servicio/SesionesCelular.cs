using System.Collections.Concurrent;
using System.Security.Cryptography;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Quién está contando desde cada celular. El celular guarda una llave para no pedir el PIN cada vez que la página se
/// recarga (se apagó la pantalla, se cerró la pestaña); dura un turno. Viven en memoria: si el servicio se reinicia, se
/// vuelve a entrar con el PIN.
/// </summary>
public sealed class SesionesCelular(TimeProvider reloj)
{
    public static readonly TimeSpan Duracion = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, (SesionDto Sesion, DateTimeOffset Vence)> _sesiones = new();

    public string Recordar(SesionDto sesion)
    {
        var ahora = reloj.GetUtcNow();
        foreach (var (llave, vencida) in _sesiones.Where(s => s.Value.Vence <= ahora))
            _sesiones.TryRemove(llave, out _);
        var nueva = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sesiones[nueva] = (sesion, ahora + Duracion);
        return nueva;
    }

    public SesionDto? Recuperar(string? llave) =>
        llave is not null && _sesiones.TryGetValue(llave, out var s) && s.Vence > reloj.GetUtcNow() ? s.Sesion : null;

    public void Olvidar(string? llave)
    {
        if (llave is not null)
            _sesiones.TryRemove(llave, out _);
    }
}
