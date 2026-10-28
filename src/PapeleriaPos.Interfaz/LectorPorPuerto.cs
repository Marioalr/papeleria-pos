using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PapeleriaPos.Interfaz;

/// <summary>Los puertos COM de esta PC (en Windows, <see cref="PuertosDelSistema"/>; en las pruebas, uno de mentira).</summary>
public interface IPuertosSerie
{
    /// <summary>Los que hay ahora: «COM1», «COM3». Un lector USB en modo COM sale de la lista al desconectarlo.</summary>
    IReadOnlyList<string> Nombres();

    /// <summary>
    /// Lo abre con esos ajustes. Si lo tiene otro programa, <see cref="UnauthorizedAccessException"/> (así lo dice Windows); si no
    /// existe, <see cref="FileNotFoundException"/> (o un <see cref="IOException"/> del aparato).
    /// </summary>
    IPuertoAbierto Abrir(AjustesLector ajustes);
}

/// <summary>Un puerto abierto. Lo cierra quien lo abrió (Dispose), también desde otro hilo: lo que esperaba a leer se suelta.</summary>
public interface IPuertoAbierto : IDisposable
{
    /// <summary>
    /// Espera lo que llegue, hasta <paramref name="espera"/> (<see cref="Timeout.InfiniteTimeSpan"/>: hasta que llegue algo), y lo
    /// deja en <paramref name="destino"/>: cuántos bytes, o 0 si pasó la espera sin nada. Si el lector se desconecta, una excepción.
    /// </summary>
    int Leer(byte[] destino, TimeSpan espera);

    /// <summary>Los errores de la línea que dio el puerto (de marco, de paridad, se perdieron bytes): la velocidad o la paridad no es.</summary>
    int ErroresDeLinea { get; }
}

/// <summary>Cómo está el lector por puerto.</summary>
public enum SituacionLector
{
    /// <summary>Esta caja no tiene «Lector» en caja.json: el lector se lee como teclado, como siempre.</summary>
    SinLector,

    /// <summary>Abriendo el puerto (al empezar).</summary>
    Abriendo,

    /// <summary>El puerto está abierto: lo que se escanea llega a la caja.</summary>
    Listo,

    /// <summary>Otro programa tiene abierto el puerto (en la caja 2, eleventa): se vuelve a intentar cada pocos segundos.</summary>
    Ocupado,

    /// <summary>El puerto no existe (el lector USB desconectado, o en otro puerto): se vuelve a buscar cada pocos segundos.</summary>
    SinConectar,

    /// <summary>No abre por otra cosa, o «Lector» en caja.json no se entiende.</summary>
    ConError,

    /// <summary>«SoloAlFrente»: la ventana de la caja no está al frente y el puerto quedó libre para eleventa.</summary>
    Suelto,
}

/// <summary>Cómo está el lector, desde cuándo y por qué.</summary>
/// <param name="Detalle">Lo que dijo Windows, o lo que no se entiende de caja.json.</param>
public sealed record EstadoLector(SituacionLector Situacion, string? Puerto, string? Detalle, DateTimeOffset Desde)
{
    /// <summary>Si hay que decirlo arriba (el chip): ocupado, sin conectar o con error.</summary>
    public bool EsProblema => Situacion is SituacionLector.Ocupado or SituacionLector.SinConectar or SituacionLector.ConError;

    /// <summary>Lo que dice el chip de arriba, corto: lo que importa va primero (si no cabe, se corta el final).</summary>
    public string? Chip => Situacion switch
    {
        SituacionLector.Ocupado => "Lector ocupado: ¿eleventa abierto?",
        SituacionLector.SinConectar => "Lector sin conectar",
        SituacionLector.ConError => "Lector con error",
        _ => null,
    };

    /// <summary>Lo que explica el chip (su title): qué pasa, qué hace la caja sola y qué hacer mientras.</summary>
    public string? Explicacion(TimeSpan reintento) => Situacion switch
    {
        SituacionLector.Ocupado =>
            $"El lector de códigos ({Puerto}) lo tiene abierto otro programa, quizá eleventa. Mientras, escribe los códigos a mano. "
            + $"La caja lo toma sola en cuanto se suelte (lo intenta cada {Segundos(reintento)}).",
        SituacionLector.SinConectar =>
            $"No se encuentra el lector de códigos en {Puerto}: está desconectado o en otro puerto. Al conectarlo, la caja lo toma sola "
            + $"(lo busca cada {Segundos(reintento)}). Mientras, escribe los códigos a mano.",
        SituacionLector.ConError when Puerto is null =>
            $"{Detalle} Mientras, el lector solo sirve como teclado. Configuración › Acerca de › Lector dice más.",
        SituacionLector.ConError =>
            $"El lector de códigos ({Puerto}) no abre: {Detalle?.TrimEnd('.')}. Se vuelve a intentar cada {Segundos(reintento)}; "
            + "Configuración › Acerca de › Lector dice más. Mientras, escribe los códigos a mano.",
        _ => null,
    };

    private static string Segundos(TimeSpan t) => $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s";
}

/// <summary>
/// El lector de códigos por puerto COM de la caja (piloto alfa, 2026-10-25: en la caja 2 el lector está configurado como puerto
/// COM, como en eleventa). Abre el puerto de «Lector» en caja.json, lo lee en su propio hilo (esperando sin gastar procesador:
/// la PC de la caja 2 es modesta) y avisa cada código (<see cref="Codigo"/>): la ventana lo entrega en la pantalla como un escaneo
/// del teclado (<see cref="EntregaDelLector"/>, caja.js). El lector como teclado sigue funcionando igual, a la par.
/// <para>
/// Un problema con el puerto nunca detiene la caja: si otro programa lo tiene abierto (eleventa sigue abierto en la caja 2 durante
/// el piloto) o el lector no está conectado, lo dice (<see cref="Estado"/>: el chip de arriba) y lo vuelve a intentar cada
/// <see cref="Reintento"/>; si se desconecta mientras lee, lo vuelve a buscar y lo toma en cuanto regresa. Lo que pasa (abrir,
/// cerrar, cada cambio y los errores) queda en el registro de la caja; las últimas lecturas, con sus bytes, en memoria para
/// Configuración › Acerca de › Lector.
/// </para>
/// </summary>
public sealed class LectorPorPuerto : IAsyncDisposable
{
    public static readonly TimeSpan ReintentoPorOmision = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Un código que la venta no dijo haber recibido en este tiempo (la página se volvió a cargar a media entrega) ya no se espera:
    /// lo que lo esperaba sigue.
    /// </summary>
    public static readonly TimeSpan Caducidad = TimeSpan.FromSeconds(3);

    /// <summary>Cuántas lecturas se recuerdan para Configuración › Acerca de › Lector.</summary>
    public const int Recordadas = 15;

    /// <summary>Hasta cuánto se espera a que el hilo del lector termine al cerrar la caja.</summary>
    private static readonly TimeSpan EsperaAlCerrar = TimeSpan.FromSeconds(2);

    private readonly IPuertosSerie _puertos;
    private readonly RegistroDiario? _registro;
    private readonly TimeProvider _reloj;
    private readonly long _inicio;
    private readonly CancellationTokenSource _alto = new();
    private readonly AutoResetEvent _despertar = new(false);
    private readonly Lock _candado = new();
    private readonly Queue<Lectura> _ultimas = new();
    private Thread? _hilo;
    private IPuertoAbierto? _abierto;
    private bool _alFrente = true;

    /// <summary>
    /// Si la ventana de la caja está al frente, como lo dice Windows (<see cref="AlFrente"/>): cada código se marca con lo que era
    /// al leerlo (revisión del PR #142).
    /// </summary>
    private volatile bool _ventanaAlFrente = true;

    /// <summary>
    /// Lo que el puerto ya leyó y la venta todavía no recibe: cada código (su número y cuándo se leyó), hasta que la venta lo toma
    /// (<see cref="Recibido"/>) o se sabe que no va a ella. El cobro, F6, «5*» y lo demás que actúa sobre lo último escaneado lo
    /// esperan (<see cref="EsperarLoLeidoAsync"/>), como esperan el escaneo del teclado que va en camino (revisión del PR #142).
    /// </summary>
    private readonly Dictionary<long, long> _enCamino = [];

    /// <summary>Un código a medias en el puerto (llegó una parte y falta su fin o su pausa): también se espera.</summary>
    private volatile bool _aMedias;

    private long _ultimoId;
    private TaskCompletionSource _cambioEnCamino = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile EstadoLector _estado;
    private long _codigos;
    private long _descartadas;
    private long _sinUsar;
    private int _erroresDeLinea;

    /// <param name="ajustes">Los de caja.json (<see cref="AjustesLector.Leer"/>); vacío: esta caja no tiene lector por puerto.</param>
    /// <param name="problema">Lo que no se entendió de «Lector» en caja.json: el lector queda con error, sin intentar nada.</param>
    /// <param name="reintento">Cada cuánto se vuelve a intentar abrir el puerto (en las pruebas, poco).</param>
    public LectorPorPuerto(AjustesLector? ajustes, string? problema, IPuertosSerie puertos, RegistroDiario? registro = null, TimeProvider? reloj = null,
        TimeSpan? reintento = null)
    {
        Ajustes = ajustes;
        _puertos = puertos;
        _registro = registro;
        _reloj = reloj ?? TimeProvider.System;
        _inicio = _reloj.GetTimestamp();
        Reintento = reintento ?? ReintentoPorOmision;
        var ahora = _reloj.GetLocalNow();
        _estado = problema is not null ? new EstadoLector(SituacionLector.ConError, null, problema, ahora)
            : ajustes is null ? new EstadoLector(SituacionLector.SinLector, null, null, ahora)
            : new EstadoLector(SituacionLector.Abriendo, ajustes.Puerto, null, ahora);
    }

    /// <summary>Los de caja.json; vacío si esta caja no tiene lector por puerto (o no se entendieron).</summary>
    public AjustesLector? Ajustes { get; }

    public TimeSpan Reintento { get; }

    public EstadoLector Estado => _estado;

    /// <summary>Un código del lector (en el hilo del lector): la ventana lo entrega en la pantalla.</summary>
    public event Action<Lectura>? Codigo;

    /// <summary>Cambió <see cref="Estado"/> (en el hilo del lector): el chip de arriba.</summary>
    public event Action? CambioDeEstado;

    /// <summary>Llegó algo, o se supo a dónde fue un código: Configuración › Acerca de › Lector se vuelve a pintar.</summary>
    public event Action? Actividad;

    /// <summary>Cada pedazo que llega del puerto, con su hora desde que se abrió la caja (para probar-lector; nadie más lo usa).</summary>
    public event Action<byte[], TimeSpan>? Pedazo;

    /// <summary>Los códigos que llegaron desde que se abrió la caja.</summary>
    public long Codigos => Interlocked.Read(ref _codigos);

    /// <summary>Lo que llegó y no era un código (basura, o un código a medias).</summary>
    public long Descartadas => Interlocked.Read(ref _descartadas);

    /// <summary>Los códigos que la pantalla no usó (la caja no estaba al frente, el cursor en un campo que no es para códigos…).</summary>
    public long SinUsar => Interlocked.Read(ref _sinUsar);

    /// <summary>Los errores de la línea que dio el puerto (de marco, de paridad): si hay, la velocidad o la paridad no es.</summary>
    public int ErroresDeLinea => Volatile.Read(ref _erroresDeLinea) + (_abierto?.ErroresDeLinea ?? 0);

    /// <summary>Las últimas lecturas, la más reciente primero.</summary>
    public IReadOnlyList<Lectura> Ultimas
    {
        get
        {
            lock (_candado)
                return _ultimas.Reverse().ToList();
        }
    }

    /// <summary>«llegaron 214 códigos, se descartaron 2 lecturas y 1 código no se usó»; «no llegó ningún código».</summary>
    public static string Cuenta(long codigos, long descartadas, long sinUsar)
    {
        var mx = System.Globalization.CultureInfo.GetCultureInfo("es-MX");
        var partes = new List<string>
        {
            codigos switch { 0 => "no llegó ningún código", 1 => "llegó 1 código", _ => $"llegaron {codigos.ToString("N0", mx)} códigos" },
        };
        if (descartadas > 0)
            partes.Add(descartadas == 1 ? "se descartó 1 lectura que no era un código" : $"se descartaron {descartadas.ToString("N0", mx)} lecturas que no eran un código");
        if (sinUsar > 0)
            partes.Add(sinUsar == 1 ? "1 código no se usó" : $"{sinUsar.ToString("N0", mx)} códigos no se usaron");
        return partes.Count == 1 ? partes[0] : $"{string.Join(", ", partes[..^1])} y {partes[^1]}";
    }

    /// <summary>Los puertos COM de esta PC, ahora (para Configuración › Acerca de › Lector); vacío si no se pudieron leer.</summary>
    public IReadOnlyList<string> PuertosDeLaPc()
    {
        try
        {
            return _puertos.Nombres();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Empieza a leer en su hilo; sin lector por puerto (o con «Lector» mal escrito), no hace nada.</summary>
    public void Iniciar()
    {
        if (Ajustes is not { } ajustes || _hilo is not null)
            return;
        _registro?.Anotar($"Lector por puerto: {ajustes.Resumen}.");
        _hilo = new Thread(Correr) { IsBackground = true, Name = "Lector por puerto" };
        _hilo.Start();
    }

    /// <summary>
    /// La ventana de la caja pasó al frente o dejó de estarlo: lo que el puerto lea mientras no lo está no se usa (como con el
    /// lector de teclado). Con «SoloAlFrente», además, el puerto se suelta mientras no lo está (para que eleventa lo pueda abrir) y
    /// se vuelve a tomar al regresar.
    /// </summary>
    public void AlFrente(bool alFrente)
    {
        _ventanaAlFrente = alFrente;
        if (Ajustes is not { SoloAlFrente: true })
            return;
        IPuertoAbierto? soltar;
        lock (_candado)
        {
            if (_alFrente == alFrente)
                return;
            _alFrente = alFrente;
            soltar = alFrente ? null : _abierto;
        }
        // Lo que esperaba a leer se suelta y el hilo ve que ya no está al frente. Aparte: cerrar el puerto no detiene la ventana.
        if (soltar is not null)
            _ = Task.Run(() => Cerrar(soltar));
        _despertar.Set();
    }

    /// <summary>La pantalla dijo a dónde fue un código (o que no lo usó): para Configuración › Acerca de › Lector.</summary>
    public void Entregado(Lectura lectura, string entrega, bool usada)
    {
        lectura.Entrega = entrega;
        lectura.Usada = usada;
        if (!usada)
            Interlocked.Increment(ref _sinUsar);
        Avisar(Actividad);
    }

    private TimeSpan Ahora => _reloj.GetElapsedTime(_inicio);

    private void Correr()
    {
        var ajustes = Ajustes!;
        var decodificador = new DecodificadorLector(ajustes.Fin, TimeSpan.FromMilliseconds(ajustes.Silencio));
        while (!_alto.IsCancellationRequested)
        {
            if (!SigueAlFrente())
            {
                Cambiar(SituacionLector.Suelto, "la ventana de la caja no está al frente", anotar: false);
                Esperar(Timeout.InfiniteTimeSpan);
                continue;
            }
            IPuertoAbierto puerto;
            try
            {
                puerto = _puertos.Abrir(ajustes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var (situacion, detalle) = Clasificar(ex, ListadoAhora(ajustes.Puerto));
                Cambiar(situacion, detalle, error: situacion == SituacionLector.ConError ? ex : null);
                Esperar(Reintento);
                continue;
            }
            lock (_candado)
            {
                if (_alto.IsCancellationRequested || !_alFrente)
                {
                    Cerrar(puerto);
                    continue;
                }
                _abierto = puerto;
            }
            var abiertoDesde = Ahora;
            // Si al abrirlo está en la lista de la PC, que salga de ella es que se desconectó (abajo, al leer).
            var enLaLista = ListadoAhora(ajustes.Puerto) == true;
            // Al volver al frente (con «SoloAlFrente») no se anota: sería una línea cada vez que se cambia de ventana.
            var deVuelta = _estado.Situacion == SituacionLector.Suelto;
            Cambiar(SituacionLector.Listo, null, anotar: false);
            if (!deVuelta)
                _registro?.Anotar($"Lector: se abrió {ajustes.Puerto}.");
            Exception? fallo = null;
            try
            {
                Leer(puerto, decodificador, enLaLista ? ajustes.Puerto : null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                fallo = ex;
            }
            finally
            {
                lock (_candado)
                    _abierto = null;
                Interlocked.Add(ref _erroresDeLinea, puerto.ErroresDeLinea);
                Cerrar(puerto);
                decodificador.Reiniciar();
                AMedias(false);
            }
            if (_alto.IsCancellationRequested)
                break;
            if (!SigueAlFrente())
                continue;
            // Se desconectó, o el puerto dejó de contestar: se vuelve a buscar. Si falló enseguida de abrir, una pausa antes, para no
            // dar vueltas sin parar con un aparato que abre pero no lee.
            _registro?.Anotar($"Lector: se desconectó {ajustes.Puerto}{(fallo is null ? "" : $" ({fallo.GetType().Name}: {fallo.Message.TrimEnd('.')})")}. Se vuelve a buscar.");
            Cambiar(SituacionLector.Abriendo, null, anotar: false);
            Esperar(Ahora - abiertoDesde < TimeSpan.FromSeconds(1) ? Reintento : TimeSpan.FromMilliseconds(300));
        }
    }

    /// <summary>
    /// Lee hasta que se cierre el puerto (al cerrar la caja, o al soltarlo) o falle (se desconectó). Sin nada a medias espera hasta
    /// <see cref="Reintento"/> (sin gastar: Windows la despierta en cuanto llega algo) y, si nada llegó, mira que el puerto siga en la
    /// PC: un lector USB desconectado se nota así aunque su controlador no dé un error al leer.
    /// </summary>
    /// <param name="vigilado">El puerto, si estaba en la lista de la PC al abrirlo; si no, no se mira la lista.</param>
    private void Leer(IPuertoAbierto puerto, DecodificadorLector decodificador, string? vigilado)
    {
        var bufer = new byte[256];
        var vaciasSinEsperar = 0;
        while (!_alto.IsCancellationRequested && SigueAlFrente())
        {
            var antes = Ahora;
            var pendiente = decodificador.EsperaHastaLaPausa(antes);
            var leidos = puerto.Leer(bufer, pendiente ?? Reintento);
            var ahora = Ahora;
            // Un puerto que regresa vacío sin esperar (cerrado del otro lado) haría girar este hilo sin parar: se da por desconectado.
            vaciasSinEsperar = leidos == 0 && pendiente is null && ahora - antes < Reintento / 4 ? vaciasSinEsperar + 1 : 0;
            if (vaciasSinEsperar >= 5)
                throw new IOException("Regresa sin datos y sin esperar.");
            if (leidos > 0)
            {
                var pedazo = bufer[..leidos];
                // A medias desde que llega algo: el cobro que se pida mientras lo espera.
                AMedias(true);
                if (Pedazo is { } pedazos)
                    Avisar(() => pedazos(pedazo, ahora));
                foreach (var lectura in decodificador.Agregar(pedazo, ahora))
                    Publicar(lectura);
            }
            // La pausa solo con el puerto de verdad sin nada en ese tiempo: esta lectura regresó vacía.
            else if (decodificador.Pausa(ahora) is { } porPausa)
                Publicar(porPausa);
            else if (pendiente is null && vigilado is not null && ListadoAhora(vigilado) == false)
                throw new IOException("Ya no está en la lista de puertos de la PC.");
            AMedias(decodificador.HayPendiente);
        }
    }

    private void Publicar(Lectura lectura)
    {
        lectura.Hora = _reloj.GetLocalNow();
        lectura.AlFrente = _ventanaAlFrente;
        lock (_candado)
        {
            _ultimas.Enqueue(lectura);
            while (_ultimas.Count > Recordadas)
                _ultimas.Dequeue();
            if (lectura.Codigo is not null)
            {
                lectura.Id = ++_ultimoId;
                // Lo que se leyó con otra ventana al frente no va a la venta: no hay que esperarlo.
                if (lectura.AlFrente)
                {
                    _enCamino[lectura.Id] = _reloj.GetTimestamp();
                    CambioEnCamino();
                }
            }
        }
        if (lectura.Codigo is not null)
        {
            Interlocked.Increment(ref _codigos);
            Avisar(Codigo, lectura);
        }
        else
        {
            // Las primeras; después, una línea a las 10, 100… para no llenar el registro (el puerto a otra velocidad manda basura sin
            // parar). Sin lo que trae: puede ser cualquier cosa (un QR con datos de alguien); su largo y por qué. En Configuración ›
            // Acerca de › Lector se ven sus bytes, en la memoria de la caja.
            var cuantas = Interlocked.Increment(ref _descartadas);
            if (cuantas <= 3 || cuantas is 10 or 100 or 1000 or 10_000)
                _registro?.Anotar($"Lector: se descartó lo que llegó porque {lectura.Descartada} ({lectura.Largo} bytes; van {cuantas}).");
        }
        Avisar(Actividad);
    }

    /// <summary>
    /// La venta recibió ese código (caja.js lo dice después de dárselo: <c>LectorRecibido</c>), o se supo que no va a ella (se usó en
    /// otro lado, o no se usó): ya no se espera.
    /// </summary>
    public void Recibido(long id)
    {
        lock (_candado)
            if (_enCamino.Remove(id))
                CambioEnCamino();
    }

    /// <summary>Si el puerto leyó algo que la venta todavía no recibe (o tiene un código a medias).</summary>
    public bool HayEnCamino
    {
        get
        {
            lock (_candado)
            {
                Caducar();
                return _enCamino.Count > 0 || _aMedias;
            }
        }
    }

    /// <summary>
    /// Espera a que la venta reciba lo que el puerto ya leyó (y lo que tiene a medias), hasta <paramref name="plazo"/>: lo usan el
    /// cobro, F6, «5*» y lo demás que actúa sobre lo último escaneado, como esperan el escaneo del teclado que va en camino. Sin nada
    /// en camino termina en el acto, sin esperar. Devuelve si llegó todo; si no llegó a tiempo, se anota y lo que esperaba sigue.
    /// </summary>
    public Task<bool> EsperarLoLeidoAsync(TimeSpan plazo)
    {
        lock (_candado)
        {
            Caducar();
            if (_enCamino.Count == 0 && !_aMedias)
                return Task.FromResult(true);
        }
        return EsperarEnCaminoAsync(plazo);
    }

    private async Task<bool> EsperarEnCaminoAsync(TimeSpan plazo)
    {
        var desde = _reloj.GetTimestamp();
        while (true)
        {
            Task cambio;
            lock (_candado)
            {
                Caducar();
                if (_enCamino.Count == 0 && !_aMedias)
                    return true;
                cambio = _cambioEnCamino.Task;
            }
            var falta = plazo - _reloj.GetElapsedTime(desde);
            if (falta <= TimeSpan.Zero)
            {
                _registro?.AnotarSinRepetir("Lector",
                    $"lo que leyó no llegó a la venta en {plazo.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s: se siguió sin esperarlo.");
                return false;
            }
            // Lo que va en camino avisa al cambiar; el código a medias se mira de nuevo cada poco.
            await Task.WhenAny(cambio, Task.Delay(falta < TimeSpan.FromMilliseconds(20) ? falta : TimeSpan.FromMilliseconds(20), _reloj));
        }
    }

    /// <summary>Lo que lleva más de <see cref="Caducidad"/> sin que la venta diga que lo recibió ya no se espera (se anota).</summary>
    private void Caducar()
    {
        if (_enCamino.Count == 0)
            return;
        var viejos = _enCamino.Where(e => _reloj.GetElapsedTime(e.Value) > Caducidad).Select(e => e.Key).ToList();
        if (viejos.Count == 0)
            return;
        foreach (var id in viejos)
            _enCamino.Remove(id);
        _registro?.AnotarSinRepetir("Lector", "la venta no dijo haber recibido un código a tiempo: se dejó de esperar.");
        CambioEnCamino();
    }

    /// <summary>Despierta a lo que espera lo que va en camino (con el candado tomado).</summary>
    private void CambioEnCamino()
    {
        var anterior = _cambioEnCamino;
        _cambioEnCamino = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        anterior.TrySetResult();
    }

    /// <summary>El puerto tiene (o ya no) un código a medias: lo dice el hilo del lector después de cada lectura.</summary>
    private void AMedias(bool aMedias)
    {
        if (_aMedias == aMedias)
            return;
        _aMedias = aMedias;
        if (!aMedias)
            lock (_candado)
                CambioEnCamino();
    }

    /// <summary>
    /// Por qué no abrió, dicho para la tienda. En Windows, System.IO.Ports da el error de Windows tal cual: «acceso denegado» cuando
    /// otro programa tiene el puerto, y «no se encuentra» (FileNotFoundException) o un error del aparato cuando no está; para saber si
    /// está, también se mira la lista de puertos de la PC.
    /// </summary>
    public static (SituacionLector Situacion, string Detalle) Clasificar(Exception ex, bool? enLaLista) => ex switch
    {
        // Fuera de Windows (probar-lector en Linux), System.IO.Ports dice «acceso denegado» con todo, como Windows con el puerto
        // ocupado: el porqué de verdad viene adentro (el errno).
        UnauthorizedAccessException { InnerException: IOException { HResult: 2 or 6 or 19 } } when !OperatingSystem.IsWindows()
            => (SituacionLector.SinConectar, "no existe en esta PC"),
        UnauthorizedAccessException { InnerException: IOException { HResult: 1 or 13 } } when !OperatingSystem.IsWindows()
            => (SituacionLector.ConError, "esta cuenta no tiene permiso para abrirlo (en Linux, el grupo dialout)"),
        UnauthorizedAccessException => (SituacionLector.Ocupado, "lo tiene abierto otro programa"),
        IOException io when CodigoDeWindows(io) is 32 => (SituacionLector.Ocupado, "lo tiene abierto otro programa"),
        // En Linux, abierto por otro: «Device or resource busy» (EBUSY).
        IOException io when !OperatingSystem.IsWindows() && io.HResult == 16 => (SituacionLector.Ocupado, "lo tiene abierto otro programa"),
        FileNotFoundException or DirectoryNotFoundException => (SituacionLector.SinConectar, "no existe en esta PC"),
        IOException io when CodigoDeWindows(io) is 2 or 3 or 31 or 1167 or 1617 || io.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            => (SituacionLector.SinConectar, enLaLista == true ? "no contesta" : "no existe en esta PC"),
        IOException when enLaLista == false => (SituacionLector.SinConectar, "no existe en esta PC"),
        ArgumentException => (SituacionLector.ConError, $"el nombre del puerto no sirve ({ex.Message.TrimEnd('.')})"),
        _ => (SituacionLector.ConError, $"{ex.GetType().Name}: {ex.Message.TrimEnd('.')}"),
    };

    /// <summary>El error de Windows dentro de un HRESULT (0x80070020: 32, compartido); 0 si no es uno.</summary>
    private static int CodigoDeWindows(IOException io) => (io.HResult & 0xFFFF0000) == 0x80070000 ? io.HResult & 0xFFFF : 0;

    /// <summary>Si el puerto está en la lista de la PC; vacío si no se pudo leer.</summary>
    private bool? ListadoAhora(string puerto)
    {
        try
        {
            return _puertos.Nombres().Contains(puerto, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private bool SigueAlFrente()
    {
        lock (_candado)
            return _alFrente;
    }

    /// <summary>
    /// Espera a que pase <paramref name="tiempo"/>, se cierre la caja o la ventana vuelva al frente (con «SoloAlFrente»): lo que
    /// pase primero.
    /// </summary>
    private void Esperar(TimeSpan tiempo) => WaitHandle.WaitAny([_alto.Token.WaitHandle, _despertar], tiempo);

    /// <summary>El último problema que se anotó; se olvida cuando el puerto abre.</summary>
    private (SituacionLector, string?)? _ultimoAnotado;

    /// <summary>
    /// Cambia el estado; un problema se anota en el registro una vez, hasta que el puerto abra (ocupado cada 3 s, o al volver al
    /// frente con «SoloAlFrente», no llena el registro).
    /// </summary>
    private void Cambiar(SituacionLector situacion, string? detalle, bool anotar = true, Exception? error = null)
    {
        var antes = _estado;
        if (antes.Situacion == situacion && antes.Detalle == detalle)
            return;
        _estado = new EstadoLector(situacion, Ajustes?.Puerto, detalle, _reloj.GetLocalNow());
        if (situacion == SituacionLector.Listo)
            _ultimoAnotado = null;
        if (anotar && Ajustes is { } ajustes && _ultimoAnotado != (situacion, detalle))
        {
            _ultimoAnotado = (situacion, detalle);
            var texto = situacion switch
            {
                SituacionLector.Ocupado => $"Lector: {ajustes.Puerto} está ocupado ({detalle}; ¿eleventa abierto?). Se vuelve a intentar cada {Reintento.TotalSeconds:0} s.",
                SituacionLector.SinConectar => $"Lector: {ajustes.Puerto} {detalle} (¿desconectado o en otro puerto?). Se vuelve a buscar cada {Reintento.TotalSeconds:0} s.",
                _ => $"Lector: {ajustes.Puerto} no abrió: {detalle}. Se vuelve a intentar cada {Reintento.TotalSeconds:0} s.",
            };
            if (error is not null)
                _registro?.AnotarError(texto, error);
            else
                _registro?.Anotar(texto);
        }
        Avisar(CambioDeEstado);
    }

    /// <summary>Lo que hagan los que escuchan no detiene al lector.</summary>
    private void Avisar(Action? aviso)
    {
        try
        {
            aviso?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _registro?.AnotarError("Lector: falló quien escuchaba", ex);
        }
    }

    private void Avisar(Action<Lectura>? aviso, Lectura lectura)
    {
        try
        {
            aviso?.Invoke(lectura);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _registro?.AnotarError("Lector: falló quien recibe los códigos", ex);
        }
    }

    /// <summary>
    /// Cerrar nunca truena: con un lector USB que se desconectó, cerrar el puerto falla en Windows. Si falla, se recoge lo que quedó
    /// abierto (si no, el mismo puerto sale «ocupado» por nosotros al volver a conectarlo).
    /// </summary>
    private static void Cerrar(IPuertoAbierto puerto)
    {
        try
        {
            puerto.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Al cerrar la caja: el puerto se cierra (lo que esperaba a leer se suelta) y se anota, antes de esperar al hilo: la caja puede
    /// estar saliendo.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_alto.IsCancellationRequested)
            return;
        _alto.Cancel();
        IPuertoAbierto? abierto;
        lock (_candado)
            abierto = _abierto;
        if (abierto is not null)
            Cerrar(abierto);
        _despertar.Set();
        if (_hilo is not { } hilo)
            return;
        if (Ajustes is { } ajustes)
            _registro?.Anotar($"Lector: se cerró {ajustes.Puerto} (se cerró la caja): {Cuenta(Codigos, Descartadas, SinUsar)}.");
        var reloj = Stopwatch.StartNew();
        while (hilo.IsAlive && reloj.Elapsed < EsperaAlCerrar)
            await Task.Delay(20);
    }
}
