using System.Runtime.CompilerServices;

namespace PapeleriaPos.Impresion;

/// <summary>En qué va un papel de la cola de la impresora de tickets.</summary>
public enum EstadoImpresion
{
    /// <summary>Espera su turno.</summary>
    EnCola,

    /// <summary>Se le está mandando a la impresora.</summary>
    Imprimiendo,

    /// <summary>La impresora no contestó (puede estar imprimiendo lo de la otra caja): se vuelve a intentar sola.</summary>
    Ocupada,

    Impreso,

    /// <summary>No salió: la cola se detiene hasta «Reintentar» o «No imprimirlo».</summary>
    NoSalio,

    /// <summary>No salió y se dejó así con «No imprimirlo»: ya no sale; lo demás de la cola siguió.</summary>
    SinImprimir,
}

/// <summary>Un papel para la impresora de tickets: el ticket de una venta, una reimpresión, un comprobante, el corte…</summary>
public sealed class TrabajoDeImpresion
{
    private TaskCompletionSource<bool> _resultado = Nuevo();
    private readonly bool _abreCajon;

    /// <param name="datos">Lo que se manda, con el pulso del cajón si lo abre.</param>
    /// <param name="sinCajon">Lo mismo sin el pulso del cajón (vacío: era solo abrir el cajón); null si no abre el cajón.</param>
    internal TrabajoDeImpresion(string titulo, byte[] datos, byte[]? sinCajon, IReadOnlyList<Elemento>? documento, DateTime encoladoUtc)
    {
        Titulo = titulo;
        Datos = datos;
        SinCajon = sinCajon;
        Documento = documento;
        EncoladoUtc = encoladoUtc;
    }

    /// <summary>Un documento del diseño del ticket: sus comandos se arman al encolarlo (<see cref="Armar"/>).</summary>
    internal TrabajoDeImpresion(string titulo, IReadOnlyList<Elemento> documento, bool abreCajon, DateTime encoladoUtc)
    {
        Titulo = titulo;
        Documento = documento;
        _abreCajon = abreCajon;
        EncoladoUtc = encoladoUtc;
    }

    /// <summary>Lo que se dice de él en la pantalla: «Ticket A-000123», «Corte del turno»…</summary>
    public string Titulo { get; }

    /// <summary>Para verlo en pantalla si no sale; vacío si solo son comandos (abrir el cajón).</summary>
    public IReadOnlyList<Elemento>? Documento { get; }

    /// <summary>Los comandos que se mandan; vacío mientras no se pudieron armar (<see cref="Armar"/>).</summary>
    internal byte[]? Datos { get; private set; }

    /// <summary>Sin el pulso del cajón: para cuando ya no es momento de abrirlo. Null si no abre el cajón.</summary>
    internal byte[]? SinCajon { get; private set; }

    internal DateTime EncoladoUtc { get; }

    /// <summary>Ya se le mandó algo a la impresora y falló después: el cajón pudo abrirse ya, no se vuelve a abrir.</summary>
    internal bool SeMando { get; set; }

    public EstadoImpresion Estado { get; internal set; }

    /// <summary>Por qué no salió, o por qué se está volviendo a intentar.</summary>
    public string? Motivo { get; internal set; }

    /// <summary>
    /// Lo que se hace si se deja sin imprimir («No imprimirlo»): el ticket de una venta que no salió nunca se puede imprimir
    /// después sin pedir motivo (no es una reimpresión), y la caja principal tiene que saberlo. Lo de las reimpresiones, no.
    /// </summary>
    internal Action? AlDejarloSinImprimir { get; init; }

    /// <summary>
    /// Termina cuando sale (true) o cuando no salió (false). Con «Reintentar» vuelve a empezar: quien lo esperaba ya supo que
    /// no salió esa vez.
    /// </summary>
    public Task<bool> Resultado => _resultado.Task;

    internal void Terminar(bool salio) => _resultado.TrySetResult(salio);

    /// <summary>
    /// Arma los comandos del documento, si faltan. Si no se puede (el logo del ticket dañado), no se le manda nada a la
    /// impresora: queda como «no salió», con el motivo y su documento para verlo en pantalla. Antes eso tronaba en la
    /// pantalla después de guardar la venta, sin papel y sin nada que reintentar. «Reintentar» lo vuelve a armar.
    /// </summary>
    /// <returns>Si ya tiene sus comandos.</returns>
    internal bool Armar()
    {
        if (Datos is not null)
            return true;
        try
        {
            Datos = TicketEscPos.Generar(Documento!, _abreCajon);
            SinCajon = _abreCajon ? TicketEscPos.Generar(Documento!, false) : null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (Estado, Motivo) = (EstadoImpresion.NoSalio, $"No se pudo armar para la impresora. {ex.Message}");
            return false;
        }
    }

    internal void OtraVez()
    {
        if (_resultado.Task.IsCompleted)
            _resultado = Nuevo();
    }

    private static TaskCompletionSource<bool> Nuevo() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// La cola de la impresora de tickets de la caja (entrega 5, parte 3, docs/propuesta-interfaz.md, «Imprimir aparte»): lo
/// que se imprime entra aquí y la pantalla queda libre al momento; la cola lo manda en orden, uno por uno, en otro hilo.
/// <para>
/// La impresora de tickets de la tienda (una POS-8360 en la red) la comparten las dos cajas: si no contesta al conectarse
/// (puede estar imprimiendo lo de la otra caja, o estar apagada), se vuelve a intentar sola unas veces, con esperas cada vez
/// más largas; no se le había mandado nada, así que no sale dos veces. Si aun así no sale, o falla después de mandarle algo
/// (sin papel, no confirmó: pudo salir una parte), el papel se queda en la cola como «no salió» y la cola se detiene: lo que
/// sigue espera, en su orden, hasta «Reintentar» o hasta «No imprimirlo», que lo deja sin imprimir (el cliente ya se fue, o
/// no salió con «no confirmó» y pudo salir ya: «Reintentar» lo imprimiría dos veces). Nada se pierde sin decirlo: lo que
/// no se pudo ni armar para la impresora (un logo dañado) también se queda como «no salió», con «Verlo en pantalla».
/// </para>
/// <para>
/// El pulso que abre el cajón va al principio del papel (<see cref="TicketEscPos"/>): el cajón se abre en cuanto la
/// impresora lo recibe, aunque después el papel no salga (se acabó, salió a medias). Va solo si el papel se manda al
/// momento (<see cref="CajonAlMomentoPorOmision"/>) y una sola vez: si esperó (a la impresora o a «Reintentar») o ya se le
/// había mandado algo, sale sin el pulso. En la tienda no hay cajón (docs/notas.md).
/// </para>
/// <para>
/// Hay una sola por impresora (<see cref="De"/>): la venta, sus páginas, el corte y la Configuración usan la misma, y sigue
/// aunque la página se vuelva a cargar. Lo que queda sin imprimir al cerrar la caja se pregunta antes de cerrarla y se anota
/// en el registro del día.
/// </para>
/// </summary>
public sealed class ColaDeImpresion
{
    /// <summary>
    /// Lo que se espera antes de cada nuevo intento si la impresora no contesta: unos 15 s en total, lo que tarda la otra caja
    /// en imprimir dos o tres tickets con su logo.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> EsperasPorOmision =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)];

    /// <summary>
    /// Hasta cuándo va el pulso del cajón con su papel: es para dar el cambio en ese momento. Si el papel se manda más tarde
    /// (esperó a la impresora o a «Reintentar»), va sin el pulso: abrir el cajón después, a media venta del siguiente,
    /// sorprendería. Si se mandó a tiempo, el cajón se abre aunque luego el papel no salga.
    /// </summary>
    public static readonly TimeSpan CajonAlMomentoPorOmision = TimeSpan.FromSeconds(5);

    private static readonly ConditionalWeakTable<IImpresora, ColaDeImpresion> Colas = new();

    private readonly IImpresora _impresora;
    private readonly IReadOnlyList<TimeSpan> _esperas;
    private readonly TimeSpan _cajonAlMomento;
    private readonly Lock _candado = new();
    private readonly List<TrabajoDeImpresion> _pendientes = [];
    private bool _trabajando;
    private bool _detenida;

    /// <param name="esperas">Antes de cada nuevo intento si no contesta; las pruebas las ponen cortas.</param>
    /// <param name="cajonAlMomento">Hasta cuándo va el pulso del cajón con su papel (<see cref="CajonAlMomentoPorOmision"/>).</param>
    public ColaDeImpresion(IImpresora impresora, IReadOnlyList<TimeSpan>? esperas = null, TimeSpan? cajonAlMomento = null)
    {
        _impresora = impresora;
        _esperas = esperas ?? EsperasPorOmision;
        _cajonAlMomento = cajonAlMomento ?? CajonAlMomentoPorOmision;
    }

    /// <summary>La cola de esa impresora: una sola por impresora, la usen la venta, el corte o la Configuración.</summary>
    public static ColaDeImpresion De(IImpresora impresora) => Colas.GetValue(impresora, i => new ColaDeImpresion(i));

    /// <summary>Sin impresora: lo que se imprime se ve en pantalla, sin la cola.</summary>
    public bool MuestraVistaPrevia => _impresora.MuestraVistaPrevia;

    /// <summary>Algo de la cola cambió (entró, salió, no salió, se volvió a intentar). Llega desde el hilo de la cola.</summary>
    public event Action? Cambio;

    /// <summary>Lo que falta: lo que espera, lo que se imprime y lo que no salió, en su orden.</summary>
    public IReadOnlyList<TrabajoDeImpresion> Pendientes
    {
        get
        {
            lock (_candado)
                return [.. _pendientes];
        }
    }

    /// <summary>
    /// Lo primero de la cola si tiene un problema: no salió (detiene la cola hasta «Reintentar») o la impresora no contesta y
    /// se vuelve a intentar. Vacío si todo va bien.
    /// </summary>
    public TrabajoDeImpresion? Problema
    {
        get
        {
            lock (_candado)
                return _pendientes is [{ Estado: EstadoImpresion.NoSalio or EstadoImpresion.Ocupada } primero, ..] ? primero : null;
        }
    }

    /// <summary>La cola está detenida por uno que no salió: lo que sigue espera a «Reintentar».</summary>
    public bool Detenida
    {
        get
        {
            lock (_candado)
                return _detenida;
        }
    }

    /// <summary>
    /// Un documento del diseño del ticket: sale después de lo que ya estaba en la cola. Con <paramref name="abrirCajon"/>, el
    /// pulso del cajón va al principio del papel si se manda al momento (<see cref="CajonAlMomentoPorOmision"/>). Si no se
    /// puede armar para la impresora (un logo dañado), entra ya como «no salió», con su motivo: no truena aquí, donde la
    /// venta ya quedó guardada.
    /// </summary>
    /// <param name="alDejarloSinImprimir">
    /// Si se deja sin imprimir («No imprimirlo»), lo que hay que hacer: con el ticket de una venta que todavía no salía (al
    /// cobrar, «Imprimir ticket» o su primera impresión), decirle a la caja principal que sigue sin salir.
    /// </param>
    public TrabajoDeImpresion Encolar(string titulo, IReadOnlyList<Elemento> documento, bool abrirCajon, Action? alDejarloSinImprimir = null)
    {
        var trabajo = new TrabajoDeImpresion(titulo, documento, abrirCajon, DateTime.UtcNow) { AlDejarloSinImprimir = alDejarloSinImprimir };
        trabajo.Armar();
        return Agregar(trabajo);
    }

    /// <summary>Comandos ya armados: salen después de lo que ya estaba en la cola.</summary>
    public TrabajoDeImpresion Encolar(string titulo, byte[] escPos) => Agregar(new TrabajoDeImpresion(titulo, escPos, null, null, DateTime.UtcNow));

    /// <summary>Solo abrir el cajón (una entrada de efectivo): en su turno, y solo si todavía es al momento.</summary>
    public TrabajoDeImpresion AbrirCajon(string titulo) =>
        Agregar(new TrabajoDeImpresion(titulo, TicketEscPos.AbrirCajon(), [], null, DateTime.UtcNow));

    private TrabajoDeImpresion Agregar(TrabajoDeImpresion trabajo)
    {
        lock (_candado)
        {
            _pendientes.Add(trabajo);
            Despertar();
        }
        Avisar();
        return trabajo;
    }

    /// <summary>«Reintentar»: lo que no salió se vuelve a mandar, y después lo que esperaba, en su orden.</summary>
    public void Reintentar()
    {
        lock (_candado)
        {
            if (!_detenida)
                return;
            _detenida = false;
            if (_pendientes is [{ Estado: EstadoImpresion.NoSalio } primero, ..])
            {
                (primero.Estado, primero.Motivo) = (EstadoImpresion.EnCola, null);
                primero.OtraVez();
            }
            Despertar();
        }
        Avisar();
    }

    /// <summary>
    /// «No imprimirlo», junto a «Reintentar»: lo que no salió se quita de la cola sin imprimirlo, y lo que esperaba sigue en
    /// su orden. Si el cliente ya se fue, la cola no se queda detenida; y lo que no salió con «no confirmó» pudo salir ya:
    /// con «Reintentar» saldría dos veces. Lo que dice ya quedó guardado: un ticket que no salió se imprime desde las ventas del día,
    /// sin pedir motivo (quien lo encoló se entera, <see cref="TrabajoDeImpresion.AlDejarloSinImprimir"/>).
    /// Solo lo que no salió y no se está mandando (otro pudo reintentarlo ya): si no, no hace nada.
    /// </summary>
    /// <returns>Si se quitó (quien lo pidió lo anota en el registro de la caja).</returns>
    public bool NoImprimir(TrabajoDeImpresion trabajo)
    {
        lock (_candado)
        {
            var lugar = _pendientes.IndexOf(trabajo);
            if (lugar < 0 || trabajo.Estado != EstadoImpresion.NoSalio || (lugar == 0 && !_detenida))
                return false;
            _pendientes.RemoveAt(lugar);
            trabajo.Estado = EstadoImpresion.SinImprimir;
            if (lugar == 0)
            {
                _detenida = false;
                Despertar();
            }
        }
        Avisar();
        trabajo.AlDejarloSinImprimir?.Invoke();
        return true;
    }

    /// <summary>Si no hay quien mande lo de la cola y no está detenida, empieza (dentro del candado).</summary>
    private void Despertar()
    {
        if (_trabajando || _detenida || _pendientes.Count == 0)
            return;
        _trabajando = true;
        _ = Task.Run(MandarAsync);
    }

    private async Task MandarAsync()
    {
        while (true)
        {
            TrabajoDeImpresion trabajo;
            lock (_candado)
            {
                if (_detenida || _pendientes.Count == 0)
                {
                    _trabajando = false;
                    return;
                }
                trabajo = _pendientes[0];
            }
            var salio = await ImprimirAsync(trabajo);
            lock (_candado)
            {
                if (salio)
                    _pendientes.Remove(trabajo);
                else
                    _detenida = true;
                // Dentro del candado: un «Reintentar» que llegue en seguida ya encuentra este resultado terminado.
                trabajo.Terminar(salio);
            }
            Avisar();
        }
    }

    /// <summary>Lo manda; si la impresora no contesta al conectarse, lo vuelve a intentar con las esperas. Si salió.</summary>
    private async Task<bool> ImprimirAsync(TrabajoDeImpresion trabajo)
    {
        // Lo que no se pudo armar (un logo dañado) se intenta armar otra vez; si tampoco, no salió, sin mandar nada.
        if (!trabajo.Armar())
            return false;
        for (var intento = 0; ; intento++)
        {
            // Mientras se vuelve a intentar sigue diciendo que espera a la impresora: el aviso no aparece y desaparece.
            if (intento == 0)
            {
                trabajo.Estado = EstadoImpresion.Imprimiendo;
                Avisar();
            }
            // El cajón, solo al momento y si no pudo abrirse ya; si era solo abrirlo y ya no es momento, no hay nada que mandar.
            var datos = trabajo.SinCajon is { } sinCajon && (trabajo.SeMando || DateTime.UtcNow - trabajo.EncoladoUtc > _cajonAlMomento)
                ? sinCajon
                : trabajo.Datos!;
            try
            {
                if (datos.Length > 0)
                    await _impresora.EnviarAsync(datos);
                (trabajo.Estado, trabajo.Motivo) = (EstadoImpresion.Impreso, null);
                return true;
            }
            catch (ErrorAlConectar ex) when (intento < _esperas.Count)
            {
                // No se le mandó nada: se puede volver a intentar sin que salga dos veces.
                (trabajo.Estado, trabajo.Motivo) = (EstadoImpresion.Ocupada, ex.Message);
                Avisar();
                await Task.Delay(_esperas[intento]);
            }
            catch (ErrorAlConectar ex)
            {
                (trabajo.Estado, trabajo.Motivo) = (EstadoImpresion.NoSalio, $"{ex.Message} Se intentó {intento + 1} veces.");
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Ya se le mandó algo: pudo salir una parte (y abrirse el cajón).
                trabajo.SeMando = true;
                (trabajo.Estado, trabajo.Motivo) = (EstadoImpresion.NoSalio, ex.Message);
                return false;
            }
        }
    }

    private void Avisar()
    {
        try
        {
            Cambio?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Quien escucha es la pantalla: si ella falla, la cola sigue.
        }
    }
}
