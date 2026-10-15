// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Atascos y cancelaciones en la impresora: Windows marca el trabajo impreso en cuanto lo envía, el contador de la
/// impresora dice cuántas hojas salieron. Los casos reproducen las pruebas reales del 2026-09-27.
/// </summary>
public class PageAuditTests
{
    static readonly DateTime T0 = new(2026, 9, 27, 13, 37, 46, DateTimeKind.Utc);

    static Guid G(int n) => new(n, 0, 0, new byte[8]);

    static Func<Guid, PageAuditor.JobInfo> Jobs(params (int Id, int Sides, bool Printed)[] jobs) =>
        id => jobs.First(j => G(j.Id) == id) is var j ? new PageAuditor.JobInfo(j.Sides, j.Printed, Finished: true) : default;

    [Fact]
    public void Cancelled_after_the_first_sheet_counts_one_of_six()
    {
        // Prueba C: contador 64,899 → 64,900 con un trabajo de 6 páginas.
        var audit = new PageAuditor();
        audit.JobDetected(G(10), 64_899, T0);
        audit.JobFinished(T0.AddSeconds(8));
        var info = Jobs((10, 6, true));
        Assert.Null(audit.OnCounter(64_900, T0.AddSeconds(20), info));            // aún no se asienta
        var result = audit.OnCounter(64_900, T0.AddSeconds(60), info)!;
        var job = Assert.Single(result);
        Assert.Equal((6, 1, true), (job.Expected, job.Counted, job.Shortfall));
        Assert.False(audit.IsOpen);
    }

    [Fact]
    public void A_complete_print_is_confirmed()
    {
        var audit = new PageAuditor();
        audit.JobDetected(G(1), 1000, T0);
        audit.JobFinished(T0.AddSeconds(10));
        var info = Jobs((1, 4, true));
        Assert.Null(audit.OnCounter(1002, T0.AddSeconds(15), info));             // sigue imprimiendo
        Assert.Null(audit.OnCounter(1004, T0.AddSeconds(25), info));             // último cambio
        var job = Assert.Single(audit.OnCounter(1004, T0.AddSeconds(60), info)!);
        Assert.False(job.Shortfall);
    }

    [Fact]
    public void Shortfall_goes_to_the_last_jobs_of_the_batch()
    {
        // Tres trabajos seguidos; un atasco en el segundo detiene el resto: salieron 3 + 1 de 3 + 3 + 2.
        var audit = new PageAuditor();
        audit.JobDetected(G(1), 500, T0);
        audit.JobDetected(G(2), null, T0.AddSeconds(5));
        audit.JobDetected(G(3), null, T0.AddSeconds(6));
        audit.JobFinished(T0.AddSeconds(30));
        var info = Jobs((1, 3, true), (2, 3, true), (3, 2, true));
        Assert.Null(audit.OnCounter(504, T0.AddMinutes(2), info));   // primera lectura con el contador ya quieto
        var result = audit.OnCounter(504, T0.AddMinutes(3), info)!;
        Assert.Equal([3, 1, 0], result.Select(r => r.Counted));
    }

    [Fact]
    public void Cancelled_jobs_do_not_expect_sheets_and_extra_sheets_never_invent_a_shortfall()
    {
        var audit = new PageAuditor();
        audit.JobDetected(G(1), 100, T0);
        audit.JobDetected(G(2), null, T0);
        audit.JobFinished(T0.AddSeconds(10));
        // El 2 se canceló en Windows; además alguien sacó 5 copias en el panel: el contador subió de más.
        var info = Jobs((1, 2, true), (2, 4, false));
        Assert.Null(audit.OnCounter(110, T0.AddMinutes(2), info));
        var result = audit.OnCounter(110, T0.AddMinutes(3), info)!;
        var job = Assert.Single(result);
        Assert.Equal((G(1), 2), (job.JobId, job.Counted));
    }

    [Fact]
    public void Without_a_counter_reading_the_batch_is_not_verified_and_the_baseline_only_goes_up()
    {
        var audit = new PageAuditor();
        audit.JobDetected(G(1), null, T0);
        Assert.False(audit.IsOpen);

        audit.JobDetected(G(2), 200, T0);
        audit.RefineBaseline(198);      // una lectura menor no puede ser posterior: se ignora
        Assert.Equal(200, audit.Baseline);
        audit.RefineBaseline(203);      // lectura justo al detectar (hubo copias desde la anterior)
        Assert.Equal(203, audit.Baseline);
    }

    [Fact]
    public void Nothing_closes_while_a_job_is_still_printing()
    {
        var audit = new PageAuditor();
        audit.JobDetected(G(1), 10, T0);
        Func<Guid, PageAuditor.JobInfo> printing = _ => new PageAuditor.JobInfo(5, false, Finished: false);
        Assert.Null(audit.OnCounter(12, T0.AddMinutes(5), printing));
        Assert.False(audit.WantsReading(printing));
        Assert.True(audit.IsOpen);
    }
}

