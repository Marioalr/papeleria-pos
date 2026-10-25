using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Cómo se lee cada celda del Excel de los productos al importarlo (Productos › Productos en Excel): los números con sus espacios,
/// su signo de pesos y la coma de los miles, pero nunca una coma que podría ser de decimales; el dinero al centavo; los códigos
/// que Excel guardó como número; «Sí» y «No»; la unidad; y los títulos de las columnas, como se escriban. Y lo que Excel ya leyó
/// como número: un precio que sube a más del doble se revisa antes de aplicar.
/// </summary>
public sealed class LecturaDeCeldasTests
{
    [Theory]
    [InlineData("12.50", 12.5)]
    [InlineData("  12.5 ", 12.5)]
    [InlineData("$1,234.50", 1234.5)]
    [InlineData(" $ 1,234.50 ", 1234.5)]
    [InlineData("1 234", 1234)]
    [InlineData("1 234.75", 1234.75)]
    [InlineData("1,234", 1234)]
    [InlineData("1,234,567.8", 1234567.8)]
    [InlineData("12.", 12)]
    [InlineData(".5", 0.5)]
    [InlineData("-5", -5)]
    [InlineData("-$5", -5)]
    public void Un_numero_escrito_como_texto_tolera_espacios_pesos_y_miles(string escrito, double esperado)
    {
        Assert.Equal((decimal)esperado, LecturaDeCeldas.NumeroDeTexto(escrito, out var comaDecimal));
        Assert.False(comaDecimal);
    }

    [Theory]
    [InlineData("12,50")]
    [InlineData("1,5")]
    [InlineData("1.234,50")]
    [InlineData("12,")]
    [InlineData("1,23.5")]
    [InlineData("1 234,50")]
    [InlineData("0,500")]
    [InlineData("0,5")]
    [InlineData("00,500")]
    [InlineData("$0,250")]
    public void Una_coma_que_podria_ser_de_decimales_no_se_lee(string escrito)
    {
        Assert.Null(LecturaDeCeldas.NumeroDeTexto(escrito, out var comaDecimal));
        Assert.True(comaDecimal);
    }

    [Theory]
    [InlineData("doce")]
    [InlineData("12 pesos")]
    [InlineData("1e5")]
    [InlineData("12.5.3")]
    [InlineData("$")]
    public void Lo_que_no_es_un_numero_no_se_lee(string escrito)
    {
        Assert.Null(LecturaDeCeldas.NumeroDeTexto(escrito, out var comaDecimal));
        Assert.False(comaDecimal);
    }

    [Fact]
    public void El_dinero_se_redondea_al_centavo_y_va_mayor_que_cero()
    {
        // Lo que deja una fórmula (10 % más) se redondea como lo enseña el formato de pesos de Excel.
        Assert.Equal(1101, LecturaDeCeldas.Centavos(CeldaLeida.DeNumero(11.005), "Precio", out var rechazo));
        Assert.Null(rechazo);
        Assert.Equal(1320, LecturaDeCeldas.Centavos(CeldaLeida.DeNumero(13.200000000000001), "Precio", out _));
        Assert.Equal(123450, LecturaDeCeldas.Centavos(CeldaLeida.DeTexto(" $1,234.50 "), "Precio", out _));
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.Vacia, "Precio", out rechazo));
        Assert.Null(rechazo);

        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeNumero(0), "Precio", out rechazo));
        Assert.Equal((PorQueSeIgnora.NumeroInvalido, "«Precio» dice «0»: el precio va mayor que cero."), (rechazo!.PorQue, rechazo.Detalle));
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeNumero(100_000.01), "Precio", out rechazo));
        Assert.Equal("«Precio» dice «100000.01»: revisa el precio (se aceptan hasta $100,000.00).", rechazo!.Detalle);
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeTexto("12,50"), "Precio", out rechazo));
        Assert.Equal(PorQueSeIgnora.ComaDecimal, rechazo!.PorQue);
        // «0,500» no es de miles: es medio peso con coma de decimales (antes se leía 500).
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeTexto("0,500"), "Precio", out rechazo));
        Assert.Equal(PorQueSeIgnora.ComaDecimal, rechazo!.PorQue);
        Assert.Null(LecturaDeCeldas.Cantidad(CeldaLeida.DeTexto("0,500"), "Mínimo", out rechazo));
        Assert.Equal(PorQueSeIgnora.ComaDecimal, rechazo!.PorQue);
        // Un número de 28 cifras escrito como texto no cabe en centavos: se dice, sin tronar.
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeTexto("9999999999999999999999999999"), "Precio", out rechazo));
        Assert.Equal("«Precio» dice «9999999999999999999999999999»: revisa el precio (se aceptan hasta $100,000.00).", rechazo!.Detalle);
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeSiNo(true), "Precio", out rechazo));
        Assert.Equal("«Precio» dice «VERDADERO»: no es un número.", rechazo!.Detalle);
        Assert.Null(LecturaDeCeldas.Centavos(CeldaLeida.DeError("#N/A"), "Precio", out rechazo));
        Assert.Equal((PorQueSeIgnora.DatoInvalido, "«Precio» tiene un error de Excel (#N/A): corrígelo en Excel (si ahí se ve bien, guárdalo otra vez en "
            + "Excel)."), (rechazo!.PorQue, rechazo.Detalle));
    }

    [Fact]
    public void Las_cantidades_a_la_milesima_y_en_piezas_enteras_si_se_vende_por_pieza()
    {
        Assert.Equal(1.5m, LecturaDeCeldas.Cantidad(CeldaLeida.DeNumero(1.5004), "Mínimo", out var rechazo));
        Assert.Null(rechazo);
        Assert.Null(LecturaDeCeldas.Cantidad(CeldaLeida.DeNumero(-1), "Mínimo", out rechazo));
        Assert.Equal("«Mínimo» dice «-1»: no puede ser negativo.", rechazo!.Detalle);
        Assert.Null(LecturaDeCeldas.Cantidad(CeldaLeida.DeNumero(2_000_000), "Máximo", out rechazo));
        Assert.Equal("«Máximo» dice «2000000»: es demasiado grande.", rechazo!.Detalle);
        Assert.Null(LecturaDeCeldas.Enteras(12, UnidadBase.Pieza, "Mayoreo 1: desde"));
        Assert.Null(LecturaDeCeldas.Enteras(1.5m, UnidadBase.Metro, "Mayoreo 1: desde"));
        Assert.Equal("«Mayoreo 1: desde» dice «1.5»: se vende por pieza, así que va en piezas enteras.",
            LecturaDeCeldas.Enteras(1.5m, UnidadBase.Pieza, "Mayoreo 1: desde")!.Detalle);
    }

    [Fact]
    public void Un_codigo_guardado_como_numero_se_lee_con_sus_cifras_y_los_ceros_perdidos_se_reconocen()
    {
        Assert.Equal("7501234567890", LecturaDeCeldas.Codigo(CeldaLeida.DeNumero(7501234567890), "Código", out var rechazo));
        Assert.Null(rechazo);
        Assert.Equal("ABC-12", LecturaDeCeldas.Codigo(CeldaLeida.DeTexto("  ABC-12 "), "Código", out _));
        Assert.Null(LecturaDeCeldas.Codigo(CeldaLeida.DeNumero(7.5e15), "Código", out rechazo));
        Assert.Equal(PorQueSeIgnora.CodigoInvalido, rechazo!.PorQue);
        Assert.Null(LecturaDeCeldas.Codigo(CeldaLeida.DeNumero(12.5), "Código", out rechazo));
        Assert.Equal(PorQueSeIgnora.CodigoInvalido, rechazo!.PorQue);
        Assert.Null(LecturaDeCeldas.Codigo(CeldaLeida.DeTexto(new string('9', 65)), "Código", out rechazo));
        Assert.Equal("«Código» trae un código de 65 letras: no parece un código (van de hasta 64).", rechazo!.Detalle);

        // Varios en una celda, sin repetir (sin importar mayúsculas).
        Assert.Equal(["750123", "INT-1", "abc"], LecturaDeCeldas.Codigos(CeldaLeida.DeTexto("750123, INT-1; abc\nint-1"), "Otros códigos", out rechazo));
        Assert.Null(rechazo);
        Assert.Equal(["750123"], LecturaDeCeldas.Codigos(CeldaLeida.DeNumero(750123), "Otros códigos", out _));

        // Excel le quitó los ceros a un código de 12 o 13 cifras que ya existe.
        var existentes = new HashSet<string> { "0012345678905", "012345678905" };
        Assert.Equal("012345678905", LecturaDeCeldas.ConLosCeros("12345678905", existentes.Contains));
        Assert.Null(LecturaDeCeldas.ConLosCeros("999", existentes.Contains));
        Assert.Null(LecturaDeCeldas.ConLosCeros("A12345678905", existentes.Contains));
    }

    [Theory]
    [InlineData("Sí", true)]
    [InlineData("si", true)]
    [InlineData(" S ", true)]
    [InlineData("x", true)]
    [InlineData("VERDADERO", true)]
    [InlineData("No", false)]
    [InlineData("n", false)]
    [InlineData("FALSO", false)]
    [InlineData("0", false)]
    public void Se_vende_dice_si_o_no(string escrito, bool esperado) =>
        Assert.Equal(esperado, LecturaDeCeldas.SiNo(CeldaLeida.DeTexto(escrito), "Se vende", out _));

    [Fact]
    public void La_clave_escondida_de_un_renglon_se_lee_o_se_dice_que_alguien_la_cambio()
    {
        var clave = Guid.NewGuid();
        Assert.Equal(clave, LecturaDeCeldas.Clave(CeldaLeida.DeTexto($" {clave:N} "), "Clave interna", out var rechazo));
        Assert.Null(rechazo);
        // Un renglón escrito a mano no la trae.
        Assert.Null(LecturaDeCeldas.Clave(CeldaLeida.Vacia, "Clave interna", out rechazo));
        Assert.Null(rechazo);
        Assert.Null(LecturaDeCeldas.Clave(CeldaLeida.DeTexto("cambiada"), "Clave interna", out rechazo));
        Assert.Equal((PorQueSeIgnora.DatoInvalido, "«Clave interna» (la columna escondida al final) dice «cambiada»: es la del Excel que se exportó y no se "
            + "cambia. Bórrala en ese renglón, o cópiala del Excel que se exportó."), (rechazo!.PorQue, rechazo.Detalle));
        Assert.Null(LecturaDeCeldas.Clave(CeldaLeida.DeNumero(12), "Clave interna", out rechazo));
        Assert.Equal(PorQueSeIgnora.DatoInvalido, rechazo!.PorQue);
    }

    [Fact]
    public void La_unidad_tambien_en_singular_y_abreviada()
    {
        Assert.Equal(UnidadBase.Pieza, LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("piezas"), "Unidad", out _));
        Assert.Equal(UnidadBase.Pieza, LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("Pza."), "Unidad", out _));
        Assert.Equal(UnidadBase.Metro, LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("m"), "Unidad", out _));
        Assert.Equal(UnidadBase.Kilogramo, LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("KG"), "Unidad", out _));
        Assert.Equal(UnidadBase.Litro, LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("litro"), "Unidad", out _));
        Assert.Null(LecturaDeCeldas.Unidad(CeldaLeida.DeTexto("cajas"), "Unidad", out var rechazo));
        Assert.Equal("«Unidad» dice «cajas»: va «piezas», «metros», «kilos» o «litros».", rechazo!.Detalle);
        Assert.Equal(["piezas", "metros", "kilos", "litros"], Enum.GetValues<UnidadBase>().Select(ProductosEnExcel.Unidad));
    }

    [Fact]
    public void Los_titulos_se_reconocen_como_se_escriban()
    {
        Assert.Equal(CampoDeProducto.Codigo, ProductosEnExcel.CampoDe("CODIGO"));
        Assert.Equal(CampoDeProducto.OtrosCodigos, ProductosEnExcel.CampoDe(" otros  códigos "));
        Assert.Equal(CampoDeProducto.Minimo, ProductosEnExcel.CampoDe("minimo"));
        Assert.Null(ProductosEnExcel.CampoDe("Notas"));
        Assert.Equal(CampoDePresentacion.CodigoDelProducto, ProductosEnExcel.CampoDePresentacionDe("Código del producto"));
        Assert.Equal((1, false), ProductosEnExcel.MayoreoDe("Mayoreo 1: desde"));
        Assert.Equal((2, true), ProductosEnExcel.MayoreoDe("mayoreo 2 precio"));
        Assert.Equal((12, true), ProductosEnExcel.MayoreoDe("MAYOREO 12 : PRECIO"));
        Assert.Null(ProductosEnExcel.MayoreoDe("Mayoreo"));
        Assert.Null(ProductosEnExcel.MayoreoDe("Mayoreo 0: desde"));
        // Cada título del Excel se reconoce como su columna: lo que se exporta es lo que se importa.
        Assert.All(Enum.GetValues<CampoDeProducto>(), c => Assert.Equal(c, ProductosEnExcel.CampoDe(ProductosEnExcel.Titulo(c))));
        Assert.All(Enum.GetValues<CampoDePresentacion>(), c => Assert.Equal(c, ProductosEnExcel.CampoDePresentacionDe(ProductosEnExcel.Titulo(c))));
        Assert.Equal((3, false), ProductosEnExcel.MayoreoDe(ProductosEnExcel.MayoreoDesde(3)));
        Assert.Equal((3, true), ProductosEnExcel.MayoreoDe(ProductosEnExcel.MayoreoPrecio(3)));
    }

    /// <summary>
    /// «12,50» escrito en una celda: Excel con la configuración de México lo guarda como el número 1250, así que no llega como texto
    /// y no se puede rechazar. Lo que queda en más del doble del precio que tiene se revisa; justo el doble, no. Sin precio de antes
    /// (uno nuevo, una escala nueva, uno sin precio) o desde $0, no hay con qué comparar.
    /// </summary>
    [Theory]
    [InlineData(1250L, 2500L, false)]
    [InlineData(1250L, 2501L, true)]
    [InlineData(1250L, 125_000L, true)]
    [InlineData(1L, 2L, false)]
    [InlineData(1L, 3L, true)]
    [InlineData(1250L, 1300L, false)]
    [InlineData(1250L, 1250L, false)]
    [InlineData(1250L, 125L, false)]
    [InlineData(1250L, null, false)]
    [InlineData(null, 125_000L, false)]
    [InlineData(0L, 1250L, false)]
    public void Un_precio_que_sube_a_mas_del_doble_se_revisa_y_justo_el_doble_no(long? antes, long? despues, bool revisar) =>
        Assert.Equal(revisar, ProductosEnExcel.SubeMucho(antes, despues));
}
