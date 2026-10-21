using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// F3 Productos (docs/fase-2-inventario.md, entrega 1): buscar, ver y editar un producto con sus variantes,
/// presentaciones, códigos y precios; el código interno para lo que no trae; y el alta rápida desde la caja.
/// Los cambios de precio quedan en el historial (lo escribe la base) y en la bitácora.
/// <para>
/// Con <paramref name="lecturaDeEleventa"/> (el piloto con eleventa, con su lectura prendida), de los productos de eleventa
/// no se cambia lo que pone la lectura (<see cref="TrastiendaEnElPiloto"/>): la ficha lo dice y al guardar se revisa.
/// </para>
/// </summary>
public sealed class RegistroCatalogo(PosDbContext db, bool lecturaDeEleventa = false)
{
    private const string SecuenciaCodigoInterno = "codigo-interno";
    private static readonly Cantidad Una = Cantidad.Unidades(1);

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    // ── Consultar ────────────────────────────────────────────────────────────

    public async Task<List<ProductoCatalogoDto>> BuscarAsync(
        Guid sucursalId, string? texto, Guid? departamentoId, bool soloPorCompletar, bool conInactivos, int maximo = 100, CancellationToken ct = default)
    {
        var consulta = db.Productos.AsNoTracking().AsQueryable();
        if (!conInactivos)
            consulta = consulta.Where(p => p.Activo);
        if (departamentoId is { } d)
            consulta = consulta.Where(p => p.DepartamentoId == d);
        if (soloPorCompletar)
            consulta = consulta.Where(p => p.PorCompletar);
        var productos = await consulta
            .Select(p => new
            {
                p.Id, p.Nombre, Marca = p.Marca == null ? null : p.Marca.Nombre, Departamento = p.Departamento!.Nombre, p.Activo, p.PorCompletar,
                Variantes = p.Variantes.Select(v => v.Id).ToList(),
            })
            .ToListAsync(ct);

        var codigos = await db.CodigosBarras.AsNoTracking()
            .Select(c => new { c.Codigo, c.Presentacion!.VarianteId, c.Presentacion.EsBase }).ToListAsync(ct);
        var codigosPorVariante = codigos.ToLookup(c => c.VarianteId);
        if (!string.IsNullOrWhiteSpace(texto))
        {
            var palabras = Texto.Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            productos = productos.Where(p =>
            {
                var todo = Texto.Normalizar($"{p.Nombre} {p.Marca} {string.Join(" ", p.Variantes.SelectMany(v => codigosPorVariante[v].Select(c => c.Codigo)))}");
                return palabras.All(todo.Contains);
            }).ToList();
        }
        productos = productos.OrderBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase).Take(maximo).ToList();

        var primeras = productos.Where(p => p.Variantes.Count > 0).Select(p => p.Variantes[0]).ToList();
        var precios = await db.Precios.AsNoTracking()
            .Where(x => primeras.Contains(x.VarianteId) && x.PresentacionId == null && x.SucursalId == null).ToListAsync(ct);
        var existencias = await db.Existencias.AsNoTracking()
            .Where(e => e.SucursalId == sucursalId && primeras.Contains(e.VarianteId)).ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad.EnUnidades, ct);
        return productos.Select(p =>
        {
            var primera = p.Variantes.Count > 0 ? p.Variantes[0] : (Guid?)null;
            var menudeo = primera is { } v ? precios.Where(x => x.VarianteId == v).MinBy(x => x.CantidadMinima)?.PrecioCentavos : null;
            var codigo = primera is { } w ? codigosPorVariante[w].OrderByDescending(c => c.EsBase).Select(c => c.Codigo).FirstOrDefault() : null;
            return new ProductoCatalogoDto(p.Id, p.Nombre, p.Marca, p.Departamento, p.Variantes.Count, codigo, menudeo,
                primera is { } x && existencias.TryGetValue(x, out var e) ? e : null, p.Activo, p.PorCompletar);
        }).ToList();
    }

    /// <summary>
    /// Los departamentos que se ofrecen (los activos) y las marcas. Los desactivados van aparte, solo para decir el de un
    /// producto que se quedó en uno (Productos › Departamentos).
    /// </summary>
    public async Task<OpcionesCatalogoDto> OpcionesAsync(CancellationToken ct = default)
    {
        var departamentos = (await db.Departamentos.AsNoTracking().ToListAsync(ct)).OrderBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
        List<OpcionCatalogoDto> Opciones(bool activos) => departamentos.Where(d => d.Activo == activos).Select(d => new OpcionCatalogoDto(d.Id, d.Nombre)).ToList();
        return new(Opciones(activos: true),
            (await db.Marcas.AsNoTracking().Where(m => m.Activa).Select(m => m.Nombre).ToListAsync(ct)).Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
            Opciones(activos: false));
    }

    /// <summary>
    /// La ficha completa. El costo solo va si <paramref name="conCostos"/> (dueño y encargado). En el piloto con eleventa,
    /// dice qué pone la lectura en los productos de eleventa (eso no se cambia aquí).
    /// </summary>
    public async Task<FichaProductoDto?> FichaAsync(Guid productoId, Guid sucursalId, bool conCostos, CancellationToken ct = default)
    {
        var p = await db.Productos.AsNoTracking().Include(x => x.Marca)
            .Include(x => x.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(pr => pr.Codigos)
            .AsSplitQuery().SingleOrDefaultAsync(x => x.Id == productoId, ct);
        if (p is null)
            return null;
        var ids = p.Variantes.Select(v => v.Id).ToList();
        var precios = (await db.Precios.AsNoTracking().Where(x => ids.Contains(x.VarianteId) && x.SucursalId == null).ToListAsync(ct)).ToLookup(x => x.VarianteId);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId && ids.Contains(e.VarianteId)).ToDictionaryAsync(e => e.VarianteId, ct);
        var proveedores = conCostos
            ? (await db.ProveedoresVariantes.AsNoTracking().Include(x => x.Proveedor).Where(x => ids.Contains(x.VarianteId)).ToListAsync(ct)).ToLookup(x => x.VarianteId)
            : null;
        var deEleventa = lecturaDeEleventa ? await TrastiendaEnElPiloto.DeEleventaAsync(db, ids, ct) : new HashSet<Guid>();
        var promociones = await TrastiendaEnElPiloto.PromocionesAsync(db, deEleventa.SelectMany(v => precios[v]).Select(x => x.Id), ct);
        bool PoneLaLectura(Precio x) => deEleventa.Contains(x.VarianteId) && (EsMenudeo(x) || promociones.Contains(x.Id));

        return new FichaProductoDto(p.Id, p.Nombre, p.DepartamentoId, p.Marca?.Nombre, p.Tipo, p.Impuesto, p.ClaveSat, p.Activo, p.PorCompletar,
            p.Variantes.OrderBy(v => v.Descripcion, StringComparer.CurrentCultureIgnoreCase).Select(v =>
            {
                var presentaciones = v.Presentaciones.OrderByDescending(x => x.EsBase).ThenBy(x => x.Factor).ToList();
                var existencia = existencias.GetValueOrDefault(v.Id);
                return new VarianteFichaDto(v.Id, v.Descripcion, v.UnidadBase, v.Activa,
                    presentaciones.Select(x => new PresentacionFichaDto(x.Id, x.Nombre, x.Factor.Milesimas, x.EsBase,
                        x.Codigos.OrderBy(c => c.Codigo).Select(c => new CodigoFichaDto(c.Codigo, c.Origen)).ToList())).ToList(),
                    precios[v.Id].OrderBy(x => x.PresentacionId is not null).ThenBy(x => x.CantidadMinima)
                        .Select(x => new PrecioFichaDto(presentaciones.FirstOrDefault(pr => pr.Id == x.PresentacionId)?.Nombre, x.CantidadMinima.Milesimas, x.PrecioCentavos,
                            PoneLaLectura(x)))
                        .ToList(),
                    existencia?.Cantidad.EnUnidades, conCostos ? existencia?.CostoPromedio : null,
                    proveedores?[v.Id].Select(x => new ProveedorFichaDto(x.ProveedorId, x.Proveedor!.Nombre, x.CodigoProveedor, x.UltimoCostoCentavos,
                        x.UltimaCompraUtc is { } u ? HoraMexico.ALocal(u) : null)).ToList(),
                    deEleventa.Contains(v.Id), new MinimoYMaximoDto(v.InventarioMinimo?.Milesimas, v.InventarioMaximo?.Milesimas));
            }).ToList(),
            deEleventa.Count > 0 ? TrastiendaEnElPiloto.ProductosEnEleventa : null, DeEleventaCompleto(p.Variantes, deEleventa));
    }

    /// <summary>El precio de menudeo, el que pone la lectura de eleventa: por unidad base, desde una.</summary>
    private static bool EsMenudeo(Precio x) => x.PresentacionId is null && x.SucursalId is null && x.CantidadMinima == Una;

    /// <summary>
    /// El nombre, el departamento y si se vende los pone la lectura cuando el producto es una sola variante de eleventa (como
    /// allá); si la depuración le juntó otras, o se le agregaron aquí, ya no.
    /// </summary>
    private static bool DeEleventaCompleto(IReadOnlyCollection<Variante> variantes, HashSet<Guid> deEleventa) =>
        variantes.Count == 1 && deEleventa.Contains(variantes.First().Id);

    /// <summary>Los últimos movimientos de una variante en la sucursal, con la existencia que dejó cada uno.</summary>
    public async Task<List<MovimientoKardexDto>> KardexAsync(Guid varianteId, Guid sucursalId, int maximo = 200, CancellationToken ct = default)
    {
        var movimientos = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.VarianteId == varianteId && m.SucursalId == sucursalId).OrderBy(m => m.FechaUtc).ToListAsync(ct);
        var resultado = new List<MovimientoKardexDto>();
        var existencia = Cantidad.Cero;
        foreach (var m in movimientos)
        {
            existencia += m.Cantidad;
            resultado.Add(new MovimientoKardexDto(HoraMexico.ALocal(m.FechaUtc), m.Tipo, m.Cantidad.EnUnidades, existencia.EnUnidades, m.Motivo, m.DocumentoTipo));
        }
        resultado.Reverse();
        return resultado.Take(maximo).ToList();
    }

    // ── La página de Productos (entrega 8) ───────────────────────────────────

    /// <summary>
    /// La lista de la página de Productos, que abre llena (docs/propuesta-interfaz.md, «F3 Productos abre vacío hasta que se
    /// busca»). Sin texto, primero lo más vendido en 90 días (<paramref name="vendidos"/>, lo mismo con que la búsqueda de la
    /// venta ordena lo que el texto no distingue) y luego por nombre; con texto, antes el código exacto y lo que empieza con lo
    /// escrito. Cada palabra escrita va en el nombre, la marca, el departamento o un código, como en el prototipo. Trae los
    /// primeros <paramref name="maximo"/> y cuántos hay. El margen bajo y el margen de cada uno, solo para quien ve la
    /// utilidad: el servicio lo revisa aquí.
    /// </summary>
    public async Task<ListaProductosDto> ListaAsync(
        Guid sucursalId, Guid usuarioId, string? texto, FiltroProductos filtro, Guid? departamentoId, bool conInactivos,
        IReadOnlyDictionary<Guid, int> vendidos, int maximo = 50, CancellationToken ct = default)
    {
        var veUtilidad = await new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        if (filtro == FiltroProductos.MargenBajo && !veUtilidad)
            throw new PermisoRequerido(AccionControlada.VerUtilidad, NivelPermiso.NoPermitido,
                "El margen lo ve quien tiene el permiso «Ver costos, utilidad y margen».");
        maximo = Math.Clamp(maximo, 1, 500);

        var consulta = db.Productos.AsNoTracking().AsQueryable();
        if (!conInactivos)
            consulta = consulta.Where(p => p.Activo);
        var todos = await consulta
            .Select(p => new
            {
                p.Id, p.Nombre, Marca = p.Marca == null ? null : p.Marca.Nombre, p.DepartamentoId, Departamento = p.Departamento!.Nombre,
                p.Activo, p.PorCompletar, p.Tipo,
                Variantes = p.Variantes.Select(v => new { v.Id, v.Descripcion, v.Activa, v.InventarioMinimo }).ToList(),
            })
            .ToListAsync(ct);
        var porCompletar = todos.Count(p => p.PorCompletar);
        var minimosDeInventario = todos.SelectMany(p => p.Variantes).Where(v => v.InventarioMinimo is not null)
            .ToDictionary(v => v.Id, v => v.InventarioMinimo);

        var codigos = (await db.CodigosBarras.AsNoTracking()
                .Select(c => new { c.Codigo, c.Presentacion!.VarianteId, c.Presentacion.EsBase }).ToListAsync(ct))
            .ToLookup(c => c.VarianteId);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId)
            .ToDictionaryAsync(e => e.VarianteId, e => (e.Cantidad, e.CostoPromedio), ct);
        // La primera variante, la que se ve en la lista: las activas primero y por nombre, como en la ficha.
        static Guid? Primera<T>(IEnumerable<T> variantes, Func<T, Guid> id, Func<T, bool> activa, Func<T, string> descripcion) =>
            variantes.OrderByDescending(activa).ThenBy(descripcion, StringComparer.CurrentCultureIgnoreCase).Select(v => (Guid?)id(v)).FirstOrDefault();

        var productos = todos.Select(p => new
            {
                Producto = p,
                Primera = Primera(p.Variantes, v => v.Id, v => v.Activa, v => v.Descripcion),
                Codigos = p.Variantes.SelectMany(v => codigos[v.Id].Select(c => c.Codigo)).ToList(),
                Vendidos = p.Variantes.Sum(v => vendidos.GetValueOrDefault(v.Id)),
            })
            .Where(x => departamentoId is not { } d || x.Producto.DepartamentoId == d)
            .ToList();

        var consultaTexto = Texto.Normalizar(texto ?? "");
        if (consultaTexto.Length > 0)
        {
            var palabras = consultaTexto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            productos = productos.Where(x =>
            {
                var todo = Texto.Normalizar($"{x.Producto.Nombre} {x.Producto.Marca} {x.Producto.Departamento} {string.Join(" ", x.Codigos)}");
                return palabras.All(todo.Contains);
            }).ToList();
        }

        // El margen de su primera variante, para el filtro y para quien lo ve: precio de menudeo contra costo promedio.
        var primeras = productos.Where(x => x.Primera is not null).Select(x => x.Primera!.Value).ToList();
        var menudeos = (await db.Precios.AsNoTracking()
                .Where(x => x.PresentacionId == null && x.SucursalId == null).Select(x => new { x.VarianteId, x.CantidadMinima, x.PrecioCentavos })
                .ToListAsync(ct))
            .GroupBy(x => x.VarianteId).ToDictionary(g => g.Key, g => g.MinBy(x => x.CantidadMinima)!.PrecioCentavos);
        var general = AlertasTelegram.Configuracion(db, sucursalId).MargenMinimoCentesimas;
        var minimos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.MargenMinimoCentesimas, ct);
        (int Margen, int Minimo)? MargenDe(Guid departamento, Guid? variante)
        {
            if (variante is not { } v || !menudeos.TryGetValue(v, out var precio) || precio <= 0
                || !existencias.TryGetValue(v, out var e) || e.CostoPromedio is not { } costo)
                return null;
            return (Margen.Calcular(precio, costo), minimos.GetValueOrDefault(departamento) ?? general);
        }
        Cantidad CantidadDe(Guid? variante) => variante is { } v && existencias.TryGetValue(v, out var e) ? e.Cantidad : Cantidad.Cero;
        decimal ExistenciaDe(Guid? variante) => CantidadDe(variante).EnUnidades;
        Cantidad? MinimoDe(Guid? variante) => variante is { } v ? minimosDeInventario.GetValueOrDefault(v) : null;

        productos = filtro switch
        {
            FiltroProductos.PorCompletar => productos.Where(x => x.Producto.PorCompletar).ToList(),
            // En su mínimo o debajo: el propio de su primera variante o, sin él, 3 o menos (PocaExistencia).
            FiltroProductos.PocaExistencia => productos
                .Where(x => x.Producto.Tipo == TipoProducto.Mercancia && x.Primera is not null
                    && PocaExistencia.Es(CantidadDe(x.Primera), MinimoDe(x.Primera))).ToList(),
            FiltroProductos.MargenBajo => productos
                .Where(x => MargenDe(x.Producto.DepartamentoId, x.Primera) is { } m && m.Margen < m.Minimo).ToList(),
            _ => productos,
        };

        var crudo = (texto ?? "").Trim();
        var ordenados = productos
            .OrderByDescending(x => crudo.Length > 0 && x.Codigos.Any(c => string.Equals(c, crudo, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(x => consultaTexto.Length > 0 && Texto.Normalizar(x.Producto.Nombre).StartsWith(consultaTexto, StringComparison.Ordinal))
            .ThenByDescending(x => x.Vendidos)
            .ThenBy(x => x.Producto.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximo)
            .ToList();

        return new ListaProductosDto(ordenados.Select(x =>
            {
                var p = x.Producto;
                var servicio = p.Tipo != TipoProducto.Mercancia;
                var margen = veUtilidad ? MargenDe(p.DepartamentoId, x.Primera) : null;
                var codigo = x.Primera is { } v ? codigos[v].OrderByDescending(c => c.EsBase).Select(c => c.Codigo).FirstOrDefault() : null;
                return new ProductoEnListaDto(p.Id, p.Nombre, p.Marca, p.Departamento, p.Variantes.Count, codigo,
                    x.Primera is { } w && menudeos.TryGetValue(w, out var precio) ? precio : null,
                    servicio || x.Primera is null ? null : ExistenciaDe(x.Primera), servicio, p.Activo, p.PorCompletar,
                    margen?.Margen, margen?.Minimo, servicio ? null : MinimoDe(x.Primera)?.EnUnidades);
            }).ToList(),
            productos.Count, porCompletar, vendidos.Count > 0);
    }

    /// <summary>
    /// Los últimos movimientos de una variante en la sucursal, para el detalle de la página de Productos: cada uno con lo
    /// que quedó, contado hacia atrás desde la existencia de hoy (la copia del kardex que se guarda con cada movimiento),
    /// sin leer todo el kardex. El folio de la venta o de la compra va como en sus pantallas.
    /// </summary>
    public async Task<List<MovimientoProductoDto>> UltimosMovimientosAsync(Guid varianteId, Guid sucursalId, int maximo = 8, CancellationToken ct = default)
    {
        maximo = Math.Clamp(maximo, 1, 50);
        var queda = (await db.Existencias.AsNoTracking().SingleOrDefaultAsync(e => e.SucursalId == sucursalId && e.VarianteId == varianteId, ct))?.Cantidad
            ?? Cantidad.Cero;
        var ultimos = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.VarianteId == varianteId && m.SucursalId == sucursalId)
            .OrderByDescending(m => m.FechaUtc).ThenByDescending(m => m.Id)
            .Take(maximo).ToListAsync(ct);

        List<Guid> De(string tipo) => ultimos.Where(m => m.DocumentoTipo == tipo && m.DocumentoId is not null).Select(m => m.DocumentoId!.Value).Distinct().ToList();
        var ventasIds = De(nameof(Venta));
        var ventas = (await db.Ventas.AsNoTracking().Where(v => ventasIds.Contains(v.Id)).Select(v => new { v.Id, v.Serie, v.Folio }).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => v.Folio is { } f ? $"{v.Serie}-{f:000000}" : null);
        var comprasIds = De(nameof(Compra));
        var compras = await (from c in db.Compras.AsNoTracking()
                             join p in db.Proveedores.AsNoTracking() on c.ProveedorId equals p.Id
                             where comprasIds.Contains(c.Id)
                             select new { c.Id, c.Folio, Proveedor = p.Nombre }).ToDictionaryAsync(c => c.Id, c => $"C-{c.Folio:D6} · {c.Proveedor}", ct);
        var devolucionesIds = De(nameof(Devolucion));
        var devoluciones = await (from d in db.Devoluciones.AsNoTracking()
                                  join v in db.Ventas.AsNoTracking() on d.VentaId equals v.Id
                                  where devolucionesIds.Contains(d.Id)
                                  select new { d.Id, v.Serie, v.Folio }).ToDictionaryAsync(d => d.Id, d => d.Folio is { } f ? $"de {d.Serie}-{f:000000}" : null, ct);

        string? Documento(MovimientoInventario m) => m.DocumentoTipo switch
        {
            nameof(Venta) when m.DocumentoId is { } id => ventas.GetValueOrDefault(id),
            nameof(Compra) when m.DocumentoId is { } id => compras.GetValueOrDefault(id),
            nameof(Devolucion) when m.DocumentoId is { } id => devoluciones.GetValueOrDefault(id),
            nameof(ConteoCiclico) => "del día",
            nameof(ConteoDepartamento) => "completo",
            nameof(TemporadaDelAnio) => "de la temporada",
            _ => null,
        };

        var resultado = new List<MovimientoProductoDto>();
        foreach (var m in ultimos)
        {
            resultado.Add(new MovimientoProductoDto(HoraMexico.ALocal(m.FechaUtc), m.Tipo, m.Cantidad.EnUnidades, queda.EnUnidades, Documento(m), m.Motivo));
            queda -= m.Cantidad;
        }
        return resultado;
    }

    // ── Guardar ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Da de alta o actualiza un producto con todo lo suyo. Lo hacen el dueño y el encargado (regla "Editar el
    /// catálogo"). Las variantes no se borran (se desactivan: tienen historia); una presentación solo se quita si nunca
    /// se vendió. Cada precio que cambia queda en la bitácora y avisa por Telegram, salvo los del dueño.
    /// </summary>
    public async Task<Guid> GuardarAsync(SolicitudGuardarProducto s, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        var decision = await control.EvaluarAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct: ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(AccionControlada.EditarCatalogo, NivelPermiso.NoPermitido, "El catálogo lo editan el dueño y el encargado.");
        var f = s.Producto;
        Validar(f);
        var departamento = await db.Departamentos.AsNoTracking().SingleOrDefaultAsync(d => d.Id == f.DepartamentoId, ct)
            ?? throw new ArgumentException("Elige el departamento.");
        await RevisarCodigosAsync(f, ct);

        var producto = f.Id is { } id
            ? await db.Productos.Include(p => p.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(p => p.Codigos).AsSplitQuery()
                .SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new InvalidOperationException("El producto ya no existe.")
            : null;
        var nuevo = producto is null;
        // Un departamento desactivado (Productos › Departamentos) ya no se ofrece: no le llega un producto activo, ni nuevo, ni
        // que se cambie a él o se reactive en él. El que ya estaba ahí se sigue guardando mientras no cambie de departamento.
        if (!departamento.Activo && f.Activo && (producto is null || producto.DepartamentoId != departamento.Id || !producto.Activo))
            throw new InvalidOperationException($"El departamento «{departamento.Nombre}» está desactivado: elige otro.");
        if (lecturaDeEleventa && producto is not null && await CambiaLoDeEleventaAsync(producto, f, ct))
            throw new InvalidOperationException(TrastiendaEnElPiloto.ProductosEnEleventa);
        if (producto is null)
        {
            producto = new Producto { Nombre = "" };
            db.Productos.Add(producto);
        }
        else if (f.Variantes.Where(v => v.Id is not null).Select(v => v.Id!.Value).ToHashSet() is var enviadas
                 && producto.Variantes.FirstOrDefault(v => !enviadas.Contains(v.Id)) is { } quitada)
            throw new InvalidOperationException($"La variante «{quitada.Descripcion}» no se puede quitar (tiene historia): desactívala.");

        (producto.Nombre, producto.DepartamentoId, producto.Tipo, producto.Impuesto, producto.Activo, producto.PorCompletar) =
            (f.Nombre.Trim(), f.DepartamentoId, f.Tipo, f.Impuesto, f.Activo, false);
        producto.ClaveSat = string.IsNullOrWhiteSpace(f.ClaveSat) ? null : f.ClaveSat.Trim();
        producto.MarcaId = await MarcaAsync(f.Marca, ct);

        var cambios = new List<string>();
        foreach (var fv in f.Variantes)
        {
            var variante = fv.Id is { } vid
                ? producto.Variantes.FirstOrDefault(v => v.Id == vid) ?? throw new InvalidOperationException("Una variante ya no existe.")
                : null;
            if (variante is null)
            {
                variante = new Variante { ProductoId = producto.Id };
                producto.Variantes.Add(variante);
                db.Variantes.Add(variante);
            }
            else if (variante.UnidadBase != fv.Unidad && await TieneHistoriaAsync(variante.Id, ct))
                throw new InvalidOperationException($"«{Nombre(producto, variante)}» ya tiene movimientos: su unidad no se cambia aquí (llega con la depuración).");
            (variante.Descripcion, variante.UnidadBase, variante.Activa) = (fv.Descripcion.Trim(), fv.Unidad, fv.Activa);
            if (MinimoYMaximo(producto, variante, f.Tipo, fv.MinimoYMaximo, nueva: fv.Id is null) is { } cambioMinimo)
                cambios.Add(cambioMinimo);
            await PresentacionesAsync(variante, fv, ct);
            cambios.AddRange(await PreciosAsync(producto, variante, fv, nuevo, ct));
        }

        var nombre = producto.Nombre;
        control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo, nuevo ? $"Alta de «{nombre}»" : $"Editó «{nombre}»");
        foreach (var cambio in cambios)
            control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo, cambio);
        await db.SaveChangesAsync(ct);
        return producto.Id;
    }

    /// <summary>
    /// Solo el precio de menudeo de una variante: el sugerido después de una compra con margen bajo, u otro. Queda en la
    /// bitácora y avisa por Telegram como cualquier cambio de precio del catálogo.
    /// </summary>
    public async Task CambiarMenudeoAsync(SolicitudPrecioMenudeo s, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct: ct)).Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(AccionControlada.EditarCatalogo, NivelPermiso.NoPermitido, "Los precios los cambian el dueño y el encargado.");
        if (lecturaDeEleventa && (await TrastiendaEnElPiloto.DeEleventaAsync(db, [s.VarianteId], ct)).Count > 0)
            throw new InvalidOperationException(TrastiendaEnElPiloto.ProductosEnEleventa);
        if (s.PrecioCentavos <= 0)
            throw new ArgumentException("Escribe el precio.");
        var variante = await db.Variantes.Include(v => v.Producto).SingleOrDefaultAsync(v => v.Id == s.VarianteId, ct)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        var menudeo = (await db.Precios.Where(p => p.VarianteId == variante.Id && p.PresentacionId == null && p.SucursalId == null).ToListAsync(ct))
            .MinBy(p => p.CantidadMinima);
        if (menudeo is null)
        {
            db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = s.PrecioCentavos });
            control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
                $"Precio de «{Nombre(variante.Producto!, variante)}»: nuevo {s.PrecioCentavos / 100m:N2}");
        }
        else if (menudeo.PrecioCentavos != s.PrecioCentavos)
        {
            control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
                $"Precio de «{Nombre(variante.Producto!, variante)}»: {menudeo.PrecioCentavos / 100m:N2} → {s.PrecioCentavos / 100m:N2}");
            menudeo.PrecioCentavos = s.PrecioCentavos;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// En el piloto con eleventa, si la ficha cambia algo que pone la lectura en un producto de eleventa: el nombre, el
    /// departamento y si se vende (si es una sola variante de eleventa); de cada variante de eleventa, si está activa, cuál es
    /// su presentación base y los códigos que ya tiene (se pueden agregar otros), y sus precios de eleventa (el de menudeo y
    /// las promociones). Lo demás (las escalas del F11 o de F3, las otras presentaciones, la marca, las variantes nuevas) sí.
    /// </summary>
    private async Task<bool> CambiaLoDeEleventaAsync(Producto producto, FichaProductoDto f, CancellationToken ct)
    {
        var deEleventa = await TrastiendaEnElPiloto.DeEleventaAsync(db, producto.Variantes.Select(v => v.Id), ct);
        if (deEleventa.Count == 0)
            return false;
        if (DeEleventaCompleto(producto.Variantes, deEleventa)
            && (f.Nombre.Trim() != producto.Nombre || f.DepartamentoId != producto.DepartamentoId || f.Activo != producto.Activo))
            return true;
        var ids = deEleventa.ToList();
        var precios = (await db.Precios.AsNoTracking().Where(x => ids.Contains(x.VarianteId) && x.PresentacionId == null && x.SucursalId == null).ToListAsync(ct))
            .ToLookup(x => x.VarianteId);
        var promociones = await TrastiendaEnElPiloto.PromocionesAsync(db, precios.SelectMany(g => g).Select(x => x.Id), ct);
        foreach (var variante in producto.Variantes.Where(v => deEleventa.Contains(v.Id)))
        {
            // Si no viene, se quería quitar: eso lo rechaza la revisión de siempre (tiene historia).
            if (f.Variantes.FirstOrDefault(x => x.Id == variante.Id) is not { } fv)
                continue;
            if (fv.Activa != variante.Activa)
                return true;
            if (variante.Presentaciones.FirstOrDefault(x => x.EsBase) is { } pieza)
            {
                var enviada = fv.Presentaciones.FirstOrDefault(x => x.Id == pieza.Id);
                var codigos = (enviada?.Codigos ?? []).Select(c => c.Codigo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (enviada is not { EsBase: true } || pieza.Codigos.Any(c => !codigos.Contains(c.Codigo)))
                    return true;
            }
            foreach (var suyo in precios[variante.Id].Where(x => EsMenudeo(x) || promociones.Contains(x.Id)))
            {
                if (!fv.Precios.Any(x => x.Presentacion is null && x.CantidadMinimaMilesimas == suyo.CantidadMinima.Milesimas && x.PrecioCentavos == suyo.PrecioCentavos))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// El mínimo y el máximo de inventario de la variante, como vienen en la ficha (ya revisados en <see cref="Validar"/>). Sin
    /// ellos (una caja de antes) se quedan como estaban; un servicio no lleva inventario, así que no los tiene. Son del POS:
    /// en el piloto con eleventa también se ponen aquí, porque la lectura no los trae. Devuelve el cambio, para la bitácora.
    /// </summary>
    private static string? MinimoYMaximo(Producto producto, Variante variante, TipoProducto tipo, MinimoYMaximoDto? enviados, bool nueva)
    {
        if (enviados is null && tipo == TipoProducto.Mercancia)
            return null;
        var (minimo, maximo) = tipo == TipoProducto.Mercancia && enviados is not null
            ? (Leer(enviados.MinimoMilesimas), Leer(enviados.MaximoMilesimas))
            : (null, null);
        if (minimo == variante.InventarioMinimo && maximo == variante.InventarioMaximo)
            return null;
        var antes = TextoMinimoYMaximo(variante.InventarioMinimo, variante.InventarioMaximo);
        (variante.InventarioMinimo, variante.InventarioMaximo) = (minimo, maximo);
        return nueva ? null : $"Mínimo y máximo de «{Nombre(producto, variante)}»: {antes} → {TextoMinimoYMaximo(minimo, maximo)}";

        static Cantidad? Leer(long? milesimas) => milesimas is { } m ? new Cantidad(m) : null;
    }

    /// <summary>«mínimo 5, máximo 20», «mínimo 5, sin máximo» o «sin mínimo ni máximo»: como queda en la bitácora.</summary>
    private static string TextoMinimoYMaximo(Cantidad? minimo, Cantidad? maximo) => (minimo, maximo) switch
    {
        (null, null) => "sin mínimo ni máximo",
        ({ } min, null) => $"mínimo {min}, sin máximo",
        (null, { } max) => $"sin mínimo, máximo {max}",
        ({ } min, { } max) => $"mínimo {min}, máximo {max}",
    };

    private static void Validar(FichaProductoDto f)
    {
        if (string.IsNullOrWhiteSpace(f.Nombre))
            throw new ArgumentException("El producto lleva nombre.");
        if (f.Variantes.Count == 0)
            throw new ArgumentException("El producto lleva al menos una variante.");
        if (f.Variantes.Count > 1 && f.Variantes.Any(v => string.IsNullOrWhiteSpace(v.Descripcion)))
            throw new ArgumentException("Con varias variantes, cada una lleva su descripción (Azul, Rojo…).");
        foreach (var v in f.Variantes)
        {
            var quien = string.IsNullOrWhiteSpace(v.Descripcion) ? f.Nombre.Trim() : $"{f.Nombre.Trim()} {v.Descripcion.Trim()}";
            if (v.Presentaciones.Count(p => p.EsBase) != 1)
                throw new ArgumentException($"«{quien}» lleva una sola presentación base (la pieza, el metro…).");
            if (v.Presentaciones.Single(p => p.EsBase).FactorMilesimas != Una.Milesimas)
                throw new ArgumentException($"La presentación base de «{quien}» contiene una unidad.");
            foreach (var p in v.Presentaciones)
            {
                if (string.IsNullOrWhiteSpace(p.Nombre))
                    throw new ArgumentException($"Cada presentación de «{quien}» lleva nombre.");
                if (p.FactorMilesimas <= 0)
                    throw new ArgumentException($"«{p.Nombre}» de «{quien}» debe contener más de cero unidades.");
                if (v.Unidad == UnidadBase.Pieza && p.FactorMilesimas % Cantidad.Escala != 0)
                    throw new ArgumentException($"«{p.Nombre}» de «{quien}» se cuenta en piezas enteras.");
            }
            if (v.Presentaciones.GroupBy(p => p.Nombre.Trim(), StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repetida)
                throw new ArgumentException($"«{quien}» tiene dos presentaciones «{repetida.Key}».");
            if (v.Precios.Any(p => p.PrecioCentavos <= 0 || p.CantidadMinimaMilesimas <= 0))
                throw new ArgumentException($"Los precios y las cantidades de «{quien}» van mayores a cero.");
            if (v.Precios.Any(p => p.Presentacion is { } n && v.Presentaciones.All(x => !string.Equals(x.Nombre.Trim(), n.Trim(), StringComparison.CurrentCultureIgnoreCase))))
                throw new ArgumentException($"Un precio de «{quien}» es de una presentación que no existe.");
            if (!v.Precios.Any(p => p.Presentacion is null && p.CantidadMinimaMilesimas == Una.Milesimas))
                throw new ArgumentException($"«{quien}» necesita su precio de menudeo (desde una unidad).");
            if (v.Precios.GroupBy(p => (p.Presentacion?.Trim().ToLowerInvariant(), p.CantidadMinimaMilesimas)).Any(g => g.Count() > 1))
                throw new ArgumentException($"«{quien}» tiene dos precios desde la misma cantidad.");
            if (f.Tipo == TipoProducto.Mercancia && v.MinimoYMaximo is { } mm)
                Variante.RevisarMinimoYMaximo(v.Unidad, mm.MinimoMilesimas is { } min ? new Cantidad(min) : null,
                    mm.MaximoMilesimas is { } max ? new Cantidad(max) : null, quien);
        }
        var codigos = f.Variantes.SelectMany(v => v.Presentaciones).SelectMany(p => p.Codigos).Select(c => c.Codigo.Trim()).ToList();
        if (codigos.Any(c => c.Length == 0))
            throw new ArgumentException("Hay un código vacío.");
        if (codigos.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } doble)
            throw new ArgumentException($"El código «{doble.Key}» está dos veces.");
    }

    /// <summary>Un código es único en todo el catálogo: si ya lo tiene otro producto, se dice cuál.</summary>
    private async Task RevisarCodigosAsync(FichaProductoDto f, CancellationToken ct)
    {
        var codigos = f.Variantes.SelectMany(v => v.Presentaciones).SelectMany(p => p.Codigos).Select(c => c.Codigo.Trim()).ToList();
        var propias = f.Variantes.SelectMany(v => v.Presentaciones).Where(p => p.Id is not null).Select(p => p.Id!.Value).ToList();
        var ocupado = await db.CodigosBarras.AsNoTracking()
            .Where(c => codigos.Contains(c.Codigo) && !propias.Contains(c.PresentacionId))
            .Select(c => new { c.Codigo, Producto = c.Presentacion!.Variante!.Producto!.Nombre })
            .FirstOrDefaultAsync(ct);
        if (ocupado is not null)
            throw new InvalidOperationException($"El código «{ocupado.Codigo}» ya es de «{ocupado.Producto}».");
    }

    private async Task<Guid?> MarcaAsync(string? nombre, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            return null;
        var normalizado = Texto.Normalizar(nombre);
        if (await db.Marcas.FirstOrDefaultAsync(m => m.NombreNormalizado == normalizado, ct) is { } marca)
            return marca.Id;
        var nueva = new Marca { Nombre = nombre };
        db.Marcas.Add(nueva);
        return nueva.Id;
    }

    private async Task<bool> TieneHistoriaAsync(Guid varianteId, CancellationToken ct) =>
        await db.MovimientosInventario.AnyAsync(m => m.VarianteId == varianteId, ct)
        || await db.Set<VentaLinea>().AnyAsync(l => l.VarianteId == varianteId, ct);

    private async Task PresentacionesAsync(Variante variante, VarianteFichaDto fv, CancellationToken ct)
    {
        var enviadas = fv.Presentaciones.Where(p => p.Id is not null).Select(p => p.Id!.Value).ToHashSet();
        foreach (var quitada in variante.Presentaciones.Where(p => !enviadas.Contains(p.Id)).ToList())
        {
            if (await db.Set<VentaLinea>().AnyAsync(l => l.PresentacionId == quitada.Id, ct) || await db.Precios.AnyAsync(p => p.PresentacionId == quitada.Id && p.SucursalId != null, ct))
                throw new InvalidOperationException($"La presentación «{quitada.Nombre}» ya se vendió: no se puede quitar.");
            if (await db.ComprasConteo.AnyAsync(c => c.PresentacionId == quitada.Id, ct) || await db.ComprasRenglones.AnyAsync(r => r.PresentacionId == quitada.Id, ct)
                || await db.OrdenesRenglones.AnyAsync(r => r.PresentacionId == quitada.Id, ct))
                throw new InvalidOperationException($"La presentación «{quitada.Nombre}» ya se usó en una compra: no se puede quitar.");
            db.CodigosBarras.RemoveRange(quitada.Codigos);
            db.LigasProveedor.RemoveRange(db.LigasProveedor.Where(l => l.PresentacionId == quitada.Id));
            db.Precios.RemoveRange(db.Precios.Where(p => p.PresentacionId == quitada.Id));
            variante.Presentaciones.Remove(quitada);
            db.Presentaciones.Remove(quitada);
        }
        foreach (var fp in fv.Presentaciones)
        {
            var presentacion = fp.Id is { } pid ? variante.Presentaciones.FirstOrDefault(p => p.Id == pid) : null;
            if (presentacion is null)
            {
                presentacion = new Presentacion { VarianteId = variante.Id, Nombre = "" };
                variante.Presentaciones.Add(presentacion);
                db.Presentaciones.Add(presentacion);
            }
            (presentacion.Nombre, presentacion.Factor, presentacion.EsBase) = (fp.Nombre.Trim(), new Cantidad(fp.FactorMilesimas), fp.EsBase);
            var codigos = fp.Codigos.Select(c => c.Codigo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var quitado in presentacion.Codigos.Where(c => !codigos.Contains(c.Codigo)).ToList())
            {
                presentacion.Codigos.Remove(quitado);
                db.CodigosBarras.Remove(quitado);
            }
            foreach (var fc in fp.Codigos.Where(c => presentacion.Codigos.All(x => !string.Equals(x.Codigo, c.Codigo.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                var codigo = new CodigoBarras { Codigo = fc.Codigo.Trim(), PresentacionId = presentacion.Id, Origen = Origen(fc.Codigo.Trim(), fc.Origen) };
                presentacion.Codigos.Add(codigo);
                db.CodigosBarras.Add(codigo);
            }
        }
    }

    /// <summary>Los precios base (los de una sucursal no se tocan aquí). Devuelve lo que cambió, para la bitácora.</summary>
    private async Task<List<string>> PreciosAsync(Producto producto, Variante variante, VarianteFichaDto fv, bool nuevo, CancellationToken ct)
    {
        var actuales = await db.Precios.Where(p => p.VarianteId == variante.Id && p.SucursalId == null).ToListAsync(ct);
        var cambios = new List<string>();
        Guid? Presentacion(string? nombre) => nombre is null ? null
            : variante.Presentaciones.First(p => string.Equals(p.Nombre, nombre.Trim(), StringComparison.CurrentCultureIgnoreCase)).Id;
        string Cual(Guid? presentacionId, Cantidad minima)
        {
            var texto = Nombre(producto, variante);
            if (presentacionId is { } pid && variante.Presentaciones.FirstOrDefault(p => p.Id == pid) is { } p)
                texto += $" ({p.Nombre})";
            return minima > Una ? $"{texto} desde {minima}" : texto;
        }

        var deseados = fv.Precios.Select(p => (Presentacion: Presentacion(p.Presentacion), Minima: new Cantidad(p.CantidadMinimaMilesimas), p.PrecioCentavos)).ToList();
        foreach (var actual in actuales)
        {
            var deseado = deseados.FirstOrDefault(d => d.Presentacion == actual.PresentacionId && d.Minima == actual.CantidadMinima);
            if (deseado == default)
            {
                db.Precios.Remove(actual);
                if (!nuevo)
                    cambios.Add($"Precio de «{Cual(actual.PresentacionId, actual.CantidadMinima)}»: {actual.PrecioCentavos / 100m:N2} → se quitó");
            }
            else if (deseado.PrecioCentavos != actual.PrecioCentavos)
            {
                if (!nuevo)
                    cambios.Add($"Precio de «{Cual(actual.PresentacionId, actual.CantidadMinima)}»: {actual.PrecioCentavos / 100m:N2} → {deseado.PrecioCentavos / 100m:N2}");
                actual.PrecioCentavos = deseado.PrecioCentavos;
            }
        }
        foreach (var d in deseados.Where(d => actuales.All(a => a.PresentacionId != d.Presentacion || a.CantidadMinima != d.Minima)))
        {
            db.Precios.Add(new Precio { VarianteId = variante.Id, PresentacionId = d.Presentacion, CantidadMinima = d.Minima, PrecioCentavos = d.PrecioCentavos });
            if (!nuevo && actuales.Count > 0)
                cambios.Add($"Precio de «{Cual(d.Presentacion, d.Minima)}»: nuevo {d.PrecioCentavos / 100m:N2}");
        }
        return cambios;
    }

    private static string Nombre(Producto p, Variante v) => string.IsNullOrWhiteSpace(v.Descripcion) ? p.Nombre : $"{p.Nombre} {v.Descripcion}";

    /// <summary>El que llega como interno (el que dio «Código interno») lo es; los demás, según el código (<see cref="CodigoBarras.OrigenDe"/>).</summary>
    private static OrigenCodigo Origen(string codigo, OrigenCodigo indicado) =>
        indicado == OrigenCodigo.Interno ? OrigenCodigo.Interno : CodigoBarras.OrigenDe(codigo);

    // ── Código interno y alta rápida ─────────────────────────────────────────

    /// <summary>
    /// Un código para lo que no trae: EAN-13 que empieza con 2 (el rango de uso interno de las tiendas), con su dígito
    /// verificador. Se imprime como Code 128 o EAN en las etiquetas.
    /// </summary>
    public async Task<string> CodigoInternoAsync(CancellationToken ct = default)
    {
        var codigo = await SiguienteCodigoInternoAsync(ct);
        await db.SaveChangesAsync(ct);
        return codigo;
    }

    /// <summary>El siguiente código interno libre, sin guardar: sirve para dar varios de una vez.</summary>
    internal async Task<string> SiguienteCodigoInternoAsync(CancellationToken ct = default)
    {
        var secuencia = await db.Secuencias.FindAsync([SecuenciaCodigoInterno], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = SecuenciaCodigoInterno };
            db.Secuencias.Add(secuencia);
        }
        string codigo;
        do
        {
            secuencia.Valor++;
            codigo = Ean13.Completar($"20{secuencia.Valor:D10}");
        }
        while (await db.CodigosBarras.AnyAsync(c => c.Codigo == codigo, ct));
        return codigo;
    }

    /// <summary>
    /// Desde la caja: un código que se escaneó y no existe. Queda con una variante, la pieza y su precio de menudeo, y
    /// "por completar" para que el encargado lo termine en F3.
    /// </summary>
    public async Task<Guid> AltaRapidaAsync(SolicitudAltaRapida s, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.Codigo) || string.IsNullOrWhiteSpace(s.Nombre))
            throw new ArgumentException("Escribe el nombre del producto.");
        if (s.PrecioCentavos <= 0)
            throw new ArgumentException("Escribe el precio.");
        var departamento = await db.Departamentos.AsNoTracking().SingleOrDefaultAsync(d => d.Id == s.DepartamentoId, ct)
            ?? throw new ArgumentException("Elige el departamento.");
        if (!departamento.Activo)
            throw new InvalidOperationException($"El departamento «{departamento.Nombre}» está desactivado: elige otro.");
        var codigo = s.Codigo.Trim();
        if (await db.CodigosBarras.AnyAsync(c => c.Codigo == codigo, ct))
            throw new InvalidOperationException($"El código «{codigo}» ya existe.");
        await new ServicioControl(db).RequerirAsync(s.UsuarioId, s.SucursalId, null, AccionControlada.AltaRapida, null,
            $"Alta rápida de «{s.Nombre.Trim()}» ({codigo}) a {s.PrecioCentavos / 100m:N2}", ct: ct);

        var producto = new Producto { Nombre = s.Nombre.Trim(), DepartamentoId = s.DepartamentoId, PorCompletar = true };
        var variante = new Variante { ProductoId = producto.Id };
        var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
        db.Productos.Add(producto);
        db.Variantes.Add(variante);
        db.Presentaciones.Add(pieza);
        db.CodigosBarras.Add(new CodigoBarras { Codigo = codigo, PresentacionId = pieza.Id, Origen = Origen(codigo, OrigenCodigo.Fabrica) });
        db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = s.PrecioCentavos });
        await db.SaveChangesAsync(ct);
        return variante.Id;
    }

    /// <summary>
    /// ¿Ve los costos (el del producto, el de lo que se cuenta o se ajusta)? Quien captura las compras («Compras: la nota y
    /// los costos») o quien ve la utilidad («Ver costos, utilidad y margen»), como en el prototipo. Al migrar, con las
    /// reglas como venían de inicio, los mismos que con los roles: el dueño y el encargado (si a un rol se le había dado o
    /// quitado «Compras», ahora sigue a ese permiso).
    /// </summary>
    public async Task<bool> VeCostosAsync(Guid usuarioId, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        return await control.PuedeAsync(usuarioId, AccionControlada.CostearCompra, ct) || await control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
    }
}
