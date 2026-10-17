using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El cobro con la versión de la copia de la caja (entrega 5, docs/propuesta-interfaz.md, «Rendimiento»): la caja escanea
/// contra su copia del catálogo, que casi siempre está al día; si el precio o el mayoreo de algo del ticket cambió después de
/// la versión con que la caja puso sus precios, el servicio no cobra con precios que el cajero no vio.
/// </summary>
public static class PreciosDeLaCopia
{
    /// <summary>
    /// Las variantes del ticket cuyo precio o mayoreo cambió después de la versión de cada línea
    /// (<see cref="LineaSolicitada.VersionCatalogo"/>, o la del ticket): un precio o una escala que se agregó, cambió o se
    /// quitó (el historial de precios, con su versión), una escala que cambió desde cuánto aplica, o la presentación de la
    /// línea (cuánto trae). Solo los precios de todas las sucursales y los de la del ticket.
    /// </summary>
    /// <param name="actual">
    /// La versión del catálogo con que se arma la venta (la del catálogo en memoria). Lo de después todavía no está en él
    /// (la recarga va en camino, entrega 5, parte 2): la venta se arma y se revisa con esa, sin decir «cambió» una y otra vez
    /// con los mismos precios.
    /// </param>
    public static async Task<List<Guid>> CambiadosAsync(
        PosDbContext db, Guid sucursalId, IReadOnlyList<LineaSolicitada> lineas, long versionDelTicket, long actual, CancellationToken ct = default)
    {
        var desdeDe = lineas.Where(l => l.VarianteId is not null)
            .GroupBy(l => l.VarianteId!.Value)
            .Select(g => (Variante: g.Key, Desde: g.Min(l => l.VersionCatalogo ?? versionDelTicket)))
            .Where(x => x.Desde < actual)
            .ToDictionary(x => x.Variante, x => x.Desde);
        if (desdeDe.Count == 0)
            return [];
        var variantes = desdeDe.Keys.ToList();
        var desde = desdeDe.Values.Min();
        var presentaciones = lineas.Where(l => l.VarianteId is { } v && desdeDe.ContainsKey(v) && l.PresentacionId is not null)
            .Select(l => l.PresentacionId!.Value).Distinct().ToList();

        var historial = await db.PreciosHistorial.AsNoTracking()
            .Where(h => variantes.Contains(h.VarianteId) && h.VersionCatalogo > desde && h.VersionCatalogo <= actual
                        && (h.SucursalId == null || h.SucursalId == sucursalId))
            .Select(h => new { h.VarianteId, Version = h.VersionCatalogo!.Value })
            .ToListAsync(ct);
        var precios = await db.Precios.AsNoTracking()
            .Where(p => variantes.Contains(p.VarianteId) && p.Version > desde && p.Version <= actual && (p.SucursalId == null || p.SucursalId == sucursalId))
            .Select(p => new { p.VarianteId, p.Version })
            .ToListAsync(ct);
        var deLasPresentaciones = await db.Presentaciones.AsNoTracking()
            .Where(p => presentaciones.Contains(p.Id) && p.Version > desde && p.Version <= actual)
            .Select(p => new { p.VarianteId, p.Version })
            .ToListAsync(ct);
        return historial.Concat(precios).Concat(deLasPresentaciones)
            .Where(x => x.Version > desdeDe[x.VarianteId])
            .Select(x => x.VarianteId)
            .Distinct()
            .ToList();
    }

    /// <summary>«Cambió el precio de «Lapicero azul» y «Cuaderno» en la caja principal: …» (la caja dice lo suyo al ponerse al día).</summary>
    public static string Mensaje(IReadOnlyList<ProductoVentaDto> productos)
    {
        var nombres = productos.Select(p => $"«{p.Descripcion}»").ToList();
        var lista = nombres.Count switch
        {
            0 => "algo del ticket",
            1 => nombres[0],
            _ => $"{string.Join(", ", nombres.Take(nombres.Count - 1))} y {nombres[^1]}",
        };
        return $"Cambió el precio de {lista} en la caja principal: revisa el total y cobra otra vez.";
    }
}
