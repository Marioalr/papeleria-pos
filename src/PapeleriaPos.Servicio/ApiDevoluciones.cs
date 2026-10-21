using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>Cancelar y devolver con el folio del ticket y verificar transferencias. La bitácora se consulta en ApiControl.</summary>
public static class ApiDevoluciones
{
    public static void MapearApiDevoluciones(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // En el piloto con eleventa dice también si el ticket no se devuelve aquí, para que la ventana no lo ofrezca (el de otro
        // turno de eleventa se devuelve allá), y solo ofrece cancelar el del mismo día (CorteEnElPiloto: el turno de esta caja
        // no se cierra aquí). Lo que se le haya devuelto en eleventa entra antes (DevolucionesConEleventa).
        api.MapGet("/sucursales/{sucursalId:guid}/ventas/folio/{folio}", async (Guid sucursalId, string folio, PosDbContext db, OpcionesEleventa eleventa,
            EnvioVentasEleventa envios) =>
        {
            var venta = await new RegistroDevoluciones(db).PorFolioAsync(sucursalId, folio);
            if (venta is null)
                return Results.NotFound(new ErrorDto($"No existe el ticket «{folio}»."));
            string? deOtroTurno = null;
            if (eleventa.CorteEnEleventa)
            {
                // Lo que se le devolvió en eleventa entra antes, y la venta se vuelve a leer: pudo cambiar.
                deOtroTurno = await RevisarEnEleventaAsync(db, venta.Id, eleventa, envios, soloRevisar: true);
                venta = await new RegistroDevoluciones(db).PorFolioAsync(sucursalId, folio) ?? venta;
            }
            var ahora = db.Reloj.GetUtcNow().UtcDateTime;
            var seCancela = await db.Turnos.AnyAsync(t => t.Id == venta.TurnoId && t.CerradoUtc == null)
                && (!eleventa.CorteEnEleventa || CorteEnElPiloto.SeCancela(venta.PagadaUtc!.Value, ahora));
            var dias = Devolucion.DiasParaDevolver - (int)(ahora - venta.PagadaUtc!.Value).TotalDays;
            var cliente = venta.ClienteId is { } clienteId ? (await db.ClientesRegistrados.FindAsync(clienteId))?.Nombre : null;
            var noSeDevuelve = eleventa.SoloEfectivo
                ? await SoloEfectivoEnElPiloto.PorQueNoSeDevuelveAsync(db, venta.Id) ?? await DevolucionesConEleventa.PorQueNoSeDevuelveAsync(db, venta.Id)
                : null;
            // Cancelada o vencida, la ventana ya lo dice.
            if (noSeDevuelve is null && venta.Estado == EstadoVenta.Pagada && dias > 0)
                noSeDevuelve = deOtroTurno;
            return Results.Ok(new VentaParaDevolverDto(venta.Id, venta.FolioTexto!, HoraMexico.ALocal(venta.PagadaUtc.Value), venta.Estado, venta.TotalCentavos,
                venta.Lineas.Select(l => new LineaDevolucionDto(l.Id, l.Descripcion, l.Cantidad.Milesimas, l.CantidadDevuelta.Milesimas, l.ImporteCentavos, l.LlevaInventario)).ToList(),
                seCancela && venta.Estado == EstadoVenta.Pagada, Math.Max(0, dias), venta.CreditoCentavos, cliente, noSeDevuelve));
        });

        // Si la venta pasó a eleventa, la cancelación o devolución ya está en la bandeja: que vaya ya. En el piloto con
        // eleventa, lo devuelto sale solo en efectivo (SoloEfectivoEnElPiloto) y solo se cancela lo del mismo día
        // (CorteEnElPiloto).
        api.MapPost("/ventas/{ventaId:guid}/cancelacion", async (Guid ventaId, SolicitudCancelacion s, PosDbContext db, IOptions<OpcionesTicket> opciones,
            EnvioVentasEleventa eleventa, OpcionesEleventa opcionesEleventa) =>
        {
            if (opcionesEleventa.SoloEfectivo)
                await SoloEfectivoEnElPiloto.RevisarDevolucionAsync(db, ventaId);
            if (opcionesEleventa.CorteEnEleventa)
                await CorteEnElPiloto.RevisarCancelacionAsync(db, ventaId);
            await RevisarEnEleventaAsync(db, ventaId, opcionesEleventa, eleventa);
            var devolucion = await new RegistroDevoluciones(db).CancelarAsync(ventaId, s.UsuarioId, s.CajaId, s.Motivo, s.AutorizacionId);
            eleventa.Despertar();
            return await ComprobanteAsync(db, devolucion, opciones.Value);
        });

        api.MapPost("/ventas/{ventaId:guid}/devoluciones", async (Guid ventaId, SolicitudDevolucion s, PosDbContext db, IOptions<OpcionesTicket> opciones,
            EnvioVentasEleventa eleventa, OpcionesEleventa opcionesEleventa) =>
        {
            if (opcionesEleventa.SoloEfectivo)
                await SoloEfectivoEnElPiloto.RevisarDevolucionAsync(db, ventaId);
            await RevisarEnEleventaAsync(db, ventaId, opcionesEleventa, eleventa);
            var lineas = s.Lineas.Select(l => new LineaPorDevolver(l.LineaId, new Cantidad(l.CantidadMilesimas), l.RegresaAlInventario)).ToList();
            var devolucion = await new RegistroDevoluciones(db).DevolverAsync(ventaId, lineas, s.UsuarioId, s.CajaId, s.Motivo, s.AutorizacionId);
            eleventa.Despertar();
            return await ComprobanteAsync(db, devolucion, opciones.Value);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/transferencias", async (Guid sucursalId, PosDbContext db) =>
        {
            var desde = db.Reloj.GetUtcNow().UtcDateTime.AddDays(-30);
            var transferencias = await (from c in db.Cobros
                                        join v in db.Ventas on c.VentaId equals v.Id
                                        join u in db.Usuarios on v.CajeroId equals u.Id
                                        where v.SucursalId == sucursalId && c.Forma == FormaPago.Transferencia && v.PagadaUtc >= desde
                                        select new { Cobro = c, Venta = v, Cajero = u.Nombre }).ToListAsync();
            // También las de los abonos de clientes (fase 3): se verifican igual, por su propia ruta.
            var abonos = await (from a in db.Abonos
                                join c in db.ClientesRegistrados on a.ClienteId equals c.Id
                                join u in db.Usuarios on a.UsuarioId equals u.Id
                                where a.SucursalId == sucursalId && a.Forma == FormaPago.Transferencia && a.FechaUtc >= desde && a.CanceladoUtc == null
                                select new { Abono = a, Cliente = c.Nombre, Cajero = u.Nombre }).ToListAsync();
            return transferencias
                .Select(t => (Fecha: t.Venta.PagadaUtc!.Value, Dto: new TransferenciaDto(t.Cobro.Id, t.Venta.FolioTexto!, HoraMexico.ALocal(t.Venta.PagadaUtc!.Value),
                    t.Cobro.MontoCentavos, t.Cobro.Referencia, t.Cajero, t.Cobro.Verificacion)))
                .Concat(abonos.Select(a => (Fecha: a.Abono.FechaUtc, Dto: new TransferenciaDto(a.Abono.Id, a.Abono.Folio, HoraMexico.ALocal(a.Abono.FechaUtc),
                    a.Abono.MontoCentavos, a.Abono.Referencia, a.Cajero, a.Abono.Verificacion, EsAbono: true, Cliente: a.Cliente))))
                .OrderBy(t => t.Dto.Verificacion != Verificacion.PorVerificar).ThenByDescending(t => t.Fecha)
                .Select(t => t.Dto)
                .ToList();
        });

        api.MapPost("/cobros/{cobroId:guid}/verificacion", async (Guid cobroId, SolicitudVerificacion s, PosDbContext db) =>
        {
            var cobro = await new RegistroCaja(db).VerificarTransferenciaAsync(cobroId, s.Usuario, s.Pin, s.Llego);
            return new { cobro.Id, cobro.Verificacion };
        });
    }

    /// <summary>
    /// El piloto (DevolucionesConEleventa): antes de devolver o cancelar aquí un ticket de la caja 2 que ya está en eleventa,
    /// lo que se le haya devuelto allá entra antes, con el candado de la bandeja y sin esperarla mucho; si eleventa no
    /// contesta, se deja pasar (decisión del dueño, 2026-10-11) y un choque queda por revisar. Si allá ya se le devolvió
    /// algo, lo que queda se devuelve en eleventa; y el de otro turno de eleventa (su turno aquí ya se cerró con el corte de
    /// eleventa), también.
    /// </summary>
    /// <param name="soloRevisar">Al buscar el ticket: la ventana lo enseña con el porqué (<c>NoSeDevuelveEnElPiloto</c>).</param>
    /// <returns>Si es de otro turno de eleventa, por qué (<see cref="DevolucionesConEleventa.DeOtroTurno"/>).</returns>
    private static async Task<string?> RevisarEnEleventaAsync(PosDbContext db, Guid ventaId, OpcionesEleventa opciones, EnvioVentasEleventa envios,
        bool soloRevisar = false)
    {
        if (!opciones.CorteEnEleventa || opciones.ParaEscribir() is not { } destino)
            return null;
        // Con el puente detenido (eleventa se restauró) sus números ya no dicen qué es de quién: se deja pasar.
        DevolucionesConEleventa.VistaEnVivo? vista = null;
        using (var candado = await RestauracionDeEleventa.DetencionAsync(db) is null ? await envios.IntentarCandadoAsync(DevolucionesConEleventa.EsperaEnVivo) : null)
        {
            if (candado is not null)
                vista = await DevolucionesConEleventa.RevisarEnVivoAsync(db, destino.Servidor, ventaId, db.Reloj.GetUtcNow().UtcDateTime, destino.Caja.CajaId);
        }
        // De otro turno de eleventa: lo dice eleventa en vivo; sin él, el turno de la venta aquí (cuarta revisión).
        var deOtroTurno = vista is { } vistaEnVivo
            ? vistaEnVivo.DelTurno ? null : DevolucionesConEleventa.DeOtroTurno
            : await DevolucionesConEleventa.DeOtroTurnoAsync(db, ventaId);
        if (!soloRevisar && (await DevolucionesConEleventa.PorQueNoSeDevuelveAsync(db, ventaId) ?? deOtroTurno) is { } porque)
            throw new InvalidOperationException(porque);
        return deOtroTurno;
    }

    private static async Task<ComprobanteDevolucionDto> ComprobanteAsync(PosDbContext db, Devolucion d, OpcionesTicket opciones)
    {
        var venta = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == d.VentaId);
        var caja = await db.Cajas.FindAsync(d.CajaId);
        var nombres = await db.Usuarios.Where(u => u.Id == d.UsuarioId || u.Id == d.AutorizoId).ToDictionaryAsync(u => u.Id, u => u.Nombre);
        var encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, d.SucursalId, opciones)).RenglonesEncabezado;
        var lineas = d.Lineas.Select(l =>
        {
            var linea = venta.Lineas.Single(x => x.Id == l.VentaLineaId);
            var cantidad = new VentaLinea { Descripcion = linea.Descripcion, Unidad = linea.Unidad, Cantidad = l.Cantidad };
            return new LineaTicketDto(ArmadoTicket.CantidadTexto(cantidad, esBase: true), linea.Descripcion, linea.PrecioCentavos, l.ImporteCentavos);
        }).ToList();
        // Lo que se regresa por su medio de pago es la tarjeta y la transferencia; lo fiado baja la cuenta del cliente.
        var cliente = venta.ClienteId is { } clienteId ? (await db.ClientesRegistrados.FindAsync(clienteId))?.Nombre : null;
        return new ComprobanteDevolucionDto(venta.FolioTexto!, d.EsCancelacion, d.TotalCentavos, d.ReembolsoEfectivoCentavos, d.Motivo,
            HoraMexico.ALocal(d.FechaUtc), caja!.Nombre, nombres[d.UsuarioId], d.AutorizoId is { } a ? nombres.GetValueOrDefault(a) : null, lineas, encabezado,
            d.RedondeoCentavos,
            d.EsCancelacion ? venta.Cobros.Where(c => c.Forma is FormaPago.Tarjeta or FormaPago.Transferencia).Sum(c => c.MontoCentavos) : 0,
            d.AlCreditoCentavos, cliente, d.AlCreditoCentavos > 0 ? await new RegistroCredito(db).SaldoDespuesDeAsync(d.Id) : null,
            // Lo que una cancelación deja de contar en el corte (la caja lleva su propia cuenta del turno).
            d.EsCancelacion ? venta.Cobros.Where(c => c.Forma == FormaPago.Tarjeta).Sum(c => c.MontoCentavos) : 0,
            d.EsCancelacion ? venta.Cobros.Where(c => c.Forma == FormaPago.Transferencia).Sum(c => c.MontoCentavos) : 0);
    }
}
