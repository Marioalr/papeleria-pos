using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Los días de «Ventas por período» (entrega 9, parte 2): los de un toque, cuál es lo que se ve, y ← → a los de antes o de
/// después del mismo largo, sin pasar de hoy.
/// </summary>
public sealed class DiasDeVentasTests
{
    /// <summary>Miércoles 21 de octubre de 2026.</summary>
    private static readonly DateOnly Hoy = new(2026, 10, 21);

    private static DateOnly D(int mes, int dia, int anio = 2026) => new(anio, mes, dia);

    [Fact]
    public void Los_de_un_toque_y_cual_es_lo_que_se_ve()
    {
        Assert.Equal((Hoy, Hoy), DiasDeVentas.De(DiasElegidos.Hoy, Hoy));
        Assert.Equal((D(10, 20), D(10, 20)), DiasDeVentas.De(DiasElegidos.Ayer, Hoy));
        Assert.Equal((D(10, 19), Hoy), DiasDeVentas.De(DiasElegidos.EstaSemana, Hoy));
        Assert.Equal((D(10, 1), Hoy), DiasDeVentas.De(DiasElegidos.EsteMes, Hoy));
        Assert.Equal((D(9, 1), D(9, 30)), DiasDeVentas.De(DiasElegidos.MesPasado, Hoy));
        // El mes pasado de enero es diciembre del año anterior.
        Assert.Equal((D(12, 1, 2025), D(12, 31, 2025)), DiasDeVentas.De(DiasElegidos.MesPasado, D(1, 15)));

        Assert.Equal(DiasElegidos.EsteMes, DiasDeVentas.Cual(D(10, 1), Hoy, Hoy));
        Assert.Equal(DiasElegidos.MesPasado, DiasDeVentas.Cual(D(9, 1), D(9, 30), Hoy));
        Assert.Null(DiasDeVentas.Cual(D(10, 3), D(10, 9), Hoy));
    }

    [Fact]
    public void Las_flechas_llevan_al_mismo_largo_de_antes_o_de_despues_sin_pasar_de_hoy()
    {
        // Un día: el de antes; hoy no tiene siguiente.
        Assert.Equal((D(10, 20), D(10, 20)), DiasDeVentas.Mover(Hoy, Hoy, -1, Hoy));
        Assert.Null(DiasDeVentas.Mover(Hoy, Hoy, 1, Hoy));
        Assert.Equal((Hoy, Hoy), DiasDeVentas.Mover(D(10, 20), D(10, 20), 1, Hoy));
        // Este mes a la fecha: el mes pasado completo; y de ahí, otra vez este mes hasta hoy.
        Assert.Equal((D(9, 1), D(9, 30)), DiasDeVentas.Mover(D(10, 1), Hoy, -1, Hoy));
        Assert.Equal((D(10, 1), Hoy), DiasDeVentas.Mover(D(9, 1), D(9, 30), 1, Hoy));
        // De marzo a febrero, con sus 28 días.
        Assert.Equal((D(2, 1), D(2, 28)), DiasDeVentas.Mover(D(3, 1), D(3, 31), -1, Hoy));
        // Esta semana a la fecha: la de antes, de lunes a domingo; y de regreso, hasta hoy.
        Assert.Equal((D(10, 12), D(10, 18)), DiasDeVentas.Mover(D(10, 19), Hoy, -1, Hoy));
        Assert.Equal((D(10, 19), Hoy), DiasDeVentas.Mover(D(10, 12), D(10, 18), 1, Hoy));
        // Unos días escritos: tantos como tiene.
        Assert.Equal((D(9, 26), D(10, 2)), DiasDeVentas.Mover(D(10, 3), D(10, 9), -1, Hoy));
        Assert.Equal((D(10, 17), Hoy), DiasDeVentas.Mover(D(10, 10), D(10, 16), 1, Hoy));
    }
}
