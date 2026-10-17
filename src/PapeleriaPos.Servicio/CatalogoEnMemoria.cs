using Microsoft.Data.Sqlite;
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
/// <para>
/// Nada pesado dentro de una petición (entrega 5, parte 2): cuando la versión cambia, la recarga va en segundo plano y
/// mientras se contesta con el vigente. Solo se espera lo que no puede contestarse sin ella: la primera carga, un código o
/// un producto que el vigente no tiene (recién dado de alta), y la copia de una caja o sus cambios, que se arman con la
/// versión que dicen. Cada carga lee la versión y el catálogo de un mismo momento de la base.
/// </para>
/// </summary>
public sealed class CatalogoEnMemoria(IServiceScopeFactory alcances, VersionesDeLaCopia versiones)
{
    /// <param name="Partes">El nombre en pedazos (color, medida, marca…): la caja junta con eso las familias de la lista.</param>
    private sealed record Articulo(
        Guid VarianteId, string Descripcion, string? Codigo, string Normalizado, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
        List<PresentacionDto> Presentaciones, List<PrecioDto> Precios, IReadOnlyList<ParteDeNombre> Partes)
    {
        /// <summary>
        /// Las palabras del nombre, la marca y los códigos: cada pedazo escrito tiene que ser el principio de una. Van
        /// tal cual («3/4», «LIB-01») y también partidas en lo que no es letra ni número («(azul)» → «azul»). También lo
        /// que lee la caja del nombre: «negro» encuentra «Negra», «surtido» los «V/Colores» y «1mm» el «1.0 Mm».
        /// </summary>
        public string[] Palabras { get; } = Normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(System.Text.RegularExpressions.Regex.Split(Normalizado, @"[^\p{L}\p{N}]+"))
            .Concat(Partes.SelectMany(p => p.Clave.Split(' ')))
            .Where(p => p.Length > 0).Distinct().ToArray();
    }

    /// <param name="CodigosDe">Los códigos de cada variante (los de sus presentaciones), como van en la copia.</param>
    private sealed record Indice(long Version, Dictionary<Guid, Articulo> PorVariante, Dictionary<string, (Guid Variante, Guid Presentacion)> PorCodigo,
        SinonimosDeBusqueda Sinonimos, ILookup<Guid, CodigoLocalDto> CodigosDe);

    private static readonly Indice Vacio = new(-1, [], [], SinonimosDeBusqueda.Ninguno, Array.Empty<CodigoLocalDto>().ToLookup(c => c.VarianteId));

    private Indice _indice = Vacio;
    private readonly Lock _candado = new();

    /// <summary>La recarga en curso: una a la vez; quien la necesita la espera junto con los demás.</summary>
    private Task<Indice>? _recargando;

    /// <summary>Cambia al invalidar: una recarga que empezó antes ya no se pone.</summary>
    private int _generacion;

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
        lock (_candado)
        {
            _generacion++;
            _indice = Vacio;
            _recargando = null;
        }
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

    /// <summary>
    /// El índice vigente. Si la versión de la base ya es otra (la de memoria, <see cref="VersionesDeLaCopia"/>), la recarga
    /// va en segundo plano y se contesta con este. Solo la primera vez (al arrancar o al empezar de cero) se espera.
    /// </summary>
    private async Task<Indice> ActualAsync(CancellationToken ct)
    {
        var indice = Volatile.Read(ref _indice);
        if (indice.Version < 0)
            return await RecargaAsync(ct);
        if ((await versiones.ActualesAsync(ct)).Catalogo != indice.Version)
            _ = RecargaAsync(CancellationToken.None);
        return indice;
    }

    /// <summary>
    /// El índice de esa versión o de una más nueva: lo que se arma con la versión que dice (la copia de una caja, sus cambios)
    /// lo espera. Si la recarga en curso empezó antes del cambio, se recarga otra vez.
    /// </summary>
    private async Task<Indice> AlMenosAsync(long version, CancellationToken ct)
    {
        var indice = Volatile.Read(ref _indice);
        for (var intento = 0; indice.Version < version && intento < 3; intento++)
            indice = await RecargaAsync(ct);
        return indice;
    }

    /// <summary>
    /// Espera a que el catálogo en memoria tenga la versión de la base de ahora (la recarga va en segundo plano): para lo que
    /// tiene que ver un cambio recién guardado, como las pruebas de lo que se cobra después de cambiar un precio.
    /// </summary>
    public async Task AlDiaAsync(PosDbContext db, CancellationToken ct = default) => await AlMenosAsync(await VersionDeLaBaseAsync(db, ct), ct);

    /// <summary>
    /// Lo que el vigente no tiene (un código o un producto recién dado de alta): con la versión de la base leída ahora, si ya
    /// es otra, el índice de esa versión. Lo que sí tiene se contesta sin esperar.
    /// </summary>
    private async Task<Indice> AlDiaAsync(PosDbContext db, Indice indice, CancellationToken ct)
    {
        var version = await VersionDeLaBaseAsync(db, ct);
        return version == indice.Version ? indice : await AlMenosAsync(version, ct);
    }

    private static Task<long> VersionDeLaBaseAsync(PosDbContext db, CancellationToken ct) =>
        db.Secuencias.AsNoTracking().Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync(ct);

    /// <summary>La recarga en curso o una nueva, fuera de la petición que la pide (con su propio contexto de la base).</summary>
    private Task<Indice> RecargaAsync(CancellationToken ct)
    {
        Task<Indice> recarga;
        lock (_candado)
        {
            if (_recargando is not { IsCompleted: false })
            {
                var generacion = _generacion;
                _recargando = Task.Run(() => CargarYPonerAsync(generacion));
            }
            recarga = _recargando;
        }
        return recarga.WaitAsync(ct);
    }

    private async Task<Indice> CargarYPonerAsync(int generacion)
    {
        using var alcance = alcances.CreateScope();
        var nuevo = await CargarAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>(), CancellationToken.None);
        lock (_candado)
        {
            if (generacion == _generacion)
                _indice = nuevo;
            return _indice;
        }
    }

    /// <summary>
    /// La versión y el catálogo de un mismo momento de la base: una lectura diferida (sin el candado de escritura) en la que
    /// lo primero que se lee es la versión. Antes la versión se leía aparte, y una carga podía traer un cambio posterior con la
    /// versión de antes.
    /// </summary>
    private static async Task<Indice> CargarAsync(PosDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var lectura = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(lectura, ct);
            try
            {
                return await CargarAsync(db, await VersionDeLaBaseAsync(db, ct), ct);
            }
            finally
            {
                await db.Database.UseTransactionAsync(null, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
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
        // Las marcas se aprenden de todo el catálogo; también valen las dadas de alta.
        var nombres = new LectorDeNombres(variantes.Select(v => Descripcion(v.Producto, v.Marca, v.Descripcion)),
            variantes.Select(v => v.Marca).OfType<string>().Distinct());

        var porVariante = new Dictionary<Guid, Articulo>();
        foreach (var v in variantes)
        {
            var descripcion = Descripcion(v.Producto, v.Marca, v.Descripcion);
            var codigo = codigosPorVariante[v.Id].OrderByDescending(c => c.EsBase).Select(c => c.Codigo).FirstOrDefault();
            var todosLosCodigos = string.Join(" ", codigosPorVariante[v.Id].Select(c => c.Codigo));
            porVariante[v.Id] = new Articulo(
                v.Id, descripcion, codigo, Texto.Normalizar($"{descripcion} {todosLosCodigos}"), v.Impuesto, v.UnidadBase,
                v.Tipo == TipoProducto.Mercancia,
                presentaciones[v.Id].OrderByDescending(p => p.EsBase).ThenBy(p => p.Factor)
                    .Select(p => new PresentacionDto(p.Id, p.Nombre, p.Factor.Milesimas, p.EsBase)).ToList(),
                precios[v.Id].Select(p => new PrecioDto(p.Id, p.PresentacionId, p.SucursalId, p.CantidadMinima.Milesimas, p.PrecioCentavos)).ToList(),
                nombres.Leer(descripcion));
        }

        var porCodigo = new Dictionary<string, (Guid, Guid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in codigos.Where(c => porVariante.ContainsKey(c.VarianteId)))
            porCodigo.TryAdd(c.Codigo, (c.VarianteId, c.PresentacionId));

        var sinonimos = await db.Sinonimos.AsNoTracking().Where(s => s.Activo).Select(s => new { s.Palabra, s.Equivale }).ToListAsync(ct);
        return new Indice(version, porVariante, porCodigo, new SinonimosDeBusqueda(sinonimos.Select(s => (s.Palabra, s.Equivale))),
            porCodigo.Select(c => new CodigoLocalDto(c.Key, c.Value.Item1, c.Value.Item2)).ToLookup(c => c.VarianteId));
    }

    /// <summary>
    /// La versión del catálogo con que se arma lo que se contesta: la del índice vigente (con que se armó la venta, aunque la
    /// base ya vaya en otra y la recarga siga en camino).
    /// </summary>
    public async Task<long> VersionAsync(PosDbContext db, CancellationToken ct = default) => (await ActualAsync(ct)).Version;

    /// <summary>El producto de un código, con la versión del catálogo de la que salieron sus precios (entrega 5).</summary>
    public async Task<ProductoVentaDto?> PorCodigoAsync(PosDbContext db, string codigo, Guid sucursalId, CancellationToken ct = default)
    {
        var indice = await ActualAsync(ct);
        if (!indice.PorCodigo.TryGetValue(codigo.Trim(), out var x) && !(indice = await AlDiaAsync(db, indice, ct)).PorCodigo.TryGetValue(codigo.Trim(), out x))
            return null;
        return await ProductoAsync(db, indice.Version, indice.PorVariante[x.Variante], x.Presentacion, sucursalId, ct);
    }

    /// <param name="conExistencia">Sin ella, no se lee la base: para armar una venta, que no la usa.</param>
    public async Task<ProductoVentaDto?> PorVarianteAsync(PosDbContext db, Guid varianteId, Guid sucursalId, CancellationToken ct = default,
        bool conExistencia = true)
    {
        var indice = await ActualAsync(ct);
        if (!indice.PorVariante.TryGetValue(varianteId, out var a) && !(indice = await AlDiaAsync(db, indice, ct)).PorVariante.TryGetValue(varianteId, out a))
            return null;
        return conExistencia ? await ProductoAsync(db, indice.Version, a, null, sucursalId, ct) : Producto(a, null, indice.Version, null);
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

        var indice = await ActualAsync(ct);
        var vendidos = await VendidosAsync(db, ct);
        Guid? elegido = null;
        if (cajaId is { } caja)
        {
            await CargarElegidoAsync(db, ct);
            elegido = _elegido.TryGetValue((caja, consulta), out var v) ? v : null;
        }
        // Cada pedazo vale por sí mismo o por los sinónimos de la tienda («bolig» → lapicero).
        var pedazos = palabras.Select(p => (Pedazo: p, Sinonimos: indice.Sinonimos.De(p))).ToList();
        static bool Directo(Articulo a, string p) => a.Palabras.Any(w => w.StartsWith(p, StringComparison.Ordinal));
        // La palabra de la tienda por la que salió, si algún pedazo no estaba en el nombre.
        static string? PorSinonimo(Articulo a, List<(string Pedazo, IReadOnlyList<(string Palabra, string Equivale)> Sinonimos)> pedazos) =>
            pedazos.Where(x => !Directo(a, x.Pedazo)).SelectMany(x => x.Sinonimos).Where(s => Directo(a, s.Equivale))
                .Select(s => s.Palabra.ToLowerInvariant()).FirstOrDefault();

        var candidatos = indice.PorVariante.Values
            .Where(a => pedazos.All(x => Directo(a, x.Pedazo) || x.Sinonimos.Any(s => Directo(a, s.Equivale))))
            .ToList();
        // Si nada coincide exacto, un error de dedo: lo parecido, en lugar de «no existe».
        var parecido = false;
        if (candidatos.Count == 0)
        {
            candidatos = indice.PorVariante.Values
                .Where(a => pedazos.All(x => ErrorDeDedo.Coincide(x.Pedazo, a.Palabras) || x.Sinonimos.Any(s => Directo(a, s.Equivale))))
                .ToList();
            parecido = candidatos.Count > 0;
        }
        var porSinonimo = candidatos.ToDictionary(a => a.VarianteId, a => PorSinonimo(a, pedazos));
        var encontrados = candidatos
            .OrderByDescending(a => a.Codigo is not null && string.Equals(a.Codigo, texto.Trim(), StringComparison.OrdinalIgnoreCase))
            // Entre los parecidos, primero los que coinciden exacto en más palabras («pluma pumto» antes que el plumón).
            .ThenByDescending(a => parecido ? palabras.Count(p => Directo(a, p)) : 0)
            .ThenByDescending(a => a.VarianteId == elegido)
            // Lo que se llama como se escribió, antes de lo que salió por un sinónimo.
            .ThenBy(a => porSinonimo[a.VarianteId] is not null)
            .ThenByDescending(a => a.Normalizado.StartsWith(consulta, StringComparison.Ordinal))
            .ThenByDescending(a => vendidos.GetValueOrDefault(a.VarianteId))
            .ThenBy(a => a.Descripcion, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .ToList();

        var existencias = await ExistenciasAsync(db, encontrados.Select(a => a.VarianteId).ToList(), sucursalId, ct);
        return encontrados.Select(a => new ResultadoBusquedaDto(
            a.VarianteId, a.Descripcion, a.Codigo, PrecioMenudeo(a, sucursalId),
            a.LlevaInventario ? existencias.GetValueOrDefault(a.VarianteId) : null,
            vendidos.GetValueOrDefault(a.VarianteId), a.Partes, parecido, porSinonimo[a.VarianteId])).ToList();
    }

    /// <summary>
    /// Todo el catálogo vendible de una sucursal, con existencias: la copia de una caja para vender sin conexión. Con la
    /// versión de la base de ahora (si la recarga sigue en camino, la espera: la caja la baja en segundo plano).
    /// </summary>
    /// <param name="existencias">Las de la sucursal, leídas por quien arma la copia (con su versión).</param>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos)> TodosAsync(
        PosDbContext db, IReadOnlyDictionary<Guid, decimal> existencias, CancellationToken ct = default)
    {
        var indice = await AlMenosAsync(await VersionDeLaBaseAsync(db, ct), ct);
        var productos = indice.PorVariante.Values.Select(a => Producto(a, null, indice.Version, existencias)).ToList();
        return (indice.Version, productos, indice.PorCodigo.Select(c => new CodigoLocalDto(c.Key, c.Value.Variante, c.Value.Presentacion)).ToList());
    }

    /// <summary>
    /// Los cambios de la copia de una caja (entrega 5, parte 2): esas variantes como van en la copia, con sus códigos, del
    /// índice de esa versión o de una más nueva; las que ya no están en el catálogo vendible van aparte, para quitarlas.
    /// </summary>
    /// <param name="existencias">Las de esas variantes en la sucursal, leídas por quien arma los cambios.</param>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos, List<Guid> Quitados)> ParaLaCopiaAsync(
        long version, IReadOnlyCollection<Guid> variantes, IReadOnlyDictionary<Guid, decimal> existencias, CancellationToken ct = default)
    {
        var indice = await AlMenosAsync(version, ct);
        var (productos, codigos, quitados) = (new List<ProductoVentaDto>(), new List<CodigoLocalDto>(), new List<Guid>());
        foreach (var id in variantes)
        {
            if (indice.PorVariante.TryGetValue(id, out var a))
            {
                productos.Add(Producto(a, null, indice.Version, existencias));
                codigos.AddRange(indice.CodigosDe[id]);
            }
            else
                quitados.Add(id);
        }
        return (indice.Version, productos, codigos, quitados);
    }

    /// <summary>Cuántos productos tiene el índice vigente (para decidir si unos cambios ya son casi la copia completa).</summary>
    public int Productos => Volatile.Read(ref _indice).PorVariante.Count;

    /// <summary>Un producto como lo vende la caja; con <paramref name="existencias"/>, con la suya (sin renglón, cero).</summary>
    private static ProductoVentaDto Producto(Articulo a, Guid? presentacionId, long version, IReadOnlyDictionary<Guid, decimal>? existencias) =>
        new(a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario,
            a.LlevaInventario && existencias is not null ? existencias.GetValueOrDefault(a.VarianteId) : null,
            a.Presentaciones.FirstOrDefault(p => p.Id == presentacionId) ?? a.Presentaciones.First(p => p.EsBase), a.Presentaciones, a.Precios, version);

    private static long PrecioMenudeo(Articulo a, Guid sucursalId)
    {
        var porUnidad = a.Precios.Where(p => p.PresentacionId is null).ToList();
        var deSucursal = porUnidad.Where(p => p.SucursalId == sucursalId).ToList();
        return (deSucursal.Count > 0 ? deSucursal : porUnidad.Where(p => p.SucursalId is null)).MinBy(p => p.CantidadMinimaMilesimas)?.PrecioCentavos ?? 0;
    }

    private static async Task<ProductoVentaDto> ProductoAsync(
        PosDbContext db, long version, Articulo a, Guid? presentacionId, Guid sucursalId, CancellationToken ct) =>
        Producto(a, presentacionId, version, a.LlevaInventario ? await ExistenciasAsync(db, [a.VarianteId], sucursalId, ct) : null);

    private static async Task<Dictionary<Guid, decimal>> ExistenciasAsync(PosDbContext db, List<Guid> variantes, Guid sucursalId, CancellationToken ct) =>
        (await db.Existencias.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && variantes.Contains(e.VarianteId))
            .ToListAsync(ct))
        .ToDictionary(e => e.VarianteId, e => e.Cantidad.EnUnidades);
}
