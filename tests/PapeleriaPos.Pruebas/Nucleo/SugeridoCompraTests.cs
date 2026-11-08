using PapeleriaPos.Nucleo.Inventario;
using P = PapeleriaPos.Nucleo.Inventario.SugeridoCompra.Producto;

namespace PapeleriaPos.Pruebas.Nucleo;

public class SugeridoCompraTests
{
    private static readonly DateOnly Hoy = new(2026, 8, 3);
    private static readonly SugeridoCompra.Parametros Reglas = new(14, 7);

    /// <summary>Tantas por día entre dos fechas (sin incluir la última).</summary>
    private static Dictionary<DateOnly, decimal> Ventas(params (DateOnly Desde, DateOnly Hasta, decimal PorDia)[] tramos)
    {
        var ventas = new Dictionary<DateOnly, decimal>();
        foreach (var (desde, hasta, porDia) in tramos)
            for (var d = desde; d < hasta; d = d.AddDays(1))
                ventas[d] = ventas.GetValueOrDefault(d) + porDia;
        return ventas;
    }

    private static readonly Dictionary<DateOnly, decimal> DosAlDia = Ventas((Hoy.AddDays(-200), Hoy, 2));

    [Fact]
    public void Con_venta_pareja_pide_hasta_cubrir_la_cobertura_y_el_colchon_en_cajas()
    {
        var r = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, 10, 0, 12, false), Hoy, Reglas);

        Assert.Equal((2m, 1m, 14m, 42m), (r.VentaDiaria, r.Temporada, r.Minimo, r.Maximo));
        // Hay 10, debajo del mínimo: urge, y se completa la caja (faltan 32: 3 cajas de 12).
        Assert.True(r.Urgente);
        Assert.Equal(3, r.Paquetes);
    }

    [Fact]
    public void Arriba_del_minimo_solo_pide_cajas_enteras_y_cuenta_lo_que_viene_en_camino()
    {
        var holgado = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, 20, 0, 12, false), Hoy, Reglas);
        var enCamino = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, 10, 24, 12, false), Hoy, Reglas);
        var negativo = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, -5, 0, 1, false), Hoy, Reglas);

        Assert.Equal((false, 1), (holgado.Urgente, holgado.Paquetes));
        Assert.Equal((false, 0), (enCamino.Urgente, enCamino.Paquetes));
        // Una existencia negativa es un error de inventario: cuenta como cero.
        Assert.Equal((0m, 42), (negativo.Disponible, negativo.Paquetes));
    }

    [Fact]
    public void En_regreso_a_clases_la_temporada_multiplica_lo_que_se_vende()
    {
        // El año pasado: 1 al día y, desde estas fechas, 3 al día por tres semanas.
        var ventas = Ventas((Hoy.AddYears(-1).AddDays(-120), Hoy.AddYears(-1), 1), (Hoy.AddYears(-1), Hoy.AddYears(-1).AddDays(21), 3),
            (Hoy.AddDays(-90), Hoy, 1));

        var r = SugeridoCompra.Calcular(new P(Guid.NewGuid(), ventas, 0, 0, 1, false), Hoy, Reglas);

        Assert.Equal((3m, 3m), (r.Temporada, r.VentaDiaria));
        Assert.Equal(63m, r.Maximo);
    }

    [Fact]
    public void Lo_de_temporada_que_no_se_ha_vendido_este_ano_se_pide_con_lo_del_ano_pasado()
    {
        var mochilas = Ventas((Hoy.AddYears(-1), Hoy.AddYears(-1).AddDays(21), 2));

        var r = SugeridoCompra.Calcular(new P(Guid.NewGuid(), mochilas, 0, 0, 1, false), Hoy, Reglas);

        Assert.Equal((2m, 42), (r.VentaDiaria, r.Paquetes));
    }

    [Fact]
    public void Un_producto_nuevo_se_mide_desde_su_primera_venta()
    {
        var nuevo = Ventas((Hoy.AddDays(-20), Hoy, 3));

        var r = SugeridoCompra.Calcular(new P(Guid.NewGuid(), nuevo, 0, 0, 1, false), Hoy, Reglas);

        Assert.Equal(3m, r.VentaDiaria);
    }

    [Fact]
    public void Lo_que_no_se_vende_no_se_pide_y_lo_de_metro_no_se_redondea_a_piezas()
    {
        var nada = SugeridoCompra.Calcular(new P(Guid.NewGuid(), new Dictionary<DateOnly, decimal>(), 0, 0, 1, false), Hoy, Reglas);
        var liston = SugeridoCompra.Calcular(new P(Guid.NewGuid(), Ventas((Hoy.AddDays(-90), Hoy, 0.5m)), 1, 0, 50, true), Hoy, Reglas);

        Assert.Equal((0, false), (nada.Paquetes, nada.Urgente));
        Assert.Equal((3.5m, 10.5m), (liston.Minimo, liston.Maximo));
        // Un rollo de 50 m.
        Assert.Equal(1, liston.Paquetes);
    }

    /// <summary>
    /// Con el mínimo y el máximo de su ficha (2026-11-07: para que la lista de compra coincida con «Bajos en inventario»): urge
    /// en su mínimo propio o debajo, y pide por lo menos hasta su máximo propio, con el cálculo encima (el mayor de los dos).
    /// </summary>
    [Fact]
    public void Con_su_minimo_propio_urge_en_el_y_pide_por_lo_menos_hasta_su_maximo()
    {
        // Calculado: mínimo 14 y máximo 42 (dos al día). Con 20 no urgía; con su mínimo propio de 20, sí.
        var enSuMinimo = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, 20, 0, 1, false, MinimoPropio: 20, MaximoPropio: 30), Hoy, Reglas);
        Assert.Equal((true, 20m, 42m, 22), (enSuMinimo.Urgente, enSuMinimo.Minimo, enSuMinimo.Maximo, enSuMinimo.Paquetes));

        // Con 12, debajo del calculado (14) pero arriba de su mínimo propio (5): ya no urge; su máximo propio (60) es la meta.
        var arriba = SugeridoCompra.Calcular(new P(Guid.NewGuid(), DosAlDia, 12, 0, 12, false, MinimoPropio: 5, MaximoPropio: 60), Hoy, Reglas);
        Assert.Equal((false, 5m, 60m, 4), (arriba.Urgente, arriba.Minimo, arriba.Maximo, arriba.Paquetes));
    }

    /// <summary>
    /// Lo que casi no se vende también se pide con su mínimo propio (como en eleventa, el mínimo igual al máximo); sin máximo
    /// propio, hasta su mínimo. Sin propios, como siempre: lo que no se vende no se pide.
    /// </summary>
    [Fact]
    public void Sin_ventas_con_su_minimo_propio_pide_hasta_el_y_sin_el_no()
    {
        var sinVentas = new Dictionary<DateOnly, decimal>();

        var igualQueEleventa = SugeridoCompra.Calcular(new P(Guid.NewGuid(), sinVentas, 2, 0, 1, false, MinimoPropio: 6, MaximoPropio: 6), Hoy, Reglas);
        var soloMinimo = SugeridoCompra.Calcular(new P(Guid.NewGuid(), sinVentas, 1, 2, 1, false, MinimoPropio: 4), Hoy, Reglas);
        var enSuMaximo = SugeridoCompra.Calcular(new P(Guid.NewGuid(), sinVentas, 6, 0, 1, false, MinimoPropio: 6, MaximoPropio: 6), Hoy, Reglas);
        var sinPropios = SugeridoCompra.Calcular(new P(Guid.NewGuid(), sinVentas, 0, 0, 1, false), Hoy, Reglas);

        Assert.Equal((true, 4), (igualQueEleventa.Urgente, igualQueEleventa.Paquetes));
        Assert.Equal((true, 4m, 1), (soloMinimo.Urgente, soloMinimo.Maximo, soloMinimo.Paquetes));
        // En su mínimo, que es su máximo: urge, pero ya no falta nada (como «Bajos en inventario»: nada que pedir).
        Assert.Equal((true, 0), (enSuMaximo.Urgente, enSuMaximo.Paquetes));
        Assert.Equal((false, 0), (sinPropios.Urgente, sinPropios.Paquetes));
    }

    /// <summary>A granel, los propios van con sus fracciones: el listón con su mínimo de 2.5 m pide en rollos de 50 m.</summary>
    [Fact]
    public void A_granel_el_minimo_propio_va_con_fracciones()
    {
        var liston = SugeridoCompra.Calcular(new P(Guid.NewGuid(), Ventas((Hoy.AddDays(-90), Hoy, 0.1m)), 2.5m, 0, 50, true, MinimoPropio: 2.5m, MaximoPropio: 2.5m),
            Hoy, Reglas);

        // Calculado: máximo 2.1 m; el propio, 2.5 m. Urge y no falta nada.
        Assert.Equal((true, 2.5m, 2.5m, 0), (liston.Urgente, liston.Minimo, liston.Maximo, liston.Paquetes));
        var debajo = SugeridoCompra.Calcular(new P(Guid.NewGuid(), Ventas((Hoy.AddDays(-90), Hoy, 0.1m)), 1.5m, 0, 50, true, MinimoPropio: 2.5m, MaximoPropio: 2.5m),
            Hoy, Reglas);
        Assert.Equal((true, 1), (debajo.Urgente, debajo.Paquetes));
    }
}
