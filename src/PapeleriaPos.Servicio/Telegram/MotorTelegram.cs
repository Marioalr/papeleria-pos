using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Servicio.Telegram;

/// <summary>Cuándo se supo por última vez de cada caja dada de alta (su latido trae la llave).</summary>
public sealed class PresenciaCajas
{
    private readonly ConcurrentDictionary<Guid, DateTime> _ultimaVez = new();

    public void Visto(Guid cajaId, DateTime ahoraUtc) => _ultimaVez[cajaId] = ahoraUtc;

    /// <summary>Vacío si no se ha sabido de ella desde que arrancó el servicio.</summary>
    public DateTime? UltimaVez(Guid cajaId) => _ultimaVez.TryGetValue(cajaId, out var visto) ? visto : null;
}

/// <summary>
/// Lo que hace el bot: manda la cola de mensajes (y les cambia el texto cuando hace falta), lee lo que contesta el
/// dueño (vincular, botones de autorización, /resumen) y revisa las alertas que dependen del tiempo (cajas sin
/// conexión, turnos fuera de horario, solicitudes vencidas). Cada paso se puede llamar solo, para probarlo.
/// </summary>
public sealed class MotorTelegram(IServiceScopeFactory alcances, ITelegram telegram, PresenciaCajas presencia, ILogger<MotorTelegram> log)
{
    private const string Offset = "telegram:offset";

    public string? NombreBot { get; private set; }

    public bool Configurado => telegram.Configurado;

    public async Task IniciarAsync(CancellationToken ct)
    {
        try
        {
            NombreBot = await telegram.NombreBotAsync(ct);
        }
        catch (TelegramException ex)
        {
            log.LogWarning("No se pudo leer el nombre del bot: {Error}", ex.Message);
        }
    }

    /// <summary>Manda lo pendiente en orden. Sin internet se detiene y lo intenta en la siguiente vuelta.</summary>
    public async Task EnviarAsync(CancellationToken ct = default)
    {
        using var alcance = alcances.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;

        foreach (var m in await db.MensajesTelegram.Where(m => m.EnviadoUtc == null && m.Error == null).OrderBy(m => m.CreadoUtc).Take(20).ToListAsync(ct))
        {
            // Una alerta de ayer ya no sirve: no se manda de golpe cuando vuelve el internet.
            if (ahora - m.CreadoUtc > TimeSpan.FromDays(1))
            {
                m.Error = "Venció sin enviarse.";
                continue;
            }
            var botones = m.SolicitudId is { } s && await db.SolicitudesRemotas.FindAsync([s], ct) is { Estado: EstadoSolicitudRemota.Pendiente }
                ? new[] { new BotonTelegram("Aprobar", $"aut:{s:N}:1"), new BotonTelegram("Rechazar", $"aut:{s:N}:0") }
                : null;
            try
            {
                m.MensajeId = await telegram.EnviarAsync(m.ChatId, m.Texto, botones, ct);
                m.EnviadoUtc = ahora;
            }
            catch (TelegramException ex) when (ex.Permanente)
            {
                m.Error = ex.Message;
            }
            catch (TelegramException ex)
            {
                m.Intentos++;
                log.LogInformation("Telegram no contesta; se reintenta: {Error}", ex.Message);
                break;
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var m in await db.MensajesTelegram.Where(m => m.TextoNuevo != null && m.MensajeId != null).ToListAsync(ct))
        {
            try
            {
                await telegram.EditarAsync(m.ChatId, m.MensajeId!.Value, m.TextoNuevo!, ct);
                m.Texto = m.TextoNuevo!;
                m.TextoNuevo = null;
            }
            catch (TelegramException ex) when (ex.Permanente)
            {
                m.TextoNuevo = null;
            }
            catch (TelegramException)
            {
                break;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lee lo que llegó al bot y lo atiende. El avance se guarda: lo leído no se vuelve a atender.</summary>
    public async Task LeerAsync(int esperaSegundos, CancellationToken ct = default)
    {
        using var alcance = alcances.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var secuencia = await db.Secuencias.FindAsync([Offset], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = Offset };
            db.Secuencias.Add(secuencia);
        }

        var servicio = new ServicioTelegram(db);
        foreach (var a in await telegram.LeerAsync(secuencia.Valor, esperaSegundos, ct))
        {
            secuencia.Valor = a.Id + 1;
            if (a.Datos is { } datos && datos.StartsWith("aut:", StringComparison.Ordinal))
            {
                var partes = datos.Split(':');
                var respuesta = partes.Length == 3 && Guid.TryParse(partes[1], out var solicitud)
                    ? await servicio.ResponderAsync(solicitud, a.ChatId, partes[2] == "1", ct)
                    : "No entendí ese botón.";
                await ContestarBotonAsync(a, respuesta, ct);
            }
            else if (a.Texto is { } texto)
            {
                await AtenderTextoAsync(db, servicio, a, texto.Trim(), ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ContestarBotonAsync(ActualizacionTelegram a, string texto, CancellationToken ct)
    {
        if (a.CallbackId is null)
            return;
        try
        {
            await telegram.ContestarBotonAsync(a.CallbackId, texto, ct);
        }
        catch (TelegramException ex)
        {
            log.LogInformation("No se pudo contestar el botón: {Error}", ex.Message);
        }
    }

    /// <summary>Los chats que no están vinculados solo pueden vincularse; lo demás se ignora.</summary>
    private static async Task AtenderTextoAsync(PosDbContext db, ServicioTelegram servicio, ActualizacionTelegram a, string texto, CancellationToken ct)
    {
        var comando = texto.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? respuesta;
        if (comando is ["/vincular", var codigo])
        {
            var usuario = await servicio.VincularAsync(codigo, a.ChatId, a.Nombre, ct);
            respuesta = usuario is null
                ? "Ese código no vale o ya venció. Pide otro en la caja: Configuración → Telegram."
                : $"Listo, {usuario.Nombre}: aquí te llegarán las alertas y las autorizaciones de la caja.";
        }
        else if (await servicio.UsuarioDelChatAsync(a.ChatId, ct) is null)
        {
            respuesta = comando is ["/start", ..] ? "Para usar este bot, vincúlalo desde la caja: Configuración → Telegram." : null;
        }
        else if (comando is ["/resumen", ..])
        {
            var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime));
            var resumenes = new List<string>();
            foreach (var sucursal in await db.Sucursales.Select(s => s.Id).ToListAsync(ct))
                resumenes.Add(await AlertasTelegram.ResumenAsync(db, sucursal, hoy, ct));
            respuesta = string.Join("\n\n", resumenes);
        }
        else
        {
            respuesta = "Te aviso lo importante de la caja y aquí apruebas las autorizaciones. Escribe /resumen para ver cómo va el día.";
        }
        if (respuesta is not null)
            AlertasTelegram.Agregar(db, [a.ChatId], null, respuesta);
    }

    /// <summary>Lo que depende del tiempo: solicitudes vencidas, cajas que dejaron de comunicarse y turnos fuera de horario.</summary>
    public async Task RevisarAsync(CancellationToken ct = default)
    {
        using var alcance = alcances.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        await new ServicioTelegram(db).VencerAsync(ct);
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;

        var abiertos = await (from t in db.Turnos
                              join c in db.Cajas on t.CajaId equals c.Id
                              join u in db.Usuarios on t.CajeroId equals u.Id
                              where t.CerradoUtc == null
                              select new { Turno = t, Caja = c, Cajero = u.Nombre }).ToListAsync(ct);
        foreach (var x in abiertos)
        {
            var configuracion = AlertasTelegram.Configuracion(db, x.Turno.SucursalId);

            // Solo las cajas dadas de alta mandan latido; si no se sabe de una desde que arrancó el servicio, no se avisa.
            if (x.Caja.TieneEquipo && presencia.UltimaVez(x.Caja.Id) is { } visto
                && ahora - visto > TimeSpan.FromMinutes(configuracion.MinutosSinConexion))
            {
                AlertasTelegram.Avisar(db, x.Turno.SucursalId, TipoAlerta.CajaSinConexion,
                    $"Caja sin conexión · {x.Caja.Nombre}\nNo se comunica con la caja principal desde las {HoraMexico.ALocal(visto):HH:mm}. "
                    + "Si sigue vendiendo, lo hace sin conexión y lo sube al volver.",
                    $"sin-conexion:{x.Caja.Id}:{visto:yyyyMMddHHmm}");
            }

            if (configuracion.FueraDeHorario(HoraMexico.ALocal(ahora)) || configuracion.FueraDeHorario(HoraMexico.ALocal(x.Turno.AbiertoUtc)))
            {
                AlertasTelegram.Avisar(db, x.Turno.SucursalId, TipoAlerta.FueraDeHorario,
                    $"Caja abierta fuera de horario · {x.Caja.Nombre}\nTurno de {x.Cajero} abierto desde las {HoraMexico.ALocal(x.Turno.AbiertoUtc):HH:mm}; "
                    + $"el horario es de {configuracion.HoraApertura:HH\\:mm} a {configuracion.HoraCierre:HH\\:mm}.",
                    $"fuera-horario:{x.Turno.Id}");
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Corre el bot mientras corre el servicio: manda cada segundo, lee con espera larga y revisa cada minuto.</summary>
public sealed class TrabajadorTelegram(MotorTelegram motor, ILogger<TrabajadorTelegram> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!motor.Configurado)
        {
            log.LogInformation("Telegram sin configurar: falta el token del bot (Telegram:Token o telegram-token.txt).");
            return;
        }
        await motor.IniciarAsync(ct);
        await Task.WhenAll(EnvioAsync(ct), LecturaAsync(ct));
    }

    private async Task EnvioAsync(CancellationToken ct)
    {
        var ultimaRevision = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            await SinTumbarAsync(async () =>
            {
                if (DateTime.UtcNow - ultimaRevision > TimeSpan.FromMinutes(1))
                {
                    await motor.RevisarAsync(ct);
                    ultimaRevision = DateTime.UtcNow;
                }
                await motor.EnviarAsync(ct);
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task LecturaAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SinTumbarAsync(() => motor.LeerAsync(esperaSegundos: 25, ct), ct);
        }
    }

    /// <summary>Un error (sin internet, la base ocupada) no detiene al bot: espera y lo vuelve a intentar.</summary>
    private async Task SinTumbarAsync(Func<Task> paso, CancellationToken ct)
    {
        try
        {
            await paso();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Telegram: se reintenta en 10 s.");
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
