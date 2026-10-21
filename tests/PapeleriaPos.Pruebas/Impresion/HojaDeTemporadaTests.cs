using System.Text.RegularExpressions;
using PapeleriaPos.Impresion;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// La hoja de la temporada y las tarjetas de precio (entrega 13, parte 3), sin impresora: cómo se reparten en las hojas carta
/// (el modelo de cada hoja) y qué lleva cada una en su página HTML.
/// </summary>
public class HojaDeTemporadaTests
{
    private static ProductoDeHoja Producto(int i, byte[]? foto = null) =>
        new($"Producto {i}", $"P{i % 10}", 1000 + i * 100, $"20000000{i:D5}", foto);

    private static LugarDeHoja Lugar(string nombre, int productos, int desde = 1) =>
        new(nombre, Enumerable.Range(desde, productos).Select(i => Producto(i)).ToList());

    [Fact]
    public void Cada_lugar_empieza_con_su_nombre_y_sus_productos_de_tres_en_tres_en_su_orden()
    {
        var paginas = HojaDeTemporada.Paginas([Lugar("Mesa de la entrada", 5), Lugar("Repisa vacía", 0), Lugar("Pared", 2, desde: 6)]);

        var pagina = Assert.Single(paginas);
        Assert.Collection(pagina.Filas,
            f => Assert.Equal(new LugarEnLaHoja("Mesa de la entrada", false), f),
            f => Assert.Equal(["Producto 1", "Producto 2", "Producto 3"], Assert.IsType<ProductosEnLaHoja>(f).Productos.Select(p => p.Nombre)),
            f => Assert.Equal(["Producto 4", "Producto 5"], Assert.IsType<ProductosEnLaHoja>(f).Productos.Select(p => p.Nombre)),
            f => Assert.Equal(new LugarEnLaHoja("Pared", false), f),
            f => Assert.Equal(["Producto 6", "Producto 7"], Assert.IsType<ProductosEnLaHoja>(f).Productos.Select(p => p.Nombre)));
    }

    [Fact]
    public void Lo_que_no_cabe_pasa_a_la_hoja_siguiente_con_el_nombre_de_su_lugar_y_nunca_un_nombre_solo_al_pie()
    {
        // Una hoja carta lleva el nombre de un lugar y cinco renglones (243.4 mm: 9 + 5 × 40 = 209; uno más no cabe).
        var paginas = HojaDeTemporada.Paginas([Lugar("Mesa de la entrada", 16), Lugar("Pared", 1, desde: 17)]);

        Assert.Equal(2, paginas.Count);
        Assert.Equal(6, paginas[0].Filas.Count);
        Assert.Equal(new LugarEnLaHoja("Mesa de la entrada", false), paginas[0].Filas[0]);
        Assert.Equal(15, paginas[0].Filas.OfType<ProductosEnLaHoja>().Sum(f => f.Productos.Count));
        // En la segunda, la mesa sigue con su nombre; luego la pared, con el suyo.
        Assert.Equal(new LugarEnLaHoja("Mesa de la entrada", true), paginas[1].Filas[0]);
        Assert.Equal(["Producto 16"], Assert.IsType<ProductosEnLaHoja>(paginas[1].Filas[1]).Productos.Select(p => p.Nombre));
        Assert.Equal(new LugarEnLaHoja("Pared", false), paginas[1].Filas[2]);
        Assert.All(paginas, p => Assert.IsNotType<LugarEnLaHoja>(p.Filas[^1]));
        // Todos los productos, una vez y en orden.
        Assert.Equal(Enumerable.Range(1, 17).Select(i => $"Producto {i}"),
            paginas.SelectMany(p => p.Filas.OfType<ProductosEnLaHoja>()).SelectMany(f => f.Productos).Select(p => p.Nombre));
    }

    [Fact]
    public void Si_el_primer_renglon_de_un_lugar_no_cabe_su_nombre_tampoco_se_queda()
    {
        // Cinco renglones de la mesa llenan la hoja: el nombre de la pared y su renglón (49 mm) ya no caben con ellos.
        var paginas = HojaDeTemporada.Paginas([Lugar("Mesa de la entrada", 15), Lugar("Pared", 3, desde: 16)]);

        Assert.Equal(2, paginas.Count);
        Assert.IsType<ProductosEnLaHoja>(paginas[0].Filas[^1]);
        Assert.Equal(new LugarEnLaHoja("Pared", false), paginas[1].Filas[0]);
        Assert.Single(HojaDeTemporada.Paginas([]));
    }

    [Fact]
    public void La_hoja_lleva_la_foto_o_las_iniciales_el_nombre_el_precio_y_el_codigo_dibujado()
    {
        var foto = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var datos = new DatosDeHojaDeTemporada("Día de <Muertos>", "del 16 de septiembre al 5 de noviembre", "Matriz", new DateOnly(2026, 10, 17),
            [new LugarDeHoja("Mesa de la entrada",
            [
                new ProductoDeHoja("Papel picado & calaveras", "PP", 4500, "2000000005012", foto),
                new ProductoDeHoja("Catrina de cartón", "CC", null, "CAT-01"),
            ])]);

        var html = HojaDeTemporada.Html(datos);

        Assert.Contains("Día de &lt;Muertos&gt;", html);
        Assert.Contains("del 16 de septiembre al 5 de noviembre · Matriz · impresa el 17/10/2026", html);
        Assert.Contains("Hoja 1 de 1", html);
        Assert.Contains("Mesa de la entrada", html);
        Assert.Contains($"src=\"data:image/jpeg;base64,{Convert.ToBase64String(foto)}\"", html);
        Assert.Contains(">CC<", html);
        Assert.Contains("Papel picado &amp; calaveras", html);
        Assert.Contains(">$45.00<", html);
        Assert.Contains("sin precio", html);
        Assert.Contains(">2000000005012<", html);
        Assert.Contains(">CAT-01<", html);
        Assert.Equal(2, Regex.Matches(html, "<svg class=\"barras\"").Count);
        Assert.Contains("@page { size: letter; margin: 0; }", html);
    }

    [Fact]
    public void Las_tarjetas_van_de_tres_en_tres_y_seis_renglones_por_hoja_en_su_orden()
    {
        var tarjetas = Enumerable.Range(1, 20).Select(i => new TarjetaDePrecioEnHoja($"Producto {i}", i * 100, $"20000000{i:D5}")).ToList();

        var paginas = HojaDeTarjetas.Paginas(tarjetas);

        Assert.Equal(2, paginas.Count);
        Assert.Equal(HojaDeTarjetas.PorHoja, paginas[0].Tarjetas.Count);
        Assert.Equal((0, 0, "Producto 1"), (paginas[0].Tarjetas[0].Fila, paginas[0].Tarjetas[0].Columna, paginas[0].Tarjetas[0].Tarjeta.Nombre));
        Assert.Equal((0, 2, "Producto 3"), (paginas[0].Tarjetas[2].Fila, paginas[0].Tarjetas[2].Columna, paginas[0].Tarjetas[2].Tarjeta.Nombre));
        Assert.Equal((1, 0, "Producto 4"), (paginas[0].Tarjetas[3].Fila, paginas[0].Tarjetas[3].Columna, paginas[0].Tarjetas[3].Tarjeta.Nombre));
        Assert.Equal((5, 2, "Producto 18"), (paginas[0].Tarjetas[17].Fila, paginas[0].Tarjetas[17].Columna, paginas[0].Tarjetas[17].Tarjeta.Nombre));
        Assert.Equal(["Producto 19", "Producto 20"], paginas[1].Tarjetas.Select(t => t.Tarjeta.Nombre));
        Assert.Equal((0, 1), (paginas[1].Tarjetas[1].Fila, paginas[1].Tarjetas[1].Columna));
        Assert.Single(HojaDeTarjetas.Paginas([]));
        // Caben en la hoja carta, al centro.
        Assert.True(HojaDeTarjetas.MargenIzquierdoMm > 5 && HojaDeTarjetas.MargenSuperiorMm > 5);
    }

    [Fact]
    public void Cada_tarjeta_lleva_el_nombre_el_precio_grande_y_el_codigo_para_escanearla()
    {
        var html = HojaDeTarjetas.Html(
        [
            new TarjetaDePrecioEnHoja("Calculadora \"básica\" 8 dígitos", 8500, "2000000005029"),
            new TarjetaDePrecioEnHoja("Agenda 2027", 123_450, "AG-2027"),
        ]);

        Assert.Contains("Calculadora &quot;básica&quot; 8 dígitos", html);
        // El precio corto, más grande; el largo, un poco menos para que quepa.
        Assert.Contains("font-size:34pt\">$85.00<", html);
        Assert.Contains("font-size:23pt\">$1,234.50<", html);
        Assert.Contains(">2000000005029<", html);
        Assert.Contains(">AG-2027<", html);
        Assert.Equal(2, Regex.Matches(html, "<svg class=\"barras\"").Count);
        Assert.Equal(2, Regex.Matches(html, "class=\"tarjeta\"").Count);
        Assert.Contains("left:10.45mm;top:13.7mm", html);
    }
}
