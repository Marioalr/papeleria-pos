namespace PapeleriaPos.Interfaz;

/// <summary>
/// El cuadro del código de la pantalla de venta. El lector teclea el código y un Enter, y Blazor atiende sus teclas sin
/// esperar a que termine lo del Enter anterior. Por eso, al Enter, el texto se toma y el cuadro se vacía antes de buscar
/// nada, y los escaneos se procesan uno tras otro, en el orden del lector: ninguno borra lo que se está escaneando ni se
/// agrega antes que el anterior. Desde la entrega 5 el código se busca en la copia de la caja, sin la red, y casi siempre
/// termina al momento; lo que todavía espera algo es un nombre (la búsqueda), un folio, o un código que no está en la copia
/// (ese va aparte, <see cref="EnVuelo"/>). El cobro también toma su lugar en la cola (<see cref="ApartarAsync{T}"/>):
/// ningún escaneo se queda fuera del ticket que se cobra ni cae en él cuando ya se cobró.
/// </summary>
public sealed class CapturaCodigo
{
    private Task _anterior = Task.CompletedTask;

    /// <summary>
    /// Lo que llega a la venta por otro camino que las teclas de la página: el lector por puerto COM (piloto alfa). Con el lector de
    /// teclado, el escaneo y las teclas que siguen van por la misma fila de la página, y el escaneo llega primero; el del puerto va
    /// por la ventana de Windows y puede llegar después de un F12 o un «5*» que se oprimieron después de escanear. Antes de actuar
    /// sobre lo último escaneado se espera también lo que el puerto ya leyó (con plazo): ningún código se queda fuera del cobro ni
    /// cae en el ticket siguiente (revisión del PR #142). Con la hora de la tecla, solo lo que el puerto empezó a leer antes de ella:
    /// lo que se escaneó después tampoco cae en el ticket que se cobra (segunda revisión).
    /// </summary>
    private Func<DateTimeOffset?, Task>? _deFuera;

    /// <summary>Que también se espere lo que leyó el lector por puerto (lo pone la pantalla de venta, si la caja lo tiene).</summary>
    public void EsperarTambien(Func<DateTimeOffset?, Task> deFuera) => _deFuera = deFuera;

    /// <summary>
    /// Termina cuando la venta ya recibió lo que el puerto leyó (o pasó su plazo); sin lector por puerto, o sin nada en camino, en el
    /// acto. Lo usa también «5*» antes de formarse: es para lo último que se escaneó.
    /// </summary>
    /// <param name="tecla">
    /// La hora de la PC en que se oprimió la tecla: solo lo que el puerto empezó a leer antes de ella. Vacía (un clic, un botón): todo
    /// lo que ya leyó, como antes.
    /// </param>
    public Task EsperarLoDeFueraAsync(DateTimeOffset? tecla = null) => _deFuera?.Invoke(tecla) ?? Task.CompletedTask;

    /// <summary>Lo que se pregunta a la caja principal en segundo plano: los códigos que no están en la copia (entrega 5).</summary>
    private readonly List<Task> _enVuelo = [];

    /// <summary>
    /// Un código que no está en la copia ya tiene su renglón («buscando…») y se pregunta a la caja principal, con plazo,
    /// sin detener la cola: los escaneos que siguen entran enseguida. Las teclas que actúan sobre el ticket y el cobro lo
    /// esperan (<see cref="EsperarAsync"/>, <see cref="ApartarAsync{T}"/>): nunca se cobra un ticket con un «buscando…».
    /// </summary>
    public void EnVuelo(Task busqueda)
    {
        if (busqueda.IsCompleted)
            return;
        lock (_enVuelo)
            _enVuelo.Add(busqueda);
        _ = busqueda.ContinueWith(t =>
        {
            lock (_enVuelo)
                _enVuelo.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Cuántos códigos se están preguntando a la caja principal.</summary>
    public int Buscando
    {
        get
        {
            lock (_enVuelo)
                return _enVuelo.Count;
        }
    }

    /// <summary>
    /// Termina cuando termina lo que se pregunta a la caja principal (con plazo), sin esperar la cola: lo usa lo que ya va
    /// dentro de ella («5*» y Enter, que cambia la cantidad de lo último que se escaneó, aunque siga «buscando…»).
    /// </summary>
    public async Task EsperarEnVueloAsync()
    {
        Task[] enVuelo;
        lock (_enVuelo)
            enVuelo = [.. _enVuelo];
        if (enVuelo.Length > 0)
            await Task.WhenAll(enVuelo).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Lo que hay en el cuadro: no está enlazado, caja.js lo manda con el Enter, las teclas y al dejar de teclear.</summary>
    public string Texto { get; set; } = "";

    /// <summary>
    /// El Enter: toma lo escrito y vacía el cuadro sin esperar nada; lo procesa cuando terminan los escaneos anteriores. La
    /// tarea termina cuando termina este escaneo.
    /// </summary>
    public Task EnterAsync(Func<string, Task> procesar)
    {
        var texto = Texto.Trim();
        Texto = "";
        if (texto.Length == 0)
            return Task.CompletedTask;
        return EnTurnoAsync(() => procesar(texto));
    }

    /// <summary>
    /// Lo que no es un escaneo pero agrega al ticket después de consultar (un renglón de la lista, un rápido): se forma en
    /// la misma cola, para quedar en orden con los escaneos y no caer en un ticket que se está cobrando. Lo que ya va dentro
    /// de la cola no se vuelve a formar: se esperaría a sí mismo.
    /// </summary>
    public Task EnTurnoAsync(Func<Task> accion) => _anterior = EnOrdenAsync(_anterior, accion);

    /// <summary>
    /// Termina cuando terminan los escaneos en cola (y el cobro, si hay uno) y lo que se pregunta a la caja principal. Las
    /// teclas que actúan sobre el ticket (F6, F5, + y −, ↑ y ↓, Supr, F11, Ctrl+E, Ctrl+D, y F12 antes de pasar el cursor al
    /// cobro) y los botones de cada fila lo esperan: F6 no manda un escaneo al ticket nuevo, ni + cambia el ticket que se
    /// está cobrando, ni ↑ elige antes de que llegue lo último, ni F12 se pierde si la primera línea sigue en camino.
    /// </summary>
    /// <param name="tecla">La hora de la PC en que se oprimió la tecla (<see cref="EsperarLoDeFueraAsync"/>); vacía en un clic.</param>
    public async Task EsperarAsync(DateTimeOffset? tecla = null)
    {
        // Primero lo que el puerto ya leyó: así se forma antes, y lo de abajo también lo espera.
        await EsperarLoDeFueraAsync(tecla);
        await _anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        await EsperarEnVueloAsync();
    }

    /// <summary>
    /// El cobro aparta su lugar en la cola: espera los escaneos que ya venían (con el Wi-Fi lento, el último puede seguir
    /// en camino al dar Enter en «Pagó con») y los que lleguen mientras se cobra esperan a que se suelte, para caer en el
    /// ticket que queda después del cobro y no en el que se cobró. La foto del ticket, de antes y de después de esperar,
    /// dice si llegó algo (<see cref="Apartado.LlegoAlgo"/>): entonces no se cobra todavía, el cajero ve el total nuevo.
    /// Siempre se suelta, se haya cobrado o no; quien lo tiene no vuelve a apartar ni espera la cola (se esperaría a sí
    /// mismo).
    /// </summary>
    /// <param name="tecla">La hora de la PC en que se oprimió la tecla que cobra (<see cref="EsperarLoDeFueraAsync"/>); vacía en un clic.</param>
    public async Task<Apartado> ApartarAsync<T>(Func<T> foto, DateTimeOffset? tecla = null)
    {
        var antes = foto();
        // Lo que el puerto ya leyó se forma antes que el cobro: si llega, el cobro lo ve en la foto y no cobra todavía.
        await EsperarLoDeFueraAsync(tecla);
        var apartado = new Apartado();
        var anterior = _anterior;
        _anterior = EnOrdenAsync(anterior, () => apartado.Soltado);
        await anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        // Y lo que se pregunta a la caja principal (con plazo): un «buscando…» nunca se cobra.
        await EsperarEnVueloAsync();
        apartado.LlegoAlgo = !EqualityComparer<T>.Default.Equals(antes, foto());
        return apartado;
    }

    /// <summary>El lugar del cobro en la cola. Dispose lo suelta: los escaneos que esperaban siguen, en su orden.</summary>
    public sealed class Apartado : IDisposable
    {
        // Lo que esperaba sigue después de quien lo suelta, no en medio de él.
        private readonly TaskCompletionSource _soltar = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Soltado => _soltar.Task;

        /// <summary>Mientras se esperaba la cola, cambió el ticket: llegó el último escaneo (o se fue a otro ticket).</summary>
        public bool LlegoAlgo { get; internal set; }

        public void Dispose() => _soltar.TrySetResult();
    }

    private static async Task EnOrdenAsync(Task anterior, Func<Task> accion)
    {
        // Si el anterior falló, su error ya le llegó a su propio Enter: este escaneo sigue.
        await anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        await accion();
    }
}
