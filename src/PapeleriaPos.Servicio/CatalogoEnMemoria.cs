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
    private sealed record Articulo(
        Guid VarianteId, string Descripcion, string? Codigo, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
        List<PresentacionDto> Presentaciones, List<PrecioDto> Precios);

    /// <param name="Codigos">Todos los códigos de lo que se vende (también los que solo cambian en mayúsculas): van a la copia de las cajas.</param>
    /// <param name="Marcas">Con las que se leen los nombres: la copia de las cajas los lee igual.</param>
    /// <param name="Busqueda">La búsqueda por nombre, la misma que arma cada caja con su copia (entrega 5, parte 3).</param>
    /// <param name="Escaneo">De qué producto es cada código escaneado: la misma regla que la copia de las cajas.</param>
    /// <param name="CodigosDe">Los códigos de cada variante (los de sus presentaciones), como van en la copia y en sus cambios.</param>
    private sealed record Indice(long Version, Dictionary<Guid, Articulo> PorVariante, CodigosDeEscaneo Escaneo,
        List<CodigoLocalDto> Codigos, IReadOnlyList<string> Marcas, IndiceDeBusqueda Busqueda, ILookup<Guid, CodigoLocalDto> CodigosDe);

    private static readonly Indice Vacio = new(-1, [], CodigosDeEscaneo.Ninguno, [], [], IndiceDeBusqueda.Vacio, Array.Empty<CodigoLocalDto>().ToLookup(c => c.VarianteId));

    private Indice _indice = Vacio;
    private readonly Lock _candado = new();

    /// <summary>La recarga en curso: una a la vez; quien la necesita la espera junto con los demás.</summary>
    private Task<Indice>? _recargando;

    /// <summary>Cambia al invalidar: una recarga que empezó antes ya no se pone.</summary>
    private int _generacion;

    /// <summary>
    /// Cuántas veces se vendió cada variante en los últimos 90 días (lo de eleventa y lo del POS): ordena la búsqueda
    /// cuando el texto no distingue. Se calcula una vez al día, para que el orden no cambie a cada rato. Su versión (cuándo se
    /// calculó) va en la copia de las cajas: si cambia, la nueva llega con los cambios (entrega 5, partes 2 y 3).
    /// </summary>
    private LoVendido _vendidos = LoVendido.Nada;

    /// <summary>Lo más vendido de un día, con hasta cuándo vale y su versión; se cambia entero, de un jalón.</summary>
    private sealed record LoVendido(DateTime Hasta, Dictionary<Guid, int> Veces, long Version)
    {
        public static readonly LoVendido Nada = new(DateTime.MinValue, [], 0);
    }

    /// <summary>
    /// Lo último que eligió cada caja con cada texto (normalizado): sale primero la vez siguiente. Cada uno con su número de
    /// orden: la copia de cada caja dice hasta cuál trae, y sus cambios traen lo suyo de después (entrega 5, partes 2 y 3).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Caja, string Texto), (Guid Variante, long Orden)> _elegido = new();
    private volatile bool _elegidoCargado;

    /// <summary>El número del último elegido (no vuelve atrás mientras corre el servicio; al arrancar, las cajas bajan su copia completa).</summary>
    private long _ordenElegido;

    /// <summary>
    /// Tomar el número de lo elegido y guardarlo van juntos, y leer hasta dónde va y la lista de una caja también: si no, lo
    /// que se elegía justo mientras se armaban los cambios de una caja se perdía para siempre (el número ya contaba y lo elegido
    /// todavía no estaba).
    /// </summary>
    private readonly Lock _candadoElegido = new();

    /// <summary>Lo elegido se lee de la base una vez (al arrancar), una lectura a la vez.</summary>
    private readonly SemaphoreSlim _cargandoElegido = new(1, 1);

    /// <summary>Cambia al invalidar: lo elegido que se leyó de la base de antes ya no se pone.</summary>
    private int _generacionElegido;

    /// <summary>Para las pruebas: entre tomar el número de lo elegido y guardarlo (dentro del candado).</summary>
    internal Action? EntreElNumeroYLoElegido { get; set; }

    /// <summary>Para las pruebas: entre leer de la base lo elegido (al arrancar) y ponerlo.</summary>
    internal Func<Task>? AlLeerLoElegido { get; set; }

    /// <summary>La base cambió entera (la prueba empezó de cero): se vuelve a cargar aunque la versión coincida.</summary>
    public void Invalidar()
    {
        lock (_candado)
        {
            _generacion++;
            _indice = Vacio;
            _recargando = null;
        }
        Volatile.Write(ref _vendidos, LoVendido.Nada);
        lock (_candadoElegido)
        {
            _generacionElegido++;
            _elegido.Clear();
            _elegidoCargado = false;
        }
    }

    /// <summary>La clave de «lo elegido antes»: lo escrito sin acentos, mayúsculas ni espacios de más.</summary>
    public static string ClaveDeBusqueda(string texto) => IndiceDeBusqueda.Clave(texto);

    /// <summary>Lo que eligió una caja con un texto: la próxima vez sale primero.</summary>
    public void Aprender(Guid cajaId, string texto, Guid varianteId)
    {
        var clave = (cajaId, ClaveDeBusqueda(texto));
        lock (_candadoElegido)
        {
            var orden = ++_ordenElegido;
            EntreElNumeroYLoElegido?.Invoke();
            _elegido[clave] = (varianteId, orden);
        }
    }

    /// <summary>
    /// Lo que eligió esta caja con cada texto, para su copia (entrega 5, parte 3): con eso la búsqueda de la caja ordena igual
    /// que aquí. Solo lo de después de <paramref name="desde"/> (lo que su copia ya trae no), en el orden de los textos, y hasta
    /// dónde llega: la copia lo guarda y sus cambios piden desde ahí (entrega 5, parte 2).
    /// </summary>
    public async Task<(long Version, List<ElegidoDto> Elegidos)> ElegidosAsync(PosDbContext db, Guid cajaId, long desde = 0, CancellationToken ct = default)
    {
        await CargarElegidoAsync(db, ct);
        // Hasta dónde va y lo de después de «desde», del mismo momento: lo que se elija mientras tanto, viene la próxima vez.
        lock (_candadoElegido)
        {
            var elegidos = _elegido.Where(x => x.Key.Caja == cajaId && x.Value.Orden > desde)
                .Select(x => new ElegidoDto(x.Key.Texto, x.Value.Variante)).OrderBy(e => e.Texto, StringComparer.Ordinal).ToList();
            return (_ordenElegido, elegidos);
        }
    }

    /// <summary>
    /// Lo elegido que guardó la base, la primera vez que hace falta (al arrancar), una lectura a la vez. Lo que se eligió
    /// mientras se leía es más nuevo que lo de la base y se queda: antes la lectura lo pisaba con lo de antes.
    /// </summary>
    private async Task CargarElegidoAsync(PosDbContext db, CancellationToken ct)
    {
        if (_elegidoCargado)
            return;
        await _cargandoElegido.WaitAsync(ct);
        try
        {
            if (_elegidoCargado)
                return;
            var generacion = Volatile.Read(ref _generacionElegido);
            // Lo más reciente de cada caja y texto gana: se recorre en orden de fecha.
            var elegidas = await db.Busquedas.AsNoTracking().Where(b => b.VarianteId != null).OrderBy(b => b.FechaUtc)
                .Select(b => new { b.CajaId, b.Texto, b.VarianteId }).ToListAsync(ct);
            var ultimo = new Dictionary<(Guid Caja, string Texto), Guid>();
            foreach (var b in elegidas)
                ultimo[(b.CajaId, b.Texto)] = b.VarianteId!.Value;
            if (AlLeerLoElegido is { } alLeer)
                await alLeer();
            lock (_candadoElegido)
            {
                // La base cambió entera mientras se leía (la prueba empezó de cero): lo leído era de la otra.
                if (generacion != _generacionElegido)
                    return;
                foreach (var (clave, variante) in ultimo)
                    _elegido.TryAdd(clave, (variante, ++_ordenElegido));
                _elegidoCargado = true;
            }
        }
        finally
        {
            _cargandoElegido.Release();
        }
    }

    /// <summary>
    /// Cuántas veces se vendió cada variante en 90 días, lo mismo con que ordena la búsqueda: la página de Productos abre con
    /// lo más vendido primero (entrega 8). Se calcula una vez al día, así que pedirlo no cuesta.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> VecesVendidoAsync(PosDbContext db, CancellationToken ct = default) => await VendidosAsync(db, ct);

    /// <summary>
    /// Lo más vendido como va en la copia de las cajas (entrega 5, parte 3), en el orden de las variantes y con su versión
    /// (cuándo se calculó): con la misma, la caja ya lo tiene.
    /// </summary>
    public async Task<(long Version, List<VecesVendidoDto> Vendidos)> VendidosParaLaCopiaAsync(PosDbContext db, CancellationToken ct = default)
    {
        await VendidosAsync(db, ct);
        var vendidos = Volatile.Read(ref _vendidos);
        return (vendidos.Version, vendidos.Veces.Where(x => x.Value > 0).OrderBy(x => x.Key).Select(x => new VecesVendidoDto(x.Key, x.Value)).ToList());
    }

    private async Task<Dictionary<Guid, int>> VendidosAsync(PosDbContext db, CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;
        var vigente = Volatile.Read(ref _vendidos);
        if (ahora < vigente.Hasta)
            return vigente.Veces;
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
        // La versión: cuándo se calculó (no se repite mientras corre el servicio).
        Volatile.Write(ref _vendidos, new LoVendido(ahora.AddDays(1), veces, Math.Max(ahora.Ticks, vigente.Version + 1)));
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

        // Las marcas se aprenden de todo el catálogo; también valen las de los productos. La copia de las cajas lleva las mismas
        // y sus sinónimos, y arma la misma búsqueda (entrega 5, parte 3).
        var marcas = variantes.Select(v => v.Marca).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
        var sinonimos = await db.Sinonimos.AsNoTracking().Where(s => s.Activo).Select(s => new { s.Palabra, s.Equivale }).ToListAsync(ct);
        var busqueda = IndiceDeBusqueda.Armar(
            porVariante.Values.Select(a => new ProductoParaBuscar(a.VarianteId, a.Descripcion, a.Codigo, codigosPorVariante[a.VarianteId].Select(c => c.Codigo))),
            marcas, sinonimos.Select(s => (s.Palabra, s.Equivale)));
        // Los cambios de la copia de una caja llevan todos los códigos de cada producto que cambió, como la copia completa.
        return new Indice(version, porVariante, new CodigosDeEscaneo(deLoQueSeVende), deLoQueSeVende, marcas, busqueda, deLoQueSeVende.ToLookup(c => c.VarianteId));
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
        // Sin importar mayúsculas; entre los que solo cambian en eso, el igual tal cual (CodigosDeEscaneo, como la copia de las cajas).
        var elCodigo = indice.Escaneo.Buscar(codigo.Trim());
        if (elCodigo is null)
        {
            indice = await AlDiaAsync(db, indice, ct);
            elCodigo = indice.Escaneo.Buscar(codigo.Trim());
            if (elCodigo is null)
                return null;
        }
        return await ProductoAsync(db, indice.Version, indice.PorVariante[elCodigo.VarianteId], elCodigo.PresentacionId, sucursalId, ct);
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
    /// mismo texto; lo que empieza con lo escrito; lo más vendido en 90 días (docs/propuesta-interfaz.md). Es la misma
    /// búsqueda que hace cada caja con su copia (<see cref="IndiceDeBusqueda"/>); aquí, con las existencias de ahora.
    /// </summary>
    public async Task<List<ResultadoBusquedaDto>> BuscarAsync(PosDbContext db, string texto, Guid sucursalId, int maximo = 30, CancellationToken ct = default,
        Guid? cajaId = null)
    {
        if (ClaveDeBusqueda(texto).Length == 0)
            return [];
        var indice = await ActualAsync(ct);
        var vendidos = await VendidosAsync(db, ct);
        Guid? elegido = null;
        if (cajaId is { } caja)
        {
            await CargarElegidoAsync(db, ct);
            elegido = _elegido.TryGetValue((caja, ClaveDeBusqueda(texto)), out var v) ? v.Variante : null;
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
    /// Todo el catálogo vendible de una sucursal, con existencias: la copia de una caja para vender sin conexión. Con la
    /// versión de la base de ahora (si la recarga sigue en camino, la espera: la caja la baja en segundo plano). Con todos los
    /// códigos (también los que solo cambian en mayúsculas: al escanear, la misma regla que aquí) y las marcas con que se leen
    /// los nombres, para que la caja arme la misma búsqueda (entrega 5, parte 3).
    /// </summary>
    /// <param name="existencias">Las de la sucursal, leídas por quien arma la copia (con su versión).</param>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos, IReadOnlyList<string> Marcas)> TodosAsync(
        PosDbContext db, IReadOnlyDictionary<Guid, decimal> existencias, CancellationToken ct = default)
    {
        var indice = await AlMenosAsync(await VersionDeLaBaseAsync(db, ct), ct);
        var productos = indice.PorVariante.Values.Select(a => Producto(a, null, indice.Version, existencias)).ToList();
        return (indice.Version, productos, indice.Codigos, indice.Marcas);
    }

    /// <summary>
    /// Los cambios de la copia de una caja (entrega 5, parte 2): esas variantes como van en la copia, con todos sus códigos, del
    /// índice de esa versión o de una más nueva; las que ya no están en el catálogo vendible van aparte, para quitarlas. Y las
    /// marcas con que se leen los nombres en ese índice: las de la búsqueda de la caja (entrega 5, parte 3).
    /// </summary>
    /// <param name="existencias">Las de esas variantes en la sucursal, leídas por quien arma los cambios.</param>
    public async Task<(long Version, List<ProductoVentaDto> Productos, List<CodigoLocalDto> Codigos, List<Guid> Quitados, IReadOnlyList<string> Marcas)> ParaLaCopiaAsync(
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
        return (indice.Version, productos, codigos, quitados, indice.Marcas);
    }

    /// <summary>Cuántos productos tiene el índice vigente (para decidir si unos cambios ya son casi la copia completa).</summary>
    public int Productos => Volatile.Read(ref _indice).PorVariante.Count;

    /// <summary>Un producto como lo vende la caja; con <paramref name="existencias"/>, con la suya (sin renglón, cero).</summary>
    private static ProductoVentaDto Producto(Articulo a, Guid? presentacionId, long version, IReadOnlyDictionary<Guid, decimal>? existencias) =>
        new(a.VarianteId, a.Descripcion, a.Codigo, a.Impuesto, a.Unidad, a.LlevaInventario,
            a.LlevaInventario && existencias is not null ? existencias.GetValueOrDefault(a.VarianteId) : null,
            a.Presentaciones.FirstOrDefault(p => p.Id == presentacionId) ?? a.Presentaciones.First(p => p.EsBase), a.Presentaciones, a.Precios, version);

    private static async Task<ProductoVentaDto> ProductoAsync(
        PosDbContext db, long version, Articulo a, Guid? presentacionId, Guid sucursalId, CancellationToken ct) =>
        Producto(a, presentacionId, version, a.LlevaInventario ? await ExistenciasAsync(db, [a.VarianteId], sucursalId, ct) : null);

    private static async Task<Dictionary<Guid, decimal>> ExistenciasAsync(PosDbContext db, List<Guid> variantes, Guid sucursalId, CancellationToken ct) =>
        (await db.Existencias.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && variantes.Contains(e.VarianteId))
            .ToListAsync(ct))
        .ToDictionary(e => e.VarianteId, e => e.Cantidad.EnUnidades);
}
