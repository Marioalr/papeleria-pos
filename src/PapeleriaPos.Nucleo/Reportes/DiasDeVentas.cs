namespace PapeleriaPos.Nucleo.Reportes;

/// <summary>Los días de «Ventas por período» que se eligen con un toque (como el «Mostrar ventas de» de eleventa).</summary>
public enum DiasElegidos
{
    Hoy,
    Ayer,

    /// <summary>De lunes a hoy.</summary>
    EstaSemana,

    /// <summary>Del día 1 a hoy.</summary>
    EsteMes,

    /// <summary>El mes de antes, completo.</summary>
    MesPasado,
}

/// <summary>De qué son los días que se ven, para ← →: tantos días, una semana (de lunes a domingo) o un mes.</summary>
public enum TipoDeLargo
{
    Dias,
    Semana,
    Mes,
}

/// <summary>
/// El largo de los días que se ven (revisión del PR #134, 2026-10-23): ← → van a los de antes o de después de este largo. Lo da
/// lo que se eligió (el de un toque o las fechas que se escribieron) y se queda al moverse: no se adivina de las fechas, que
/// pueden estar cortadas en hoy o caer en un día 1 sin ser un mes.
/// </summary>
/// <param name="Dias">Cuántos días, si es <see cref="TipoDeLargo.Dias"/>.</param>
public readonly record struct LargoDeVentas(TipoDeLargo Tipo, int Dias = 1)
{
    public static LargoDeVentas Semana => new(TipoDeLargo.Semana, 7);

    public static LargoDeVentas Mes => new(TipoDeLargo.Mes, 0);

    public static LargoDeVentas DeDias(int dias) => new(TipoDeLargo.Dias, Math.Max(dias, 1));
}

/// <summary>
/// Los días de «Ventas por período» (entrega 9, parte 2, 2026-10-22): los de un toque, cuál de ellos es lo que se ve, y ← → al
/// periodo de antes o de después, del mismo largo, como en los reportes. Nunca pasan de hoy ni empiezan antes de
/// <see cref="PrimerDia"/>, y una consulta abarca hasta <see cref="AniosMaximos"/> años (revisión del PR #134: un año mal
/// escrito, «26», pedía siglos de días).
/// </summary>
public static class DiasDeVentas
{
    /// <summary>El primer día que se puede pedir: no hay ventas de antes, y así un año mal escrito no pide siglos.</summary>
    public static readonly DateOnly PrimerDia = new(2000, 1, 1);

    /// <summary>Lo más que abarca una consulta (el Excel lleva hasta 60,000 tickets, unos dos años de la tienda).</summary>
    public const int AniosMaximos = 3;

    /// <summary>El último día que cabe en una consulta que empieza en <paramref name="desde"/>.</summary>
    public static DateOnly UltimoQueCabe(DateOnly desde) =>
        desde.Year <= DateOnly.MaxValue.Year - AniosMaximos ? desde.AddYears(AniosMaximos).AddDays(-1) : DateOnly.MaxValue;

    /// <summary>El primer día que cabe en una consulta que acaba en <paramref name="hasta"/>.</summary>
    public static DateOnly PrimeroQueCabe(DateOnly hasta) =>
        hasta.Year > AniosMaximos ? hasta.AddYears(-AniosMaximos).AddDays(1) : DateOnly.MinValue;

    public static (DateOnly Desde, DateOnly Hasta) De(DiasElegidos dias, DateOnly hoy) => dias switch
    {
        DiasElegidos.Ayer => (hoy.AddDays(-1), hoy.AddDays(-1)),
        DiasElegidos.EstaSemana => (PeriodoDeReporte.Lunes(hoy), hoy),
        DiasElegidos.EsteMes => (PrimeroDelMes(hoy), hoy),
        DiasElegidos.MesPasado => (PrimeroDelMes(hoy).AddMonths(-1), PrimeroDelMes(hoy).AddDays(-1)),
        _ => (hoy, hoy),
    };

    /// <summary>El largo de los de un toque: hoy y ayer, un día; esta semana, una semana; este mes y el pasado, un mes.</summary>
    public static LargoDeVentas LargoDe(DiasElegidos dias) => dias switch
    {
        DiasElegidos.EstaSemana => LargoDeVentas.Semana,
        DiasElegidos.EsteMes or DiasElegidos.MesPasado => LargoDeVentas.Mes,
        _ => LargoDeVentas.DeDias(1),
    };

    /// <summary>
    /// El largo de unas fechas escritas: un mes completo (del 1 al último) es un mes; de lunes a domingo, una semana; lo demás,
    /// tantos días como tiene.
    /// </summary>
    public static LargoDeVentas LargoDe(DateOnly desde, DateOnly hasta)
    {
        if (desde.Day == 1 && hasta == UltimoDelMes(desde))
            return LargoDeVentas.Mes;
        if (desde.DayOfWeek == DayOfWeek.Monday && hasta.DayNumber - desde.DayNumber == 6)
            return LargoDeVentas.Semana;
        return LargoDeVentas.DeDias(hasta.DayNumber - desde.DayNumber + 1);
    }

    /// <summary>
    /// Cuál de los de un toque son esos días; vacío si ninguno (se escribieron las fechas). Con <paramref name="largo"/>, solo uno
    /// de ese largo: el día 1, «Este mes» y «Hoy» son el mismo día, pero ← → no van igual.
    /// </summary>
    public static DiasElegidos? Cual(DateOnly desde, DateOnly hasta, DateOnly hoy, LargoDeVentas? largo = null) =>
        Enum.GetValues<DiasElegidos>().Where(d => De(d, hoy) == (desde, hasta) && (largo is null || LargoDe(d) == largo))
            .Select(d => (DiasElegidos?)d).FirstOrDefault();

    /// <summary>
    /// ← (<paramref name="pasos"/> negativo) y →: el periodo de antes o de después del mismo <paramref name="largo"/> (un mes, una
    /// semana de lunes a domingo o tantos días), contado desde <paramref name="desde"/>, el primer día del que se ve: aunque ese
    /// esté cortado en hoy, el de antes es completo. Lo que pase de hoy llega hasta hoy y lo de antes de <see cref="PrimerDia"/>
    /// empieza en él; vacío si ya no hay a dónde (después de hoy, antes de <see cref="PrimerDia"/>, o unas fechas fuera de eso).
    /// </summary>
    public static (DateOnly Desde, DateOnly Hasta)? Mover(LargoDeVentas largo, DateOnly desde, int pasos, DateOnly hoy)
    {
        // Todo se cuenta en números de día (o de mes) y se revisa antes de volverlo fecha: nada se desborda.
        if (desde < PrimerDia || desde > hoy)
            return null;
        long inicio, fin;
        switch (largo.Tipo)
        {
            case TipoDeLargo.Mes:
            {
                var mes = desde.Year * 12L + desde.Month - 1 + pasos;
                if (mes < PrimerDia.Year * 12L + PrimerDia.Month - 1 || mes > hoy.Year * 12L + hoy.Month - 1)
                    return null;
                var primero = new DateOnly((int)(mes / 12), (int)(mes % 12) + 1, 1);
                (inicio, fin) = (primero.DayNumber, UltimoDelMes(primero).DayNumber);
                break;
            }
            case TipoDeLargo.Semana:
                inicio = PeriodoDeReporte.Lunes(desde).DayNumber + 7L * pasos;
                fin = inicio + 6;
                break;
            default:
            {
                var dias = (long)Math.Max(largo.Dias, 1);
                inicio = desde.DayNumber + dias * pasos;
                fin = inicio + dias - 1;
                break;
            }
        }
        if (fin < PrimerDia.DayNumber || inicio > hoy.DayNumber)
            return null;
        return (DateOnly.FromDayNumber((int)Math.Max(inicio, PrimerDia.DayNumber)), DateOnly.FromDayNumber((int)Math.Min(fin, hoy.DayNumber)));
    }

    private static DateOnly PrimeroDelMes(DateOnly dia) => new(dia.Year, dia.Month, 1);

    private static DateOnly UltimoDelMes(DateOnly dia) => dia.Year == DateOnly.MaxValue.Year && dia.Month == 12
        ? DateOnly.MaxValue
        : PrimeroDelMes(dia).AddMonths(1).AddDays(-1);
}
