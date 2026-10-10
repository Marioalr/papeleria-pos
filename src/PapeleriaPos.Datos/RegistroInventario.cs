using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

public sealed record DiferenciaExistencia(
    Guid SucursalId, Guid VarianteId, Cantidad CantidadKardex, Cantidad CantidadGuardada, long CostoKardex, long CostoGuardado);

/// <summary>Guarda los movimientos del kardex y mantiene la copia de las existencias en la misma transacción.</summary>
public sealed class RegistroInventario(PosDbContext db)
{
    private sealed record SumaKardex(Guid SucursalId, Guid VarianteId, long Cantidad, long CostoCentavos);

    /// <summary>La existencia actual; si la variante nunca ha tenido movimientos en la sucursal, una en cero.</summary>
    public async Task<Existencia> ExistenciaAsync(Guid sucursalId, Guid varianteId, CancellationToken ct = default) =>
        await db.Existencias.FindAsync([sucursalId, varianteId], ct)
        ?? new Existencia { SucursalId = sucursalId, VarianteId = varianteId };

    /// <summary>Agrega el movimiento y actualiza la existencia, sin guardar: sirve para registrar muchos de una vez.</summary>
    public async Task AgregarAsync(MovimientoInventario movimiento, CancellationToken ct = default)
    {
        var existencia = await db.Existencias.FindAsync([movimiento.SucursalId, movimiento.VarianteId], ct);
        if (existencia is null)
        {
            existencia = new Existencia { SucursalId = movimiento.SucursalId, VarianteId = movimiento.VarianteId };
            db.Existencias.Add(existencia);
        }
        existencia.Aplicar(movimiento);
        db.MovimientosInventario.Add(movimiento);
    }

    /// <summary>
    /// El movimiento de la diferencia de un conteo (cíclico o completo), sin agregarlo: lo que sobró entra a su costo
    /// promedio (o al último costo si no hay existencia) y lo que faltó sale a su costo promedio. Sin diferencia, nada.
    /// </summary>
    public async Task<MovimientoInventario?> DiferenciaDeConteoAsync(
        Guid sucursalId, Variante variante, Cantidad diferencia, DateTime ahoraUtc, string documentoTipo, Guid documentoId, CancellationToken ct = default)
    {
        if (diferencia.Milesimas == 0)
            return null;
        var existencia = await ExistenciaAsync(sucursalId, variante.Id, ct);
        var ultimo = await new RegistroCaja(db).UltimoCostoAsync(variante.Id, ct);
        return diferencia.EsPositiva
            ? Kardex.Entrada(sucursalId, variante, TipoMovimiento.Conteo, diferencia,
                existencia.CostoPromedio is { } promedio && promedio > 0
                    ? (long)Math.Round(promedio * diferencia.Milesimas / Cantidad.Escala, MidpointRounding.AwayFromZero)
                    : diferencia.Importe(ultimo),
                ahoraUtc, documentoTipo: documentoTipo, documentoId: documentoId)
            : Kardex.Salida(sucursalId, variante, TipoMovimiento.Conteo, diferencia.Abs(), existencia, ultimo, ahoraUtc,
                documentoTipo: documentoTipo, documentoId: documentoId);
    }

    /// <summary>Agrega la diferencia de un conteo, sin guardar. Devuelve lo que vale, con signo.</summary>
    public async Task<long> AgregarDiferenciaDeConteoAsync(
        Guid sucursalId, Variante variante, Cantidad diferencia, DateTime ahoraUtc, string documentoTipo, Guid documentoId, CancellationToken ct = default)
    {
        if (await DiferenciaDeConteoAsync(sucursalId, variante, diferencia, ahoraUtc, documentoTipo, documentoId, ct) is not { } movimiento)
            return 0;
        await AgregarAsync(movimiento, ct);
        return movimiento.CostoCentavos;
    }

    public async Task RegistrarAsync(MovimientoInventario movimiento, CancellationToken ct = default)
    {
        await AgregarAsync(movimiento, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recalcula todas las existencias desde el kardex.</summary>
    public async Task ReconstruirExistenciasAsync(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Existencias", ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Existencias (SucursalId, VarianteId, Cantidad, CostoCentavos)
            SELECT SucursalId, VarianteId, SUM(Cantidad), SUM(CostoCentavos)
            FROM MovimientosInventario
            GROUP BY SucursalId, VarianteId
            """, ct);
        await tx.CommitAsync(ct);

        foreach (var entry in db.ChangeTracker.Entries<Existencia>().ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>Existencias guardadas que no coinciden con la suma del kardex. Debe salir vacío siempre.</summary>
    public async Task<List<DiferenciaExistencia>> VerificarExistenciasAsync(CancellationToken ct = default)
    {
        var kardex = await db.Database.SqlQueryRaw<SumaKardex>(
            """
            SELECT SucursalId, VarianteId, SUM(Cantidad) AS Cantidad, SUM(CostoCentavos) AS CostoCentavos
            FROM MovimientosInventario
            GROUP BY SucursalId, VarianteId
            """).ToDictionaryAsync(s => (s.SucursalId, s.VarianteId), ct);
        var guardadas = await db.Existencias.AsNoTracking().ToDictionaryAsync(e => (e.SucursalId, e.VarianteId), ct);

        return kardex.Keys.Union(guardadas.Keys)
            .Select(clave =>
            {
                kardex.TryGetValue(clave, out var k);
                guardadas.TryGetValue(clave, out var g);
                return new DiferenciaExistencia(
                    clave.SucursalId, clave.VarianteId,
                    new Cantidad(k?.Cantidad ?? 0), g?.Cantidad ?? Cantidad.Cero,
                    k?.CostoCentavos ?? 0, g?.CostoCentavos ?? 0);
            })
            .Where(d => d.CantidadKardex != d.CantidadGuardada || d.CostoKardex != d.CostoGuardado)
            .ToList();
    }
}
