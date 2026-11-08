using System.Diagnostics;
using System.Globalization;

namespace PapeleriaPos.Interfaz;

/// <summary>Cuánto tardan los últimos escaneos, del Enter a la línea pintada: la mitad, el 95 % y el 99 %.</summary>
/// <param name="Cuantos">De cuántos escaneos (los últimos, hasta <see cref="RendimientoCaja.Recordados"/>).</param>
/// <param name="Maximo">El más lento de ellos.</param>
public sealed record MedidaEscaneos(int Cuantos, double Mitad, double P95, double P99, double Maximo = 0)
{
    /// <summary>«Del Enter a la línea en pantalla, últimos 214: la mitad en 9 ms, el 95 % en 18 ms y el 99 % en 31 ms.»</summary>
    public string Texto => Cuantos == 0
        ? "Todavía no hay escaneos medidos: se miden en la venta, del Enter a la línea en pantalla."
        : $"Del Enter a la línea en pantalla, {(Cuantos == 1 ? "el último escaneo" : $"los últimos {Cuantos} escaneos")}: la mitad en {Ms(Mitad)}, "
          + $"el 95 % en {Ms(P95)} y el 99 % en {Ms(P99)}.";

    private static string Ms(double ms) => $"{Math.Round(ms).ToString("#,##0", CultureInfo.GetCultureInfo("es-MX"))} ms";
}

/// <summary>
/// Lo que mide la caja de su rendimiento (entrega 5, docs/propuesta-interfaz.md, «Cómo se mide»), en memoria: lo que tardan
/// los últimos escaneos (del Enter a la línea pintada, con el reloj de la página) y lo que pasó en la caja y tardó (una
/// escritura larga en la base local, bajar la copia), para decir qué pasaba cuando la pantalla se trabó
/// (<see cref="VigiaDeLaPantalla"/>). Lo usan a la vez la pantalla, el escritor de la base y la sincronización.
/// </summary>
public sealed class RendimientoCaja
{
    /// <summary>Cuántos escaneos se recuerdan para la mitad, el 95 % y el 99 %.</summary>
    public const int Recordados = 500;

    /// <summary>Cuántas cosas que tardaron se recuerdan para decir qué pasaba.</summary>
    private const int ActividadesRecordadas = 64;

    private readonly Lock _candado = new();
    private readonly double[] _escaneos = new double[Recordados];
    private int _siguiente;
    private int _cuantos;
    private readonly Queue<(string Que, long Desde, long Hasta)> _actividades = new();

    /// <summary>
    /// Cuántos escaneos guarda cada periodo de los resúmenes del registro (la hora y el turno, <see cref="TiemposDeLaCaja"/>): una
    /// hora de mucha venta son unos cientos; un turno, unos miles. Pasado esto, se cuentan los primeros.
    /// </summary>
    public const int PorPeriodo = 50_000;

    private readonly List<double> _deLaHora = [];
    private readonly List<double> _delTurno = [];

    /// <summary>
    /// Si alguien saca los resúmenes de la hora y del turno (la ventana de Windows, con su registro): si no (la caja del navegador,
    /// las pruebas), no se guardan.
    /// </summary>
    internal bool JuntarPeriodos { get; set; }

    /// <summary>Lo que tardó un escaneo, del Enter a la línea pintada.</summary>
    public void AnotarEscaneo(TimeSpan tardo)
    {
        if (tardo < TimeSpan.Zero || tardo > TimeSpan.FromMinutes(5))
            return;
        lock (_candado)
        {
            _escaneos[_siguiente] = tardo.TotalMilliseconds;
            _siguiente = (_siguiente + 1) % Recordados;
            _cuantos = Math.Min(_cuantos + 1, Recordados);
            if (!JuntarPeriodos)
                return;
            if (_deLaHora.Count < PorPeriodo)
                _deLaHora.Add(tardo.TotalMilliseconds);
            if (_delTurno.Count < PorPeriodo)
                _delTurno.Add(tardo.TotalMilliseconds);
        }
    }

    /// <summary>La mitad, el 95 % y el 99 % de los últimos escaneos (con el rango más cercano: un valor que sí se midió).</summary>
    public MedidaEscaneos Escaneos
    {
        get
        {
            double[] valores;
            lock (_candado)
                valores = _escaneos[.._cuantos];
            return Medir(valores);
        }
    }

    /// <summary>Lo de los escaneos desde el resumen de la hora anterior, y empieza otra hora (<see cref="TiemposDeLaCaja"/>).</summary>
    public MedidaEscaneos SacarDeLaHora() => Sacar(_deLaHora);

    /// <summary>Lo de los escaneos desde que empezó el turno (o desde el resumen anterior), y empieza otro (<see cref="TiemposDeLaCaja"/>).</summary>
    public MedidaEscaneos SacarDelTurno() => Sacar(_delTurno);

    private MedidaEscaneos Sacar(List<double> periodo)
    {
        double[] valores;
        lock (_candado)
        {
            valores = [.. periodo];
            periodo.Clear();
        }
        return Medir(valores);
    }

    private static MedidaEscaneos Medir(double[] valores)
    {
        if (valores.Length == 0)
            return new MedidaEscaneos(0, 0, 0, 0);
        Array.Sort(valores);
        double Rango(double p) => valores[Math.Max(0, (int)Math.Ceiling(p / 100 * valores.Length) - 1)];
        return new MedidaEscaneos(valores.Length, Rango(50), Rango(95), Rango(99), valores[^1]);
    }

    /// <summary>Algo que pasó y tardó (lo dicen quienes lo hacen, en cualquier hilo): termina ahora.</summary>
    public void Actividad(string que, TimeSpan tardo)
    {
        var hasta = Stopwatch.GetTimestamp();
        var desde = hasta - (long)(tardo.TotalSeconds * Stopwatch.Frequency);
        lock (_candado)
        {
            _actividades.Enqueue((que, desde, hasta));
            while (_actividades.Count > ActividadesRecordadas)
                _actividades.Dequeue();
        }
    }

    /// <summary>Lo que pasaba entre esos dos momentos (de <see cref="Stopwatch.GetTimestamp"/>), cada cosa con lo que tardó.</summary>
    public List<string> Entre(long desde, long hasta)
    {
        lock (_candado)
            return _actividades.Where(a => a.Desde < hasta && a.Hasta > desde)
                .Select(a => $"{a.Que}, {Math.Round(Stopwatch.GetElapsedTime(a.Desde, a.Hasta).TotalMilliseconds)} ms")
                .Distinct()
                .ToList();
    }
}

/// <summary>
/// El vigía del hilo de la pantalla (docs/propuesta-interfaz.md, «Cómo se mide»): mientras hay una ráfaga de escaneos, cada
/// poco deja algo vacío en la fila de la pantalla y mide cuánto tarda en atenderse. Si pasa de 30 ms, la pantalla estuvo
/// trabada: lo anota en el registro del día, con lo que pasaba (la base de la caja, la sincronización). Fuera de una
/// ráfaga no hace nada: no estorba ni llena el registro.
/// </summary>
public sealed class VigiaDeLaPantalla : IAsyncDisposable
{
    /// <summary>Desde cuánto se anota (la meta es nunca pasar de 50 ms).</summary>
    public static readonly TimeSpan Trabada = TimeSpan.FromMilliseconds(30);

    /// <summary>Cuánto dura una ráfaga después del último escaneo.</summary>
    private static readonly TimeSpan Rafaga = TimeSpan.FromSeconds(2);

    /// <summary>Cada cuánto se mira durante una ráfaga.</summary>
    private static readonly TimeSpan CadaCuanto = TimeSpan.FromMilliseconds(15);

    /// <summary>Lo menos entre una línea del registro y la siguiente: una pantalla que se traba seguido no lo llena.</summary>
    private static readonly TimeSpan EntreLineas = TimeSpan.FromSeconds(10);

    private readonly Func<Action, Task> _enLaPantalla;
    private readonly RendimientoCaja _rendimiento;
    private readonly RegistroDiario? _registro;
    private readonly SemaphoreSlim _despertar = new(0);
    private readonly CancellationTokenSource _alto = new();
    private readonly Task _vigilando;
    private long _ultimoEscaneo;
    private long _ultimaLinea;
    private int _sinAnotar;
    private TimeSpan _peorSinAnotar;

    /// <param name="enLaPantalla">Pone algo en la fila de la pantalla (el InvokeAsync de Blazor) y termina cuando se atendió.</param>
    public VigiaDeLaPantalla(Func<Action, Task> enLaPantalla, RendimientoCaja rendimiento, RegistroDiario? registro)
    {
        _enLaPantalla = enLaPantalla;
        _rendimiento = rendimiento;
        _registro = registro;
        _vigilando = Task.Run(VigilarAsync);
    }

    /// <summary>Cuántas veces se vio la pantalla trabada (para las pruebas y el diagnóstico).</summary>
    public int Trabadas => Volatile.Read(ref _trabadas);

    private int _trabadas;

    /// <summary>Un escaneo: empieza o sigue la ráfaga. Con la pantalla ya yéndose, nada: el vigía solo mide.</summary>
    public void Escaneo()
    {
        if (_parado)
            return;
        Interlocked.Exchange(ref _ultimoEscaneo, Stopwatch.GetTimestamp());
        try
        {
            if (_despertar.CurrentCount == 0)
                _despertar.Release();
        }
        catch (ObjectDisposedException)
        {
            // Se desechó entre la revisión y el aviso.
        }
    }

    /// <summary>Ya se pidió que pare: lo que llegue después no lo despierta.</summary>
    private volatile bool _parado;

    private bool EnRafaga => Stopwatch.GetElapsedTime(Interlocked.Read(ref _ultimoEscaneo)) < Rafaga;

    private async Task VigilarAsync()
    {
        try
        {
            while (true)
            {
                await _despertar.WaitAsync(_alto.Token);
                while (EnRafaga)
                {
                    var puesto = Stopwatch.GetTimestamp();
                    var atendido = 0L;
                    await _enLaPantalla(() => atendido = Stopwatch.GetTimestamp()).WaitAsync(_alto.Token);
                    var espero = Stopwatch.GetElapsedTime(puesto, atendido);
                    if (espero > Trabada)
                        Anotar(espero, puesto, atendido);
                    await Task.Delay(CadaCuanto, _alto.Token);
                }
            }
        }
        catch (Exception)
        {
            // Se fue la pantalla (o lo que sea: el vigía solo mide, nunca tumba la caja).
        }
    }

    private void Anotar(TimeSpan espero, long desde, long hasta)
    {
        if (_ultimaLinea != 0 && Stopwatch.GetElapsedTime(_ultimaLinea) < EntreLineas)
        {
            _sinAnotar++;
            _peorSinAnotar = espero > _peorSinAnotar ? espero : _peorSinAnotar;
        }
        else
        {
            var mientras = _rendimiento.Entre(desde, hasta);
            var antes = _sinAnotar > 0 ? $" Antes de esta, {_sinAnotar} más (la peor, {Math.Round(_peorSinAnotar.TotalMilliseconds)} ms)." : "";
            _registro?.Anotar($"La pantalla se trabó {Math.Round(espero.TotalMilliseconds)} ms durante una ráfaga de escaneos"
                              + (mientras.Count > 0 ? $"; mientras: {string.Join("; ", mientras)}." : ".") + antes);
            (_ultimaLinea, _sinAnotar, _peorSinAnotar) = (hasta, 0, TimeSpan.Zero);
        }
        // Después de anotarlo: quien lo cuenta ya lo encuentra en el registro.
        Interlocked.Increment(ref _trabadas);
    }

    public async ValueTask DisposeAsync()
    {
        _parado = true;
        await _alto.CancelAsync();
        await _vigilando.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _alto.Dispose();
        _despertar.Dispose();
    }
}
