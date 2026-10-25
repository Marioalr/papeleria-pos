using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Un registro de mentiras para las pruebas: guarda en memoria cada línea que escribe el servicio (su categoría, su nivel, el
/// texto y el error), para revisar lo que deja el puente, la lectura o la contingencia sin leer archivos. Se pone con
/// <c>s.AddSingleton&lt;ILoggerProvider&gt;(registro)</c>.
/// </summary>
internal sealed class RegistroEnMemoria : ILoggerProvider
{
    private readonly ConcurrentQueue<Linea> _lineas = new();

    public sealed record Linea(string Categoria, LogLevel Nivel, string Texto, Exception? Error);

    public IReadOnlyList<Linea> Lineas => [.. _lineas];

    public IEnumerable<string> Textos => _lineas.Select(l => l.Texto);

    public ILogger CreateLogger(string categoryName) => new Registrador(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>
    /// La primera línea que cumpla, esperándola hasta un minuto (el puente y la lectura van en segundo plano). Si no llega, la
    /// prueba falla con lo que sí se escribió.
    /// </summary>
    public async Task<Linea> EsperarAsync(Func<Linea, bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            if (_lineas.FirstOrDefault(condicion) is { } linea)
                return linea;
            if (DateTime.UtcNow > limite)
                Assert.Fail($"No llegó al registro {que}. Llegó:\n{string.Join("\n", Textos)}");
            await Task.Delay(50);
        }
    }

    /// <summary>La primera línea que empieza con eso (y, si se da, que también contiene lo otro).</summary>
    public Task<Linea> EsperarAsync(string empieza, string? contiene = null) =>
        EsperarAsync(l => l.Texto.StartsWith(empieza, StringComparison.Ordinal) && (contiene is null || l.Texto.Contains(contiene, StringComparison.Ordinal)),
            $"«{empieza}…»{(contiene is null ? "" : $" con «{contiene}»")}");

    private sealed class Registrador(RegistroEnMemoria registro, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            registro._lineas.Enqueue(new Linea(categoria, logLevel, formatter(state, exception), exception));
    }
}
