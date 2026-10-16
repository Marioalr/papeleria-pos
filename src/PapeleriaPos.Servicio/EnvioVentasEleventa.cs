using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El piloto, pasos 3 y 4 (docs/migracion-eleventa.md, «Mandar cada venta», «Cancelar, devolver y cerrar el turno» y
/// «Entradas y salidas de dinero»): manda a eleventa la bandeja de salida (ventas, cancelaciones, devoluciones, entradas
/// y salidas de efectivo y cierres de turno), en el orden en que pasaron. Si algo no entra, lo que sigue espera: una
/// devolución nunca llega antes que su venta ni un turno se cierra antes de que lleguen sus ventas, entradas y salidas.
/// Al cobrar se intenta la venta enseguida, para que el ticket salga con el folio de eleventa; si no contesta a tiempo, o
/// hay algo antes en la bandeja, el ticket sale con el folio de esta caja. Uno a la vez: la marca que evita duplicar en
/// eleventa no aguanta dos envíos de lo mismo al mismo tiempo.
/// <para>
/// Lo que falta del piloto (docs/migracion-eleventa.md, «Lo que falta para el piloto»): cada cosa va al turno de eleventa
/// de su hora y lo primero de un turno de la caja nueva le da su turno de eleventa (<see cref="TurnosDelPiloto"/>); lo que
/// choca con eleventa (<see cref="ChoqueConEleventa"/>) queda por revisar sin detener lo demás, y solo la falla de red se
/// reintenta; la revisión tardía de los folios va en su propio paso (<see cref="FoliosRepetidos"/>); y la lectura de
/// eleventa usa el mismo candado (<see cref="CandadoAsync"/>).
/// </para>
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
    /// Solo del piloto: lo que se está mandando (o se anota a mano) y lo que eleventa rechazó, para que la lectura de
    /// eleventa no ajuste la existencia de lo que va y sí descuente lo que no entró.
    /// </summary>
    public EnviosEnCurso EnCurso { get; } = new();

    /// <summary>
    /// Pone la venta en la bandeja antes de cobrarla: se guarda en la misma transacción que la venta, así no hay venta
    /// cobrada que se quede sin mandar. Sus cancelaciones, devoluciones y el cierre de su turno se encolan solos, y las
    /// entradas y salidas, en <c>RegistroCaja</c> si esta caja manda a eleventa (<see cref="Activo"/>).
    /// </summary>
    public void Encolar(PosDbContext db, Guid ventaId)
    {
        if (Activo)
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventaId, CreadoUtc = reloj.GetUtcNow().UtcDateTime });
    }

    /// <summary>Recién cobrada: el folio de eleventa si contestó a tiempo; si no, vacío, y se manda en la vuelta.</summary>
    public async Task<int?> MandarAlCobrarAsync(Guid ventaId)
    {
        // Sin red hace poco, o todavía va un envío (la red lenta, lo que quedó pendiente): no se espera en cada cobro; la
        // vuelta de fondo la manda.
        if (!Activo || reloj.GetUtcNow().UtcDateTime < _sinRedHastaUtc || _uno.CurrentCount == 0)
            return null;
        try
        {
            // Si no alcanza, el envío sigue por su lado y deja su resultado en la bandeja.
            return await VentaAlCobrarAsync(ventaId, vida.ApplicationStopping).WaitAsync(TimeSpan.FromSeconds(opciones.SegundosAlCobrar));
        }
        catch (TimeoutException)
        {
            // Tan lenta como si no hubiera red: los siguientes cobros no esperan hasta que algo entre.
            _sinRedHastaUtc = reloj.GetUtcNow().UtcDateTime + SinRedPor;
            return null;
        }
    }

    /// <summary>
    /// Solo del piloto: la lectura de eleventa usa el mismo candado que la bandeja (docs/migracion-eleventa.md, «Lo que falta
    /// para el piloto»): mientras lee y aplica lo que trajo (el corte de eleventa, las existencias), la bandeja no manda nada;
    /// así lo que la bandeja escribe no se cruza con lo que la lectura acaba de ver. El cobro no espera: si la lectura va, el
    /// ticket sale con el folio de la caja y la venta se manda en la vuelta.
    /// </summary>
    public async Task<IDisposable> CandadoAsync(CancellationToken ct)
    {
        await _uno.WaitAsync(ct);
        return new Soltar(_uno);
    }

    private sealed class Soltar(SemaphoreSlim candado) : IDisposable
    {
        private int _soltado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _soltado, 1) == 0)
                candado.Release();
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

    /// <summary>
    /// Lo que no entra y no se puede arreglar desde aquí se deja por revisar, para que lo que sigue en la bandeja pueda
    /// pasar. Si ya se corrigió a mano en eleventa, va <see cref="AnotarAManoAsync"/>: mientras quede por revisar, la
    /// lectura de eleventa lo sigue descontando de la existencia.
    /// </summary>
    public async Task<bool> DejarPorRevisarAsync(PosDbContext db, Guid id)
    {
        if (await db.EnviosAEleventa.FindAsync(id) is not { Estado: EstadoEnvioEleventa.Pendiente } envio)
            return false;
        envio.Estado = EstadoEnvioEleventa.PorRevisar;
        envio.Error = Recortar($"Se dejó para revisar a mano. {envio.Error}".Trim());
        await db.SaveChangesAsync();
        Despertar();
        return true;
    }

    /// <summary>
    /// Lo que ya quedó en eleventa a mano (alguien lo registró allá, o allá ya estaba), pendiente o por revisar: ya no se
    /// manda, lo que sigue en la bandeja puede pasar, y la lectura de eleventa ya no lo descuenta de la existencia.
    /// </summary>
    public async Task<bool> AnotarAManoAsync(PosDbContext db, Guid id)
    {
        if (await db.EnviosAEleventa.FindAsync(id) is not { Estado: EstadoEnvioEleventa.Pendiente or EstadoEnvioEleventa.PorRevisar } envio)
            return false;
        envio.Estado = EstadoEnvioEleventa.AMano;
        envio.Error = Recortar($"Se anotó a mano en eleventa. {envio.Error}".Trim());
        // Antes de guardarlo: una lectura de eleventa a medias no sabe si lo leyó antes o después de que se registrara allá.
        EnCurso.AnotadoAMano(envio.Id);
        await db.SaveChangesAsync();
        Despertar();
        return true;
    }

    public async Task<EstadoEnviosEleventaDto> EstadoAsync(PosDbContext db)
    {
        var porAtender = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => e.Estado == EstadoEnvioEleventa.Pendiente || e.Estado == EstadoEnvioEleventa.PorRevisar || e.FolioRepetido)
            .OrderBy(e => e.CreadoUtc).Take(100).ToListAsync();
        var ventaIds = porAtender.Where(e => e.VentaId is not null).Select(e => e.VentaId!.Value).Distinct().ToList();
        var ventas = await db.Ventas.AsNoTracking().Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Serie, v.Folio, v.PagadaUtc, Total = v.TotalCentavos + v.RedondeoCentavos }).ToDictionaryAsync(v => v.Id);
        var devolucionIds = porAtender.Where(e => e.DevolucionId is not null).Select(e => e.DevolucionId!.Value).ToList();
        var devoluciones = await db.Devoluciones.AsNoTracking().Where(d => devolucionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.FechaUtc, d.ReembolsoEfectivoCentavos }).ToDictionaryAsync(d => d.Id);
        var movimientoIds = porAtender.Where(e => e.MovimientoId is not null).Select(e => e.MovimientoId!.Value).ToList();
        var movimientos = await db.MovimientosCaja.AsNoTracking().Where(m => movimientoIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Tipo, m.MontoCentavos, m.Motivo, m.FechaUtc }).ToDictionaryAsync(m => m.Id);
        var conteo = await db.EnviosAEleventa.AsNoTracking().GroupBy(e => e.Estado).Select(g => new { g.Key, Cuantos = g.Count() }).ToListAsync();

        EnvioEleventaDto Dto(EnvioAEleventa e)
        {
            var venta = e.VentaId is { } v && ventas.TryGetValue(v, out var x) ? x : null;
            var devolucion = e.DevolucionId is { } d && devoluciones.TryGetValue(d, out var y) ? y : null;
            var movimiento = e.MovimientoId is { } m && movimientos.TryGetValue(m, out var z) ? z : null;
            var folio = venta is null ? "" : $"{venta.Serie}-{venta.Folio:000000}";
            var (descripcion, fecha, total) = e.Tipo switch
            {
                TipoEnvioEleventa.Venta => ($"Venta {folio}", venta?.PagadaUtc ?? e.CreadoUtc, venta?.Total ?? 0),
                TipoEnvioEleventa.Cancelacion => ($"Cancelación de {folio}", devolucion?.FechaUtc ?? e.CreadoUtc, devolucion?.ReembolsoEfectivoCentavos ?? 0),
                TipoEnvioEleventa.Devolucion => ($"Devolución de {folio}", devolucion?.FechaUtc ?? e.CreadoUtc, devolucion?.ReembolsoEfectivoCentavos ?? 0),
                TipoEnvioEleventa.MovimientoDeCaja => (
                    $"{(movimiento?.Tipo == Nucleo.Turnos.TipoMovimientoCaja.Salida ? "Salida" : "Entrada")} de efectivo: {movimiento?.Motivo}",
                    movimiento?.FechaUtc ?? e.CreadoUtc, movimiento?.MontoCentavos ?? 0),
                _ => ("Cierre del turno", e.CreadoUtc, 0L),
            };
            return new EnvioEleventaDto(e.Id, e.Tipo, descripcion, HoraMexico.ALocal(fecha), total, e.Estado, e.Intentos, e.Error, e.FolioEleventa);
        }

        return new EstadoEnviosEleventaDto(Activo, _destino is { } destino ? $"{destino.Servidor}, caja {destino.Caja.CajaId}" : null,
            opciones.Escribir && _destino is null ? "Falta el servidor, la base, la clave, la caja o el cajero de eleventa en la configuración." : null,
            conteo.Where(c => c.Key == EstadoEnvioEleventa.Pendiente).Sum(c => c.Cuantos),
            conteo.Where(c => c.Key == EstadoEnvioEleventa.PorRevisar).Sum(c => c.Cuantos),
            porAtender.Count(e => e.FolioRepetido),
            _ultimoEnvioUtc is { } u ? HoraMexico.ALocal(u) : null, _ultimoError,
            porAtender.Select(Dto).ToList());
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_destino is null)
        {
            if (opciones.Escribir)
                log.LogWarning("Mandar a eleventa está activo, pero falta el servidor, la base, la clave, la caja o el cajero.");
            return;
        }
        var espera = TimeSpan.FromSeconds(Math.Max(5, opciones.SegundosEntreEnvios));
        while (!ct.IsCancellationRequested)
        {
            // La revisión tardía de los folios va en su propio paso: aunque la bandeja esté atorada en otra cosa.
            await RevisarFoliosAsync(ct);
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

    /// <summary>
    /// La revisión tardía de los folios (<see cref="FoliosRepetidos"/>): otra caja pudo guardar el mismo folio un instante
    /// después de la revisión al guardar. Si eleventa no contesta, se revisa en la siguiente vuelta.
    /// </summary>
    private async Task RevisarFoliosAsync(CancellationToken ct)
    {
        var (servidor, _) = _destino!.Value;
        await UnoAsync(async db =>
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            // Sin red hace poco: la vuelta lo vuelve a intentar; aquí no se espera otra vez a que no conteste.
            if (ahora < _sinRedHastaUtc || await FoliosRepetidos.PorRevisarAsync(db, ahora, ct) is not { Count: > 0 } enviadas)
                return true;
            try
            {
                await using var conexion = new FbConnection(servidor.Cadena());
                await conexion.OpenAsync(ct);
                if (await FoliosRepetidos.RevisarAsync(db, conexion, enviadas, ahora, ct) is > 0 and var repetidos)
                    log.LogWarning("{Repetidos} ventas de esta caja tienen en eleventa el mismo folio que otra caja: sus reimpresiones salen con el folio de la caja.",
                        repetidos);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && EsDeRed(ex))
            {
                // Sin red: se revisa en otra vuelta, y el cobro tampoco espera a eleventa un rato.
                _sinRedHastaUtc = ahora + SinRedPor;
            }
            return true;
        }, ct);
    }

    /// <summary>En el orden en que pasaron; en cuanto algo no entra, lo demás espera a la siguiente vuelta.</summary>
    private async Task VueltaAsync(CancellationToken ct)
    {
        List<Guid> pendientes;
        using (var alcance = alcances.CreateScope())
        {
            pendientes = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().EnviosAEleventa.AsNoTracking()
                .Where(e => e.Estado == EstadoEnvioEleventa.Pendiente).OrderBy(e => e.CreadoUtc).Select(e => e.Id).ToListAsync(ct);
        }
        foreach (var id in pendientes)
        {
            if (!await UnoAsync(async db => await db.EnviosAEleventa.FindAsync([id], ct) is not { Estado: EstadoEnvioEleventa.Pendiente } envio
                    || await MandarAsync(db, envio, ct), ct))
                return;
        }
    }

    private async Task<int?> VentaAlCobrarAsync(Guid ventaId, CancellationToken ct)
    {
        int? folio = null;
        await UnoAsync(async db =>
        {
            if (await db.EnviosAEleventa.SingleOrDefaultAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == ventaId, ct) is not { } envio)
                return true;
            // Lo que estaba antes en la bandeja va primero: se pide la vuelta y el ticket sale con el folio de la caja.
            if (await db.EnviosAEleventa.AnyAsync(e => e.Estado == EstadoEnvioEleventa.Pendiente && e.CreadoUtc < envio.CreadoUtc, ct))
            {
                Despertar();
                return true;
            }
            var entro = await MandarAsync(db, envio, ct);
            folio = envio.FolioEleventa;
            return entro;
        }, ct);
        return folio;
    }

    /// <summary>Uno a la vez, cada uno con su propia base. Falso si no entró (lo que sigue tiene que esperar).</summary>
    private async Task<bool> UnoAsync(Func<PosDbContext, Task<bool>> trabajo, CancellationToken ct)
    {
        await _uno.WaitAsync(ct);
        try
        {
            using var alcance = alcances.CreateScope();
            return await trabajo(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // De esta base, no de eleventa: se queda en la bandeja y se intenta en la siguiente vuelta.
            log.LogError(ex, "No se pudo preparar un envío a eleventa.");
            return false;
        }
        finally
        {
            _uno.Release();
        }
    }

    /// <summary>Arma y manda uno de la bandeja. Verdadero si ya no está pendiente (entró, se descartó o queda por revisar).</summary>
    private async Task<bool> MandarAsync(PosDbContext db, EnvioAEleventa envio, CancellationToken ct)
    {
        var (servidor, caja) = _destino!.Value;
        // Escribe en eleventa y dice en qué turno de eleventa quedó (vacío si ya estaba): el turno de la caja nueva lo adopta.
        Func<Task<TurnoDeEleventa?>>? escribir = null;
        string? porRevisar = null;
        var descartada = false;
        switch (envio.Tipo)
        {
            case TipoEnvioEleventa.Venta:
                var venta = await VentasParaEleventa.ArmarAsync(db, envio.VentaId!.Value, ct);
                (porRevisar, descartada) = (venta.PorRevisar, venta.Descartada);
                if (venta.Venta is { } paraEleventa)
                    escribir = async () =>
                    {
                        var r = await EnvioEleventa.MandarAsync(servidor, caja, paraEleventa, ct);
                        (envio.TicketEleventa, envio.FolioEleventa, envio.FolioRepetido) = (r.TicketId, r.Folio, r.FolioRepetido);
                        if (r.FolioAnterior is { } anterior)
                            log.LogWarning("Otra caja de eleventa cobró con el folio {Anterior} al mismo tiempo que la venta {Venta}: esta tomó el {Folio}.",
                                anterior, envio.VentaId, r.Folio);
                        if (r.FolioRepetido)
                            log.LogWarning("Eleventa le dio a la venta {Venta} el folio {Folio}, que otra caja usó al mismo tiempo: hay que revisarlo.", envio.VentaId, r.Folio);
                        return r.Turno;
                    };
                break;
            case TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion:
                var devolucion = await VentasParaEleventa.ArmarDevolucionAsync(db, envio.DevolucionId!.Value, ct);
                (porRevisar, descartada) = (devolucion.PorRevisar, devolucion.Descartada);
                if (devolucion.Devolucion is { } paraDevolver)
                    escribir = async () =>
                    {
                        var r = await EnvioEleventa.DevolverAsync(servidor, caja, paraDevolver, ct);
                        envio.IdEleventa = r.DevolucionId;
                        return r.Turno;
                    };
                break;
            case TipoEnvioEleventa.MovimientoDeCaja:
                var movimiento = await VentasParaEleventa.ArmarMovimientoAsync(db, envio.MovimientoId!.Value, ct);
                porRevisar = movimiento.PorRevisar;
                if (movimiento.Movimiento is { } paraMover)
                    escribir = async () =>
                    {
                        var r = await EnvioEleventa.MoverEfectivoAsync(servidor, caja, paraMover, ct);
                        envio.IdEleventa = r.MovimientoId;
                        return r.Turno;
                    };
                break;
            case TipoEnvioEleventa.CierreDeTurno when opciones.CorteEnEleventa:
                // El piloto: el corte se hace en eleventa y el puente no cierra su turno (la caja nueva toma ese corte,
                // TurnosDelPiloto). Un cierre en la bandeja (de antes del piloto, o de un corte Z que subió de la
                // contingencia) no se manda: queda por revisar, y lo que sigue pasa al turno de eleventa, que sigue abierto.
                // Mientras dure el piloto este puente siempre manda en el piloto (Escribir), así que el caso de abajo no
                // corre; se queda para cuando se deje de convivir con eleventa (lo prueba CorteEnElPilotoTests: el corte Z
                // lo sigue encolando fuera del piloto).
                porRevisar = CorteEnElPiloto.NoSeCierraEleventa;
                break;
            case TipoEnvioEleventa.CierreDeTurno:
                var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == envio.TurnoId, ct);
                var cerrado = HoraMexico.ALocal(turno.CerradoUtc ?? envio.CreadoUtc);
                // Lo que faltó o sobró al contar en el corte de la caja nueva.
                var diferencia = await db.Cortes.AsNoTracking().Where(x => x.TurnoId == turno.Id && x.Tipo == Nucleo.Turnos.TipoCorte.Z)
                    .Select(x => x.DiferenciaCentavos).FirstOrDefaultAsync(ct) ?? 0;
                escribir = async () =>
                {
                    envio.IdEleventa = (await EnvioEleventa.CerrarTurnoAsync(servidor, caja, cerrado, diferencia / 100m, ct)).TurnoId;
                    return null;
                };
                break;
        }

        if (escribir is null)
        {
            (envio.Estado, envio.Error) = (descartada ? EstadoEnvioEleventa.Descartada : EstadoEnvioEleventa.PorRevisar, porRevisar);
            await db.SaveChangesAsync(ct);
            if (!descartada)
                log.LogWarning("{Tipo} no pasa sola a eleventa: {Motivo}", envio.Tipo, porRevisar);
            return true;
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        envio.Intentos++;
        envio.UltimoIntentoUtc = ahora;
        // Hasta que quede anotado aquí lo que pasó, la lectura de eleventa no sabe si ya está allá.
        using var mandando = EnCurso.Mandando(envio.Id);
        TurnoDeEleventa? deEleventa = null;
        try
        {
            deEleventa = await escribir();
            (envio.Estado, envio.EnviadaUtc, envio.Error) = (EstadoEnvioEleventa.Enviada, reloj.GetUtcNow().UtcDateTime, null);
            (_ultimoEnvioUtc, _ultimoError, _sinRedHastaUtc) = (envio.EnviadaUtc, null, default);
        }
        catch (ChoqueConEleventa ex)
        {
            // Eleventa ya tiene otra cosa (su turno se cortó, hay dos abiertos, el ticket ya está cancelado…): no es la red y
            // reintentar no lo arregla. Queda por revisar, con el porqué, y lo demás de la bandeja sigue. No está allá.
            (envio.Estado, envio.Error) = (EstadoEnvioEleventa.PorRevisar, Recortar(ex.Message));
            EnCurso.Rechazado(envio.Id);
            // Eleventa sí contestó: el cobro puede volver a esperarlo.
            _sinRedHastaUtc = default;
            log.LogWarning("{Tipo} {Id} queda por revisar: {Motivo}", envio.Tipo, envio.Id, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            envio.Error = _ultimoError = Recortar(LectorEleventa.Explicar(ex, servidor));
            // Si no fue la red, eleventa contestó que no y deshizo lo que llevaba: no está allá, y la lectura ya lo puede
            // descontar. De lo que se cortó por la red (o a medias) no se sabe.
            if (EsDeRed(ex))
                _sinRedHastaUtc = ahora + SinRedPor;
            else if (ex is not OperationCanceledException)
                EnCurso.Rechazado(envio.Id);
            log.LogWarning(ex, "{Tipo} {Id} no entró a eleventa ({Servidor}); se vuelve a intentar.", envio.Tipo, envio.Id, servidor);
        }
        // Lo primero que manda un turno de la caja nueva le da su turno de eleventa (si la lectura no se lo dio antes). Si
        // falla, la lectura lo adopta después: lo que se mandó ya quedó.
        if (envio.Estado == EstadoEnvioEleventa.Enviada && deEleventa is not null)
        {
            try
            {
                if (await TurnoAquiAsync(db, envio, CancellationToken.None) is { } turnoAqui)
                    await TurnosDelPiloto.AdoptarAsync(db, turnoAqui, deEleventa, envio.EnviadaUtc!.Value, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "No se pudo anotar el turno de eleventa de {Tipo} {Id}.", envio.Tipo, envio.Id);
            }
        }
        // Aunque se esté apagando el servicio: si ya entró a eleventa, que quede anotado (si no, al volver lo encuentra la marca).
        await db.SaveChangesAsync(CancellationToken.None);
        return envio.Estado != EstadoEnvioEleventa.Pendiente;
    }

    /// <summary>El turno de la caja nueva de lo que se mandó: el de la venta, el de donde salió lo devuelto o el del movimiento.</summary>
    private static async Task<Guid?> TurnoAquiAsync(PosDbContext db, EnvioAEleventa envio, CancellationToken ct) => envio.Tipo switch
    {
        TipoEnvioEleventa.Venta => await db.Ventas.Where(v => v.Id == envio.VentaId).Select(v => (Guid?)v.TurnoId).FirstOrDefaultAsync(ct),
        TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion =>
            await db.Devoluciones.Where(d => d.Id == envio.DevolucionId).Select(d => (Guid?)d.TurnoId).FirstOrDefaultAsync(ct),
        TipoEnvioEleventa.MovimientoDeCaja => await db.MovimientosCaja.Where(m => m.Id == envio.MovimientoId).Select(m => (Guid?)m.TurnoId).FirstOrDefaultAsync(ct),
        _ => null,
    };

    private static string Recortar(string texto) => texto.Length > 500 ? texto[..500] : texto;

    /// <summary>La PC apagada, la red caída o lenta: no es de lo que se manda, es de la red.</summary>
    private static bool EsDeRed(Exception ex) =>
        ex is FbException { ErrorCode: 335544721 /* network_error */ or 335544726 /* net_read_err */ or 335544727 /* net_write_err */ }
            or TimeoutException or System.Net.Sockets.SocketException or IOException
        || ex.InnerException is { } interna && EsDeRed(interna);
}
