using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El catálogo vendible en memoria, para que escanear y buscar respondan al instante. Se recarga solo cuando cambia
/// la versión del catálogo. Las existencias no se guardan aquí: se leen al momento.
/// </summary>
public sealed class CatalogoEnMemoria
{
    private sealed record Articulo(
        Guid VarianteId, string Descripcion, string? Codigo, string Normalizado, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
        List<PresentacionDto> Presentaciones, List<PrecioDto> Precios);

    private sealed record Indice(long Version, Dictionary<Guid, Articulo> PorVariante, Dictionary<string, (Guid Variante, Guid Presentacion)> PorCodigo);

    private Indice _indice = new(-1, [], []);
    private readonly SemaphoreSlim _recarga = new(1, 1);

    private async Task<Indice> ActualAsync(PosDbContext db, CancellationToken ct)
    {
        var version = await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync(ct);
        if (version == _indice.Version)
            return _indice;

        await _recarga.WaitAsync(ct);
        try
        {
            if (version != _indice.Version)
                _indice = await CargarAsync(db, version, ct);
            return _indice;
        }
        finally
        {
            _recarga.Release();
        }
    }

    private static async Task<Indice> CargarAsync(PosDbContext db, long version, CancellationToken ct)
    {
        var variantes = await db.Variantes.AsNoTracking()
            .Where(v => v.Activa && v.Producto!.Activo)
            .Select(v => new
            {
                v.Id,
                v.Descripcion,
                v.UnidadBase,
                Producto = v.Producto!.Nombre,
                Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre,
                v.Producto.Impuesto,
                v.Producto.Tipo,
            })
            .ToListAsync(ct);
        var presentaciones = (await db.Presentaciones.AsNoTracking().ToListAsync(ct)).ToLookup(p => p.VarianteId);
        var precios = (await db.Precios.AsNoTracking().ToListAsync(ct)).ToLookup(p => p.VarianteId);
        var codigos = await db.CodigosBarras.AsNoTracking()
            .Select(c => new { c.Codigo, c.PresentacionId, c.Presentacion!.VarianteId, c.Presentacion.EsBase })
            .ToListAsync(ct);
        var codigosPorVariante = codigos.ToLookup(c => c.VarianteId);

        var porVariante = new Dictionary<Guid, Articulo>();
        foreach (var v in variantes)
        {
            var descripcion = string.Join(" ", new[] { v.Producto, v.Marca, v.Descripcion }.Where(p => !string.IsNullOrWhiteSpace(p)));
            var codigo = codigosPorVariante[v.Id].OrderByDescending(c => c.EsBase).Select(c => c.Codigo).FirstOrDefault();
            var todosLosCodigos = string.Join(" ", codigosPorVariante[v.Id].Select(c => c.Codigo));
            porVariante[v.Id] = new Articulo(
                v.Id, descripcion, codigo, Texto.Normalizar($"{descripcion} {todosLosCodigos}"), v.Impuesto, v.UnidadBase,
                v.Tipo == TipoProducto.Mercancia,
                presentaciones[v.Id].OrderByDescending(p => p.EsBase).ThenBy(p => p.Factor)
                    .Select(p => new PresentacionDto(p.Id, p.Nombre, p.Factor.Milesimas, p.EsBase)).ToList(),
                precios[v.Id].Select(p => new PrecioDto(p.Id, p.PresentacionId, p.SucursalId, p.CantidadMinima.Milesimas, p.PrecioCentavos)).ToList());
        }

        var porCodigo = new Dictionary<string, (Guid, Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in codigos.Where(c => porVariante.ContainsKey(c.VarianteId)))
            porCodigo.TryAdd(c.Codigo, (c.VarianteId, c.PresentacionId));

        return new Indice(version, porVariante, porCodigo);
    }

    public async Task<ProductoVentaDto?> PorCodigoAsync(PosDbContext db, string codigo, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        return indice.PorCodigo.TryGetValue(codigo.Trim(), out var x)
            ? await ProductoAsync(db, indice.PorVariante[x.Variante], x.Presentacion, sucursalId, ct)
            : null;
    }

    public async Task<ProductoVentaDto?> PorVarianteAsync(PosDbContext db, Guid varianteId, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        return indice.PorVariante.TryGetValue(varianteId, out var a) ? await ProductoAsync(db, a, null, sucursalId, ct) : null;
    }

    /// <summary>
    /// Por nombre, código o marca, sin importar acentos ni el orden de las palabras: cada palabra tiene que aparecer.
    /// Primero el código exacto, luego lo que empieza con lo escrito.
    /// </summary>
    public async Task<List<ResultadoBusquedaDto>> BuscarAsync(PosDbContext db, string texto, Guid sucursalId, int maximo = 30, CancellationToken ct = default)
    {
        var palabras = Texto.Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length == 0)
            return [];

        var indice = await ActualAsync(db, ct);
        var consulta = Texto.Normalizar(texto);
        var encontrados = indice.PorVariante.Values
            .Where(a => palabras.All(a.Normalizado.Contains))
            .OrderByDescending(a => a.Codigo is not null && string.Equals(a.Codigo, texto.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.Normalizado.StartsWith(consulta, StringComparison.Ordinal))
            .ThenBy(a => a.Descripcion, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .ToList();

        var existencias = await ExistenciasAsync(db, encontrados.Select(a => a.VarianteId).ToList(), sucursalId, ct);
        return encontrados.Select(a => new ResultadoBusquedaDto(
            a.VarianteId, a.Descripcion, a.Codigo, PrecioMenudeo(a, sucursalId),
            a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null)).ToList();
    }

    private static long PrecioMenudeo(Articulo a, Guid sucursalId)
    {
        var porUnidad = a.Precios.Where(p => p.PresentacionId is null).ToList();
        var deSucursal = porUnidad.Where(p => p.SucursalId == sucursalId).ToList();
        return (deSucursal.Count > 0 ? deSucursal : porUnidad.Where(p => p.SucursalId is null)).MinBy(p => p.CantidadMinimaMilesimas)?.PrecioCentavos ?? 0;
    }

    private static async Task<ProductoVentaDto> ProductoAsync(PosDbContext db, Articulo a, Guid? presentacionId, Guid sucursalId, CancellationToken ct)
    {
        var existencia = a.LlevaInventario ? (await ExistenciasAsync(db, [a.VarianteId], sucursalId, ct)).GetValueOrDefault(a.VarianteId) : (decimal?)null;
        var presentacion = a.Presentaciones.FirstOrDefault(p => p.Id == presentacionId) ?? a.Presentaciones.First(p => p.EsBase);
        return new ProductoVentaDto(a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario, existencia, presentacion, a.Presentaciones, a.Precios);
    }

    private static async Task<Dictionary<Guid, decimal>> ExistenciasAsync(PosDbContext db, List<Guid> variantes, Guid sucursalId, CancellationToken ct) =>
        (await db.Existencias.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && variantes.Contains(e.VarianteId))
            .ToListAsync(ct))
        .ToDictionary(e => e.VarianteId, e => e.Cantidad.EnUnidades);
}
