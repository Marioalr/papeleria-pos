using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
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
/// <para>
/// Los respaldos de eleventa: antes de lo primero que escribe cada día, el respaldo propio con gbak
/// (<see cref="RespaldoDeEleventa"/>); sin él, por omisión, no se escribe. Y si eleventa ya no tiene lo que le mandó la caja
/// 2 (se restauró un respaldo, <see cref="RestauracionDeEleventa"/>), el puente se detiene hasta que se reanude a mano
/// (<see cref="ReanudarAsync"/>).
/// </para>
/// </summary>
public sealed class EnvioVentasEleventa(
    IServiceScopeFactory alcances, OpcionesEleventa opciones, RespaldoDeEleventa respaldo, TimeProvider reloj, IHostApplicationLifetime vida,
    ILogger<EnvioVentasEleventa> log)
    : BackgroundService
{
    /// <summary>Después de una falla de red, cuánto tiempo el cobro ya no espera a eleventa (para no tardar en cada venta).</summary>
    public static readonly TimeSpan SinRedPor = TimeSpan.FromMinutes(1);

    /// <summary>Después de un respaldo de eleventa que no se pudo hacer, cuánto se espera para volver a intentarlo.</summary>
    public static readonly TimeSpan ReintentarRespaldo = TimeSpan.FromMinutes(5);

    private readonly (ServidorEleventa Servidor, CajaEnEleventa Caja)? _destino = opciones.ParaEscribir();
    private readonly SemaphoreSlim _uno = new(1, 1);
    private readonly SemaphoreSlim _ahora = new(0, 1);
    private DateTime _sinRedHastaUtc;
    private DateTime? _ultimoEnvioUtc;
    private string? _ultimoError;
    private volatile string? _turnoDeOtro;

    // El respaldo de eleventa del día: cuándo se intentó, por qué no se pudo y cuándo se vuelve a intentar.
    private DateOnly? _respaldoIntentado;
    private string? _respaldoError;
    private DateTime _respaldoDespuesUtc;
    private volatile bool _respaldando;
    private volatile bool _respaldarPronto;

    public bool Activo => _destino is not null;

    /// <summary>
    /// Solo del piloto: lo que se está mandando (o se anota a mano) y lo que eleventa rechazó, para que la lectura de
    /// eleventa no ajuste la existencia de lo que va y sí descuente lo que no entró.
    /// </summary>
    public EnviosEnCurso EnCurso { get; } = new();

    /// <summary>
    /// Pone la venta en la bandeja antes de cobrarla: se guarda en la misma transacción que la venta, así no hay venta
    /// cobrada que se quede sin mandar. Sus cancelaciones, devoluciones y el cierre de su turno se encolan solos, y las
    /// entradas y salidas, en <c>RegistroCaja</c> si esta caja manda a eleventa (<see cref="Activo"/>). Va a nombre de quien la
    /// cobra (<see cref="CajeroDeEleventa"/>), tomado ahora: lo que cambie después no le cambia el cajero en eleventa.
    /// </summary>
    public async Task EncolarAsync(PosDbContext db, Venta venta)
    {
        if (Activo)
            db.EnviosAEleventa.Add(new EnvioAEleventa
            {
                Tipo = TipoEnvioEleventa.Venta, VentaId = venta.Id, CreadoUtc = reloj.GetUtcNow().UtcDateTime,
                CajeroEleventa = await CajeroDeEleventa.DeAsync(db, venta.CajeroId),
            });
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

    /// <summary>
    /// El candado, si se libera pronto; vacío si no. Para revisar en vivo un ticket antes de devolverlo: la cajera tiene al
    /// cliente enfrente, y si la bandeja o la lectura van, se deja pasar.
    /// </summary>
    public async Task<IDisposable?> IntentarCandadoAsync(TimeSpan espera, CancellationToken ct = default) =>
        await _uno.WaitAsync(espera, ct) ? new Soltar(_uno) : null;

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
    /// lectura de eleventa lo sigue descontando de la existencia. Lo deja quien entró con «Configuración de la tienda»,
    /// como anotarlo a mano, y queda en la bitácora.
    /// <para>
    /// Con el candado de la bandeja (que no se esté mandando en ese momento) y, si ya se intentó mandar, buscándolo antes en
    /// eleventa por su marca (cuarta revisión, 2026-10-17): un intento que se cortó pudo dejarlo allá aunque aquí no se
    /// supo, y si se anotara a mano quedaría dos veces. Si está, queda como enviado; sin eleventa no se sabe, y no se deja.
    /// </para>
    /// </summary>
    public async Task<bool> DejarPorRevisarAsync(PosDbContext db, Guid id, SolicitudRevision s)
    {
        if (await db.EnviosAEleventa.FindAsync(id) is not { Estado: EstadoEnvioEleventa.Pendiente } envio)
            return false;
        var control = await ConPermisoAsync(db, envio, s);
        using var candado = await IntentarCandadoAsync(opciones.EsperaMaxima * 2) ?? throw new InvalidOperationException(BandejaMandando);
        await db.Entry(envio).ReloadAsync();
        if (envio.Estado != EstadoEnvioEleventa.Pendiente)
            return false;
        var dejado = (await EstadoAsync(db)).PorAtender.FirstOrDefault(e => e.Id == id);
        YaEnEleventa? yaEstaba = null;
        if (envio.Intentos > 0)
        {
            try
            {
                yaEstaba = await YaEstaEnEleventaAsync(db, envio);
            }
            catch (Exception ex) when (EsDeRed(ex) || ex is FbException)
            {
                throw new InvalidOperationException($"{LectorEleventa.Explicar(ex, _destino!.Value.Servidor)} {SinEleventaNoSeDeja}");
            }
        }
        if (yaEstaba is { } ya)
        {
            (envio.Estado, envio.EnviadaUtc, envio.Error) = (EstadoEnvioEleventa.Enviada, reloj.GetUtcNow().UtcDateTime, null);
            (envio.TicketEleventa, envio.FolioEleventa, envio.IdEleventa) =
                (envio.TicketEleventa ?? ya.TicketEleventa, envio.FolioEleventa ?? ya.FolioEleventa, envio.IdEleventa ?? ya.IdEleventa);
            control.Anotar($"Iba a dejar por revisar {dejado?.Descripcion ?? envio.Tipo.ToString()}, pero ya estaba en eleventa: quedó como enviado",
                dejado?.TotalCentavos);
        }
        else
        {
            envio.Estado = EstadoEnvioEleventa.PorRevisar;
            envio.Error = Recortar($"Se dejó para revisar a mano. {envio.Error}".Trim());
            (envio.PorRevisarPorque, envio.PorRevisarUtc) = (MotivoPorRevisar.AnotarAMano, reloj.GetUtcNow().UtcDateTime);
            control.Anotar($"Dejó por revisar lo que no pasa solo a eleventa: {dejado?.Descripcion ?? envio.Tipo.ToString()}", dejado?.TotalCentavos);
        }
        await db.SaveChangesAsync();
        Despertar();
        return true;
    }

    public const string BandejaMandando = "La bandeja está mandando a eleventa en este momento: vuelve a intentarlo en un momento.";

    public const string SinEleventaNoSeDeja = "Sin eleventa no se sabe si ya llegó (un intento pudo dejarlo allá): déjalo por revisar cuando conteste.";

    /// <summary>Lo que ya dejó en eleventa un intento de algo de la bandeja: su ticket y folio (una venta) o su número (lo demás).</summary>
    private sealed record YaEnEleventa(int? TicketEleventa, int? FolioEleventa, int? IdEleventa);

    /// <summary>
    /// Si algo de la bandeja ya está en eleventa aunque aquí no se supo (un intento que se cortó después de que eleventa lo
    /// guardó), por su marca, en vivo y sin escribir nada. Vacío si no está; error si eleventa no contesta a tiempo.
    /// </summary>
    private async Task<YaEnEleventa?> YaEstaEnEleventaAsync(PosDbContext db, EnvioAEleventa envio)
    {
        var (servidor, caja) = _destino ?? throw new InvalidOperationException("Esta caja no manda a eleventa.");
        var nunca = FirebirdSinCancelar.Nunca;
        switch (envio.Tipo)
        {
            case TipoEnvioEleventa.Venta:
                var creada = await db.Ventas.AsNoTracking().Where(v => v.Id == envio.VentaId).Select(v => v.CreadaUtc).SingleAsync();
                return await EnvioEleventa.BuscarVentaAsync(servidor, caja, HoraMexico.ALocal(creada), nunca).WaitAsync(opciones.EsperaMaxima) is { } venta
                    ? new YaEnEleventa(venta.TicketId, venta.Folio, null)
                    : null;
            case TipoEnvioEleventa.Devolucion or TipoEnvioEleventa.Cancelacion:
                // Si su venta no está allá, tampoco su devolución. Después de restaurar un respaldo del POS, la venta pudo regresar
                // pendiente aunque ya esté allá: hasta que el puente la vuelva a mandar (y la encuentre por su marca), su devolución no
                // se busca, y «dejar por revisar» la pide anotar a mano. Del lado seguro: la advertencia al restaurar en el piloto dice
                // que se revise en eleventa antes de anotarla. Revisión del PR #136: se deja así, dicho.
                if (await db.EnviosAEleventa.AsNoTracking()
                        .Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == envio.VentaId && e.Estado == EstadoEnvioEleventa.Enviada && e.TicketEleventa != null)
                        .Select(e => e.TicketEleventa).FirstOrDefaultAsync() is not { } ticket)
                    return null;
                var hecha = await db.Devoluciones.AsNoTracking().Where(d => d.Id == envio.DevolucionId).Select(d => d.FechaUtc).SingleAsync();
                return await EnvioEleventa.BuscarDevolucionAsync(servidor, ticket, HoraMexico.ALocal(hecha), nunca).WaitAsync(opciones.EsperaMaxima) is { } devolucion
                    ? new YaEnEleventa(null, null, devolucion)
                    : null;
            case TipoEnvioEleventa.DevolucionDeEleventa:
                var ida = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => d.Id == envio.DevolucionDeEleventaId)
                    .Select(d => new { d.TicketEleventa, d.FechaUtc }).SingleAsync();
                return await EnvioEleventa.BuscarDevolucionAsync(servidor, ida.TicketEleventa, HoraMexico.ALocal(ida.FechaUtc), nunca).WaitAsync(opciones.EsperaMaxima)
                    is { } deLaIda
                    ? new YaEnEleventa(null, null, deLaIda)
                    : null;
            case TipoEnvioEleventa.MovimientoDeCaja:
                var movimiento = await db.MovimientosCaja.AsNoTracking().Where(m => m.Id == envio.MovimientoId)
                    .Select(m => new { m.Tipo, m.FechaUtc }).SingleAsync();
                return await EnvioEleventa.BuscarMovimientoAsync(servidor, caja, movimiento.Tipo == Nucleo.Turnos.TipoMovimientoCaja.Entrada,
                    HoraMexico.ALocal(movimiento.FechaUtc), nunca).WaitAsync(opciones.EsperaMaxima) is { } enCaja
                    ? new YaEnEleventa(null, null, enCaja)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Lo que ya quedó en eleventa a mano (alguien lo registró allá, o allá ya estaba), pendiente o por revisar: ya no se
    /// manda, lo que sigue en la bandeja puede pasar, y la lectura de eleventa ya no lo descuenta de la existencia. Lo anota
    /// quien entró con «Configuración de la tienda» (como «Ya lo revisé» y reanudar el puente), y queda en la bitácora.
    /// <para>
    /// Una devolución de aquí de un ticket que está en eleventa dice además cuál es su anotación allá (revisión del 2026-10-17,
    /// decidido): una o varias de las devoluciones de eleventa de ese ticket, leídas en vivo, que juntas devuelven lo mismo.
    /// Se ligan y no se pasan a la venta; si ninguna es, todavía no se puede marcar. Sin eleventa, tampoco.
    /// </para>
    /// </summary>
    public async Task<bool> AnotarAManoAsync(PosDbContext db, Guid id, SolicitudAnotadoAMano s)
    {
        if (await db.EnviosAEleventa.FindAsync(id) is not { Estado: EstadoEnvioEleventa.Pendiente or EstadoEnvioEleventa.PorRevisar } envio)
            return false;
        var control = await ConPermisoAsync(db, envio, new SolicitudRevision(s.UsuarioId, s.Nota));
        // Cómo se ve en la bandeja («Venta B-000123», «Salida de efectivo: …»), para la bitácora.
        var anotado = (await EstadoAsync(db)).PorAtender.FirstOrDefault(e => e.Id == id);
        var anotacion = "";
        if (await DevolucionesConEleventa.SeMandaSolaAsync(db, envio))
            throw new InvalidOperationException(DevolucionesConEleventa.PrimeroPorRevisar);
        if (await DevolucionesConEleventa.PorAnotarAsync(db, envio) is { } porAnotar)
        {
            var leidas = await DevolucionesConEleventa.AnotacionesEnVivoAsync(db, Servidor(), porAnotar)
                ?? throw new InvalidOperationException(DevolucionesConEleventa.NoContestaParaElegir);
            // Llegó sola a eleventa (allá está con su marca): queda como enviada, no hay nada que anotar.
            if (leidas.Suya is { } suya)
            {
                await DevolucionesConEleventa.DarPorEnviadaAsync(db, envio.DevolucionId!.Value, suya.Id, reloj.GetUtcNow().UtcDateTime);
                control.Anotar($"Iba a marcar como anotado a mano {anotado?.Descripcion ?? envio.Tipo.ToString()}, pero llegó sola a eleventa: quedó como enviado",
                    anotado?.TotalCentavos);
                await db.SaveChangesAsync();
                Despertar();
                return true;
            }
            if (s.Anotaciones is not { Count: > 0 } elegidas)
                throw new InvalidOperationException(DevolucionesConEleventa.DiCualEs);
            var tomadas = leidas.Candidatas.Where(l => elegidas.Contains(l.Id)).ToList();
            if (tomadas.Count != elegidas.Distinct().Count())
                throw new InvalidOperationException(DevolucionesConEleventa.YaNoSeElige);
            if (await DevolucionesConEleventa.PorQueNoEsAsync(db, porAnotar, tomadas) is { } porque)
                throw new InvalidOperationException(porque);
            DevolucionesConEleventa.Ligar(db, porAnotar, tomadas, reloj.GetUtcNow().UtcDateTime);
            anotacion = $" (en eleventa: {string.Join(", ", tomadas.Select(t => $"{(t.EsCancelacion ? "cancelación" : "devolución")} #{t.Id} del {t.DevueltoEn:dd/MM HH:mm}"))})";
        }
        envio.Estado = EstadoEnvioEleventa.AMano;
        envio.Error = Recortar($"Se anotó a mano en eleventa{anotacion}. {envio.Error}".Trim());
        control.Anotar($"Anotó a mano en eleventa: {anotado?.Descripcion ?? envio.Tipo.ToString()}{anotacion}", anotado?.TotalCentavos);
        // Antes de guardarlo: una lectura de eleventa a medias no sabe si lo leyó antes o después de que se registrara allá.
        EnCurso.AnotadoAMano(envio.Id);
        await db.SaveChangesAsync();
        if (anotacion.Length > 0)
            await PasarLoRetenidoAsync(db, envio.VentaId!.Value);
        Despertar();
        return true;
    }

    /// <summary>
    /// Ya no espera nada de ese ticket (se eligió su anotación o se resolvió a mano): lo que eleventa le devolvió y la vuelta
    /// retenía se pasa ya a la venta, leído en vivo y con el candado de la bandeja, sin esperar la siguiente lectura (que,
    /// en un ticket de hace más de 31 días, ya no lo leería). Si eleventa no contesta o la bandeja está ocupada, lo pasa la
    /// siguiente lectura.
    /// </summary>
    private async Task PasarLoRetenidoAsync(PosDbContext db, Guid ventaId)
    {
        if (_destino is not { } destino)
            return;
        using var candado = await IntentarCandadoAsync(DevolucionesConEleventa.EsperaEnVivo);
        if (candado is null)
            return;
        try
        {
            await DevolucionesConEleventa.RevisarEnVivoAsync(db, destino.Servidor, ventaId, reloj.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "No se pudo pasar lo que eleventa devolvió de la venta {Venta}: lo pasa la siguiente lectura.", ventaId);
        }
    }

    /// <summary>
    /// «Resolverlo a mano»: la salida cuando la anotación a mano de una devolución de aquí no cuadra con nada que se pueda
    /// elegir (dos de aquí anotadas en una sola allá, más o menos artículos, una cancelación anotada como devolución…; cuarta
    /// revisión, 2026-10-17). Quien tiene «Configuración de la tienda» dice que ya lo resolvió en eleventa: la de aquí queda
    /// anotada a mano sin ligarse con nada, y lo que eleventa devolvió de ese ticket deja de esperar y se pasa a la venta como
    /// cualquier otra devolución de allá (o queda por revisar, si aquí ya no alcanza). Desde ahí la existencia sigue a
    /// eleventa. Queda en la bitácora, con su nota.
    /// </summary>
    public async Task<bool> ResolverAManoAsync(PosDbContext db, Guid id, SolicitudRevision s)
    {
        if (await db.EnviosAEleventa.FindAsync(id) is not { } envio || await DevolucionesConEleventa.PorAnotarAsync(db, envio) is null)
            return false;
        var control = await ConPermisoAsync(db, envio, s);
        var resuelto = (await EstadoAsync(db)).PorAtender.FirstOrDefault(e => e.Id == id);
        envio.Estado = EstadoEnvioEleventa.AMano;
        envio.Error = Recortar($"{DevolucionesConEleventa.ResueltaAMano} {envio.Error}".Trim());
        control.Anotar($"Resolvió a mano en eleventa, sin elegir su anotación: {resuelto?.Descripcion ?? envio.Tipo.ToString()}", resuelto?.TotalCentavos);
        EnCurso.AnotadoAMano(envio.Id);
        await db.SaveChangesAsync();
        await PasarLoRetenidoAsync(db, envio.VentaId!.Value);
        Despertar();
        return true;
    }

    /// <summary>
    /// Las devoluciones de eleventa que pueden ser la anotación a mano de una devolución de aquí, leídas en vivo, para elegir
    /// la suya al marcarla «Ya lo anoté en eleventa». Vacía si todavía no hay ninguna; error si eso no se marca eligiendo o si
    /// eleventa no contesta.
    /// </summary>
    public async Task<List<AnotacionEnEleventaDto>> AnotacionesAsync(PosDbContext db, Guid id)
    {
        var envio = await db.EnviosAEleventa.FindAsync(id) ?? throw new InvalidOperationException("Eso ya no está en la bandeja.");
        var porAnotar = await DevolucionesConEleventa.PorAnotarAsync(db, envio) ?? throw new InvalidOperationException(DevolucionesConEleventa.NoSeElige);
        var leidas = await DevolucionesConEleventa.AnotacionesEnVivoAsync(db, Servidor(), porAnotar)
            ?? throw new InvalidOperationException(DevolucionesConEleventa.NoContestaParaElegir);
        // Llegó sola a eleventa (allá está con su marca): se corrige aquí (queda como enviada) y no hay nada que elegir.
        if (leidas.Suya is { } suya)
        {
            await DevolucionesConEleventa.DarPorEnviadaAsync(db, porAnotar.Envio.DevolucionId!.Value, suya.Id, reloj.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync();
            throw new InvalidOperationException(DevolucionesConEleventa.LlegoSola);
        }
        return leidas.Candidatas.Select(DevolucionesConEleventa.ComoAnotacion).ToList();
    }

    private ServidorEleventa Servidor() => _destino?.Servidor ?? throw new InvalidOperationException("Esta caja no manda a eleventa.");

    /// <summary>
    /// Lo que se hace a mano con algo de la bandeja (dejarlo por revisar, anotarlo a mano) lo hace quien entró con
    /// «Configuración de la tienda», en la sucursal y la caja de eso: si no lo tiene, queda en la bitácora como intento
    /// sin permiso y no se hace. Regresa con qué anotar en la bitácora lo que sí se hizo.
    /// </summary>
    private static async Task<HechoAMano> ConPermisoAsync(PosDbContext db, EnvioAEleventa envio, SolicitudRevision s)
    {
        var (sucursalId, cajaId) = await DondeAsync(db, envio);
        var control = new ServicioControl(db);
        await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, cajaId, AccionControlada.ConfigurarTienda);
        return new HechoAMano(control, sucursalId, cajaId, envio.VentaId, s);
    }

    /// <summary>Lo que se hizo a mano con algo de la bandeja, en la bitácora: quién, dónde, de qué venta, cuánto y su nota.</summary>
    private sealed class HechoAMano(ServicioControl control, Guid sucursalId, Guid? cajaId, Guid? ventaId, SolicitudRevision s)
    {
        public void Anotar(string que, long? monto) =>
            control.Registrar(sucursalId, cajaId, s.UsuarioId, AccionControlada.ConfigurarTienda,
                que + (string.IsNullOrWhiteSpace(s.Nota) ? "" : $". {s.Nota.Trim()}"), monto, ventaId);
    }

    /// <summary>
    /// La sucursal y la caja de lo que va en la bandeja (las de su venta, su devolución de un ticket de eleventa, su entrada
    /// o salida, o su turno): ahí se revisa el permiso y ahí va la bitácora.
    /// </summary>
    private static async Task<(Guid Sucursal, Guid? Caja)> DondeAsync(PosDbContext db, EnvioAEleventa envio)
    {
        if (envio.VentaId is { } ventaId
            && await db.Ventas.AsNoTracking().Where(v => v.Id == ventaId).Select(v => new { v.SucursalId, v.CajaId }).FirstOrDefaultAsync() is { } venta)
            return (venta.SucursalId, venta.CajaId);
        if (envio.DevolucionDeEleventaId is { } idaId
            && await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => d.Id == idaId).Select(d => new { d.SucursalId, d.CajaId }).FirstOrDefaultAsync() is { } ida)
            return (ida.SucursalId, ida.CajaId);
        var turnoId = envio.TurnoId ?? (envio.MovimientoId is { } movimientoId
            ? await db.MovimientosCaja.AsNoTracking().Where(m => m.Id == movimientoId).Select(m => (Guid?)m.TurnoId).FirstOrDefaultAsync()
            : null);
        return turnoId is { } t && await db.Turnos.AsNoTracking().Where(x => x.Id == t).Select(x => new { x.SucursalId, x.CajaId }).FirstOrDefaultAsync() is { } turno
            ? (turno.SucursalId, turno.CajaId)
            : throw new InvalidOperationException("No se sabe de qué caja es eso.");
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
        var deEleventaIds = porAtender.Where(e => e.DevolucionDeEleventaId is not null).Select(e => e.DevolucionDeEleventaId!.Value).ToList();
        var deEleventa = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => deEleventaIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Folio, d.CajaNombre, d.FechaUtc, d.ReembolsoEfectivoCentavos }).ToDictionaryAsync(d => d.Id);
        var conteo = await db.EnviosAEleventa.AsNoTracking().GroupBy(e => e.Estado).Select(g => new { g.Key, Cuantos = g.Count() }).ToListAsync();

        EnvioEleventaDto Dto(EnvioAEleventa e)
        {
            var venta = e.VentaId is { } v && ventas.TryGetValue(v, out var x) ? x : null;
            var devolucion = e.DevolucionId is { } d && devoluciones.TryGetValue(d, out var y) ? y : null;
            var movimiento = e.MovimientoId is { } m && movimientos.TryGetValue(m, out var z) ? z : null;
            var ida = e.DevolucionDeEleventaId is { } i && deEleventa.TryGetValue(i, out var w) ? w : null;
            var folio = venta is null ? "" : $"{venta.Serie}-{venta.Folio:000000}";
            var (descripcion, fecha, total) = e.Tipo switch
            {
                TipoEnvioEleventa.Venta => ($"Venta {folio}", venta?.PagadaUtc ?? e.CreadoUtc, venta?.Total ?? 0),
                TipoEnvioEleventa.Cancelacion => ($"Cancelación de {folio}", devolucion?.FechaUtc ?? e.CreadoUtc, devolucion?.ReembolsoEfectivoCentavos ?? 0),
                TipoEnvioEleventa.Devolucion => ($"Devolución de {folio}", devolucion?.FechaUtc ?? e.CreadoUtc, devolucion?.ReembolsoEfectivoCentavos ?? 0),
                TipoEnvioEleventa.MovimientoDeCaja => (
                    $"{(movimiento?.Tipo == Nucleo.Turnos.TipoMovimientoCaja.Salida ? "Salida" : "Entrada")} de efectivo: {movimiento?.Motivo}",
                    movimiento?.FechaUtc ?? e.CreadoUtc, movimiento?.MontoCentavos ?? 0),
                TipoEnvioEleventa.DevolucionDeEleventa => (
                    $"Devolución del ticket #{ida?.Folio} de eleventa ({ida?.CajaNombre})", ida?.FechaUtc ?? e.CreadoUtc, ida?.ReembolsoEfectivoCentavos ?? 0),
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
            // Sin red hace poco: la vuelta lo vuelve a intentar; aquí no se espera otra vez a que no conteste. Con el puente
            // detenido (eleventa se restauró), sus números ya no dicen qué es de quién.
            if (ahora < _sinRedHastaUtc || await RestauracionDeEleventa.DetencionAsync(db, ct) is not null
                || await FoliosRepetidos.PorRevisarAsync(db, ahora, ct) is not { Count: > 0 } enviadas)
                return true;
            // Con un límite: una llamada colgada no detiene la bandeja (es como si no hubiera red). Se deja de esperar, sin
            // cancelarla: con Firebird 2.0 cancelar truena el servicio (FirebirdSinCancelar).
            try
            {
                if (await RevisarConEleventaAsync().WaitAsync(opciones.EsperaMaxima, ct) is > 0 and var repetidos)
                    log.LogWarning("{Repetidos} ventas de esta caja tienen en eleventa el mismo folio que otra caja: sus reimpresiones salen con el folio de la caja.",
                        repetidos);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when ((ex is not OperationCanceledException || !ct.IsCancellationRequested) && EsDeRed(ex))
            {
                // Sin red: se revisa en otra vuelta, y el cobro tampoco espera a eleventa un rato.
                _sinRedHastaUtc = ahora + SinRedPor;
            }
            return true;

            async Task<int> RevisarConEleventaAsync()
            {
                await using var conexion = new FbConnection(servidor.Cadena());
                await conexion.OpenAsync(FirebirdSinCancelar.Nunca);
                return await FoliosRepetidos.RevisarAsync(db, conexion, enviadas, ahora, FirebirdSinCancelar.Nunca);
            }
        }, ct);
    }

    /// <summary>
    /// En el orden en que pasaron; en cuanto algo no entra, lo demás espera a la siguiente vuelta. Antes de lo primero del
    /// día, el respaldo de eleventa; con el puente detenido (eleventa se restauró), nada.
    /// </summary>
    private async Task VueltaAsync(CancellationToken ct)
    {
        List<Guid> pendientes;
        using (var alcance = alcances.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (await RestauracionDeEleventa.DetencionAsync(db, ct) is not null)
                return;
            pendientes = await db.EnviosAEleventa.AsNoTracking()
                .Where(e => e.Estado == EstadoEnvioEleventa.Pendiente).OrderBy(e => e.CreadoUtc).Select(e => e.Id).ToListAsync(ct);
        }
        var hoy = Hoy();
        // El respaldo del día: antes de escribir (o en cuanto la lectura ve que eleventa contesta), y después de una falla,
        // cada pocos minutos. Sin la ruta de gbak no hay con qué: si es obligatorio, no se escribe (la pantalla lo dice).
        if ((pendientes.Count > 0 || _respaldarPronto) && respaldo.Configurado && !respaldo.HayDelDia(hoy)
            && reloj.GetUtcNow().UtcDateTime >= _respaldoDespuesUtc)
            await RespaldarAsync(hoy, ct);
        _respaldarPronto = false;
        if (!PuedeEscribir(hoy))
            return;
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
            // Eleventa se restauró (no se escribe hasta reanudar) o falta el respaldo del día (lo hace la vuelta, que después
            // la manda): el ticket sale con el folio de la caja.
            if (await RestauracionDeEleventa.DetencionAsync(db, ct) is not null)
                return true;
            if (!PuedeEscribir(Hoy()))
            {
                Despertar();
                return true;
            }
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
        // La escritura va sin cancelación: con Firebird 2.0 cancelar truena el servicio (FirebirdSinCancelar). Si tarda más de
        // la cuenta se deja de esperar, y lo que alcance a guardar lo encuentra la marca en el siguiente intento.
        var escritura = FirebirdSinCancelar.Nunca;
        // Si este intento ya se dio por perdido: lo que se dejó de esperar no empieza a escribir si conecta tarde, y lo que
        // llega a confirmar tarde se deshace (revisión del 2026-10-17): si no, entraría cuando aquí ya se decidió sin ella.
        var perdido = 0;
        // Lo último que escribió el puente: si eleventa ya no lo tiene (se restauró un respaldo), no se escribe nada.
        caja = caja with
        {
            Huella = await RestauracionDeEleventa.HuellaAsync(db, reloj.GetUtcNow().UtcDateTime, RestauracionDeEleventa.AlEscribir, ct),
            SigueEsperando = () => Volatile.Read(ref perdido) == 0,
        };
        // Escribe en eleventa y dice en qué turno de eleventa quedó (vacío si ya estaba): el turno de la caja nueva lo adopta.
        Func<Task<TurnoDeEleventa?>>? escribir = null;
        string? porRevisar = null;
        var descartada = false;
        // A nombre de quien lo hizo, tomado al ponerlo en la bandeja (2026-10-25); vacío: el cajero de la configuración. Lo que
        // escribe este intento (no si ya estaba) y de quién es su turno de eleventa, para decirlo en el registro.
        var cajero = envio.CajeroEleventa;
        var escrito = false;
        int? cajeroDelTurno = null;
        // Una venta cancelada aquí que, al buscarla en eleventa, no estaba: se descarta.
        var noLlego = false;
        // Si este intento solo busca en eleventa la venta que se canceló aquí: lo que le pase no dice nada del intento anterior.
        var buscando = false;
        switch (envio.Tipo)
        {
            case TipoEnvioEleventa.Venta:
                var venta = await VentasParaEleventa.ArmarAsync(db, envio.VentaId!.Value, ct, opciones.ProductoImpresiones);
                (porRevisar, descartada) = (venta.PorRevisar, venta.Descartada);
                if (venta.Descartada && envio.Intentos > 0)
                {
                    // Se canceló aquí después de algún intento de mandarla: cualquiera pudo dejarla en eleventa (se cortó por
                    // tiempo o por la red después de que eleventa la guardó), aunque el último ni se haya conectado (revisiones
                    // del 2026-10-17). Antes de descartarla se busca allá por su marca; si eleventa no contesta, no se sabe y
                    // espera. Si está, queda como enviada y su cancelación va detrás, como la de cualquier venta que ya llegó;
                    // descartada, eleventa la seguiría teniendo como vendida y la lectura no lo sabría.
                    var creada = HoraMexico.ALocal(await db.Ventas.AsNoTracking().Where(v => v.Id == envio.VentaId).Select(v => v.CreadaUtc).SingleAsync(ct));
                    buscando = true;
                    escribir = async () =>
                    {
                        if (await EnvioEleventa.BuscarVentaAsync(servidor, caja, creada, escritura) is { } yaEstaba)
                            (envio.TicketEleventa, envio.FolioEleventa) = (yaEstaba.TicketId, yaEstaba.Folio);
                        else
                            noLlego = true;
                        return null;
                    };
                }
                else if (venta.Venta is { } paraEleventa)
                    escribir = async () =>
                    {
                        await using var conexion = new FbConnection(servidor.Cadena());
                        await conexion.OpenAsync(escritura);
                        if (Volatile.Read(ref perdido) == 1)
                            return null;
                        var r = await EnvioEleventa.MandarAsync(conexion, caja, paraEleventa with { Cajero = cajero }, escritura);
                        (escrito, cajeroDelTurno) = (!r.YaEstaba, r.CajeroDelTurno);
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
                var devolucion = await VentasParaEleventa.ArmarDevolucionAsync(db, envio.DevolucionId!.Value, ct, opciones.ProductoImpresiones);
                (porRevisar, descartada) = (devolucion.PorRevisar, devolucion.Descartada);
                if (devolucion.Devolucion is { } paraDevolver)
                    escribir = async () =>
                    {
                        var r = await EnvioEleventa.DevolverAsync(servidor, caja, paraDevolver with { Cajero = cajero }, escritura);
                        (escrito, cajeroDelTurno) = (!r.YaEstaba, r.CajeroDelTurno);
                        envio.IdEleventa = r.DevolucionId;
                        return r.Turno;
                    };
                break;
            case TipoEnvioEleventa.DevolucionDeEleventa:
                // La ida: la devolución aquí de un ticket que se cobró en eleventa, escrita como la escribe eleventa.
                var ida = await VentasParaEleventa.ArmarDevolucionDeEleventaAsync(db, envio.DevolucionDeEleventaId!.Value, ct);
                if (ida.Devolucion is { } paraDevolverAlla)
                    escribir = async () =>
                    {
                        var r = await EnvioEleventa.DevolverAsync(servidor, caja, paraDevolverAlla with { Cajero = cajero }, escritura);
                        (escrito, cajeroDelTurno) = (!r.YaEstaba, r.CajeroDelTurno);
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
                        var r = await EnvioEleventa.MoverEfectivoAsync(servidor, caja, paraMover with { Cajero = cajero }, escritura);
                        (escrito, cajeroDelTurno) = (!r.YaEstaba, r.CajeroDelTurno);
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
                    envio.IdEleventa = (await EnvioEleventa.CerrarTurnoAsync(servidor, caja, cerrado, diferencia / 100m, escritura)).TurnoId;
                    return null;
                };
                break;
        }

        if (escribir is null)
        {
            (envio.Estado, envio.Error) = (descartada ? EstadoEnvioEleventa.Descartada : EstadoEnvioEleventa.PorRevisar, porRevisar);
            if (!descartada)
                (envio.PorRevisarPorque, envio.PorRevisarUtc) = (MotivoPorRevisar.Otro, reloj.GetUtcNow().UtcDateTime);
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
            // Con un límite: una llamada que se queda colgada se deja de esperar y se toma como falla de red (se reintenta y no
            // detiene la bandeja). Si sigue y llega a confirmar, se deshace (SigueEsperando); si ya había confirmado, la marca la
            // encuentra en el siguiente intento.
            deEleventa = await escribir().WaitAsync(opciones.EsperaMaxima, ct);
            if (noLlego)
                (envio.Estado, envio.Error) = (EstadoEnvioEleventa.Descartada, porRevisar);
            else
            {
                (envio.Estado, envio.EnviadaUtc, envio.Error) = (EstadoEnvioEleventa.Enviada, reloj.GetUtcNow().UtcDateTime, null);
                _ultimoEnvioUtc = envio.EnviadaUtc;
                if (descartada)
                    log.LogWarning("La venta {Venta} se canceló aquí y sí había llegado a eleventa (ticket {Ticket}): se manda su cancelación.",
                        envio.VentaId, envio.TicketEleventa);
            }
            (_ultimoError, _sinRedHastaUtc) = (null, default);
        }
        catch (ChoqueConEleventa ex)
        {
            // Eleventa ya tiene otra cosa (su turno se cortó, hay dos abiertos, el ticket ya está cancelado…): no es la red y
            // reintentar no lo arregla. Queda por revisar, con el porqué, y lo demás de la bandeja sigue. No está allá.
            (envio.Estado, envio.Error) = (EstadoEnvioEleventa.PorRevisar, Recortar(ex.Message));
            // Si se anota a mano allá o si eleventa ya lo tenía: la vuelta no toma lo de eleventa por su anotación a mano.
            (envio.PorRevisarPorque, envio.PorRevisarUtc) = (ex.Motivo, reloj.GetUtcNow().UtcDateTime);
            EnCurso.Rechazado(envio.Id);
            // Eleventa sí contestó: el cobro puede volver a esperarlo.
            _sinRedHastaUtc = default;
            log.LogWarning("{Tipo} {Id} queda por revisar: {Motivo}", envio.Tipo, envio.Id, ex.Message);
        }
        catch (EleventaRestaurada ex)
        {
            // Eleventa ya no tiene lo que le mandó la caja 2: no se escribió nada. El puente se detiene (no escribe ni aplica la
            // lectura) hasta que se reanude a mano; esto se queda pendiente.
            envio.Error = Recortar(ex.Message);
            EnCurso.Rechazado(envio.Id);
            _sinRedHastaUtc = default;
            await DetenerAsync(db, ex.Restauracion, CancellationToken.None);
            log.LogError("El puente con eleventa se detuvo: {Motivo}", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Se apaga el servicio: lo que siga en camino ya no se confirma.
            Volatile.Write(ref perdido, 1);
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref perdido, 1);
            envio.Error = _ultimoError = Recortar(LectorEleventa.Explicar(ex, servidor));
            // Si no fue la red, eleventa contestó que no y deshizo lo que llevaba: no está allá, y la lectura ya lo puede
            // descontar. De lo que se cortó por la red o por tiempo (o a medias) no se sabe. Tampoco de un choque de candados:
            // otra escritura tenía esos renglones, quizá la de un intento anterior que se cortó por tiempo y sigue abierta
            // (revisión del 2026-10-17); se reintenta sin darlo por rechazado.
            if (EsDeRed(ex))
                _sinRedHastaUtc = ahora + SinRedPor;
            else if (!EsChoqueDeCandados(ex) && !buscando)
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
        if (envio.Estado == EstadoEnvioEleventa.Enviada && escrito)
        {
            try
            {
                await DecirAQuienAsync(db, envio, caja.CajeroId, cajeroDelTurno);
            }
            catch (Exception ex)
            {
                // Solo es lo que se dice en el registro: no detiene que quede anotado lo que se mandó.
                log.LogWarning(ex, "No se pudo decir a nombre de quién quedó {Tipo} {Id} en eleventa.", envio.Tipo, envio.Id);
            }
        }
        // Aunque se esté apagando el servicio: si ya entró a eleventa, que quede anotado (si no, al volver lo encuentra la marca).
        await db.SaveChangesAsync(CancellationToken.None);
        return envio.Estado != EstadoEnvioEleventa.Pendiente;
    }

    /// <summary>
    /// A nombre de quién quedó en eleventa lo que se acaba de escribir (el piloto alfa, 2026-10-25), en el registro del servicio,
    /// cuando hay algo que decir: si quien lo hizo no viene de eleventa, quedó a nombre del cajero de la configuración; y si el
    /// turno de eleventa donde quedó es de otro cajero. Eleventa nunca lo hace (base del 2026-10-24: al entrar otro cajero cierra
    /// el turno y abre otro): pasa si eleventa está abierto en la caja 2 con otro usuario que el de quien vende aquí. Se escribe
    /// de todos modos a nombre de quien lo hizo (decidido, por revisar), y queda dicho para revisarlo.
    /// </summary>
    private async Task DecirAQuienAsync(PosDbContext db, EnvioAEleventa envio, int cajeroDeLaCaja, int? cajeroDelTurno)
    {
        // Eleventa no anota quién hizo una entrada o salida (CORTE_MOVIMIENTOS no tiene usuario: va con el cajero de su turno).
        if (envio.Tipo == TipoEnvioEleventa.MovimientoDeCaja)
            return;
        var usado = envio.CajeroEleventa ?? cajeroDeLaCaja;
        var deOtroTurno = cajeroDelTurno is { } delTurno && delTurno != usado;
        // Lo último quedó en el turno de quien lo hizo: ya no hay nada que decir en la pantalla.
        if (cajeroDelTurno is not null && !deOtroTurno)
            _turnoDeOtro = null;
        if (envio.CajeroEleventa is not null && !deOtroTurno)
            return;
        var quien = await QuienLoHizoAsync(db, envio) ?? "—";
        if (envio.CajeroEleventa is null)
            log.LogWarning("{Tipo} {Id} quedó en eleventa a nombre del cajero de la configuración (Eleventa:Cajero, USUARIOS.ID {Cajero}): {Quien} no viene de eleventa.",
                envio.Tipo, envio.Id, usado, quien);
        if (deOtroTurno)
        {
            log.LogWarning("{Tipo} {Id} de {Quien} quedó en eleventa a nombre del cajero {Cajero} en un turno del cajero {DelTurno}: eleventa no lo hace "
                + "(al entrar otro cajero cierra el turno y abre otro). Revisa con qué usuario está abierto eleventa en la caja 2.",
                envio.Tipo, envio.Id, quien, usado, cajeroDelTurno);
            // También en la pantalla de la caja 2 (el chip «Eleventa»), que es lo que se ve en la tienda: el registro nadie lo lee ahí.
            var delTurnoAqui = await db.Usuarios.Where(u => u.EleventaId == cajeroDelTurno).Select(u => u.Nombre).FirstOrDefaultAsync();
            var deQuien = delTurnoAqui is { } nombre ? $"de {nombre}" : $"del usuario {cajeroDelTurno} de eleventa";
            _turnoDeOtro = $"{HoraMexico.ALocal(reloj.GetUtcNow().UtcDateTime):HH:mm}: lo que hizo {quien} en la caja 2 quedó en eleventa a su nombre, pero en "
                + $"el turno {deQuien}: eleventa está abierto en la caja 2 con ese usuario. Para que el corte de cada quien cuadre en eleventa, que "
                + "cierren ese turno allá y que entre con su usuario quien está vendiendo.";
        }
    }

    /// <summary>
    /// Si lo último que se escribió en eleventa quedó en el turno de otro cajero (revisión del PR #144): lo que dice la pantalla de la
    /// caja 2. Vacío si no, o si después algo ya quedó en el turno de quien lo hizo. En memoria: al reiniciar el servicio se olvida
    /// (queda en el registro).
    /// </summary>
    public string? AvisoDelTurno() => _turnoDeOtro;

    /// <summary>El nombre de quien hizo aquí lo que va en la bandeja: quien cobró, devolvió o hizo la entrada o salida.</summary>
    private static async Task<string?> QuienLoHizoAsync(PosDbContext db, EnvioAEleventa envio)
    {
        var usuarioId = envio.Tipo switch
        {
            TipoEnvioEleventa.Venta => await db.Ventas.Where(v => v.Id == envio.VentaId).Select(v => (Guid?)v.CajeroId).FirstOrDefaultAsync(),
            TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion =>
                await db.Devoluciones.Where(d => d.Id == envio.DevolucionId).Select(d => (Guid?)d.UsuarioId).FirstOrDefaultAsync(),
            TipoEnvioEleventa.MovimientoDeCaja => await db.MovimientosCaja.Where(m => m.Id == envio.MovimientoId).Select(m => (Guid?)m.UsuarioId).FirstOrDefaultAsync(),
            TipoEnvioEleventa.DevolucionDeEleventa =>
                await db.Set<DevolucionDeEleventa>().Where(d => d.Id == envio.DevolucionDeEleventaId).Select(d => (Guid?)d.UsuarioId).FirstOrDefaultAsync(),
            _ => null,
        };
        return usuarioId is { } id ? await db.Usuarios.Where(u => u.Id == id).Select(u => u.Nombre).FirstOrDefaultAsync() : null;
    }

    // ── Los respaldos de eleventa ──

    private DateOnly Hoy() => DateOnly.FromDateTime(HoraMexico.ALocal(reloj.GetUtcNow().UtcDateTime));

    /// <summary>
    /// Si hoy ya se puede escribir en eleventa: con el respaldo del día, sí. Sin él, solo si no es obligatorio
    /// (<see cref="OpcionesEleventa.RespaldoObligatorio"/>) y no hay con qué respaldar, o ya se intentó hoy y no se pudo.
    /// </summary>
    private bool PuedeEscribir(DateOnly hoy) =>
        respaldo.HayDelDia(hoy) || !opciones.RespaldoObligatorio && (!respaldo.Configurado || _respaldoIntentado == hoy);

    /// <summary>El respaldo del día con gbak. Si no se pudo, se vuelve a intentar en unos minutos.</summary>
    private async Task RespaldarAsync(DateOnly hoy, CancellationToken ct)
    {
        _respaldando = true;
        try
        {
            var hecho = await respaldo.HacerAsync(_destino!.Value.Servidor, hoy, ct);
            _respaldoIntentado = hoy;
            if (hecho.Listo)
            {
                _respaldoError = null;
                log.LogInformation("Respaldo de eleventa del día en {Archivo}.", hecho.Archivo);
            }
            else
            {
                _respaldoError = hecho.Error;
                _respaldoDespuesUtc = reloj.GetUtcNow().UtcDateTime + ReintentarRespaldo;
                log.LogWarning("No se pudo respaldar eleventa: {Motivo}", hecho.Error);
            }
        }
        finally
        {
            _respaldando = false;
        }
    }

    /// <summary>
    /// La lectura vio que eleventa contesta: si falta el respaldo del día, que la bandeja lo haga ya, aunque no haya nada que
    /// mandar (así lo primero que se cobre en el día no espera a gbak).
    /// </summary>
    public void RespaldarSiFalta()
    {
        if (Activo && respaldo.Configurado && !respaldo.HayDelDia(Hoy()) && reloj.GetUtcNow().UtcDateTime >= _respaldoDespuesUtc)
        {
            _respaldarPronto = true;
            Despertar();
        }
    }

    /// <summary>Lo que la pantalla de la caja 2 dice del respaldo de eleventa; vacío si no hay nada que decir.</summary>
    public string? EstadoDelRespaldo()
    {
        var hoy = Hoy();
        if (!Activo || respaldo.HayDelDia(hoy))
            return null;
        if (_respaldando)
            return "Respaldando eleventa antes de mandarle lo del día…";
        var error = _respaldoIntentado == hoy ? _respaldoError : null;
        if (opciones.RespaldoObligatorio)
            return !respaldo.Configurado
                ? "No se manda nada a eleventa: falta la ruta de gbak.exe para respaldarla antes (Eleventa:Gbak)."
                : error is null ? null
                : $"No se manda nada a eleventa hasta respaldarla: {error} Se vuelve a intentar cada {ReintentarRespaldo.TotalMinutes:0} minutos.";
        return error is null ? null : $"Hoy no se pudo respaldar eleventa y se le manda sin respaldo: {error}";
    }

    // ── Eleventa se restauró ──

    /// <summary>
    /// Eleventa ya no tiene lo que le mandó la caja 2 (se restauró un respaldo): el puente deja de escribir y de aplicar la
    /// lectura hasta que alguien con «Configuración de la tienda» lo reanude (<see cref="ReanudarAsync"/>). La caja sigue
    /// vendiendo y lo que va a eleventa espera en la bandeja. Una detención a la vez.
    /// </summary>
    public async Task<DetencionDelPuente> DetenerAsync(PosDbContext db, Restauracion restauracion, CancellationToken ct)
    {
        if (await RestauracionDeEleventa.DetencionAsync(db, ct) is { } ya)
            return ya;
        var pruebas = string.Join("\n", restauracion.Pruebas);
        var detencion = new DetencionDelPuente
        {
            DetectadaUtc = reloj.GetUtcNow().UtcDateTime, Pruebas = pruebas.Length > 2000 ? pruebas[..2000] : pruebas, DesdeUtc = restauracion.DesdeUtc,
        };
        db.Set<DetencionDelPuente>().Add(detencion);
        await db.SaveChangesAsync(ct);
        return detencion;
    }

    /// <summary>
    /// Reanudar a mano, después de revisar eleventa (quien llama revisa el permiso): lo que el puente había mandado y ya no
    /// está allá queda por revisar, para anotarlo a mano (la lectura lo sigue descontando de las existencias); los turnos
    /// adoptados que ya no están se sueltan (la caja vuelve a tomar el que eleventa tenga abierto); y el puente vuelve a
    /// escribir y a leer. Queda en la bitácora. Sin eleventa no se puede: no se sabría qué se perdió. Regresa cuántos
    /// envíos quedaron por revisar.
    /// </summary>
    public async Task<int> ReanudarAsync(PosDbContext db, Nucleo.Turnos.Caja caja, Guid usuarioId, string? nota, CancellationToken ct = default)
    {
        var detencion = await db.Set<DetencionDelPuente>().Where(d => d.ReanudadoUtc == null).OrderBy(d => d.DetectadaUtc).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("El puente con eleventa no está detenido.");
        var (servidor, _) = _destino ?? throw new InvalidOperationException("Esta caja no manda a eleventa.");
        int perdidos;
        // Con el candado: ni la bandeja ni la lectura van mientras tanto (si no se suelta pronto, se intenta después).
        using (await IntentarCandadoAsync(opciones.EsperaMaxima * 2, ct)
            ?? throw new InvalidOperationException("La bandeja de eleventa sigue ocupada con eleventa: vuelve a intentarlo en un momento."))
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            List<EnvioAEleventa> sinEleventa;
            List<TurnoEnEleventa> sueltos;
            try
            {
                (sinEleventa, sueltos) = await RevisarConEleventaAsync().WaitAsync(opciones.EsperaMaxima, ct);
                async Task<(List<EnvioAEleventa>, List<TurnoEnEleventa>)> RevisarConEleventaAsync()
                {
                    await using var conexion = new FbConnection(servidor.Cadena());
                    await conexion.OpenAsync(FirebirdSinCancelar.Nunca);
                    return (await RestauracionDeEleventa.PerdidosAsync(db, conexion, ahora, FirebirdSinCancelar.Nunca),
                        await TurnosQueYaNoEstanAsync(db, conexion, FirebirdSinCancelar.Nunca));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && (EsDeRed(ex) || ex is FbException))
            {
                throw new InvalidOperationException($"{LectorEleventa.Explicar(ex, servidor)} Sin eleventa no se sabe qué se perdió: reanuda cuando conteste.");
            }
            foreach (var envio in sinEleventa)
            {
                envio.Error = Recortar($"Eleventa ya no lo tiene: se perdió al restaurar un respaldo (se había mandado el "
                    + $"{HoraMexico.ALocal(envio.EnviadaUtc!.Value):dd/MM HH:mm}). Anótalo a mano en eleventa.");
                envio.Estado = EstadoEnvioEleventa.PorRevisar;
                (envio.PorRevisarPorque, envio.PorRevisarUtc) = (MotivoPorRevisar.AnotarAMano, ahora);
                // Su número en eleventa ya es de otra cosa (o de nada); el folio de la venta se queda: es el que salió impreso.
                (envio.TicketEleventa, envio.IdEleventa) = (null, null);
            }
            db.Set<TurnoEnEleventa>().RemoveRange(sueltos);
            detencion.ReanudadoUtc = ahora;
            detencion.ReanudoId = usuarioId;
            detencion.Nota = nota?.Trim() is { Length: > 0 } texto ? (texto.Length > 500 ? texto[..500] : texto) : null;
            detencion.Perdidos = perdidos = sinEleventa.Count;
            new ServicioControl(db).Registrar(caja.SucursalId, caja.Id, usuarioId, AccionControlada.ConfigurarTienda,
                Recortar($"Reanudó el puente con eleventa (detenido el {HoraMexico.ALocal(detencion.DetectadaUtc):dd/MM HH:mm}: "
                    + $"{detencion.Pruebas.Replace("\n", "; ")}). {(perdidos == 0 ? "No se perdió nada de lo que mandó la caja 2"
                        : $"{perdidos} {(perdidos == 1 ? "cosa que había mandado la caja 2 quedó" : "cosas que había mandado la caja 2 quedaron")} por revisar")}"
                    + (sueltos.Count > 0 ? $"; se soltaron {sueltos.Count} turnos de eleventa que ya no están" : "")
                    + (detencion.Nota is { } n ? $". {n}" : "")));
            await db.SaveChangesAsync(ct);
        }
        log.LogWarning("Se reanudó el puente con eleventa; {Perdidos} envíos quedaron por revisar.", perdidos);
        Despertar();
        return perdidos;
    }

    /// <summary>Los turnos de eleventa que adoptó la caja nueva, sin su corte todavía, que ya no están en eleventa como estaban.</summary>
    private static async Task<List<TurnoEnEleventa>> TurnosQueYaNoEstanAsync(PosDbContext db, FbConnection conexion, CancellationToken ct)
    {
        var adoptados = await db.Set<TurnoEnEleventa>().Where(t => t.TomadoUtc == null).ToListAsync(ct);
        if (adoptados.Count == 0)
            return [];
        await using var tx = await conexion.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var sueltos = new List<TurnoEnEleventa>();
        foreach (var adoptado in adoptados)
        {
            var uno = new HuellaDelPuente([], [new TurnoDeEleventa(adoptado.TurnoEleventa, HoraMexico.ALocal(adoptado.InicioUtc))], new Dictionary<string, int>());
            if (await RestauracionDeEleventa.RevisarAsync(conexion, tx, uno, ct) is not null)
                sueltos.Add(adoptado);
        }
        await tx.CommitAsync(ct);
        return sueltos;
    }

    /// <summary>El turno de la caja nueva de lo que se mandó: el de la venta, el de donde salió lo devuelto o el del movimiento.</summary>
    private static async Task<Guid?> TurnoAquiAsync(PosDbContext db, EnvioAEleventa envio, CancellationToken ct) => envio.Tipo switch
    {
        TipoEnvioEleventa.Venta => await db.Ventas.Where(v => v.Id == envio.VentaId).Select(v => (Guid?)v.TurnoId).FirstOrDefaultAsync(ct),
        TipoEnvioEleventa.Cancelacion or TipoEnvioEleventa.Devolucion =>
            await db.Devoluciones.Where(d => d.Id == envio.DevolucionId).Select(d => (Guid?)d.TurnoId).FirstOrDefaultAsync(ct),
        TipoEnvioEleventa.MovimientoDeCaja => await db.MovimientosCaja.Where(m => m.Id == envio.MovimientoId).Select(m => (Guid?)m.TurnoId).FirstOrDefaultAsync(ct),
        TipoEnvioEleventa.DevolucionDeEleventa =>
            await db.Set<DevolucionDeEleventa>().Where(d => d.Id == envio.DevolucionDeEleventaId).Select(d => (Guid?)d.TurnoId).FirstOrDefaultAsync(ct),
        _ => null,
    };

    private static string Recortar(string texto) => texto.Length > 500 ? texto[..500] : texto;

    /// <summary>
    /// La PC apagada, la red caída o lenta, o eleventa que no terminó de contestar a tiempo (la llamada se canceló): no es de
    /// lo que se manda, es de la red. Quien llama ya descartó que se esté apagando el servicio.
    /// </summary>
    private static bool EsDeRed(Exception ex) =>
        ex is FbException { ErrorCode: 335544721 /* network_error */ or 335544726 /* net_read_err */ or 335544727 /* net_write_err */ }
            or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException or IOException
        || ex.InnerException is { } interna && EsDeRed(interna);

    /// <summary>Otra escritura tenía los mismos renglones de eleventa (el mismo producto, el mismo turno…).</summary>
    private static bool EsChoqueDeCandados(Exception ex) =>
        ex is FbException f && f.Errors.Any(e => e.Number is 335544345 /* lock_conflict */ or 335544336 /* deadlock */ or 335544451 /* update_conflict */);
}
