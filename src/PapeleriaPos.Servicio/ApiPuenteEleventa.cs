using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Lo que la caja 2 le pide al puente con eleventa (solo del piloto: se quita al dejar eleventa): devolver con el folio
/// de eleventa escrito (también un ticket cobrado en eleventa, la ida), lo que quedó por revisar y si el puente se detuvo
/// o espera al respaldo de eleventa, para su pantalla, y reanudarlo.
/// </summary>
public static class ApiPuenteEleventa
{
    public static void MapearPuente(this RouteGroupBuilder api)
    {
        // Devolver con el folio de eleventa escrito (el ticket ya no lleva QR): los tickets con ese folio, aquí y en eleventa
        // (en vivo), para elegir por la hora y el total si eleventa lo repitió en otra caja.
        api.MapGet("/folio/{folio:int}", async (int folio, PosDbContext db, OpcionesEleventa opciones) =>
        {
            var destino = opciones.ParaEscribir();
            var enEleventa = destino is { } d ? await DevolucionesConEleventa.PorFolioEnVivoAsync(d.Servidor, folio) : null;
            return await FoliosRepetidos.BuscarAsync(db, folio, enEleventa, destino is not null, db.Reloj.GetUtcNow().UtcDateTime);
        });

        // La ida: un ticket cobrado en eleventa, leído en vivo, como lo enseña la ventana de devoluciones.
        api.MapGet("/tickets/{ticket:int}", async (int ticket, PosDbContext db, OpcionesEleventa opciones) =>
        {
            var leido = await DevolucionesConEleventa.TicketEnVivoAsync(Destino(opciones), ticket);
            await NoEsDeEstaCajaAsync(db, leido);
            return await DevolucionesConEleventa.ParaDevolverAsync(db, leido, db.Reloj.GetUtcNow().UtcDateTime);
        });

        // La ida: se vuelve a leer en vivo (otra caja pudo devolver algo mientras tanto) y se devuelve aquí, con las reglas y
        // los permisos de la caja nueva. La devolución va a eleventa por la bandeja.
        api.MapPost("/tickets/{ticket:int}/devolucion", async (int ticket, SolicitudDevolucion s, PosDbContext db, OpcionesEleventa opciones,
            EnvioVentasEleventa envios, IOptions<OpcionesTicket> ticketOpciones) =>
        {
            var leido = await DevolucionesConEleventa.TicketEnVivoAsync(Destino(opciones), ticket);
            await NoEsDeEstaCajaAsync(db, leido);
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var (devolucion, _) = await DevolucionesConEleventa.DevolverAsync(db, leido, s, db.Reloj.GetUtcNow().UtcDateTime);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                envios.Despertar();
                return await ComprobanteAsync(db, devolucion, ticketOpciones.Value);
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });

        // Lo que la pantalla de la caja 2 enseña del puente: lo que quedó por revisar y lo que conviene saber.
        api.MapGet("/pantalla", async (PosDbContext db, EnvioVentasEleventa envios, LecturaEleventa lectura) => await PantallaAsync(db, envios, lectura));

        // Una devolución de eleventa que no se pudo pasar a su venta, ya revisada: deja de enseñarse. Con «Configuración de la
        // tienda», como reanudar la escritura; queda en la bitácora.
        api.MapPost("/vuelta/{id:guid}/revisada", async (Guid id, SolicitudRevision s, PosDbContext db) =>
        {
            var vuelta = await db.Set<DevolucionDesdeEleventa>().FindAsync(id) ?? throw new InvalidOperationException("Esa devolución de eleventa no existe.");
            var venta = await db.Ventas.FindAsync(vuelta.VentaId) ?? throw new InvalidOperationException("La venta no existe.");
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, venta.SucursalId, null, AccionControlada.ConfigurarTienda);
            vuelta.RevisadaUtc = db.Reloj.GetUtcNow().UtcDateTime;
            control.Registrar(venta.SucursalId, venta.CajaId, s.UsuarioId, AccionControlada.ConfigurarTienda,
                $"Revisó la {(vuelta.EsCancelacion ? "cancelación" : "devolución")} en eleventa del ticket {venta.FolioTexto}"
                + (string.IsNullOrWhiteSpace(s.Nota) ? "" : $": {s.Nota.Trim()}"));
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Los respaldos de eleventa: el puente se detuvo porque eleventa ya no tiene lo que le mandó la caja 2 (se restauró un
        // respaldo). Se reanuda a mano, después de revisar eleventa, con «Configuración de la tienda»; queda en la bitácora.
        // Lo que se perdió allá queda por revisar, para anotarlo a mano.
        api.MapPost("/reanudar", async (SolicitudReanudarPuente s, PosDbContext db, EnvioVentasEleventa envios, LecturaEleventa lectura) =>
        {
            var caja = await db.Cajas.FindAsync(s.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda);
            await envios.ReanudarAsync(db, caja, s.UsuarioId, s.Nota);
            lectura.LeerAhora();
            return await PantallaAsync(db, envios, lectura);
        });
    }

    private static ServidorEleventa Destino(OpcionesEleventa opciones) =>
        opciones.ParaEscribir()?.Servidor ?? throw new InvalidOperationException("Esta caja no manda a eleventa: los tickets de eleventa se devuelven en eleventa.");

    /// <summary>Un ticket que mandó esta caja se devuelve con su venta de aquí, no como uno de eleventa.</summary>
    private static async Task NoEsDeEstaCajaAsync(PosDbContext db, TicketLeido t)
    {
        if (await db.EnviosAEleventa.AnyAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.TicketEleventa == t.Id))
            throw new InvalidOperationException("Ese ticket es de esta caja: búscalo con el folio de la caja.");
    }

    private static async Task<ComprobanteDevolucionDto> ComprobanteAsync(PosDbContext db, DevolucionDeEleventa d, OpcionesTicket opciones)
    {
        var caja = await db.Cajas.FindAsync(d.CajaId);
        var nombres = await db.Usuarios.Where(u => u.Id == d.UsuarioId || u.Id == d.AutorizoId).ToDictionaryAsync(u => u.Id, u => u.Nombre);
        var encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, d.SucursalId, opciones)).RenglonesEncabezado;
        var lineas = d.Articulos.Select(a =>
        {
            var leido = d.Ticket.FirstOrDefault(x => x.Id == a.ArticuloEleventa);
            return new LineaTicketDto(a.Cantidad.ToString("0.###", CultureInfo.InvariantCulture), a.Nombre,
                (long)Math.Round((leido?.Precio ?? 0) * 100, MidpointRounding.AwayFromZero), a.ImporteCentavos);
        }).ToList();
        return new ComprobanteDevolucionDto($"{d.Folio} (eleventa, {d.CajaNombre})", false, d.TotalCentavos, d.ReembolsoEfectivoCentavos, d.Motivo,
            HoraMexico.ALocal(d.FechaUtc), caja!.Nombre, nombres[d.UsuarioId], d.AutorizoId is { } a ? nombres.GetValueOrDefault(a) : null, lineas, encabezado,
            d.RedondeoCentavos);
    }

    /// <summary>
    /// Lo que quedó por revisar (de la bandeja y lo de eleventa que no se pudo pasar a su venta) y lo que conviene saber:
    /// lo que espera para irse a eleventa y por qué, los folios repetidos y lo que vio la lectura de cada pocos minutos.
    /// </summary>
    internal static async Task<PuenteEleventaDto> PantallaAsync(PosDbContext db, EnvioVentasEleventa envios, LecturaEleventa lectura)
    {
        var estado = await envios.EstadoAsync(db);
        var porRevisar = estado.PorAtender.Where(e => e.Estado == EstadoEnvioEleventa.PorRevisar)
            .Select(e => new PorRevisarEleventaDto(e.Id, null, e.Descripcion, e.FechaLocal, e.TotalCentavos, e.Error ?? ""))
            .ToList();
        var vueltas = await (from v in db.Set<DevolucionDesdeEleventa>().AsNoTracking()
                             join venta in db.Ventas.AsNoTracking() on v.VentaId equals venta.Id
                             where v.PorRevisar != null && v.RevisadaUtc == null
                             orderby v.DevueltaUtc
                             select new { v.Id, v.EsCancelacion, v.DevueltaUtc, v.TotalCentavos, v.PorRevisar, venta.Serie, venta.Folio }).ToListAsync();
        porRevisar.AddRange(vueltas.Select(v => new PorRevisarEleventaDto(null, v.Id,
            $"{(v.EsCancelacion ? "Cancelación" : "Devolución")} en eleventa de {v.Serie}-{v.Folio:000000}", HoraMexico.ALocal(v.DevueltaUtc), v.TotalCentavos,
            v.PorRevisar!)));

        var avisos = new List<string>();
        if (estado.Pendientes > 0 && estado.PorAtender.FirstOrDefault(e => e.Estado == EstadoEnvioEleventa.Pendiente)?.Error is { } espera)
            avisos.Add($"{estado.Pendientes} por mandar a eleventa: {espera}");
        if (estado.FoliosRepetidos > 0)
            avisos.Add($"{estado.FoliosRepetidos} {(estado.FoliosRepetidos == 1 ? "ticket tiene" : "tickets tienen")} un folio que otra caja de eleventa "
                + "repitió: se reimprimen con el folio de la caja.");
        foreach (var aviso in lectura.Estado.Avisos.Keys.Where(a => a is TurnosDelPiloto.AvisoDosTurnos or TurnosDelPiloto.AvisoTurnoNoEsta))
            avisos.Add(aviso);
        // Los respaldos de eleventa: si el puente se detuvo, y si espera al respaldo del día.
        var detenido = await RestauracionDeEleventa.DetencionAsync(db) is { } detencion
            ? $"Detenido desde el {HoraMexico.ALocal(detencion.DetectadaUtc):dd/MM HH:mm}: eleventa ya no tiene lo que le mandó la caja 2 (¿se restauró un "
                + $"respaldo?): {detencion.Pruebas.Replace("\n", "; ")}. No se le manda nada ni se aplica su lectura; la caja sigue vendiendo. Revisa eleventa "
                + "y reanuda: lo que se perdió allá queda por revisar, para anotarlo a mano."
            : null;
        return new PuenteEleventaDto(porRevisar.OrderBy(p => p.FechaLocal).ToList(), avisos, detenido, envios.EstadoDelRespaldo());
    }
}
