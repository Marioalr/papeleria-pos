using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Lo que dice la franja de la caja con <see cref="SinEscribir"/>.</summary>
    public const string AvisoSinEscribir = "Esta caja no le manda a eleventa: vende en eleventa. Lo que se cobre aquí no llega allá.";

    /// <summary>
    /// Esta PC es una caja de eleventa (<see cref="Caja"/>) y no le manda nada, como después del ensayo o de apagar el puente: lo vendido no
    /// entra a la bandeja y nunca llega allá (auditoría, 2026-11-04). Sin bloquear el cobro. Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public bool SinEscribir => (Caja is > 0 || Cajas.Count > 0) && !Escribir;

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

    /// <summary>
    /// El modo multicaja con el puente (2026-11-09): un solo servicio, en la caja principal, y cada caja del POS escribe en eleventa como
    /// su propia caja de allá, en su turno. Por la serie de la caja del POS («A», «B»): su <c>CAJAS.ID</c> y, si se quiere otro que
    /// <see cref="Cajero"/>, su cajero. La caja que no está aquí usa <see cref="Caja"/> y <see cref="Cajero"/>; vacío, todas (como el
    /// piloto en la caja 2, con su propio servicio). Solo para convivir con eleventa: se quita al dejarlo.
    /// </summary>
    public Dictionary<string, CajaDelPuente> Cajas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// A dónde y como qué caja escribir; vacío si no se escribe o falta algo (un 0, como lo trae la plantilla, es que falta). En el piloto
    /// (<see cref="CorteEnEleventa"/>) el puente no abre el turno de eleventa: lo abre eleventa.
    /// </summary>
    public (ServidorEleventa Servidor, CajaEnEleventa Caja)? ParaEscribir() => ParaEscribir(null);

    /// <summary>
    /// A dónde y como qué caja de eleventa escribe la caja del POS con esa serie (<see cref="Cajas"/>); sin serie, o si no está ahí, con
    /// <see cref="Caja"/> y <see cref="Cajero"/>. Vacío si no se escribe o a esa caja le falta algo.
    /// </summary>
    public (ServidorEleventa Servidor, CajaEnEleventa Caja)? ParaEscribir(string? serie) =>
        Escribir && Destino() is { } servidor && DeLaSerie(serie) is var (caja, cajero)
            ? (servidor, new CajaEnEleventa(caja, cajero, IpLocal(), AbreTurno: !CorteEnEleventa))
            : null;

    /// <summary>A dónde se escribe, si alguna caja puede escribir (<see cref="CajasDeEleventa"/>); vacío si no se escribe o falta algo.</summary>
    public ServidorEleventa? ServidorParaEscribir() => Escribir && CajasDeEleventa().Count > 0 ? Destino() : null;

    /// <summary>La caja y el cajero de eleventa de la caja del POS con esa serie; vacío si falta alguno (un 0 es que falta).</summary>
    public (int Caja, int Cajero)? DeLaSerie(string? serie)
    {
        var propia = serie is { Length: > 0 } && Cajas.TryGetValue(serie.Trim(), out var suya) ? suya : null;
        return (propia?.Caja ?? Caja, propia?.Cajero is > 0 and var suyo ? suyo : Cajero) is ( > 0 and var caja, > 0 and var cajero) ? (caja, cajero) : null;
    }

    /// <summary>
    /// Las cajas de eleventa como las que escribe este servicio, completas (con su cajero): la de <see cref="Caja"/> y las de
    /// <see cref="Cajas"/>. De ellas se leen los turnos abiertos.
    /// </summary>
    public IReadOnlyList<int> CajasDeEleventa() =>
        Cajas.Keys.Select(DeLaSerie).Append(DeLaSerie(null)).Where(c => c is not null).Select(c => c!.Value.Caja).Distinct().Order().ToList();

    /// <summary>Lo que falta de <see cref="Cajas"/>, dicho para quien configura; vacío si está bien. Dos cajas del POS no comparten una de eleventa.</summary>
    public string? ProblemaDeLasCajas()
    {
        if (Cajas.FirstOrDefault(c => c.Value is not { Caja: > 0 }) is { Key: { } sinCaja })
            return $"En Eleventa:Cajas, a la caja «{sinCaja}» le falta su número de caja de eleventa (Caja).";
        var repetida = Cajas.GroupBy(c => c.Value.Caja).FirstOrDefault(g => g.Count() > 1);
        return repetida is null ? null
            : $"En Eleventa:Cajas, las cajas {string.Join(" y ", repetida.Select(c => $"«{c.Key}»"))} tienen la misma caja de eleventa ({repetida.Key}): cada una lleva la suya.";
    }

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
/// De qué caja de eleventa es cada caja del POS (<see cref="OpcionesEleventa.Cajas"/>, por su serie), con la base a la mano. Solo para
/// convivir con eleventa: se quita al dejarlo.
/// </summary>
public static class CajasDelPuente
{
    /// <summary>Las cajas de eleventa de las cajas del POS, para los turnos del piloto (<see cref="TurnosDelPiloto"/>).</summary>
    public static async Task<CajasDelPiloto> DelPilotoAsync(this OpcionesEleventa opciones, PosDbContext db, CancellationToken ct = default)
    {
        // La caja del POS que está en Cajas a medias (sin su número o sin cajero) no toma la de las demás: no adopta nada hasta que se corrija.
        var (propias, sinLaSuya) = (new Dictionary<Guid, int>(), new HashSet<Guid>());
        if (opciones.Cajas.Count > 0)
            foreach (var caja in await db.Cajas.AsNoTracking().Select(c => new { c.Id, c.Serie }).ToListAsync(ct))
            {
                if (!opciones.Cajas.ContainsKey(caja.Serie))
                    continue;
                if (opciones.DeLaSerie(caja.Serie) is var (deEleventa, _))
                    propias[caja.Id] = deEleventa;
                else
                    sinLaSuya.Add(caja.Id);
            }
        return new CajasDelPiloto(opciones.DeLaSerie(null)?.Caja, propias, sinLaSuya);
    }

    /// <summary>La serie de una caja del POS; vacía si no se dice cuál o ya no está.</summary>
    public static async Task<string?> SerieAsync(PosDbContext db, Guid? cajaId, CancellationToken ct = default) =>
        cajaId is { } id ? await db.Cajas.AsNoTracking().Where(c => c.Id == id).Select(c => c.Serie).FirstOrDefaultAsync(ct) : null;

    /// <summary>A dónde y como qué caja de eleventa escribe esa caja del POS (<see cref="OpcionesEleventa.ParaEscribir(string?)"/>).</summary>
    public static async Task<(ServidorEleventa Servidor, CajaEnEleventa Caja)?> ParaEscribirAsync(this OpcionesEleventa opciones, PosDbContext db, Guid? cajaId,
        CancellationToken ct = default) =>
        opciones.ParaEscribir(opciones.Cajas.Count > 0 ? await SerieAsync(db, cajaId, ct) : null);

    /// <summary>Lo que dice la franja de una caja que vende (ya abrió algún turno) sin su caja de eleventa: lo que cobra espera en la bandeja.</summary>
    public const string AvisoSinSuCaja = "A esta caja le falta su caja de eleventa en la configuración: lo que se cobre aquí espera y no llega a eleventa hasta que se corrija. Avísale al dueño.";

    /// <summary>
    /// <see cref="AvisoSinSuCaja"/>, si aplica a esa caja del POS. Un celular (una caja que nunca ha abierto un turno) no vende: no se le dice nada.
    /// </summary>
    public static async Task<string?> AvisoSinSuCajaAsync(OpcionesEleventa opciones, PosDbContext db, Guid? cajaId, CancellationToken ct = default) =>
        opciones.Escribir && cajaId is { } id && opciones.DeLaSerie(await SerieAsync(db, id, ct)) is null && await db.Turnos.AnyAsync(t => t.CajaId == id, ct)
            ? AvisoSinSuCaja
            : null;

    /// <summary>La caja del POS que llama, por su llave; vacía desde el navegador de la PC del servicio (la de desarrollo).</summary>
    public static Guid? CajaQueLlama(this HttpContext http) =>
        http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var valor) && valor is Guid caja ? caja : null;
}

/// <summary>La caja de eleventa de una caja del POS (<see cref="OpcionesEleventa.Cajas"/>).</summary>
public sealed class CajaDelPuente
{
    /// <summary>Su caja de eleventa (<c>CAJAS.ID</c>).</summary>
    public int Caja { get; set; }

    /// <summary>El cajero de eleventa de quien no viene de eleventa (<c>USUARIOS.ID</c>); vacío, <see cref="OpcionesEleventa.Cajero"/>.</summary>
    public int? Cajero { get; set; }
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
    IServiceScopeFactory alcances, OpcionesEleventa opciones, EnvioVentasEleventa envios, ServicioRespaldos respaldos, TimeProvider reloj, ILogger<LecturaEleventa> log)
    : BackgroundService
{
    /// <summary>La bandeja no soltó el candado a tiempo: esta lectura se deja para la siguiente vuelta.</summary>
    public const string BandejaOcupada = "La bandeja de eleventa sigue ocupada con eleventa: esta lectura se deja para la siguiente vuelta.";

    /// <summary>
    /// Una base nueva (2026-10-31): sin tienda no hay a dónde traer lo de eleventa. No se le pregunta nada a eleventa; al crearse la
    /// tienda se lee al momento.
    /// </summary>
    public const string SinTienda = "Todavía no hay tienda en esta base: eleventa se lee en cuanto se cree, en la caja principal («Tienda nueva»).";

    private readonly SemaphoreSlim _ahora = new(0, 1);
    private readonly ServidorEleventa? _servidor = opciones.Conexion();
    private EstadoEleventaDto? _estado;
    private int _lecturas;
    // Cuándo salió bien la última lectura de este arranque, y si después ya no se pudo leer.
    private DateTime? _ultimaBuenaUtc;
    private volatile SinLeer? _sinLeer;

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

    /// <summary>Cuántas vueltas seguidas sin leer para decirlo en la pantalla (con una cada 5 minutos, unos 15).</summary>
    public const int VueltasParaAvisar = 3;

    private sealed record SinLeer(int Vueltas, DateTime DesdeUtc, string Motivo);

    private void NoSeLeyo(DateTime inicio, string motivo) =>
        _sinLeer = new SinLeer((_sinLeer?.Vueltas ?? 0) + 1, _sinLeer?.DesdeUtc ?? inicio, motivo);

    /// <summary>
    /// Si la lectura lleva varias vueltas sin poder leer: lo que dice la pantalla de la caja 2. La caja sigue vendiendo con lo último
    /// que se leyó (la decisión), pero que se sepa. En memoria: al reiniciar el servicio cuenta desde ahí.
    /// </summary>
    public string? AvisoSinLeer()
    {
        if (_sinLeer is not { Vueltas: >= VueltasParaAvisar } sinLeer)
            return null;
        return _ultimaBuenaUtc is { } buena
            ? $"Los precios de esta caja son de las {Hora(buena)}: eleventa no se ha podido leer. {sinLeer.Motivo} La caja sigue vendiendo."
            : $"Los precios de esta caja pueden estar viejos: eleventa no se ha podido leer desde las {Hora(sinLeer.DesdeUtc)}. {sinLeer.Motivo} La caja sigue vendiendo.";
    }

    /// <summary>«10:42», y con la fecha si no es de hoy.</summary>
    private string Hora(DateTime utc)
    {
        var (local, hoy) = (HoraMexico.ALocal(utc), HoraMexico.ALocal(reloj.GetUtcNow().UtcDateTime));
        return local.ToString(local.Date == hoy.Date ? "HH:mm" : "HH:mm 'del' dd/MM", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Una lectura. Cada una deja su renglón en el registro del servicio (el piloto alfa, 2026-10-25: juntar datos), siempre igual
    /// para poder juntarlos después: «Lectura de eleventa · 830 ms · 1,950 productos · 3 precios, 12 existencias · avisos: …», o
    /// por qué no se leyó o no se aplicó. Lo que pasa con los turnos del piloto (adoptar el de eleventa, tomar su corte) va aparte.
    /// </summary>
    private async Task LeerAsync(ServidorEleventa servidor, TimeSpan espera, CancellationToken ct)
    {
        var inicio = reloj.GetUtcNow().UtcDateTime;
        var cronometro = Stopwatch.StartNew();
        try
        {
            using var alcance = alcances.CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (!await db.Sucursales.AnyAsync(ct))
            {
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, SinTienda,
                    HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                log.LogInformation("Lectura de eleventa · no se leyó: {Motivo}", SinTienda);
                return;
            }
            // El piloto: la lectura y la bandeja usan el mismo candado (mientras se lee y se aplica, no se manda nada). Si la
            // bandeja lleva demasiado con eleventa (una llamada colgada), esta lectura se deja para la siguiente vuelta en lugar
            // de esperar sin fin (revisión del 2026-10-17).
            using var candado = envios.Activo ? await envios.IntentarCandadoAsync(opciones.EsperaMaxima * 2, ct) : null;
            if (envios.Activo && candado is null)
            {
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, BandejaOcupada,
                    HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                NoSeLeyo(inicio, BandejaOcupada);
                log.LogWarning("Lectura de eleventa · no se leyó (esperó {Tardo}): {Motivo}", EnvioVentasEleventa.Segundos(cronometro.Elapsed), BandejaOcupada);
                return;
            }
            // Antes de leer: si cambió la base con algo pendiente, faltarían los tickets de la otra y parecería una restauración, cuyo aviso
            // dice que se reanude (revisión de la auditoría, 2026-11-04).
            if (envios.Activo)
                await envios.RevisarBaseAsync(db, ct);
            // El puente detenido (eleventa se restauró): lo de eleventa ya no cuadra con lo que mandó la caja 2 (sus
            // existencias, sus turnos, sus números), así que no se aplica nada hasta reanudarlo.
            if (envios.Activo && await RestauracionDeEleventa.DetencionAsync(db, ct) is { } detenido)
            {
                _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado,
                    $"El puente con eleventa está detenido desde el {HoraMexico.ALocal(detenido.DetectadaUtc):dd/MM HH:mm}: no se aplica la lectura hasta reanudarlo.",
                    HoraMexico.ALocal(inicio + espera), ++_lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
                log.LogInformation("Lectura de eleventa · no se leyó: {Motivo}", _estado.Error);
                return;
            }
            // Lo que la bandeja mande mientras se lee pudo entrar a eleventa sin que aquí se sepa todavía.
            using var mandados = envios.EnCurso.Vigilar();
            // En el piloto, en la misma foto: los turnos de la caja 2 en eleventa, lo que se devolvió allá de sus tickets y si
            // sigue ahí lo que escribió el puente.
            var ahora = reloj.GetUtcNow().UtcDateTime;
            var cajas = opciones.CorteEnEleventa ? await opciones.DelPilotoAsync(db, ct) : null;
            var consulta = cajas is { Todas.Count: > 0 }
                ? await TurnosDelPiloto.ConsultaAsync(db, cajas, ct) with
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
                // El renglón de la detención («Puente con eleventa · se detuvo») ya lo dejó DetenerAsync.
                log.LogWarning("Lectura de eleventa · {Tardo} · no se aplicó: {Motivo}", EnvioVentasEleventa.Milisegundos(cronometro.Elapsed), _estado.Error);
                return;
            }
            var turnos = consulta is not null && datos.Piloto is { } piloto ? await TurnosDelPiloto.AplicarAsync(db, cajas!, piloto, ahora, ct) : null;
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
            (_ultimaBuenaUtc, _sinLeer) = (ahora, null);
            // Cada lectura, con lo que tardó: en el piloto dice cómo contesta eleventa por la red a lo largo del día.
            log.LogInformation("Lectura de eleventa · {Tardo} · {Productos} · {Resultado}{Avisos}", EnvioVentasEleventa.Milisegundos(cronometro.Elapsed),
                $"{datos.Productos.Count.ToString("#,##0", CultureInfo.InvariantCulture)} {(datos.Productos.Count == 1 ? "producto" : "productos")}", texto,
                avisos.Count == 0 ? "" : " · avisos: " + string.Join(" | ", avisos.Select(a => $"{a.Key} ({a.Value})")));
            foreach (var hecho in turnos?.Hechos ?? [])
                log.LogInformation("Turno del piloto · {Hecho}", hecho);
            // Lo que seguía en la bandeja de un turno que eleventa cortó: su hora dice si va al turno siguiente o queda por revisar.
            if (turnos is { Cortes: > 0 })
            {
                envios.Despertar();
                // Con cada corte, una copia de la base, como el corte Z del POS: en el piloto ese no corre (auditoría, 2026-11-04).
                var hora = HoraMexico.ALocal(ahora);
                _ = Task.Run(() => respaldos.Hacer($"z-{hora:HHmm}"), CancellationToken.None);
            }
            // Eleventa contesta: si falta su respaldo del día, que se haga antes de que haya algo que mandarle.
            envios.RespaldarSiFalta();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Si eleventa no contesta (la PC apagada, la red caída), se sigue vendiendo con lo último que se leyó.
            _estado = new EstadoEleventaDto(true, servidor.ToString(), HoraMexico.ALocal(inicio), false, _estado?.Resultado, LectorEleventa.Explicar(ex, servidor),
                HoraMexico.ALocal(inicio + espera), _lecturas, _estado?.Avisos ?? new Dictionary<string, int>());
            NoSeLeyo(inicio, _estado.Error!);
            // El detalle completo solo de lo inesperado: la red caída, el tiempo o lo que contestó eleventa ya lo dice el texto.
            log.LogWarning(EnvioVentasEleventa.EsDeRed(ex) || ex is FirebirdSql.Data.FirebirdClient.FbException ? null : ex,
                "Lectura de eleventa · no se pudo ({Tardo}): {Motivo}{Detalle}", EnvioVentasEleventa.Milisegundos(cronometro.Elapsed), _estado.Error,
                LectorEleventa.DetalleParaElRegistro(ex, servidor));
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
