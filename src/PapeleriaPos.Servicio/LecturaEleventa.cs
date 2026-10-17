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

    /// <summary>La caja de eleventa como la que vende esta caja (<c>CAJAS.ID</c>).</summary>
    public int? Caja { get; set; }

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
            // El piloto: la lectura y la bandeja usan el mismo candado (mientras se lee y se aplica, no se manda nada).
            using var candado = envios.Activo ? await envios.CandadoAsync(ct) : null;
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
            var datos = await LectorEleventa.LeerCatalogoAsync(servidor, consulta, ct);
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
            // La vuelta, antes de las existencias: lo devuelto en eleventa entra con su devolución, no como un ajuste.
            var vuelta = datos.Piloto is { } conDevoluciones ? await DevolucionesConEleventa.AplicarVueltaAsync(db, conDevoluciones.Devoluciones, ahora, ct) : null;
            if (vuelta is not null)
                await db.SaveChangesAsync(ct);
            var resultado = await SincronizacionEleventa.AplicarAsync(db, datos, ahora, mandados, ct);
            var avisos = new Dictionary<string, int>(resultado.Avisos);
            foreach (var aviso in turnos?.Avisos ?? [])
                avisos[aviso] = avisos.GetValueOrDefault(aviso) + 1;
            if (vuelta is { PorRevisar: > 0 } choques)
                avisos[DevolucionesConEleventa.NoAlcanza] = avisos.GetValueOrDefault(DevolucionesConEleventa.NoAlcanza) + choques.PorRevisar;
            var texto = resultado.ToString();
            if (turnos is { Cortes: > 0 } conCorte)
                texto += $"; {conCorte.Cortes} {(conCorte.Cortes == 1 ? "turno cerrado" : "turnos cerrados")} con el corte de eleventa";
            if (vuelta is { Pasadas: > 0 } pasadas)
                texto += $"; {pasadas.Pasadas} {(pasadas.Pasadas == 1 ? "devolución" : "devoluciones")} de eleventa";
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), true, texto, null,
                HoraMexico.ALocal(inicio + espera), ++_lecturas, avisos);
            if (resultado.HuboCambios || turnos is { Cortes: > 0 } or { Adoptados: > 0 } || vuelta is { Pasadas: > 0 } or { PorRevisar: > 0 })
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
        api.MapPost("/envios/{id:guid}/revisar", async (Guid id, EnvioVentasEleventa envios, PosDbContext db) =>
            await envios.DejarPorRevisarAsync(db, id)
                ? Results.Ok(await envios.EstadoAsync(db))
                : Results.BadRequest(new ErrorDto("Solo se deja por revisar lo que está pendiente.")));

        // Lo que ya quedó en eleventa a mano (pendiente o por revisar): ya no se manda ni se descuenta al leer eleventa.
        api.MapPost("/envios/{id:guid}/a-mano", async (Guid id, EnvioVentasEleventa envios, PosDbContext db) =>
            await envios.AnotarAManoAsync(db, id)
                ? Results.Ok(await envios.EstadoAsync(db))
                : Results.BadRequest(new ErrorDto("Solo se anota a mano lo que está pendiente o por revisar.")));

        // Devolver con el folio de eleventa (también un ticket cobrado en eleventa) y lo que quedó por revisar (ApiPuenteEleventa).
        api.MapearPuente();
    }
}
