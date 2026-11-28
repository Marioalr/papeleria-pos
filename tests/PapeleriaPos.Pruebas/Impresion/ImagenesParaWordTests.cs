using System.Text.RegularExpressions;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Imagenes;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// Las imágenes de «Juntar imágenes para Word» van tal como llegaron (el dueño, 2026-11-28), y el bloque RTF lleva cada una con su tamaño
/// en centímetros, un párrafo por renglón del acomodo.
/// </summary>
public class ImagenesParaWordTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, FormatoDeImagen.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg, FormatoDeImagen.Jpeg)]
    public void Png_y_jpeg_derechos_van_con_los_mismos_bytes(SKEncodedImageFormat formato, FormatoDeImagen esperado)
    {
        var original = ImagenesDePrueba.Foto(1080, 2340, formato);
        var imagen = ImagenesJuntadas.Preparar(original)!;
        Assert.Same(original, imagen.Datos);
        Assert.Equal(esperado, imagen.Formato);
        Assert.Equal((1080, 2340), (imagen.AnchoPx, imagen.AltoPx));
    }

    [Fact]
    public void Otro_formato_se_vuelve_png_con_todos_sus_pixeles()
    {
        var webp = ImagenesDePrueba.Foto(640, 480, SKEncodedImageFormat.Webp);
        var imagen = ImagenesJuntadas.Preparar(webp)!;
        Assert.Equal(FormatoDeImagen.Png, imagen.Formato);
        Assert.Equal((640, 480), (imagen.AnchoPx, imagen.AltoPx));
        using var leida = ImagenesDePrueba.Leer(imagen.Datos);
        Assert.Equal((640, 480), (leida.Width, leida.Height));
    }

    [Fact]
    public void La_foto_de_lado_se_endereza_sin_achicarla()
    {
        // Orientación 6: el celular en vertical; la imagen guardada es ancha y se ve alta.
        var deLado = ImagenesDePrueba.ConOrientacion(ImagenesDePrueba.Mitades(400, 200), 6);
        var imagen = ImagenesJuntadas.Preparar(deLado)!;
        Assert.Equal(FormatoDeImagen.Png, imagen.Formato);
        Assert.Equal((200, 400), (imagen.AnchoPx, imagen.AltoPx));
        // Girada a la derecha: lo rojo (que estaba a la izquierda) queda arriba.
        using var leida = ImagenesDePrueba.Leer(imagen.Datos);
        Assert.True(ImagenesDePrueba.EsRojo(leida.GetPixel(100, 50)));
        Assert.True(ImagenesDePrueba.EsAzul(leida.GetPixel(100, 350)));
    }

    [Fact]
    public void Lo_que_no_es_imagen_no_se_junta()
    {
        Assert.Null(ImagenesJuntadas.Preparar([]));
        Assert.Null(ImagenesJuntadas.Preparar("hola, esto es texto"u8.ToArray()));
    }

    [Fact]
    public void La_misma_imagen_tiene_la_misma_huella()
    {
        var datos = ImagenesDePrueba.Foto(300, 300);
        Assert.Equal(ImagenesJuntadas.Preparar(datos)!.Huella, ImagenesJuntadas.Preparar(datos.ToArray())!.Huella);
        Assert.NotEqual(ImagenesJuntadas.Preparar(datos)!.Huella, ImagenesJuntadas.Preparar(ImagenesDePrueba.Foto(301, 300))!.Huella);
    }

    [Fact]
    public void Girarla_cambia_el_ancho_por_el_alto_y_no_la_achica()
    {
        var imagen = ImagenesJuntadas.Preparar(ImagenesDePrueba.Mitades(400, 200))!;
        Assert.Same(imagen, ImagenesJuntadas.ComoVa(imagen, 0, gris: false));
        var girada = ImagenesJuntadas.ComoVa(imagen, 90, gris: false);
        Assert.Equal((200, 400), (girada.AnchoPx, girada.AltoPx));
        Assert.Equal(FormatoDeImagen.Png, girada.Formato);
        Assert.Equal(new ImagenPorAcomodar(200, 400), imagen.ParaAcomodar(90));
        using var leida = ImagenesDePrueba.Leer(girada.Datos);
        Assert.True(ImagenesDePrueba.EsRojo(leida.GetPixel(100, 50)));
    }

    [Fact]
    public void En_blanco_y_negro_queda_gris_del_mismo_tamano()
    {
        var imagen = ImagenesJuntadas.Preparar(ImagenesDePrueba.Mitades(200, 100))!;
        var gris = ImagenesJuntadas.ComoVa(imagen, 0, gris: true);
        Assert.Equal((200, 100), (gris.AnchoPx, gris.AltoPx));
        using var leida = ImagenesDePrueba.Leer(gris.Datos);
        var pixel = leida.GetPixel(50, 50);
        Assert.True(Math.Abs(pixel.Red - pixel.Green) <= 1 && Math.Abs(pixel.Green - pixel.Blue) <= 1, $"No es gris: {pixel}");
    }

    [Fact]
    public void La_miniatura_es_chica_y_solo_para_la_pantalla()
    {
        var imagen = ImagenesJuntadas.Preparar(ImagenesDePrueba.Foto(1080, 2340))!;
        using var mini = ImagenesDePrueba.Leer(ImagenesJuntadas.Miniatura(imagen));
        Assert.Equal(112, Math.Max(mini.Width, mini.Height));
    }

    [Fact]
    public void El_rtf_lleva_cada_imagen_con_su_tamano_en_twips_y_sus_bytes_tal_cual()
    {
        var png = ImagenesJuntadas.Preparar(ImagenesDePrueba.Foto(800, 1000, SKEncodedImageFormat.Png))!;
        var jpeg = ImagenesJuntadas.Preparar(ImagenesDePrueba.Foto(1600, 1000))!;
        var imagenes = new[] { png, jpeg, png };
        var acomodo = AcomodoDeImagenes.Acomodar(imagenes.Select(i => i.ParaAcomodar(0)).ToList(), Hoja.Carta, ModoDelTamano.Alto, 5, 0.3)!;
        var rtf = DocumentoParaWord.Rtf(imagenes, acomodo, 0.3, lineasDeCorte: false, hoja: null);

        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.EndsWith("}", rtf);
        // 5 cm = 2835 twips de alto; 4 cm y 8 cm de ancho.
        var picts = Regex.Matches(rtf, @"\\pict\\(png|jpeg)blip\\picw(\d+)\\pich(\d+)\\picwgoal(\d+)\\pichgoal(\d+)\n([0-9a-f\n]+)\}");
        Assert.Equal(3, picts.Count);
        Assert.Equal(["png", "jpeg", "png"], picts.Select(m => m.Groups[1].Value));
        Assert.Equal(["2268", "4535", "2268"], picts.Select(m => m.Groups[4].Value));
        Assert.All(picts, m => Assert.Equal("2835", m.Groups[5].Value));
        Assert.Equal(["800", "1600", "800"], picts.Select(m => m.Groups[2].Value));
        Assert.Equal(Convert.ToHexString(jpeg.Datos).ToLowerInvariant(), picts[1].Groups[6].Value.Replace("\n", ""));
        // Dos renglones (la tercera ya no cabe: 4 + 0.3 + 8 + 0.3 + 4 = 16.6 > 15.59): dos párrafos, con 0.3 cm (170 twips) debajo.
        Assert.Equal(2, Regex.Matches(rtf, @"\\par\n").Count);
        Assert.Equal(2, Regex.Matches(rtf, @"\\sa170\\").Count);
        // Entre las dos del primer renglón, el espacio ensanchado a 0.3 cm.
        Assert.Single(Regex.Matches(rtf, @"\\expndtw165 "));
        Assert.DoesNotContain(@"\paperw", rtf);
        Assert.DoesNotContain(@"\chbrdr", rtf);
    }

    [Fact]
    public void Para_abrir_en_word_lleva_la_hoja_y_sus_margenes()
    {
        var imagen = ImagenesJuntadas.Preparar(ImagenesDePrueba.Foto(400, 400))!;
        var hoja = Hoja.De(TamanoDeHoja.Carta, horizontal: true, 1, 1, 1.5, 1.5);
        var acomodo = AcomodoDeImagenes.Acomodar([imagen.ParaAcomodar(0)], hoja, ModoDelTamano.Alto, 5, 0.3)!;
        var rtf = DocumentoParaWord.Rtf([imagen], acomodo, 0.3, lineasDeCorte: true, hoja);
        Assert.Contains(@"\paperw15840\paperh12240", rtf);
        Assert.Contains(@"\margl850\margr850\margt567\margb567", rtf);
        Assert.Contains(@"\landscape", rtf);
        Assert.Contains(@"{\chbrdr\brdrs\brdrw5\brdrcf1 {\pict", rtf);
        Assert.All(DocumentoParaWord.Bytes(rtf), b => Assert.True(b < 128));
    }
}
