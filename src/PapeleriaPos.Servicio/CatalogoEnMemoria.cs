using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El catálogo vendible en memoria, para que escanear y buscar respondan al instante. Se recarga solo cuando cambia
/// la versión del catálogo. Las existencias no se guardan aquí: se leen al momento.
/// </summary>
public sealed class CatalogoEnMemoria
{
    private sealed record Articulo(
        Guid VarianteId, string Descripcion, string? Codigo, string Normalizado, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
        List<PresentacionDto> Presentaciones, List<PrecioDto> Precios)
    {
        /// <summary>
        /// Las palabras del nombre, la marca y los códigos: cada pedazo escrito tiene que ser el principio de una. Van
        /// tal cual («3/4», «LIB-01») y también partidas en lo que no es letra ni número («(azul)» → «azul»).
        /// </summary>
        public string[] Palabras { get; } = Normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(System.Text.RegularExpressions.Regex.Split(Normalizado, @"[^\p{L}\p{N}]+"))
            .Where(p => p.Length > 0).Distinct().ToArray();
    }

    private sealed record Indice(long Version, Dictionary<Guid, Articulo> PorVariante, Dictionary<string, (Guid Variante, Guid Presentacion)> PorCodigo);

    private Indice _indice = new(-1, [], []);
    private readonly SemaphoreSlim _recarga = new(1, 1);

    /// <summary>
    /// Cuántas veces se vendió cada variante en los últimos 90 días (lo de eleventa y lo del POS): ordena la búsqueda
    /// cuando el texto no distingue. Se calcula una vez al día, para que el orden no cambie a cada rato.
    /// </summary>
    private (DateTime Hasta, Dictionary<Guid, int> Veces) _vendidos = (DateTime.MinValue, []);

    /// <summary>Lo último que eligió cada caja con cada texto (normalizado): sale primero la vez siguiente.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Caja, string Texto), Guid> _elegido = new();
    private bool _elegidoCargado;

    /// <summary>La base cambió entera (la prueba empezó de cero): se vuelve a cargar aunque la versión coincida.</summary>
    public void Invalidar()
    {
        _indice = new(-1, [], []);
        _vendidos = (DateTime.MinValue, []);
        _elegido.Clear();
        _elegidoCargado = false;
    }

    /// <summary>La clave de «lo elegido antes»: lo escrito sin acentos, mayúsculas ni espacios de más.</summary>
    public static string ClaveDeBusqueda(string texto) => Texto.Normalizar(texto);

    /// <summary>Lo que eligió una caja con un texto: la próxima vez sale primero.</summary>
    public void Aprender(Guid cajaId, string texto, Guid varianteId) => _elegido[(cajaId, ClaveDeBusqueda(texto))] = varianteId;

    private async Task CargarElegidoAsync(PosDbContext db, CancellationToken ct)
    {
        if (_elegidoCargado)
            return;
        // Lo más reciente de cada caja y texto gana: se recorre en orden de fecha.
        var elegidas = await db.Busquedas.AsNoTracking().Where(b => b.VarianteId != null).OrderBy(b => b.FechaUtc)
            .Select(b => new { b.CajaId, b.Texto, b.VarianteId }).ToListAsync(ct);
        foreach (var b in elegidas)
            _elegido[(b.CajaId, b.Texto)] = b.VarianteId!.Value;
        _elegidoCargado = true;
    }

    private async Task<Dictionary<Guid, int>> VendidosAsync(PosDbContext db, CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;
        if (ahora < _vendidos.Hasta)
            return _vendidos.Veces;
        var desde = ahora.AddDays(-90);
        var historicas = await (from l in db.VentasHistoricasLineas
                                join v in db.VentasHistoricas on l.VentaHistoricaId equals v.Id
                                where !v.Cancelada && v.FechaUtc >= desde
                                group l by l.VarianteId into g
                                select new { Variante = g.Key, Veces = g.Count() }).ToListAsync(ct);
        var delPos = await (from l in db.VentasLineas
                            join v in db.Ventas on l.VentaId equals v.Id
                            where v.Estado == EstadoVenta.Pagada && v.CreadaUtc >= desde && l.VarianteId != null
                            group l by l.VarianteId into g
                            select new { Variante = g.Key!.Value, Veces = g.Count() }).ToListAsync(ct);
        var veces = historicas.ToDictionary(x => x.Variante, x => x.Veces);
        foreach (var x in delPos)
            veces[x.Variante] = veces.GetValueOrDefault(x.Variante) + x.Veces;
        _vendidos = (ahora.AddDays(1), veces);
        return veces;
    }

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
    /// Por nombre, código o marca, sin importar acentos ni el orden de las palabras: cada pedazo escrito es el principio
    /// de una palabra («lus az» = Papel lustre azul). El orden: el código exacto; lo que esa caja eligió antes con el
    /// mismo texto; lo que empieza con lo escrito; lo más vendido en 90 días (docs/propuesta-interfaz.md).
    /// </summary>
    public async Task<List<ResultadoBusquedaDto>> BuscarAsync(PosDbContext db, string texto, Guid sucursalId, int maximo = 30, CancellationToken ct = default,
        Guid? cajaId = null)
    {
        var consulta = ClaveDeBusqueda(texto);
        var palabras = consulta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length == 0)
            return [];

        var indice = await ActualAsync(db, ct);
        var vendidos = await VendidosAsync(db, ct);
        Guid? elegido = null;
        if (cajaId is { } caja)
        {
            await CargarElegidoAsync(db, ct);
            elegido = _elegido.TryGetValue((caja, consulta), out var v) ? v : null;
        }
        var encontrados = indice.PorVariante.Values
            .Where(a => palabras.All(p => a.Palabras.Any(w => w.StartsWith(p, StringComparison.Ordinal))))
            .OrderByDescending(a => a.Codigo is not null && string.Equals(a.Codigo, texto.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.VarianteId == elegido)
            .ThenByDescending(a => a.Normalizado.StartsWith(consulta, StringComparison.Ordinal))
            .ThenByDescending(a => vendidos.GetValueOrDefault(a.VarianteId))
            .ThenBy(a => a.Descripcion, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .ToList();

        var existencias = await ExistenciasAsync(db, encontrados.Select(a => a.VarianteId).ToList(), sucursalId, ct);
        return encontrados.Select(a => new ResultadoBusquedaDto(
            a.VarianteId, a.Descripcion, a.Codigo, PrecioMenudeo(a, sucursalId),
            a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null)).ToList();
    }

    /// <summary>Todo el catálogo vendible de una sucursal, con existencias: la copia de una caja para vender sin conexión.</summary>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos)> TodosAsync(
        PosDbContext db, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId)
            .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad.EnUnidades, ct);
        var productos = indice.PorVariante.Values.Select(a => new ProductoVentaDto(
            a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario,
            a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null,
            a.Presentaciones.First(p => p.EsBase), a.Presentaciones, a.Precios)).ToList();
        return (indice.Version, productos, indice.PorCodigo.Select(c => new CodigoLocalDto(c.Key, c.Value.Variante, c.Value.Presentacion)).ToList());
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
