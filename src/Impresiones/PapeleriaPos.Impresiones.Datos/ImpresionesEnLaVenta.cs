using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Lo que la venta del POS hace con las impresiones, y solo por aquí (la fachada): cargar las que se cobran en un ticket,
/// dejarlas cobradas con la venta, regresarlas a por cobrar si la venta se cancela o se devuelve, y lo que de ellas va en
/// el resumen del día. El cobro, las devoluciones y el resumen no tocan las tablas del módulo: así el módulo puede
/// cambiar sus tablas y su lógica sin tocar la venta, mientras esto siga diciendo lo mismo.
/// <para>
/// Nada de aquí guarda: va dentro de la transacción de la venta (o de la devolución) y la guarda quien llama, así las
/// impresiones quedan cobradas, o vuelven a estar por cobrar, junto con la venta o con nada.
/// </para>
/// </summary>
public sealed class ImpresionesEnLaVenta(DbContext db, IPosParaImpresiones pos)
{
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();

    /// <summary>Los trabajos que se van a cobrar en un ticket: todos tienen que existir en la sucursal y seguir por cobrar.</summary>
    public async Task<List<TrabajoImpresion>> ParaCobrarAsync(Guid sucursalId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var trabajos = await Trabajos.Where(t => ids.Contains(t.Id) && t.SucursalId == sucursalId).ToListAsync(ct);
        if (trabajos.Count != ids.Distinct().Count())
            throw new InvalidOperationException("Una de las impresiones ya no existe.");
        if (trabajos.FirstOrDefault(t => t.Cobro != EstadoCobroImpresion.PorCobrar) is { } cobrada)
            throw new InvalidOperationException($"«{cobrada.Documento}» ya no está por cobrar: quítala del ticket.");
        return trabajos;
    }

    /// <summary>Los que van en la línea «Impresiones» quedan cobrados con la venta (los que cargó <see cref="ParaCobrarAsync"/>).</summary>
    public void Cobrar(IEnumerable<TrabajoImpresion> trabajos, Guid ventaId, DateTime ahoraUtc)
    {
        foreach (var trabajo in trabajos)
            trabajo.Cobrar(ventaId, ahoraUtc);
    }

    /// <summary>Si se cancela la venta o se devuelve su línea «Impresiones», sus trabajos vuelven a estar por cobrar.</summary>
    public async Task LiberarAsync(Guid ventaId, CancellationToken ct = default)
    {
        foreach (var trabajo in await Trabajos.Where(t => t.VentaId == ventaId).ToListAsync(ct))
            trabajo.Liberar();
    }

    /// <summary>
    /// Lo de las impresiones en el resumen del día (hora de México): las que se marcaron no vendidas, con su costo (es
    /// pérdida), y lo que contó la impresora contra lo registrado. Lo cobrado ya va en las ventas (la línea «Impresiones»).
    /// </summary>
    public async Task<ImpresionesDelDia> ResumenDelDiaAsync(Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var noVendidas = await Trabajos.AsNoTracking()
            .Where(i => i.SucursalId == sucursalId && i.Cobro == EstadoCobroImpresion.NoVendida && i.CobroUtc >= desde && i.CobroUtc < hasta)
            .Select(i => i.CostoCentavos).ToListAsync(ct);
        var contador = await new ServicioImpresora(db, pos).ComparacionAsync(sucursalId, fecha, ct);
        return new ImpresionesDelDia(noVendidas.Count, noVendidas.Sum(), contador);
    }
}

/// <summary>Las impresiones en el resumen del día.</summary>
/// <param name="NoVendidas">Las que se marcaron no vendidas ese día.</param>
/// <param name="CostoNoVendidasCentavos">Lo que costaron: es pérdida.</param>
/// <param name="Contador">Lo que contó la impresora contra lo registrado; vacío sin dos lecturas del contador para ese día.</param>
public sealed record ImpresionesDelDia(int NoVendidas, long CostoNoVendidasCentavos, CounterCheck? Contador);
