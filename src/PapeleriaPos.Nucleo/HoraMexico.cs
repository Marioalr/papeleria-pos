namespace PapeleriaPos.Nucleo;

/// <summary>
/// Conversión entre UTC (como se guarda todo) y la hora de Ciudad de México (como se muestra). La zona trae el
/// horario de verano que hubo hasta octubre de 2022, así que sirve también para fechas viejas de eleventa.
/// </summary>
public static class HoraMexico
{
    public static readonly TimeZoneInfo Zona = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");

    public static DateTime AUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // La hora que no existió al adelantar el reloj en primavera se toma como la siguiente.
        if (Zona.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zona);
    }

    public static DateTime ALocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zona);
}
