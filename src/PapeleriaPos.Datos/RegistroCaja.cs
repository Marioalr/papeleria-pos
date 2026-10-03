using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo que pasa en una caja y se guarda: turnos, cobros, entradas y salidas de efectivo y cortes. Cada operación es una
/// sola transacción: o queda todo o no queda nada.
/// </summary>
public sealed class RegistroCaja(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<Turno?> TurnoAbiertoAsync(Guid cajaId, CancellationToken ct = default) =>
        await db.Turnos.SingleOrDefaultAsync(t => t.CajaId == cajaId && t.CerradoUtc == null, ct);

    /// <summary>Un turno por cajero, y una caja no puede tener dos turnos abiertos.</summary>
    public async Task<Turno> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondoCentavos, CancellationToken ct = default)
    {
        if (fondoCentavos < 0)
            throw new ArgumentOutOfRangeException(nameof(fondoCentavos), "El fondo no puede ser negativo.");
        var caja = await db.Cajas.FindAsync([cajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        if (await TurnoAbiertoAsync(cajaId, ct) is not null)
            throw new InvalidOperationException($"La {caja.Nombre} ya tiene un turno abierto: primero el corte del turno anterior.");

        var turno = new Turno { SucursalId = caja.SucursalId, CajaId = cajaId, CajeroId = cajeroId, FondoCentavos = fondoCentavos, AbiertoUtc = Ahora };
        db.Turnos.Add(turno);
        await db.SaveChangesAsync(ct);
        return turno;
    }

    /// <summary>F7 y F8: entrada o salida de efectivo, siempre con motivo.</summary>
    public async Task<MovimientoCaja> RegistrarMovimientoAsync(
        Guid turnoId, TipoMovimientoCaja tipo, long montoCentavos, string motivo, Guid usuarioId, CancellationToken ct = default)
    {
        if (montoCentavos <= 0)
            throw new ArgumentOutOfRangeException(nameof(montoCentavos), "El monto debe ser mayor a cero.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Toda entrada o salida de efectivo lleva motivo.", nameof(motivo));
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);

        var movimiento = new MovimientoCaja { TurnoId = turno.Id, Tipo = tipo, MontoCentavos = montoCentavos, Motivo = motivo.Trim(), UsuarioId = usuarioId, FechaUtc = Ahora };
        db.MovimientosCaja.Add(movimiento);
        db.AperturasCajon.Add(new AperturaCajon { CajaId = turno.CajaId, TurnoId = turno.Id, UsuarioId = usuarioId, Motivo = MotivoApertura.SinVenta, Nota = $"{tipo}: {motivo.Trim()}", FechaUtc = Ahora });
        await db.SaveChangesAsync(ct);
        return movimiento;
    }

    /// <summary>
    /// F12: asigna el folio de la serie, aplica los cobros, descuenta del inventario lo vendido (guardando su costo en
    /// cada línea) y registra la apertura del cajón si hubo efectivo.
    /// </summary>
    public async Task<Venta> CobrarAsync(Venta venta, IReadOnlyList<CobroSolicitado> cobros, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(venta.TurnoId, ct);
        if (turno.CajaId != venta.CajaId || turno.SucursalId != venta.SucursalId)
            throw new InvalidOperationException("La venta no es del turno abierto de esta caja.");

        venta.ValidarCobros(cobros);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var ahora = Ahora;
            var folio = await SiguienteFolioAsync(venta.SucursalId, venta.Serie, ct);

            var inventario = new RegistroInventario(db);
            foreach (var linea in venta.Lineas.Where(l => l.LlevaInventario && l.VarianteId is not null))
            {
                var variante = await db.Variantes.FindAsync([linea.VarianteId!.Value], ct)
                    ?? throw new InvalidOperationException($"«{linea.Descripcion}» ya no está en el catálogo.");
                var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
                var salida = Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Venta, linea.CantidadBase, existencia,
                    await UltimoCostoAsync(variante.Id, ct), ahora, documentoTipo: nameof(Venta), documentoId: venta.Id);
                linea.CostoCentavos = -salida.CostoCentavos;
                await inventario.AgregarAsync(salida, ct);
            }

            venta.Pagar(folio, cobros, ahora);
            db.Ventas.Add(venta);
            if (venta.LlevaEfectivo)
                db.AperturasCajon.Add(new AperturaCajon { CajaId = venta.CajaId, TurnoId = turno.Id, UsuarioId = venta.CajeroId, Motivo = MotivoApertura.Venta, VentaId = venta.Id, FechaUtc = ahora });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return venta;
        }
        catch
        {
            // Que no queden en memoria el folio, las existencias ni la venta a medias.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Corte parcial: informativo, el turno sigue abierto.</summary>
    public async Task<Corte> CorteXAsync(Guid turnoId, Guid usuarioId, CancellationToken ct = default)
    {
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.X, usuarioId, Ahora);
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        return corte;
    }

    /// <summary>Corte de cierre: lo hace el encargado contando a ciegas; el turno se cierra.</summary>
    public async Task<Corte> CorteZAsync(Guid turnoId, Guid encargadoId, IReadOnlyDictionary<long, int> conteo, CancellationToken ct = default)
    {
        var encargado = await db.Usuarios.FindAsync([encargadoId], ct) ?? throw new InvalidOperationException("El usuario no existe.");
        if (!encargado.PuedeHacerCorte)
            throw new InvalidOperationException("El corte de cierre lo hace el encargado.");
        var turno = await TurnoAbiertoPorIdAsync(turnoId, ct);

        var ahora = Ahora;
        var corte = CalculoCorte.Calcular(turno, await VentasAsync(turno.Id, ct), await MovimientosAsync(turno.Id, ct), TipoCorte.Z, encargadoId, ahora, conteo);
        turno.CerradoUtc = ahora;
        db.Cortes.Add(corte);
        await db.SaveChangesAsync(ct);
        return corte;
    }

    private async Task<Turno> TurnoAbiertoPorIdAsync(Guid turnoId, CancellationToken ct)
    {
        var turno = await db.Turnos.FindAsync([turnoId], ct) ?? throw new InvalidOperationException("El turno no existe.");
        return turno.Abierto ? turno : throw new InvalidOperationException("El turno ya está cerrado.");
    }

    private async Task<int> SiguienteFolioAsync(Guid sucursalId, string serie, CancellationToken ct)
    {
        var nombre = $"folio:{sucursalId}:{serie}";
        var secuencia = await db.Secuencias.FindAsync([nombre], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = nombre };
            db.Secuencias.Add(secuencia);
        }
        secuencia.Valor++;
        return checked((int)secuencia.Valor);
    }

    private async Task<long> UltimoCostoAsync(Guid varianteId, CancellationToken ct) =>
        await db.ProveedoresVariantes
            .Where(pv => pv.VarianteId == varianteId && pv.UltimoCostoCentavos != null)
            .OrderByDescending(pv => pv.UltimaCompraUtc)
            .Select(pv => pv.UltimoCostoCentavos!.Value)
            .FirstOrDefaultAsync(ct);

    private async Task<List<Venta>> VentasAsync(Guid turnoId, CancellationToken ct) =>
        await db.Ventas.Include(v => v.Cobros).Where(v => v.TurnoId == turnoId).ToListAsync(ct);

    private async Task<List<MovimientoCaja>> MovimientosAsync(Guid turnoId, CancellationToken ct) =>
        await db.MovimientosCaja.Where(m => m.TurnoId == turnoId).ToListAsync(ct);
}
