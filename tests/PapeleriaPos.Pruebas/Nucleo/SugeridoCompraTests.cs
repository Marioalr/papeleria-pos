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
}
