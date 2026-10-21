using PapeleriaPos.Impresion;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las fotos de los productos se preparan de una en una (revisión de la entrega 13, parte 2): leer una foto ocupa memoria por
/// un momento (hasta unos 100 MB con la más grande que se acepta, <see cref="FotoDelProducto"/>), y esta es la caja principal,
/// la que cobra. Si llegan varias a la vez (dos celulares, o una foto que la página no pudo achicar y llegó grande), esperan su
/// turno; si la fila no avanza en un rato, se dice que se intente otra vez.
/// </summary>
public sealed class PreparacionDeFotos
{
    /// <summary>Lo más que una foto espera su turno.</summary>
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _unaALaVez = new(1, 1);
    private int _esperando;
    private int _preparando;
    private int _masALaVez;

    /// <summary>Cuántas esperan su turno ahora (para las pruebas).</summary>
    internal int Esperando => Volatile.Read(ref _esperando);

    /// <summary>Cuántas se han preparado a la vez, como mucho (para las pruebas: siempre una).</summary>
    internal int MasALaVez => Volatile.Read(ref _masALaVez);

    /// <summary>Para las pruebas: lo que pasa ya con su turno, antes de leerla.</summary>
    internal Func<Task>? AlEmpezar { get; set; }

    /// <summary>La foto que llegó, revisada, derecha y chica (<see cref="FotoDelProducto.Preparar"/>), cuando le toca.</summary>
    public async Task<FotoPreparada> PrepararAsync(byte[] original, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _esperando);
        bool turno;
        try
        {
            turno = await _unaALaVez.WaitAsync(EsperaMaxima, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _esperando);
        }
        if (!turno)
            throw new InvalidOperationException("La caja principal está preparando otras fotos: vuelve a intentarlo en un momento.");
        try
        {
            var aLaVez = Interlocked.Increment(ref _preparando);
            int antes;
            while ((antes = Volatile.Read(ref _masALaVez)) < aLaVez && Interlocked.CompareExchange(ref _masALaVez, aLaVez, antes) != antes)
            {
            }
            if (AlEmpezar is { } alEmpezar)
                await alEmpezar();
            return FotoDelProducto.Preparar(original);
        }
        finally
        {
            Interlocked.Decrement(ref _preparando);
            _unaALaVez.Release();
        }
    }
}
