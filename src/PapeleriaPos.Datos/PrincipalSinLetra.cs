using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Al arrancar el servicio (2026-11-22, <see cref="Folios"/>): la caja de la PC del servicio es la caja principal y se queda sin letra.
/// La migración ya quitó la letra de la caja marcada como principal (la de «Tienda nueva»); esta es la de una base importada de eleventa,
/// como la de la sucursal 2, donde la caja de esa PC se dio de alta como «Caja nueva» con la serie A. Lo que ya se cobró no cambia.
/// </summary>
public static class PrincipalSinLetra
{
    /// <summary>
    /// Si hay una sola caja activa dada de alta desde <paramref name="equipo"/> (el nombre de esta PC) y en su sucursal ninguna otra está
    /// sin letra, le quita la suya y la marca como principal. Se puede llamar cada vez: si ya está, no hace nada.
    /// </summary>
    /// <returns>La caja y la letra que tenía, si se cambió; vacío si no.</returns>
    public static async Task<(Caja Caja, string LetraDeAntes)?> AplicarAsync(PosDbContext db, string equipo, CancellationToken ct = default)
    {
        var deEstaPc = (await db.Cajas.Where(c => c.Activa && c.LlaveHuella != null && c.Equipo != null).ToListAsync(ct))
            .Where(c => string.Equals(c.Equipo, equipo, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (deEstaPc is not [var caja] || caja.Serie == Folios.SinLetra)
            return null;
        if (await db.Cajas.AnyAsync(c => c.SucursalId == caja.SucursalId && c.Serie == Folios.SinLetra, ct))
            return null;
        var antes = caja.Serie;
        caja.Serie = Folios.SinLetra;
        caja.EsPrincipal = true;
        await db.SaveChangesAsync(ct);
        return (caja, antes);
    }
}
