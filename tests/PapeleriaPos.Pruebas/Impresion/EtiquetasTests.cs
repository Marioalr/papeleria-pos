using System.Text.RegularExpressions;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Impresion;

public class CodigosDeBarrasTests
{
    [Fact]
    public void Las_tablas_de_ean13_cumplen_sus_reglas()
    {
        for (var d = 0; d < 10; d++)
        {
            // L tiene paridad impar; R es L al revés en colores; G es R de atrás para adelante.
            Assert.True(CodigosDeBarras.L[d].Count(c => c == '1') % 2 == 1);
            Assert.Equal(new string(CodigosDeBarras.L[d].Select(c => c == '1' ? '0' : '1').ToArray()), CodigosDeBarras.R[d]);
            Assert.Equal(new string(CodigosDeBarras.R[d].Reverse().ToArray()), CodigosDeBarras.G[d]);
        }
        Assert.Equal(10, CodigosDeBarras.L.Distinct().Count());
    }

    [Fact]
    public void Un_ean13_tiene_sus_guardas_y_la_paridad_del_primer_digito()
    {
        var modulos = new string(CodigosDeBarras.CodificarEan13("4006381333931").Select(b => b ? '1' : '0').ToArray());

        Assert.Equal(95, modulos.Length);
        Assert.Equal("101", modulos[..3]);
        Assert.Equal("01010", modulos.Substring(45, 5));
        Assert.Equal("101", modulos[^3..]);
        // El 4 inicial pone L G L L G G en los seis de la izquierda: 0, 0, 6, 3, 8, 1.
        Assert.Equal(CodigosDeBarras.L[0] + CodigosDeBarras.G[0] + CodigosDeBarras.L[6] + CodigosDeBarras.L[3] + CodigosDeBarras.G[8] + CodigosDeBarras.G[1],
            modulos.Substring(3, 42));
        Assert.Throws<ArgumentException>(() => CodigosDeBarras.CodificarEan13("4006381333932"));
    }

    [Fact]
    public void La_tabla_de_code128_tiene_107_simbolos_de_11_modulos()
    {
        Assert.Equal(107, CodigosDeBarras.Patrones.Count);
        Assert.Equal(107, CodigosDeBarras.Patrones.Distinct().Count());
        Assert.All(CodigosDeBarras.Patrones.Take(106), p => Assert.Equal((6, 11), (p.Length, p.Sum(c => c - '0'))));
        Assert.Equal(13, CodigosDeBarras.Patrones[106].Sum(c => c - '0'));
    }

    [Fact]
    public void El_digito_de_verificacion_de_code128_es_el_del_ejemplo()
    {
        // Inicio B (104) + P J J 1 2 3 C, cada uno por su posición: 879 entre 103 deja 55.
        Assert.Equal([104, 48, 42, 42, 17, 18, 19, 35, 55, 106], CodigosDeBarras.Simbolos("PJJ123C"));
    }

    [Theory]
    [InlineData("P000190")]
    [InlineData("ABC-12")]
    [InlineData("12345678")]
    [InlineData("7501234567")]
    [InlineData("X1234567Y89")]
    [InlineData("2000000000017")]
    public void Lo_que_se_codifica_en_code128_se_lee_igual(string texto)
    {
        Assert.Equal(texto, Leer(CodigosDeBarras.CodificarCode128(texto)));
    }

    [Fact]
    public void Los_digitos_van_de_dos_en_dos_y_salen_mas_cortos()
    {
        Assert.Equal(8, CodigosDeBarras.Simbolos("P000190").Count);
        Assert.Equal(105, CodigosDeBarras.Simbolos("12345678")[0]);
        Assert.Throws<ArgumentException>(() => CodigosDeBarras.Simbolos("Ñandú"));
    }

    [Fact]
    public void Los_internos_salen_en_ean13_y_lo_demas_en_code128()
    {
        Assert.Equal(Simbologia.Ean13, CodigosDeBarras.Para(Ean13.Completar("200000000001")).Simbologia);
        Assert.Equal(Simbologia.Code128, CodigosDeBarras.Para("P000190").Simbologia);
    }

    /// <summary>Un lector de Code 128 de prueba: de las franjas a los símbolos, revisa el de verificación y arma el texto.</summary>
    private static string Leer(bool[] modulos)
    {
        var anchos = new List<int>();
        for (var i = 0; i < modulos.Length;)
        {
            var inicio = i;
            while (i < modulos.Length && modulos[i] == modulos[inicio])
                i++;
            anchos.Add(i - inicio);
        }
        var simbolos = new List<int>();
        for (var i = 0; i + 6 <= anchos.Count; i += 6)
        {
            var patron = string.Concat(anchos.Skip(i).Take(i + 7 == anchos.Count ? 7 : 6));
            simbolos.Add(CodigosDeBarras.Patrones.ToList().IndexOf(patron));
            if (i + 7 == anchos.Count)
                break;
        }
        Assert.Equal(106, simbolos[^1]);
        var suma = simbolos[0];
        for (var p = 1; p < simbolos.Count - 2; p++)
            suma += simbolos[p] * p;
        Assert.Equal(suma % 103, simbolos[^2]);

        var texto = new System.Text.StringBuilder();
        var enC = simbolos[0] == 105;
        foreach (var s in simbolos.Skip(1).SkipLast(2))
        {
            if (s == 99 || s == 100)
                enC = s == 99;
            else if (enC)
                texto.Append(s.ToString("00"));
            else
                texto.Append((char)(s + ' '));
        }
        return texto.ToString();
    }
}

public class HojaEtiquetasTests
{
    private static readonly FormatoEtiquetas Hoja30 = FormatoEtiquetas.Comunes.Single(f => f.Id == "5160");
    private static readonly FormatoEtiquetas Hoja80 = FormatoEtiquetas.Comunes.Single(f => f.Id == "5167");

    [Fact]
    public void Las_hojas_comunes_caben_en_una_carta()
    {
        Assert.All(FormatoEtiquetas.Comunes, f => Assert.Null(f.Problema()));
        Assert.NotNull((Hoja30 with { Filas = 12 }).Problema());
        Assert.NotNull((Hoja30 with { PasoHorizontalMm = 50 }).Problema());
    }

    [Theory]
    [InlineData(30, 1, 1)]
    [InlineData(31, 1, 2)]
    [InlineData(1, 30, 1)]
    [InlineData(2, 30, 2)]
    [InlineData(0, 1, 1)]
    public void Cuantas_hojas_salen(int etiquetas, int empezarEn, int hojas) => Assert.Equal(hojas, HojaEtiquetas.Hojas(Hoja30, etiquetas, empezarEn));

    [Fact]
    public void Empieza_en_la_etiqueta_pedida_con_el_ajuste_y_el_nombre_escapado()
    {
        var etiquetas = Enumerable.Range(1, 28).Select(i => new Etiqueta($"Goma <{i}>", Ean13.Completar($"2{i:00000000000}"))).ToList();

        var html = HojaEtiquetas.Html(Hoja30, etiquetas, empezarEn: 4, ajusteXMm: 1.5m, ajusteYMm: -0.5m);

        Assert.Equal(28, Regex.Count(html, "class=\"etiqueta\""));
        Assert.Equal(2, Regex.Count(html, "class=\"hoja\""));
        // La cuarta es la primera de la segunda fila: 4.76 + 1.5 a la izquierda, 12.7 + 25.4 − 0.5 arriba.
        Assert.Contains("class=\"etiqueta\" style=\"left:6.26mm;top:37.6mm\"", html);
        Assert.Contains("Goma &lt;1&gt;", html);
        Assert.DoesNotContain("Goma <1>", html);
        Assert.Contains("@page { size: letter; margin: 0; }", html);
    }

    [Fact]
    public void El_precio_solo_va_si_se_pide()
    {
        var con = HojaEtiquetas.Html(Hoja30, [new Etiqueta("Clip", "P000190", 250)]);
        var sin = HojaEtiquetas.Html(Hoja30, [new Etiqueta("Clip", "P000190")]);

        Assert.Contains("$2.50", con);
        Assert.DoesNotContain("$", sin.Replace("$$", ""));
    }

    [Fact]
    public void En_la_etiqueta_chica_el_codigo_cabe_y_uno_larguisimo_no()
    {
        var ean = CodigosDeBarras.Para(Ean13.Completar("200000000001"));
        var largo = CodigosDeBarras.Para("ABCDEFGHIJKLMNOPQRSTUVWX");

        Assert.InRange(HojaEtiquetas.Modulo(Hoja80, ean)!.Value, HojaEtiquetas.ModuloMinimoMm, HojaEtiquetas.ModuloMaximoMm);
        Assert.Null(HojaEtiquetas.Modulo(Hoja80, largo));
        Assert.Contains("no cabe en esta etiqueta", HojaEtiquetas.Html(Hoja80, [new Etiqueta("Listón", largo.Texto)]));
    }

    [Fact]
    public void La_hoja_de_prueba_dibuja_todas_las_etiquetas_numeradas()
    {
        var html = HojaEtiquetas.Html(Hoja80, [], soloBordes: true);

        Assert.Equal(80, Regex.Count(html, "class=\"etiqueta borde\""));
        Assert.Contains(">80</div>", html);
    }
}

public class HojaDeCodigosTests
{
    private static readonly List<ProductoEnHoja> Productos =
    [
        new("Escolar", "Goma blanca <chica>", Ean13.Completar("200000000011")),
        new("Oficina", "Clip mariposa", "P000190", 150),
        new("Escolar", "Botón dorado", Ean13.Completar("200000000012")),
    ];

    [Fact]
    public void Separa_por_departamento_en_el_orden_en_que_aparecen()
    {
        var html = HojaDeCodigos.Html("Códigos del mostrador · Matriz", Productos, porDepartamento: true);

        Assert.Equal(2, Regex.Count(html, "<h2>"));
        Assert.True(html.IndexOf("<h2>Escolar</h2>", StringComparison.Ordinal) < html.IndexOf("<h2>Oficina</h2>", StringComparison.Ordinal));
        // Los de Escolar quedan juntos aunque en la lista estén separados.
        Assert.True(html.IndexOf("Botón dorado", StringComparison.Ordinal) < html.IndexOf("Clip mariposa", StringComparison.Ordinal));
        Assert.Equal(3, Regex.Count(html, "<svg class=\"barras\""));
        Assert.Contains("Goma blanca &lt;chica&gt;", html);
        Assert.Contains("$1.50", html);
    }

    [Fact]
    public void Sin_separar_respeta_el_orden_de_la_lista()
    {
        var html = HojaDeCodigos.Html("Hoja", Productos, porDepartamento: false);

        Assert.DoesNotContain("<h2>", html);
        Assert.True(html.IndexOf("Clip mariposa", StringComparison.Ordinal) < html.IndexOf("Botón dorado", StringComparison.Ordinal));
    }
}
