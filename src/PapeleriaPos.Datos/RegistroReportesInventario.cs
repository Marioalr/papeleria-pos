using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los reportes del inventario que eleventa tiene en F4 (2026-10-17): lo que vale a costo, por departamento («Reporte de
/// inventario»); los movimientos del kardex por días, tipo, producto, departamento y quién («Movimientos de inventario»), y lo
/// que está en su mínimo o debajo, con cuánto pedir («Productos bajos en inventario»). Solo leen: no mueven existencias, así
/// que también van en el piloto con eleventa. Cada uno con sus permisos en «Puede» (<see cref="ConsultasInventario"/>); los
/// costos, solo con «Ver costos, utilidad y margen».
/// </summary>
public sealed class RegistroReportesInventario(PosDbContext db)
{
    /// <summary>Cuántos movimientos trae cada página, del más reciente al más viejo.</summary>
    public const int MovimientosPorPagina = 50;

    /// <summary>Cuántos productos trae cada página de lo que vale el inventario.</summary>
    public const int ProductosPorPagina = 50;

    /// <summary>Cuántos trae cada página de los bajos en inventario (cada uno con su cálculo del sugerido, si no tiene máximo).</summary>
    public const int BajosPorPagina = 100;

    /// <summary>Hasta dónde se puede pedir una página: más allá ya no hay nada (y la cuenta no se desborda).</summary>
    private const int UltimaPagina = 1_000_000;

    /// <summary>Si lo escrito coincide con más productos que esto, se pide escribir más (la consulta los lleva uno por uno).</summary>
    public const int MaximoProductosDelTexto = 2000;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>«Pluma Bic Azul»: el producto, su marca y su variante, como en el sugerido y los ajustes.</summary>
    internal static string Nombre(string producto, string? marca, string? descripcion) =>
        string.Join(" ", new[] { producto, marca, descripcion }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// Quien consulta tiene alguno de <paramref name="permisos"/> en «Puede» (y está activo); si no, el error que dice cuáles
    /// hacen falta. Consultar no se autoriza.
    /// </summary>
    private async Task ExigirAsync(Guid usuarioId, IReadOnlyList<AccionControlada> permisos, string que, CancellationToken ct)
    {
        if (await db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == usuarioId && u.Activo, ct))
        {
            var suyos = await new ServicioControl(db).PermisosAsync(usuarioId, ct);
            if (permisos.Any(p => Permisos.Evaluar(suyos, p).Nivel == NivelPermiso.Permitido))
                return;
        }
        throw new PermisoRequerido(permisos[0], NivelPermiso.NoPermitido, ConsultasInventario.QuienConsulta(que, permisos));
    }

    /// <summary>El primer código de cada variante (el de su pieza antes que el de un paquete), para leerlo en la lista.</summary>
    private async Task<Dictionary<Guid, string>> CodigosAsync(IReadOnlyCollection<Guid> variantes, CancellationToken ct) =>
        variantes.Count == 0
            ? []
            : (await db.CodigosBarras.AsNoTracking().Where(c => variantes.Contains(c.Presentacion!.VarianteId))
                .Select(c => new { c.Codigo, c.Presentacion!.VarianteId, c.Presentacion.EsBase }).ToListAsync(ct))
            .GroupBy(c => c.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.EsBase).ThenBy(c => c.Codigo, StringComparer.Ordinal).First().Codigo);

    /// <summary>Si cada palabra de lo escrito está en el nombre, la marca, la variante o un código: como busca la lista de Productos.</summary>
    private static Func<string, IEnumerable<string>, bool> Coincide(string texto)
    {
        var palabras = Texto.Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (nombre, codigos) =>
        {
            var todo = Texto.Normalizar($"{nombre} {string.Join(" ", codigos)}");
            return palabras.All(todo.Contains);
        };
    }

    // ── Lo que vale el inventario ───────────────────────────────────────────

    /// <param name="Activo">Si se vende (la variante y su producto activos).</param>
    /// <param name="Costo">El valor de su existencia en el kardex (la suma de los costos de sus movimientos).</param>
    private sealed record RenglonValor(Guid VarianteId, Guid ProductoId, string Nombre, Guid DepartamentoId, UnidadBase Unidad, bool Activo, Cantidad Cantidad,
        long Costo)
    {
        public bool ConExistencia => Cantidad.EsPositiva;
        public bool EnCero => Cantidad.Milesimas == 0;
        public bool Negativa => Cantidad.Milesimas < 0;
        public bool SinCosto => ConExistencia && Costo <= 0;

        /// <summary>A costo promedio: el valor de su existencia, si tiene existencia y costo. Lo negativo no resta.</summary>
        public long Valor => ConExistencia && Costo > 0 ? Costo : 0;
    }

    private static ValorGrupoDto Grupo(Guid? departamentoId, string nombre, IReadOnlyCollection<RenglonValor> renglones) =>
        new(departamentoId, nombre, renglones.Sum(r => r.Valor), renglones.Count(r => r.ConExistencia), renglones.Count(r => r.EnCero),
            renglones.Count(r => r.Negativa), renglones.Count(r => r.SinCosto));

    /// <summary>
    /// Lo que vale el inventario de la sucursal a costo promedio (el que usa el POS: el valor de cada existencia en el kardex),
    /// en total y por departamento, con cuántos productos tienen existencia, cuántos están en cero, en negativo o sin costo; y
    /// una página de los productos de <paramref name="departamentoId"/> (o de todos) con <paramref name="filtro"/>. Cuenta lo
    /// que se vende y lo que, sin venderse, todavía tiene existencia. Solo con «Ver costos, utilidad y margen».
    /// </summary>
    public async Task<ValorInventarioDto> ValorAsync(
        Guid sucursalId, Guid usuarioId, Guid? departamentoId, FiltroValor filtro, int pagina = 0, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, ConsultasInventario.Valor, "Lo que vale el inventario", ct);
        pagina = Math.Clamp(pagina, 0, UltimaPagina);
        var variantes = await (from v in db.Variantes.AsNoTracking()
                               join p in db.Productos.AsNoTracking() on v.ProductoId equals p.Id
                               where p.Tipo == TipoProducto.Mercancia
                               select new
                               {
                                   v.Id, v.ProductoId, Producto = p.Nombre, Marca = p.Marca == null ? null : p.Marca.Nombre, v.Descripcion, p.DepartamentoId,
                                   v.UnidadBase, Activo = v.Activa && p.Activo,
                               }).ToListAsync(ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId)
            .ToDictionaryAsync(e => e.VarianteId, e => (e.Cantidad, e.CostoCentavos), ct);
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Activo ? d.Nombre : $"{d.Nombre} (desactivado)", ct);

        var renglones = variantes
            .Select(v =>
            {
                var (cantidad, costo) = existencias.TryGetValue(v.Id, out var e) ? e : (Cantidad.Cero, 0L);
                return new RenglonValor(v.Id, v.ProductoId, Nombre(v.Producto, v.Marca, v.Descripcion), v.DepartamentoId, v.UnidadBase, v.Activo, cantidad, costo);
            })
            // Lo que se vende y lo que, sin venderse, todavía tiene existencia (positiva o negativa).
            .Where(r => r.Activo || !r.EnCero)
            .ToList();
        var porDepartamento = renglones.GroupBy(r => r.DepartamentoId)
            .Select(g => Grupo(g.Key, departamentos.GetValueOrDefault(g.Key, "Sin departamento"), g.ToList()))
            .OrderByDescending(g => g.ValorCentavos).ThenBy(g => g.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var delDepartamento = departamentoId is { } d ? renglones.Where(r => r.DepartamentoId == d) : renglones;
        // Con lo mismo, por nombre y luego por su Id: así las páginas no se repiten ni se saltan a nadie.
        var conFiltro = (filtro switch
        {
            FiltroValor.EnCero => delDepartamento.Where(r => r.EnCero).OrderBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase),
            FiltroValor.Negativa => delDepartamento.Where(r => r.Negativa).OrderBy(r => r.Cantidad).ThenBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase),
            FiltroValor.SinCosto => delDepartamento.Where(r => r.SinCosto).OrderByDescending(r => r.Cantidad).ThenBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase),
            _ => delDepartamento.Where(r => r.ConExistencia).OrderByDescending(r => r.Valor).ThenBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase),
        }).ThenBy(r => r.VarianteId).ToList();
        var deLaPagina = conFiltro.Skip(pagina * ProductosPorPagina).Take(ProductosPorPagina).ToList();
        var codigos = await CodigosAsync(deLaPagina.Select(r => r.VarianteId).ToList(), ct);
        var productos = deLaPagina.Select(r => new ProductoValorDto(r.VarianteId, r.ProductoId, r.Nombre, codigos.GetValueOrDefault(r.VarianteId),
                departamentos.GetValueOrDefault(r.DepartamentoId, "Sin departamento"), r.Unidad, r.Cantidad.EnUnidades,
                r.ConExistencia ? r.Costo * Cantidad.Escala / (decimal)r.Cantidad.Milesimas : null, r.Valor, r.Activo))
            .ToList();
        return new ValorInventarioDto(HoraMexico.ALocal(Ahora), Grupo(null, "Toda la tienda", renglones), porDepartamento, departamentoId, filtro, productos,
            conFiltro.Count, pagina, ProductosPorPagina);
    }

    // ── Movimientos ─────────────────────────────────────────────────────────

    /// <summary>
    /// Para el filtro «Quién»: los usuarios de la sucursal (y los que entran en todas), también los que ya se dieron de baja,
    /// porque sus movimientos se quedan.
    /// </summary>
    public async Task<List<OpcionCatalogoDto>> QuienesAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, ConsultasInventario.Movimientos, "Los movimientos del inventario", ct);
        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.SucursalId == null || u.SucursalId == sucursalId)
            .Select(u => new { u.Id, u.Nombre, u.Activo }).ToListAsync(ct);
        return usuarios.OrderByDescending(u => u.Activo).ThenBy(u => u.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new OpcionCatalogoDto(u.Id, u.Activo ? u.Nombre : $"{u.Nombre} (de baja)")).ToList();
    }

    /// <summary>Cómo se lee un movimiento del kardex: su tipo, con la lectura de eleventa aparte.</summary>
    public static ClaseMovimiento ClaseDe(TipoMovimiento tipo, string? documentoTipo) =>
        documentoTipo == OrigenDelMovimiento.LecturaEleventa
            ? ClaseMovimiento.LecturaEleventa
            : tipo switch
            {
                TipoMovimiento.Venta => ClaseMovimiento.Venta,
                TipoMovimiento.Devolucion => ClaseMovimiento.Devolucion,
                TipoMovimiento.Compra => ClaseMovimiento.Compra,
                TipoMovimiento.Ajuste => ClaseMovimiento.Ajuste,
                TipoMovimiento.Merma => ClaseMovimiento.Merma,
                TipoMovimiento.AperturaPaquete => ClaseMovimiento.Paquete,
                TipoMovimiento.InventarioInicial => ClaseMovimiento.InventarioInicial,
                TipoMovimiento.TraspasoSalida or TipoMovimiento.TraspasoEntrada => ClaseMovimiento.Traspaso,
                TipoMovimiento.ConsumoServicio => ClaseMovimiento.ConsumoServicio,
                _ => ClaseMovimiento.Conteo,
            };

    private static IQueryable<MovimientoInventario> DeLaClase(IQueryable<MovimientoInventario> consulta, ClaseMovimiento clase) => clase switch
    {
        ClaseMovimiento.LecturaEleventa => consulta.Where(m => m.DocumentoTipo == OrigenDelMovimiento.LecturaEleventa),
        ClaseMovimiento.Venta => consulta.Where(m => m.Tipo == TipoMovimiento.Venta),
        ClaseMovimiento.Devolucion => consulta.Where(m => m.Tipo == TipoMovimiento.Devolucion),
        ClaseMovimiento.Compra => consulta.Where(m => m.Tipo == TipoMovimiento.Compra),
        ClaseMovimiento.Ajuste => consulta.Where(m => m.Tipo == TipoMovimiento.Ajuste),
        ClaseMovimiento.Merma => consulta.Where(m => m.Tipo == TipoMovimiento.Merma),
        ClaseMovimiento.Paquete => consulta.Where(m => m.Tipo == TipoMovimiento.AperturaPaquete),
        // La lectura de eleventa deja la existencia con estos dos: va aparte.
        ClaseMovimiento.InventarioInicial => consulta.Where(m => m.Tipo == TipoMovimiento.InventarioInicial && m.DocumentoTipo != OrigenDelMovimiento.LecturaEleventa),
        ClaseMovimiento.Conteo => consulta.Where(m => m.Tipo == TipoMovimiento.Conteo && m.DocumentoTipo != OrigenDelMovimiento.LecturaEleventa),
        ClaseMovimiento.Traspaso => consulta.Where(m => m.Tipo == TipoMovimiento.TraspasoSalida || m.Tipo == TipoMovimiento.TraspasoEntrada),
        _ => consulta.Where(m => m.Tipo == TipoMovimiento.ConsumoServicio),
    };

    /// <summary>
    /// Lo que hizo alguien, según el documento de cada movimiento (el movimiento no guarda quién): sus ventas, sus devoluciones,
    /// las compras que cerró (o recibió, si no se han cerrado), sus ajustes y mermas, los paquetes que abrió, lo que contó en
    /// el conteo del día y los conteos completos que cerró.
    /// </summary>
    private IQueryable<MovimientoInventario> DeQuien(IQueryable<MovimientoInventario> consulta, Guid quien)
    {
        var ventas = db.Ventas.Where(v => v.CajeroId == quien).Select(v => (Guid?)v.Id);
        var devoluciones = db.Devoluciones.Where(d => d.UsuarioId == quien).Select(d => (Guid?)d.Id);
        var compras = db.Compras.Where(c => (c.CosteoId ?? c.RecibioId) == quien).Select(c => (Guid?)c.Id);
        var ajustes = db.AjustesInventario.Where(a => a.UsuarioId == quien).Select(a => (Guid?)a.Id);
        var aperturas = db.AperturasPaquete.Where(a => a.UsuarioId == quien).Select(a => (Guid?)a.Id);
        var completos = db.ConteosDepartamento.Where(c => c.TerminadoPorId == quien).Select(c => (Guid?)c.Id);
        return consulta.Where(m =>
            (m.DocumentoTipo == nameof(Venta) && ventas.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(Devolucion) && devoluciones.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(Compra) && compras.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(AjusteInventario) && ajustes.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(AperturaPaquete) && aperturas.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(ConteoDepartamento) && completos.Contains(m.DocumentoId))
            || (m.DocumentoTipo == nameof(ConteoCiclico)
                && db.ConteosCiclicosRenglones.Any(r => r.ConteoId == m.DocumentoId && r.VarianteId == m.VarianteId && r.ContadoPorId == quien)));
    }

    /// <summary>Las variantes cuyo producto coincide con lo escrito (el nombre, la marca, la variante o un código).</summary>
    private async Task<List<Guid>> VariantesDelTextoAsync(string texto, CancellationToken ct)
    {
        var variantes = await db.Variantes.AsNoTracking()
            .Select(v => new { v.Id, Producto = v.Producto!.Nombre, Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion })
            .ToListAsync(ct);
        var codigos = (await db.CodigosBarras.AsNoTracking().Select(c => new { c.Codigo, c.Presentacion!.VarianteId }).ToListAsync(ct))
            .ToLookup(c => c.VarianteId, c => c.Codigo);
        var coincide = Coincide(texto);
        return variantes.Where(v => coincide(Nombre(v.Producto, v.Marca, v.Descripcion), codigos[v.Id])).Select(v => v.Id).ToList();
    }

    /// <param name="Cantidad">La existencia que quedó después del movimiento, en milésimas.</param>
    private sealed record Quedo(Guid Id, long Cantidad);

    /// <summary>
    /// La existencia que quedó después de cada uno: la suma del kardex de su variante en la sucursal hasta él (con lo de la
    /// misma hora, en el orden de la lista). Una sola consulta para la página, que usa el índice de cada variante por fecha.
    /// </summary>
    private async Task<Dictionary<Guid, Cantidad>> QuedoAsync(IReadOnlyList<MovimientoInventario> movimientos, CancellationToken ct)
    {
        if (movimientos.Count == 0)
            return [];
        var lugares = string.Join(", ", movimientos.Select((_, i) => $"{{{i}}}"));
        var sql = $"""
            SELECT m."Id" AS "Id",
                   (SELECT COALESCE(SUM(x."Cantidad"), 0) FROM "MovimientosInventario" AS x
                    WHERE x."SucursalId" = m."SucursalId" AND x."VarianteId" = m."VarianteId"
                      AND (x."FechaUtc" < m."FechaUtc" OR (x."FechaUtc" = m."FechaUtc" AND x."Id" <= m."Id"))) AS "Cantidad"
            FROM "MovimientosInventario" AS m
            WHERE m."Id" IN ({lugares})
            """;
        return (await db.Database.SqlQueryRaw<Quedo>(sql, movimientos.Select(m => (object)m.Id).ToArray()).ToListAsync(ct))
            .ToDictionary(q => q.Id, q => new Cantidad(q.Cantidad));
    }

    /// <summary>
    /// Los movimientos del kardex de la sucursal en unos días, del más reciente al más viejo, de
    /// <see cref="MovimientosPorPagina"/> en <see cref="MovimientosPorPagina"/>: con el tipo, el producto, el departamento y
    /// quién, si se piden. Cada uno con su producto, lo que entró o salió, la existencia que dejó, quién lo hizo y su documento;
    /// el costo solo para quien tiene «Ver costos, utilidad y margen».
    /// </summary>
    public async Task<MovimientosInventarioDto> MovimientosAsync(
        Guid sucursalId, Guid usuarioId, FiltroMovimientosDto filtro, int pagina = 0, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, ConsultasInventario.Movimientos, "Los movimientos del inventario", ct);
        if (filtro.Hasta < filtro.Desde)
            throw new ArgumentException("El primer día va antes que el último.");
        var conCostos = await new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        var desde = HoraMexico.AUtc(filtro.Desde.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(filtro.Hasta.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var consulta = db.MovimientosInventario.AsNoTracking().Where(m => m.SucursalId == sucursalId && m.FechaUtc >= desde && m.FechaUtc < hasta);
        if (filtro.Clase is { } clase)
            consulta = DeLaClase(consulta, clase);
        // El producto y el departamento, como la lista de sus variantes: con el índice de cada variante por fecha es lo más
        // rápido. Un departamento enorme (más que lo que se deja buscar) se revisa renglón por renglón.
        List<Guid>? variantes = null;
        if (!string.IsNullOrWhiteSpace(filtro.Texto))
        {
            variantes = await VariantesDelTextoAsync(filtro.Texto, ct);
            if (variantes.Count > MaximoProductosDelTexto)
                throw new ArgumentException(
                    $"«{filtro.Texto.Trim()}» coincide con {variantes.Count.ToString("N0", Mexico)} productos: escribe más del nombre o el código.");
        }
        if (filtro.DepartamentoId is { } departamento)
        {
            var delDepartamento = await db.Variantes.AsNoTracking().Where(v => v.Producto!.DepartamentoId == departamento).Select(v => v.Id).ToListAsync(ct);
            if (variantes is not null)
                variantes = variantes.Intersect(delDepartamento).ToList();
            else if (delDepartamento.Count <= MaximoProductosDelTexto)
                variantes = delDepartamento;
            else
                consulta = consulta.Where(m => db.Variantes.Any(v => v.Id == m.VarianteId && v.Producto!.DepartamentoId == departamento));
        }
        if (variantes is not null)
            consulta = consulta.Where(m => variantes.Contains(m.VarianteId));
        if (filtro.QuienId is { } quien)
            consulta = DeQuien(consulta, quien);

        var total = await consulta.CountAsync(ct);
        pagina = Math.Clamp(pagina, 0, UltimaPagina);
        var movimientos = total == 0
            ? []
            : await consulta.OrderByDescending(m => m.FechaUtc).ThenByDescending(m => m.Id)
                .Skip(pagina * MovimientosPorPagina).Take(MovimientosPorPagina).ToListAsync(ct);

        var ids = movimientos.Select(m => m.VarianteId).Distinct().ToList();
        var variantesDeLaPagina = ids.Count == 0 ? [] : await db.Variantes.AsNoTracking().Where(v => ids.Contains(v.Id))
            .Select(v => new
            {
                v.Id, v.ProductoId, Producto = v.Producto!.Nombre, Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion,
                v.Producto.DepartamentoId, v.UnidadBase,
            })
            .ToDictionaryAsync(v => v.Id, ct);
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        var codigos = await CodigosAsync(ids, ct);
        var quedo = await QuedoAsync(movimientos, ct);
        var documentos = await new DocumentosDelKardex(db).DeAsync(movimientos, ct);

        var renglones = movimientos.Select(m =>
        {
            var v = variantesDeLaPagina.GetValueOrDefault(m.VarianteId);
            var documento = documentos[m.Id];
            return new MovimientoInventarioDto(m.Id, HoraMexico.ALocal(m.FechaUtc), ClaseDe(m.Tipo, m.DocumentoTipo), v?.ProductoId ?? Guid.Empty,
                v is null ? "?" : Nombre(v.Producto, v.Marca, v.Descripcion), codigos.GetValueOrDefault(m.VarianteId),
                v is null ? "" : departamentos.GetValueOrDefault(v.DepartamentoId, ""), v?.UnidadBase ?? UnidadBase.Pieza, m.Cantidad.EnUnidades,
                quedo.GetValueOrDefault(m.Id).EnUnidades, documento.Texto, m.Motivo, documento.Quien, conCostos ? m.CostoCentavos : null);
        }).ToList();
        return new MovimientosInventarioDto(renglones, total, pagina, MovimientosPorPagina, conCostos);
    }

    // ── Bajos en inventario ─────────────────────────────────────────────────

    /// <summary>Una variante que se vende y lleva inventario, con su existencia en la sucursal y su mínimo y su máximo.</summary>
    private sealed record VarianteConMinimo(
        Guid Id, Guid ProductoId, string Nombre, Guid DepartamentoId, UnidadBase Unidad, Cantidad? Minimo, Cantidad? Maximo, Cantidad Existencia)
    {
        public bool EsBaja => PocaExistencia.Es(Existencia, Minimo);
    }

    /// <summary>Las variantes activas de los productos activos que llevan inventario: lo que se puede quedar sin existencia.</summary>
    private async Task<List<VarianteConMinimo>> VariantesConMinimoAsync(Guid sucursalId, CancellationToken ct)
    {
        var variantes = await (from v in db.Variantes.AsNoTracking()
                               join p in db.Productos.AsNoTracking() on v.ProductoId equals p.Id
                               where v.Activa && p.Activo && p.Tipo == TipoProducto.Mercancia
                               select new
                               {
                                   v.Id, v.ProductoId, Producto = p.Nombre, Marca = p.Marca == null ? null : p.Marca.Nombre, v.Descripcion, p.DepartamentoId,
                                   v.UnidadBase, v.InventarioMinimo, v.InventarioMaximo,
                               }).ToListAsync(ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId).ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad, ct);
        return variantes.Select(v => new VarianteConMinimo(v.Id, v.ProductoId, Nombre(v.Producto, v.Marca, v.Descripcion), v.DepartamentoId, v.UnidadBase,
                v.InventarioMinimo, v.InventarioMaximo, existencias.GetValueOrDefault(v.Id)))
            .ToList();
    }

    /// <summary>Cuántos productos están en su mínimo o debajo: la cuenta de la tarjeta «Bajos en inventario».</summary>
    public async Task<int> ContarBajosAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var minimos = await (from v in db.Variantes.AsNoTracking()
                             join p in db.Productos.AsNoTracking() on v.ProductoId equals p.Id
                             where v.Activa && p.Activo && p.Tipo == TipoProducto.Mercancia
                             select new { v.Id, v.InventarioMinimo }).ToListAsync(ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId).ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad, ct);
        return minimos.Count(v => PocaExistencia.Es(existencias.GetValueOrDefault(v.Id), v.InventarioMinimo));
    }

    /// <summary>
    /// Lo que está en su mínimo o debajo (el de su ficha o, sin él, <see cref="PocaExistencia.Hasta"/> o menos), como «Poca
    /// existencia» de Productos: por departamento y por nombre, de <see cref="BajosPorPagina"/> en <see cref="BajosPorPagina"/>.
    /// Cada uno con su mínimo, su máximo, lo que ya viene en órdenes abiertas y cuánto pedir: lo que falta para su máximo o, si
    /// no tiene, lo que pide el sugerido de compra (se calcula solo para los de la página). Para quien pide la mercancía o pone
    /// los mínimos.
    /// </summary>
    public async Task<BajosInventarioDto> BajosAsync(
        Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? texto, bool soloConMinimoPropio, int pagina = 0, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, ConsultasInventario.Bajos, "Bajos en inventario", ct);
        pagina = Math.Clamp(pagina, 0, UltimaPagina);
        var bajas = (await VariantesConMinimoAsync(sucursalId, ct)).Where(v => v.EsBaja);
        if (departamentoId is { } d)
            bajas = bajas.Where(v => v.DepartamentoId == d);
        if (!string.IsNullOrWhiteSpace(texto))
        {
            var lista = bajas.ToList();
            var codigosDeTodas = (await db.CodigosBarras.AsNoTracking().Select(c => new { c.Codigo, c.Presentacion!.VarianteId }).ToListAsync(ct))
                .ToLookup(c => c.VarianteId, c => c.Codigo);
            var coincide = Coincide(texto);
            bajas = lista.Where(v => coincide(v.Nombre, codigosDeTodas[v.Id]));
        }
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Nombre, ct);
        var todas = bajas
            .OrderBy(v => departamentos.GetValueOrDefault(v.DepartamentoId, ""), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.Id)
            .ToList();
        var conMinimo = todas.Count(v => v.Minimo is not null);
        var elegidas = soloConMinimoPropio ? todas.Where(v => v.Minimo is not null).ToList() : todas;

        var primeras = elegidas.Skip(pagina * BajosPorPagina).Take(BajosPorPagina).ToList();
        var ids = primeras.Select(v => v.Id).ToList();
        var codigos = await CodigosAsync(ids, ct);
        var sugerido = new RegistroSugerido(db);
        var enCamino = ids.Count == 0 ? [] : await sugerido.EnCaminoAsync(sucursalId, ids, ct);
        var calculos = await sugerido.DeVariantesAsync(sucursalId, primeras.Where(v => v.Maximo is null).Select(v => v.Id).ToList(), ct);

        var productos = primeras.Select(v =>
        {
            var viene = enCamino.GetValueOrDefault(v.Id);
            decimal? pedir = null, maximoSugerido = null;
            string? pedirEn = null;
            if (v.Maximo is { } propio)
            {
                // Hasta su máximo, contando lo que ya viene (lo negativo cuenta como cero, como en el sugerido).
                var falta = propio.EnUnidades - (Math.Max(0, v.Existencia.EnUnidades) + viene);
                pedir = falta > 0 ? falta : null;
            }
            else if (calculos.GetValueOrDefault(v.Id) is { } calculo)
            {
                maximoSugerido = calculo.Resultado.Maximo > 0 ? calculo.Resultado.Maximo : null;
                if (calculo.Resultado.Paquetes > 0)
                {
                    var factor = calculo.Presentacion.Factor.EnUnidades;
                    pedir = calculo.Resultado.Sugerido(factor);
                    pedirEn = factor == 1 ? null : $"{calculo.Resultado.Paquetes} {calculo.Presentacion.Nombre}";
                }
            }
            return new BajoInventarioDto(v.Id, v.ProductoId, v.Nombre, codigos.GetValueOrDefault(v.Id), v.DepartamentoId,
                departamentos.GetValueOrDefault(v.DepartamentoId, "Sin departamento"), v.Unidad, v.Existencia.EnUnidades,
                PocaExistencia.Minimo(v.Minimo).EnUnidades, v.Minimo is not null, v.Maximo?.EnUnidades, maximoSugerido, viene, pedir, pedirEn);
        }).ToList();
        return new BajosInventarioDto(productos, elegidas.Count, todas.Count, conMinimo, pagina, BajosPorPagina);
    }
}
