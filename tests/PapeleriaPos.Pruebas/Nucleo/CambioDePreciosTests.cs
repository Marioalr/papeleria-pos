using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Cambiar precios de varios a la vez (Productos › Cambiar precios): un porcentaje o una cantidad más o menos, o un precio
/// fijo, sobre el menudeo; el mayoreo en la misma proporción; y el redondeo al más cercano, que nunca queda al revés de lo
/// pedido ni en $0.
/// </summary>
public class CambioDePreciosTests
{
    private static ReglaDePrecios Regla(ComoCambiarPrecios como, long valor, RedondeoDePrecios redondeo = RedondeoDePrecios.SinRedondear) =>
        new(como, valor, ConMayoreo: true, redondeo);

    [Theory]
    // Subir 10 % a $12.50: $13.75; a los 50 centavos, el medio sube ($14.00); al peso, $14.00.
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.SinRedondear, 1250, 1375)]
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos, 1250, 1400)]
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.UnPeso, 1250, 1400)]
    // Con decimales: 12.5 % a $8.00 es $9.00.
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 1250, RedondeoDePrecios.SinRedondear, 800, 900)]
    // Bajar 10 % a $12.50: $11.25; a los 50 centavos, $11.50 (el medio sube); al peso, $11.00.
    [InlineData(ComoCambiarPrecios.BajarPorcentaje, 1000, RedondeoDePrecios.SinRedondear, 1250, 1125)]
    [InlineData(ComoCambiarPrecios.BajarPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos, 1250, 1150)]
    [InlineData(ComoCambiarPrecios.BajarPorcentaje, 1000, RedondeoDePrecios.UnPeso, 1250, 1100)]
    // Una cantidad: $1.50 más a $12.50 es $14.00; $3.00 menos, $9.50 (al peso, $10.00).
    [InlineData(ComoCambiarPrecios.SubirMonto, 150, RedondeoDePrecios.SinRedondear, 1250, 1400)]
    [InlineData(ComoCambiarPrecios.BajarMonto, 300, RedondeoDePrecios.CincuentaCentavos, 1250, 950)]
    [InlineData(ComoCambiarPrecios.BajarMonto, 300, RedondeoDePrecios.UnPeso, 1250, 1000)]
    // Un precio fijo queda tal cual, aunque se redondee.
    [InlineData(ComoCambiarPrecios.PrecioFijo, 1530, RedondeoDePrecios.UnPeso, 1250, 1530)]
    public void El_menudeo_cambia_con_la_regla_y_el_redondeo(ComoCambiarPrecios como, long valor, RedondeoDePrecios redondeo, long antes, long despues) =>
        Assert.Equal(despues, CambioDePrecios.Menudeo(Regla(como, valor, redondeo), antes));

    [Fact]
    public void El_mayoreo_cambia_en_la_misma_proporcion_que_el_menudeo()
    {
        // $2.00 más a un menudeo de $10.00 es 20 % más: la escala de $8.00 queda en $9.60.
        var monto = Regla(ComoCambiarPrecios.SubirMonto, 200);
        var proporcion = CambioDePrecios.ProporcionDe(monto, 1000);

        Assert.Equal(960, CambioDePrecios.EnProporcion(800, proporcion, RedondeoDePrecios.SinRedondear));
        Assert.Equal(950, CambioDePrecios.EnProporcion(800, proporcion, RedondeoDePrecios.CincuentaCentavos));
        Assert.Equal(1000, CambioDePrecios.EnProporcion(800, proporcion, RedondeoDePrecios.UnPeso));
        // Con un porcentaje, la misma para todos: el paquete de $130.00, 10 % más.
        Assert.Equal(14300, CambioDePrecios.EnProporcion(13000, CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000), 1250),
            RedondeoDePrecios.CincuentaCentavos));
        // Un precio fijo de $15.00 a un menudeo de $10.00: el mayoreo sube 50 %.
        Assert.Equal(1200, CambioDePrecios.EnProporcion(800, CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.PrecioFijo, 1500), 1000),
            RedondeoDePrecios.SinRedondear));
    }

    [Fact]
    public void El_redondeo_nunca_queda_al_reves_de_lo_pedido_ni_en_cero()
    {
        var subir2 = CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.SubirPorcentaje, 200), 510);
        var bajar2 = CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.BajarPorcentaje, 200), 540);

        // $5.10 + 2 % es $5.20: el más cercano a los 50 centavos ($5.00) bajaría el precio, así que se queda en $5.10.
        Assert.Equal(510, CambioDePrecios.EnProporcion(510, subir2, RedondeoDePrecios.CincuentaCentavos));
        // $5.40 − 2 % es $5.29: el más cercano ($5.50) lo subiría, así que se queda en $5.40.
        Assert.Equal(540, CambioDePrecios.EnProporcion(540, bajar2, RedondeoDePrecios.CincuentaCentavos));
        // Lo que vale menos de medio paso no se redondea a $0: $0.20 + 10 % queda en $0.22.
        var subir10 = CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000), 20);
        Assert.Equal(22, CambioDePrecios.EnProporcion(20, subir10, RedondeoDePrecios.CincuentaCentavos));
        Assert.Equal(22, CambioDePrecios.EnProporcion(20, subir10, RedondeoDePrecios.UnPeso));
        // Sin cambio (un precio fijo igual al de antes), el mayoreo no se toca aunque no esté redondeado.
        Assert.Equal(833, CambioDePrecios.EnProporcion(833, CambioDePrecios.ProporcionDe(Regla(ComoCambiarPrecios.PrecioFijo, 1000), 1000),
            RedondeoDePrecios.CincuentaCentavos));
    }

    [Fact]
    public void Lo_que_quedaria_en_cero_o_menos_no_tiene_precio_nuevo()
    {
        var bajar = Regla(ComoCambiarPrecios.BajarMonto, 1300);

        Assert.Null(CambioDePrecios.Menudeo(bajar, 1250));
        Assert.Null(CambioDePrecios.Menudeo(bajar, 1300));
        Assert.Null(CambioDePrecios.EnProporcion(800, CambioDePrecios.ProporcionDe(bajar, 1250), RedondeoDePrecios.SinRedondear));
        Assert.Null(CambioDePrecios.Menudeo(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000), 0));
        Assert.Equal(1, CambioDePrecios.Menudeo(bajar, 1301));
    }

    [Theory]
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 0, "Escribe el porcentaje: más de cero.")]
    [InlineData(ComoCambiarPrecios.BajarMonto, -100, "Escribe cuánto: más de cero.")]
    [InlineData(ComoCambiarPrecios.PrecioFijo, 0, "Escribe el precio: más de cero.")]
    [InlineData(ComoCambiarPrecios.BajarPorcentaje, 10_000, "No se puede bajar 100 % o más: los precios quedarían en $0.")]
    [InlineData(ComoCambiarPrecios.SubirPorcentaje, 100_001, "Revisa el porcentaje: se sube hasta 1000 %.")]
    [InlineData(ComoCambiarPrecios.SubirMonto, 10_000_001, "Revisa la cantidad: hasta $100,000.00.")]
    public void Una_regla_sin_sentido_dice_que_corregir(ComoCambiarPrecios como, long valor, string mensaje) =>
        Assert.Equal(mensaje, Assert.Throws<ArgumentException>(() => CambioDePrecios.Validar(Regla(como, valor))).Message);

    [Fact]
    public void La_regla_se_lee_como_la_dice_la_bitacora()
    {
        Assert.Equal("subir 10 %, con el mayoreo en la misma proporción, redondeado a los 50 centavos más cercanos",
            CambioDePrecios.Texto(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos)));
        Assert.Equal("bajar 12.5 %, sin cambiar el mayoreo, redondeado al peso más cercano",
            CambioDePrecios.Texto(new ReglaDePrecios(ComoCambiarPrecios.BajarPorcentaje, 1250, false, RedondeoDePrecios.UnPeso)));
        Assert.Equal("subir $1,500.00, con el mayoreo en la misma proporción, sin redondear",
            CambioDePrecios.Texto(Regla(ComoCambiarPrecios.SubirMonto, 150_000)));
        // Un precio fijo sin el mayoreo no redondea nada.
        Assert.Equal("ponerlos a $12.00, sin cambiar el mayoreo",
            CambioDePrecios.Texto(new ReglaDePrecios(ComoCambiarPrecios.PrecioFijo, 1200, false, RedondeoDePrecios.UnPeso)));
    }

    // ── Lo que se cobra queda en orden: el mayoreo nunca más caro que el menudeo ──

    private static readonly Guid Menudeo = Guid.NewGuid(), Doce = Guid.NewGuid(), Veinticuatro = Guid.NewGuid(), Seis = Guid.NewGuid(), Par = Guid.NewGuid(),
        PaqueteDelPar = Guid.NewGuid();

    /// <summary>Un precio por pieza desde tantas piezas (de eleventa, si se dice).</summary>
    private static PrecioDeVariante PorPieza(Guid id, long desde, long centavos, bool eleventa = false) =>
        new(id, null, 1000, desde * 1000, centavos, eleventa);

    /// <summary>El precio propio de un paquete de tantas piezas.</summary>
    private static PrecioDeVariante DelPaquete(long piezas, long centavos) => new(Par, PaqueteDelPar, piezas * 1000, 1000, centavos);

    [Fact]
    public void Con_el_mayoreo_en_orden_nada_se_ajusta()
    {
        var cambio = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos),
            [PorPieza(Menudeo, 1, 1250), PorPieza(Doce, 12, 1100), DelPaquete(12, 13000)]);

        Assert.Null(cambio.NoCambia);
        Assert.Equal(new Dictionary<Guid, long> { [Menudeo] = 1400, [Doce] = 1200, [Par] = 14300 }, cambio.Cambios);
        Assert.Equal((1250L, 1400L, false), (cambio.MenudeoAntes, cambio.MenudeoDespues, cambio.MayoreoAjustado));
    }

    [Fact]
    public void Subir_poco_con_el_redondeo_no_empareja_la_escala_con_el_menudeo_ni_el_paquete_con_sus_piezas()
    {
        // $10.00, desde 12 a $9.80 y el par a $19.90; 2 % más, a los 50 centavos: el menudeo se queda en $10.00 y la escala
        // ($10.00) y el par ($20.50) ya no serían más baratos: como subir, no bajan, se quedan como estaban.
        var cambio = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.SubirPorcentaje, 200, RedondeoDePrecios.CincuentaCentavos),
            [PorPieza(Menudeo, 1, 1000), PorPieza(Doce, 12, 980), DelPaquete(2, 1990)]);

        Assert.Equal(PorQueNoCambia.QuedaIgual, cambio.NoCambia);
        Assert.Empty(cambio.Cambios);
    }

    [Fact]
    public void Un_precio_fijo_deja_el_mayoreo_al_paso_de_abajo_del_menudeo()
    {
        // $20.00 y desde 12 a $19.30; a $10.90 con el mayoreo, al peso: la escala ($10.52, al peso $11.00) pasaría del menudeo,
        // así que al peso de abajo: $10.00.
        var cambio = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.PrecioFijo, 1090, RedondeoDePrecios.UnPeso),
            [PorPieza(Menudeo, 1, 2000), PorPieza(Doce, 12, 1930)]);

        Assert.Null(cambio.NoCambia);
        Assert.Equal(new Dictionary<Guid, long> { [Menudeo] = 1090, [Doce] = 1000 }, cambio.Cambios);
        Assert.True(cambio.MayoreoAjustado);
    }

    [Fact]
    public void Bajando_el_paso_de_abajo_y_subiendo_lo_de_antes_cuando_el_redondeo_no_cabe()
    {
        // Bajar 20 % a $10.00 con desde 12 a $9.80: $8.00 y $7.84, a los 50 centavos $8.00, que no es más barato: $7.50.
        var bajar = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.BajarPorcentaje, 2000, RedondeoDePrecios.CincuentaCentavos),
            [PorPieza(Menudeo, 1, 1000), PorPieza(Doce, 12, 980)]);
        // Subir 10 % al peso a $10.00 con desde 12 a $9.90: $11.00 y $10.89, al peso $11.00: al peso de abajo, $10.00.
        var subir = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.UnPeso),
            [PorPieza(Menudeo, 1, 1000), PorPieza(Doce, 12, 990)]);

        Assert.Equal(new Dictionary<Guid, long> { [Menudeo] = 800, [Doce] = 750 }, bajar.Cambios);
        Assert.Equal(new Dictionary<Guid, long> { [Menudeo] = 1100, [Doce] = 1000 }, subir.Cambios);
        Assert.True(bajar.MayoreoAjustado && subir.MayoreoAjustado);
    }

    [Fact]
    public void Sin_el_mayoreo_un_menudeo_que_bajaria_hasta_su_mayoreo_no_cambia()
    {
        var sinMayoreo = (ComoCambiarPrecios como, long valor) => new ReglaDePrecios(como, valor, false, RedondeoDePrecios.CincuentaCentavos);

        // Bajar 20 % a $10.00 con desde 12 a $9.00: el menudeo de $8.00 dejaría la escala más cara.
        Assert.Equal(PorQueNoCambia.MayoreoMasCaro,
            CambioDePrecios.Variante(sinMayoreo(ComoCambiarPrecios.BajarPorcentaje, 2000), [PorPieza(Menudeo, 1, 1000), PorPieza(Doce, 12, 900)]).NoCambia);
        // El par de $19.00 costaría más que dos piezas de $9.00.
        Assert.Equal(PorQueNoCambia.MayoreoMasCaro,
            CambioDePrecios.Variante(sinMayoreo(ComoCambiarPrecios.BajarPorcentaje, 1000), [PorPieza(Menudeo, 1, 1000), DelPaquete(2, 1900)]).NoCambia);
        // Subir, sin el mayoreo, sí: el mayoreo queda más barato todavía.
        var subir = CambioDePrecios.Variante(sinMayoreo(ComoCambiarPrecios.SubirPorcentaje, 1000), [PorPieza(Menudeo, 1, 1000), PorPieza(Doce, 12, 900)]);
        Assert.Equal(new Dictionary<Guid, long> { [Menudeo] = 1100 }, subir.Cambios);
    }

    [Fact]
    public void En_el_piloto_el_mayoreo_de_aqui_no_pasa_de_lo_de_eleventa()
    {
        var subir10 = Regla(ComoCambiarPrecios.SubirPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos);
        // Menudeo de eleventa $15.00 y su promoción desde 12 a $14.50, que se quedan; la escala de aquí desde 24, $14.00, no
        // sube a $15.50: no puede pasar de la promoción. Hay que subirlo primero en eleventa.
        var sube = CambioDePrecios.Variante(subir10,
            [PorPieza(Menudeo, 1, 1500, eleventa: true), PorPieza(Doce, 12, 1450, eleventa: true), PorPieza(Veinticuatro, 24, 1400)]);
        // Una escala de aquí entre las de eleventa tampoco baja de la promoción que sigue.
        var baja = CambioDePrecios.Variante(Regla(ComoCambiarPrecios.BajarPorcentaje, 1000, RedondeoDePrecios.CincuentaCentavos),
            [PorPieza(Menudeo, 1, 1500, eleventa: true), PorPieza(Seis, 6, 1480), PorPieza(Doce, 12, 1450, eleventa: true)]);
        // Con lugar abajo de la promoción, sí sube, en su proporción.
        var cabe = CambioDePrecios.Variante(subir10,
            [PorPieza(Menudeo, 1, 1500, eleventa: true), PorPieza(Doce, 12, 1450, eleventa: true), PorPieza(Veinticuatro, 24, 1200)]);

        Assert.Equal(PorQueNoCambia.EnEleventa, sube.NoCambia);
        Assert.Equal(PorQueNoCambia.EnEleventa, baja.NoCambia);
        Assert.Equal(new Dictionary<Guid, long> { [Veinticuatro] = 1300 }, cabe.Cambios);
        Assert.Equal((1500L, 1500L, true), (cabe.MenudeoAntes, cabe.MenudeoDespues, cabe.MenudeoEnEleventa));
    }
}
