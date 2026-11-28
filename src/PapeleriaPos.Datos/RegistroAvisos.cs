using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Avisos;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los avisos de la caja sobre la base del POS (<see cref="IAvisos"/>, 2026-11-28): cada módulo pone y quita los suyos y las cajas leen los
/// vigentes (<c>GET /api/sucursales/{id}/avisos</c>). No guarda: lo puesto o quitado queda en el contexto y se guarda con lo siguiente que
/// guarde quien lo usa. Ve también lo que se puso en este mismo contexto y todavía no se guarda, para que poner dos veces el mismo aviso
/// antes de guardar no lo duplique.
/// </summary>
public sealed class RegistroAvisos(PosDbContext db) : IAvisos
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<bool> PonerAsync(AvisoNuevo nuevo, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nuevo.Modulo);
        ArgumentException.ThrowIfNullOrWhiteSpace(nuevo.Clave);
        ArgumentException.ThrowIfNullOrWhiteSpace(nuevo.Titulo);
        var vigente = Pendiente(a => a.SucursalId == nuevo.SucursalId && a.Modulo == nuevo.Modulo && a.Clave == nuevo.Clave).FirstOrDefault()
            ?? await db.Avisos.FirstOrDefaultAsync(a => a.SucursalId == nuevo.SucursalId && a.Modulo == nuevo.Modulo && a.Clave == nuevo.Clave
                && a.RetiradoUtc == null, ct);
        var ahora = Ahora;
        if (vigente is null)
        {
            db.Avisos.Add(new Aviso
            {
                SucursalId = nuevo.SucursalId, CajaId = nuevo.CajaId, Modulo = nuevo.Modulo, Clave = nuevo.Clave, Nivel = nuevo.Nivel,
                Titulo = nuevo.Titulo, Texto = nuevo.Texto, Accion = nuevo.Accion, AccionTexto = nuevo.AccionTexto, AccionDatos = nuevo.AccionDatos,
                DesdeUtc = ahora, ActualizadoUtc = ahora,
            });
            return true;
        }
        // El mismo aviso: lo que cambió, sin moverle el «desde» (ni tocar la base si nada cambió).
        if (vigente.Nivel != nuevo.Nivel || vigente.Titulo != nuevo.Titulo || vigente.Texto != nuevo.Texto || vigente.Accion != nuevo.Accion
            || vigente.AccionTexto != nuevo.AccionTexto || vigente.AccionDatos != nuevo.AccionDatos || vigente.CajaId != nuevo.CajaId)
        {
            (vigente.Nivel, vigente.Titulo, vigente.Texto, vigente.Accion, vigente.AccionTexto, vigente.AccionDatos, vigente.CajaId) =
                (nuevo.Nivel, nuevo.Titulo, nuevo.Texto, nuevo.Accion, nuevo.AccionTexto, nuevo.AccionDatos, nuevo.CajaId);
            vigente.ActualizadoUtc = ahora;
        }
        return false;
    }

    public async Task<int> QuitarAsync(Guid sucursalId, string modulo, string prefijo, IReadOnlyCollection<string>? excepto = null, CancellationToken ct = default)
    {
        var guardados = await db.Avisos.Where(a => a.SucursalId == sucursalId && a.Modulo == modulo && a.RetiradoUtc == null && a.Clave.StartsWith(prefijo))
            .ToListAsync(ct);
        var quitar = guardados.Concat(Pendiente(a => a.SucursalId == sucursalId && a.Modulo == modulo && a.Clave.StartsWith(prefijo, StringComparison.Ordinal)))
            .Distinct().Where(a => a.RetiradoUtc is null && excepto?.Contains(a.Clave) != true).ToList();
        var ahora = Ahora;
        foreach (var aviso in quitar)
        {
            if (db.Entry(aviso).State == EntityState.Added)
                db.Avisos.Remove(aviso);
            else
                aviso.RetiradoUtc = ahora;
        }
        return quitar.Count;
    }

    public async Task<IReadOnlyList<Aviso>> VigentesAsync(Guid sucursalId, Guid? cajaId, CancellationToken ct = default)
    {
        var vigentes = await db.Avisos.AsNoTracking().Where(a => a.SucursalId == sucursalId && a.RetiradoUtc == null && (a.CajaId == null || a.CajaId == cajaId))
            .ToListAsync(ct);
        return [.. vigentes.OrderByDescending(a => a.Nivel).ThenBy(a => a.DesdeUtc).ThenBy(a => a.Clave, StringComparer.Ordinal)];
    }

    /// <summary>Los que se pusieron en este contexto y todavía no se guardan.</summary>
    private IEnumerable<Aviso> Pendiente(Func<Aviso, bool> condicion) =>
        db.ChangeTracker.Entries<Aviso>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).Where(condicion);
}
