using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Nucleo.Reportes;

/// <summary>
/// Los días de un reporte de ventas (entrega 9) y con qué se compara: el mismo periodo del año pasado. Decidido el
/// 2026-10-22, por revisar (docs/plan.md tenía la pregunta «¿contra el mismo día de la semana del año pasado o contra la misma
/// fecha?»):
/// <list type="bullet">
/// <item>hoy y la semana, por día de la semana: 364 días antes (52 semanas justas), así un lunes se compara con un lunes y un
/// sábado con un sábado, que en la tienda venden distinto;</item>
/// <item>el mes y el año, por las mismas fechas del calendario: octubre con octubre, enero a octubre con enero a octubre;</item>
/// <item>la semana va de lunes a domingo.</item>
/// </list>
/// Si el periodo incluye hoy, va «a la fecha»: hasta ahora, y el año pasado hasta el mismo momento (la misma hora del día que
/// le toca); un 29 de febrero, el año pasado va hasta el final del 28. Si ya pasó, se comparan completos. Todo en hora de la
/// tienda: <see cref="HoraMexico"/> lo pasa a UTC con el horario de verano que hubo hasta octubre de 2022.
/// </summary>
public sealed class PeriodoDeReporte
{
    /// <summary>Hoy y la semana se comparan con 52 semanas antes: el mismo día de la semana.</summary>
    public const int DiasAntes = 364;

    private PeriodoDeReporte(
        PeriodoReporte tipo, DateOnly desde, DateOnly hasta, DateOnly desdeAnterior, DateOnly hastaAnterior, DateTime? ahora, DateTime? corteAnterior)
    {
        (Tipo, Desde, Hasta, DesdeAnterior, HastaAnterior, Ahora, CorteAnterior) = (tipo, desde, hasta, desdeAnterior, hastaAnterior, ahora, corteAnterior);
    }

    public PeriodoReporte Tipo { get; }

    /// <summary>El primer día.</summary>
    public DateOnly Desde { get; }

    /// <summary>El día después del último (no se incluye).</summary>
    public DateOnly Hasta { get; }

    /// <summary>El primer día del mismo periodo del año pasado.</summary>
    public DateOnly DesdeAnterior { get; }

    /// <summary>El día después de su último día.</summary>
    public DateOnly HastaAnterior { get; }

    /// <summary>A la fecha: ahora, en hora de la tienda. Vacío si el periodo ya pasó.</summary>
    public DateTime? Ahora { get; }

    /// <summary>A la fecha: hasta cuándo se compara el año pasado (sin incluirlo). Vacío si el periodo ya pasó.</summary>
    public DateTime? CorteAnterior { get; }

    /// <summary>El periodo incluye hoy.</summary>
    public bool ALaFecha => Ahora is not null;

    /// <summary>El periodo de <paramref name="tipo"/> en que cae <paramref name="dia"/>, visto <paramref name="ahoraLocal"/>.</summary>
    public static PeriodoDeReporte De(PeriodoReporte tipo, DateOnly dia, DateTime ahoraLocal)
    {
        var (desde, hasta) = tipo switch
        {
            PeriodoReporte.Hoy => (dia, dia.AddDays(1)),
            PeriodoReporte.Semana => (Lunes(dia), Lunes(dia).AddDays(7)),
            PeriodoReporte.Mes => (new DateOnly(dia.Year, dia.Month, 1), new DateOnly(dia.Year, dia.Month, 1).AddMonths(1)),
            PeriodoReporte.Anio => (new DateOnly(dia.Year, 1, 1), new DateOnly(dia.Year + 1, 1, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "No hay ese periodo."),
        };
        var porSemanas = PorDiaDeLaSemana(tipo);
        var (desdeAnterior, hastaAnterior) = porSemanas
            ? (desde.AddDays(-DiasAntes), hasta.AddDays(-DiasAntes))
            : (desde.AddYears(-1), hasta.AddYears(-1));
        var hoy = DateOnly.FromDateTime(ahoraLocal);
        if (hoy < desde || hoy >= hasta)
            return new(tipo, desde, hasta, desdeAnterior, hastaAnterior, null, null);
        var corte = porSemanas
            ? desdeAnterior.ToDateTime(TimeOnly.MinValue) + (ahoraLocal - desde.ToDateTime(TimeOnly.MinValue))
            : UnAnioAntes(ahoraLocal);
        return new(tipo, desde, hasta, desdeAnterior, hastaAnterior, ahoraLocal, corte);
    }

    /// <summary>Hoy y la semana se comparan por día de la semana; el mes y el año, por fecha.</summary>
    public static bool PorDiaDeLaSemana(PeriodoReporte tipo) => tipo is PeriodoReporte.Hoy or PeriodoReporte.Semana;

    /// <summary>Un día del periodo anterior (<paramref name="pasos"/> negativo) o del siguiente: ← y → en la pantalla.</summary>
    public static DateOnly Mover(PeriodoReporte tipo, DateOnly dia, int pasos) => tipo switch
    {
        PeriodoReporte.Hoy => dia.AddDays(pasos),
        PeriodoReporte.Semana => dia.AddDays(7 * pasos),
        PeriodoReporte.Mes => dia.AddMonths(pasos),
        _ => dia.AddYears(pasos),
    };

    /// <summary>El lunes de la semana de ese día.</summary>
    public static DateOnly Lunes(DateOnly dia) => dia.AddDays(-(((int)dia.DayOfWeek + 6) % 7));

    /// <summary>
    /// El mismo momento un año antes. Un 29 de febrero no existe el año pasado: hasta el final del 28 (el 1 de marzo a las
    /// 00:00), no hasta las mismas horas del 28.
    /// </summary>
    private static DateTime UnAnioAntes(DateTime local) =>
        local is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(local.Year - 1) ? new DateTime(local.Year - 1, 3, 1) : local.AddYears(-1);

    // ── La gráfica: una cubeta por hora (hoy), por día (la semana y el mes) o por mes (el año) ──

    /// <summary>Las cubetas, en orden: las 24 horas, los 7 días (0 es el lunes), los días del mes o los 12 meses.</summary>
    public IEnumerable<int> Cubetas => Tipo switch
    {
        PeriodoReporte.Hoy => Enumerable.Range(0, 24),
        PeriodoReporte.Semana => Enumerable.Range(0, 7),
        PeriodoReporte.Mes => Enumerable.Range(1, DateTime.DaysInMonth(Desde.Year, Desde.Month)),
        _ => Enumerable.Range(1, 12),
    };

    /// <summary>La cubeta de un momento (hora de la tienda) del periodo o, con <paramref name="anterior"/>, del año pasado.</summary>
    public int Cubeta(DateTime local, bool anterior = false) => Tipo switch
    {
        PeriodoReporte.Hoy => local.Hour,
        PeriodoReporte.Semana => DateOnly.FromDateTime(local).DayNumber - (anterior ? DesdeAnterior : Desde).DayNumber,
        PeriodoReporte.Mes => local.Day,
        _ => local.Month,
    };

    /// <summary>Cuándo empieza una cubeta (hora de la tienda); la del año pasado, vacía si ese día no existe (el 29 de febrero).</summary>
    public DateTime? Inicio(int cubeta, bool anterior = false)
    {
        var desde = anterior ? DesdeAnterior : Desde;
        return Tipo switch
        {
            PeriodoReporte.Hoy => desde.ToDateTime(TimeOnly.MinValue).AddHours(cubeta),
            PeriodoReporte.Semana => desde.AddDays(cubeta).ToDateTime(TimeOnly.MinValue),
            PeriodoReporte.Mes => cubeta <= DateTime.DaysInMonth(desde.Year, desde.Month) ? new DateTime(desde.Year, desde.Month, cubeta) : null,
            _ => new DateTime(desde.Year, cubeta, 1),
        };
    }

    /// <summary>Cuándo termina una cubeta del periodo (sin incluirlo).</summary>
    public DateTime Fin(int cubeta)
    {
        var inicio = Inicio(cubeta)!.Value;
        return Tipo switch
        {
            PeriodoReporte.Hoy => inicio.AddHours(1),
            PeriodoReporte.Anio => inicio.AddMonths(1),
            _ => inicio.AddDays(1),
        };
    }
}
