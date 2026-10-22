using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que dicen los reportes de ventas (entrega 9, docs/propuesta-interfaz.md «Reportes»): qué periodo se ve, contra qué se
/// compara, cuánto subió o bajó y las etiquetas de la gráfica. Los nombres de los días y los meses van aquí, en minúsculas como
/// se escriben en español, para que digan lo mismo en Windows y en las pruebas.
/// </summary>
public static class TextosDeReportes
{
    /// <summary>Los periodos, como en el prototipo.</summary>
    public static IReadOnlyList<(PeriodoReporte Periodo, string Nombre)> Periodos { get; } =
        [(PeriodoReporte.Hoy, "Hoy"), (PeriodoReporte.Semana, "Semana"), (PeriodoReporte.Mes, "Mes"), (PeriodoReporte.Anio, "Año a la fecha")];

    private static readonly string[] Dias = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];
    private static readonly string[] DiasCortos = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];
    private static readonly string[] LetraDelDia = ["D", "L", "M", "M", "J", "V", "S"];

    private static readonly string[] Meses =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static readonly string[] MesesCortos = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    private static string Mayuscula(string texto) => char.ToUpper(texto[0], Formato.Mexico) + texto[1..];

    /// <summary>«miércoles 21 de octubre», con el año si no es <paramref name="anio"/>.</summary>
    public static string Dia(DateOnly dia, int? anio = null) =>
        $"{Dias[(int)dia.DayOfWeek]} {dia.Day} de {Meses[dia.Month - 1]}" + (anio is { } a && a == dia.Year ? "" : $" de {dia.Year}");

    /// <summary>«del 19 al 25 de octubre», «del 29 de septiembre al 5 de octubre», con el año si no es <paramref name="anio"/>.</summary>
    private static string DelAl(DateOnly desde, DateOnly hasta, int? anio = null)
    {
        var deAnio = anio is { } a && a == hasta.Year ? "" : $" de {hasta.Year}";
        return desde.Month == hasta.Month
            ? $"del {desde.Day} al {hasta.Day} de {Meses[hasta.Month - 1]}{deAnio}"
            : $"del {desde.Day} de {Meses[desde.Month - 1]}{(desde.Year != hasta.Year ? $" de {desde.Year}" : "")} al {hasta.Day} de {Meses[hasta.Month - 1]}{deAnio}";
    }

    /// <summary>
    /// El periodo que se ve: «Hoy, miércoles 21 de octubre», «Martes 20 de octubre», «Esta semana, del 19 al 25 de octubre»,
    /// «Semana del 12 al 18 de octubre», «Octubre a la fecha», «Septiembre de 2026», «2026 a la fecha», «2025».
    /// </summary>
    public static string Titulo(ReporteVentasDto r) => r.Periodo switch
    {
        PeriodoReporte.Hoy => r.ALaFecha ? $"Hoy, {Dia(r.Desde, r.Hoy.Year)}" : Mayuscula(Dia(r.Desde, r.Hoy.Year)),
        PeriodoReporte.Semana => r.ALaFecha ? $"Esta semana, {DelAl(r.Desde, r.Hasta, r.Hoy.Year)}" : $"Semana {DelAl(r.Desde, r.Hasta, r.Hoy.Year)}",
        PeriodoReporte.Mes => r.ALaFecha ? $"{Mayuscula(Meses[r.Desde.Month - 1])} a la fecha" : $"{Mayuscula(Meses[r.Desde.Month - 1])} de {r.Desde.Year}",
        _ => r.ALaFecha ? $"{r.Desde.Year} a la fecha" : r.Desde.Year.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// El título de la gráfica, como en el prototipo: «Hoy, por hora», «Esta semana, por día», «Octubre a la fecha, por día»,
    /// «2026 a la fecha, por mes»; de un periodo que ya pasó, el periodo: «Martes 20 de octubre, por hora».
    /// </summary>
    public static string TituloDeLaGrafica(ReporteVentasDto r)
    {
        var porQue = r.Periodo switch
        {
            PeriodoReporte.Hoy => "por hora",
            PeriodoReporte.Anio => "por mes",
            _ => "por día",
        };
        var cual = r.Periodo switch
        {
            PeriodoReporte.Hoy when r.ALaFecha => "Hoy",
            PeriodoReporte.Semana when r.ALaFecha => "Esta semana",
            PeriodoReporte.Semana => Mayuscula(DelAl(r.Desde, r.Hasta, r.Hoy.Year)),
            _ => Titulo(r),
        };
        return $"{cual}, {porQue}";
    }

    /// <summary>
    /// Contra qué se compara: «Contra el miércoles 22 de octubre de 2025, hasta la misma hora», «Contra la semana del 20 al 26
    /// de octubre de 2025, hasta el mismo día y hora», «Contra octubre de 2025, hasta el 21 a la misma hora», «Contra 2025, hasta
    /// el 21 de octubre a la misma hora». Sin con qué comparar, lo dice.
    /// </summary>
    public static string Comparacion(ReporteVentasDto r)
    {
        if (r.Piloto)
            return "Sin comparar: en el piloto aquí solo está lo de esta caja";
        var contra = r.Periodo switch
        {
            PeriodoReporte.Hoy => $"el {Dia(r.DesdeAnterior)}",
            PeriodoReporte.Semana => $"la semana {DelAl(r.DesdeAnterior, r.HastaAnterior)}",
            PeriodoReporte.Mes => $"{Meses[r.DesdeAnterior.Month - 1]} de {r.DesdeAnterior.Year}",
            _ => r.DesdeAnterior.Year.ToString(CultureInfo.InvariantCulture),
        };
        var hasta = !r.ALaFecha || r.AhoraLocal is not { } ahora ? "" : r.Periodo switch
        {
            PeriodoReporte.Hoy => ", hasta la misma hora",
            PeriodoReporte.Semana => ", hasta el mismo día y hora",
            PeriodoReporte.Mes => $", hasta el {ahora.Day} a la misma hora",
            _ => $", hasta el {ahora.Day} de {Meses[ahora.Month - 1]} a la misma hora",
        };
        var historial = r.Anterior is { DelHistorialCentavos: not 0 } ? " (del historial de eleventa)" : "";
        return r.Anterior is null ? $"Sin ventas el año pasado para comparar ({contra})" : $"Contra {contra}{hasta}{historial}";
    }

    /// <summary>Cuánto subió o bajó, en porcentaje con un decimal («12.3 %»); vacío sin con qué comparar (o con nada antes).</summary>
    public static (bool Sube, string Porcentaje)? Cambio(decimal actual, decimal? anterior)
    {
        if (anterior is not { } antes || antes <= 0)
            return null;
        var cambio = Math.Round((actual - antes) * 100 / antes, 1, MidpointRounding.AwayFromZero);
        return (cambio >= 0, $"{Math.Abs(cambio).ToString("0.#", Formato.Mexico)} %");
    }

    /// <summary>Para las tablas, en enteros: «+12 %», «−8 %», «nuevo» (sin nada el año pasado) o «—».</summary>
    public static string CambioCorto(long actual, long? anterior) => anterior switch
    {
        null => "—",
        <= 0 when actual > 0 => "nuevo",
        <= 0 => "—",
        { } antes => Math.Round((actual - antes) * 100m / antes, MidpointRounding.AwayFromZero) is var c && c >= 0
            ? $"+{c.ToString("0", Formato.Mexico)} %" : $"−{Math.Abs(c).ToString("0", Formato.Mexico)} %",
    };

    /// <summary>La etiqueta de un punto en el eje de la gráfica: la hora («9»), el día de la semana («L»), del mes («21») o el mes («O»).</summary>
    public static string Etiqueta(PeriodoReporte periodo, DateTime inicio) => periodo switch
    {
        PeriodoReporte.Hoy => inicio.Hour.ToString(CultureInfo.InvariantCulture),
        PeriodoReporte.Semana => LetraDelDia[(int)inicio.DayOfWeek],
        PeriodoReporte.Mes => inicio.Day.ToString(CultureInfo.InvariantCulture),
        _ => Meses[inicio.Month - 1][..1].ToUpper(Formato.Mexico),
    };

    /// <summary>De qué es un punto, en el cuadro de la gráfica y en la tabla: «de 9:00 a 10:00», «lunes 19 oct», «mié 21 oct», «octubre».</summary>
    public static string Detalle(PeriodoReporte periodo, DateTime inicio) => periodo switch
    {
        PeriodoReporte.Hoy => $"de {inicio.Hour}:00 a {inicio.Hour + 1}:00",
        PeriodoReporte.Semana => $"{Dias[(int)inicio.DayOfWeek]} {inicio.Day} {MesesCortos[inicio.Month - 1]}",
        PeriodoReporte.Mes => $"{DiasCortos[(int)inicio.DayOfWeek]} {inicio.Day} {MesesCortos[inicio.Month - 1]}",
        _ => Meses[inicio.Month - 1],
    };

    /// <summary>El punto del año pasado que le toca: «mié 22 oct 2025» o «octubre de 2025».</summary>
    public static string DetalleAnterior(PeriodoReporte periodo, DateTime inicio) => periodo == PeriodoReporte.Anio
        ? $"{Meses[inicio.Month - 1]} de {inicio.Year}"
        : $"{DiasCortos[(int)inicio.DayOfWeek]} {inicio.Day} {MesesCortos[inicio.Month - 1]} {inicio.Year}";

    /// <summary>Las cifras del eje: «$850», «$12.5 mil», «$1.2 M».</summary>
    public static string Compacto(long centavos)
    {
        var pesos = centavos / 100m;
        var signo = pesos < 0 ? "−" : "";
        pesos = Math.Abs(pesos);
        return signo + (pesos >= 1_000_000 ? $"${(pesos / 1_000_000).ToString("0.#", Formato.Mexico)} M"
            : pesos >= 1_000 ? $"${(pesos / 1_000).ToString("0.#", Formato.Mexico)} mil"
            : $"${Math.Round(pesos).ToString("0", Formato.Mexico)}");
    }

    /// <summary>
    /// De dónde sale la venta neta: lo cobrado menos lo devuelto, sin los cancelados, como en el corte del día. Lo dicen la pantalla
    /// y el Excel.
    /// </summary>
    public static string VentaNeta(CifrasReporteDto a)
    {
        var partes = $"Venta neta: lo cobrado ({Formato.Dinero(a.VendidoCentavos)})";
        if (a.DevueltoCentavos != 0)
            partes += $" menos lo devuelto ({Formato.Dinero(a.DevueltoCentavos)}{(a.Devoluciones == 1 ? ", 1 devolución" : $", {a.Devoluciones} devoluciones")})";
        partes += a.Cancelados switch
        {
            0 => "; los tickets cancelados no cuentan",
            1 => $"; el ticket cancelado ({Formato.Dinero(a.CanceladosCentavos)}) no cuenta",
            _ => $"; los {a.Cancelados} tickets cancelados ({Formato.Dinero(a.CanceladosCentavos)}) no cuentan",
        };
        return partes + ", como en el corte del día.";
    }

    /// <summary>Las notas de abajo: de dónde sale cada cosa. Las dicen la pantalla y el Excel.</summary>
    public static IEnumerable<string> Notas(ReporteVentasDto r)
    {
        if (r.Anterior is { DelHistorialCentavos: not 0 })
            yield return "El año pasado sale del historial de eleventa: sus tickets, con lo que eleventa les bajó al devolver"
                + (r.ConUtilidad ? "; su utilidad, con el costo que se capturó en eleventa." : ".");
        yield return r.Actual.DelHistorialCentavos != 0
            ? $"De este periodo, {Formato.Dinero(r.Actual.DelHistorialCentavos)} ({Formato.Numero(r.Actual.DelHistorialTickets)} tickets) salen del "
              + "historial de eleventa, que no trae el cajero ni la forma de pago: esas tablas son de lo vendido aquí."
            : "El cajero y la forma de pago son de lo vendido aquí: el historial de eleventa no los trae. Lo devuelto se resta de la forma "
              + "en que se regresó: a la cuenta del cliente (lo fiado) o en efectivo.";
        yield return r.Piloto
            ? "En el piloto el corte se hace en eleventa: aquí no hay diferencias de corte."
            : "Las diferencias son las de los cortes del turno hechos en el periodo, a nombre del cajero del turno.";
        if (r.ConUtilidad && r.Actual.Utilidad is { } u)
            yield return "La utilidad es lo vendido menos el costo que guardó cada venta al cobrar (el promedio del inventario; el de las "
                + "impresiones, el de cada trabajo), con lo devuelto restado. El artículo común no tiene costo: no entra."
                + (u.ServiciosSinCostoCentavos != 0
                    ? $" Los servicios ({Formato.Dinero(u.ServiciosSinCostoCentavos)}) todavía no tienen costo: cuentan completos, como en el corte del día."
                    : "");
    }

    // ── Ventas por período (entrega 9, parte 2) ──

    /// <summary>
    /// Los días de «Ventas por período»: «Hoy, jueves 22 de octubre», «Martes 20 de octubre», «Del 1 al 22 de octubre», «Del 29
    /// de septiembre al 5 de octubre»; con el año si no es el de hoy (o siempre, con <paramref name="conAnio"/>: el Excel y el
    /// papel se guardan).
    /// </summary>
    public static string Periodo(DateOnly desde, DateOnly hasta, DateOnly hoy, bool conAnio = false)
    {
        int? anio = conAnio ? null : hoy.Year;
        if (desde == hasta)
            return desde == hoy ? $"Hoy, {Dia(desde, anio)}" : Mayuscula(Dia(desde, anio));
        return Mayuscula(DelAl(desde, hasta, anio));
    }

    /// <summary>Cómo se pagó un ticket: «Efectivo», «Efectivo y tarjeta», «Tarjeta, transferencia y crédito».</summary>
    public static string FormasDePago(IReadOnlyList<FormaPago> formas)
    {
        var nombres = formas.Select(f => f switch
        {
            FormaPago.Tarjeta => "tarjeta",
            FormaPago.Transferencia => "transferencia",
            FormaPago.Credito => "crédito",
            _ => "efectivo",
        }).ToList();
        if (nombres.Count == 0)
            return "Sin cobro";
        return Mayuscula(nombres.Count == 1 ? nombres[0] : $"{string.Join(", ", nombres.Take(nombres.Count - 1))} y {nombres[^1]}");
    }

    /// <summary>Si la venta se canceló o se devolvió, como en «Ventas del día»: «Cancelada», «Devuelta», «Devuelta en parte»; vacío si no.</summary>
    public static string? Estado(VentaDeUnDiaDto v) =>
        v.Cancelada ? "Cancelada" : v.DevueltoCentavos <= 0 ? null : v.DevueltaCompleta ? "Devuelta" : "Devuelta en parte";

    /// <summary>Cómo se llama cada forma de pago en el reporte.</summary>
    public static string Forma(FormaPago forma) => forma switch
    {
        FormaPago.Efectivo => "Efectivo",
        FormaPago.Tarjeta => "Tarjeta",
        FormaPago.Transferencia => "Transferencia",
        _ => "Crédito (fiado)",
    };
}
