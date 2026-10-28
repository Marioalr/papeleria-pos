namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// Trabajos «por cobrar» inventados, como los tiene Control Print 1.12 (sus nombres en inglés): para las tarjetas de su cola en la
/// caja (las pruebas del módulo y las del POS, que traen este archivo).
/// </summary>
public static class PorCobrarDePrueba
{
    public const string Codigo = "K7QP-3MZX-9RTD-W2HB";

    /// <summary>Uno impreso desde la caja principal, por cobrar, de 2 páginas en B/N, carta y papel normal.</summary>
    public static JobRecordCp Trabajo(long id, DateTime detectadoUtc, decimal precio = 4, string documento = "Tarea de historia.pdf",
        PaymentState cobro = PaymentState.Pending, JobState estado = JobState.Printed, string pc = "PRINCIPAL") => new()
    {
        Id = id, SourcePc = pc, Kind = JobKind.Print, Printer = "WF-6590 Series(Red)", SpoolJobId = 100 + (int)id, SubmittedUtc = detectadoUtc.AddSeconds(-2),
        Document = documento, UserName = "Mostrador", Machine = pc, Datatype = "NT EMF 1.008", DetectedUtc = detectadoUtc,
        AnalyzedUtc = estado == JobState.Detected ? null : detectadoUtc.AddSeconds(3), FinishedUtc = estado == JobState.Printed ? detectadoUtc.AddSeconds(30) : null,
        State = estado, Pages = 2, Copies = 1, Sides = 2, ColorSides = 0, Sheets = 2, Color = false, Quality = "Standard", MediaType = "Plain",
        MediaName = "Papel normal", PaperSize = "Carta", InkK = 0.4, CostInk = 0.62m, CostPaper = 0.4m, CostMaintenance = 0.1m, CostTotal = 1.12m,
        MarginPercent = 100, Price = precio, PriceKind = PriceKind.Calculated, Payment = cobro,
        PaymentBy = cobro == PaymentState.Pending ? null : "Ana", PaymentUtc = cobro == PaymentState.Pending ? null : detectadoUtc.AddMinutes(1),
        OwnerId = DatosDeControlPrint.Invitado, OwnerName = "Sin sesión",
    };

    /// <summary>Hace <paramref name="minutos"/> minutos, en UTC (sin las fracciones de segundo, como las guarda Control Print).</summary>
    public static DateTime Hace(double minutos)
    {
        var hora = DateTime.UtcNow.AddMinutes(-minutos);
        return new DateTime(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>Espera hasta 15 s a que algo pase en segundo plano.</summary>
    public static async Task HastaAsync(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No pasó: {que}.");
            await Task.Delay(25);
        }
    }
}
