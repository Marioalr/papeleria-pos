using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// De dónde salió cada movimiento del kardex y quién lo hizo, para leerlo en Inventario › Movimientos: «A-000123» y el
/// cajero de la venta, «C-000041 · Proveedor» y quien cerró la compra, quien ajustó, quien contó (también al terminar una
/// temporada)… El movimiento no guarda quién: lo dice su documento. Una consulta por tipo de documento, solo de los movimientos que se van a enseñar.
/// </summary>
internal sealed class DocumentosDelKardex(PosDbContext db)
{
    /// <param name="Texto">El folio o lo que lo distingue; vacío si el tipo ya lo dice todo (un ajuste, la lectura de eleventa).</param>
    /// <param name="QuienId">Quien lo hizo, si es alguien del POS.</param>
    /// <param name="Quien">Quien lo hizo por su nombre, o «eleventa» en su lectura; vacío si no se sabe (la importación, la depuración).</param>
    internal sealed record Documento(string? Texto, Guid? QuienId, string? Quien);

    public async Task<Dictionary<Guid, Documento>> DeAsync(IReadOnlyCollection<MovimientoInventario> movimientos, CancellationToken ct = default)
    {
        List<Guid> De(string tipo) =>
            movimientos.Where(m => m.DocumentoTipo == tipo && m.DocumentoId is not null).Select(m => m.DocumentoId!.Value).Distinct().ToList();

        var ventasIds = De(nameof(Venta));
        var ventas = ventasIds.Count == 0 ? [] : await db.Ventas.AsNoTracking().Where(v => ventasIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Serie, v.Folio, v.CajeroId }).ToDictionaryAsync(v => v.Id, ct);
        var comprasIds = De(nameof(Compra));
        var compras = comprasIds.Count == 0 ? [] : await (from c in db.Compras.AsNoTracking()
                                                          join p in db.Proveedores.AsNoTracking() on c.ProveedorId equals p.Id
                                                          where comprasIds.Contains(c.Id)
                                                          select new { c.Id, c.Folio, Proveedor = p.Nombre, Quien = c.CosteoId ?? c.RecibioId })
            .ToDictionaryAsync(c => c.Id, ct);
        var devolucionesIds = De(nameof(Devolucion));
        var devoluciones = devolucionesIds.Count == 0 ? [] : await (from d in db.Devoluciones.AsNoTracking()
                                                                    join v in db.Ventas.AsNoTracking() on d.VentaId equals v.Id
                                                                    where devolucionesIds.Contains(d.Id)
                                                                    select new { d.Id, v.Serie, v.Folio, d.UsuarioId, d.EsCancelacion })
            .ToDictionaryAsync(d => d.Id, ct);
        var ajustesIds = De(nameof(AjusteInventario));
        var ajustes = ajustesIds.Count == 0 ? [] : await db.AjustesInventario.AsNoTracking().Where(a => ajustesIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.UsuarioId, ct);
        var aperturasIds = De(nameof(AperturaPaquete));
        var aperturas = aperturasIds.Count == 0 ? [] : await db.AperturasPaquete.AsNoTracking().Where(a => aperturasIds.Contains(a.Id))
            .Select(a => new { a.Id, a.OrigenId, a.DestinoId, a.UsuarioId }).ToDictionaryAsync(a => a.Id, ct);
        var deLosPaquetes = aperturas.Values.SelectMany(a => new[] { a.OrigenId, a.DestinoId }).Distinct().ToList();
        var nombres = deLosPaquetes.Count == 0 ? [] : (await db.Variantes.AsNoTracking().Where(v => deLosPaquetes.Contains(v.Id))
                .Select(v => new { v.Id, Producto = v.Producto!.Nombre, Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion })
                .ToListAsync(ct))
            .ToDictionary(v => v.Id, v => RegistroReportesInventario.Nombre(v.Producto, v.Marca, v.Descripcion));
        var conteosIds = De(nameof(ConteoCiclico));
        var contados = conteosIds.Count == 0 ? [] : (await db.ConteosCiclicosRenglones.AsNoTracking().Where(r => conteosIds.Contains(r.ConteoId))
                .Select(r => new { r.ConteoId, r.VarianteId, r.ContadoPorId }).ToListAsync(ct))
            .GroupBy(r => (r.ConteoId, r.VarianteId)).ToDictionary(g => g.Key, g => g.Select(r => r.ContadoPorId).FirstOrDefault(q => q is not null));
        // Lo contado al terminar una temporada (entrega 13, parte 3): quién contó ese producto.
        var temporadasIds = De(nameof(TemporadaDelAnio));
        var deTemporada = temporadasIds.Count == 0 ? [] : (await db.ContadosDeTemporada.AsNoTracking().Where(c => temporadasIds.Contains(c.TemporadaId))
                .Select(c => new { c.TemporadaId, c.VarianteId, c.UsuarioId }).ToListAsync(ct))
            .GroupBy(c => (c.TemporadaId, c.VarianteId)).ToDictionary(g => g.Key, g => g.First().UsuarioId);
        var completosIds = De(nameof(ConteoDepartamento));
        var completos = completosIds.Count == 0 ? [] : await db.ConteosDepartamento.AsNoTracking().Where(c => completosIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.TerminadoPorId, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        Documento Con(string? texto, Guid? quien) => new(texto, quien, quien is { } q ? usuarios.GetValueOrDefault(q) : null);
        static string Folio(string serie, int? folio) => folio is { } f ? $"{serie}-{f:000000}" : serie;

        Documento DeUno(MovimientoInventario m)
        {
            var id = m.DocumentoId ?? Guid.Empty;
            return m.DocumentoTipo switch
            {
                nameof(Venta) when ventas.TryGetValue(id, out var v) => Con(Folio(v.Serie, v.Folio), v.CajeroId),
                nameof(Compra) when compras.TryGetValue(id, out var c) => Con($"C-{c.Folio:D6} · {c.Proveedor}", c.Quien),
                // Lo que volvió dañado sale como merma de la misma devolución: su motivo ya dice de qué ticket.
                nameof(Devolucion) when devoluciones.TryGetValue(id, out var d) =>
                    Con(m.Tipo == TipoMovimiento.Merma ? null : $"de {Folio(d.Serie, d.Folio)}{(d.EsCancelacion ? " (cancelación)" : "")}", d.UsuarioId),
                nameof(AjusteInventario) when ajustes.TryGetValue(id, out var quien) => Con(null, quien),
                nameof(AperturaPaquete) when aperturas.TryGetValue(id, out var a) =>
                    Con(m.Cantidad.EsPositiva ? $"de «{nombres.GetValueOrDefault(a.OrigenId, "?")}»" : $"a «{nombres.GetValueOrDefault(a.DestinoId, "?")}»",
                        a.UsuarioId),
                nameof(ConteoCiclico) => Con("del día", contados.GetValueOrDefault((id, m.VarianteId))),
                nameof(ConteoDepartamento) => Con("completo", completos.GetValueOrDefault(id)),
                nameof(TemporadaDelAnio) => Con("de la temporada", deTemporada.TryGetValue((id, m.VarianteId), out var conto) ? conto : null),
                nameof(DecisionDepuracion) => Con("de la depuración", null),
                OrigenDelMovimiento.LecturaEleventa => new Documento(null, null, "eleventa"),
                OrigenDelMovimiento.ImportacionEleventa => Con("de la importación de eleventa", null),
                OrigenDelMovimiento.Ficticios => Con("de la importación", null),
                _ => Con(null, null),
            };
        }

        return movimientos.ToDictionary(m => m.Id, DeUno);
    }
}
