using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

public class FamiliasTests
{
    /// <summary>Un catálogo inventado: las marcas (Pluvia, Trazo) se repiten al final de nombres de varios productos.</summary>
    private static readonly string[] Catalogo =
    [
        "Lapicero 0.7mm Negro P/Fino Pluvia", "Lapicero 0.7mm Azul P/Fino Pluvia", "Lapicero 0.7mm Rojo P/Fino Pluvia",
        "Lapicero 0.7mm Colores Surt. Pluvia", "Lapicero 1.0mm Negro P/Mediano Pluvia", "Lapicero 1.0mm Rojo P/Medio Pluvia",
        "Lapicero 0.5mm Negro P/Fino Pluvia", "Lapicero 0.7mm Negro P/Fino Trazo", "Lapicero 1.0 Mm Negro Trazo",
        "Plumon Permanente Negro Pluvia", "Plumon Permanente Rojo Trazo", "Jgo Lapiceros C/10 Pluvia", "Jgo Lapiceros C/4 Pluvia",
        "Foami Carta Azul Rey", "Foami Carta Azul", "Foami Oficio Azul",
        "Cuaderno Prof 100h Raya Escriba", "Cuaderno Prof 100h Cuadro Grande Escriba", "Cuaderno Prof 100h Blanco Escriba",
        "Libreta Forma Italiana Raya Escriba",
        "Bolsa Regalo Grande Ocasion", "Bolsa Regalo Chica Ocasion", "Bolsa Regalo Mediana Ocasion",
    ];

    private static readonly LectorDeNombres Lector = new(Catalogo);

    private sealed record Producto(string Nombre, int Vendidos);

    private static AgrupacionDeFamilias<Producto> Agrupar(EjeFamilia? preferido, params Producto[] productos) =>
        Familias.Agrupar(productos, p => Lector.Leer(p.Nombre), p => p.Vendidos, preferido);

    [Fact]
    public void El_nombre_se_lee_en_pedazos_y_lo_que_acompana_a_los_milimetros_es_de_la_medida()
    {
        var partes = Lector.Leer("Lapicero 0.7mm Negro P/Fino Pluvia");

        Assert.Equal(
        [
            new ParteDeNombre("Lapicero", null, "LAPICERO"),
            new ParteDeNombre("0.7mm", EjeFamilia.Medida, "0.7MM"),
            new ParteDeNombre("Negro", EjeFamilia.Color, "NEGRO"),
            new ParteDeNombre("P/Fino", EjeFamilia.Medida, ""),
            new ParteDeNombre("Pluvia", EjeFamilia.Marca, "PLUVIA"),
        ], partes);
    }

    [Theory]
    [InlineData("Lapicero 1.0 Mm Negro Trazo", "1MM")]
    [InlineData("Lapicero 1mm Negro Trazo", "1MM")]
    [InlineData("Lapicero 1.0 Negro Trazo", "1MM")]
    [InlineData("Pintura 250 Ml Negra Trazo", "250ML")]
    [InlineData("Bola Unicel No. 9", "#9")]
    [InlineData("Bola Unicel #9", "#9")]
    [InlineData("Papel Cascaron 1/8", "1/8")]
    [InlineData("Foami 45x60 Azul", "45X60")]
    [InlineData("Foami Carta Azul", "CARTA")]
    public void La_medida_se_compara_igual_aunque_se_escriba_distinto(string nombre, string clave)
    {
        Assert.Equal(clave, Familias.Clave(Lector.Leer(nombre), EjeFamilia.Medida));
    }

    [Theory]
    [InlineData("Lapicero 0.7mm Colores Surt. Pluvia", "SURTIDO", "Colores Surt.")]
    [InlineData("Lapicero 1.0mm V/Colores Pluvia", "SURTIDO", "V/Colores")]
    [InlineData("Foami Carta Azul Rey", "AZUL REY", "Azul Rey")]
    [InlineData("Pintura 250 Ml Negra Trazo", "NEGRO", "Negra")]
    [InlineData("Liston Palo De Rosa", "PALO DE ROSA", "Palo De Rosa")]
    public void El_color_se_lee_con_su_tono_y_el_surtido_es_un_color_mas(string nombre, string clave, string texto)
    {
        var partes = Lector.Leer(nombre);

        Assert.Equal((clave, texto), (Familias.Clave(partes, EjeFamilia.Color), Familias.Valor(partes, EjeFamilia.Color)));
    }

    [Fact]
    public void La_marca_se_aprende_de_lo_que_se_repite_al_final_de_varios_productos()
    {
        Assert.Equal("PLUVIA", Familias.Clave(Lector.Leer("Goma Blanca Pluvia"), EjeFamilia.Marca));
        // «Ocasion» se repite al final, pero solo en bolsas: no es marca.
        Assert.Equal("", Familias.Clave(Lector.Leer("Bolsa Regalo Grande Ocasion"), EjeFamilia.Marca));
        // Y la dada de alta vale aunque no se repita.
        Assert.Equal("ESCRIBA", Familias.Clave(new LectorDeNombres([], ["Escriba"]).Leer("Cuaderno Raya Escriba"), EjeFamilia.Marca));
    }

    [Theory]
    [InlineData("Pluma Punto Fino Tinta Viva Negro", "TINTA VIVA")]
    [InlineData("Plumon Permanente Trazo Rojo", "TRAZO")]
    [InlineData("Lapicero 1.0mm V/Colores Trazo Tinta Viva", "TRAZO TINTA VIVA")]
    public void La_marca_es_lo_ultimo_del_nombre_aunque_despues_venga_el_color(string nombre, string marca)
    {
        var lector = new LectorDeNombres(Catalogo, ["Tinta Viva"]);

        Assert.Equal(marca, Familias.Clave(lector.Leer(nombre), EjeFamilia.Marca));
    }

    [Fact]
    public void Lo_que_solo_cambia_en_el_color_va_en_un_renglon_y_Enter_agrega_el_mas_vendido()
    {
        var agrupacion = Agrupar(null,
            new("Lapicero 0.7mm Azul P/Fino Pluvia", 700),
            new("Lapicero 0.7mm Colores Surt. Pluvia", 900),
            new("Lapicero 0.7mm Negro P/Fino Pluvia", 1600),
            new("Lapicero 0.7mm Rojo P/Fino Pluvia", 500),
            new("Lapicero 4 Puntas Trazo", 10));

        Assert.Equal(EjeFamilia.Color, agrupacion.Eje);
        Assert.Equal(2, agrupacion.Renglones.Count);
        var familia = agrupacion.Renglones[0];
        Assert.Equal("Lapicero 0.7mm P/Fino Pluvia", familia.Titulo);
        // Del más vendido al menos, con el surtido al final aunque se venda más.
        Assert.Equal(["Negro", "Azul", "Rojo", "Colores Surt."], familia.Valores.Select(v => v.Texto));
        Assert.Equal("Lapicero 0.7mm Negro P/Fino Pluvia", familia.Elegido.Nombre);
        Assert.False(agrupacion.Renglones[1].EsFamilia);
    }

    [Fact]
    public void Si_el_color_ya_no_cambia_se_despliega_la_medida_y_se_puede_elegir_la_marca()
    {
        Producto[] negros =
        [
            new("Lapicero 0.7mm Negro P/Fino Pluvia", 1600), new("Lapicero 1.0mm Negro P/Mediano Pluvia", 1000),
            new("Lapicero 0.5mm Negro P/Fino Pluvia", 500), new("Lapicero 0.7mm Negro P/Fino Trazo", 40),
            new("Lapicero 1.0 Mm Negro Trazo", 30),
        ];

        var porMedida = Agrupar(null, negros);
        var porMarca = Agrupar(EjeFamilia.Marca, negros);

        Assert.Equal([EjeFamilia.Medida, EjeFamilia.Marca], porMedida.Ejes);
        Assert.Equal(EjeFamilia.Medida, porMedida.Eje);
        Assert.Equal("Lapicero Negro Pluvia", porMedida.Renglones[0].Titulo);
        // Las medidas, de menor a mayor; Enter, la más vendida.
        Assert.Equal(["0.5mm P/Fino", "0.7mm P/Fino", "1.0mm P/Mediano"], porMedida.Renglones[0].Valores.Select(v => v.Texto));
        Assert.Equal("0.7mm P/Fino", porMedida.Renglones[0].Valores[porMedida.Renglones[0].MasVendido].Texto);
        Assert.Equal(EjeFamilia.Marca, porMarca.Eje);
        Assert.Contains(porMarca.Renglones, r => r.EsFamilia && r.Valores.Select(v => v.Texto).SequenceEqual(["Pluvia", "Trazo"]));
    }

    [Fact]
    public void Lo_que_se_escribe_distinto_junto_a_los_milimetros_no_separa_la_familia()
    {
        var agrupacion = Agrupar(null, new("Lapicero 1.0mm Negro P/Mediano Pluvia", 10), new("Lapicero 1.0mm Rojo P/Medio Pluvia", 5));

        Assert.Single(agrupacion.Renglones);
        Assert.Equal(["Negro", "Rojo"], agrupacion.Renglones[0].Valores.Select(v => v.Texto));
    }

    [Fact]
    public void Los_juegos_no_se_juntan_con_la_pieza_y_entre_juegos_cambia_el_contenido()
    {
        var agrupacion = Agrupar(null,
            new("Jgo Lapiceros C/10 Pluvia", 20), new("Jgo Lapiceros C/4 Pluvia", 30), new("Lapicero 0.7mm Negro P/Fino Pluvia", 1600));

        Assert.Equal(EjeFamilia.Contenido, agrupacion.Eje);
        Assert.Equal(["C/4", "C/10"], agrupacion.Renglones.Single(r => r.EsFamilia).Valores.Select(v => v.Texto));
        Assert.Equal(2, agrupacion.Renglones.Count);
    }

    [Fact]
    public void En_cuadernos_el_rayado_va_primero_y_blanco_es_sin_rayas()
    {
        var agrupacion = Agrupar(null,
            new("Cuaderno Prof 100h Raya Escriba", 50), new("Cuaderno Prof 100h Cuadro Grande Escriba", 40),
            new("Cuaderno Prof 100h Blanco Escriba", 10));

        Assert.Equal(EjeFamilia.Rayado, agrupacion.Eje);
        Assert.Equal("", Familias.Clave(Lector.Leer("Cuaderno Prof 100h Blanco Escriba"), EjeFamilia.Color));
        Assert.Equal(3, agrupacion.Renglones.Single().Valores.Count);
    }

    [Fact]
    public void Sin_nada_que_juntar_cada_producto_va_solo_en_el_orden_de_la_busqueda()
    {
        var agrupacion = Agrupar(null, new("Lapicero 4 Puntas Trazo", 5), new("Goma Blanca Pluvia", 80));

        Assert.Null(agrupacion.Eje);
        Assert.Empty(agrupacion.Ejes);
        Assert.Equal(["Lapicero 4 Puntas Trazo", "Goma Blanca Pluvia"], agrupacion.Renglones.Select(r => r.Elegido.Nombre));
    }
}
