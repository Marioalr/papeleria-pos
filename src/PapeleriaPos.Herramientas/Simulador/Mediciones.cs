using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Cuánto tardó algo, de las veces que se midió: la mitad, el 95 % (19 de cada 20), el 99 % y la peor, en ms.</summary>
public sealed record Medida(int Veces, double Mitad, double P95, double P99, double Maximo)
{
    /// <summary>Con el rango más cercano: cada número es una vez que sí se midió (como la caja, <c>RendimientoCaja</c>).</summary>
    public static Medida? De(IEnumerable<double> milisegundos)
    {
        var valores = milisegundos.ToArray();
        if (valores.Length == 0)
            return null;
        Array.Sort(valores);
        double Rango(double p) => valores[Math.Max(0, (int)Math.Ceiling(p / 100 * valores.Length) - 1)];
        return new Medida(valores.Length, Rango(50), Rango(95), Rango(99), valores[^1]);
    }
}

/// <summary>
/// Lo que se mide en el día simulado, de todas las cajas a la vez: lo que espera quien atiende (escanear, buscar, cobrar) y
/// cada llamada a la caja principal por su ruta (<see cref="MedidorDeRed"/>), con lo que pesó la respuesta.
/// </summary>
public sealed class Mediciones
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _tiempos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _fallas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _rechazos = new(StringComparer.Ordinal);

    /// <summary>
    /// Mientras se hace el cuadre, lo que se le pregunta a la caja principal no es parte del día: no cuenta. Lo de antes de
    /// empezar (dar de alta las cajas, bajar su copia) sí, aparte.
    /// </summary>
    public volatile bool Midiendo = true;

    public void Anotar(string que, TimeSpan tardo, long bytes = 0)
    {
        if (!Midiendo)
            return;
        _tiempos.GetOrAdd(que, _ => new ConcurrentQueue<double>()).Enqueue(tardo.TotalMilliseconds);
        if (bytes > 0)
            _bytes.AddOrUpdate(que, bytes, (_, antes) => antes + bytes);
    }

    /// <summary>Desde <paramref name="inicio"/> (de <see cref="Stopwatch.GetTimestamp"/>) hasta ahora.</summary>
    public void Anotar(string que, long inicio) => Anotar(que, Stopwatch.GetElapsedTime(inicio));

    /// <summary>No contestó (no se pudo conectar, pasó su plazo, se cortó).</summary>
    public void Fallo(string que)
    {
        if (Midiendo)
            _fallas.AddOrUpdate(que, 1, (_, n) => n + 1);
    }

    /// <summary>Contestó con un error (un 409 de precios cambiados, un 400 con el motivo): se cuenta aparte del tiempo.</summary>
    public void Rechazo(string que, HttpStatusCode codigo)
    {
        if (Midiendo)
            _rechazos.AddOrUpdate($"{que} → {(int)codigo}", 1, (_, n) => n + 1);
    }

    public Medida? De(string que) => _tiempos.TryGetValue(que, out var tiempos) ? Medida.De(tiempos) : null;

    /// <summary>
    /// Si la peor vez de algo fue la primera (con el servicio recién arrancado, lo que se usa por primera vez se compila): así el
    /// informe no la confunde con una que tardó a media jornada.
    /// </summary>
    public bool LaPeorFueLaPrimera(IEnumerable<string> claves) =>
        claves.Select(c => _tiempos.TryGetValue(c, out var t) && t.TryPeek(out var primera) ? (double?)primera : null).OfType<double>().ToList() is { Count: > 0 } primeras
        && De(claves) is { Veces: > 1 } medida && primeras.Max() >= medida.Maximo;

    /// <summary>De varias claves juntas (la misma operación por varios caminos).</summary>
    public Medida? De(IEnumerable<string> claves) => Medida.De(claves.SelectMany(c => _tiempos.TryGetValue(c, out var t) ? t : []));

    public long Bytes(string que) => _bytes.GetValueOrDefault(que);

    public int Fallas(string que) => _fallas.GetValueOrDefault(que);

    public IReadOnlyDictionary<string, int> Rechazos => _rechazos;

    public IReadOnlyCollection<string> Claves => _tiempos.Keys.Union(_fallas.Keys).ToList();
}

/// <summary>
/// Cada llamada de una caja simulada a la caja principal, con lo que tardó y lo que pesó la respuesta, por su ruta
/// («GET api/estado», «GET api/cajas/{id}/cambios»…). Va antes de la red simulada: lo que no se pudo conectar también cuenta,
/// como falla. Lee la respuesta completa (como la lee la caja: el cliente HTTP la guarda entera antes de entregarla), salvo el
/// aviso por SSE, que se queda abierto.
/// </summary>
public sealed class MedidorDeRed(Mediciones mediciones)
{
    private readonly ConcurrentDictionary<string, int> _exitos = new(StringComparer.Ordinal);

    /// <summary>
    /// Cuántas de esa ruta contestó bien la caja principal (aunque el día no se esté midiendo): así se sabe si un cobro o una
    /// salida de efectivo se hizo en línea o con la copia.
    /// </summary>
    public int Exitos(string ruta) => _exitos.GetValueOrDefault(ruta);

    public const string RutaCobro = "POST api/ventas";
    public const string RutaMovimiento = "POST api/turnos/{id}/movimientos";

    public HttpMessageHandler Envolver(HttpMessageHandler interno) => new Medidor(this, interno);

    /// <summary>La ruta sin lo que cambia de una llamada a otra: los Id, el código escaneado, el folio, la consulta.</summary>
    public static string Ruta(HttpRequestMessage solicitud)
    {
        var partes = solicitud.RequestUri!.AbsolutePath.Trim('/').Split('/');
        for (var i = 0; i < partes.Length; i++)
        {
            if (Guid.TryParse(partes[i], out _))
                partes[i] = "{id}";
            else if (i > 0 && partes[i - 1] is "codigo" or "folio")
                partes[i] = partes[i - 1] == "codigo" ? "{codigo}" : "{folio}";
        }
        return $"{solicitud.Method.Method} {string.Join('/', partes)}";
    }

    private sealed class Medidor(MedidorDeRed medidor, HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = Ruta(request);
            var sse = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
            var inicio = Stopwatch.GetTimestamp();
            HttpResponseMessage respuesta;
            try
            {
                respuesta = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception)
            {
                medidor._mediciones.Fallo(ruta);
                throw;
            }
            if (!sse)
            {
                try
                {
                    await respuesta.Content.LoadIntoBufferAsync(cancellationToken);
                }
                catch (Exception)
                {
                    respuesta.Dispose();
                    medidor._mediciones.Fallo(ruta);
                    throw;
                }
            }
            // Del aviso cuenta lo que tarda en conectarse (hasta que llegan sus encabezados).
            var tardo = Stopwatch.GetElapsedTime(inicio);
            if (!respuesta.IsSuccessStatusCode)
                medidor._mediciones.Rechazo(ruta, respuesta.StatusCode);
            else
                medidor._mediciones.Anotar(ruta, tardo, sse ? 0 : respuesta.Content.Headers.ContentLength ?? 0);
            if (respuesta.IsSuccessStatusCode)
                medidor._exitos.AddOrUpdate(ruta, 1, (_, n) => n + 1);
            return respuesta;
        }
    }

    private readonly Mediciones _mediciones = mediciones;
}
