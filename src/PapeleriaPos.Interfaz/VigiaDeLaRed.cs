using System.Globalization;
using System.Net;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Qué revisa el vigía de la red (<see cref="VigiaDeLaRed"/>) y cada cuánto.</summary>
public sealed class OpcionesDelVigia
{
    /// <summary>De quién es la bitácora, para su primera línea: «esta caja», «la caja principal».</summary>
    public string Quien { get; init; } = "esta PC";

    /// <summary>Lo que dice la última línea al cerrar bien: «se cerró la caja», «se detuvo el servicio».</summary>
    public string AlTerminar { get; init; } = "se cerró";

    /// <summary>
    /// A qué se conecta para saber si hay internet (TCP, sin mandar nada; por IP, sin depender del DNS del router). Vacío: no
    /// lo revisa (la caja: lo revisa la caja principal y se lo dice en el latido).
    /// </summary>
    public IReadOnlyList<(string Host, int Puerto)> Internet { get; init; } = [];

    /// <summary>
    /// Un nombre que el router contesta solo (el Tenda, «tendawifi.com»): si su DNS no contesta ni eso, está trabado (docs/notas.md).
    /// Vacío: no se pregunta.
    /// </summary>
    public string? NombreDelRouter { get; init; } = "tendawifi.com";

    /// <summary>
    /// Si revisa el router. La caja principal con su propia caja: lo revisa su servicio (no se anota dos veces en la misma PC).
    /// </summary>
    public bool RevisarRouter { get; init; } = true;

    /// <summary>Otros equipos a los que se les hace ping (la impresora de tickets, el grabador, el módem): qué es y su IP.</summary>
    public IReadOnlyList<(string Que, IPAddress Ip)> Equipos { get; init; } = [];

    /// <summary>Cada cuánto revisa.</summary>
    public TimeSpan Cada { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sin internet revisa más seguido: así se sabe pronto que volvió.</summary>
    public TimeSpan CadaSinInternet { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Cada cuánto anota el resumen (los latidos, el router, el Wi-Fi, la difusión).</summary>
    public TimeSpan Resumen { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Cuánto lleva prendida la PC (para anotar a qué hora arrancó); sin esto, el de Windows.</summary>
    public Func<TimeSpan>? Encendida { get; init; }

    /// <summary>La caja principal (en la caja): se mira la tarjeta de su red. Vacía: la que tiene router.</summary>
    public Func<IPAddress?>? Hacia { get; init; }

    /// <summary>Lo que se anota al empezar, después de la primera línea: lo configurado por nombre o con WSD.</summary>
    public Func<IEnumerable<string>>? AlEmpezar { get; init; }

    /// <summary>Lo que se revisa además en cada vuelta (en la caja principal: qué cajas dejaron de latir).</summary>
    public Func<BitacoraDeRed, CancellationToken, Task>? Ademas { get; init; }

    /// <summary>Las de internet de siempre: dos servidores muy conocidos, por su IP, en el puerto de HTTPS.</summary>
    public static readonly IReadOnlyList<(string Host, int Puerto)> InternetPorOmision = [("1.1.1.1", 443), ("8.8.8.8", 443)];
}

/// <summary>
/// El vigía de la red (la red de la tienda en el POS, docs/arquitectura.md, «La red de la matriz»): cada medio minuto revisa
/// la red de esta PC y lo anota en la bitácora de red (<see cref="BitacoraDeRed"/>), solo cuando algo cambia. Lo usan las dos
/// puntas:
/// <list type="bullet">
/// <item>la caja principal (el servicio): si hay internet (una conexión a dos servidores conocidos, con 2 s de plazo) y si
/// contesta su router; lo dice en su estado (<see cref="Estado"/>, en el latido de las cajas) para la franja «Sin internet»;</item>
/// <item>cada caja: lo que dice su latido (buena, lenta o caída, con lo que tarda), su router, la impresora de tickets (solo
/// ping: una conexión al 9100 le estorbaría a eleventa) y lo que la caja principal dice del internet.</item>
/// </list>
/// Las dos anotan su tarjeta (cable o Wi-Fi, la IP y de dónde viene, el router y, en Windows, el Wi-Fi con su señal y la MAC
/// del router) al empezar y cada vez que cambia, y cada 15 minutos un resumen: lo que tardaron los latidos y el router, el
/// Wi-Fi y cuánta difusión llegó (una tormenta de tráfico). Es lo que docs/notas.md pide juntar en la próxima caída.
/// <para>
/// Para no confundir un ping perdido con una caída, el router, los equipos y el internet se dan por perdidos con dos revisiones
/// seguidas sin respuesta, y vuelven con la primera que contesta. Nada de esto toca la venta: corre aparte, con plazos cortos.
/// </para>
/// </summary>
public sealed class VigiaDeLaRed
{
    /// <summary>Lo que se espera un ping (al router, a un equipo).</summary>
    public static readonly TimeSpan EsperaPing = TimeSpan.FromSeconds(1);

    /// <summary>Lo que se espera a un servidor de internet.</summary>
    public static readonly TimeSpan EsperaInternet = TimeSpan.FromSeconds(2);

    /// <summary>Cuántas revisiones seguidas sin respuesta para darlo por perdido.</summary>
    public const int FallosParaPerder = 2;

    /// <summary>Tanta difusión por segundo ya no es normal en una red chica: puede ser una tormenta de tráfico.</summary>
    public const double DifusionDeTormenta = 1000;

    private readonly BitacoraDeRed _bitacora;
    private readonly IRedDelEquipo _equipo;
    private readonly IPruebasDeRed _pruebas;
    private readonly OpcionesDelVigia _opciones;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _paso = new(1, 1);
    private readonly Lock _candado = new();

    private FotoDeLaRed? _foto;
    private Seguimiento _router;
    private bool? _dnsRouter;
    private readonly Dictionary<string, Seguimiento> _equipos = new(StringComparer.Ordinal);
    private Seguimiento _internet;
    private DateTimeOffset? _internetDesde;
    private bool _tormenta;
    private (long Difusion, DateTimeOffset Cuando)? _difusionAntes;

    // Lo del latido de la caja.
    private EstadoRed? _principal;
    private readonly Queue<TimeSpan> _ultimosLatidos = new();
    private bool? _internetDeLaPrincipal;

    // Lo del resumen de cada 15 minutos.
    private DateTimeOffset _resumenDesde;
    private readonly List<double> _latidosMs = [];
    private int _latidosSinRespuesta;
    private readonly List<double> _routerMs = [];
    private int _routerSinRespuesta;
    private (long Difusion, long Descartados)? _contadoresDesde;

    /// <summary>Si contesta o no, con cuántas veces seguidas que no, y si ya contestó alguna vez.</summary>
    private struct Seguimiento
    {
        public bool? Contesta;
        public int Fallos;
        public bool YaContesto;
    }

    public VigiaDeLaRed(BitacoraDeRed bitacora, IRedDelEquipo equipo, IPruebasDeRed pruebas, OpcionesDelVigia? opciones = null, TimeProvider? reloj = null)
    {
        _bitacora = bitacora;
        _equipo = equipo;
        _pruebas = pruebas;
        _opciones = opciones ?? new OpcionesDelVigia();
        _reloj = reloj ?? TimeProvider.System;
        _resumenDesde = _reloj.GetUtcNow();
    }

    public BitacoraDeRed Bitacora => _bitacora;

    /// <summary>Si hay internet (con dos revisiones seguidas sin respuesta, no); vacío si no se revisa o todavía no se sabe.</summary>
    public bool? Internet
    {
        get
        {
            lock (_candado)
                return _internet.Contesta;
        }
    }

    /// <summary>Si contesta el router; vacío si no tiene o todavía no se sabe.</summary>
    public bool? Router
    {
        get
        {
            lock (_candado)
                return _router.Contesta;
        }
    }

    /// <summary>Lo último que se leyó de la red de esta PC.</summary>
    public FotoDeLaRed? Equipo
    {
        get
        {
            lock (_candado)
                return _foto;
        }
    }

    /// <summary>Lo que dice la caja principal en su estado (<see cref="EstadoServicioDto.Red"/>).</summary>
    public RedDeLaPrincipalDto Estado
    {
        get
        {
            lock (_candado)
                return new RedDeLaPrincipalDto(_internet.Contesta, _router.Contesta, _internetDesde?.UtcDateTime);
        }
    }

    /// <summary>Cambió el internet o el router: quien lo enseña, que lo vuelva a pintar.</summary>
    public event Action? Cambio;

    /// <summary>Cuánto espera para la siguiente revisión: sin internet, menos.</summary>
    public TimeSpan SiguienteEspera => Internet == false ? _opciones.CadaSinInternet : _opciones.Cada;

    /// <summary>La primera línea de la bitácora, con la hora en que arrancó la PC (lo que tiene prendida, de Windows).</summary>
    public void Empezar()
    {
        var encendida = _opciones.Encendida?.Invoke() ?? TimeSpan.FromMilliseconds(Environment.TickCount64);
        _bitacora.Empezar(_opciones.Quien, _reloj.GetLocalNow().DateTime - encendida);
        foreach (var aviso in _opciones.AlEmpezar?.Invoke() ?? [])
            _bitacora.Anotar(aviso);
    }

    /// <summary>
    /// Revisa cada <see cref="OpcionesDelVigia.Cada"/> hasta que se cierre (la caja o el servicio): la primera línea al empezar y
    /// la última al cerrar. Una revisión que falla no lo detiene.
    /// </summary>
    public async Task VigilarAsync(CancellationToken ct)
    {
        Empezar();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RevisarAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una revisión que falla (algo raro de la tarjeta) no detiene al vigía: la siguiente vuelve a intentar.
                }
                await Task.Delay(SiguienteEspera, _reloj, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Se cerró.
        }
        finally
        {
            _bitacora.Terminar(_opciones.AlTerminar);
        }
    }

    /// <summary>Una revisión: la red de esta PC, el router, los equipos, el internet y, si ya toca, el resumen.</summary>
    public async Task RevisarAsync(CancellationToken ct = default)
    {
        await _paso.WaitAsync(ct);
        try
        {
            var foto = _equipo.Leer(_opciones.Hacia?.Invoke());
            AnotarLaPc(foto);
            // A la vez: la revisión entera tarda lo de la más lenta (2 s sin internet), no la suma.
            var router = _opciones.RevisarRouter && foto.Router is { } ip ? RouterAsync(ip, ct) : Task.FromResult<(TimeSpan?, bool?)>((null, null));
            var equipos = _opciones.Equipos.Select(e => _pruebas.PingAsync(e.Ip, EsperaPing, ct)).ToList();
            var internet = _opciones.Internet.Count > 0 ? InternetAsync(ct) : null;
            await Task.WhenAll([router, .. equipos, .. internet is null ? Array.Empty<Task>() : [internet]]);

            var (alRouter, dns) = await router;
            if (_opciones.RevisarRouter)
                RouterRevisado(foto, alRouter, dns);
            for (var i = 0; i < equipos.Count; i++)
                EquipoRevisado(_opciones.Equipos[i].Que, _opciones.Equipos[i].Ip, await equipos[i]);
            if (internet is not null && await internet is var (servidor, tardo))
                InternetRevisado(servidor, tardo, foto);
            ResumenSiToca(foto);
            if (_opciones.Ademas is { } ademas)
                await ademas(_bitacora, ct);
        }
        finally
        {
            _paso.Release();
        }
    }

    // ── Esta PC ─────────────────────────────────────────────────────────────

    private void AnotarLaPc(FotoDeLaRed foto)
    {
        FotoDeLaRed? antes;
        lock (_candado)
            (antes, _foto) = (_foto, foto);
        if (antes is null)
        {
            _bitacora.Anotar($"Esta PC: {foto.Texto}.");
            if (foto.Autoasignada)
                _bitacora.Anotar("Esta PC no tiene una IP del router (169.254.x.x): el router no le dio dirección.");
            AnotarDifusion(foto);
            return;
        }
        if (antes.Adaptador != foto.Adaptador || antes.Tipo != foto.Tipo)
            _bitacora.Anotar($"Esta PC cambió de conexión: {antes.Conexion} → {foto.Conexion}.");
        else if (antes.Wifi != foto.Wifi && foto.Tipo == TipoDeConexion.WiFi)
            _bitacora.Anotar($"Esta PC cambió de Wi-Fi: {antes.Conexion} → {foto.Conexion}.");
        if (!Equals(antes.Ip, foto.Ip) || antes.Prefijo != foto.Prefijo || antes.PorDhcp != foto.PorDhcp)
        {
            _bitacora.Anotar($"Esta PC cambió de IP: {antes.TextoIp} → {foto.TextoIp}.");
            if (foto.Autoasignada)
                _bitacora.Anotar("Esta PC no tiene una IP del router (169.254.x.x): el router no le dio dirección.");
        }
        if (!Equals(antes.Router, foto.Router))
            _bitacora.Anotar($"Esta PC cambió de router: {(antes.Router?.ToString() ?? "ninguno")} → {(foto.Router?.ToString() ?? "ninguno")}.");
        else if (antes.MacDelRouter is { } macAntes && foto.MacDelRouter is { } mac && macAntes != mac)
            _bitacora.Anotar($"El router {foto.Router} cambió de MAC: {macAntes} → {mac} (otro aparato contesta con su IP).");
        if (foto.Tipo == TipoDeConexion.WiFi && antes.Tipo == TipoDeConexion.WiFi && Banda(antes.Senal) != Banda(foto.Senal) && foto.Senal is { } senal)
            _bitacora.Anotar($"La señal del Wi-Fi {(senal < (antes.Senal ?? 0) ? "bajó" : "subió")} a {senal} % ({Banda(foto.Senal)}).");
        AnotarDifusion(foto);
    }

    /// <summary>La señal en tres: buena (60 % o más), regular o mala (menos de 30 %). Solo se anota cuando cambia de una a otra.</summary>
    private static string? Banda(int? senal) => senal switch
    {
        null => null,
        >= 60 => "buena",
        >= 30 => "regular",
        _ => "mala",
    };

    /// <summary>Si de una revisión a otra llegó demasiada difusión (una tormenta de tráfico), y cuando se calma.</summary>
    private void AnotarDifusion(FotoDeLaRed foto)
    {
        if (foto.Difusion is not { } difusion)
            return;
        var ahora = _reloj.GetUtcNow();
        (long Difusion, DateTimeOffset Cuando)? antes;
        lock (_candado)
            (antes, _difusionAntes) = (_difusionAntes, (difusion, ahora));
        if (antes is not { } a || ahora - a.Cuando < TimeSpan.FromSeconds(1) || difusion < a.Difusion)
            return;
        var porSegundo = (difusion - a.Difusion) / (ahora - a.Cuando).TotalSeconds;
        if (!_tormenta && porSegundo >= DifusionDeTormenta)
        {
            _tormenta = true;
            _bitacora.Anotar($"Llega mucha difusión a esta PC: {porSegundo.ToString("#,##0", CultureInfo.InvariantCulture)} paquetes por segundo (puede ser una tormenta de tráfico).");
        }
        else if (_tormenta && porSegundo < DifusionDeTormenta / 5)
        {
            _tormenta = false;
            _bitacora.Anotar($"La difusión volvió a lo normal: {porSegundo.ToString("#,##0", CultureInfo.InvariantCulture)} paquetes por segundo.");
        }
    }

    // ── El router y los equipos ─────────────────────────────────────────────

    /// <summary>Ping al router y, si no contesta, su página (puerto 80); y su DNS, con un nombre que contesta él solo.</summary>
    private async Task<(TimeSpan? Tardo, bool? Dns)> RouterAsync(IPAddress router, CancellationToken ct)
    {
        var dns = _opciones.NombreDelRouter is { } nombre ? _pruebas.PreguntarDnsAsync(router, nombre, EsperaPing, ct) : null;
        var tardo = await _pruebas.PingAsync(router, EsperaPing, ct) ?? await _pruebas.ConectarAsync(router.ToString(), 80, EsperaPing, ct);
        bool? contestoElDns = dns is null ? null : await dns is not null;
        return (tardo, contestoElDns);
    }

    private void RouterRevisado(FotoDeLaRed foto, TimeSpan? tardo, bool? dns)
    {
        if (foto.Router is not { } router)
        {
            // Sin router (se desconectó el cable, se cayó el Wi-Fi): si contestaba, se perdió.
            if (Router is true)
                Perder(ref _router, "el router", "esta PC se quedó sin red", inmediato: true);
            return;
        }
        lock (_candado)
        {
            if (tardo is { } t)
                _routerMs.Add(t.TotalMilliseconds);
            else
                _routerSinRespuesta++;
        }
        if (tardo is { } enCuanto)
            Contesto(ref _router, "el router", $"{router} contesta en {RedLocal.Ms(enCuanto)}");
        else
            Perder(ref _router, "el router", $"{router} no contesta el ping ni su página (puerto 80) en {Segundos(EsperaPing)}");
        if (dns is { } contesta && contesta != _dnsRouter)
        {
            var antes = _dnsRouter;
            _dnsRouter = contesta;
            if (!contesta)
                _bitacora.Anotar($"El DNS del router no contesta («{_opciones.NombreDelRouter}» en {Segundos(EsperaPing)}): está trabado o se reinicia.");
            else if (antes is false)
                _bitacora.Anotar("El DNS del router volvió a contestar.");
        }
    }

    /// <summary>
    /// Un equipo (la impresora de tickets, la otra caja): como el router, salvo que uno que no ha contestado ni una vez desde que
    /// empezó la bitácora no cuenta como caído (el firewall de Windows no deja contestar el ping si no se le permite,
    /// docs/notas.md): se dice una vez y, si un día contesta, desde ahí cuenta.
    /// </summary>
    private void EquipoRevisado(string que, IPAddress ip, TimeSpan? tardo)
    {
        Seguimiento estado;
        lock (_candado)
            estado = _equipos.GetValueOrDefault(que);
        if (tardo is { } t)
        {
            if (!estado.YaContesto && estado.Contesta == false)
            {
                // Nunca había contestado: no fue una caída.
                (estado.Contesta, estado.Fallos, estado.YaContesto) = (true, 0, true);
                _bitacora.Anotar($"«{que}» ({ip}) ya contesta el ping ({RedLocal.Ms(t)}).");
            }
            else
                Contesto(ref estado, que, $"{ip} contesta el ping en {RedLocal.Ms(t)}");
        }
        else if (!estado.YaContesto)
        {
            if (++estado.Fallos == FallosParaPerder)
            {
                estado.Contesta = false;
                _bitacora.Anotar($"«{que}» ({ip}) no contesta el ping desde que empezó la bitácora (su firewall puede no dejarlo): no se cuenta como caída.");
            }
        }
        else
            Perder(ref estado, que, $"{ip} no contesta el ping en {Segundos(EsperaPing)}");
        lock (_candado)
            _equipos[que] = estado;
    }

    // ── Internet ────────────────────────────────────────────────────────────

    /// <summary>A todos a la vez; con que uno conecte, hay internet. Dice cuál y en cuánto, o vacío si ninguno.</summary>
    private async Task<(string? Servidor, TimeSpan? Tardo)> InternetAsync(CancellationToken ct)
    {
        var resultados = await Task.WhenAll(_opciones.Internet.Select(async s =>
            (Servidor: $"{s.Host}:{s.Puerto}", Tardo: await _pruebas.ConectarAsync(s.Host, s.Puerto, EsperaInternet, ct))));
        var primero = resultados.Where(r => r.Tardo is not null).OrderBy(r => r.Tardo).FirstOrDefault();
        return primero.Tardo is null ? (null, null) : (primero.Servidor, primero.Tardo);
    }

    private void InternetRevisado(string? servidor, TimeSpan? tardo, FotoDeLaRed foto)
    {
        var antes = Internet;
        if (tardo is { } t)
            Contesto(ref _internet, "internet", $"{servidor} contesta en {RedLocal.Ms(t)}");
        else
        {
            var routerDice = Router switch
            {
                true => "el router sí contesta",
                false => "el router tampoco contesta",
                _ => foto.Router is null ? "esta PC no tiene router" : "el router, sin saber",
            };
            Perder(ref _internet, "internet",
                $"{Textos.Lista(_opciones.Internet.Select(s => $"{s.Host}:{s.Puerto}").ToList())} no contestan en {Segundos(EsperaInternet)}; {routerDice}");
        }
        if (Internet != antes)
        {
            lock (_candado)
                _internetDesde = _reloj.GetUtcNow();
            Cambio?.Invoke();
        }
    }

    // ── El latido de la caja ────────────────────────────────────────────────

    /// <summary>
    /// El latido de la caja (cada 5 s): lo que tardó la caja principal (vacío si no contestó) y cómo quedó la red. Se anota cuando
    /// cambia de buena a lenta o a caída, con los últimos latidos; lo demás va al resumen de cada 15 minutos.
    /// </summary>
    /// <param name="principal">A dónde se le habló (la dirección de la caja principal).</param>
    public void Latido(TimeSpan? latencia, EstadoRed estado, Uri principal)
    {
        EstadoRed? antes;
        string ultimos;
        lock (_candado)
        {
            if (latencia is { } l)
            {
                _latidosMs.Add(l.TotalMilliseconds);
                _ultimosLatidos.Enqueue(l);
                if (_ultimosLatidos.Count > 3)
                    _ultimosLatidos.Dequeue();
            }
            else
                _latidosSinRespuesta++;
            ultimos = _ultimosLatidos.Count > 0 ? Textos.Lista(_ultimosLatidos.Select(RedLocal.Ms).ToList()) : "";
            (antes, _principal) = (_principal, estado);
        }
        if (antes == estado)
            return;
        var direccion = principal.Authority;
        if (estado == EstadoRed.Caida)
        {
            _bitacora.SePerdio("la caja principal",
                $"{direccion} no contesta{(antes is { } a ? $"; antes, la red {Nombre(a)}{(ultimos.Length > 0 ? $" (últimos latidos: {ultimos})" : "")}" : "")}.");
            lock (_candado)
                _ultimosLatidos.Clear();
        }
        else if (antes == EstadoRed.Caida)
            _bitacora.Volvio("la caja principal", $"{direccion} contesta en {(latencia is { } l ? RedLocal.Ms(l) : "?")}; la red, {Nombre(estado)}.");
        else if (antes is { } desde)
            _bitacora.Anotar($"Red con la caja principal: {Nombre(desde)} → {Nombre(estado)} (últimos latidos: {ultimos}).");
    }

    /// <summary>
    /// Lo que la caja principal dice del internet en el latido (la caja no lo revisa: la de la caja principal es la que cuenta).
    /// Solo cuando contesta: sin ella no se sabe.
    /// </summary>
    public void InternetDeLaPrincipal(bool? hay)
    {
        bool? antes;
        lock (_candado)
        {
            if (hay is null || hay == _internetDeLaPrincipal)
                return;
            (antes, _internetDeLaPrincipal) = (_internetDeLaPrincipal, hay);
        }
        if (hay == false)
            _bitacora.SePerdio("internet", "lo dice la caja principal.");
        else if (antes == false)
            _bitacora.Volvio("internet", "lo dice la caja principal.");
    }

    private static string Nombre(EstadoRed estado) => estado switch
    {
        EstadoRed.Buena => "buena",
        EstadoRed.Lenta => "lenta",
        _ => "caída",
    };

    // ── El resumen de cada 15 minutos ───────────────────────────────────────

    private void ResumenSiToca(FotoDeLaRed foto)
    {
        var ahora = _reloj.GetUtcNow();
        string? texto = null;
        lock (_candado)
        {
            if (ahora - _resumenDesde < _opciones.Resumen)
            {
                _contadoresDesde ??= foto.Difusion is { } d0 ? (d0, foto.Descartados ?? 0) : null;
                return;
            }
            var partes = new List<string>();
            if (_latidosMs.Count + _latidosSinRespuesta > 0)
                partes.Add($"la caja principal {Rango(_latidosMs)} ({_latidosMs.Count + _latidosSinRespuesta} latidos, "
                           + $"{(_latidosSinRespuesta == 0 ? "ninguno" : _latidosSinRespuesta.ToString(CultureInfo.InvariantCulture))} sin respuesta)");
            if (_routerMs.Count + _routerSinRespuesta > 0)
                partes.Add($"el router {Rango(_routerMs)} ({_routerMs.Count} de {_routerMs.Count + _routerSinRespuesta} contestaron)");
            if (_internet.Contesta is { } hay)
                partes.Add(hay ? "internet sí" : "sin internet");
            else if (_internetDeLaPrincipal is { } segunLaPrincipal)
                partes.Add(segunLaPrincipal ? "internet sí (según la caja principal)" : "sin internet (según la caja principal)");
            if (foto.Tipo == TipoDeConexion.WiFi && foto.Senal is { } senal)
                partes.Add($"señal del Wi-Fi {senal} %");
            var segundos = (ahora - _resumenDesde).TotalSeconds;
            if (_contadoresDesde is { } desde && foto.Difusion is { } difusion && difusion >= desde.Difusion && segundos > 0)
                partes.Add($"difusión {((difusion - desde.Difusion) / segundos).ToString("#,##0.#", CultureInfo.InvariantCulture)} por segundo, "
                           + $"{Math.Max(0, (foto.Descartados ?? 0) - desde.Descartados).ToString("#,##0", CultureInfo.InvariantCulture)} descartados");
            if (partes.Count > 0)
                texto = $"Resumen de {Textos.Duracion(ahora - _resumenDesde)}: {string.Join("; ", partes)}.";
            _resumenDesde = ahora;
            (_latidosSinRespuesta, _routerSinRespuesta) = (0, 0);
            _latidosMs.Clear();
            _routerMs.Clear();
            _contadoresDesde = foto.Difusion is { } d ? (d, foto.Descartados ?? 0) : null;
        }
        if (texto is not null)
            _bitacora.Anotar(texto);
    }

    /// <summary>«en 4–12 ms (la mitad, 6 ms)» o «sin respuesta».</summary>
    private static string Rango(List<double> ms)
    {
        if (ms.Count == 0)
            return "sin respuesta";
        var orden = ms.Order().ToList();
        string Uno(double x) => RedLocal.Ms(TimeSpan.FromMilliseconds(x));
        return orden[0] == orden[^1] ? $"en {Uno(orden[0])}" : $"en {Uno(orden[0])} a {Uno(orden[^1])} (la mitad, {Uno(orden[orden.Count / 2])})";
    }

    // ── Perder y volver ─────────────────────────────────────────────────────

    /// <summary>Contestó: si estaba perdido, volvió (con cuánto duró).</summary>
    private void Contesto(ref Seguimiento estado, string que, string detalle)
    {
        bool? antes;
        lock (_candado)
        {
            antes = estado.Contesta;
            (estado.Contesta, estado.Fallos, estado.YaContesto) = (true, 0, true);
        }
        if (antes == false)
            _bitacora.Volvio(que, $"{detalle}.");
        if (antes != true && que == "el router")
            Cambio?.Invoke();
    }

    /// <summary>No contestó: con <see cref="FallosParaPerder"/> seguidas (o de una, si ya no hay ni red), se perdió.</summary>
    private void Perder(ref Seguimiento estado, string que, string detalle, bool inmediato = false)
    {
        bool perdio;
        lock (_candado)
        {
            estado.Fallos++;
            perdio = estado.Contesta != false && (inmediato || estado.Fallos >= FallosParaPerder);
            if (perdio)
                estado.Contesta = false;
        }
        if (!perdio)
            return;
        _bitacora.SePerdio(que, $"{detalle}.");
        if (que == "el router")
            Cambio?.Invoke();
    }

    private static string Segundos(TimeSpan espera) => $"{espera.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
}
