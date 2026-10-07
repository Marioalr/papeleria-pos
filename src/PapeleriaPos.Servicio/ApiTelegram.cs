using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>Telegram (entrega 4): vincular el chat del dueño, ajustes de las alertas y autorizaciones a distancia.</summary>
public static class ApiTelegram
{
    public static void MapearApiTelegram(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/telegram", async (Guid sucursalId, PosDbContext db, MotorTelegram motor) =>
        {
            var chats = await (from c in db.ChatsTelegram
                               join u in db.Usuarios on c.UsuarioId equals u.Id
                               where c.Activo
                               orderby c.VinculadoUtc
                               select new { c.Id, u.Nombre, Chat = c.Nombre, c.VinculadoUtc }).ToListAsync();
            return new EstadoTelegramDto(motor.Configurado, motor.NombreBot,
                chats.Select(c => new ChatTelegramDto(c.Id, c.Nombre, c.Chat, HoraMexico.ALocal(c.VinculadoUtc))).ToList(),
                Ajustes(AlertasTelegram.Configuracion(db, sucursalId)));
        });

        api.MapPost("/sucursales/{sucursalId:guid}/telegram/codigo", async (Guid sucursalId, SolicitudCredenciales s, PosDbContext db, MotorTelegram motor) =>
        {
            var codigo = await new ServicioTelegram(db).CodigoAsync(s.Usuario, s.Pin, sucursalId);
            return new CodigoVinculacionDto(codigo.Codigo, HoraMexico.ALocal(codigo.VenceUtc), motor.NombreBot);
        });

        api.MapPost("/sucursales/{sucursalId:guid}/telegram/chats/{chatId:guid}/desvincular", async (Guid sucursalId, Guid chatId, SolicitudCredenciales s, PosDbContext db) =>
        {
            await new ServicioTelegram(db).DesvincularAsync(chatId, s.Usuario, s.Pin, sucursalId);
            return Results.NoContent();
        });

        api.MapPut("/sucursales/{sucursalId:guid}/telegram/alertas", async (Guid sucursalId, SolicitudAlertas s, PosDbContext db) =>
        {
            await new ServicioControl(db).IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarTelegram);
            var a = s.Alertas;
            if (a.UmbralCorteCentavos < 0 || a.UmbralDevolucionCentavos < 0 || a.UmbralSalidaCentavos < 0 || a.MinutosSinConexion < 1)
                throw new ArgumentException("Los umbrales no pueden ser negativos y la caja sin conexión avisa desde 1 minuto.");
            if (a.HoraCierre <= a.HoraApertura)
                throw new ArgumentException("La hora de cierre va después de la de apertura.");
            var configuracion = await db.ConfiguracionesSucursal.FindAsync(sucursalId);
            if (configuracion is null)
            {
                configuracion = new ConfiguracionSucursal { SucursalId = sucursalId };
                db.ConfiguracionesSucursal.Add(configuracion);
            }
            configuracion.Apagar(a.Apagadas);
            configuracion.UmbralCorteCentavos = a.UmbralCorteCentavos;
            configuracion.UmbralDevolucionCentavos = a.UmbralDevolucionCentavos;
            configuracion.UmbralSalidaCentavos = a.UmbralSalidaCentavos;
            configuracion.UmbralAjusteCentavos = a.UmbralAjusteCentavos;
            configuracion.MinutosSinConexion = a.MinutosSinConexion;
            configuracion.HoraApertura = a.HoraApertura;
            configuracion.HoraCierre = a.HoraCierre;
            await db.SaveChangesAsync();
            return Ajustes(configuracion);
        });

        api.MapPost("/sucursales/{sucursalId:guid}/telegram/prueba", async (Guid sucursalId, SolicitudCredenciales s, PosDbContext db) =>
        {
            var dueno = await new ServicioControl(db).IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarTelegram);
            if (!AlertasTelegram.Agregar(db, AlertasTelegram.Chats(db), null, $"Mensaje de prueba de la caja: {dueno.Nombre}, así te llegarán las alertas."))
                throw new InvalidOperationException("Todavía no hay un chat vinculado.");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ── Autorización a distancia ─────────────────────────────────────────

        api.MapPost("/autorizaciones/remotas", async (SolicitudAutorizacionRemota s, PosDbContext db, MotorTelegram motor) =>
        {
            if (!motor.Configurado)
                throw new InvalidOperationException("Telegram no está configurado en la caja principal.");
            var solicitud = await new ServicioTelegram(db).PedirAsync(s.Accion, s.SucursalId, s.CajaId, s.SolicitanteId, s.MontoCentavos, s.Detalle);
            return new SolicitudRemotaDto(solicitud.Id, solicitud.VenceUtc);
        });

        api.MapGet("/autorizaciones/remotas/{id:guid}", async (Guid id, PosDbContext db) =>
        {
            var (solicitud, autorizacion, respondio) = await new ServicioTelegram(db).EstadoAsync(id);
            return new EstadoSolicitudRemotaDto(solicitud.Estado,
                autorizacion is null ? null : new AutorizacionDto(autorizacion.Id, autorizacion.VenceUtc, respondio ?? ""), respondio);
        });

        api.MapPost("/autorizaciones/remotas/{id:guid}/cancelar", async (Guid id, PosDbContext db) =>
        {
            await new ServicioTelegram(db).CancelarAsync(id);
            return Results.NoContent();
        });
    }

    private static AjustesAlertasDto Ajustes(ConfiguracionSucursal c) =>
        new(c.Apagadas(), c.UmbralCorteCentavos, c.UmbralDevolucionCentavos, c.UmbralSalidaCentavos, c.MinutosSinConexion, c.HoraApertura, c.HoraCierre,
            c.UmbralAjusteCentavos);
}
