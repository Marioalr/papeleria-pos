using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El piloto, paso 3 (docs/migracion-eleventa.md, «Mandar cada venta»): manda a eleventa las ventas de la bandeja. Al
/// cobrar se intenta enseguida, para que el ticket salga con el folio de eleventa; si no contesta a tiempo, el ticket
/// sale con el folio de esta caja y la venta se manda en la siguiente vuelta. Una venta a la vez: la marca que evita
/// duplicarla en eleventa no aguanta dos envíos de la misma venta al mismo tiempo.
/// </summary>
public sealed class EnvioVentasEleventa(
    IServiceScopeFactory alcances, OpcionesEleventa opciones, TimeProvider reloj, IHostApplicationLifetime vida, ILogger<EnvioVentasEleventa> log)
    : BackgroundService
{
    /// <summary>Después de una falla de red, cuánto tiempo el cobro ya no espera a eleventa (para no tardar en cada venta).</summary>
    public static readonly TimeSpan SinRedPor = TimeSpan.FromMinutes(1);

    private readonly (ServidorEleventa Servidor, CajaEnEleventa Caja)? _destino = opciones.ParaEscribir();
    private readonly SemaphoreSlim _uno = new(1, 1);
    private readonly SemaphoreSlim _ahora = new(0, 1);
    private DateTime _sinRedHastaUtc;
    private DateTime? _ultimoEnvioUtc;
    private string? _ultimoError;

    public bool Activo => _destino is not null;

    /// <summary>
    /// Pone la venta en la bandeja antes de cobrarla: se guarda en la misma transacción que la venta, así no hay venta
    /// cobrada que se quede sin mandar.
    /// </summary>
    public void Encolar(PosDbContext db, Guid ventaId)
    {
        if (Activo)
            db.EnviosAEleventa.Add(new EnvioAEleventa { VentaId = ventaId, CreadoUtc = reloj.GetUtcNow().UtcDateTime });
    }

    /// <summary>Recién cobrada: el folio de eleventa si contestó a tiempo; si no, vacío, y se manda en la siguiente vuelta.</summary>
    public async Task<int?> MandarAlCobrarAsync(Guid ventaId)
    {
        // Sin red hace poco, o todavía va un envío (la red lenta, lo que quedó pendiente): no se espera en cada cobro; la
        // vuelta de fondo la manda.
        if (!Activo || reloj.GetUtcNow().UtcDateTime < _sinRedHastaUtc || _uno.CurrentCount == 0)
            return null;
        try
        {
            // Si no alcanza, el envío sigue por su lado y deja su resultado en la bandeja.
            return await MandarAsync(ventaId, vida.ApplicationStopping).WaitAsync(TimeSpan.FromSeconds(opciones.SegundosAlCobrar));
        }
        catch (TimeoutException)
        {
            // Tan lenta como si no hubiera red: los siguientes cobros no esperan hasta que un envío entre.
            _sinRedHastaUtc = reloj.GetUtcNow().UtcDateTime + SinRedPor;
            return null;
        }
    }

    /// <summary>Que la siguiente vuelta sea ya.</summary>
    public void Despertar()
    {
        try
        {
            _ahora.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya estaba pedida.
        }
    }

    public async Task<EstadoEnviosEleventaDto> EstadoAsync(PosDbContext db)
    {
        var porAtender = await (from e in db.EnviosAEleventa.AsNoTracking()
                                join v in db.Ventas.AsNoTracking() on e.VentaId equals v.Id
                                where e.Estado != EstadoEnvioEleventa.Enviada || e.FolioRepetido
                                orderby e.CreadoUtc
                                select new { Envio = e, v.Serie, v.Folio, v.PagadaUtc, v.TotalCentavos, v.RedondeoCentavos })
            .Take(100).ToListAsync();
        var conteo = await db.EnviosAEleventa.AsNoTracking().GroupBy(e => e.Estado).Select(g => new { g.Key, Cuantos = g.Count() }).ToListAsync();
        string? problema = opciones.Escribir && _destino is null
            ? "Falta el servidor, la base, la clave, la caja o el cajero de eleventa en la configuración."
            : null;
        return new EstadoEnviosEleventaDto(Activo, _destino is { } d ? $"{d.Servidor}, caja {d.Caja.CajaId}" : null, problema,
            conteo.Where(c => c.Key == EstadoEnvioEleventa.Pendiente).Sum(c => c.Cuantos),
            conteo.Where(c => c.Key == EstadoEnvioEleventa.PorRevisar).Sum(c => c.Cuantos),
            porAtender.Count(x => x.Envio.FolioRepetido),
            _ultimoEnvioUtc is { } u ? HoraMexico.ALocal(u) : null, _ultimoError,
            porAtender.Select(x => new EnvioEleventaDto(x.Envio.VentaId, $"{x.Serie}-{x.Folio:000000}", HoraMexico.ALocal(x.PagadaUtc ?? x.Envio.CreadoUtc),
                x.TotalCentavos + x.RedondeoCentavos, x.Envio.Estado, x.Envio.Intentos, x.Envio.Error, x.Envio.FolioEleventa)).ToList());
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_destino is null)
        {
            if (opciones.Escribir)
                log.LogWarning("Mandar las ventas a eleventa está activo, pero falta el servidor, la base, la clave, la caja o el cajero.");
            return;
        }
        var espera = TimeSpan.FromSeconds(Math.Max(5, opciones.SegundosEntreEnvios));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await VueltaAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "No se pudo recorrer la bandeja de eleventa.");
            }
            try
            {
                await _ahora.WaitAsync(espera, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>De la más vieja a la más nueva. Si es la red, las demás tampoco entrarían: se espera a la siguiente vuelta.</summary>
    private async Task VueltaAsync(CancellationToken ct)
    {
        List<Guid> pendientes;
        using (var alcance = alcances.CreateScope())
        {
            pendientes = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().EnviosAEleventa.AsNoTracking()
                .Where(e => e.Estado == EstadoEnvioEleventa.Pendiente).OrderBy(e => e.CreadoUtc).Select(e => e.VentaId).ToListAsync(ct);
        }
        foreach (var ventaId in pendientes)
        {
            await MandarAsync(ventaId, ct);
            if (reloj.GetUtcNow().UtcDateTime < _sinRedHastaUtc)
                return;
        }
    }

    private async Task<int?> MandarAsync(Guid ventaId, CancellationToken ct)
    {
        await _uno.WaitAsync(ct);
        try
        {
            return await MandarUnaAsync(ventaId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // De esta base, no de eleventa: la venta sigue en la bandeja y se intenta en la siguiente vuelta.
            log.LogError(ex, "No se pudo preparar la venta {Venta} para eleventa.", ventaId);
            return null;
        }
        finally
        {
            _uno.Release();
        }
    }

    private async Task<int?> MandarUnaAsync(Guid ventaId, CancellationToken ct)
    {
        var (servidor, caja) = _destino!.Value;
        using var alcance = alcances.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var envio = await db.EnviosAEleventa.FindAsync([ventaId], ct);
        if (envio is not { Estado: EstadoEnvioEleventa.Pendiente })
            return envio?.FolioEleventa;

        var armada = await VentasParaEleventa.ArmarAsync(db, ventaId, ct);
        if (armada.Venta is not { } venta)
        {
            (envio.Estado, envio.Error) = (EstadoEnvioEleventa.PorRevisar, armada.PorRevisar);
            await db.SaveChangesAsync(ct);
            log.LogWarning("La venta {Venta} no pasa sola a eleventa: {Motivo}", ventaId, armada.PorRevisar);
            return null;
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        envio.Intentos++;
        envio.UltimoIntentoUtc = ahora;
        try
        {
            var r = await EnvioEleventa.MandarAsync(servidor, caja, venta, ct);
            (envio.Estado, envio.TicketEleventa, envio.FolioEleventa, envio.FolioRepetido, envio.EnviadaUtc, envio.Error) =
                (EstadoEnvioEleventa.Enviada, r.TicketId, r.Folio, r.FolioRepetido, reloj.GetUtcNow().UtcDateTime, null);
            (_ultimoEnvioUtc, _ultimoError, _sinRedHastaUtc) = (envio.EnviadaUtc, null, default);
            if (r.FolioRepetido)
                log.LogWarning("Eleventa le dio a la venta {Venta} el folio {Folio}, que otra caja usó al mismo tiempo: hay que revisarlo.", ventaId, r.Folio);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var explicado = LectorEleventa.Explicar(ex, servidor);
            envio.Error = _ultimoError = explicado.Length > 500 ? explicado[..500] : explicado;
            if (EsDeRed(ex))
                _sinRedHastaUtc = ahora + SinRedPor;
            log.LogWarning(ex, "La venta {Venta} no entró a eleventa ({Servidor}); se vuelve a intentar.", ventaId, servidor);
        }
        // Aunque se esté apagando el servicio: si ya entró a eleventa, que quede anotado (si no, al volver lo encuentra la marca).
        await db.SaveChangesAsync(CancellationToken.None);
        return envio.FolioEleventa;
    }

    /// <summary>La PC apagada, la red caída o lenta: no es de la venta, es de la red.</summary>
    private static bool EsDeRed(Exception ex) =>
        ex is FbException { ErrorCode: 335544721 /* network_error */ or 335544726 /* net_read_err */ or 335544727 /* net_write_err */ }
            or TimeoutException or System.Net.Sockets.SocketException or IOException
        || ex.InnerException is { } interna && EsDeRed(interna);
}
