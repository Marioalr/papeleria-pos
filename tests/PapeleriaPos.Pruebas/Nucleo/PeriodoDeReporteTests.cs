using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Los días de los reportes de ventas y con qué se comparan (decidido el 2026-10-22, por revisar): hoy y la semana con 52
/// semanas antes (el mismo día de la semana), el mes y el año con las mismas fechas; la semana de lunes a domingo; a la fecha,
/// hasta el mismo momento.
/// </summary>
public class PeriodoDeReporteTests
{
    /// <summary>El miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static DateOnly D(int anio, int mes, int dia) => new(anio, mes, dia);

    [Fact]
    public void Hoy_y_la_semana_se_comparan_con_52_semanas_antes_el_mismo_dia_de_la_semana()
    {
        var hoy = PeriodoDeReporte.De(PeriodoReporte.Hoy, DateOnly.FromDateTime(Ahora), Ahora);
        var semana = PeriodoDeReporte.De(PeriodoReporte.Semana, DateOnly.FromDateTime(Ahora), Ahora);

        Assert.Equal((D(2026, 10, 21), D(2026, 10, 22), D(2025, 10, 22), D(2025, 10, 23)), (hoy.Desde, hoy.Hasta, hoy.DesdeAnterior, hoy.HastaAnterior));
        Assert.Equal((DayOfWeek.Wednesday, DayOfWeek.Wednesday), (hoy.Desde.DayOfWeek, hoy.DesdeAnterior.DayOfWeek));
        Assert.Equal((true, Ahora, new DateTime(2025, 10, 22, 13, 30, 0)), (hoy.ALaFecha, hoy.Ahora!.Value, hoy.CorteAnterior!.Value));
        // De lunes a domingo; el año pasado, también de lunes, hasta el miércoles a la 1:30.
        Assert.Equal((D(2026, 10, 19), D(2026, 10, 26), D(2025, 10, 20), D(2025, 10, 27)), (semana.Desde, semana.Hasta, semana.DesdeAnterior, semana.HastaAnterior));
        Assert.Equal(new DateTime(2025, 10, 22, 13, 30, 0), semana.CorteAnterior);
        Assert.Equal(DayOfWeek.Monday, semana.DesdeAnterior.DayOfWeek);
    }

    [Fact]
    public void El_mes_y_el_anio_se_comparan_con_las_mismas_fechas()
    {
        var mes = PeriodoDeReporte.De(PeriodoReporte.Mes, D(2026, 10, 5), Ahora);
        var anio = PeriodoDeReporte.De(PeriodoReporte.Anio, D(2026, 3, 1), Ahora);

        // Cualquier día del mes da el mismo mes; como incluye hoy, va a la fecha.
        Assert.Equal((D(2026, 10, 1), D(2026, 11, 1), D(2025, 10, 1), D(2025, 11, 1)), (mes.Desde, mes.Hasta, mes.DesdeAnterior, mes.HastaAnterior));
        Assert.Equal(new DateTime(2025, 10, 21, 13, 30, 0), mes.CorteAnterior);
        Assert.Equal((D(2026, 1, 1), D(2027, 1, 1), D(2025, 1, 1), D(2026, 1, 1)), (anio.Desde, anio.Hasta, anio.DesdeAnterior, anio.HastaAnterior));
        Assert.Equal(new DateTime(2025, 10, 21, 13, 30, 0), anio.CorteAnterior);
    }

    [Fact]
    public void Un_periodo_que_ya_paso_se_compara_completo()
    {
        var septiembre = PeriodoDeReporte.De(PeriodoReporte.Mes, D(2026, 9, 15), Ahora);
        var ayer = PeriodoDeReporte.De(PeriodoReporte.Hoy, D(2026, 10, 20), Ahora);

        Assert.Equal((false, (DateTime?)null, (DateTime?)null), (septiembre.ALaFecha, septiembre.Ahora, septiembre.CorteAnterior));
        Assert.Equal((D(2025, 9, 1), D(2025, 10, 1)), (septiembre.DesdeAnterior, septiembre.HastaAnterior));
        Assert.False(ayer.ALaFecha);
        Assert.Equal(D(2025, 10, 21), ayer.DesdeAnterior);
    }

    /// <summary>El 29 de febrero no existe el año anterior: el mes y el año se comparan hasta el final del 28.</summary>
    [Fact]
    public void Un_29_de_febrero_se_compara_hasta_el_final_del_28()
    {
        var ahora = new DateTime(2028, 2, 29, 10, 0, 0);

        var mes = PeriodoDeReporte.De(PeriodoReporte.Mes, DateOnly.FromDateTime(ahora), ahora);
        var anio = PeriodoDeReporte.De(PeriodoReporte.Anio, DateOnly.FromDateTime(ahora), ahora);
        var hoy = PeriodoDeReporte.De(PeriodoReporte.Hoy, DateOnly.FromDateTime(ahora), ahora);

        Assert.Equal(new DateTime(2027, 3, 1), mes.CorteAnterior);
        Assert.Equal(new DateTime(2027, 3, 1), anio.CorteAnterior);
        // Hoy, por día de la semana: el martes de hace 52 semanas, a la misma hora.
        Assert.Equal((D(2027, 3, 2), new DateTime(2027, 3, 2, 10, 0, 0)), (hoy.DesdeAnterior, hoy.CorteAnterior!.Value));
        // La gráfica del mes: 29 días; el 29 no existe el año pasado.
        Assert.Equal(29, mes.Cubetas.Count());
        Assert.Equal(new DateTime(2027, 2, 28), mes.Inicio(28, anterior: true));
        Assert.Null(mes.Inicio(29, anterior: true));
        // El día siguiente ya no: un año antes, el 1 de marzo.
        Assert.Equal(new DateTime(2027, 3, 1, 10, 0, 0), PeriodoDeReporte.De(PeriodoReporte.Mes, D(2028, 3, 1), new DateTime(2028, 3, 1, 10, 0, 0)).CorteAnterior);
    }

    [Fact]
    public void La_semana_empieza_en_lunes_y_las_flechas_mueven_un_periodo()
    {
        Assert.Equal(D(2026, 10, 19), PeriodoDeReporte.Lunes(D(2026, 10, 25)));
        Assert.Equal(D(2026, 10, 19), PeriodoDeReporte.Lunes(D(2026, 10, 19)));
        Assert.Equal(D(2025, 12, 29), PeriodoDeReporte.Lunes(D(2026, 1, 1)));

        Assert.Equal(D(2026, 10, 20), PeriodoDeReporte.Mover(PeriodoReporte.Hoy, D(2026, 10, 21), -1));
        Assert.Equal(D(2026, 10, 14), PeriodoDeReporte.Mover(PeriodoReporte.Semana, D(2026, 10, 21), -1));
        Assert.Equal(D(2026, 9, 30), PeriodoDeReporte.Mover(PeriodoReporte.Mes, D(2026, 10, 31), -1));
        Assert.Equal(D(2027, 10, 21), PeriodoDeReporte.Mover(PeriodoReporte.Anio, D(2026, 10, 21), 1));
    }

    [Fact]
    public void Las_cubetas_de_la_grafica()
    {
        var hoy = PeriodoDeReporte.De(PeriodoReporte.Hoy, D(2026, 10, 21), Ahora);
        var semana = PeriodoDeReporte.De(PeriodoReporte.Semana, D(2026, 10, 21), Ahora);
        var anio = PeriodoDeReporte.De(PeriodoReporte.Anio, D(2026, 10, 21), Ahora);

        Assert.Equal((13, 13), (hoy.Cubeta(Ahora), hoy.Cubeta(new DateTime(2025, 10, 22, 13, 59, 0), anterior: true)));
        Assert.Equal((new DateTime(2026, 10, 21, 13, 0, 0), new DateTime(2026, 10, 21, 14, 0, 0)), (hoy.Inicio(13)!.Value, hoy.Fin(13)));
        // La semana: 0 es el lunes, aquí y hace 52 semanas.
        Assert.Equal((2, 3), (semana.Cubeta(Ahora), semana.Cubeta(new DateTime(2025, 10, 23, 9, 0, 0), anterior: true)));
        Assert.Equal(new DateTime(2025, 10, 23), semana.Inicio(3, anterior: true));
        Assert.Equal((10, 12), (anio.Cubeta(Ahora), anio.Cubetas.Count()));
        Assert.Equal(new DateTime(2026, 11, 1), anio.Fin(10));
    }
}
