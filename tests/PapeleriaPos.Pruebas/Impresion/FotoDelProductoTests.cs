using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Catalogo;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// Cómo se guarda la foto de un producto (entrega 13, parte 2): la del celular o la de la PC, derecha, de a lo más 240 píxeles
/// por lado y unos 15 KB, en JPEG; lo transparente, blanco; lo que no es una foto (o es demasiado grande), no.
/// </summary>
public class FotoDelProductoTests
{
    [Fact]
    public void Una_foto_grande_del_celular_queda_de_240_y_chica_sin_deformarse()
    {
        var original = ImagenesDePrueba.Foto(4000, 3000);

        var foto = FotoDelProducto.Preparar(original);

        Assert.Equal((240, 180), (foto.Ancho, foto.Alto));
        Assert.True(foto.Jpeg.Length <= FotoDelProducto.BytesObjetivo, $"Pesa {foto.Jpeg.Length:N0} bytes.");
        using var leida = ImagenesDePrueba.Leer(foto.Jpeg);
        Assert.Equal((240, 180), (leida.Width, leida.Height));
        // Es un JPEG (lo que leen todas las cajas y los celulares), con su huella, que es la de sus bytes.
        Assert.Equal([0xFF, 0xD8], foto.Jpeg[..2]);
        Assert.Equal(FotoProducto.HuellaDe(foto.Jpeg), foto.Huella);
        Assert.Equal(FotoProducto.LargoHuella, foto.Huella.Length);
        // La franja de en medio sigue roja a la izquierda y azul a la derecha.
        Assert.True(ImagenesDePrueba.EsRojo(leida.GetPixel(40, 90)), leida.GetPixel(40, 90).ToString());
        Assert.True(ImagenesDePrueba.EsAzul(leida.GetPixel(200, 90)), leida.GetPixel(200, 90).ToString());
    }

    [Fact]
    public void Una_foto_tomada_con_el_celular_en_vertical_sale_derecha()
    {
        // Como la guarda el celular: acostada, con la etiqueta «girarla a la derecha» (6). Lo rojo (la izquierda de la guardada)
        // queda arriba.
        var original = ImagenesDePrueba.ConOrientacion(ImagenesDePrueba.Mitades(400, 300), 6);

        var foto = FotoDelProducto.Preparar(original);

        Assert.Equal((180, 240), (foto.Ancho, foto.Alto));
        using var leida = ImagenesDePrueba.Leer(foto.Jpeg);
        Assert.True(ImagenesDePrueba.EsRojo(leida.GetPixel(90, 40)), leida.GetPixel(90, 40).ToString());
        Assert.True(ImagenesDePrueba.EsAzul(leida.GetPixel(90, 200)), leida.GetPixel(90, 200).ToString());
    }

    [Theory]
    [InlineData(3, 400, 300, 200, 150, false)] // de cabeza: lo rojo queda a la derecha
    [InlineData(8, 300, 400, 120, 200, true)] // de lado, al revés: lo rojo queda abajo
    [InlineData(2, 400, 300, 200, 150, false)] // al espejo: lo rojo queda a la derecha
    public void Cada_orientacion_del_celular_queda_derecha(ushort orientacion, int anchoLeido, int altoLeido, int x, int y, bool rojoAbajo)
    {
        var original = ImagenesDePrueba.ConOrientacion(ImagenesDePrueba.Mitades(400, 300), orientacion);

        var foto = FotoDelProducto.Preparar(original);

        var escala = 240.0 / Math.Max(anchoLeido, altoLeido);
        Assert.Equal(((int)Math.Round(anchoLeido * escala), (int)Math.Round(altoLeido * escala)), (foto.Ancho, foto.Alto));
        using var leida = ImagenesDePrueba.Leer(foto.Jpeg);
        var (cercaDelInicio, cercaDelFinal) = rojoAbajo
            ? (leida.GetPixel(x / 2, 30), leida.GetPixel(x / 2, foto.Alto - 30))
            : (leida.GetPixel(30, y / 2), leida.GetPixel(foto.Ancho - 30, y / 2));
        Assert.True(ImagenesDePrueba.EsAzul(cercaDelInicio), cercaDelInicio.ToString());
        Assert.True(ImagenesDePrueba.EsRojo(cercaDelFinal), cercaDelFinal.ToString());
    }

    [Fact]
    public void Una_chica_no_se_agranda_y_lo_transparente_queda_blanco()
    {
        var foto = FotoDelProducto.Preparar(ImagenesDePrueba.Transparente(90));

        Assert.Equal((90, 90), (foto.Ancho, foto.Alto));
        using var leida = ImagenesDePrueba.Leer(foto.Jpeg);
        var esquina = leida.GetPixel(3, 3);
        Assert.True(esquina is { Red: > 245, Green: > 245, Blue: > 245 }, esquina.ToString());
        var centro = leida.GetPixel(45, 45);
        Assert.True(centro is { Red: < 40, Green: < 40, Blue: < 40 }, centro.ToString());
    }

    [Fact]
    public void Aun_la_mas_dificil_de_comprimir_queda_chica()
    {
        var foto = FotoDelProducto.Preparar(ImagenesDePrueba.Ruido(1200, 900));

        Assert.Equal((240, 180), (foto.Ancho, foto.Alto));
        // Se baja la calidad hasta que quepa: el ruido es lo peor y aun así no pasa de unos 30 KB.
        Assert.True(foto.Jpeg.Length <= 32 * 1024, $"Pesa {foto.Jpeg.Length:N0} bytes.");
    }

    [Fact]
    public void Un_png_y_un_webp_tambien_sirven()
    {
        var png = FotoDelProducto.Preparar(ImagenesDePrueba.Foto(800, 800, SKEncodedImageFormat.Png));
        var webp = FotoDelProducto.Preparar(ImagenesDePrueba.Foto(600, 900, SKEncodedImageFormat.Webp, 90));

        Assert.Equal((240, 240), (png.Ancho, png.Alto));
        Assert.Equal((160, 240), (webp.Ancho, webp.Alto));
        Assert.All(new[] { png, webp }, f => Assert.Equal([0xFF, 0xD8], f.Jpeg[..2]));
    }

    [Fact]
    public void Lo_que_no_es_una_foto_o_es_demasiado_grande_no_se_guarda()
    {
        Assert.Contains("No llegó", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar([])).Message);
        Assert.Contains("No se reconoce", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar("no es una foto"u8.ToArray())).Message);
        Assert.Contains("15 MB", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(new byte[FotoDelProducto.BytesMaximos + 1])).Message);
        // Una que dice medir 20,000 por 20,000 no se lee: se rechaza por lo que dice, antes de leerla.
        Assert.Contains("demasiado grande", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.BmpEnorme(20_000, 20_000))).Message);
        // Una cortada a la mitad (se cortó al subirla) tampoco.
        var foto = ImagenesDePrueba.Foto(800, 600);
        Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(foto[..(foto.Length / 2)]));
    }

    // ── Sin gastar de más (revisión de la entrega 13): esto corre en la caja principal, la que cobra ──

    [Fact]
    public void Lo_que_se_lee_completo_va_hasta_16_megapixeles_y_lo_demas_se_rechaza_antes_de_apartar_memoria()
    {
        // El de la revisión: un PNG que dice medir 10,000 × 9,999 (en blanco pesaba 300 KB y ocupaba 900 MB al leerlo).
        Assert.Contains("se lee completa", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.PngQueDiceMedir(10_000, 9_999))).Message);
        Assert.Contains("se lee completa", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.BmpEnorme(9_999, 9_999))).Message);
        // Lo que dice medir más de lo que su archivo puede traer, aunque quepa en los 16: tampoco (el BMP de 118 bytes apartaba
        // 377 MB antes de ver que no traía nada).
        Assert.Contains("demasiado chico", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.PngQueDiceMedir(4_000, 4_000))).Message);
        Assert.Contains("demasiado chico", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.BmpEnorme(3_000, 3_000))).Message);
    }

    [Fact]
    public void El_jpg_progresivo_se_lee_completo_y_va_hasta_16_megapixeles()
    {
        var progresiva = ImagenesDePrueba.Progresiva();
        var deUnBarrido = ImagenesDePrueba.Foto(400, 300);

        // El progresivo guarda la imagen entera aunque se lea reducido; el de la cámara o el celular (un barrido) no, aunque
        // traiga adentro una miniatura con su propio barrido (va dentro de su segmento).
        Assert.True(FotoDelProducto.Barridos(progresiva) > 1);
        Assert.Equal(1, FotoDelProducto.Barridos(deUnBarrido));
        Assert.Equal(1, FotoDelProducto.Barridos(ImagenesDePrueba.ConMiniaturaAdentro(deUnBarrido)));
        Assert.Equal(0, FotoDelProducto.Barridos("no es un jpg"u8));
        // El mismo progresivo diciendo que mide 5,000 × 4,000 (20 megapíxeles): no se lee.
        Assert.Contains("se lee completa", Assert.Throws<ArgumentException>(() => FotoDelProducto.Preparar(ImagenesDePrueba.ConMedidas(progresiva, 5_000, 4_000))).Message);
        // Como es (32 × 24), sí: con su mitad roja a la izquierda.
        var foto = FotoDelProducto.Preparar(progresiva);
        Assert.Equal((32, 24), (foto.Ancho, foto.Alto));
        using var leida = ImagenesDePrueba.Leer(foto.Jpeg);
        Assert.True(ImagenesDePrueba.EsRojo(leida.GetPixel(4, 12)), leida.GetPixel(4, 12).ToString());
        Assert.True(ImagenesDePrueba.EsAzul(leida.GetPixel(28, 12)), leida.GetPixel(28, 12).ToString());
        // Y una de la cámara con su miniatura adentro se lee igual que sin ella.
        var conMiniatura = FotoDelProducto.Preparar(ImagenesDePrueba.ConMiniaturaAdentro(deUnBarrido));
        Assert.Equal((240, 180), (conMiniatura.Ancho, conMiniatura.Alto));
    }

    /// <summary>
    /// Con lo que dice medir y lo que pesa el archivo, antes de apartar memoria: si se lee, y si no, por qué. Solo el JPG puede
    /// ser de un barrido (el de la cámara o el celular, que se lee reducido).
    /// </summary>
    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, 12_000, 8_000, true, 5_000_000L, null)] // del celular, 96 megapíxeles: se lee reducida
    [InlineData(SKEncodedImageFormat.Jpeg, 12_000, 9_000, true, 5_000_000L, "demasiado grande")]
    [InlineData(SKEncodedImageFormat.Jpeg, 4_000, 4_000, false, 3_000_000L, null)] // progresiva: hasta 16
    [InlineData(SKEncodedImageFormat.Jpeg, 5_000, 4_000, false, 3_000_000L, "se lee completa")]
    [InlineData(SKEncodedImageFormat.Png, 4_000, 4_000, false, 2_000_000L, null)]
    [InlineData(SKEncodedImageFormat.Png, 4_001, 4_000, false, 2_000_000L, "se lee completa")]
    [InlineData(SKEncodedImageFormat.Webp, 5_000, 4_000, false, 2_000_000L, "se lee completa")]
    [InlineData(SKEncodedImageFormat.Gif, 5_000, 4_000, false, 2_000_000L, "se lee completa")]
    [InlineData(SKEncodedImageFormat.Bmp, 5_000, 4_000, false, 60_000_000L, "se lee completa")]
    [InlineData(SKEncodedImageFormat.Png, 2_000, 2_000, false, 1_000L, "demasiado chico")] // 4 megapíxeles en 1 KB
    [InlineData(SKEncodedImageFormat.Jpeg, 10_000, 9_000, true, 50_000L, "demasiado chico")]
    [InlineData(SKEncodedImageFormat.Png, 900, 900, false, 100L, null)] // las chicas casi no ocupan: no se revisa lo que pesan
    [InlineData(SKEncodedImageFormat.Png, 0, 900, false, 100L, "No se reconoce")]
    public void Antes_de_leerla_se_revisa_lo_que_dice_medir(SKEncodedImageFormat formato, int ancho, int alto, bool unBarrido, long bytes, string? porQue)
    {
        var dice = FotoDelProducto.Revisar(formato, ancho, alto, unBarrido, bytes);

        if (porQue is null)
            Assert.Null(dice);
        else
            Assert.Contains(porQue, dice);
    }

    [Fact]
    public void La_huella_y_el_archivo_de_la_pantalla_se_leen_igual_en_la_caja_y_en_el_servicio()
    {
        var variante = Guid.NewGuid();
        var huella = FotoProducto.HuellaDe("una foto"u8);

        var archivo = FotoProducto.Archivo(variante, huella);

        Assert.Equal($"api/fotos/{variante:N}-{huella}.jpg", archivo);
        Assert.True(FotoProducto.LeerArchivo(archivo, out var leida, out var suHuella));
        Assert.Equal((variante, huella), (leida, suHuella));
        Assert.True(FotoProducto.LeerArchivo("/" + archivo, out _, out _));
        Assert.True(FotoProducto.LeerArchivo($"{variante:N}-{huella}.jpg", out _, out _));
        Assert.False(FotoProducto.LeerArchivo("_framework/blazor.webview.js", out _, out _));
        Assert.False(FotoProducto.LeerArchivo($"api/fotos/{variante:N}-{huella}.png", out _, out _));
        Assert.False(FotoProducto.LeerArchivo($"api/fotos/{variante}", out _, out _));
    }
}
