// Traídas de Control Print v1.12 (ControlPrint.Core.Tests).
using PapeleriaPos.Medicion;

namespace PapeleriaPos.Medicion.Pruebas;

public class CounterCheckTests
{
    static readonly DateTime T0 = new(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);
    static PrinterStatus At(double minutes, long life, long? color = null) =>
        new() { TakenUtc = T0.AddMinutes(minutes), LifeCount = life, ColorCount = color };

    [Fact]
    public void Counted_minus_registered_is_what_was_not_charged()
    {
        var history = new[] { At(-30, 1000, 300), At(10, 1010, 302), At(60, 1050, 310), At(200, 1100, 330) };
        (DateTime, DateTime)? asked = null;
        var check = CounterCheck.Compute(history, T0, T0.AddMinutes(90),
            (a, b) => { asked = (a, b); return new RegisteredSides(30, 4, 6, 2); })!;

        // Desde la última lectura antes del periodo hasta la última dentro de él.
        Assert.Equal((T0.AddMinutes(-30), T0.AddMinutes(60)), asked);
        Assert.Equal(50, check.Counted);
        Assert.Equal(10, check.CountedColor);
        Assert.Equal(36, check.RegisteredTotal);
        Assert.Equal(14, check.Unregistered);
        Assert.Equal(4, check.UnregisteredColor);
        Assert.Equal(10, check.UnregisteredMono);
    }

    [Fact]
    public void Never_negative_and_needs_two_readings()
    {
        var history = new[] { At(0, 1000, 300), At(30, 1005, 300) };
        var over = CounterCheck.Compute(history, T0, T0.AddHours(1), (_, _) => new RegisteredSides(8, 1, 0, 0))!;
        Assert.Equal(0, over.Unregistered);           // el trabajo aún no termina de salir: no hay faltante
        Assert.Equal(0, over.UnregisteredColor);

        Assert.Null(CounterCheck.Compute([At(0, 1000)], T0, T0.AddHours(1), (_, _) => RegisteredSides.None));
        Assert.Null(CounterCheck.Compute([At(0, 9000), At(30, 10)], T0, T0.AddHours(1), (_, _) => RegisteredSides.None));
        // Sin contador de color: solo el total.
        var noColor = CounterCheck.Compute([At(0, 10), At(5, 20)], T0, T0.AddHours(1), (_, _) => RegisteredSides.None)!;
        Assert.Null(noColor.UnregisteredColor);
        Assert.Equal(10, noColor.Unregistered);
    }
}
