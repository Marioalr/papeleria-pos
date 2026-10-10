using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Órdenes de compra (docs/fase-2-inventario.md, entrega 2, parte 3): lo que se le pide a un proveedor, con el texto
/// listo para mandárselo. Son opcionales: al capturar la nota de una compra se dice de qué orden viene y la comparación
/// muestra lo pedido. Las hacen el dueño y el encargado (regla "Hacer órdenes de compra").
/// </summary>
public sealed class RegistroOrdenes(PosDbContext db)
{
    private const string Mensaje = "Las órdenes de compra las hacen el dueño y el encargado.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Las abiertas y las últimas 50 recibidas o canceladas.</summary>
    public async Task<List<OrdenResumenDto>> ListaAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var ordenes = await db.OrdenesCompra.AsNoTracking().Include(o => o.Renglones)
            .Where(o => o.SucursalId == sucursalId && o.Estado == EstadoOrden.Abierta).ToListAsync(ct);
        ordenes.AddRange(await db.OrdenesCompra.AsNoTracking().Include(o => o.Renglones)
            .Where(o => o.SucursalId == sucursalId && o.Estado != EstadoOrden.Abierta).OrderByDescending(o => o.Folio).Take(50).ToListAsync(ct));
        var proveedores = await db.Proveedores.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Nombre, ct);
        var compras = await FoliosDeComprasAsync(ordenes.Where(o => o.CompraId is not null).Select(o => o.CompraId!.Value), ct);
        return ordenes.OrderBy(o => o.Estado != EstadoOrden.Abierta).ThenByDescending(o => o.Folio)
            .Select(o => new OrdenResumenDto(o.Id, o.FolioTexto, o.ProveedorId, proveedores.GetValueOrDefault(o.ProveedorId, "?"), o.Estado,
                HoraMexico.ALocal(o.CreadaUtc), o.Renglones.Count, o.CompraId is { } c ? compras.GetValueOrDefault(c) : null))
            .ToList();
    }

    public async Task<OrdenDto> AbrirAsync(SolicitudAbrirOrden s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.PedirCompra, Mensaje, ct);
        if (await db.Proveedores.FindAsync([s.ProveedorId], ct) is not { Activo: true })
            throw new ArgumentException("Elige el proveedor.");
        var orden = new OrdenCompra
        {
            SucursalId = s.SucursalId, ProveedorId = s.ProveedorId, Folio = await Secuencia.SiguienteAsync(db, $"ordenes:{s.SucursalId}", ct),
            Notas = RegistroCompras.Limpio(s.Notas), UsuarioId = s.UsuarioId, CreadaUtc = Ahora,
        };
        db.OrdenesCompra.Add(orden);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(orden.Id, ct))!;
    }

    /// <summary>Pone lo que se pide de una presentación (cero la quita).</summary>
    public async Task<OrdenDto> PedirAsync(Guid ordenId, SolicitudPedido s, CancellationToken ct = default)
    {
        var orden = await OrdenAsync(ordenId, ct);
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, orden.SucursalId, AccionControlada.PedirCompra, Mensaje, ct);
        var presentacion = await new RegistroCompras(db).PresentacionAsync(s.VarianteId, s.PresentacionId, ct);
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!presentacion.Variante!.AdmiteCantidad(cantidad.Por(presentacion.Factor)))
            throw new ArgumentException($"«{RegistroCompras.Nombre(presentacion.Variante)}» se pide en piezas enteras.");
        var antes = orden.Renglones.ToList();
        orden.Pedir(s.VarianteId, s.PresentacionId, cantidad, presentacion.Factor);
        foreach (var nuevo in orden.Renglones.Except(antes))
            db.Add(nuevo);
        foreach (var quitado in antes.Except(orden.Renglones))
            db.Remove(quitado);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(ordenId, ct))!;
    }

    public async Task<OrdenDto> CancelarAsync(Guid ordenId, Guid usuarioId, CancellationToken ct = default)
    {
        var orden = await OrdenAsync(ordenId, ct);
        await new ServicioControl(db).ExigirAsync(usuarioId, orden.SucursalId, AccionControlada.PedirCompra, Mensaje, ct);
        orden.Cancelar();
        new ServicioControl(db).Registrar(orden.SucursalId, null, usuarioId, AccionControlada.PedirCompra, $"Canceló la orden {orden.FolioTexto}");
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(ordenId, ct))!;
    }

    /// <summary>La orden con el último costo de cada producto con ese proveedor, su existencia y el texto para mandarla.</summary>
    public async Task<OrdenDto?> DetalleAsync(Guid ordenId, CancellationToken ct = default)
    {
        var orden = await db.OrdenesCompra.AsNoTracking().Include(o => o.Renglones).SingleOrDefaultAsync(o => o.Id == ordenId, ct);
        if (orden is null)
            return null;
        var ids = orden.Renglones.Select(r => r.VarianteId).ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        var deProveedor = await db.ProveedoresVariantes.AsNoTracking().Where(p => p.ProveedorId == orden.ProveedorId && ids.Contains(p.VarianteId))
            .ToDictionaryAsync(p => p.VarianteId, ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == orden.SucursalId && ids.Contains(e.VarianteId))
            .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad.EnUnidades, ct);
        var proveedor = await db.Proveedores.AsNoTracking().Where(p => p.Id == orden.ProveedorId).Select(p => p.Nombre).FirstAsync(ct);
        var sucursal = await db.Sucursales.AsNoTracking().Where(x => x.Id == orden.SucursalId).Select(x => x.Nombre).FirstAsync(ct);
        var usuario = await db.Usuarios.AsNoTracking().Where(u => u.Id == orden.UsuarioId).Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "?";

        var renglones = orden.Renglones
            .Select(r =>
            {
                var v = variantes[r.VarianteId];
                return new OrdenRenglonDto(r.VarianteId, r.PresentacionId, RegistroCompras.Nombre(v),
                    v.Presentaciones.FirstOrDefault(p => p.Id == r.PresentacionId)?.Nombre ?? "?", r.Cantidad.Milesimas, r.CantidadBase.EnUnidades,
                    deProveedor.GetValueOrDefault(r.VarianteId)?.UltimoCostoCentavos, existencias.TryGetValue(r.VarianteId, out var e) ? e : null);
            })
            .OrderBy(r => r.Producto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var texto = new StringBuilder($"Pedido {orden.FolioTexto} · {sucursal}").AppendLine().AppendLine($"Para: {proveedor}");
        foreach (var r in renglones)
        {
            var clave = deProveedor.GetValueOrDefault(r.VarianteId)?.CodigoProveedor;
            texto.AppendLine($"• {(r.CantidadMilesimas / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {r.Presentacion} de {r.Producto}"
                + (clave is null ? "" : $" (clave {clave})"));
        }
        if (orden.Notas is { } notas)
            texto.AppendLine($"Notas: {notas}");

        var compra = orden.CompraId is { } c ? (await FoliosDeComprasAsync([c], ct)).GetValueOrDefault(c) : null;
        return new OrdenDto(orden.Id, orden.FolioTexto, orden.ProveedorId, proveedor, orden.Estado, HoraMexico.ALocal(orden.CreadaUtc), usuario, orden.Notas,
            renglones, compra, texto.ToString().TrimEnd());
    }

    private async Task<Dictionary<Guid, string>> FoliosDeComprasAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var lista = ids.ToList();
        return (await db.Compras.AsNoTracking().Where(c => lista.Contains(c.Id)).Select(c => new { c.Id, c.Folio }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => $"C-{c.Folio:D6}");
    }

    private async Task<OrdenCompra> OrdenAsync(Guid ordenId, CancellationToken ct) =>
        await db.OrdenesCompra.Include(o => o.Renglones).SingleOrDefaultAsync(o => o.Id == ordenId, ct)
        ?? throw new InvalidOperationException("La orden ya no existe.");
}
