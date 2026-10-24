using Microsoft.EntityFrameworkCore;

namespace PapeleriaPos.Datos;

/// <summary>
/// El «último costo» de cada variante, uno solo para todo el POS (revisión del PR #138): lo último que se supo que costaba, por
/// unidad base y en centavos. Es el más reciente de dos: el último costo de un proveedor (el que deja una compra cerrada, o la
/// importación de eleventa, que no trae fecha y cuenta como el más viejo) y el de la última entrada de «Agregar inventario» con
/// costo de verdad (uno escrito, o el de ese momento si lo había: lo que se agregó a $0 porque no se conocía ninguno, no
/// cuenta). En la tienda casi todo entra con «Agregar inventario», sin proveedor: sin esto, lo que solo entra por ahí no tendría
/// costo para el resto del POS. No se inventa un proveedor: cada uno se queda en su tabla.
/// <para>
/// Lo usan la venta sin existencia (el costo de su salida), la revisión de lo que se vende debajo del costo (también la de las
/// promociones), los ajustes, las mermas, los conteos y abrir un paquete sin existencia, el costo de ahora al agregar
/// inventario, lo que llega en una compra sin estar en su nota, el costo de hoy de la temporada, el sugerido de compra de lo
/// que no tiene costo de su proveedor y el diagnóstico de la base.
/// </para>
/// </summary>
public static class UltimosCostos
{
    /// <summary>El de cada una de <paramref name="variantes"/> que tiene alguno; las que no, no están.</summary>
    public static Task<Dictionary<Guid, long>> DeAsync(PosDbContext db, IReadOnlyCollection<Guid> variantes, CancellationToken ct = default) =>
        variantes.Count == 0 ? Task.FromResult(new Dictionary<Guid, long>()) : CalcularAsync(db, variantes, ct);

    /// <summary>El de una variante; vacío si no tiene ninguno.</summary>
    public static async Task<long?> DeAsync(PosDbContext db, Guid variante, CancellationToken ct = default) =>
        (await DeAsync(db, [variante], ct)).TryGetValue(variante, out var costo) ? costo : null;

    /// <summary>El de todas las variantes que tienen alguno (el diagnóstico de la base, que las revisa todas).</summary>
    public static Task<Dictionary<Guid, long>> DeTodasAsync(PosDbContext db, CancellationToken ct = default) => CalcularAsync(db, null, ct);

    private static async Task<Dictionary<Guid, long>> CalcularAsync(PosDbContext db, IReadOnlyCollection<Guid>? variantes, CancellationToken ct)
    {
        var proveedores = db.ProveedoresVariantes.AsNoTracking().Where(pv => pv.UltimoCostoCentavos != null);
        var entradas = db.EntradasInventario.AsNoTracking().Where(e => e.CostoEscrito || e.CostoUnitarioCentavos > 0);
        if (variantes is not null)
        {
            proveedores = proveedores.Where(pv => variantes.Contains(pv.VarianteId));
            entradas = entradas.Where(e => variantes.Contains(e.VarianteId));
        }
        var deProveedores = await proveedores.Select(pv => new { pv.VarianteId, Fecha = pv.UltimaCompraUtc, Costo = pv.UltimoCostoCentavos!.Value }).ToListAsync(ct);
        var deEntradas = await entradas.Select(e => new { e.VarianteId, Fecha = e.FechaUtc, Costo = e.CostoUnitarioCentavos }).ToListAsync(ct);
        // El más reciente de todos; sin fecha (la importación), el más viejo. En un empate, lo agregado.
        return deProveedores.Select(c => (c.VarianteId, Fecha: c.Fecha ?? DateTime.MinValue, c.Costo, Entrada: false))
            .Concat(deEntradas.Select(c => (c.VarianteId, c.Fecha, c.Costo, Entrada: true)))
            .GroupBy(c => c.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Fecha).ThenByDescending(c => c.Entrada).First().Costo);
    }
}
