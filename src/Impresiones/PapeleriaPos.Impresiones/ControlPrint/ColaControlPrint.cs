using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.ControlPrint;

/// <summary>
/// A qué Control Print se conecta este servicio para ver su cola (la sección «ControlPrint» del servicio de la caja 2). El
/// código de conexión va solo en la variable de entorno del servicio (<c>ControlPrint__Codigo</c>), como la clave de eleventa.
/// </summary>
/// <param name="Servidor">La IP o el nombre de la PC de Control Print (la caja principal).</param>
public sealed record OpcionesColaControlPrint(string Servidor)
{
    public int Puerto { get; init; } = ProtocoloControlPrint.PuertoPorOmision;

    /// <summary>La huella configurada (ControlPrint:Huella). Sin ella, la que se fijó en el primer saludo (<see cref="ArchivoHuella"/>).</summary>
    public string? Huella { get; init; }

    /// <summary>El código de conexión (Control Print → Ajustes → Red), de la variable de entorno del servicio.</summary>
    public string? Codigo { get; init; }

    /// <summary>De cuántos días se ven los «por cobrar»: 2 es hoy y ayer (lo de antes, si sigue por cobrar allá, ya no sale).</summary>
    public int Dias { get; init; } = 2;

    /// <summary>
    /// Dónde se guarda la huella del certificado que se fijó en el primer saludo (junto a la base): así, al volver a arrancar, otro
    /// certificado en esa dirección se rechaza. Vacío: no se guarda.
    /// </summary>
    public string? ArchivoHuella { get; init; }

    /// <summary>Cómo sale esta PC en Control Print (Ajustes → Red); vacío, «PC (POS)».</summary>
    public string? Equipo { get; init; }

    /// <summary>La hora de la ventana de días y de lo que se anota; el cliente usa siempre la de la PC.</summary>
    public TimeProvider Reloj { get; init; } = TimeProvider.System;

    /// <summary>Sin conexión, o si Control Print no avisa sus cambios: cada cuánto se vuelve a leer.</summary>
    public TimeSpan Reintento { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Con algo que tiene que arreglar alguien (el código, el certificado, la versión): cada cuánto se vuelve a intentar.</summary>
    public TimeSpan ReintentoLargo { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Lo que se espera a que Control Print avise un cambio (su espera larga, hasta 25 s). Son 10 s (2026-10-29): si la caja principal
    /// se apaga sin avisar, la cola lo nota en unos 13 s (esto y 3 s más) y F2 deja de ofrecer sus tarjetas.
    /// </summary>
    public TimeSpan EsperaCambios { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lo que se espera después de un aviso: un trabajo cambia varias veces seguidas (detectado, analizado, impreso).</summary>
    public TimeSpan Juntar { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Aunque Control Print no avise nada, la cola se vuelve a leer cada esto (2026-10-29). Control Print 1.12 no avisa a sus PC el
    /// «Cobrar» de una sola tarjeta (solo los lotes): sin esto, lo que la caja principal cobra así no se notaría hasta el siguiente
    /// cambio allá. Son 20 s, dos esperas de cambios: una búsqueda indexada de lo que sigue por cobrar en los días que se ven, sin
    /// miniaturas (unos 1.3 KB por trabajo, casi siempre una sola página), poco para la caja 2 y para Control Print.
    /// </summary>
    public TimeSpan ReleerCada { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Lo que la caja 2 cobró sigue «por cobrar» en Control Print para siempre: lo de antes de los días que se ven se revisa cada esto
    /// (en la hora del servicio), hasta <see cref="DiasCobrados"/> días atrás.
    /// </summary>
    public TimeSpan RevisarCobradosCada { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Hasta cuántos días atrás se sigue revisando si lo cobrado aquí se cobró también allá.</summary>
    public int DiasCobrados { get; init; } = 30;
}

/// <summary>Cómo está la cola de Control Print: lo último que se leyó y si contesta.</summary>
/// <param name="Contesta">Vacío mientras se conecta por primera vez.</param>
/// <param name="Problema">Por qué no contesta o qué falta, para mostrarlo tal cual (nunca lleva el código).</param>
/// <param name="LeidaUtc">La última vez que se leyó la cola: lo de <paramref name="PorCobrar"/> es de esa hora.</param>
/// <param name="PorCobrar">
/// Todos los por cobrar de los días que se ven (hasta <see cref="ColaControlPrint.MaxPaginas"/> páginas), los más nuevos primero:
/// también los que la caja 2 ya cobró, que allá siguen por cobrar. El servicio los quita y ofrece hasta <see cref="ColaControlPrint.Limite"/>.
/// </param>
/// <param name="Total">Cuántos por cobrar hay en esos días (pueden ser más de los que se leyeron).</param>
/// <param name="SoloInvitado">Control Print está en «cada quien ve sus trabajos»: sin sesión solo se ven los de «Sin sesión».</param>
/// <param name="AvisoCostos">Por qué no se usan los costos de Control Print (los de fábrica: ¿se reinstaló?); vacío si se usan.</param>
public sealed record EstadoColaControlPrint(
    bool Configurado, string? Servidor, int Puerto, bool TieneCodigo, bool? Contesta, string? Problema, FallaControlPrint? Falla, string? Nombre,
    string? VersionServidor, string? Huella, bool HuellaConfigurada, DateTimeOffset? LeidaUtc, int Lecturas, IReadOnlyList<TrabajoControlPrint> PorCobrar,
    int Total, bool SoloInvitado, string? AvisoCostos = null);

/// <summary>
/// Lo que se leyó de la cola por cobrar de Control Print en una vuelta (para <see cref="ColaControlPrint.AlLeer"/>): la de los días
/// que se ven o, de vez en cuando, la de antes (hasta <see cref="OpcionesColaControlPrint.DiasCobrados"/> días).
/// </summary>
/// <param name="DesdeUtc">Desde cuándo (lo visto por Control Print a partir de esa hora).</param>
/// <param name="Completa">Se leyeron todos los por cobrar de ese rango.</param>
/// <param name="MasViejoUtc">La hora del más viejo que se leyó.</param>
/// <param name="PorCobrar">Los que allá siguen por cobrar (con <see cref="ColaControlPrint.Clave"/>).</param>
/// <param name="HastaUtc">Hasta cuándo (sin incluirla); la de los días que se ven, sin límite.</param>
public sealed record LecturaControlPrint(DateTime DesdeUtc, bool Completa, DateTime? MasViejoUtc, IReadOnlySet<(long Id, long Segundo)> PorCobrar,
    DateTime? HastaUtc = null)
{
    public bool SiguePorCobrar(long id, DateTime detectadoUtc) => PorCobrar.Contains(ColaControlPrint.Clave(id, detectadoUtc));

    /// <summary>Si esta lectura alcanza a ver un trabajo de esa hora: si alcanza y no está, allá ya no está por cobrar.</summary>
    public bool Alcanza(DateTime detectadoUtc) =>
        detectadoUtc >= DesdeUtc && (HastaUtc is not { } hasta || detectadoUtc < hasta) && (Completa || MasViejoUtc is { } viejo && detectadoUtc >= viejo);
}

/// <summary>Lo que dijo Control Print, justo antes de cobrar, de los trabajos que van en un ticket.</summary>
/// <param name="Siguen">Siguen por cobrar allá y con el mismo precio: se pueden cobrar.</param>
/// <param name="YaNo">Ya no están por cobrar allá (se cobraron en la caja principal, se marcaron no vendidas, se cancelaron).</param>
/// <param name="OtroPrecio">Les cambiaron el precio allá (un precio especial, por ejemplo): el de ahora.</param>
/// <param name="NoContesta">Control Print no contestó (o no a tiempo) por <paramref name="SinRespuesta"/>: por qué.</param>
/// <param name="SinRespuesta">
/// Los que no se pudieron revisar (2026-10-29: lo que sí contestó a tiempo cuenta; solo estos quedan sin saberse).
/// </param>
public sealed record RevisionControlPrint(
    IReadOnlyList<TrabajoControlPrint> Siguen, IReadOnlyList<(long Id, string Porque)> YaNo, IReadOnlyList<(long Id, long PrecioCentavos)> OtroPrecio,
    string? NoContesta, IReadOnlyList<long>? SinRespuesta = null)
{
    public bool TodoBien => NoContesta is null && YaNo.Count == 0 && OtroPrecio.Count == 0;
}

/// <summary>
/// La cola «por cobrar» de Control Print vista desde este servicio, siempre al día y SOLO LEYENDO (el dueño, 2026-10-25: el POS
/// nunca le escribe a Control Print). Se conecta como sus PC «Conectadas» (<see cref="ClienteControlPrint"/>), lee los por cobrar de
/// los últimos días (<see cref="OpcionesColaControlPrint.Dias"/>) y después espera sus cambios (su espera larga,
/// <c>api/changes</c>): cuando cambia un trabajo, vuelve a leer. Si Control Print no avisa sus cambios, lee cada
/// <see cref="OpcionesColaControlPrint.Reintento"/>; sin conexión, lo vuelve a intentar con esa misma pausa y dice que no contesta.
/// <para>
/// También guarda en memoria las miniaturas que se piden (de una en una o de dos en dos: la caja 2 tiene pocos recursos) y los
/// costos de Control Print (Ajustes → Costos), con los que se cobra lo que imprime la caja 2. Antes de cobrar, se revisa otra vez
/// cada trabajo del ticket (<see cref="RevisarAsync"/>): lo que ya se cobró en la caja principal no se cobra dos veces.
/// </para>
/// </summary>
public sealed class ColaControlPrint : IDisposable
{
    /// <summary>Cuántas tarjetas se ofrecen como máximo: los por cobrar más nuevos que la caja 2 no ha cobrado.</summary>
    public const int Limite = 200;

    /// <summary>Cuántos por cobrar se piden a la vez.</summary>
    public const int Pagina = 200;

    /// <summary>Lo más que se lee en una vuelta: 10 páginas (2,000 por cobrar en los días que se ven).</summary>
    public const int MaxPaginas = 10;

    /// <summary>Al revisar unos trabajos, los que se vieron a esta distancia o menos se buscan juntos (una búsqueda por su rango de horas).</summary>
    public static readonly TimeSpan Juntos = TimeSpan.FromMinutes(10);

    /// <summary>Cuántas miniaturas se guardan en memoria (son chicas: las de Control Print).</summary>
    public const int MiniaturasEnMemoria = 300;

    /// <summary>Un trabajo sin miniatura (todavía se analiza) se vuelve a pedir después de esto.</summary>
    public static readonly TimeSpan SinMiniaturaPor = TimeSpan.FromMinutes(1);

    private readonly OpcionesColaControlPrint? _opciones;

    /// <summary>
    /// «Servidor» no es una IP ni el nombre de una PC, o parece el código de conexión puesto ahí por error: no se busca en la red ni
    /// se repite en ningún lado (ni en el estado, ni en el registro, ni en <c>/api/controlprint</c>), como en recopilar-control-print.
    /// </summary>
    private readonly bool _servidorNoSirve;

    private readonly SemaphoreSlim _pidiendoMiniaturas = new(2, 2);
    private readonly ConcurrentDictionary<long, (byte[]? Png, DateTimeOffset LeidaUtc)> _miniaturas = new();
    private readonly ConcurrentDictionary<long, DateTime> _vistos = new();
    private long _leidaEn;
    private DateTimeOffset _cobradosRevisadosUtc;
    private ClienteControlPrint? _cliente;
    private EstadoColaControlPrint _estado;
    private CostosDeControlPrint? _costos;
    private string? _costosAvisados;

    /// <param name="opciones">Vacías: este servicio no tiene la sección «ControlPrint» y no hay tarjetas (como antes).</param>
    public ColaControlPrint(OpcionesColaControlPrint? opciones)
    {
        _opciones = opciones is { Servidor: { Length: > 0 } servidor } ? opciones with { Servidor = servidor.Trim() } : null;
        _servidorNoSirve = _opciones is not null
            && (Uri.CheckHostName(_opciones.Servidor) == UriHostNameType.Unknown || ProtocoloControlPrint.PareceCodigo(_opciones.Servidor));
        var tieneCodigo = _opciones?.Codigo is { } codigo && ProtocoloControlPrint.CodigoCompleto(codigo);
        _estado = new EstadoColaControlPrint(_opciones is not null, _servidorNoSirve ? null : _opciones?.Servidor,
            _opciones?.Puerto ?? ProtocoloControlPrint.PuertoPorOmision, tieneCodigo, null, null, null, null, null, null, _opciones?.Huella is not null, null, 0,
            [], 0, false);
        if (_servidorNoSirve)
            Estado = Estado with
            {
                Contesta = false,
                Problema = "En servicio.json, «ControlPrint:Servidor» no es la IP ni el nombre de la PC de Control Print (no se repite aquí, por si era "
                    + "el código de conexión, que va solo con poner-codigo-controlprint.ps1): pon la IP de la caja principal y reinicia el servicio.",
                Falla = FallaControlPrint.Servidor,
            };
        else if (_opciones is not null && !tieneCodigo)
            Estado = Estado with
            {
                Contesta = false,
                Problema = _opciones.Codigo is null
                    ? "Falta el código de conexión de Control Print en este servicio: ponlo con poner-codigo-controlprint.ps1 (Control Print → Ajustes → Red)."
                    : "El código de conexión de Control Print de este servicio no tiene 16 letras y números: vuelve a ponerlo con poner-codigo-controlprint.ps1.",
                Falla = FallaControlPrint.CodigoIncompleto,
            };
    }

    public bool Configurado => _opciones is not null;

    public EstadoColaControlPrint Estado
    {
        get => Volatile.Read(ref _estado);
        private set
        {
            var antes = Volatile.Read(ref _estado);
            Volatile.Write(ref _estado, value);
            if (antes.Contesta != value.Contesta || antes.Problema != value.Problema || antes.AvisoCostos != value.AvisoCostos)
                CambioDeEstado?.Invoke(value);
        }
    }

    /// <summary>
    /// Cuando empieza o deja de contestar, cambia por qué no contesta o si se usan sus costos (para el registro del servicio).
    /// </summary>
    public event Action<EstadoColaControlPrint>? CambioDeEstado;

    /// <summary>
    /// Cuando se leen los costos de Control Print y son otros que los de la vez anterior (para guardarlos como los de la sucursal:
    /// con ellos se pone precio a lo que imprime la caja 2, también si Control Print no contesta al arrancar).
    /// </summary>
    public Func<CostSettings, CancellationToken, Task>? AlCambiarCostos { get; set; }

    /// <summary>
    /// Después de cada lectura de la cola, con lo que se leyó: el servicio revisa si algo que la caja 2 cobró dejó de estar por cobrar
    /// allá (¿se cobró también en la caja principal?). Lo que haga no detiene la cola.
    /// </summary>
    public Func<LecturaControlPrint, CancellationToken, Task>? AlLeer { get; set; }

    /// <summary>Un trabajo por su número y la hora en que lo vio Control Print, al segundo (así los guarda Control Print).</summary>
    public static (long Id, long Segundo) Clave(long id, DateTime detectadoUtc) =>
        (id, TarjetaControlPrint.Utc(detectadoUtc).Ticks / TimeSpan.TicksPerSecond);

    private DateTimeOffset Ahora => (_opciones?.Reloj ?? TimeProvider.System).GetUtcNow();

    // ── La vuelta: conectar, leer y esperar cambios ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Mantiene la cola al día hasta que se detenga el servicio. Sin configuración, sin el código o con un «Servidor» que no sirve, no
    /// hace nada (el estado lo dice).
    /// </summary>
    public async Task CorrerAsync(CancellationToken ct)
    {
        if (_opciones is null || _servidorNoSirve || !Estado.TieneCodigo)
            return;
        var vistas = VersionesControlPrint.Ninguna;
        // Si ya se leyeron, desde que contesta, sus ajustes, sus costos y su cola: después solo se espera a que algo cambie.
        var alDia = false;
        var leer = true;
        var porSondeo = false;
        while (!ct.IsCancellationRequested)
        {
            var espera = TimeSpan.Zero;
            try
            {
                var cliente = await ClienteAsync(ct);
                if (!alDia)
                {
                    // Recién conectado (o de vuelta): primero sus contadores, que contestan de inmediato (y sin la pausa sin conexión
                    // del cliente: son los que dicen que ya volvió); luego quién ve los trabajos allá, sus costos y su cola.
                    if (!porSondeo)
                    {
                        try
                        {
                            vistas = await cliente.CambiosAsync(VersionesControlPrint.Ninguna, TimeSpan.Zero, ct);
                        }
                        catch (ErrorControlPrint ex) when (ex.Falla is FallaControlPrint.NoExiste or FallaControlPrint.Servidor or FallaControlPrint.SinPermiso)
                        {
                            // Este Control Print no avisa sus cambios: desde ahora se lee cada tanto.
                            porSondeo = true;
                        }
                    }
                    await LeerAjustesAsync(cliente, ct);
                    await LeerCostosAsync(cliente, ct);
                    leer = true;
                }
                if (leer)
                {
                    await LeerPorCobrarAsync(cliente, ct);
                    leer = false;
                }
                alDia = true;
                VersionesControlPrint? nuevas = null;
                if (!porSondeo)
                {
                    try
                    {
                        nuevas = await cliente.CambiosAsync(vistas, _opciones.EsperaCambios, ct);
                    }
                    catch (ErrorControlPrint ex) when (ex.Falla is FallaControlPrint.NoExiste or FallaControlPrint.Servidor or FallaControlPrint.SinPermiso)
                    {
                        porSondeo = true;
                    }
                }
                if (nuevas is null)
                {
                    (espera, leer) = (_opciones.Reintento, true);
                    // Los costos: se vuelven a pedir cada 5 minutos (si no, salen de memoria).
                    await LeerCostosAsync(cliente, ct);
                }
                else
                {
                    Contesto(cliente);
                    // Al reiniciarse, Control Print vuelve a empezar sus contadores: cualquier diferencia es un cambio.
                    var reinicio = nuevas.VersionApp != vistas.VersionApp;
                    var cambio = nuevas.Trabajos != vistas.Trabajos || nuevas.Usuarios != vistas.Usuarios || reinicio;
                    if (nuevas.Configuracion != vistas.Configuracion || reinicio)
                        await LeerAjustesAsync(cliente, ct);
                    vistas = nuevas;
                    // Los costos: los vuelve a pedir si cambió su configuración o pasaron 5 minutos (si no, salen de memoria).
                    await LeerCostosAsync(cliente, ct);
                    if (cambio)
                        (leer, espera) = (true, _opciones.Juntar);
                    // Aunque no avise nada (un «Cobrar» de una sola tarjeta no se avisa), cada ReleerCada se vuelve a leer.
                    else if (TocaReleer())
                        leer = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ErrorControlPrint ex)
            {
                NoContesto(ex.Message, ex.Falla);
                (alDia, leer) = (false, true);
                if (ex.Falla != FallaControlPrint.SinConexion)
                {
                    // El código, el certificado o la versión: con un cliente nuevo se vuelve a saludar (si ya se arregló, conecta).
                    Interlocked.Exchange(ref _cliente, null)?.Dispose();
                }
                espera = ex.Falla == FallaControlPrint.SinConexion ? _opciones.Reintento : _opciones.ReintentoLargo;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or HttpRequestException)
            {
                // La configuración (una huella o una dirección mal escritas) o algo que no se esperaba: se dice y se vuelve a intentar.
                NoContesto($"No se pudo leer Control Print en {_opciones.Servidor}:{_opciones.Puerto}: {ex.Message}", FallaControlPrint.Servidor);
                (alDia, leer) = (false, true);
                Interlocked.Exchange(ref _cliente, null)?.Dispose();
                espera = _opciones.ReintentoLargo;
            }
            if (espera > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(espera, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<ClienteControlPrint> ClienteAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _cliente) is { } listo)
            return listo;
        var opciones = _opciones!;
        var guardada = opciones.Huella is null ? HuellaGuardada() : null;
        var cliente = await ClienteControlPrint.ConectarAsync(
            new OpcionesClienteControlPrint(opciones.Servidor, opciones.Puerto)
            {
                Huella = opciones.Huella ?? guardada,
                Equipo = opciones.Equipo ?? $"{Environment.MachineName} (POS)",
            },
            opciones.Codigo!, ct);
        // La primera vez que contesta con el código correcto, su certificado queda fijado también para la próxima vez.
        if (opciones.Huella is null && guardada is null)
            GuardarHuella(cliente);
        Volatile.Write(ref _cliente, cliente);
        Estado = Estado with
        {
            Nombre = SinElCodigo(cliente, cliente.NombreServidor), VersionServidor = SinElCodigo(cliente, cliente.VersionServidor), Huella = cliente.Huella,
        };
        return cliente;
    }

    private async Task LeerPorCobrarAsync(ClienteControlPrint cliente, CancellationToken ct)
    {
        var desde = DesdeUtc();
        // Todos los de los días que se ven, de página en página: lo que la caja 2 ya cobró sigue «por cobrar» allá (2026-10-29) y, si se
        // leyeran solo los 200 más nuevos, en un día de mucho trabajo podrían ser todos de la caja 2 y lo que de verdad falta no saldría.
        var (porCobrar, total, completa) = await PorCobrarAsync(cliente, desde, null, ct);
        foreach (var t in porCobrar)
            _vistos[t.Id] = TarjetaControlPrint.Utc(t.DetectadoUtc);
        foreach (var (id, hora) in _vistos.Where(v => v.Value < desde.AddDays(-1)).ToList())
            _vistos.TryRemove(new KeyValuePair<long, DateTime>(id, hora));
        // Primero lo que el servicio revisa (lo cobrado aquí que dejó de estar por cobrar allá) y después se publica: así, quien vea la
        // lectura nueva ya ve también lo que se notó con ella.
        await AvisarLecturaAsync(new LecturaControlPrint(desde, completa, Mas(porCobrar), Claves(porCobrar)), ct);
        Contesto(cliente);
        Estado = Estado with { LeidaUtc = Ahora, Lecturas = Estado.Lecturas + 1, PorCobrar = porCobrar, Total = Math.Max(total, porCobrar.Count) };
        Interlocked.Exchange(ref _leidaEn, System.Diagnostics.Stopwatch.GetTimestamp());
        OlvidarMiniaturas(porCobrar);
        await RevisarCobradosDeAntesAsync(cliente, desde, ct);
    }

    /// <summary>
    /// Lo que la caja 2 cobró sigue «por cobrar» allá para siempre (2026-10-29): lo de antes de los días que se ven (hasta
    /// <see cref="OpcionesColaControlPrint.DiasCobrados"/> días) también se revisa, de vez en cuando
    /// (<see cref="OpcionesColaControlPrint.RevisarCobradosCada"/>, en la hora del servicio): si la caja principal lo cobra días
    /// después, también se nota.
    /// </summary>
    private async Task RevisarCobradosDeAntesAsync(ClienteControlPrint cliente, DateTime hastaUtc, CancellationToken ct)
    {
        if (AlLeer is null)
            return;
        if (_cobradosRevisadosUtc == default)
            _cobradosRevisadosUtc = Ahora;
        if (Ahora - _cobradosRevisadosUtc < _opciones!.RevisarCobradosCada)
            return;
        _cobradosRevisadosUtc = Ahora;
        var desde = hastaUtc.AddDays(-Math.Max(1, _opciones.DiasCobrados));
        var (porCobrar, _, completa) = await PorCobrarAsync(cliente, desde, hastaUtc, ct);
        await AvisarLecturaAsync(new LecturaControlPrint(desde, completa, Mas(porCobrar), Claves(porCobrar), hastaUtc), ct);
    }

    /// <summary>Los por cobrar (con precio) vistos en ese rango, de página en página (hasta <see cref="MaxPaginas"/>), sin el código.</summary>
    private static async Task<(List<TrabajoControlPrint> PorCobrar, int Total, bool Completa)> PorCobrarAsync(ClienteControlPrint cliente, DateTime desde,
        DateTime? hasta, CancellationToken ct)
    {
        var leidos = new List<TrabajoControlPrint>();
        var (total, saltar, paginas) = (0, 0, 0);
        bool completa;
        while (true)
        {
            var pagina = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint
            {
                Cobro = CobroControlPrint.PorCobrar,
                SoloCobrables = true,
                DesdeUtc = desde,
                HastaUtc = hasta,
                Limite = Pagina,
                Saltar = saltar,
            }, ct);
            (total, saltar, paginas) = (pagina.Total, saltar + pagina.Trabajos.Count, paginas + 1);
            leidos.AddRange(pagina.Trabajos
                .Where(t => t.Cobro == CobroControlPrint.PorCobrar && t.Estado is EstadoTrabajoControlPrint.Analizado or EstadoTrabajoControlPrint.Impreso)
                .Select(t => SinElCodigo(cliente, t, ProtocoloControlPrint.Json)));
            completa = pagina.Trabajos.Count == 0 || saltar >= pagina.Total;
            if (completa || paginas >= MaxPaginas)
                break;
        }
        // Si entre una página y otra llegó uno nuevo, el último de una se repite al principio de la siguiente.
        return (leidos.DistinctBy(t => t.Id).OrderByDescending(t => t.Id).ToList(), total, completa);
    }

    private static DateTime? Mas(IReadOnlyList<TrabajoControlPrint> porCobrar) =>
        porCobrar.Count == 0 ? null : porCobrar.Min(t => TarjetaControlPrint.Utc(t.DetectadoUtc));

    private static HashSet<(long, long)> Claves(IEnumerable<TrabajoControlPrint> trabajos) => trabajos.Select(t => Clave(t.Id, t.DetectadoUtc)).ToHashSet();

    private async Task AvisarLecturaAsync(LecturaControlPrint lectura, CancellationToken ct)
    {
        if (AlLeer is not { } alLeer)
            return;
        try
        {
            await alLeer(lectura, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Lo que haga el servicio con la lectura no detiene la cola: él anota lo que falló.
        }
    }

    /// <summary>
    /// Si ya toca releer aunque Control Print no haya avisado nada: pasó <see cref="OpcionesColaControlPrint.ReleerCada"/> desde la
    /// última lectura, en tiempo de la PC (un segundo antes, para no esperar otra espera de cambios entera por milisegundos).
    /// </summary>
    private bool TocaReleer() =>
        System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _leidaEn)) >= _opciones!.ReleerCada - TimeSpan.FromSeconds(1);

    /// <summary>El principio del primer día que se ve, en hora de la tienda (el de hoy menos los días de antes).</summary>
    public DateTime DesdeUtc()
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora.UtcDateTime));
        return HoraMexico.AUtc(hoy.AddDays(-(Math.Max(1, _opciones!.Dias) - 1)).ToDateTime(TimeOnly.MinValue));
    }

    private async Task LeerAjustesAsync(ClienteControlPrint cliente, CancellationToken ct)
    {
        try
        {
            var ajustes = await cliente.AjustesAsync(ct);
            Estado = Estado with { SoloInvitado = ajustes.VisibilidadTrabajos == VisibilidadTrabajosControlPrint.CadaQuienLosSuyos };
        }
        catch (ErrorControlPrint ex) when (ex.Falla is FallaControlPrint.SinPermiso or FallaControlPrint.NoExiste or FallaControlPrint.Servidor)
        {
            // Sin sus ajustes se sigue como antes: solo sirven para avisar de «cada quien ve sus trabajos».
        }
    }

    private async Task LeerCostosAsync(ClienteControlPrint cliente, CancellationToken ct)
    {
        CostosDeControlPrint costos;
        try
        {
            costos = await cliente.CostosAsync(ct: ct);
        }
        catch (ErrorControlPrint ex) when (ex.Falla is FallaControlPrint.SinPermiso or FallaControlPrint.NoExiste or FallaControlPrint.Servidor)
        {
            return;
        }
        costos = costos with { Costos = SinElCodigo(cliente, costos.Costos, CostSettings.Json) };
        if (!SirvenLosCostos(costos))
            return;
        Volatile.Write(ref _costos, costos);
        var json = JsonSerializer.Serialize(costos.Costos, CostSettings.Json);
        if (costos.DeAntes || json == _costosAvisados || AlCambiarCostos is not { } avisar)
            return;
        await avisar(costos.Costos, ct);
        _costosAvisados = json;
    }

    /// <summary>Los costos de fábrica de Control Print (los mismos que trae <see cref="CostSettings"/>, como en su versión 1.12).</summary>
    private static readonly string CostosDeFabrica = JsonSerializer.Serialize(new CostSettings(), CostSettings.Json);

    /// <summary>
    /// Si los costos de Control Print se pueden usar: los de fábrica no (2026-10-29). Un Control Print reinstalado arranca con ellos y
    /// reemplazarían los de la sucursal y su calibración; se avisa (<see cref="EstadoColaControlPrint.AvisoCostos"/>) y se sigue con
    /// los de antes.
    /// </summary>
    private bool SirvenLosCostos(CostosDeControlPrint costos)
    {
        var deFabrica = JsonSerializer.Serialize(costos.Costos, CostSettings.Json) == CostosDeFabrica;
        var aviso = deFabrica
            ? "Control Print tiene sus costos de fábrica (¿se reinstaló?): no se usan; se sigue cobrando con los de antes. Revisa Ajustes → "
              + "Costos en Control Print."
            : null;
        if (Estado.AvisoCostos != aviso)
            Estado = Estado with { AvisoCostos = aviso };
        return !deFabrica;
    }

    private void Contesto(ClienteControlPrint cliente)
    {
        if (Estado.Contesta != true || Estado.Problema is not null)
            Estado = Estado with
            {
                Contesta = true, Problema = null, Falla = null, Nombre = SinElCodigo(cliente, cliente.NombreServidor),
                VersionServidor = SinElCodigo(cliente, cliente.VersionServidor),
            };
    }

    private void NoContesto(string problema, FallaControlPrint falla) => Estado = Estado with { Contesta = false, Problema = problema, Falla = falla };

    // ── La huella fijada en el primer saludo ─────────────────────────────────────────────────────────────────────

    private sealed record HuellaFijada(string Huella, string Servidor, string Version, DateTimeOffset FijadaUtc);

    private string? HuellaGuardada()
    {
        if (_opciones?.ArchivoHuella is not { } archivo || !File.Exists(archivo))
            return null;
        try
        {
            return JsonSerializer.Deserialize<HuellaFijada>(File.ReadAllText(archivo)) is { Huella: { } huella } ? ProtocoloControlPrint.LeerHuella(huella) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void GuardarHuella(ClienteControlPrint cliente)
    {
        if (_opciones?.ArchivoHuella is not { } archivo || string.IsNullOrEmpty(cliente.Huella))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivo))!);
            File.WriteAllText(archivo, JsonSerializer.Serialize(new HuellaFijada(ProtocoloControlPrint.HuellaLegible(cliente.Huella),
                SinElCodigo(cliente, cliente.NombreServidor), SinElCodigo(cliente, cliente.VersionServidor), Ahora),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin poder guardarla, se fija igual mientras corre el servicio; al volver a arrancar se fija otra vez en el saludo.
        }
    }

    // ── Lo que pide la caja: miniaturas, costos y la revisión antes de cobrar ────────────────────────────────────

    /// <summary>
    /// La miniatura (PNG) de un trabajo de la cola; vacía si no tiene, si no está en la cola o si Control Print no contesta. Se guarda
    /// en memoria: la misma no se vuelve a pedir. Se piden de dos en dos como máximo.
    /// </summary>
    public async Task<byte[]?> MiniaturaAsync(long id, CancellationToken ct = default)
    {
        if (Guardada(id) is { } guardada)
            return guardada.Png;
        if (Volatile.Read(ref _cliente) is not { } cliente || !Estado.PorCobrar.Any(t => t.Id == id))
            return null;
        await _pidiendoMiniaturas.WaitAsync(ct);
        try
        {
            if (Guardada(id) is { } yaLlego)
                return yaLlego.Png;
            var png = await cliente.MiniaturaAsync(id, ct);
            _miniaturas[id] = (png is { Length: > 0 } ? png : null, Ahora);
            return png is { Length: > 0 } ? png : null;
        }
        catch (ErrorControlPrint)
        {
            return null;
        }
        finally
        {
            _pidiendoMiniaturas.Release();
        }
    }

    private (byte[]? Png, DateTimeOffset LeidaUtc)? Guardada(long id) =>
        _miniaturas.TryGetValue(id, out var guardada) && (guardada.Png is not null || Ahora - guardada.LeidaUtc < SinMiniaturaPor) ? guardada : null;

    /// <summary>Si ya son muchas, se olvidan las de los trabajos que ya no están en la cola.</summary>
    private void OlvidarMiniaturas(IReadOnlyList<TrabajoControlPrint> porCobrar)
    {
        if (_miniaturas.Count <= MiniaturasEnMemoria)
            return;
        var siguen = porCobrar.Select(t => t.Id).ToHashSet();
        foreach (var id in _miniaturas.Keys.Where(id => !siguen.Contains(id)).ToList())
            _miniaturas.TryRemove(id, out _);
    }

    /// <summary>
    /// Los costos de Control Print (Ajustes → Costos), para ponerle precio como él a lo que imprime la caja 2: de memoria si están al
    /// día, si no se vuelven a leer; si no contesta, o si ahora son los de fábrica, los de antes (<see cref="CostosDeControlPrint.DeAntes"/>).
    /// Vacío si nunca se pudieron leer unos que sirvan (o no está configurado): entonces se cobra con los de la sucursal.
    /// </summary>
    public async Task<CostosDeControlPrint?> CostosAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _cliente) is { } cliente)
        {
            try
            {
                var costos = await cliente.CostosAsync(ct: ct);
                costos = costos with { Costos = SinElCodigo(cliente, costos.Costos, CostSettings.Json) };
                if (SirvenLosCostos(costos))
                {
                    Volatile.Write(ref _costos, costos);
                    return costos;
                }
            }
            catch (ErrorControlPrint)
            {
                // Se dan los de antes, si hay.
            }
        }
        return Volatile.Read(ref _costos) is { } antes ? antes with { DeAntes = true } : null;
    }

    /// <summary>
    /// Justo antes de cobrar: los trabajos del ticket se vuelven a leer en Control Print (solo leer, sin sus miniaturas, todos a la
    /// vez) con <paramref name="presupuesto"/> como lo más que se espera en total. Si alguno ya no está por cobrar allá (se cobró en
    /// la caja principal), cambió su precio o tiene hojas por revisar, se dice y no se cobra. Lo que contestó a tiempo cuenta aunque
    /// otra parte no (2026-10-29): solo lo que no contestó queda en <see cref="RevisionControlPrint.SinRespuesta"/> y, mientras, la
    /// cola deja de ofrecer sus tarjetas hasta que conteste.
    /// </summary>
    public async Task<RevisionControlPrint> RevisarAsync(IReadOnlyList<(long Id, long PrecioCentavos)> pedidos, TimeSpan presupuesto,
        CancellationToken ct = default)
    {
        var unicos = pedidos.DistinctBy(p => p.Id).ToList();
        if (_opciones is null)
            return new RevisionControlPrint([], [], [], "Este servicio no tiene configurado Control Print: quita sus impresiones del ticket.",
                unicos.Select(p => p.Id).ToList());
        if (Volatile.Read(ref _cliente) is not { } cliente)
            return new RevisionControlPrint([], [], [], Estado.Problema ?? NoContestaTodavia, unicos.Select(p => p.Id).ToList());
        LecturaParcial leidos;
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(presupuesto);
            leidos = await LeerTrabajosAsync(cliente, unicos.Select(p => (p.Id, DetectadoDe(p.Id))).ToList(), limite.Token);
        }
        ct.ThrowIfCancellationRequested();
        string? noContesta = null;
        if (leidos.SinRespuesta.Count > 0)
        {
            noContesta = leidos.Problema ?? $"Control Print no contestó a tiempo ({presupuesto.TotalSeconds:0.#} s) al revisar un cobro.";
            NoContesto(noContesta, leidos.Falla ?? FallaControlPrint.SinConexion);
        }
        var siguen = new List<TrabajoControlPrint>();
        var yaNo = new List<(long, string)>();
        var otroPrecio = new List<(long, long)>();
        foreach (var (id, precio) in unicos.Where(p => !leidos.SinRespuesta.Contains(p.Id)))
        {
            var trabajo = leidos.Trabajos.GetValueOrDefault(id);
            if (trabajo is null)
                yaNo.Add((id, Estado.SoloInvitado
                    ? $"#{id} no se puede leer en Control Print sin sesión (está en «cada quien ve sus trabajos»)"
                    : $"#{id} ya no está en Control Print"));
            else if (trabajo.Cobro == CobroControlPrint.Cobrado)
                yaNo.Add((id, $"#{id} ya se cobró en Control Print{(string.IsNullOrWhiteSpace(trabajo.CobroPor) ? "" : $" ({trabajo.CobroPor.Trim()})")}"));
            else if (trabajo.Cobro == CobroControlPrint.NoVendido)
                yaNo.Add((id, $"#{id} se marcó como no vendida en Control Print"));
            else if (trabajo.Cobro != CobroControlPrint.PorCobrar || trabajo.Estado is not (EstadoTrabajoControlPrint.Analizado or EstadoTrabajoControlPrint.Impreso))
                yaNo.Add((id, $"#{id} ya no se puede cobrar en Control Print"));
            else if (TarjetaControlPrint.RevisarHojas(trabajo) is not null)
                yaNo.Add((id, HojasPorRevisar(id)));
            else if (TarjetaControlPrint.Centavos(TarjetaControlPrint.PrecioACobrar(trabajo)) is var ahora && ahora != precio)
                otroPrecio.Add((id, ahora));
            else
                siguen.Add(trabajo);
        }
        return new RevisionControlPrint(siguen, yaNo, otroPrecio, noContesta, leidos.SinRespuesta);
    }

    /// <summary>
    /// «Revisar hojas»: la impresora contó menos de las que se mandaron. La caja 2 solo puede cobrarla completa, así que no la cobra
    /// hasta que en la caja principal se resuelva («cobrar lo que salió» o «no vendida»; 2026-10-29).
    /// </summary>
    public static string HojasPorRevisar(long id) =>
        $"#{id} tiene hojas por revisar (la impresora contó menos): se resuelve en la caja principal, en Control Print, y después se cobra aquí";

    /// <summary>La hora en que Control Print vio ese trabajo, si se sabe (lo que se leyó en los días que se ven).</summary>
    private DateTime? DetectadoDe(long id) =>
        _vistos.TryGetValue(id, out var hora) ? hora
        : Estado.PorCobrar.FirstOrDefault(t => t.Id == id) is { } trabajo ? TarjetaControlPrint.Utc(trabajo.DetectadoUtc) : null;

    /// <summary>
    /// Lo que se leyó de unos trabajos (<see cref="LeerTrabajosAsync(IReadOnlyCollection{ValueTuple{long, DateTime?}}, CancellationToken)"/>):
    /// los que contestaron (vacío el que ya no está) y los que no, con por qué.
    /// </summary>
    public sealed record LecturaParcial(IReadOnlyDictionary<long, TrabajoControlPrint?> Trabajos, IReadOnlyList<long> SinRespuesta, string? Problema,
        FallaControlPrint? Falla);

    /// <summary>
    /// Unos trabajos de Control Print como están ahora, sin sus miniaturas (solo leer): los de horas cercanas en una búsqueda por su
    /// rango de horas (está indexado allá) y, si no se sabe su hora, por su número; todas a la vez. Vacío el que ya no está. Lo que no
    /// contestó (o no a tiempo, si <paramref name="ct"/> se cancela) queda en <see cref="LecturaParcial.SinRespuesta"/>: lo demás sirve.
    /// </summary>
    public Task<LecturaParcial> LeerTrabajosAsync(IReadOnlyCollection<(long Id, DateTime? DetectadoUtc)> trabajos, CancellationToken ct = default) =>
        Volatile.Read(ref _cliente) is { } cliente
            ? LeerTrabajosAsync(cliente, trabajos, ct)
            : Task.FromResult(new LecturaParcial(new Dictionary<long, TrabajoControlPrint?>(), trabajos.Select(t => t.Id).Distinct().ToList(),
                Estado.Problema ?? NoContestaTodavia, FallaControlPrint.SinConexion));

    private static async Task<LecturaParcial> LeerTrabajosAsync(ClienteControlPrint cliente, IReadOnlyCollection<(long Id, DateTime? DetectadoUtc)> trabajos,
        CancellationToken ct)
    {
        // Cada búsqueda, con los trabajos que alcanza: los de horas cercanas (a Juntos o menos), y cada uno sin hora, por su número.
        var grupos = new List<(BusquedaTrabajosControlPrint Busqueda, List<long> Ids)>();
        var conHora = trabajos.Where(t => t.DetectadoUtc is not null)
            .Select(t => (t.Id, Hora: TarjetaControlPrint.Utc(t.DetectadoUtc!.Value))).OrderBy(t => t.Hora).ToList();
        for (var i = 0; i < conHora.Count;)
        {
            var fin = i;
            while (fin + 1 < conHora.Count && conHora[fin + 1].Hora - conHora[fin].Hora <= Juntos)
                fin++;
            grupos.Add((new BusquedaTrabajosControlPrint { DesdeUtc = conHora[i].Hora.AddSeconds(-1), HastaUtc = conHora[fin].Hora.AddSeconds(1), Limite = 0 },
                conHora.Skip(i).Take(fin - i + 1).Select(t => t.Id).ToList()));
            i = fin + 1;
        }
        grupos.AddRange(trabajos.Where(t => t.DetectadoUtc is null)
            .Select(t => (new BusquedaTrabajosControlPrint { Texto = $"#{t.Id}", Limite = 50 }, new List<long> { t.Id })));
        var busquedas = grupos.Select(g => (g.Ids, Tarea: cliente.BuscarTrabajosAsync(g.Busqueda, ct))).ToList();
        try
        {
            await Task.WhenAll(busquedas.Select(b => b.Tarea));
        }
        catch (Exception)
        {
            // Cada búsqueda dice si contestó: lo que sí, sirve; lo demás queda sin respuesta.
        }
        var resultado = new Dictionary<long, TrabajoControlPrint?>();
        var sinRespuesta = new List<long>();
        string? problema = null;
        FallaControlPrint? falla = null;
        foreach (var (ids, tarea) in busquedas)
        {
            if (!tarea.IsCompletedSuccessfully)
            {
                sinRespuesta.AddRange(ids);
                if (tarea.Exception?.InnerException is ErrorControlPrint error)
                    (problema, falla) = (problema ?? error.Message, falla ?? error.Falla);
                continue;
            }
            foreach (var id in ids)
            {
                var detectado = trabajos.First(t => t.Id == id).DetectadoUtc;
                var trabajo = tarea.Result.Trabajos.FirstOrDefault(t => t.Id == id && (detectado is null || Clave(t.Id, t.DetectadoUtc) == Clave(id, detectado.Value)));
                resultado[id] = trabajo is null ? null : SinElCodigo(cliente, trabajo, ProtocoloControlPrint.Json);
            }
        }
        return new LecturaParcial(resultado, sinRespuesta.Distinct().ToList(), problema, falla);
    }

    private const string NoContestaTodavia = "Control Print todavía no contesta: no se puede revisar que sigan por cobrar.";

    // ── Nunca el código de conexión ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lo que llega de Control Print, sin el código de conexión (como recopilar-control-print, revisión del #143): un texto que lo
    /// lleve, escrito como sea (<see cref="ClienteControlPrint.LlevaElCodigo"/>), se queda vacío. Así no sale en F2 ni se guarda: ni
    /// en lo que la caja recuerda que cobró, ni en los tickets en espera, ni en los costos de la sucursal, ni en la huella fijada ni
    /// en el registro del servicio.
    /// </summary>
    private static T SinElCodigo<T>(ClienteControlPrint cliente, T valor, JsonSerializerOptions opciones) where T : class
    {
        var nodo = JsonSerializer.SerializeToNode(valor, opciones);
        return Vaciar(nodo, cliente.LlevaElCodigo) ? nodo.Deserialize<T>(opciones) ?? valor : valor;
    }

    private static string SinElCodigo(ClienteControlPrint cliente, string texto) => cliente.LlevaElCodigo(texto) ? "" : texto;

    /// <summary>Deja vacíos los textos que llevan el código; si quitó alguno, true.</summary>
    private static bool Vaciar(JsonNode? nodo, Func<string, bool> llevaElCodigo)
    {
        var quito = false;
        switch (nodo)
        {
            case JsonObject objeto:
                foreach (var nombre in objeto.Select(p => p.Key).ToList())
                {
                    if (LlevaElCodigo(objeto[nombre], llevaElCodigo))
                        (objeto[nombre], quito) = ("", true);
                    else
                        quito |= Vaciar(objeto[nombre], llevaElCodigo);
                }
                break;
            case JsonArray arreglo:
                for (var i = 0; i < arreglo.Count; i++)
                {
                    if (LlevaElCodigo(arreglo[i], llevaElCodigo))
                        (arreglo[i], quito) = ("", true);
                    else
                        quito |= Vaciar(arreglo[i], llevaElCodigo);
                }
                break;
        }
        return quito;
    }

    private static bool LlevaElCodigo(JsonNode? nodo, Func<string, bool> llevaElCodigo) =>
        nodo is JsonValue valor && valor.TryGetValue<string>(out var texto) && llevaElCodigo(texto);

    public void Dispose()
    {
        Interlocked.Exchange(ref _cliente, null)?.Dispose();
        _pidiendoMiniaturas.Dispose();
    }
}
