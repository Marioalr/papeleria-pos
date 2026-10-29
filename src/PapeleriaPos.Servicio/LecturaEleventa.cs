using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// «Eleventa» en la configuración del servicio de la caja 2 durante el piloto: de dónde leer el catálogo y a qué caja
/// de eleventa mandar cada venta. Los datos reales (la PC de la caja principal, la ruta de su base, la clave, los ID)
/// van en la configuración de esa PC, nunca en el repositorio. La clave puede ir en la variable de entorno
/// Eleventa__Clave.
/// </summary>
public sealed class OpcionesEleventa
{
    /// <summary>Leer el catálogo cada pocos minutos (paso 1).</summary>
    public bool Activo { get; set; }

    /// <summary>Mandar cada venta a eleventa (paso 3). Necesita <see cref="Caja"/> y <see cref="Cajero"/>.</summary>
    public bool Escribir { get; set; }

    /// <summary>
    /// El piloto: mientras esta caja manda sus ventas a eleventa, solo cobra y devuelve en efectivo (decisión del dueño,
    /// 2026-10-12; <see cref="SoloEfectivoEnElPiloto"/>). Lo marca <see cref="Escribir"/> y no <see cref="Activo"/>: solo
    /// leer el catálogo no manda nada a eleventa. Basta la bandera, aunque falte algo de la configuración: una caja del
    /// piloto mal configurada tampoco cobra con tarjeta. Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public bool SoloEfectivo => Escribir;

    /// <summary>
    /// El piloto: mientras esta caja manda sus ventas a eleventa, su corte se hace en eleventa (decisión del dueño,
    /// 2026-10-12; <see cref="CorteEnElPiloto"/>): el servicio no hace el corte Z, el puente no abre ni cierra el turno de
    /// eleventa (lo abre y lo cierra eleventa) y solo se cancela un ticket del mismo día. Lo marca <see cref="Escribir"/>,
    /// como <see cref="SoloEfectivo"/>. Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public bool CorteEnEleventa => Escribir;

    /// <summary>
    /// El piloto para lo que cuenta las ventas de la tienda (los reportes de ventas; revisión del PR #131, 2026-10-22): con
    /// cualquiera de los dos prendidos, eleventa sigue siendo el punto de venta de la caja principal, así que en esta base solo
    /// está lo de la caja 2 y el historial de eleventa es de toda la tienda. La lectura (<see cref="Activo"/>) es lo que marca el
    /// piloto en la trastienda (productos, inventario, promociones); mandar las ventas (<see cref="Escribir"/>), en el cobro y
    /// el corte. Una caja 2 que solo lee el catálogo también es del piloto. Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public bool Piloto => Activo || Escribir;

    /// <summary>La caja de eleventa como la que vende esta caja (<c>CAJAS.ID</c>).</summary>
    public int? Caja { get; set; }

    /// <summary>
    /// El código del producto con que la tienda cobra las impresiones en eleventa (IMP «Impresiones», a granel a $10). Mientras esta
    /// caja manda sus ventas a eleventa (<see cref="Escribir"/>), la línea de impresiones del POS (las de la caja 2 y las de Control
    /// Print) va como él: la cantidad es el monto entre su precio, como las cobra la tienda (2026-10-25). Solo para convivir con
    /// eleventa: se quita al dejarlo.
    /// </summary>
    public string ProductoImpresiones { get; set; } = "IMP";

    /// <summary>
    /// El código del producto de eleventa con que la tienda cobra las copias del panel de la impresora (COP, a granel a $10, en el
    /// departamento de papelería; 2026-10-29): en el piloto, la línea de copias del ticket va como él, aparte de la de impresiones.
    /// </summary>
    public string ProductoCopias { get; set; } = "COP";

    /// <summary>El cajero de eleventa a cuyo nombre van las ventas (<c>USUARIOS.ID</c>).</summary>
    public int? Cajero { get; set; }

    /// <summary>Cuánto espera el cobro a que eleventa dé el folio antes de imprimir con el de esta caja.</summary>
    public double SegundosAlCobrar { get; set; } = 2;

    /// <summary>Cada cuánto se vuelve a intentar lo que quedó en la bandeja.</summary>
    public int SegundosEntreEnvios { get; set; } = 30;

    /// <summary>La PC de la caja principal de eleventa (nombre o IP).</summary>
    public string? Servidor { get; set; }

    public int Puerto { get; set; } = 3050;

    /// <summary>La ruta de la base en esa PC («C:\…\PDVDATA.FDB»).</summary>
    public string? Base { get; set; }

    public string Usuario { get; set; } = "SYSDBA";

    public string? Clave { get; set; }

    /// <summary>Cada cuánto se lee.</summary>
    public int Minutos { get; set; } = 5;

    /// <summary>
    /// Lo más que se espera a eleventa en una lectura, un envío de la bandeja o una revisión (revisión del 2026-10-17): una
    /// llamada que se queda colgada (la red se cae a medias) se deja para la siguiente vuelta, como si no hubiera red, y no
    /// detiene ni la lectura ni la bandeja. Firebird por sí solo puede esperar sin fin.
    /// </summary>
    public int SegundosMaximos { get; set; } = 120;

    public TimeSpan EsperaMaxima => TimeSpan.FromSeconds(Math.Max(1, SegundosMaximos));

    /// <summary>
    /// Los respaldos de eleventa (<see cref="RespaldoDeEleventa"/>): la ruta de <c>gbak.exe</c> en esta PC, el de Firebird
    /// 2.0 como el de la tienda (por ejemplo, «C:\Program Files\Firebird\Firebird_2_0\bin\gbak.exe»). Vacía, no hay respaldo
    /// propio, y con <see cref="RespaldoObligatorio"/> no se escribe en eleventa.
    /// </summary>
    public string? Gbak { get; set; }

    /// <summary>Dónde van los respaldos de eleventa: en el disco de esta PC y fuera de OneDrive. Por omisión, junto a la base («respaldos-eleventa»).</summary>
    public string? CarpetaRespaldos { get; set; }

    /// <summary>Cuántos días de respaldos de eleventa se guardan.</summary>
    public int DiasDeRespaldos { get; set; } = 5;

    /// <summary>
    /// Sin el respaldo del día no se escribe en eleventa: lo que va a eleventa espera en la bandeja y la pantalla de la caja
    /// 2 dice por qué (la caja sigue vendiendo). En falso se escribe aunque no se haya podido respaldar.
    /// </summary>
    public bool RespaldoObligatorio { get; set; } = true;

    public ServidorEleventa? Conexion() => Activo ? Destino() : null;

    /// <summary>
    /// A dónde y como qué caja escribir; vacío si no se escribe o falta algo. En el piloto (<see cref="CorteEnEleventa"/>)
    /// el puente no abre el turno de eleventa: lo abre eleventa.
    /// </summary>
    public (ServidorEleventa Servidor, CajaEnEleventa Caja)? ParaEscribir() =>
        Escribir && Destino() is { } servidor && Caja is { } caja && Cajero is { } cajero
            ? (servidor, new CajaEnEleventa(caja, cajero, IpLocal(), AbreTurno: !CorteEnEleventa))
            : null;

    private ServidorEleventa? Destino() =>
        Servidor is { Length: > 0 } servidor && Base is { Length: > 0 } ruta && Clave is { Length: > 0 } clave
            ? new ServidorEleventa(servidor, ruta, Usuario, clave, Puerto)
            : null;

    /// <summary>La IP de esta PC en la red de la tienda, para <c>CAJAS.ULTIMA_IP</c>, como la anota eleventa.</summary>
    private static string? IpLocal()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}

/// <summary>
/// El piloto, paso 1 (docs/plan.md): cada pocos minutos lee el catálogo, los precios, las promociones y las existencias
/// del eleventa de la caja principal, solo leyendo, y pone al día la base de esta caja. Si eleventa no contesta, se
/// sigue vendiendo con lo último que se leyó y se vuelve a intentar en la siguiente vuelta. Mientras esta caja manda a
/// eleventa, en la misma foto trae los turnos de la caja 2 (la caja nueva adopta el abierto y toma su corte,
/// <see cref="TurnosDelPiloto"/>) y revisa que siga ahí lo que escribió el puente (si no, eleventa se restauró: no se
/// aplica nada y el puente se detiene, <see cref="RestauracionDeEleventa"/>), y lee y aplica con el candado de la bandeja.
/// </summary>
public sealed class LecturaEleventa(
    IServiceScopeFactory alcances, OpcionesEleventa opciones, EnvioVentasEleventa envios, TimeProvider reloj, ILogger<LecturaEleventa> log)
    : BackgroundService
{
    /// <summary>La bandeja no soltó el candado a tiempo: esta lectura se deja para la siguiente vuelta.</summary>
    public const string BandejaOcupada = "La bandeja de eleventa sigue ocupada con eleventa: esta lectura se deja para la siguiente vuelta.";

    private readonly SemaphoreSlim _ahora = new(0, 1);
    private readonly ServidorEleventa? _servidor = opciones.Conexion();
    private EstadoEleventaDto? _estado;
    private int _lecturas;

    public EstadoEleventaDto Estado => _estado ?? new EstadoEleventaDto(_servidor is not null, _servidor?.ToString(), null, false, null,
        opciones.Activo && _servidor is null ? "Falta el servidor, la base o la clave de eleventa en la configuración." : null, null, 0,
        new Dictionary<string, int>());

    /// <summary>Que lea ya, sin esperar la vuelta (después de cambiar algo en eleventa).</summary>
    public void LeerAhora()
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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_servidor is not { } servidor)
        {
            if (opciones.Activo)
                log.LogWarning("La lectura de eleventa está activa pero falta el servidor, la base o la clave.");
            return;
        }
        var espera = TimeSpan.FromMinutes(Math.Max(1, opciones.Minutos));
        while (!ct.IsCancellationRequested)
        {
            await LeerAsync(servidor, espera, ct);
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

    private async Task LeerAsync(ServidorEleventa servidor, TimeSpan espera, CancellationToken ct)
    {
        var inicio = reloj.GetUtcNow().UtcDateTime;
        try
        {
            using var alcance = alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            // El piloto: la lectura y la bandeja usan el mismo candado (mientras se lee y se aplica, no se manda nada). Si la
            // bandeja lleva demasiado con eleventa (una llamada colgada), esta lectura se deja para la siguiente vuelta en lugar
            // de esperar sin fin (revisión del 2026-10-17).
            using var candado = envios.Activo ? await envios.IntentarCandadoAsync(opciones.EsperaMaxima * 2, ct) : null;
            if (envios.Activo && candado is null)
            {
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, BandejaOcupada,
                    HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                log.LogWarning("No se leyó eleventa: {Motivo}", BandejaOcupada);
                return;
            }
            // El puente detenido (eleventa se restauró): lo de eleventa ya no cuadra con lo que mandó la caja 2 (sus
            // existencias, sus turnos, sus números), así que no se aplica nada hasta reanudarlo.
            if (envios.Activo && await RestauracionDeEleventa.DetencionAsync(db, ct) is { } detenido)
            {
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado,
                    $"El puente con eleventa está detenido desde el {HoraMexico.ALocal(detenido.DetectadaUtc):dd/MM HH:mm}: no se aplica la lectura hasta reanudarlo.",
                    HoraMexico.ALocal(inicio + espera), ++_lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                return;
            }
            // Lo que la bandeja mande mientras se lee pudo entrar a eleventa sin que aquí se sepa todavía.
            using var mandados = envios.EnCurso.Vigilar();
            // En el piloto, en la misma foto: los turnos de la caja 2 en eleventa, lo que se devolvió allá de sus tickets y si
            // sigue ahí lo que escribió el puente.
            var ahora = reloj.GetUtcNow().UtcDateTime;
            var consulta = opciones.CorteEnEleventa && opciones.Caja is { } caja
                ? await TurnosDelPiloto.ConsultaAsync(db, caja, ct) with
                {
                    Tickets = await DevolucionesConEleventa.TicketsDeLaVueltaAsync(db, ahora, ct),
                    Huella = envios.Activo ? await RestauracionDeEleventa.HuellaAsync(db, ahora, RestauracionDeEleventa.AlLeer, ct) : null,
                }
                : null;
            // Con un límite: si eleventa se queda callado a media lectura, se deja de esperar y se vuelve a intentar en la siguiente
            // vuelta. Sin cancelarla: con Firebird 2.0 cancelar truena el servicio (FirebirdSinCancelar).
            var datos = await LectorEleventa.LeerCatalogoAsync(servidor, consulta, FirebirdSinCancelar.Nunca).WaitAsync(opciones.EsperaMaxima, ct);
            ahora = reloj.GetUtcNow().UtcDateTime;
            if (datos.Piloto?.Restauracion is { } restauracion)
            {
                await envios.DetenerAsync(db, restauracion, ct);
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado,
                    $"{RestauracionDeEleventa.Texto(restauracion)} No se aplicó la lectura y el puente se detuvo.", HoraMexico.ALocal(inicio + espera),
                    ++_lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                log.LogError("El puente con eleventa se detuvo: {Motivo}", RestauracionDeEleventa.Texto(restauracion));
                return;
            }
            var turnos = consulta is not null && datos.Piloto is { } piloto ? await TurnosDelPiloto.AplicarAsync(db, consulta.CajaId, piloto, ahora, ct) : null;
            // La vuelta, antes de las existencias: lo devuelto en eleventa entra con su devolución, no como un ajuste. En su propia
            // transacción: una devolución de la caja que no esperó el candado no se pierde.
            var vuelta = datos.Piloto is { } conDevoluciones ? await DevolucionesConEleventa.PasarVueltaAsync(db, conDevoluciones.Devoluciones, ahora, ct) : null;
            var resultado = await SincronizacionEleventa.AplicarAsync(db, datos, ahora, mandados, ct);
            var avisos = new Dictionary<string, int>(resultado.Avisos);
            foreach (var aviso in turnos?.Avisos ?? [])
                avisos[aviso] = avisos.GetValueOrDefault(aviso) + 1;
            if (vuelta is { PorRevisar: > 0 } choques)
                avisos[DevolucionesConEleventa.VueltaPorRevisar] = avisos.GetValueOrDefault(DevolucionesConEleventa.VueltaPorRevisar) + choques.PorRevisar;
            if (vuelta is { Retenidas: > 0 } retenidas)
                avisos[DevolucionesConEleventa.VueltaRetenida] = avisos.GetValueOrDefault(DevolucionesConEleventa.VueltaRetenida) + retenidas.Retenidas;
            var texto = resultado.ToString();
            if (turnos is { Cortes: > 0 } conCorte)
                texto += $"; {conCorte.Cortes} {(conCorte.Cortes == 1 ? "turno cerrado" : "turnos cerrados")} con el corte de eleventa";
            if (vuelta is { Pasadas: > 0 } pasadas)
                texto += $"; {pasadas.Pasadas} {(pasadas.Pasadas == 1 ? "devolución" : "devoluciones")} de eleventa";
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), true, texto, null,
                HoraMexico.ALocal(inicio + espera), ++_lecturas, avisos);
            if (resultado.HuboCambios || turnos is { Cortes: > 0 } or { Adoptados: > 0 } || vuelta is { Pasadas: > 0 } or { PorRevisar: > 0 } or { Retenidas: > 0 })
                log.LogInformation("Eleventa ({Servidor}): {Resultado}.", servidor, texto);
            // Lo que seguía en la bandeja de un turno que eleventa cortó: su hora dice si va al turno siguiente o queda por revisar.
            if (turnos is { Cortes: > 0 })
                envios.Despertar();
            // Eleventa contesta: si falta su respaldo del día, que se haga antes de que haya algo que mandarle.
            envios.RespaldarSiFalta();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Si eleventa no contesta (la PC apagada, la red caída), se sigue vendiendo con lo último que se leyó.
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, LectorEleventa.Explicar(ex, servidor),
                HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
            log.LogWarning(ex, "No se pudo leer eleventa ({Servidor}).", servidor);
        }
    }
}

public static class ApiEleventa
{
    public static void MapearApiEleventa(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/eleventa").ConErrores().SoloSuCaja();

        api.MapGet("", (LecturaEleventa lectura) => lectura.Estado);

        api.MapPost("/leer", (LecturaEleventa lectura) =>
        {
            lectura.LeerAhora();
            return Results.Accepted();
        });

        // La bandeja de salida: lo pendiente, lo que hay que revisar y el último error.
        api.MapGet("/envios", (EnvioVentasEleventa envios, PosDbContext db) => envios.EstadoAsync(db));

        api.MapPost("/enviar", (EnvioVentasEleventa envios) =>
        {
            envios.Despertar();
            return Results.Accepted();
        });

        // Lo que no entra: se deja por revisar para que lo demás pase. Hasta anotarlo a mano, la lectura lo sigue descontando.
        // Lo deja quien entró con «Configuración de la tienda», como anotarlo a mano; queda en la bitácora.
        api.MapPost("/envios/{id:guid}/revisar", async (Guid id, SolicitudRevision s, EnvioVentasEleventa envios, PosDbContext db) =>
            await envios.DejarPorRevisarAsync(db, id, s)
                ? Results.Ok(await envios.EstadoAsync(db))
                : Results.BadRequest(new ErrorDto("Solo se deja por revisar lo que está pendiente.")));

        // Lo que ya quedó en eleventa a mano (pendiente o por revisar): ya no se manda ni se descuenta al leer eleventa. Lo
        // anota quien entró con «Configuración de la tienda», como «Ya lo revisé» y reanudar el puente; queda en la bitácora.
        // Una devolución de aquí de un ticket que está en eleventa dice además cuál es su anotación allá (se ligan): lo que la
        // lectura retenía de ese ticket y no se eligió se pasa en la siguiente lectura, que se pide ya.
        api.MapPost("/envios/{id:guid}/a-mano", async (Guid id, SolicitudAnotadoAMano s, EnvioVentasEleventa envios, PosDbContext db, LecturaEleventa lectura) =>
        {
            if (!await envios.AnotarAManoAsync(db, id, s))
                return Results.BadRequest(new ErrorDto("Solo se anota a mano lo que está pendiente o por revisar."));
            if (s.Anotaciones is { Count: > 0 })
                lectura.LeerAhora();
            return Results.Ok(await envios.EstadoAsync(db));
        });

        // La salida cuando la anotación a mano de una devolución de aquí no cuadra con nada que se pueda elegir: ya se resolvió
        // a mano en eleventa. Lo que eleventa devolvió de ese ticket se pasa a la venta en la siguiente lectura, que se pide ya.
        // Con «Configuración de la tienda»; queda en la bitácora.
        api.MapPost("/envios/{id:guid}/resuelta", async (Guid id, SolicitudRevision s, EnvioVentasEleventa envios, PosDbContext db, LecturaEleventa lectura) =>
        {
            if (!await envios.ResolverAManoAsync(db, id, s))
                return Results.BadRequest(new ErrorDto("Solo se resuelve a mano una devolución de aquí que espera su anotación en eleventa."));
            lectura.LeerAhora();
            return Results.Ok(await envios.EstadoAsync(db));
        });

        // Las devoluciones de eleventa que pueden ser la anotación a mano de una devolución de aquí, leídas en vivo: para elegir
        // la suya al marcarla «Ya lo anoté en eleventa».
        api.MapGet("/envios/{id:guid}/anotaciones", async (Guid id, EnvioVentasEleventa envios, PosDbContext db) => await envios.AnotacionesAsync(db, id));

        // Devolver con el folio de eleventa (también un ticket cobrado en eleventa) y lo que quedó por revisar (ApiPuenteEleventa).
        api.MapearPuente();
    }
}
