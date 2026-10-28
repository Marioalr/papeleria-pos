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
    private DbSet<TrabajoControlPrintCobrado> CobradosDeControlPrint => db.Set<TrabajoControlPrintCobrado>();

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

    /// <summary>
    /// Si se cancela la venta o se devuelve su línea «Impresiones», sus trabajos vuelven a estar por cobrar; los de Control Print
    /// dejan de contar como cobrados aquí (si allá siguen por cobrar, su tarjeta vuelve a salir).
    /// </summary>
    public async Task LiberarAsync(Guid ventaId, CancellationToken ct = default)
    {
        foreach (var trabajo in await Trabajos.Where(t => t.VentaId == ventaId).ToListAsync(ct))
            trabajo.Liberar();
        var ahora = pos.Reloj.GetUtcNow().UtcDateTime;
        foreach (var cobrado in await CobradosDeControlPrint.Where(c => c.VentaId == ventaId && c.LiberadoUtc == null).ToListAsync(ct))
            cobrado.LiberadoUtc = ahora;
    }

    // ── Control Print (solo se lee: 2026-10-25) ──────────────────────────────

    /// <summary>
    /// De esos trabajos de Control Print, los que esta caja ya cobró y siguen cobrados (sin liberar): sus tarjetas ya no se
    /// muestran, aunque allá sigan «por cobrar».
    /// </summary>
    public async Task<List<TrabajoControlPrintCobrado>> CobradosAsync(Guid sucursalId, IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
        ids.Count == 0
            ? []
            : await CobradosDeControlPrint.AsNoTracking()
                .Where(c => c.SucursalId == sucursalId && ids.Contains(c.TrabajoId) && c.LiberadoUtc == null)
                .ToListAsync(ct);

    /// <summary>
    /// Los de Control Print que van en la línea «Impresiones» quedan cobrados aquí, con la venta (ya revisados allá: siguen por
    /// cobrar y con ese precio). En Control Print siguen «por cobrar»: el POS nunca le escribe.
    /// </summary>
    public void CobrarControlPrint(Guid sucursalId, IEnumerable<TrabajoControlPrintParaCobrar> trabajos, Guid ventaId, DateTime ahoraUtc)
    {
        foreach (var t in trabajos)
            CobradosDeControlPrint.Add(new TrabajoControlPrintCobrado
            {
                SucursalId = sucursalId, TrabajoId = t.Id, DetectadoUtc = t.DetectadoUtc, VentaId = ventaId, Documento = Recortar(t.Documento, 200),
                Pc = Recortar(t.Pc, 100), PrecioCentavos = t.PrecioCentavos, CostoCentavos = t.CostoCentavos, CobradoUtc = ahoraUtc,
            });
    }

    /// <summary>Lo que se cobró de Control Print en una venta (los vigentes y los ya liberados).</summary>
    public async Task<List<TrabajoControlPrintCobrado>> DeControlPrintEnLaVentaAsync(Guid ventaId, CancellationToken ct = default) =>
        await CobradosDeControlPrint.AsNoTracking().Where(c => c.VentaId == ventaId).OrderBy(c => c.TrabajoId).ToListAsync(ct);

    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];

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
