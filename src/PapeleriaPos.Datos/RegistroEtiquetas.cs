using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las etiquetas de código de barras (docs/fase-2-inventario.md, entrega 4, parte 1): qué productos llevan (lo de una
/// compra, lo que no tiene código), su código (si no tiene, se le da uno interno) y la hoja que compra la sucursal.
/// También la hoja de códigos del mostrador (parte 2), para lo diminuto que no lleva etiqueta.
/// </summary>
public sealed class RegistroEtiquetas(PosDbContext db)
{
    /// <summary>Más de esto en una sola impresión es un error de dedo.</summary>
    public const int Maximo = 2000;

    public async Task<ConfiguracionEtiquetasDto> ConfiguracionAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var c = await db.ConfiguracionesSucursal.AsNoTracking().SingleOrDefaultAsync(x => x.SucursalId == sucursalId, ct) ?? new ConfiguracionSucursal();
        return new ConfiguracionEtiquetasDto(c.EtiquetasFormato, c.EtiquetasPropio, c.EtiquetasAjusteXDecimas, c.EtiquetasAjusteYDecimas);
    }

    public async Task<ConfiguracionEtiquetasDto> GuardarConfiguracionAsync(Guid sucursalId, SolicitudConfiguracionEtiquetas s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, sucursalId, AccionControlada.EditarCatalogo, "La hoja de etiquetas la configuran el dueño y el encargado.", ct);
        var nueva = s.Configuracion;
        if (nueva.FormatoId == FormatoEtiquetas.Propio)
        {
            if (nueva.Propio is null)
                throw new ArgumentException("Escribe las medidas de la hoja.");
            if (nueva.Propio.Problema() is { } problema)
                throw new ArgumentException(problema);
        }
        else if (FormatoEtiquetas.Comunes.All(f => f.Id != nueva.FormatoId))
            throw new ArgumentException("Esa hoja de etiquetas no existe.");
        if (Math.Abs(nueva.AjusteXDecimas) > 100 || Math.Abs(nueva.AjusteYDecimas) > 100)
            throw new ArgumentException("El ajuste es de un centímetro como máximo: si hace falta más, revisa las medidas de la hoja.");

        var configuracion = await db.ConfiguracionesSucursal.SingleOrDefaultAsync(x => x.SucursalId == sucursalId, ct);
        if (configuracion is null)
        {
            configuracion = new ConfiguracionSucursal { SucursalId = sucursalId };
            db.ConfiguracionesSucursal.Add(configuracion);
        }
        configuracion.EtiquetasFormato = nueva.FormatoId;
        configuracion.EtiquetasPropio = nueva.FormatoId == FormatoEtiquetas.Propio && nueva.Propio is { } propio ? propio with { Id = FormatoEtiquetas.Propio, Nombre = "Hoja propia" } : null;
        configuracion.EtiquetasAjusteXDecimas = nueva.AjusteXDecimas;
        configuracion.EtiquetasAjusteYDecimas = nueva.AjusteYDecimas;
        await db.SaveChangesAsync(ct);
        return await ConfiguracionAsync(sucursalId, ct);
    }

    /// <summary>Lo que llegó en una compra cerrada, con cuántas etiquetas (en piezas).</summary>
    public async Task<List<ProductoEtiquetaDto>> DeCompraAsync(Guid compraId, CancellationToken ct = default)
    {
        var entradas = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.DocumentoTipo == nameof(Compra) && m.DocumentoId == compraId && m.Tipo == TipoMovimiento.Compra)
            .Select(m => new { m.VarianteId, m.Cantidad }).ToListAsync(ct);
        if (entradas.Count == 0)
            throw new InvalidOperationException("Esa compra todavía no entra al inventario: se cierra al capturar la nota.");
        var cantidades = entradas.GroupBy(e => e.VarianteId).ToDictionary(g => g.Key, g => g.Aggregate(Cantidad.Cero, (s, e) => s + e.Cantidad));
        var productos = await ProductosAsync(cantidades.Keys, ct);
        return productos.Select(p => p with { Sugeridas = Sugeridas(cantidades[p.VarianteId]) }).ToList();
    }

    /// <summary>Los productos de inventario de un departamento que no tienen ningún código: se les da uno al imprimir.</summary>
    public async Task<List<ProductoEtiquetaDto>> SinCodigoAsync(Guid departamentoId, CancellationToken ct = default)
    {
        var ids = await db.Variantes.AsNoTracking()
            .Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia && v.Producto.DepartamentoId == departamentoId
                && !v.Presentaciones.SelectMany(p => p.Codigos).Any())
            .Select(v => v.Id).ToListAsync(ct);
        return await ProductosAsync(ids, ct);
    }

    /// <summary>
    /// Las etiquetas listas para imprimir. A lo que no tiene código se le da uno interno (EAN-13 que empieza con 2), lo
    /// que necesita el permiso de editar el catálogo; las cajas lo reciben con el catálogo.
    /// </summary>
    public async Task<EtiquetasDto> PrepararAsync(SolicitudEtiquetas s, CancellationToken ct = default)
    {
        var renglones = s.Renglones.Where(r => r.Cantidad > 0).ToList();
        if (renglones.Count == 0)
            throw new ArgumentException("Agrega al menos un producto a la lista.");
        if (renglones.Sum(r => r.Cantidad) > Maximo)
            throw new ArgumentException($"Son más de {Maximo} etiquetas: imprímelas en partes.");
        var ids = renglones.Select(r => r.VarianteId).Distinct().ToList();
        var variantes = await VariantesAsync(ids, tracking: true, ct);
        if (ids.FirstOrDefault(id => !variantes.ContainsKey(id)) is var falta && falta != Guid.Empty)
            throw new InvalidOperationException("Uno de los productos ya no existe.");

        var nuevos = await DarCodigosAsync(variantes.Values, s.UsuarioId, s.SucursalId, "al imprimir etiquetas", ct);

        var precios = s.ConPrecio ? await MenudeoAsync(ids, s.SucursalId, ct) : [];
        var etiquetas = renglones
            .SelectMany(r =>
            {
                var v = variantes[r.VarianteId];
                var etiqueta = new EtiquetaDto(v.Id, RegistroCompras.Nombre(v), Codigo(v)!, s.ConPrecio ? precios.GetValueOrDefault(v.Id) : null);
                return Enumerable.Repeat(etiqueta, r.Cantidad);
            })
            .ToList();
        return new EtiquetasDto(etiquetas, nuevos);
    }

    // ── La hoja de códigos del mostrador (entrega 4, parte 2) ──

    /// <summary>Lo que va en la hoja de códigos de la sucursal, en su orden.</summary>
    public async Task<List<RenglonHojaCodigosDto>> HojaCodigosAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var renglones = await db.HojaCodigos.AsNoTracking().Where(r => r.SucursalId == sucursalId).OrderBy(r => r.Orden).ToListAsync(ct);
        var variantes = await VariantesAsync(renglones.Select(r => r.VarianteId), tracking: false, ct);
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        return renglones.Where(r => variantes.ContainsKey(r.VarianteId))
            .Select(r => variantes[r.VarianteId])
            .Select(v => new RenglonHojaCodigosDto(v.Id, RegistroCompras.Nombre(v), departamentos.GetValueOrDefault(v.Producto!.DepartamentoId, ""), Codigo(v)))
            .ToList();
    }

    /// <summary>Guarda qué productos van en la hoja y en qué orden (la lista completa).</summary>
    public async Task<List<RenglonHojaCodigosDto>> GuardarHojaCodigosAsync(Guid sucursalId, SolicitudHojaCodigos s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, sucursalId, AccionControlada.EditarCatalogo, "La hoja de códigos la arman el dueño y el encargado.", ct);
        var ids = s.Variantes.Distinct().ToList();
        if (ids.Count > 300)
            throw new ArgumentException("Son demasiados para una hoja de mostrador: deja solo lo que no lleva etiqueta.");
        var existentes = await db.Variantes.Where(v => ids.Contains(v.Id)).Select(v => v.Id).ToListAsync(ct);
        if (existentes.Count != ids.Count)
            throw new InvalidOperationException("Uno de los productos ya no existe.");
        // Dos guardados a la vez (un doble clic, dos cajas) se forman: el segundo ve lo que dejó el primero.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var actuales = await db.HojaCodigos.Where(r => r.SucursalId == sucursalId).ToListAsync(ct);
        db.HojaCodigos.RemoveRange(actuales.Where(r => !ids.Contains(r.VarianteId)));
        for (var i = 0; i < ids.Count; i++)
        {
            if (actuales.FirstOrDefault(r => r.VarianteId == ids[i]) is { } renglon)
                renglon.Orden = i;
            else
                db.HojaCodigos.Add(new RenglonHojaCodigos { SucursalId = sucursalId, VarianteId = ids[i], Orden = i });
        }
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await HojaCodigosAsync(sucursalId, ct);
    }

    /// <summary>La hoja lista para imprimir: a lo que no tiene código se le da uno interno.</summary>
    public async Task<HojaCodigosDto> PrepararHojaCodigosAsync(Guid sucursalId, SolicitudPrepararHojaCodigos s, CancellationToken ct = default)
    {
        var renglones = await db.HojaCodigos.AsNoTracking().Where(r => r.SucursalId == sucursalId).OrderBy(r => r.Orden).ToListAsync(ct);
        if (renglones.Count == 0)
            throw new InvalidOperationException("La hoja está vacía: agrega los productos que no llevan etiqueta.");
        var variantes = await VariantesAsync(renglones.Select(r => r.VarianteId), tracking: true, ct);
        var nuevos = await DarCodigosAsync(variantes.Values, s.UsuarioId, sucursalId, "para la hoja de códigos", ct);
        var precios = s.ConPrecio ? await MenudeoAsync(variantes.Keys.ToList(), sucursalId, ct) : [];
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        var sucursal = await db.Sucursales.AsNoTracking().Where(x => x.Id == sucursalId).Select(x => x.Nombre).SingleAsync(ct);
        var productos = renglones.Where(r => variantes.ContainsKey(r.VarianteId))
            .Select(r => variantes[r.VarianteId])
            .Select(v => new ProductoHojaCodigosDto(departamentos.GetValueOrDefault(v.Producto!.DepartamentoId, ""), RegistroCompras.Nombre(v), Codigo(v)!,
                s.ConPrecio ? precios.GetValueOrDefault(v.Id) : null))
            .ToList();
        return new HojaCodigosDto(sucursal, productos, nuevos);
    }

    private async Task<Dictionary<Guid, Variante>> VariantesAsync(IEnumerable<Guid> idsVariantes, bool tracking, CancellationToken ct)
    {
        var ids = idsVariantes.ToList();
        var consulta = db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones).ThenInclude(p => p.Codigos)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery();
        return await (tracking ? consulta : consulta.AsNoTracking()).ToDictionaryAsync(v => v.Id, ct);
    }

    /// <summary>
    /// A lo que no tiene ningún código le da uno interno (EAN-13 que empieza con 2) en la pieza y lo guarda; pide el
    /// permiso de editar el catálogo. Las variantes tienen que venir con sus presentaciones y códigos, rastreadas.
    /// </summary>
    private async Task<int> DarCodigosAsync(IEnumerable<Variante> variantes, Guid usuarioId, Guid sucursalId, string para, CancellationToken ct)
    {
        var sinCodigo = variantes.Where(v => Codigo(v) is null).ToList();
        if (sinCodigo.Count == 0)
            return 0;
        await new ServicioControl(db).ExigirAsync(usuarioId, sucursalId, AccionControlada.EditarCatalogo,
            $"{sinCodigo.Count} {(sinCodigo.Count == 1 ? "producto no tiene" : "productos no tienen")} código: se los da el encargado al imprimir.", ct);
        var catalogo = new RegistroCatalogo(db);
        foreach (var v in sinCodigo)
        {
            var pieza = v.Presentaciones.FirstOrDefault(p => p.EsBase)
                ?? throw new InvalidOperationException($"«{RegistroCompras.Nombre(v)}» no tiene presentación base: revísalo en F3.");
            var codigo = new CodigoBarras { Codigo = await catalogo.SiguienteCodigoInternoAsync(ct), PresentacionId = pieza.Id, Origen = OrigenCodigo.Interno };
            pieza.Codigos.Add(codigo);
            db.CodigosBarras.Add(codigo);
        }
        new ServicioControl(db).Registrar(sucursalId, null, usuarioId, AccionControlada.EditarCatalogo,
            sinCodigo.Count == 1
                ? $"Código interno para «{RegistroCompras.Nombre(sinCodigo[0])}»: {Codigo(sinCodigo[0])}"
                : $"Código interno para {sinCodigo.Count} productos ({para})", null);
        await db.SaveChangesAsync(ct);
        return sinCodigo.Count;
    }

    /// <summary>El código de la etiqueta: el interno si tiene (para eso se le dio), si no el de fábrica, de la pieza.</summary>
    private static string? Codigo(Variante v)
    {
        var codigos = v.Presentaciones.Where(p => p.EsBase).SelectMany(p => p.Codigos).ToList();
        return (codigos.FirstOrDefault(c => c.Origen == OrigenCodigo.Interno) ?? codigos.FirstOrDefault())?.Codigo;
    }

    /// <summary>Una por pieza; lo que se vende por metro o kilo, una.</summary>
    private static int Sugeridas(Cantidad cantidad) =>
        cantidad.EsEntera && cantidad.EsPositiva ? (int)Math.Min(cantidad.Milesimas / Cantidad.Escala, Maximo) : 1;

    private async Task<List<ProductoEtiquetaDto>> ProductosAsync(IEnumerable<Guid> idsVariantes, CancellationToken ct)
    {
        var ids = idsVariantes.ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .ThenInclude(p => p.Codigos).Where(v => ids.Contains(v.Id)).AsSplitQuery().ToListAsync(ct);
        return variantes
            .Select(v =>
            {
                var codigo = Codigo(v);
                var deFabrica = v.Presentaciones.Where(p => p.EsBase).SelectMany(p => p.Codigos).Any(c => c.Origen == OrigenCodigo.Fabrica);
                return new ProductoEtiquetaDto(v.Id, RegistroCompras.Nombre(v), codigo, deFabrica, 1);
            })
            .OrderBy(p => p.Producto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>El precio de menudeo de la pieza: el de la sucursal si tiene uno propio, si no el general.</summary>
    private async Task<Dictionary<Guid, long>> MenudeoAsync(List<Guid> ids, Guid sucursalId, CancellationToken ct) =>
        (await db.Precios.AsNoTracking().Where(p => ids.Contains(p.VarianteId) && p.PresentacionId == null && (p.SucursalId == null || p.SucursalId == sucursalId))
            .ToListAsync(ct))
        .GroupBy(p => p.VarianteId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.SucursalId is not null).ThenBy(p => p.CantidadMinima.Milesimas).First().PrecioCentavos);
}
