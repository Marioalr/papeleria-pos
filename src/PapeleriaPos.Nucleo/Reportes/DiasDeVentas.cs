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

/// <summary>
/// Los días de «Ventas por período» (entrega 9, parte 2, 2026-10-22): los de un toque, cuál de ellos es lo que se ve, y ← → al
/// periodo de antes o de después, del mismo largo, como en los reportes. Nunca pasan de hoy.
/// </summary>
public static class DiasDeVentas
{
    public static (DateOnly Desde, DateOnly Hasta) De(DiasElegidos dias, DateOnly hoy) => dias switch
    {
        DiasElegidos.Ayer => (hoy.AddDays(-1), hoy.AddDays(-1)),
        DiasElegidos.EstaSemana => (PeriodoDeReporte.Lunes(hoy), hoy),
        DiasElegidos.EsteMes => (PrimeroDelMes(hoy), hoy),
        DiasElegidos.MesPasado => (PrimeroDelMes(hoy).AddMonths(-1), PrimeroDelMes(hoy).AddDays(-1)),
        _ => (hoy, hoy),
    };

    /// <summary>Cuál de los de un toque son esos días; vacío si ninguno (se escribieron las fechas).</summary>
    public static DiasElegidos? Cual(DateOnly desde, DateOnly hasta, DateOnly hoy) =>
        Enum.GetValues<DiasElegidos>().Where(d => De(d, hoy) == (desde, hasta)).Select(d => (DiasElegidos?)d).FirstOrDefault();

    /// <summary>
    /// ← (<paramref name="pasos"/> negativo) y →: un mes (completo, o el de hoy hasta hoy) va al mes de antes o de después; una
    /// semana de lunes a domingo (o la de hoy hasta hoy), a la otra; lo demás, tantos días como tiene. Lo que pase de hoy llega
    /// hasta hoy; vacío si ya no hay a dónde (después de hoy).
    /// </summary>
    public static (DateOnly Desde, DateOnly Hasta)? Mover(DateOnly desde, DateOnly hasta, int pasos, DateOnly hoy)
    {
        if (pasos == 0)
            return (desde, hasta);
        if (pasos > 0 && hasta >= hoy)
            return null;
        DateOnly nuevoDesde, nuevoHasta;
        if (desde != hasta && desde.Day == 1 && (hasta == UltimoDelMes(desde) || (hasta == hoy && PrimeroDelMes(hoy) == desde)))
        {
            nuevoDesde = desde.AddMonths(pasos);
            nuevoHasta = UltimoDelMes(nuevoDesde);
        }
        else if (desde != hasta && desde.DayOfWeek == DayOfWeek.Monday && (hasta == desde.AddDays(6) || (hasta == hoy && PeriodoDeReporte.Lunes(hoy) == desde)))
        {
            nuevoDesde = desde.AddDays(7 * pasos);
            nuevoHasta = nuevoDesde.AddDays(6);
        }
        else
        {
            var largo = hasta.DayNumber - desde.DayNumber + 1;
            (nuevoDesde, nuevoHasta) = (desde.AddDays(largo * pasos), hasta.AddDays(largo * pasos));
        }
        if (nuevoDesde > hoy)
            return null;
        return (nuevoDesde, nuevoHasta > hoy ? hoy : nuevoHasta);
    }

    private static DateOnly PrimeroDelMes(DateOnly dia) => new(dia.Year, dia.Month, 1);

    private static DateOnly UltimoDelMes(DateOnly dia) => PrimeroDelMes(dia).AddMonths(1).AddDays(-1);
}
