using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// A nombre de quién va en eleventa lo que se pone en la bandeja (el piloto alfa, 2026-10-25): el número de eleventa
/// (<c>USUARIOS.ID</c>) de quien lo hizo aquí, tomado en ese momento y guardado con lo que se manda
/// (<see cref="EnvioAEleventa.CajeroEleventa"/>). Vacío si no viene de eleventa: el puente lo manda a nombre del cajero de la
/// configuración y lo anota en el registro del servicio. Solo del piloto: se quita al dejar eleventa.
/// </summary>
public static class CajeroDeEleventa
{
    public static async Task<int?> DeAsync(PosDbContext db, Guid usuarioId, CancellationToken ct = default) =>
        await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => u.EleventaId).FirstOrDefaultAsync(ct);
}
