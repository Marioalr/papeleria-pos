// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

namespace PapeleriaPos.Medicion;

/// <summary>
/// Compara las hojas que la impresora contó (su contador de páginas) con las que se esperaban de un lote de
/// trabajos. Windows marca un trabajo como impreso en cuanto termina de enviarlo, así que no se entera de un atasco
/// ni de una cancelación con el botón de la impresora; el contador sí (verificado: se canceló tras la primera hoja
/// de 6 y el contador subió 1).
/// <para>
/// Un lote empieza con el primer trabajo detectado (contador base) y se cierra cuando todos terminaron y el
/// contador dejó de moverse. Si faltan hojas, se reparten en orden: los primeros trabajos salieron completos y el
/// faltante corresponde a los últimos (un atasco o una cancelación detienen lo que sigue). Las impresiones de otras
/// fuentes (copias, celular) suben el contador: pueden ocultar un faltante, pero nunca inventarlo.
/// </para>
/// </summary>
public sealed class PageAuditor
{
    public sealed record JobCount(Guid JobId, int Expected, int Counted)
    {
        public bool Shortfall => Counted < Expected;
    }

    /// <summary>Datos de un trabajo del lote al cerrarlo: caras esperadas y si Windows lo terminó de imprimir.</summary>
    public readonly record struct JobInfo(int Sides, bool Printed, bool Finished);

    /// <summary>Tiempo sin cambios del contador para dar el lote por terminado.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    /// <summary>Un lote que no se cierra en este tiempo se evalúa con lo que haya.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(45);

    readonly List<Guid> jobs = [];
    long? baseline;
    DateTime openedUtc, lastFinishUtc;
    long? lastCounter;
    DateTime lastCounterChangeUtc;

    public bool IsOpen => jobs.Count > 0;
    public IReadOnlyList<Guid> Jobs => jobs;
    public long? Baseline => baseline;

    /// <summary>
    /// Un trabajo nuevo entra al lote. Si no había lote, lo abre con <paramref name="counterBefore"/> (la última
    /// lectura antes del trabajo; sin lectura no se puede verificar y el lote se ignora).
    /// </summary>
    public void JobDetected(Guid jobId, long? counterBefore, DateTime utc)
    {
        if (!IsOpen)
        {
            if (counterBefore is null) return;
            baseline = counterBefore;
            openedUtc = utc;
            lastCounter = counterBefore;
            lastCounterChangeUtc = utc;
        }
        if (!jobs.Contains(jobId)) jobs.Add(jobId);
    }

    /// <summary>
    /// Lectura hecha justo al detectar el primer trabajo (antes de que salga la primera hoja): reemplaza la base si
    /// es más reciente. Nunca la baja (el contador solo sube).
    /// </summary>
    public void RefineBaseline(long counter)
    {
        if (!IsOpen || baseline is not { } b || counter < b) return;
        baseline = counter;
        lastCounter = counter;
    }

    public void JobFinished(DateTime utc) => lastFinishUtc = utc;

    /// <summary>
    /// Nueva lectura del contador. Si todos los trabajos terminaron y el contador lleva <see cref="Settle"/> sin
    /// moverse (o pasó <see cref="MaxWindow"/>), cierra el lote y devuelve cuántas caras le tocan a cada trabajo
    /// impreso. Mientras tanto devuelve null.
    /// </summary>
    public IReadOnlyList<JobCount>? OnCounter(long counter, DateTime utc, Func<Guid, JobInfo> info)
    {
        if (!IsOpen || baseline is not { } b) return null;
        if (lastCounter != counter)
        {
            lastCounter = counter;
            lastCounterChangeUtc = utc;
        }
        var infos = jobs.Select(id => (Id: id, Info: info(id))).ToList();
        bool allFinished = infos.All(j => j.Info.Finished);
        bool settled = allFinished && utc - lastCounterChangeUtc >= Settle && utc - lastFinishUtc >= Settle;
        bool expired = utc - openedUtc >= MaxWindow;
        if (!settled && !(expired && allFinished)) return null;

        long remaining = Math.Max(0, counter - b);
        var result = new List<JobCount>();
        foreach (var (id, i) in infos)
        {
            if (!i.Printed || i.Sides <= 0) continue;
            int counted = (int)Math.Min(i.Sides, remaining);
            remaining -= counted;
            result.Add(new JobCount(id, i.Sides, counted));
        }
        jobs.Clear();
        baseline = null;
        return result;
    }

    /// <summary>¿Conviene otra lectura pronto? (lote abierto y todos sus trabajos ya terminaron).</summary>
    public bool WantsReading(Func<Guid, JobInfo> info) => IsOpen && jobs.All(id => info(id).Finished);
}
