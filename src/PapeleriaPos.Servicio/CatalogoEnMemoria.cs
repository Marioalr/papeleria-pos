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
        Guid VarianteId, string Descripcion, string? Codigo, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
        List<PresentacionDto> Presentaciones, List<PrecioDto> Precios);

    /// <param name="Codigos">Todos los códigos de lo que se vende, en el orden de la base (también los repetidos): van a la copia de las cajas.</param>
    /// <param name="Marcas">Con las que se leen los nombres: la copia de las cajas los lee igual.</param>
    /// <param name="Busqueda">La búsqueda por nombre, la misma que arma cada caja con su copia (entrega 5, parte 3).</param>
    private sealed record Indice(long Version, Dictionary<Guid, Articulo> PorVariante, Dictionary<string, (Guid Variante, Guid Presentacion)> PorCodigo,
        List<CodigoLocalDto> Codigos, IReadOnlyList<string> Marcas, IndiceDeBusqueda Busqueda);

    private static Indice SinCargar() => new(-1, [], [], [], [], IndiceDeBusqueda.Vacio);

    private Indice _indice = SinCargar();
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
        _indice = SinCargar();
        _vendidos = (DateTime.MinValue, []);
        _elegido.Clear();
        _elegidoCargado = false;
    }

    /// <summary>La clave de «lo elegido antes»: lo escrito sin acentos, mayúsculas ni espacios de más.</summary>
    public static string ClaveDeBusqueda(string texto) => IndiceDeBusqueda.Clave(texto);

    /// <summary>Lo que eligió una caja con un texto: la próxima vez sale primero.</summary>
    public void Aprender(Guid cajaId, string texto, Guid varianteId) => _elegido[(cajaId, ClaveDeBusqueda(texto))] = varianteId;

    /// <summary>
    /// Lo que eligió esta caja con cada texto, para su copia (entrega 5, parte 3): con eso la búsqueda de la caja ordena
    /// igual que aquí.
    /// </summary>
    public async Task<List<ElegidoDto>> ElegidosAsync(PosDbContext db, Guid cajaId, CancellationToken ct = default)
    {
        await CargarElegidoAsync(db, ct);
        return _elegido.Where(x => x.Key.Caja == cajaId).Select(x => new ElegidoDto(x.Key.Texto, x.Value)).ToList();
    }

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

    /// <summary>
    /// Cuántas veces se vendió cada variante en 90 días, lo mismo con que ordena la búsqueda: la página de Productos abre con
    /// lo más vendido primero (entrega 8). Se calcula una vez al día, así que pedirlo no cuesta.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> VecesVendidoAsync(PosDbContext db, CancellationToken ct = default) => await VendidosAsync(db, ct);

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

        static string Descripcion(string producto, string? marca, string? descripcion) =>
            string.Join(" ", new[] { producto, marca, descripcion }.Where(p => !string.IsNullOrWhiteSpace(p)));

        var porVariante = new Dictionary<Guid, Articulo>();
        foreach (var v in variantes)
        {
            var codigo = codigosPorVariante[v.Id].OrderByDescending(c => c.EsBase).Select(c => c.Codigo).FirstOrDefault();
            porVariante[v.Id] = new Articulo(
                v.Id, Descripcion(v.Producto, v.Marca, v.Descripcion), codigo, v.Impuesto, v.UnidadBase, v.Tipo == TipoProducto.Mercancia,
                presentaciones[v.Id].OrderByDescending(p => p.EsBase).ThenBy(p => p.Factor)
                    .Select(p => new PresentacionDto(p.Id, p.Nombre, p.Factor.Milesimas, p.EsBase)).ToList(),
                precios[v.Id].Select(p => new PrecioDto(p.Id, p.PresentacionId, p.SucursalId, p.CantidadMinima.Milesimas, p.PrecioCentavos)).ToList());
        }

        var deLoQueSeVende = codigos.Where(c => porVariante.ContainsKey(c.VarianteId)).Select(c => new CodigoLocalDto(c.Codigo, c.VarianteId, c.PresentacionId)).ToList();
        var porCodigo = new Dictionary<string, (Guid, Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in deLoQueSeVende)
            porCodigo.TryAdd(c.Codigo, (c.VarianteId, c.PresentacionId));

        // Las marcas se aprenden de todo el catálogo; también valen las de los productos. La copia de las cajas lleva las mismas
        // y sus sinónimos, y arma la misma búsqueda (entrega 5, parte 3).
        var marcas = variantes.Select(v => v.Marca).OfType<string>().Distinct().ToList();
        var sinonimos = await db.Sinonimos.AsNoTracking().Where(s => s.Activo).Select(s => new { s.Palabra, s.Equivale }).ToListAsync(ct);
        var busqueda = IndiceDeBusqueda.Armar(
            porVariante.Values.Select(a => new ProductoParaBuscar(a.VarianteId, a.Descripcion, a.Codigo, codigosPorVariante[a.VarianteId].Select(c => c.Codigo))),
            marcas, sinonimos.Select(s => (s.Palabra, s.Equivale)));
        return new Indice(version, porVariante, porCodigo, deLoQueSeVende, marcas, busqueda);
    }

    /// <summary>La versión del catálogo con que se arma lo que se contesta (la de la base, ya cargada en memoria).</summary>
    public async Task<long> VersionAsync(PosDbContext db, CancellationToken ct = default) => (await ActualAsync(db, ct)).Version;

    /// <summary>El producto de un código, con la versión del catálogo de la que salieron sus precios (entrega 5).</summary>
    public async Task<ProductoVentaDto?> PorCodigoAsync(PosDbContext db, string codigo, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        return indice.PorCodigo.TryGetValue(codigo.Trim(), out var x)
            ? await ProductoAsync(db, indice.Version, indice.PorVariante[x.Variante], x.Presentacion, sucursalId, ct)
            : null;
    }

    public async Task<ProductoVentaDto?> PorVarianteAsync(PosDbContext db, Guid varianteId, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        return indice.PorVariante.TryGetValue(varianteId, out var a) ? await ProductoAsync(db, indice.Version, a, null, sucursalId, ct) : null;
    }

    /// <summary>
    /// Por nombre, código o marca, sin importar acentos ni el orden de las palabras: cada pedazo escrito es el principio
    /// de una palabra («lus az» = Papel lustre azul). El orden: el código exacto; lo que esa caja eligió antes con el
    /// mismo texto; lo que empieza con lo escrito; lo más vendido en 90 días (docs/propuesta-interfaz.md). Es la misma
    /// búsqueda que hace cada caja con su copia (<see cref="IndiceDeBusqueda"/>); aquí, con las existencias de ahora.
    /// </summary>
    public async Task<List<ResultadoBusquedaDto>> BuscarAsync(PosDbContext db, string texto, Guid sucursalId, int maximo = 30, CancellationToken ct = default,
        Guid? cajaId = null)
    {
        if (ClaveDeBusqueda(texto).Length == 0)
            return [];
        var indice = await ActualAsync(db, ct);
        var vendidos = await VendidosAsync(db, ct);
        Guid? elegido = null;
        if (cajaId is { } caja)
        {
            await CargarElegidoAsync(db, ct);
            elegido = _elegido.TryGetValue((caja, ClaveDeBusqueda(texto)), out var v) ? v : null;
        }
        var encontrados = indice.Busqueda.Buscar(texto, maximo, elegido, vendidos.GetValueOrDefault);
        var existencias = await ExistenciasAsync(db, encontrados.Select(e => e.Articulo.VarianteId).ToList(), sucursalId, ct);
        return encontrados.Select(e =>
        {
            var a = indice.PorVariante[e.Articulo.VarianteId];
            return new ResultadoBusquedaDto(a.VarianteId, a.Descripcion, a.Codigo, PrecioDeMenudeo.De(a.Precios, sucursalId),
                a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null,
                vendidos.GetValueOrDefault(a.VarianteId), e.Articulo.Partes, e.Parecido, e.PorSinonimo);
        }).ToList();
    }

    /// <summary>
    /// Todo el catálogo vendible de una sucursal, con existencias: la copia de una caja para vender sin conexión. Con todos los
    /// códigos (también los repetidos: al escanear vale el primero, como aquí) y las marcas con que se leen los nombres, para
    /// que la caja arme la misma búsqueda (entrega 5, parte 3).
    /// </summary>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos, IReadOnlyList<string> Marcas)> TodosAsync(
        PosDbContext db, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(db, ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId)
            .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad.EnUnidades, ct);
        var productos = indice.PorVariante.Values.Select(a => new ProductoVentaDto(
            a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario,
            a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null,
            a.Presentaciones.First(p => p.EsBase), a.Presentaciones, a.Precios, indice.Version)).ToList();
        return (indice.Version, productos, indice.Codigos, indice.Marcas);
    }

    private static async Task<ProductoVentaDto> ProductoAsync(
        PosDbContext db, long version, Articulo a, Guid? presentacionId, Guid sucursalId, CancellationToken ct)
    {
        var existencia = a.LlevaInventario ? (await ExistenciasAsync(db, [a.VarianteId], sucursalId, ct)).GetValueOrDefault(a.VarianteId) : (decimal?)null;
        var presentacion = a.Presentaciones.FirstOrDefault(p => p.Id == presentacionId) ?? a.Presentaciones.First(p => p.EsBase);
        return new ProductoVentaDto(a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario, existencia, presentacion, a.Presentaciones,
            a.Precios, version);
    }

    private static async Task<Dictionary<Guid, decimal>> ExistenciasAsync(PosDbContext db, List<Guid> variantes, Guid sucursalId, CancellationToken ct) =>
        (await db.Existencias.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && variantes.Contains(e.VarianteId))
            .ToListAsync(ct))
        .ToDictionary(e => e.VarianteId, e => e.Cantidad.EnUnidades);
}
