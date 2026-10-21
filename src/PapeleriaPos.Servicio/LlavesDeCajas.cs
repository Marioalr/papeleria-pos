using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las llaves de las cajas dadas de alta, en memoria (entrega 5, parte 2: nada pesado dentro de una petición): cada llamada de
/// una caja (el latido cada 5 s, el aviso, el cobro) ya no lee la base para saber qué caja es. Dar de alta o quitar un equipo
/// las olvida al momento (<see cref="Olvidar"/>); lo que cambie otro programa en la base, a lo más en <see cref="Vigencia"/>.
/// Una llave que no es de ninguna caja no se recuerda: se vuelve a buscar cada vez.
/// </summary>
public sealed class LlavesDeCajas
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (Guid Caja, long Desde)> _conocidas = new();

    /// <summary>La caja activa de esa huella; vacía si no es de ninguna.</summary>
    public async Task<Guid?> CajaAsync(PosDbContext db, string huella, CancellationToken ct = default)
    {
        if (_conocidas.TryGetValue(huella, out var conocida) && Stopwatch.GetElapsedTime(conocida.Desde) < Vigencia)
            return conocida.Caja;
        var cajaId = await db.Cajas.AsNoTracking().Where(c => c.LlaveHuella == huella && c.Activa).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
        if (cajaId is { } id)
            _conocidas[huella] = (id, Stopwatch.GetTimestamp());
        else
            _conocidas.TryRemove(huella, out _);
        return cajaId;
    }

    /// <summary>Cambió el equipo de una caja (alta, quitar) o la base entera: se vuelven a leer.</summary>
    public void Olvidar() => _conocidas.Clear();
}
