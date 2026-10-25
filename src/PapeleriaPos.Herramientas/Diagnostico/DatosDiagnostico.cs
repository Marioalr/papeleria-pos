using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Herramientas.Diagnostico;

/// <summary>Una variante con todo lo que el diagnóstico necesita mostrar de ella.</summary>
public sealed record Articulo(
    Guid VarianteId, string Departamento, string Producto, string? Marca, string Variante, TipoProducto Tipo,
    UnidadBase Unidad, string? Codigo)
{
    public string Nombre => string.Join(" ", new[] { Producto, Marca, Variante }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Recargas y pagos de servicios: el dinero es del proveedor, no es venta propia.</summary>
    public bool EsCobroTerceros => Tipo == TipoProducto.CobroTerceros;
}

public sealed record LineaVendida(Guid VarianteId, Guid VentaId, DateTime FechaLocal, Cantidad Cantidad, long ImporteCentavos, long CostoCentavos);

public sealed record Venta(Guid Id, DateTime FechaLocal, long TotalCentavos, bool Cancelada);

public sealed record PrecioBase(Guid VarianteId, string? Presentacion, Cantidad Factor, Cantidad CantidadMinima, long PrecioCentavos);

public sealed record Inventario(Guid VarianteId, Cantidad Cantidad, long CostoCentavos)
{
    /// <summary>Centavos por unidad base. Vacío si no hay existencia.</summary>
    public decimal? CostoPromedio => Cantidad.EsPositiva ? CostoCentavos * Cantidad.Escala / (decimal)Cantidad.Milesimas : null;
}

/// <summary>Lo que se lee de la base para el diagnóstico. Las fechas ya vienen en hora de México.</summary>
public sealed class DatosDiagnostico
{
    public required Dictionary<Guid, Articulo> Articulos { get; init; }
    public required List<Venta> Ventas { get; init; }

    /// <summary>Solo de ventas no canceladas.</summary>
    public required List<LineaVendida> Lineas { get; init; }

    /// <summary>Suma de todas las sucursales.</summary>
    public required Dictionary<Guid, Inventario> Existencias { get; init; }

    /// <summary>Precios base (no los de una sucursal).</summary>
    public required List<PrecioBase> Precios { get; init; }

    /// <summary>Centavos por unidad base, el más reciente de cualquier proveedor.</summary>
    public required Dictionary<Guid, long> UltimoCosto { get; init; }

    /// <summary>Solo las ventas desde ese día; el catálogo y las existencias quedan igual.</summary>
    public DatosDiagnostico Desde(DateOnly dia)
    {
        var inicio = dia.ToDateTime(TimeOnly.MinValue);
        return new DatosDiagnostico
        {
            Articulos = Articulos,
            Ventas = Ventas.Where(v => v.FechaLocal >= inicio).ToList(),
            Lineas = Lineas.Where(l => l.FechaLocal >= inicio).ToList(),
            Existencias = Existencias,
            Precios = Precios,
            UltimoCosto = UltimoCosto,
        };
    }

    public static async Task<DatosDiagnostico> CargarAsync(PosDbContext db, CancellationToken ct = default)
    {
        var variantes = await db.Variantes.AsNoTracking()
            .Select(v => new
            {
                v.Id,
                v.Descripcion,
                v.UnidadBase,
                Producto = v.Producto!.Nombre,
                v.Producto.Tipo,
                Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre,
                Departamento = v.Producto.Departamento!.Nombre,
            })
            .ToListAsync(ct);

        var codigos = (await db.CodigosBarras.AsNoTracking()
                .Select(c => new { c.Codigo, c.Presentacion!.VarianteId, c.Presentacion.EsBase })
                .ToListAsync(ct))
            .GroupBy(c => c.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.EsBase).ThenBy(c => c.Codigo).First().Codigo);

        var ventas = (await db.VentasHistoricas.AsNoTracking()
                .Select(v => new { v.Id, v.FechaUtc, v.TotalCentavos, v.Cancelada })
                .ToListAsync(ct))
            .Select(v => new Venta(v.Id, HoraMexico.ALocal(v.FechaUtc), v.TotalCentavos, v.Cancelada))
            .ToList();

        var lineas = (await (
                from l in db.VentasHistoricasLineas.AsNoTracking()
                join v in db.VentasHistoricas on l.VentaHistoricaId equals v.Id
                where !v.Cancelada
                select new { l.VarianteId, VentaId = v.Id, v.FechaUtc, l.Cantidad, l.ImporteCentavos, l.CostoCentavos })
                .ToListAsync(ct))
            .Select(l => new LineaVendida(l.VarianteId, l.VentaId, HoraMexico.ALocal(l.FechaUtc), l.Cantidad, l.ImporteCentavos, l.CostoCentavos))
            .ToList();

        var existencias = (await db.Existencias.AsNoTracking().ToListAsync(ct))
            .GroupBy(e => e.VarianteId)
            .ToDictionary(g => g.Key, g => new Inventario(
                g.Key, new Cantidad(g.Sum(e => e.Cantidad.Milesimas)), g.Sum(e => e.CostoCentavos)));

        var precios = await db.Precios.AsNoTracking()
            .Where(p => p.SucursalId == null)
            .Select(p => new PrecioBase(
                p.VarianteId,
                p.Presentacion == null ? null : p.Presentacion.Nombre,
                p.Presentacion == null ? Cantidad.Piezas(1) : p.Presentacion.Factor,
                p.CantidadMinima,
                p.PrecioCentavos))
            .ToListAsync(ct);

        // El mismo «último costo» de todo el POS: el de un proveedor o el de lo último agregado con costo, el más reciente.
        var ultimoCosto = await UltimosCostos.DeTodasAsync(db, ct);

        return new DatosDiagnostico
        {
            Articulos = variantes.ToDictionary(
                v => v.Id,
                v => new Articulo(v.Id, v.Departamento, v.Producto, v.Marca, v.Descripcion, v.Tipo, v.UnidadBase, codigos.GetValueOrDefault(v.Id))),
            Ventas = ventas,
            Lineas = lineas,
            Existencias = existencias,
            Precios = precios,
            UltimoCosto = ultimoCosto,
        };
    }
}
