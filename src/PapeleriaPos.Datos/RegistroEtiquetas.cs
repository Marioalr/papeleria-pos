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
        var variantes = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones).ThenInclude(p => p.Codigos)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        if (ids.FirstOrDefault(id => !variantes.ContainsKey(id)) is var falta && falta != Guid.Empty)
            throw new InvalidOperationException("Uno de los productos ya no existe.");

        var sinCodigo = variantes.Values.Where(v => Codigo(v) is null).ToList();
        if (sinCodigo.Count > 0)
        {
            await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo,
                $"{sinCodigo.Count} {(sinCodigo.Count == 1 ? "producto no tiene" : "productos no tienen")} código: se los da el encargado al imprimir.", ct);
            var catalogo = new RegistroCatalogo(db);
            foreach (var v in sinCodigo)
            {
                var basePresentacion = v.Presentaciones.FirstOrDefault(p => p.EsBase)
                    ?? throw new InvalidOperationException($"«{RegistroCompras.Nombre(v)}» no tiene presentación base: revísalo en F3.");
                var codigo = new CodigoBarras { Codigo = await catalogo.SiguienteCodigoInternoAsync(ct), PresentacionId = basePresentacion.Id, Origen = OrigenCodigo.Interno };
                basePresentacion.Codigos.Add(codigo);
                db.CodigosBarras.Add(codigo);
            }
            new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
                sinCodigo.Count == 1
                    ? $"Código interno para «{RegistroCompras.Nombre(sinCodigo[0])}»: {Codigo(sinCodigo[0])}"
                    : $"Código interno para {sinCodigo.Count} productos (al imprimir etiquetas)", null);
            await db.SaveChangesAsync(ct);
        }

        var precios = s.ConPrecio ? await MenudeoAsync(ids, s.SucursalId, ct) : [];
        var etiquetas = renglones
            .SelectMany(r =>
            {
                var v = variantes[r.VarianteId];
                var etiqueta = new EtiquetaDto(v.Id, RegistroCompras.Nombre(v), Codigo(v)!, s.ConPrecio ? precios.GetValueOrDefault(v.Id) : null);
                return Enumerable.Repeat(etiqueta, r.Cantidad);
            })
            .ToList();
        return new EtiquetasDto(etiquetas, sinCodigo.Count);
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
