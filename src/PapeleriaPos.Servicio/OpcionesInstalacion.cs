using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// «Instalacion» en servicio.json (C:\ProgramData\PapeleriaPos\servicio.json): el papel de esta PC, como se eligió en el instalador
/// (2026-10-31). Sin la sección, o con <c>false</c>, es la caja principal, como hasta hoy: las demás cajas y los celulares se conectan
/// a ella. «Otra caja» no tiene servicio, así que no tiene servicio.json.
/// </summary>
public sealed class OpcionesInstalacion
{
    /// <summary>
    /// «La única caja»: el servicio y la caja en esta PC, y ninguna otra caja se da de alta desde otra PC. No contesta a quien busca
    /// el servicio en la red (UDP 5081) y <c>/api/alta</c> no acepta otras PC; la caja de esta PC trabaja como siempre, y los celulares
    /// (TCP 5080) también. Para conectar otra caja, se corre otra vez el instalador y se elige «Caja principal».
    /// </summary>
    public bool UnaSolaCaja { get; set; }

    /// <summary>
    /// Con «La única caja», las cajas que ya estaban dadas de alta en otras PC (revisión del 2026-10-31): siguen vendiendo, pero sin la
    /// búsqueda en la red ya no encuentran solas a esta caja principal si le cambia la IP. El servicio lo avisa al arrancar (su
    /// registro). Vacío si no hay ninguna: las de esta misma PC (<paramref name="equipo"/>) no cuentan.
    /// </summary>
    public static async Task<string?> AvisoDeOtrasCajasAsync(PosDbContext db, string equipo, CancellationToken ct = default)
    {
        var otras = (await db.Cajas.AsNoTracking().Where(c => c.Activa && c.LlaveHuella != null && c.Equipo != null)
                .Select(c => new { c.Nombre, c.Equipo }).ToListAsync(ct))
            .Where(c => !string.Equals(c.Equipo, equipo, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return otras.Count == 0
            ? null
            : $"Esta caja principal se instaló como «La única caja», pero {(otras.Count == 1 ? "hay una caja dada" : $"hay {otras.Count} cajas dadas")} de alta en otras PC "
              + $"({string.Join(", ", otras.Select(c => $"{c.Nombre} en «{c.Equipo}»"))}): siguen vendiendo, pero sin la búsqueda en la red ya no "
              + "encuentran solas a esta si le cambia la IP. Si se van a seguir usando, corre otra vez el instalador y elige «Caja principal».";
    }
}
