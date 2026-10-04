using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>Cancelar y devolver con el folio del ticket, verificar transferencias y consultar la bitácora.</summary>
public static class ApiDevoluciones
{
    public static void MapearApiDevoluciones(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/ventas/folio/{folio}", async (Guid sucursalId, string folio, PosDbContext db) =>
        {
            var venta = await new RegistroDevoluciones(db).PorFolioAsync(sucursalId, folio);
            if (venta is null)
                return Results.NotFound(new ErrorDto($"No existe el ticket «{folio}»."));
            var turnoAbierto = await db.Turnos.AnyAsync(t => t.Id == venta.TurnoId && t.CerradoUtc == null);
            var dias = Devolucion.DiasParaDevolver - (int)(db.Reloj.GetUtcNow().UtcDateTime - venta.PagadaUtc!.Value).TotalDays;
            return Results.Ok(new VentaParaDevolverDto(venta.Id, venta.FolioTexto!, HoraMexico.ALocal(venta.PagadaUtc.Value), venta.Estado, venta.TotalCentavos,
                venta.Lineas.Select(l => new LineaDevolucionDto(l.Id, l.Descripcion, l.Cantidad.Milesimas, l.CantidadDevuelta.Milesimas, l.ImporteCentavos, l.LlevaInventario)).ToList(),
                turnoAbierto && venta.Estado == EstadoVenta.Pagada, Math.Max(0, dias)));
        });

        api.MapPost("/ventas/{ventaId:guid}/cancelacion", async (Guid ventaId, SolicitudCancelacion s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var devolucion = await new RegistroDevoluciones(db).CancelarAsync(ventaId, s.UsuarioId, s.CajaId, s.Motivo, s.AutorizacionId);
            return await ComprobanteAsync(db, devolucion, opciones.Value);
        });

        api.MapPost("/ventas/{ventaId:guid}/devoluciones", async (Guid ventaId, SolicitudDevolucion s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var lineas = s.Lineas.Select(l => new LineaPorDevolver(l.LineaId, new Cantidad(l.CantidadMilesimas), l.RegresaAlInventario)).ToList();
            var devolucion = await new RegistroDevoluciones(db).DevolverAsync(ventaId, lineas, s.UsuarioId, s.CajaId, s.Motivo, s.AutorizacionId);
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
            return transferencias
                .OrderBy(t => t.Cobro.Verificacion != Verificacion.PorVerificar).ThenByDescending(t => t.Venta.PagadaUtc)
                .Select(t => new TransferenciaDto(t.Cobro.Id, t.Venta.FolioTexto!, HoraMexico.ALocal(t.Venta.PagadaUtc!.Value), t.Cobro.MontoCentavos,
                    t.Cobro.Referencia, t.Cajero, t.Cobro.Verificacion))
                .ToList();
        });

        api.MapPost("/cobros/{cobroId:guid}/verificacion", async (Guid cobroId, SolicitudVerificacion s, PosDbContext db) =>
        {
            var cobro = await new RegistroCaja(db).VerificarTransferenciaAsync(cobroId, s.Usuario, s.Pin, s.Llego);
            return new { cobro.Id, cobro.Verificacion };
        });

        api.MapPost("/sucursales/{sucursalId:guid}/bitacora/consulta", async (Guid sucursalId, SolicitudConsultaBitacora s, PosDbContext db) =>
        {
            var control = new ServicioControl(db);
            await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConsultarBitacora);
            var registros = await control.ConsultarAsync(sucursalId,
                HoraMexico.AUtc(s.Desde.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(s.Hasta.AddDays(1).ToDateTime(TimeOnly.MinValue)));
            return registros.Select(r => new BitacoraDto(HoraMexico.ALocal(r.Registro.FechaUtc), r.Usuario, r.Registro.Accion, r.Registro.Detalle,
                r.Registro.MontoCentavos, r.Autorizo, r.Registro.Resultado)).ToList();
        });
    }

    private static async Task<ComprobanteDevolucionDto> ComprobanteAsync(PosDbContext db, Devolucion d, OpcionesTicket opciones)
    {
        var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == d.VentaId);
        var caja = await db.Cajas.FindAsync(d.CajaId);
        var nombres = await db.Usuarios.Where(u => u.Id == d.UsuarioId || u.Id == d.AutorizoId).ToDictionaryAsync(u => u.Id, u => u.Nombre);
        var encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, d.SucursalId, opciones)).RenglonesEncabezado;
        var lineas = d.Lineas.Select(l =>
        {
            var linea = venta.Lineas.Single(x => x.Id == l.VentaLineaId);
            var cantidad = new VentaLinea { Descripcion = linea.Descripcion, Unidad = linea.Unidad, Cantidad = l.Cantidad };
            return new LineaTicketDto(ApiCaja.CantidadTexto(cantidad, esBase: true), linea.Descripcion, linea.PrecioCentavos, l.ImporteCentavos);
        }).ToList();
        return new ComprobanteDevolucionDto(venta.FolioTexto!, d.EsCancelacion, d.TotalCentavos, d.ReembolsoEfectivoCentavos, d.Motivo,
            HoraMexico.ALocal(d.FechaUtc), caja!.Nombre, nombres[d.UsuarioId], d.AutorizoId is { } a ? nombres.GetValueOrDefault(a) : null, lineas, encabezado);
    }
}
