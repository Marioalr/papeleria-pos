using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>Vincular el chat del dueño y las autorizaciones que se piden por Telegram.</summary>
public sealed class ServicioTelegram(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    // ── Vincular ────────────────────────────────────────────────────────────

    /// <summary>El código para escribirle al bot. Vincula su chat quien tiene «Configurar Telegram».</summary>
    public async Task<CodigoVinculacion> CodigoAsync(string usuario, string pin, Guid sucursalId, CancellationToken ct = default)
    {
        var dueno = await new ServicioControl(db).IdentificarAsync(usuario, pin, sucursalId, AccionControlada.ConfigurarTelegram, ct);
        var codigo = new CodigoVinculacion { UsuarioId = dueno.Id, Codigo = CodigoVinculacion.Nuevo(), VenceUtc = Ahora + CodigoVinculacion.Vigencia };
        db.CodigosVinculacion.Add(codigo);
        await db.SaveChangesAsync(ct);
        return codigo;
    }

    /// <summary>"/vincular 123456" desde un chat. Devuelve el usuario si el código vale; el código se gasta.</summary>
    public async Task<Usuario?> VincularAsync(string codigo, long chatId, string? nombre, CancellationToken ct = default)
    {
        var ahora = Ahora;
        var vigente = await db.CodigosVinculacion.FirstOrDefaultAsync(c => c.Codigo == codigo.Trim() && c.UsadoUtc == null && c.VenceUtc > ahora, ct);
        if (vigente is null || await db.Usuarios.FindAsync([vigente.UsuarioId], ct) is not { Activo: true } usuario
            || !await new ServicioControl(db).PuedeAsync(usuario.Id, AccionControlada.ConfigurarTelegram, ct))
            return null;
        vigente.UsadoUtc = ahora;
        foreach (var anterior in await db.ChatsTelegram.Where(c => c.ChatId == chatId && c.Activo).ToListAsync(ct))
            anterior.Activo = false;
        db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = usuario.Id, ChatId = chatId, Nombre = nombre, VinculadoUtc = ahora });
        await db.SaveChangesAsync(ct);
        return usuario;
    }

    public async Task DesvincularAsync(Guid chatId, string usuario, string pin, Guid sucursalId, CancellationToken ct = default)
    {
        await new ServicioControl(db).IdentificarAsync(usuario, pin, sucursalId, AccionControlada.ConfigurarTelegram, ct);
        var chat = await db.ChatsTelegram.FindAsync([chatId], ct) ?? throw new InvalidOperationException("Ese chat no existe.");
        chat.Activo = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>El usuario de un chat vinculado, o nada si ese chat no es de nadie.</summary>
    public async Task<Usuario?> UsuarioDelChatAsync(long chatId, CancellationToken ct = default) =>
        await db.ChatsTelegram.Where(c => c.ChatId == chatId && c.Activo)
            .Join(db.Usuarios.Where(u => u.Activo), c => c.UsuarioId, u => u.Id, (c, u) => u)
            .FirstOrDefaultAsync(ct);

    // ── Autorizar a distancia ───────────────────────────────────────────────

    /// <summary>La caja pide la autorización: le llega al dueño con botones Aprobar y Rechazar.</summary>
    public async Task<SolicitudRemota> PedirAsync(
        AccionControlada accion, Guid sucursalId, Guid? cajaId, Guid solicitanteId, long? monto, string detalle, CancellationToken ct = default)
    {
        var chats = AlertasTelegram.Chats(db);
        if (chats.Count == 0)
            throw new InvalidOperationException("El dueño todavía no vincula su Telegram (Configuración → Telegram).");
        var ahora = Ahora;
        var solicitud = new SolicitudRemota
        {
            Accion = accion, SucursalId = sucursalId, CajaId = cajaId, SolicitanteId = solicitanteId, MontoCentavos = monto, Detalle = detalle,
            CreadaUtc = ahora, VenceUtc = ahora + SolicitudRemota.Espera, Estado = EstadoSolicitudRemota.Pendiente,
        };
        db.SolicitudesRemotas.Add(solicitud);
        AlertasTelegram.Agregar(db, chats, null, await TextoAsync(solicitud, ct), solicitudId: solicitud.Id);
        await db.SaveChangesAsync(ct);
        return solicitud;
    }

    /// <summary>
    /// El dueño contestó con un botón. Aprobada, queda la misma autorización de un solo uso que da el PIN (marcada como
    /// de Telegram); rechazada, queda en la bitácora. Devuelve lo que se le contesta en Telegram.
    /// </summary>
    public async Task<string> ResponderAsync(Guid solicitudId, long chatId, bool aprobar, CancellationToken ct = default)
    {
        var solicitud = await db.SolicitudesRemotas.FindAsync([solicitudId], ct);
        if (solicitud is null)
            return "Esa solicitud ya no existe.";
        if (solicitud.Estado != EstadoSolicitudRemota.Pendiente)
            return $"Ya estaba {Estado(solicitud.Estado)}.";
        var ahora = Ahora;
        if (ahora > solicitud.VenceUtc)
        {
            await CerrarAsync(solicitud, EstadoSolicitudRemota.Vencida, null, ct);
            return "Venció: la caja tiene que pedirla otra vez.";
        }
        var usuario = await UsuarioDelChatAsync(chatId, ct);
        if (usuario is null
            || !Permisos.PuedeAutorizar(await new ServicioControl(db).PermisosAsync(usuario.Id, ct), solicitud.Accion, solicitud.MontoCentavos))
            return "Este chat no puede autorizar.";

        if (aprobar)
        {
            var autorizacion = new Autorizacion
            {
                Accion = solicitud.Accion, SucursalId = solicitud.SucursalId, CajaId = solicitud.CajaId, SolicitanteId = solicitud.SolicitanteId,
                AutorizadorId = usuario.Id, MontoCentavos = solicitud.MontoCentavos, Detalle = solicitud.Detalle, CreadaUtc = ahora,
                VenceUtc = ahora + Autorizacion.VigenciaDe(solicitud.Accion), PorTelegram = true,
            };
            db.Autorizaciones.Add(autorizacion);
            solicitud.AutorizacionId = autorizacion.Id;
        }
        else
        {
            new ServicioControl(db).Registrar(solicitud.SucursalId, solicitud.CajaId, solicitud.SolicitanteId, solicitud.Accion,
                $"Rechazada por Telegram: {solicitud.Detalle}", Permisos.MontoParaBitacora(solicitud.Accion, solicitud.MontoCentavos), autorizoId: usuario.Id, resultado: ResultadoBitacora.Negado);
        }
        await CerrarAsync(solicitud, aprobar ? EstadoSolicitudRemota.Aprobada : EstadoSolicitudRemota.Rechazada, usuario, ct);
        return aprobar ? "Aprobada: la caja ya puede seguir." : "Rechazada.";
    }

    public async Task CancelarAsync(Guid solicitudId, CancellationToken ct = default)
    {
        if (await db.SolicitudesRemotas.FindAsync([solicitudId], ct) is { Estado: EstadoSolicitudRemota.Pendiente } solicitud)
            await CerrarAsync(solicitud, EstadoSolicitudRemota.Cancelada, null, ct);
    }

    /// <summary>Las que nadie contestó a tiempo.</summary>
    public async Task VencerAsync(CancellationToken ct = default)
    {
        var ahora = Ahora;
        foreach (var solicitud in await db.SolicitudesRemotas.Where(s => s.Estado == EstadoSolicitudRemota.Pendiente && s.VenceUtc < ahora).ToListAsync(ct))
            await CerrarAsync(solicitud, EstadoSolicitudRemota.Vencida, null, ct);
    }

    /// <summary>Lo que la caja ve mientras espera: cuando la aprueban, recibe la autorización para seguir.</summary>
    public async Task<(SolicitudRemota Solicitud, Autorizacion? Autorizacion, string? Respondio)> EstadoAsync(Guid solicitudId, CancellationToken ct = default)
    {
        var solicitud = await db.SolicitudesRemotas.FindAsync([solicitudId], ct) ?? throw new InvalidOperationException("La solicitud no existe.");
        if (solicitud.Estado == EstadoSolicitudRemota.Pendiente && Ahora > solicitud.VenceUtc)
            await CerrarAsync(solicitud, EstadoSolicitudRemota.Vencida, null, ct);
        var autorizacion = solicitud.AutorizacionId is { } a ? await db.Autorizaciones.FindAsync([a], ct) : null;
        var respondio = solicitud.RespondioId is { } r ? await db.Usuarios.Where(u => u.Id == r).Select(u => u.Nombre).FirstOrDefaultAsync(ct) : null;
        return (solicitud, autorizacion, respondio);
    }

    /// <summary>Cierra la solicitud y cambia el texto del mensaje en todos los chats (ya sin botones).</summary>
    private async Task CerrarAsync(SolicitudRemota solicitud, EstadoSolicitudRemota estado, Usuario? respondio, CancellationToken ct)
    {
        solicitud.Estado = estado;
        solicitud.RespondioId = respondio?.Id;
        solicitud.RespondidaUtc = Ahora;
        var texto = await TextoAsync(solicitud, ct) + "\n\n" + estado switch
        {
            EstadoSolicitudRemota.Aprobada => $"Aprobada por {respondio?.Nombre}.",
            EstadoSolicitudRemota.Rechazada => $"Rechazada por {respondio?.Nombre}.",
            EstadoSolicitudRemota.Vencida => "Venció sin respuesta.",
            _ => "La caja ya no la necesita.",
        };
        foreach (var mensaje in await db.MensajesTelegram.Where(m => m.SolicitudId == solicitud.Id).ToListAsync(ct))
        {
            if (mensaje.MensajeId is null)
                mensaje.Texto = texto;
            else
                mensaje.TextoNuevo = texto;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> TextoAsync(SolicitudRemota s, CancellationToken ct)
    {
        var caja = s.CajaId is { } c ? await db.Cajas.Where(x => x.Id == c).Select(x => x.Nombre).FirstOrDefaultAsync(ct) : null;
        var quien = await db.Usuarios.Where(u => u.Id == s.SolicitanteId).Select(u => u.Nombre).FirstOrDefaultAsync(ct);
        return $"Autorización · {caja ?? "Caja"}\n{quien} pide: {s.Detalle}"
            + (s.MontoCentavos is { } m ? $"\n{Permisos.TextoMonto(s.Accion, m)}" : "");
    }

    private static string Estado(EstadoSolicitudRemota e) => e switch
    {
        EstadoSolicitudRemota.Aprobada => "aprobada",
        EstadoSolicitudRemota.Rechazada => "rechazada",
        EstadoSolicitudRemota.Vencida => "vencida",
        _ => "cancelada",
    };
}
