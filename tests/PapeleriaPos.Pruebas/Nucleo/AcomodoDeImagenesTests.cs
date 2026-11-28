using PapeleriaPos.Nucleo.Imagenes;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// El acomodo de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes.md): renglones de izquierda a derecha, a la siguiente hoja
/// cuando no cabe el renglón, y en «En hojas» el alto más grande con el que caben.
/// </summary>
public class AcomodoDeImagenesTests
{
    private static readonly Hoja Carta = Hoja.Carta; // 15.59 × 22.94 cm útiles

    private static List<ImagenPorAcomodar> Iguales(int cuantas, int ancho, int alto) =>
        Enumerable.Range(0, cuantas).Select(_ => new ImagenPorAcomodar(ancho, alto)).ToList();

    [Fact]
    public void Carta_con_los_margenes_de_Word_deja_15_59_por_22_94()
    {
        Assert.Equal(15.59, Carta.AnchoUtil, 2);
        Assert.Equal(22.94, Carta.AltoUtil, 2);
        var horizontal = Hoja.De(TamanoDeHoja.Carta, horizontal: true, 2.5, 2.5, 3, 3);
        Assert.Equal(27.94 - 6, horizontal.AnchoUtil, 2);
    }

    [Fact]
    public void En_alto_todas_salen_del_mismo_alto_y_el_ancho_sale_de_cada_una()
    {
        var a = AcomodoDeImagenes.Acomodar([new(1000, 2000), new(2000, 1000)], Carta, ModoDelTamano.Alto, 5, 0.3)!;
        Assert.All(a.Piezas, p => Assert.Equal(5, p.Alto, 6));
        Assert.Equal(2.5, a.Piezas[0].Ancho, 6);
        Assert.Equal(10, a.Piezas[1].Ancho, 6);
        Assert.Equal(2.8, a.Piezas[1].X, 6);
        Assert.Equal(0, a.Piezas[1].Renglon);
    }

    [Fact]
    public void Pasa_al_siguiente_renglon_cuando_ya_no_cabe_a_lo_ancho()
    {
        // 4 cm de ancho y 0.3 de separación: caben 3 por renglón (4 + 0.3 + 4 + 0.3 + 4 = 12.6; la cuarta llegaría a 16.9).
        var a = AcomodoDeImagenes.Acomodar(Iguales(7, 800, 1000), Carta, ModoDelTamano.Alto, 5, 0.3)!;
        Assert.Equal([0, 0, 0, 1, 1, 1, 2], a.Piezas.Select(p => p.Renglon));
        Assert.Equal(0, a.Piezas[3].X);
        Assert.Equal(5 + 0.3 + AcomodoDeImagenes.HolguraPorRenglon, a.Piezas[3].Y, 6);
        Assert.Equal(3, a.Renglones().Count);
    }

    [Fact]
    public void Pasa_a_la_siguiente_hoja_cuando_el_renglon_no_cabe_a_lo_alto()
    {
        // Renglones de 5 cm + 0.35: caben 4 por hoja (4 × 5.35 = 21.4; el quinto llegaría a 26.75 > 22.94).
        var a = AcomodoDeImagenes.Acomodar(Iguales(15, 800, 1000), Carta, ModoDelTamano.Alto, 5, 0.3)!;
        Assert.Equal(2, a.Hojas);
        Assert.All(a.Piezas.Take(12), p => Assert.Equal(1, p.HojaNumero));
        Assert.All(a.Piezas.Skip(12), p => Assert.Equal(2, p.HojaNumero));
        Assert.Equal(0, a.Piezas[12].Y);
    }

    [Fact]
    public void La_que_no_cabe_a_lo_ancho_se_achica_al_ancho_de_la_hoja_y_se_marca()
    {
        var panoramica = new ImagenPorAcomodar(1600, 400); // a 5 cm de alto mediría 20 cm
        var a = AcomodoDeImagenes.Acomodar([panoramica], Carta, ModoDelTamano.Alto, 5, 0.3)!;
        var p = Assert.Single(a.Piezas);
        Assert.True(p.Achicada);
        Assert.Equal(Carta.AnchoUtil, p.Ancho, 6);
        Assert.Equal(Carta.AnchoUtil / 4, p.Alto, 6);
    }

    [Fact]
    public void En_ancho_todas_salen_del_mismo_ancho_y_el_renglon_mide_lo_de_la_mas_alta()
    {
        var a = AcomodoDeImagenes.Acomodar([new(1000, 1000), new(1000, 2000), new(1000, 500), new(1000, 1000)], Carta, ModoDelTamano.Ancho, 5, 0.5)!;
        Assert.All(a.Piezas, p => Assert.Equal(5, p.Ancho, 6));
        Assert.Equal([5, 10, 2.5, 5], a.Piezas.Select(p => Math.Round(p.Alto, 6)));
        // 5 + 0.5 + 5 + 0.5 + 5 = 16 > 15.59: dos por renglón... la tercera ya no cabe (11 + 5 = 16).
        Assert.Equal(1, a.Piezas[2].Renglon);
        Assert.Equal(10 + 0.5 + AcomodoDeImagenes.HolguraPorRenglon, a.Piezas[2].Y, 6);
    }

    [Fact]
    public void Las_que_tienen_pocos_pixeles_para_su_tamano_salen_borrosas()
    {
        // 5 cm ≈ 1.97 pulgadas: 240 px dan ~122 ppp (borrosa), 160 px ~81 (muy borrosa), 1600 px ~813 (bien).
        var a = AcomodoDeImagenes.Acomodar([new(240, 240), new(160, 160), new(1600, 1600)], Carta, ModoDelTamano.Alto, 5, 0.3)!;
        Assert.Equal([true, true, false], a.Piezas.Select(p => p.Borrosa));
        Assert.Equal([false, true, false], a.Piezas.Select(p => p.MuyBorrosa));
        Assert.Equal(240 / (5 / 2.54), a.Piezas[0].PuntosPorPulgada, 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void En_hojas_busca_el_alto_mas_grande_con_el_que_caben(int hojas)
    {
        var imagenes = Iguales(24, 1080, 2340);
        var a = AcomodoDeImagenes.Acomodar(imagenes, Carta, ModoDelTamano.EnHojas, hojas, 0.3)!;
        Assert.False(a.NoCaben);
        Assert.True(a.Hojas <= hojas, $"Salieron {a.Hojas} hojas con {a.AltoCalculado} cm");
        Assert.NotNull(a.AltoCalculado);
        // A décimas: con una décima más ya no caben.
        Assert.Equal(Math.Round(a.AltoCalculado!.Value, 1), a.AltoCalculado.Value, 9);
        var unaDecimaMas = AcomodoDeImagenes.Acomodar(imagenes, Carta, ModoDelTamano.Alto, a.AltoCalculado.Value + 0.1, 0.3)!;
        Assert.True(unaDecimaMas.Hojas > hojas);
        Assert.All(a.Piezas, p => Assert.Equal(a.AltoCalculado.Value, p.Alto, 6));
    }

    [Fact]
    public void En_hojas_dice_cuando_ni_con_el_alto_mas_chico_caben()
    {
        var a = AcomodoDeImagenes.Acomodar(Iguales(2000, 1000, 1000), Carta, ModoDelTamano.EnHojas, 1, 0.3)!;
        Assert.True(a.NoCaben);
        Assert.True(a.Hojas > 1);
        Assert.Equal(AcomodoDeImagenes.AltoMinimo, a.AltoCalculado);
    }

    [Fact]
    public void Una_mas_alta_que_ya_no_cabe_baja_con_todo_su_renglon_a_la_siguiente_hoja()
    {
        // En ancho: tres renglones de 5 cm, y en el cuarto la segunda mide 10 de alto: ese renglón ya no cabe y baja completo.
        var imagenes = Iguales(6, 1000, 1000).Concat([new ImagenPorAcomodar(1000, 1000), new ImagenPorAcomodar(500, 1000)]).ToList();
        var a = AcomodoDeImagenes.Acomodar(imagenes, Carta, ModoDelTamano.Ancho, 5, 0.3)!;
        var ultimo = a.Renglones()[^1];
        Assert.Equal(2, ultimo.Count);
        Assert.All(ultimo, p => Assert.Equal(2, p.HojaNumero));
        Assert.All(ultimo, p => Assert.Equal(0, p.Y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void Sin_un_numero_que_sirva_no_hay_acomodo(double valor) =>
        Assert.Null(AcomodoDeImagenes.Acomodar(Iguales(3, 100, 100), Carta, ModoDelTamano.Alto, valor, 0.3));

    [Fact]
    public void Sin_imagenes_no_hay_acomodo() =>
        Assert.Null(AcomodoDeImagenes.Acomodar([], Carta, ModoDelTamano.Alto, 5, 0.3));

    [Fact]
    public void Los_ajustes_guardan_el_numero_de_cada_modo()
    {
        var ajustes = new AjustesDeJuntarImagenes().ConValor(6);
        Assert.Equal(6, ajustes.Alto);
        ajustes = (ajustes with { Modo = ModoDelTamano.EnHojas }).ConValor(2.4);
        Assert.Equal(2, ajustes.Hojas);
        Assert.Equal(6, (ajustes with { Modo = ModoDelTamano.Alto }).Valor);
        Assert.Equal(2, ajustes.Valor);
    }
}
