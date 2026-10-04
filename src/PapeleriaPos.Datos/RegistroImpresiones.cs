using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Medicion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los trabajos de impresión de la sucursal (docs/fase-1-impresion.md): el agente de cada caja los mide y aquí se
/// les pone costo y precio con el modelo de Control Print y la configuración de la sucursal.
/// </summary>
public sealed class RegistroImpresiones(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Los costos de la sucursal; sin configurar, los valores por omisión de Control Print (WF-6590).</summary>
    public async Task<CostSettings> CostosAsync(Guid sucursalId, CancellationToken ct = default) =>
        await db.ConfiguracionesImpresion.FindAsync([sucursalId], ct) is { } c
            ? JsonSerializer.Deserialize<CostSettings>(c.CostosJson, CostSettings.Json) ?? new CostSettings()
            : new CostSettings();

    /// <summary>Guarda la configuración (por ejemplo, la importada de Control Print).</summary>
    public async Task GuardarCostosAsync(Guid sucursalId, CostSettings costos, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(costos, CostSettings.Json);
        if (await db.ConfiguracionesImpresion.FindAsync([sucursalId], ct) is { } existente)
        {
            existente.CostosJson = json;
            existente.ModificadoUtc = Ahora;
        }
        else
        {
            db.ConfiguracionesImpresion.Add(new ConfiguracionImpresion { SucursalId = sucursalId, CostosJson = json, ModificadoUtc = Ahora });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Un trabajo que llegó del agente. Si ya estaba (el agente lo volvió a mandar), no se repite.</summary>
    public async Task<TrabajoImpresion> RegistrarAsync(TrabajoMedidoDto t, CancellationToken ct = default)
    {
        if (await db.TrabajosImpresion.FindAsync([t.Id], ct) is { } existente)
            return existente;
        var caja = await db.Cajas.FindAsync([t.CajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        var costo = CostCalculator.Calculate(Analisis(t), await CostosAsync(caja.SucursalId, ct));

        var trabajo = new TrabajoImpresion
        {
            Id = t.Id, SucursalId = caja.SucursalId, CajaId = caja.Id, AtendioId = t.AtendioId, Pc = t.Pc, Cola = t.Cola, SpoolId = t.SpoolId,
            Documento = string.IsNullOrWhiteSpace(t.Documento) ? "(sin nombre)" : t.Documento.Trim(), UsuarioWindows = t.UsuarioWindows,
            ImpresoUtc = t.ImpresoUtc, Estado = t.Estado,
            Paginas = costo.LogicalPages, Copias = costo.Copies, Caras = costo.Sides, CarasColor = costo.ColorSides, Hojas = costo.Sheets,
            Color = t.Ajustes.Color, Duplex = t.Ajustes.Duplex, Tamano = costo.PaperSize.ToString(), TipoPapel = costo.MediaName,
            Calidad = t.Ajustes.Calidad, Medido = t.Medido, Nota = t.Nota,
            Miniatura = t.MiniaturaBase64 is { Length: > 0 } m ? Convert.FromBase64String(m) : null,
            CostoTintaCentavos = Centavos(costo.InkCost), CostoPapelCentavos = Centavos(costo.PaperCost),
            CostoOtrosCentavos = Centavos(costo.MaintenanceCost + costo.PowerCost + costo.ExtrasCost + costo.DepreciationCost),
            CostoCentavos = Centavos(costo.Total), MargenPorcentaje = costo.MarginPercent, PrecioCentavos = Centavos(costo.Price),
            DetalleJson = JsonSerializer.Serialize(new { t.Ajustes, t.Paginas }, CostSettings.Json),
            Cobro = EstadoCobroImpresion.PorCobrar,
        };
        db.TrabajosImpresion.Add(trabajo);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    /// <summary>Lo que midió el agente, en los términos del cálculo de Control Print.</summary>
    public static JobAnalysis Analisis(TrabajoMedidoDto t)
    {
        var a = t.Ajustes;
        var ajustes = new JobSettings
        {
            Quality = a.Calidad, MediaType = a.TipoPapel, MediaSizeName = a.Tamano, MediaWidthMm = a.AnchoMm, MediaHeightMm = a.AltoMm,
            Color = a.Color, Duplex = a.Duplex, Copies = a.Copias, PagesPerSheet = a.PaginasPorHoja, FromPrintTicket = a.DelPrintTicket,
        };
        var paginas = t.Paginas.Select(p => new PageCoverage(p.C, p.M, p.Y, p.K, p.Gris, p.AreaPulg2, p.ConColor)).ToList();
        return new JobAnalysis(ajustes, paginas, t.Medido ? AnalysisLevel.Measured : AnalysisLevel.Estimated, null, t.Nota, TimeSpan.Zero);
    }

    public async Task<List<TrabajoImpresion>> PorCobrarAsync(Guid sucursalId, CancellationToken ct = default) =>
        await db.TrabajosImpresion.Where(t => t.SucursalId == sucursalId && t.Cobro == EstadoCobroImpresion.PorCobrar)
            .OrderByDescending(t => t.ImpresoUtc).ToListAsync(ct);

    /// <summary>Los trabajos que se van a cobrar en un ticket: todos tienen que seguir por cobrar.</summary>
    public async Task<List<TrabajoImpresion>> ParaCobrarAsync(Guid sucursalId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var trabajos = await db.TrabajosImpresion.Where(t => ids.Contains(t.Id) && t.SucursalId == sucursalId).ToListAsync(ct);
        if (trabajos.Count != ids.Distinct().Count())
            throw new InvalidOperationException("Una de las impresiones ya no existe.");
        if (trabajos.FirstOrDefault(t => t.Cobro != EstadoCobroImpresion.PorCobrar) is { } cobrada)
            throw new InvalidOperationException($"«{cobrada.Documento}» ya no está por cobrar: quítala del ticket.");
        return trabajos;
    }

    public async Task<TrabajoImpresion> NoVendidaAsync(Guid trabajoId, SolicitudNoVendida s, CancellationToken ct = default)
    {
        var trabajo = await db.TrabajosImpresion.FindAsync([trabajoId], ct) ?? throw new InvalidOperationException("La impresión no existe.");
        trabajo.MarcarNoVendida(s.Motivo, s.Nota, s.UsuarioId, Ahora);
        await db.SaveChangesAsync(ct);
        return trabajo;
    }

    private static long Centavos(decimal pesos) => (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
}
