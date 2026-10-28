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

    /// <summary>Lo que se espera a que Control Print avise un cambio (su espera larga, hasta 25 s).</summary>
    public TimeSpan EsperaCambios { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Lo que se espera después de un aviso: un trabajo cambia varias veces seguidas (detectado, analizado, impreso).</summary>
    public TimeSpan Juntar { get; init; } = TimeSpan.FromMilliseconds(600);
}

/// <summary>Cómo está la cola de Control Print: lo último que se leyó y si contesta.</summary>
/// <param name="Contesta">Vacío mientras se conecta por primera vez.</param>
/// <param name="Problema">Por qué no contesta o qué falta, para mostrarlo tal cual (nunca lleva el código).</param>
/// <param name="PorCobrar">Los por cobrar de los días que se ven, los más nuevos primero (hasta <see cref="ColaControlPrint.Limite"/>).</param>
/// <param name="Total">Cuántos por cobrar hay en esos días (pueden ser más de los que se leyeron).</param>
/// <param name="SoloInvitado">Control Print está en «cada quien ve sus trabajos»: sin sesión solo se ven los de «Sin sesión».</param>
public sealed record EstadoColaControlPrint(
    bool Configurado, string? Servidor, int Puerto, bool TieneCodigo, bool? Contesta, string? Problema, FallaControlPrint? Falla, string? Nombre,
    string? VersionServidor, string? Huella, bool HuellaConfigurada, DateTimeOffset? LeidaUtc, int Lecturas, IReadOnlyList<TrabajoControlPrint> PorCobrar,
    int Total, bool SoloInvitado);

/// <summary>Lo que dijo Control Print, justo antes de cobrar, de los trabajos que van en un ticket.</summary>
/// <param name="Siguen">Siguen por cobrar allá y con el mismo precio: se pueden cobrar.</param>
/// <param name="YaNo">Ya no están por cobrar allá (se cobraron en la caja principal, se marcaron no vendidas, se cancelaron).</param>
/// <param name="OtroPrecio">Les cambiaron el precio allá (un precio especial, por ejemplo): el de ahora.</param>
/// <param name="NoContesta">Control Print no contestó: no se sabe si siguen por cobrar y no se cobran.</param>
public sealed record RevisionControlPrint(
    IReadOnlyList<TrabajoControlPrint> Siguen, IReadOnlyList<(long Id, string Porque)> YaNo, IReadOnlyList<(long Id, long PrecioCentavos)> OtroPrecio,
    string? NoContesta)
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
    /// <summary>Cuántos por cobrar se leen como máximo (los más nuevos).</summary>
    public const int Limite = 200;

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
            if (antes.Contesta != value.Contesta || antes.Problema != value.Problema)
                CambioDeEstado?.Invoke(value);
        }
    }

    /// <summary>Cuando empieza o deja de contestar, o cambia por qué no contesta (para el registro del servicio).</summary>
    public event Action<EstadoColaControlPrint>? CambioDeEstado;

    /// <summary>
    /// Cuando se leen los costos de Control Print y son otros que los de la vez anterior (para guardarlos como los de la sucursal:
    /// con ellos se pone precio a lo que imprime la caja 2, también si Control Print no contesta al arrancar).
    /// </summary>
    public Func<CostSettings, CancellationToken, Task>? AlCambiarCostos { get; set; }

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
                    if (nuevas.Trabajos != vistas.Trabajos || nuevas.Usuarios != vistas.Usuarios || reinicio)
                        leer = true;
                    if (nuevas.Configuracion != vistas.Configuracion || reinicio)
                        await LeerAjustesAsync(cliente, ct);
                    vistas = nuevas;
                    // Los costos: los vuelve a pedir si cambió su configuración o pasaron 5 minutos (si no, salen de memoria).
                    await LeerCostosAsync(cliente, ct);
                    if (leer)
                        espera = _opciones.Juntar;
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
        var pagina = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint
        {
            Cobro = CobroControlPrint.PorCobrar,
            SoloCobrables = true,
            DesdeUtc = DesdeUtc(),
            Limite = Limite,
        }, ct);
        var porCobrar = pagina.Trabajos
            .Where(t => t.Cobro == CobroControlPrint.PorCobrar && t.Estado is EstadoTrabajoControlPrint.Analizado or EstadoTrabajoControlPrint.Impreso)
            .OrderByDescending(t => t.Id)
            .Select(t => SinElCodigo(cliente, t, ProtocoloControlPrint.Json))
            .ToList();
        Contesto(cliente);
        Estado = Estado with { LeidaUtc = Ahora, Lecturas = Estado.Lecturas + 1, PorCobrar = porCobrar, Total = Math.Max(pagina.Total, porCobrar.Count) };
        OlvidarMiniaturas(porCobrar);
    }

    /// <summary>El principio del primer día que se ve, en hora de la tienda (el de hoy menos los días de antes).</summary>
    private DateTime DesdeUtc()
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
        Volatile.Write(ref _costos, costos);
        var json = JsonSerializer.Serialize(costos.Costos, CostSettings.Json);
        if (costos.DeAntes || json == _costosAvisados || AlCambiarCostos is not { } avisar)
            return;
        await avisar(costos.Costos, ct);
        _costosAvisados = json;
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
    /// día, si no se vuelven a leer; si no contesta, los de antes (<see cref="CostosDeControlPrint.DeAntes"/>). Vacío si nunca se
    /// pudieron leer (o no está configurado).
    /// </summary>
    public async Task<CostosDeControlPrint?> CostosAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _cliente) is { } cliente)
        {
            try
            {
                var costos = await cliente.CostosAsync(ct: ct);
                costos = costos with { Costos = SinElCodigo(cliente, costos.Costos, CostSettings.Json) };
                Volatile.Write(ref _costos, costos);
                return costos;
            }
            catch (ErrorControlPrint)
            {
                // Se dan los de antes, si hay.
            }
        }
        return Volatile.Read(ref _costos) is { } antes ? antes with { DeAntes = true } : null;
    }

    /// <summary>
    /// Justo antes de cobrar: cada trabajo del ticket se vuelve a leer en Control Print (solo leer). Si ya no está por cobrar allá (se
    /// cobró en la caja principal) o cambió su precio, se dice y no se cobra; si Control Print no contesta, tampoco: nunca se cobra
    /// dos veces.
    /// </summary>
    public async Task<RevisionControlPrint> RevisarAsync(IReadOnlyList<(long Id, long PrecioCentavos)> pedidos, CancellationToken ct = default)
    {
        if (_opciones is null)
            return new RevisionControlPrint([], [], [], "Este servicio no tiene configurado Control Print: quita sus impresiones del ticket.");
        if (Volatile.Read(ref _cliente) is not { } cliente)
            return new RevisionControlPrint([], [], [], Estado.Problema ?? NoContestaTodavia);
        var siguen = new List<TrabajoControlPrint>();
        var yaNo = new List<(long, string)>();
        var otroPrecio = new List<(long, long)>();
        foreach (var (id, precio) in pedidos.DistinctBy(p => p.Id))
        {
            TrabajoControlPrint? trabajo;
            try
            {
                trabajo = await cliente.TrabajoAsync(id, ct) is { } leido ? SinElCodigo(cliente, leido, ProtocoloControlPrint.Json) : null;
            }
            catch (ErrorControlPrint ex) when (ex.Falla == FallaControlPrint.SinPermiso)
            {
                yaNo.Add((id, $"#{id} no se puede leer en Control Print sin sesión (está en «cada quien ve sus trabajos»)"));
                continue;
            }
            catch (ErrorControlPrint ex)
            {
                return new RevisionControlPrint([], [], [], ex.Message);
            }
            if (trabajo is null)
                yaNo.Add((id, $"#{id} ya no está en Control Print"));
            else if (trabajo.Cobro == CobroControlPrint.Cobrado)
                yaNo.Add((id, $"#{id} ya se cobró en Control Print{(string.IsNullOrWhiteSpace(trabajo.CobroPor) ? "" : $" ({trabajo.CobroPor.Trim()})")}"));
            else if (trabajo.Cobro == CobroControlPrint.NoVendido)
                yaNo.Add((id, $"#{id} se marcó como no vendida en Control Print"));
            else if (trabajo.Cobro != CobroControlPrint.PorCobrar || trabajo.Estado is not (EstadoTrabajoControlPrint.Analizado or EstadoTrabajoControlPrint.Impreso))
                yaNo.Add((id, $"#{id} ya no se puede cobrar en Control Print"));
            else if (TarjetaControlPrint.Centavos(TarjetaControlPrint.PrecioACobrar(trabajo)) is var ahora && ahora != precio)
                otroPrecio.Add((id, ahora));
            else
                siguen.Add(trabajo);
            if (trabajo?.Miniatura is { Length: > 0 } png)
                _miniaturas[id] = (png, Ahora);
        }
        return new RevisionControlPrint(siguen, yaNo, otroPrecio, null);
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
