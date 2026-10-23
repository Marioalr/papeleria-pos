using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Los días de «Ventas por período» (entrega 9, parte 2): los de un toque, cuál es lo que se ve, y ← → a los de antes o de
/// después del largo que se eligió (aunque los que se ven estén cortados en hoy o empiecen un día 1), sin pasar de hoy ni de
/// antes de 2000 y sin desbordarse con un año mal escrito (revisión del PR #134).
/// </summary>
public sealed class DiasDeVentasTests
{
    /// <summary>Miércoles 21 de octubre de 2026.</summary>
    private static readonly DateOnly Hoy = new(2026, 10, 21);

    private static DateOnly D(int mes, int dia, int anio = 2026) => new(anio, mes, dia);

    private static readonly LargoDeVentas UnDia = LargoDeVentas.DeDias(1);

    [Fact]
    public void Los_de_un_toque_su_largo_y_cual_es_lo_que_se_ve()
    {
        Assert.Equal((Hoy, Hoy), DiasDeVentas.De(DiasElegidos.Hoy, Hoy));
        Assert.Equal((D(10, 20), D(10, 20)), DiasDeVentas.De(DiasElegidos.Ayer, Hoy));
        Assert.Equal((D(10, 19), Hoy), DiasDeVentas.De(DiasElegidos.EstaSemana, Hoy));
        Assert.Equal((D(10, 1), Hoy), DiasDeVentas.De(DiasElegidos.EsteMes, Hoy));
        Assert.Equal((D(9, 1), D(9, 30)), DiasDeVentas.De(DiasElegidos.MesPasado, Hoy));
        // El mes pasado de enero es diciembre del año anterior.
        Assert.Equal((D(12, 1, 2025), D(12, 31, 2025)), DiasDeVentas.De(DiasElegidos.MesPasado, D(1, 15)));

        Assert.Equal([UnDia, UnDia, LargoDeVentas.Semana, LargoDeVentas.Mes, LargoDeVentas.Mes],
            Enum.GetValues<DiasElegidos>().Select(DiasDeVentas.LargoDe));

        Assert.Equal(DiasElegidos.EsteMes, DiasDeVentas.Cual(D(10, 1), Hoy, Hoy));
        Assert.Equal(DiasElegidos.MesPasado, DiasDeVentas.Cual(D(9, 1), D(9, 30), Hoy));
        Assert.Null(DiasDeVentas.Cual(D(10, 3), D(10, 9), Hoy));
        // El día 1, «Hoy» y «Este mes» son el mismo día: con el largo se sabe cuál es.
        var primero = D(11, 1);
        Assert.Equal(DiasElegidos.Hoy, DiasDeVentas.Cual(primero, primero, primero, UnDia));
        Assert.Equal(DiasElegidos.EsteMes, DiasDeVentas.Cual(primero, primero, primero, LargoDeVentas.Mes));
    }

    [Fact]
    public void El_largo_de_unas_fechas_escritas()
    {
        Assert.Equal(LargoDeVentas.Mes, DiasDeVentas.LargoDe(D(2, 1), D(2, 28)));
        Assert.Equal(LargoDeVentas.Semana, DiasDeVentas.LargoDe(D(10, 12), D(10, 18)));
        Assert.Equal(LargoDeVentas.DeDias(11), DiasDeVentas.LargoDe(D(10, 10), D(10, 20)));
        // Del 1 a hoy no es un mes completo: son tantos días.
        Assert.Equal(LargoDeVentas.DeDias(21), DiasDeVentas.LargoDe(D(10, 1), Hoy));
    }

    [Fact]
    public void Las_flechas_llevan_al_mismo_largo_de_antes_o_de_despues_sin_pasar_de_hoy()
    {
        // Un día: el de antes; hoy no tiene siguiente.
        Assert.Equal((D(10, 20), D(10, 20)), DiasDeVentas.Mover(UnDia, Hoy, -1, Hoy));
        Assert.Null(DiasDeVentas.Mover(UnDia, Hoy, 1, Hoy));
        Assert.Equal((Hoy, Hoy), DiasDeVentas.Mover(UnDia, D(10, 20), 1, Hoy));
        // Este mes a la fecha: el mes pasado completo; y de ahí, otra vez este mes hasta hoy.
        Assert.Equal((D(9, 1), D(9, 30)), DiasDeVentas.Mover(LargoDeVentas.Mes, D(10, 1), -1, Hoy));
        Assert.Equal((D(10, 1), Hoy), DiasDeVentas.Mover(LargoDeVentas.Mes, D(9, 1), 1, Hoy));
        // De marzo a febrero, con sus 28 días.
        Assert.Equal((D(2, 1), D(2, 28)), DiasDeVentas.Mover(LargoDeVentas.Mes, D(3, 1), -1, Hoy));
        // Esta semana a la fecha: la de antes, de lunes a domingo; y de regreso, hasta hoy.
        Assert.Equal((D(10, 12), D(10, 18)), DiasDeVentas.Mover(LargoDeVentas.Semana, D(10, 19), -1, Hoy));
        Assert.Equal((D(10, 19), Hoy), DiasDeVentas.Mover(LargoDeVentas.Semana, D(10, 12), 1, Hoy));
        // Unos días escritos: tantos como tienen.
        Assert.Equal((D(9, 26), D(10, 2)), DiasDeVentas.Mover(LargoDeVentas.DeDias(7), D(10, 3), -1, Hoy));
        Assert.Equal((D(10, 17), Hoy), DiasDeVentas.Mover(LargoDeVentas.DeDias(7), D(10, 10), 1, Hoy));
    }

    /// <summary>Lo que encontró la revisión del PR #134: las flechas adivinaban el largo de las fechas y lo perdían.</summary>
    [Fact]
    public void El_largo_se_queda_aunque_las_fechas_esten_cortadas_o_empiecen_un_dia_1()
    {
        // «Esta semana» que empieza el lunes 1 de junio (igual que el mes): ← es la semana de antes, no mayo completo.
        var jueves = new DateOnly(2026, 6, 4);
        var semana = DiasDeVentas.De(DiasElegidos.EstaSemana, jueves);
        Assert.Equal((D(5, 25), D(5, 31)), DiasDeVentas.Mover(LargoDeVentas.Semana, semana.Desde, -1, jueves));

        // Once días (del 10 al 20): → se corta en hoy (del 21 al 21), y ← regresa a los once días, no a uno.
        var once = LargoDeVentas.DeDias(11);
        var despues = DiasDeVentas.Mover(once, D(10, 10), 1, Hoy)!.Value;
        Assert.Equal((D(10, 21), Hoy), despues);
        Assert.Equal((D(10, 10), D(10, 20)), DiasDeVentas.Mover(once, despues.Desde, -1, Hoy));

        // «Este mes» el día 1 (un solo día) y «Esta semana» un lunes: ← es el mes o la semana de antes completos.
        var primero = D(11, 1);
        var mes = DiasDeVentas.De(DiasElegidos.EsteMes, primero);
        Assert.Equal((D(10, 1), D(10, 31)), DiasDeVentas.Mover(DiasDeVentas.LargoDe(DiasElegidos.EsteMes), mes.Desde, -1, primero));
        var lunes = D(10, 19);
        var deLunes = DiasDeVentas.De(DiasElegidos.EstaSemana, lunes);
        Assert.Equal((lunes, lunes), deLunes);
        Assert.Equal((D(10, 12), D(10, 18)), DiasDeVentas.Mover(DiasDeVentas.LargoDe(DiasElegidos.EstaSemana), deLunes.Desde, -1, lunes));
    }

    [Fact]
    public void Un_anio_mal_escrito_no_se_desborda_y_antes_de_2000_no_hay()
    {
        // «26» en el año: no hay a dónde (antes tronaba con ArgumentOutOfRangeException al querer ir 730 mil días antes).
        var largo = DiasDeVentas.LargoDe(new DateOnly(26, 10, 1), Hoy);
        Assert.Null(DiasDeVentas.Mover(largo, new DateOnly(26, 10, 1), -1, Hoy));
        Assert.Null(DiasDeVentas.Mover(LargoDeVentas.Mes, DateOnly.MinValue, -1, Hoy));
        // Un largo enorme tampoco: empieza en el primer día que se puede pedir.
        Assert.Equal((DiasDeVentas.PrimerDia, D(9, 30)), DiasDeVentas.Mover(LargoDeVentas.DeDias(int.MaxValue), D(10, 1), -1, Hoy));
        // Lo de antes del 1 de enero de 2000 empieza en él; antes, ya no hay.
        Assert.Equal((DiasDeVentas.PrimerDia, D(1, 4, 2000)), DiasDeVentas.Mover(LargoDeVentas.DeDias(10), D(1, 5, 2000), -1, Hoy));
        Assert.Null(DiasDeVentas.Mover(LargoDeVentas.Mes, DiasDeVentas.PrimerDia, -1, Hoy));
        Assert.Null(DiasDeVentas.Mover(LargoDeVentas.Semana, DiasDeVentas.PrimerDia, -1, Hoy));
        // Hasta tres años a la vez.
        Assert.Equal(D(12, 31, 2028), DiasDeVentas.UltimoQueCabe(D(1, 1, 2026)));
        Assert.Equal(D(1, 1, 2024), DiasDeVentas.PrimeroQueCabe(D(12, 31, 2026)));
        Assert.Equal(DateOnly.MaxValue, DiasDeVentas.UltimoQueCabe(DateOnly.MaxValue));
        Assert.Equal(DateOnly.MinValue, DiasDeVentas.PrimeroQueCabe(DateOnly.MinValue));
    }
}
