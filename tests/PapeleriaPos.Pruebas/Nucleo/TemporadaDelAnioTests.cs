using System.Globalization;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Empezar y terminar la temporada (entrega 13, parte 3): qué vez de la temporada se prepara (la que corre o la siguiente) y
/// cuál se termina (la última que empezó), la del año pasado, el mayoreo que pone un piso al menudeo, el sugerido que nunca baja
/// un precio, y por qué una tarjeta de precio se vuelve a imprimir.
/// </summary>
public class TemporadaDelAnioTests
{
    private static readonly DiaDelAnio Del20DeNoviembre = new(11, 20), Al1DeEnero = new(1, 1);
    private static readonly DiaDelAnio Del16DeSeptiembre = new(9, 16), Al5DeNoviembre = new(11, 5);

    private static DateOnly Dia(string fecha) => DateOnly.Parse(fecha, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-10-17", "2026-11-20", "2027-01-01")]
    [InlineData("2026-11-20", "2026-11-20", "2027-01-01")]
    [InlineData("2026-12-30", "2026-11-20", "2027-01-01")]
    [InlineData("2027-01-01", "2026-11-20", "2027-01-01")]
    [InlineData("2027-01-02", "2027-11-20", "2028-01-01")]
    public void La_que_sigue_es_la_que_corre_o_la_siguiente_y_navidad_cruza_el_anio(string hoy, string inicio, string fin)
    {
        var periodo = PeriodoDeTemporada.QueSigue(Del20DeNoviembre, Al1DeEnero, Dia(hoy));

        Assert.Equal((Dia(inicio), Dia(fin)), (periodo.Inicio, periodo.Fin));
        Assert.Equal((Dia(inicio).AddYears(-1), Dia(fin).AddYears(-1)), (periodo.AnioAnterior(Del20DeNoviembre, Al1DeEnero).Inicio,
            periodo.AnioAnterior(Del20DeNoviembre, Al1DeEnero).Fin));
    }

    [Theory]
    [InlineData("2026-09-15", "2025-09-16")]
    [InlineData("2026-09-16", "2026-09-16")]
    [InlineData("2026-11-05", "2026-09-16")]
    [InlineData("2026-11-10", "2026-09-16")]
    [InlineData("2027-03-01", "2026-09-16")]
    public void La_que_se_termina_es_la_ultima_que_empezo(string hoy, string inicio)
    {
        var periodo = PeriodoDeTemporada.UltimaQueEmpezo(Del16DeSeptiembre, Al5DeNoviembre, Dia(hoy));

        Assert.Equal(Dia(inicio), periodo.Inicio);
        Assert.Equal(new DateOnly(Dia(inicio).Year, 11, 5), periodo.Fin);
    }

    [Fact]
    public void El_29_de_febrero_en_un_anio_sin_el_es_el_28_y_una_de_un_dia_empieza_y_termina_ese_dia()
    {
        var bisiesta = PeriodoDeTemporada.DelAnio(new DiaDelAnio(2, 29), new DiaDelAnio(3, 2), 2027);
        var deUnDia = PeriodoDeTemporada.QueSigue(new DiaDelAnio(4, 30), new DiaDelAnio(4, 30), new DateOnly(2027, 4, 30));

        Assert.Equal((new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 2)), (bisiesta.Inicio, bisiesta.Fin));
        Assert.Equal(new DateOnly(2028, 2, 29), PeriodoDeTemporada.DelAnio(new DiaDelAnio(2, 29), new DiaDelAnio(3, 2), 2028).Inicio);
        Assert.Equal((new DateOnly(2027, 4, 30), new DateOnly(2027, 4, 30)), (deUnDia.Inicio, deUnDia.Fin));
        Assert.True(deUnDia.Contiene(new DateOnly(2027, 4, 30)));
    }

    [Fact]
    public void En_utc_va_de_la_medianoche_del_primer_dia_a_la_del_dia_siguiente_al_ultimo_en_la_tienda()
    {
        var (desde, hasta) = PeriodoDeTemporada.DelAnio(Del20DeNoviembre, Al1DeEnero, 2026).EnUtc();

        // La Ciudad de México está 6 horas detrás de UTC (sin horario de verano desde 2022).
        Assert.Equal(new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), desde);
        Assert.Equal(new DateTime(2027, 1, 2, 6, 0, 0, DateTimeKind.Utc), hasta);
        Assert.Equal("del 20/11/2026 al 1/1/2027", PeriodoDeTemporada.DelAnio(Del20DeNoviembre, Al1DeEnero, 2026).ToString());
    }

    [Fact]
    public void El_piso_deja_el_mayoreo_mas_barato_que_el_menudeo()
    {
        // Sin mayoreo, cualquier precio.
        Assert.Equal(0, PreciosDeTemporada.Piso(4500, [], []));
        // Una escala de $40.00 desde 12: el menudeo va de $40.01 para arriba (igual no: el mayoreo ya no sería más barato).
        Assert.Equal(4001, PreciosDeTemporada.Piso(4500, [4000, 3800], []));
        // Un paquete de 12 a $480 (sin descuento, $40 la pieza, igual que el menudeo de hoy): el menudeo no baja de $40.
        Assert.Equal(4000, PreciosDeTemporada.Piso(4000, [], [(12_000, 48_000)]));
        // Con descuento ($450 = $37.50 la pieza contra $40): lo sigue teniendo, el menudeo va de $37.51.
        Assert.Equal(3751, PreciosDeTemporada.Piso(4000, [], [(12_000, 45_000)]));
        // Lo más alto de todo.
        Assert.Equal(4001, PreciosDeTemporada.Piso(4000, [4000], [(12_000, 45_000)]));
    }

    [Fact]
    public void El_sugerido_nunca_baja_un_precio_y_sube_al_margen_minimo()
    {
        // Con un costo de $20 y 30 % de margen mínimo, lo menos es $28.57 → $29.00 (a los 50 centavos de arriba).
        Assert.Equal(2900, PreciosDeTemporada.Sugerido(2500, null, 2000m, 3000));
        // El de hoy ya deja más: se queda.
        Assert.Equal(4000, PreciosDeTemporada.Sugerido(4000, null, 2000m, 3000));
        // Lo que se rebajó al final de la temporada pasada no se regala otra vez: el del año pasado, si era más alto.
        Assert.Equal(4500, PreciosDeTemporada.Sugerido(2250, 4500, 2000m, 3000));
        // Sin costo no hay con qué medir el margen.
        Assert.Null(PreciosDeTemporada.Sugerido(4000, 4500, null, 3000));
        Assert.Null(PreciosDeTemporada.Sugerido(4000, 4500, 0m, 3000));
    }

    [Fact]
    public void Una_tarjeta_se_reimprime_si_cambio_su_precio_o_su_codigo_ya_no_es_suyo()
    {
        var tarjeta = new TarjetaDePrecio { VarianteId = Guid.NewGuid(), PrecioCentavos = 4500, Codigo = "2000000005012" };

        Assert.Null(tarjeta.PorQueReimprimir(4500, ["2000000005012", "7501234567890"]));
        Assert.Null(tarjeta.PorQueReimprimir(4500, ["2000000005012".ToLowerInvariant()]));
        Assert.Equal("cambió el precio", tarjeta.PorQueReimprimir(4800, ["2000000005012"]));
        Assert.Equal("ya no tiene precio", tarjeta.PorQueReimprimir(null, ["2000000005012"]));
        Assert.Equal("su código cambió", tarjeta.PorQueReimprimir(4500, ["7501234567890"]));
    }
}
