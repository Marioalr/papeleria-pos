using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Lo que dicen los reportes de ventas: el periodo, contra qué se compara, cuánto subió o bajó y la gráfica.</summary>
public class TextosDeReportesTests
{
    /// <summary>El miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static readonly CifrasReporteDto Nada = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static ReporteVentasDto Reporte(PeriodoReporte periodo, DateOnly dia, CifrasReporteDto? anterior = null, bool sinAnterior = false, bool piloto = false)
    {
        var p = PeriodoDeReporte.De(periodo, dia, Ahora);
        return new ReporteVentasDto(periodo, p.Desde, p.Hasta.AddDays(-1), DateOnly.FromDateTime(Ahora), p.Ahora, p.DesdeAnterior, p.HastaAnterior.AddDays(-1),
            p.CorteAnterior, Nada, piloto || sinAnterior ? null : anterior ?? Nada, [], [], [], [], [], ConUtilidad: false, piloto);
    }

    private static DateOnly D(int anio, int mes, int dia) => new(anio, mes, dia);

    [Fact]
    public void El_titulo_del_periodo_y_el_de_la_grafica()
    {
        Assert.Equal("Hoy, miércoles 21 de octubre", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21))));
        Assert.Equal("Martes 20 de octubre", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Hoy, D(2026, 10, 20))));
        Assert.Equal("Miércoles 31 de diciembre de 2025", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Hoy, D(2025, 12, 31))));
        Assert.Equal("Esta semana, del 19 al 25 de octubre", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Semana, D(2026, 10, 21))));
        Assert.Equal("Semana del 28 de septiembre al 4 de octubre", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Semana, D(2026, 10, 1))));
        Assert.Equal("Semana del 29 de diciembre de 2025 al 4 de enero", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Semana, D(2026, 1, 1))));
        Assert.Equal("Octubre a la fecha", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Mes, D(2026, 10, 2))));
        Assert.Equal("Septiembre de 2026", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Mes, D(2026, 9, 2))));
        Assert.Equal("2026 a la fecha", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Anio, D(2026, 10, 21))));
        Assert.Equal("2025", TextosDeReportes.Titulo(Reporte(PeriodoReporte.Anio, D(2025, 5, 1))));

        // La gráfica, como en el prototipo.
        Assert.Equal("Hoy, por hora", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21))));
        Assert.Equal("Esta semana, por día", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Semana, D(2026, 10, 21))));
        Assert.Equal("Octubre a la fecha, por día", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Mes, D(2026, 10, 21))));
        Assert.Equal("2026 a la fecha, por mes", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Anio, D(2026, 10, 21))));
        Assert.Equal("Martes 20 de octubre, por hora", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Hoy, D(2026, 10, 20))));
        Assert.Equal("Del 12 al 18 de octubre, por día", TextosDeReportes.TituloDeLaGrafica(Reporte(PeriodoReporte.Semana, D(2026, 10, 14))));
    }

    [Fact]
    public void Contra_que_se_compara()
    {
        Assert.Equal("Contra el miércoles 22 de octubre de 2025, hasta la misma hora",
            TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21))));
        Assert.Equal("Contra la semana del 20 al 26 de octubre de 2025, hasta el mismo día y hora",
            TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Semana, D(2026, 10, 21))));
        Assert.Equal("Contra octubre de 2025, hasta el 21 a la misma hora", TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Mes, D(2026, 10, 21))));
        Assert.Equal("Contra 2025, hasta el 21 de octubre a la misma hora", TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Anio, D(2026, 10, 21))));
        Assert.Equal("Contra septiembre de 2025", TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Mes, D(2026, 9, 21))));
        // Del historial de eleventa; sin nada el año pasado; en el piloto.
        Assert.Equal("Contra 2024 (del historial de eleventa)",
            TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Anio, D(2025, 1, 1), anterior: Nada with { VentaCentavos = 100, DelHistorialCentavos = 100 })));
        Assert.Equal("Sin ventas el año pasado para comparar (el martes 21 de octubre de 2025)",
            TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Hoy, D(2026, 10, 20), sinAnterior: true)));
        Assert.Equal("Sin comparar: en el piloto aquí solo está lo de esta caja", TextosDeReportes.Comparacion(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21), piloto: true)));
    }

    [Fact]
    public void Cuanto_subio_o_bajo()
    {
        Assert.Equal((true, "12.3 %"), TextosDeReportes.Cambio(1123, 1000));
        Assert.Equal((false, "10 %"), TextosDeReportes.Cambio(900, 1000));
        Assert.Equal((true, "0 %"), TextosDeReportes.Cambio(1000, 1000));
        Assert.Null(TextosDeReportes.Cambio(1000, 0));
        Assert.Null(TextosDeReportes.Cambio(5, null));

        Assert.Equal("+12 %", TextosDeReportes.CambioCorto(1120, 1000));
        Assert.Equal("−12 %", TextosDeReportes.CambioCorto(880, 1000));
        Assert.Equal("nuevo", TextosDeReportes.CambioCorto(500, 0));
        Assert.Equal("—", TextosDeReportes.CambioCorto(0, 0));
        Assert.Equal("—", TextosDeReportes.CambioCorto(5, null));
    }

    [Fact]
    public void La_grafica_sus_cifras_etiquetas_y_detalles()
    {
        Assert.Equal("$850", TextosDeReportes.Compacto(85_000));
        Assert.Equal("$1 mil", TextosDeReportes.Compacto(100_000));
        Assert.Equal("$12.5 mil", TextosDeReportes.Compacto(1_250_000));
        Assert.Equal("$1.2 M", TextosDeReportes.Compacto(120_000_000));
        Assert.Equal("−$5 mil", TextosDeReportes.Compacto(-500_000));

        Assert.Equal(("9", "de 9:00 a 10:00"), (TextosDeReportes.Etiqueta(PeriodoReporte.Hoy, new DateTime(2026, 10, 21, 9, 0, 0)),
            TextosDeReportes.Detalle(PeriodoReporte.Hoy, new DateTime(2026, 10, 21, 9, 0, 0))));
        Assert.Equal(("L", "lunes 19 oct"), (TextosDeReportes.Etiqueta(PeriodoReporte.Semana, new DateTime(2026, 10, 19)),
            TextosDeReportes.Detalle(PeriodoReporte.Semana, new DateTime(2026, 10, 19))));
        Assert.Equal(("21", "mié 21 oct"), (TextosDeReportes.Etiqueta(PeriodoReporte.Mes, new DateTime(2026, 10, 21)),
            TextosDeReportes.Detalle(PeriodoReporte.Mes, new DateTime(2026, 10, 21))));
        Assert.Equal(("O", "octubre"), (TextosDeReportes.Etiqueta(PeriodoReporte.Anio, new DateTime(2026, 10, 1)),
            TextosDeReportes.Detalle(PeriodoReporte.Anio, new DateTime(2026, 10, 1))));
        Assert.Equal("mié 22 oct 2025", TextosDeReportes.DetalleAnterior(PeriodoReporte.Hoy, new DateTime(2025, 10, 22, 9, 0, 0)));
        Assert.Equal("octubre de 2025", TextosDeReportes.DetalleAnterior(PeriodoReporte.Anio, new DateTime(2025, 10, 1)));
        // Un febrero de 28 días contra uno de 29: el 28 lleva también el 29 del año pasado (revisión del PR #131).
        Assert.Equal("lun 28 y mar 29 feb 2028", TextosDeReportes.DetalleAnterior(PeriodoReporte.Mes, new DateTime(2028, 2, 28), D(2029, 2, 1)));
        Assert.Equal("dom 27 feb 2028", TextosDeReportes.DetalleAnterior(PeriodoReporte.Mes, new DateTime(2028, 2, 27), D(2029, 2, 1)));
        Assert.Equal("dom 28 feb 2027", TextosDeReportes.DetalleAnterior(PeriodoReporte.Mes, new DateTime(2027, 2, 28), D(2028, 2, 1)));
    }

    /// <summary>
    /// Lo que dice una cifra de arriba cuando no puede decir cuánto subió o bajó (revisión del PR #131): lo que falta en este
    /// periodo no es lo que no hubo el año pasado, una utilidad del año pasado en cero o negativa no es «sin ventas», y «a estas
    /// alturas» solo va con un periodo a la fecha.
    /// </summary>
    [Fact]
    public void Por_que_una_cifra_no_dice_cuanto_subio_o_bajo()
    {
        var hoy = Reporte(PeriodoReporte.Hoy, D(2026, 10, 21), anterior: Nada with { VentaCentavos = 12345, Tickets = 1 });
        var ayer = Reporte(PeriodoReporte.Hoy, D(2026, 10, 20), anterior: Nada);

        // Con algo que comparar, lo dice el cambio.
        Assert.Null(TextosDeReportes.SinCambio(hoy, 0, 12345, "sin ventas", enPesos: true));
        // Hoy sin tickets no hay ticket promedio; el año pasado a estas alturas, sí.
        Assert.Equal("sin tickets todavía", TextosDeReportes.SinCambio(hoy, null, 12345, "sin tickets", enPesos: true));
        Assert.Equal("sin tickets en este periodo", TextosDeReportes.SinCambio(ayer, null, null, "sin tickets", enPesos: true));
        // Lo que no hubo el año pasado.
        Assert.Equal("sin tickets el año pasado a estas alturas", TextosDeReportes.SinCambio(hoy, 2500, null, "sin tickets", enPesos: true));
        Assert.Equal("sin ventas el año pasado a estas alturas", TextosDeReportes.SinCambio(hoy, 500, 0, "sin ventas", enPesos: true));
        Assert.Equal("sin ventas el año pasado", TextosDeReportes.SinCambio(ayer, 500, 0, "sin ventas", enPesos: true));
        // La utilidad del año pasado en cero o negativa.
        Assert.Equal("sin utilidad el año pasado a estas alturas", TextosDeReportes.SinCambio(hoy, 3000, 0, "sin utilidad", enPesos: true));
        Assert.Equal("el año pasado a estas alturas: −$12.00", TextosDeReportes.SinCambio(hoy, 3000, -1200, "sin utilidad", enPesos: true));
        // Sin nada en todo el periodo del año pasado, y en el piloto.
        Assert.Equal("sin ventas el año pasado", TextosDeReportes.SinCambio(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21), sinAnterior: true), 500, null, "sin ventas"));
        Assert.Equal("sin comparar en el piloto", TextosDeReportes.SinCambio(Reporte(PeriodoReporte.Hoy, D(2026, 10, 21), piloto: true), null, null, "sin tickets"));
    }
}
