using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Cancelaciones y devoluciones: pasan por las reglas, sacan el reembolso de la caja (cuenta en el corte) y regresan
/// la mercancía al inventario al mismo costo con que salió, o la registran como merma si llegó dañada.
/// </summary>
public sealed class RegistroDevoluciones(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Busca un ticket por su folio ("A-000123"), como viene en el QR.</summary>
    public async Task<Venta?> PorFolioAsync(Guid sucursalId, string folio, CancellationToken ct = default)
    {
        // En el piloto, el ticket impreso lleva el folio de eleventa (solo número); el QR, el de la caja (A-000123).
        if (folio.Trim() is { Length: > 0 } soloNumero && soloNumero.All(char.IsAsciiDigit) && int.TryParse(soloNumero, out var deEleventa))
        {
            var ventaId = await db.EnviosAEleventa.Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == deEleventa).Select(e => e.VentaId).FirstOrDefaultAsync(ct);
            return ventaId is null ? null : await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros)
                .SingleOrDefaultAsync(v => v.Id == ventaId && v.SucursalId == sucursalId, ct);
        }

        var partes = folio.Trim().ToUpperInvariant().Split('-');
        if (partes.Length != 2 || !int.TryParse(partes[1], out var numero))
            return null;
        return await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros)
            .SingleOrDefaultAsync(v => v.SucursalId == sucursalId && v.Serie == partes[0] && v.Folio == numero, ct);
    }

    public async Task<Devolucion> CancelarAsync(Guid ventaId, Guid usuarioId, Guid cajaId, string motivo, Guid? autorizacionId, CancellationToken ct = default)
    {
        var venta = await VentaAsync(ventaId, ct);
        var turno = await TurnoAbiertoAsync(cajaId, ct);
        var devolucion = CalculoDevolucion.ArmarCancelacion(venta, turno.Id, usuarioId, motivo, Ahora);
        return await GuardarAsync(venta, devolucion, AccionControlada.CancelarTicket, $"Canceló el ticket {venta.FolioTexto}", autorizacionId, ct);
    }

    public async Task<Devolucion> DevolverAsync(
        Guid ventaId, IReadOnlyList<LineaPorDevolver> lineas, Guid usuarioId, Guid cajaId, string motivo, Guid? autorizacionId, CancellationToken ct = default)
    {
        var venta = await VentaAsync(ventaId, ct);
        var turno = await TurnoAbiertoAsync(cajaId, ct);
        var anteriores = await db.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).ToListAsync(ct);
        var devolucion = CalculoDevolucion.Armar(venta, lineas, cajaId, turno.Id, usuarioId, motivo, Ahora, anteriores);
        return await GuardarAsync(venta, devolucion, AccionControlada.Devolucion, $"Devolución del ticket {venta.FolioTexto}", autorizacionId, ct);
    }

    private async Task<Devolucion> GuardarAsync(Venta venta, Devolucion devolucion, AccionControlada accion, string detalle, Guid? autorizacionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Lo devuelto se calculó con el ticket leído antes: si otra caja lo canceló o devolvió algo mientras tanto, no
            // se reembolsa (ni se baja de la cuenta del cliente) dos veces.
            var actual = await db.Ventas.AsNoTracking().Include(v => v.Lineas).SingleAsync(v => v.Id == venta.Id, ct);
            if (actual.Estado != venta.Estado || actual.Lineas.Any(l => l.CantidadDevuelta != venta.Lineas.Single(x => x.Id == l.Id).CantidadDevuelta))
                throw new InvalidOperationException("El ticket cambió mientras tanto (se canceló o se devolvió algo en otra caja): vuelve a buscarlo.");

            var control = new ServicioControl(db);
            await control.RequerirAsync(devolucion.UsuarioId, venta.SucursalId, devolucion.CajaId, accion, devolucion.TotalCentavos, detalle,
                autorizacionId, devolucion.Motivo, venta.Id, ct);
            devolucion.AutorizoId = autorizacionId is { } id ? (await db.Autorizaciones.FindAsync([id], ct))?.AutorizadorId : null;

            var inventario = new RegistroInventario(db);
            foreach (var d in devolucion.Lineas)
            {
                var linea = venta.Lineas.Single(l => l.Id == d.VentaLineaId);
                if (!linea.LlevaInventario || linea.VarianteId is not { } varianteId)
                    continue;
                var variante = await db.Variantes.FindAsync([varianteId], ct) ?? throw new InvalidOperationException($"«{linea.Descripcion}» ya no está en el catálogo.");
                var cantidadBase = new Cantidad(Redondeo.Dividir((Int128)linea.CantidadBase.Milesimas * d.Cantidad.Milesimas, linea.Cantidad.Milesimas));
                await inventario.AgregarAsync(Kardex.Entrada(venta.SucursalId, variante, TipoMovimiento.Devolucion, cantidadBase, d.CostoCentavos, Ahora,
                    documentoTipo: nameof(Devolucion), documentoId: devolucion.Id), ct);
                if (!d.RegresaAlInventario)
                {
                    var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
                    await inventario.AgregarAsync(Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Merma, cantidadBase, existencia, 0, Ahora,
                        $"Devuelto dañado ({venta.FolioTexto})", nameof(Devolucion), devolucion.Id), ct);
                }
            }

            // Si se cancela o se devuelve la línea "Impresiones", esos trabajos vuelven a estar por cobrar.
            if (devolucion.EsCancelacion || devolucion.Lineas.Any(d => venta.Lineas.Single(l => l.Id == d.VentaLineaId).EsImpresiones))
                await ImpresionesDelPos.EnLaVenta(db).LiberarAsync(venta.Id, ct);

            CalculoDevolucion.Aplicar(venta, devolucion);
            db.Devoluciones.Add(devolucion);
            // El piloto en la caja 2: si la venta va a eleventa, su cancelación o devolución también, después de ella.
            if (await db.EnviosAEleventa.AnyAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == venta.Id, ct))
                db.EnviosAEleventa.Add(new EnvioAEleventa
                {
                    Tipo = devolucion.EsCancelacion ? TipoEnvioEleventa.Cancelacion : TipoEnvioEleventa.Devolucion,
                    VentaId = venta.Id, DevolucionId = devolucion.Id, CreadoUtc = Ahora,
                });
            // De una venta fiada, lo devuelto baja primero la cuenta del cliente.
            await new RegistroCredito(db).RevertirAsync(venta, devolucion, ct);
            if (devolucion.ReembolsoEfectivoCentavos > 0)
                db.AperturasCajon.Add(new AperturaCajon
                {
                    CajaId = devolucion.CajaId, TurnoId = devolucion.TurnoId, UsuarioId = devolucion.UsuarioId, Motivo = MotivoApertura.SinVenta,
                    Nota = $"{(devolucion.EsCancelacion ? "Cancelación" : "Devolución")} {venta.FolioTexto}", FechaUtc = Ahora,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return devolucion;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Venta> VentaAsync(Guid ventaId, CancellationToken ct) =>
        await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId, ct)
            ?? throw new InvalidOperationException("La venta no existe.");

    private async Task<Turno> TurnoAbiertoAsync(Guid cajaId, CancellationToken ct) =>
        await new RegistroCaja(db).TurnoAbiertoAsync(cajaId, ct) ?? throw new InvalidOperationException("Abre un turno en esta caja para reembolsar.");
}
