using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// El sugerido de compra (docs/fase-2-inventario.md, entrega 4, parte 3): junta lo vendido (el POS y el historial de
/// eleventa), la existencia, lo que ya viene en órdenes abiertas, la presentación en que se compra y el proveedor de
/// cada producto; <see cref="SugeridoCompra"/> calcula. La lista sale por proveedor y cada grupo se vuelve una orden.
/// </summary>
public sealed class RegistroSugerido(PosDbContext db)
{
    private const string Mensaje = "El sugerido de compra es del dueño y el encargado.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<ParametrosSugeridoDto> ParametrosAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var c = await db.ConfiguracionesSucursal.AsNoTracking().SingleOrDefaultAsync(x => x.SucursalId == sucursalId, ct) ?? new ConfiguracionSucursal();
        return new ParametrosSugeridoDto(c.SugeridoCoberturaDias, c.SugeridoColchonDias);
    }

    public async Task<ParametrosSugeridoDto> GuardarParametrosAsync(Guid sucursalId, SolicitudParametrosSugerido s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, sucursalId, AccionControlada.PedirCompra, Mensaje, ct);
        if (s.Parametros.CoberturaDias is < 1 or > 120 || s.Parametros.ColchonDias is < 0 or > 60)
            throw new ArgumentException("Cada cuánto compras va de 1 a 120 días y el colchón de 0 a 60.");
        var configuracion = await db.ConfiguracionesSucursal.SingleOrDefaultAsync(x => x.SucursalId == sucursalId, ct);
        if (configuracion is null)
        {
            configuracion = new ConfiguracionSucursal { SucursalId = sucursalId };
            db.ConfiguracionesSucursal.Add(configuracion);
        }
        (configuracion.SugeridoCoberturaDias, configuracion.SugeridoColchonDias) = (s.Parametros.CoberturaDias, s.Parametros.ColchonDias);
        await db.SaveChangesAsync(ct);
        return await ParametrosAsync(sucursalId, ct);
    }

    /// <summary>Lo que hay que pedir hoy, por proveedor; lo más urgente primero.</summary>
    public async Task<SugeridoDto> CalcularAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(usuarioId, sucursalId, AccionControlada.PedirCompra, Mensaje, ct);
        var parametros = await ParametrosAsync(sucursalId, ct);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));
        var desdeLocal = hoy.AddYears(-1).AddDays(-SugeridoCompra.DiasRecientes - 1);
        var desde = HoraMexico.AUtc(desdeLocal.ToDateTime(TimeOnly.MinValue));

        var ventas = await VentasAsync(sucursalId, desde, ct);
        var vendidas = ventas.Keys.ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia && vendidas.Contains(v.Id))
            .AsSplitQuery().ToListAsync(ct);
        var ids = variantes.Select(v => v.Id).ToList();
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId && ids.Contains(e.VarianteId))
            .ToDictionaryAsync(e => e.VarianteId, ct);
        var enCamino = (await (from r in db.OrdenesRenglones
                               join o in db.OrdenesCompra on r.OrdenId equals o.Id
                               where o.SucursalId == sucursalId && o.Estado == EstadoOrden.Abierta && ids.Contains(r.VarianteId)
                               select new { r.VarianteId, r.CantidadBase }).ToListAsync(ct))
            .GroupBy(x => x.VarianteId).ToDictionary(g => g.Key, g => g.Sum(x => x.CantidadBase.EnUnidades));
        var presentacionDeCompra = await PresentacionesDeCompraAsync(ids, ct);
        var proveedores = await ProveedoresAsync(ids, ct);
        var nombresProveedor = await db.Proveedores.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Nombre, ct);
        var reglas = new SugeridoCompra.Parametros(parametros.CoberturaDias, parametros.ColchonDias);

        var renglones = new List<(Guid? Proveedor, RenglonSugeridoDto Renglon)>();
        foreach (var v in variantes)
        {
            var presentacion = presentacionDeCompra.GetValueOrDefault(v.Id) is { } pid && v.Presentaciones.FirstOrDefault(p => p.Id == pid) is { } elegida
                ? elegida
                : v.Presentaciones.FirstOrDefault(p => p.EsBase) ?? v.Presentaciones.First();
            var existencia = existencias.GetValueOrDefault(v.Id);
            var resultado = SugeridoCompra.Calcular(
                new SugeridoCompra.Producto(v.Id, ventas[v.Id], existencia?.Cantidad.EnUnidades ?? 0, enCamino.GetValueOrDefault(v.Id),
                    presentacion.Factor.EnUnidades, v.UnidadBase.AdmiteFracciones()),
                hoy, reglas);
            if (resultado.Paquetes == 0)
                continue;
            var proveedor = proveedores.GetValueOrDefault(v.Id);
            var costoUnidad = proveedor?.UltimoCostoCentavos ?? (existencia?.CostoPromedio is { } promedio ? (long)Math.Round(promedio) : null);
            renglones.Add((proveedor?.ProveedorId, new RenglonSugeridoDto(v.Id, RegistroCompras.Nombre(v), proveedor?.CodigoProveedor, v.UnidadBase,
                existencia?.Cantidad.EnUnidades ?? 0, enCamino.GetValueOrDefault(v.Id), resultado.VentaDiaria, resultado.Temporada, resultado.Minimo,
                resultado.Maximo, presentacion.Id, presentacion.Nombre, presentacion.Factor.EnUnidades, resultado.Paquetes,
                costoUnidad is { } c ? (long)Math.Round(c * presentacion.Factor.EnUnidades) : null, resultado.Urgente)));
        }

        var grupos = renglones.GroupBy(r => r.Proveedor)
            .Select(g => new ProveedorSugeridoDto(g.Key, g.Key is { } id ? nombresProveedor.GetValueOrDefault(id, "?") : "Sin proveedor",
                g.Select(x => x.Renglon).OrderByDescending(r => r.Urgente).ThenBy(r => r.Producto, StringComparer.CurrentCultureIgnoreCase).ToList(),
                g.Sum(x => (x.Renglon.CostoPaqueteCentavos ?? 0) * x.Renglon.Paquetes)))
            .OrderBy(g => g.ProveedorId is null).ThenByDescending(g => g.Renglones.Count(r => r.Urgente)).ThenBy(g => g.Proveedor, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SugeridoDto(hoy, parametros, grupos);
    }

    /// <summary>Lo vendido por variante y día local, en unidad base: las ventas pagadas del POS y el historial de eleventa.</summary>
    private async Task<Dictionary<Guid, Dictionary<DateOnly, decimal>>> VentasAsync(Guid sucursalId, DateTime desde, CancellationToken ct)
    {
        var delPos = await (from l in db.VentasLineas
                             join v in db.Ventas on l.VentaId equals v.Id
                             where v.SucursalId == sucursalId && v.Estado == EstadoVenta.Pagada && v.PagadaUtc >= desde && l.VarianteId != null
                             select new { Variante = l.VarianteId!.Value, Fecha = v.PagadaUtc!.Value, l.CantidadBase }).ToListAsync(ct);
        var historicas = await (from l in db.VentasHistoricasLineas
                                join v in db.VentasHistoricas on l.VentaHistoricaId equals v.Id
                                where v.SucursalId == sucursalId && !v.Cancelada && v.FechaUtc >= desde
                                select new { Variante = l.VarianteId, Fecha = v.FechaUtc, CantidadBase = l.Cantidad }).ToListAsync(ct);
        var ventas = new Dictionary<Guid, Dictionary<DateOnly, decimal>>();
        foreach (var l in delPos.Concat(historicas))
        {
            if (!ventas.TryGetValue(l.Variante, out var porDia))
                ventas[l.Variante] = porDia = [];
            var dia = DateOnly.FromDateTime(HoraMexico.ALocal(l.Fecha));
            porDia[dia] = porDia.GetValueOrDefault(dia) + l.CantidadBase.EnUnidades;
        }
        return ventas;
    }

    /// <summary>En qué se compra cada producto: la presentación de su última compra cerrada u orden (la caja de 12).</summary>
    private async Task<Dictionary<Guid, Guid>> PresentacionesDeCompraAsync(List<Guid> ids, CancellationToken ct)
    {
        var compradas = await (from r in db.ComprasRenglones
                               join c in db.Compras on r.CompraId equals c.Id
                               where c.Estado == EstadoCompra.Cerrada && r.VarianteId != null && r.PresentacionId != null && ids.Contains(r.VarianteId!.Value)
                               select new { Variante = r.VarianteId!.Value, Presentacion = r.PresentacionId!.Value, Fecha = c.CerradaUtc!.Value }).ToListAsync(ct);
        var pedidas = await (from r in db.OrdenesRenglones
                             join o in db.OrdenesCompra on r.OrdenId equals o.Id
                             where o.Estado != EstadoOrden.Cancelada && ids.Contains(r.VarianteId)
                             select new { Variante = r.VarianteId, Presentacion = r.PresentacionId, Fecha = o.CreadaUtc }).ToListAsync(ct);
        return compradas.Concat(pedidas).GroupBy(x => x.Variante).ToDictionary(g => g.Key, g => g.MaxBy(x => x.Fecha)!.Presentacion);
    }

    /// <summary>El proveedor de cada producto: al que se le compró la última vez.</summary>
    private async Task<Dictionary<Guid, ProveedorVariante>> ProveedoresAsync(List<Guid> ids, CancellationToken ct)
    {
        var activos = await db.Proveedores.AsNoTracking().Where(p => p.Activo).Select(p => p.Id).ToListAsync(ct);
        return (await db.ProveedoresVariantes.AsNoTracking().Where(pv => ids.Contains(pv.VarianteId) && activos.Contains(pv.ProveedorId)).ToListAsync(ct))
            .GroupBy(pv => pv.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(pv => pv.UltimaCompraUtc ?? DateTime.MinValue).ThenByDescending(pv => pv.ModificadoUtc).First());
    }

    /// <summary>Una orden de compra abierta con lo que se eligió del sugerido de un proveedor.</summary>
    public async Task<OrdenDto> OrdenAsync(Guid sucursalId, SolicitudOrdenSugerida s, CancellationToken ct = default)
    {
        var renglones = s.Renglones.Where(r => r.Paquetes > 0).ToList();
        if (renglones.Count == 0)
            throw new ArgumentException("Elige al menos un producto para la orden.");
        // Toda la orden o nada: si un renglón no se puede pedir, no queda una orden a medias.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var registro = new RegistroOrdenes(db);
        var orden = await registro.AbrirAsync(new SolicitudAbrirOrden(s.UsuarioId, sucursalId, s.ProveedorId, "Del sugerido de compra"), ct);
        foreach (var r in renglones)
            await registro.PedirAsync(orden.Id, new SolicitudPedido(s.UsuarioId, r.VarianteId, r.PresentacionId, r.Paquetes * Cantidad.Escala), ct);
        new ServicioControl(db).Registrar(sucursalId, null, s.UsuarioId, AccionControlada.PedirCompra,
            $"Armó la orden {orden.Folio} desde el sugerido ({renglones.Count} {(renglones.Count == 1 ? "producto" : "productos")})");
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return (await registro.DetalleAsync(orden.Id, ct))!;
    }
}
