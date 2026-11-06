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

    /// <summary>
    /// Busca un ticket por el folio que trae impreso: el de la caja («A-000123», también «a123») o, en el piloto, el de eleventa. Solo
    /// el número, fuera del piloto, es el de la serie de <paramref name="cajaId"/>; sin caja, el más reciente de la sucursal.
    /// </summary>
    public async Task<Venta?> PorFolioAsync(Guid sucursalId, string folio, CancellationToken ct = default, Guid? cajaId = null)
    {
        var texto = folio.Trim().ToUpperInvariant();
        if (VentasDeOtrosDias.FormaDelFolio().Match(texto) is not { Success: true } partes)
            return null;
        var numero = int.Parse(partes.Groups["numero"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var ventas = db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).Where(v => v.SucursalId == sucursalId);
        if (partes.Groups["serie"].Success)
        {
            return await PorSerieAsync(ventas, partes.Groups["serie"].Value, numero, ct);
        }

        // En el piloto, el ticket impreso lleva el folio de eleventa (solo número) si eleventa contestó a tiempo; si no, el de
        // la caja, con su serie. Si eleventa repitió el folio en otra caja, la caja enseña los dos antes de llegar aquí (el puente,
        // FoliosRepetidos); aquí, el más reciente de esta caja.
        if (await db.EnviosAEleventa.AnyAsync(ct))
        {
            var ventaId = await db.EnviosAEleventa.Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.FolioEleventa == numero)
                .OrderByDescending(e => e.CreadoUtc).Select(e => e.VentaId).FirstOrDefaultAsync(ct);
            return ventaId is null ? null : await ventas.SingleOrDefaultAsync(v => v.Id == ventaId, ct);
        }
        if (cajaId is { } caja && await db.Cajas.Where(c => c.Id == caja).Select(c => c.Serie).FirstOrDefaultAsync(ct) is { } deLaCaja)
            return await PorSerieAsync(ventas, deLaCaja, numero, ct);
        return await ventas.Where(v => v.Folio == numero).OrderByDescending(v => v.PagadaUtc).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Por lo que trae impreso: un ticket sin conexión con un folio que ya era de otra venta se guardó con el siguiente, y los que subieron después
    /// se recorren igual (<see cref="RegistroCaja"/>). Su nota dice con cuál salió, y ese es el que busca quien vuelve con él: si no, se
    /// devolvería la venta de otro ticket (revisión de la rama junta, 2026-11-06).
    /// </summary>
    private static async Task<Venta?> PorSerieAsync(IQueryable<Venta> ventas, string serie, int numero, CancellationToken ct)
    {
        var impreso = RegistroCaja.SalioConElFolio(serie, numero);
        return await ventas.Where(v => v.Serie == serie && v.EnContingencia && v.Nota != null && v.Nota.StartsWith(impreso)).FirstOrDefaultAsync(ct)
               ?? await ventas.SingleOrDefaultAsync(v => v.Serie == serie && v.Folio == numero, ct);
    }

    /// <summary>
    /// La que ya quedó con ese Id (lo pone la ventana al abrir el ticket): si se perdió la respuesta y se repite, es esta, antes de
    /// revisar nada más, y no se registra otra.
    /// </summary>
    public async Task<Devolucion?> YaHechaAsync(Guid? id, Guid ventaId, CancellationToken ct = default) =>
        id is null ? null : await db.Devoluciones.Include(d => d.Lineas).SingleOrDefaultAsync(d => d.Id == id && d.VentaId == ventaId, ct);

    /// <summary>
    /// La que ya quedó con ese Id, si es lo que se pide ahora (<paramref name="lineas"/>; vacío, cancelar). Si se perdió la respuesta y
    /// se cambió lo escrito sin volver a buscar el ticket, su comprobante diría que se hizo otra cosa: el error dice lo que quedó.
    /// </summary>
    public async Task<Devolucion?> YaHechaAsync(Guid? id, Guid ventaId, IReadOnlyList<LineaPorDevolver>? lineas, CancellationToken ct = default)
    {
        if (await YaHechaAsync(id, ventaId, ct) is not { } hecha)
            return null;
        var esLaMisma = lineas is null
            ? hecha.EsCancelacion
            : !hecha.EsCancelacion && lineas.GroupBy(l => l.VentaLineaId).Select(g => (g.Key, g.Sum(l => l.Cantidad.Milesimas), g.All(l => l.RegresaAlInventario))).ToHashSet()
                .SetEquals(hecha.Lineas.Select(l => (l.VentaLineaId, l.Cantidad.Milesimas, l.RegresaAlInventario)));
        return esLaMisma ? hecha : throw YaQuedoOtra(hecha.EsCancelacion, hecha.TotalCentavos, hecha.ReembolsoEfectivoCentavos);
    }

    public static SolicitudDistinta YaQuedoOtra(bool esCancelacion, long totalCentavos, long efectivoCentavos) => new(
        $"Ya quedó registrada {(esCancelacion ? "la cancelación" : "una devolución")} de este ticket por {AlertasTelegram.Dinero(totalCentavos)}"
        + (efectivoCentavos > 0 ? $": entrega {AlertasTelegram.Dinero(efectivoCentavos)} en efectivo" : "") + ". Vuelve a buscarlo para ver lo que quedó.");

    public async Task<Devolucion> CancelarAsync(
        Guid ventaId, Guid usuarioId, Guid cajaId, string motivo, Guid? autorizacionId, Guid? id = null, CancellationToken ct = default)
    {
        var venta = await VentaAsync(ventaId, ct);
        var turno = await TurnoAbiertoAsync(cajaId, ct);
        var devolucion = ConId(CalculoDevolucion.ArmarCancelacion(venta, turno.Id, usuarioId, motivo, Ahora), id);
        return await GuardarAsync(venta, devolucion, AccionControlada.CancelarTicket, $"Canceló el ticket {venta.FolioTexto}", autorizacionId, ct);
    }

    public async Task<Devolucion> DevolverAsync(
        Guid ventaId, IReadOnlyList<LineaPorDevolver> lineas, Guid usuarioId, Guid cajaId, string motivo, Guid? autorizacionId, Guid? id = null, CancellationToken ct = default)
    {
        var venta = await VentaAsync(ventaId, ct);
        var turno = await TurnoAbiertoAsync(cajaId, ct);
        var anteriores = await db.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).ToListAsync(ct);
        var devolucion = ConId(CalculoDevolucion.Armar(venta, lineas, cajaId, turno.Id, usuarioId, motivo, Ahora, anteriores), id);
        return await GuardarAsync(venta, devolucion, AccionControlada.Devolucion, $"Devolución del ticket {venta.FolioTexto}", autorizacionId, ct);
    }

    private static Devolucion ConId(Devolucion devolucion, Guid? id)
    {
        if (id is { } suyo)
        {
            devolucion.Id = suyo;
            foreach (var linea in devolucion.Lineas)
                linea.DevolucionId = suyo;
        }
        return devolucion;
    }

    private async Task<Devolucion> GuardarAsync(Venta venta, Devolucion devolucion, AccionControlada accion, string detalle, Guid? autorizacionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Otra petición con el mismo Id pudo guardarla mientras esta esperaba su turno.
            if (await YaHechaAsync(devolucion.Id, venta.Id, ct) is { } yaEsta)
                return yaEsta;

            // El turno se buscó antes del candado: si le hicieron el corte a la vez, este reembolso ya no entra a él.
            if (!await db.Turnos.AsNoTracking().AnyAsync(t => t.Id == devolucion.TurnoId && t.CerradoUtc == null, ct))
                throw new InvalidOperationException("El turno ya está cerrado: cambia de usuario (Ctrl+U) para abrir otro.");

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
                // Si después se juntó con otro, regresa al que quedó: la existencia del inactivo ya no se vende.
                var variante = await RegistroInventario.EnLaQueQuedoAsync(db, varianteId, ct) ?? throw new InvalidOperationException($"«{linea.Descripcion}» ya no está en el catálogo.");
                var cantidadBase = new Cantidad(Redondeo.Dividir((Int128)linea.CantidadBase.Milesimas * d.Cantidad.Milesimas, linea.Cantidad.Milesimas));
                if (!variante.AdmiteCantidad(cantidadBase))
                    throw new ArgumentException($"De «{linea.Descripcion}» se devuelven piezas enteras.");
                // Un ticket de antes puede traer un costo negativo (el promedio de una existencia con su valor en negativo): entra sin valor.
                await inventario.AgregarAsync(Kardex.Entrada(venta.SucursalId, variante, TipoMovimiento.Devolucion, cantidadBase, Math.Max(0, d.CostoCentavos), Ahora,
                    documentoTipo: nameof(Devolucion), documentoId: devolucion.Id), ct);
                if (!d.RegresaAlInventario)
                {
                    var existencia = await inventario.ExistenciaAsync(venta.SucursalId, variante.Id, ct);
                    await inventario.AgregarAsync(Kardex.Salida(venta.SucursalId, variante, TipoMovimiento.Merma, cantidadBase, existencia, 0, Ahora,
                        $"Devuelto dañado ({venta.FolioTexto})", nameof(Devolucion), devolucion.Id), ct);
                }
            }

            // Si se cancela o se devuelve la línea "Impresiones" (siempre completa), esos trabajos vuelven a estar por cobrar; los de
            // Control Print dejan de contar como cobrados aquí y, si allá siguen por cobrar, vuelven a salir en F2.
            // Las copias del panel van en su propia línea (2026-10-29): devolver una completa solo libera lo suyo.
            if (CalculoDevolucion.ImpresionesQueLibera(venta, devolucion) is var (impresiones, copias) && (impresiones || copias))
                await ImpresionesDelPos.EnLaVenta(db).LiberarAsync(venta.Id, impresiones, copias, ct);

            CalculoDevolucion.Aplicar(venta, devolucion);
            db.Devoluciones.Add(devolucion);
            // El piloto en la caja 2: si la venta va a eleventa, su cancelación o devolución también, después de ella, a nombre de
            // quien la hizo.
            if (await db.EnviosAEleventa.AnyAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == venta.Id, ct))
                db.EnviosAEleventa.Add(new EnvioAEleventa
                {
                    Tipo = devolucion.EsCancelacion ? TipoEnvioEleventa.Cancelacion : TipoEnvioEleventa.Devolucion,
                    VentaId = venta.Id, DevolucionId = devolucion.Id, CreadoUtc = Ahora,
                    CajeroEleventa = await CajeroDeEleventa.DeAsync(db, devolucion.UsuarioId, ct),
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
