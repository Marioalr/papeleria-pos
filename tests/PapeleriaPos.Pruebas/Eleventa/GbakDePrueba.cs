using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Un gbak de mentiras: nunca corre el de verdad ni toca una base. Anota con qué lo llamaron y, si se le pide que salga
/// bien, deja un respaldo inventado donde gbak lo dejaría (el último argumento). Trae su propia carpeta temporal, que se
/// borra al terminar: ahí van el gbak.exe de mentiras y, si se usa <see cref="Respaldos"/>, los respaldos.
/// </summary>
public sealed class GbakDePrueba : IEjecutorGbak, IDisposable
{
    private readonly string _carpeta = Directory.CreateTempSubdirectory("gbak-de-prueba-").FullName;
    private readonly Lock _candado = new();
    private readonly List<(string Gbak, IReadOnlyList<string> Argumentos, IReadOnlyDictionary<string, string> Entorno)> _llamadas = [];

    /// <summary>Si sale bien (código 0 y el archivo) o con un error de gbak.</summary>
    public bool Bien { get; set; } = true;

    /// <summary>Lo que dice gbak cuando no puede.</summary>
    public string Error { get; set; } = "gbak: ERROR:Unable to complete network request to host \"10.0.0.10\".";

    /// <summary>Algo que hacer justo cuando se le llama (por ejemplo, ver cómo estaba la bandeja en ese momento).</summary>
    public Func<Task>? AlRespaldar { get; set; }

    /// <summary>Un archivo que hace de gbak.exe: el respaldo revisa que exista.</summary>
    public string Ruta => Path.Combine(_carpeta, "gbak.exe");

    /// <summary>Una carpeta para los respaldos, que se borra con lo demás.</summary>
    public string Respaldos => Path.Combine(_carpeta, "respaldos-eleventa");

    public GbakDePrueba() => File.WriteAllText(Ruta, "");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch (IOException)
        {
            // Se queda en la carpeta temporal.
        }
    }

    public IReadOnlyList<(string Gbak, IReadOnlyList<string> Argumentos, IReadOnlyDictionary<string, string> Entorno)> Llamadas
    {
        get
        {
            lock (_candado)
                return _llamadas.ToList();
        }
    }

    public async Task<(int Codigo, string Salida)> EjecutarAsync(string gbak, IReadOnlyList<string> argumentos, IReadOnlyDictionary<string, string> entorno,
        CancellationToken ct)
    {
        lock (_candado)
            _llamadas.Add((gbak, argumentos.ToList(), new Dictionary<string, string>(entorno)));
        if (AlRespaldar is { } alRespaldar)
            await alRespaldar();
        if (!Bien)
            return (1, Error);
        await File.WriteAllTextAsync(argumentos[^1], "respaldo de mentiras", ct);
        return (0, "");
    }
}
