namespace PapeleriaPos.Interfaz;

/// <summary>
/// El cuadro del código de la pantalla de venta. El lector teclea el código y un Enter, y Blazor atiende sus teclas sin
/// esperar a que termine la consulta del Enter anterior (con el Wi-Fi lento tarda). Por eso, al Enter, el texto se toma y
/// el cuadro se vacía antes de consultar nada, y los escaneos se procesan uno tras otro, en el orden del lector: ninguno
/// borra lo que se está escaneando ni se agrega antes que el anterior. El cobro también toma su lugar en la cola
/// (<see cref="ApartarAsync{T}"/>): ningún escaneo se queda fuera del ticket que se cobra ni cae en él cuando ya se cobró.
/// </summary>
public sealed class CapturaCodigo
{
    private Task _anterior = Task.CompletedTask;

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
    /// Termina cuando terminan los escaneos en cola (y el cobro, si hay uno). Las teclas que actúan sobre el ticket (F6,
    /// F5, + y −, Supr, F11, y F12 antes de pasar el cursor al cobro) lo esperan: F6 no manda un escaneo al ticket nuevo,
    /// ni + cambia el ticket que se está cobrando, ni F12 se pierde si la primera línea sigue en camino.
    /// </summary>
    public async Task EsperarAsync() =>
        await _anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>
    /// El cobro aparta su lugar en la cola: espera los escaneos que ya venían (con el Wi-Fi lento, el último puede seguir
    /// en camino al dar Enter en «Pagó con») y los que lleguen mientras se cobra esperan a que se suelte, para caer en el
    /// ticket que queda después del cobro y no en el que se cobró. La foto del ticket, de antes y de después de esperar,
    /// dice si llegó algo (<see cref="Apartado.LlegoAlgo"/>): entonces no se cobra todavía, el cajero ve el total nuevo.
    /// Siempre se suelta, se haya cobrado o no; quien lo tiene no vuelve a apartar ni espera la cola (se esperaría a sí
    /// mismo).
    /// </summary>
    public async Task<Apartado> ApartarAsync<T>(Func<T> foto)
    {
        var antes = foto();
        var apartado = new Apartado();
        var anterior = _anterior;
        _anterior = EnOrdenAsync(anterior, () => apartado.Soltado);
        await anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
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
