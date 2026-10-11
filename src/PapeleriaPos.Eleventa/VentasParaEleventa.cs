using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que se le manda a eleventa de una venta, o por qué no se manda sola.</summary>
public sealed record VentaArmada(VentaParaEleventa? Venta, string? PorRevisar);

/// <summary>
/// Pasa una venta de la caja nueva a lo que entiende eleventa (docs/migracion-eleventa.md, «Mandar cada venta»): cada
/// línea con el producto de eleventa del que vino (el de la importación o la lectura), y lo demás como artículo común.
/// </summary>
public static class VentasParaEleventa
{
    public static async Task<VentaArmada> ArmarAsync(PosDbContext db, Guid ventaId, CancellationToken ct = default)
    {
        var venta = await db.Ventas.AsNoTracking().Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == ventaId, ct);
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == venta.TurnoId, ct);
        var variantes = venta.Lineas.Where(l => l.VarianteId is not null).Select(l => l.VarianteId!.Value).Distinct().ToList();
        var mapeos = await db.MapeosImportacion.AsNoTracking()
            .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante) && variantes.Contains(m.IdNuevo))
            .ToListAsync(ct);
        // Si la depuración juntó dos productos de eleventa en uno, se usa el primero que se dio de alta allá.
        var productoDe = mapeos.Where(m => int.TryParse(m.IdOrigen, out _)).GroupBy(m => m.IdNuevo)
            .ToDictionary(g => g.Key, g => g.Min(m => int.Parse(m.IdOrigen)));
        return Armar(venta, turno, productoDe);
    }

    /// <param name="productoDe">El <c>PRODUCTOS.ID</c> de eleventa de cada variante que vino de allá.</param>
    public static VentaArmada Armar(Venta venta, Turno turno, IReadOnlyDictionary<Guid, int> productoDe)
    {
        if (venta.Estado == EstadoVenta.Cancelada)
            return new(null, "Se canceló antes de pasar a eleventa.");
        if (venta.Estado != EstadoVenta.Pagada || venta.PagadaUtc is not { } pagada)
            return new(null, "No está cobrada.");
        if (venta.Cobros.FirstOrDefault(c => c.Forma != FormaPago.Efectivo) is { } otra)
            return new(null, $"Fue con {Forma(otra.Forma)}: a eleventa solo pasan solas las ventas en efectivo.");

        var articulos = venta.Lineas
            .Select(l => new ArticuloParaEleventa(l.VarianteId is { } v && productoDe.TryGetValue(v, out var id) ? id : null,
                l.Descripcion, l.CantidadBase.EnUnidades, l.ImporteCentavos / 100m))
            .ToList();
        if (articulos.Count == 0)
            return new(null, "No tiene artículos.");

        // El redondeo del efectivo va en la línea más grande, para que el total de eleventa sea lo que entró a la caja.
        if (venta.RedondeoCentavos != 0)
        {
            var mayor = articulos.IndexOf(articulos.MaxBy(a => a.Importe)!);
            articulos[mayor] = articulos[mayor] with { Importe = articulos[mayor].Importe + venta.RedondeoCentavos / 100m };
        }

        return new(new VentaParaEleventa(
            HoraMexico.ALocal(venta.CreadaUtc), HoraMexico.ALocal(pagada),
            venta.Cobros.Sum(c => c.RecibidoCentavos) / 100m,
            new TurnoParaEleventa(HoraMexico.ALocal(turno.AbiertoUtc), turno.FondoCentavos / 100m),
            articulos), null);
    }

    private static string Forma(FormaPago forma) => forma switch
    {
        FormaPago.Tarjeta => "tarjeta",
        FormaPago.Transferencia => "transferencia",
        FormaPago.Credito => "crédito",
        _ => forma.ToString(),
    };
}
