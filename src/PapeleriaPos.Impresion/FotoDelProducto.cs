using PapeleriaPos.Nucleo.Catalogo;
using SkiaSharp;

namespace PapeleriaPos.Impresion;

/// <summary>Una foto ya lista para las cajas: el JPEG chico, su tamaño y su huella (<see cref="FotoProducto.HuellaDe"/>).</summary>
public sealed record FotoPreparada(byte[] Jpeg, int Ancho, int Alto, string Huella);

/// <summary>
/// La foto de un producto, como se guarda (entrega 13, parte 2): la del celular o la de la PC, derecha (como se vio al
/// tomarla: el celular dice de qué lado estaba), en un JPEG de a lo más <see cref="Lado"/> píxeles por lado y unos 15 KB.
/// Completa, sin recortarla: en la pantalla cabe en su cuadro. Lo transparente queda blanco. La original nunca se guarda.
/// <para>
/// Sin gastar de más (revisión de la entrega 13): esto corre en la caja principal, la que cobra, y una imagen chica puede
/// decir que mide 10,000 por 10,000 (300 KB de PNG en blanco ocupaban 900 MB al leerla). Antes de apartar memoria se revisa
/// lo que dice medir (<see cref="Revisar"/>): la foto JPG de una cámara o un celular (de un solo barrido) se lee ya reducida,
/// hasta 1/8, y puede ser de hasta 100 megapíxeles; lo que se lee completo (PNG, WebP, GIF, BMP y el JPG progresivo, que
/// guarda la imagen entera aunque se lea reducida), hasta 16; y nada que diga medir mucho más de lo que su archivo puede
/// traer. Después se achica a la mitad las veces que haga falta, sin copias de más. En el servicio, una a la vez.
/// </para>
/// </summary>
public static class FotoDelProducto
{
    /// <summary>Lo más largo de un lado: el verificador la enseña de 120 y las piezas de Ctrl+M de 60 a 72, con el doble de nitidez.</summary>
    public const int Lado = 240;

    /// <summary>Lo más que pesa la foto que llega: la de un celular sin achicar (la página la achica antes de mandarla).</summary>
    public const int BytesMaximos = 15 * 1024 * 1024;

    /// <summary>
    /// Lo más grande de una foto JPG de un solo barrido (la de una cámara o un celular): se lee ya reducida (hasta 1/8 por lado),
    /// así que aun de 100 megapíxeles ocupa unos pocos MB al leerla.
    /// </summary>
    public const long PixelesMaximos = 100_000_000;

    /// <summary>
    /// Lo más grande de lo que se lee completo: PNG, WebP, GIF, BMP y el JPG progresivo (o de varios barridos), que guarda la
    /// imagen entera. Con 16 megapíxeles son de 64 a 100 MB por un momento, una a la vez; una captura de pantalla de 4K es de 8.
    /// </summary>
    public const long PixelesMaximosCompletas = 16_000_000;

    /// <summary>
    /// Cuántos píxeles puede traer cada byte del archivo, a lo más: una foto de verdad trae mucho menos que esto (de 2 a 50). Lo que
    /// dice medir más es un archivo cortado o hecho para que el servicio aparte la memoria de una imagen que no trae.
    /// </summary>
    public const int PixelesPorByteMaximos = 1024;

    /// <summary>Desde cuántos píxeles se revisa que el archivo pueda traerlos (las chicas casi no ocupan memoria).</summary>
    private const long PixelesSinRevisar = 1_000_000;

    /// <summary>Lo que se busca que pese la guardada: se baja la calidad hasta que quepa (casi siempre, con la primera).</summary>
    public const int BytesObjetivo = 20 * 1024;

    /// <summary>Las calidades del JPEG, de la primera a la última que se prueba.</summary>
    private static readonly int[] Calidades = [78, 68, 58, 48, 38];

    private static readonly HashSet<SKEncodedImageFormat> Formatos =
        [SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp, SKEncodedImageFormat.Gif, SKEncodedImageFormat.Bmp];

    public static FotoPreparada Preparar(byte[] original)
    {
        if (original.Length == 0)
            throw new ArgumentException("No llegó ninguna foto.");
        if (original.Length > BytesMaximos)
            throw new ArgumentException($"La foto pasa de {BytesMaximos / 1024 / 1024} MB: usa una más chica.");
        using var datos = SKData.CreateCopy(original);
        // Leer el encabezado no aparta la imagen: con lo que dice medir se decide si se lee y cómo.
        using var codec = SKCodec.Create(datos);
        if (codec is null || !Formatos.Contains(codec.EncodedFormat))
            throw new ArgumentException("No se reconoce la imagen: usa una foto JPG, PNG o WebP.");
        var unBarrido = codec.EncodedFormat == SKEncodedImageFormat.Jpeg && Barridos(original) <= 1;
        if (Revisar(codec.EncodedFormat, codec.Info.Width, codec.Info.Height, unBarrido, original.Length) is { } porQueNo)
            throw new ArgumentException(porQueNo);

        using var leida = Leer(codec);
        var origen = codec.EncodedOrigin;
        // De lado (el celular en vertical): el ancho y el alto se cambian al enderezarla.
        var deLado = origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (derechaAncho, derechaAlto) = deLado ? (leida.Height, leida.Width) : (leida.Width, leida.Height);
        var factor = Math.Min(1.0, (double)Lado / Math.Max(derechaAncho, derechaAlto));
        var final = new SKImageInfo(Math.Max(1, (int)Math.Round(derechaAncho * factor)), Math.Max(1, (int)Math.Round(derechaAlto * factor)),
            SKColorType.Rgba8888, SKAlphaType.Premul);

        // A la mitad (el promedio de cada 2×2) mientras pase del doble del lado final: la grande se suelta enseguida, sin
        // mipmaps ni copias (antes, la imagen se copiaba entera al dibujarla y sus mipmaps ocupaban un tercio más).
        var reducida = AMenosDelDoble(leida, Lado);
        try
        {
            var (ancho, alto) = (reducida.Width, reducida.Height);
            (derechaAncho, derechaAlto) = deLado ? (alto, ancho) : (ancho, alto);
            using var chica = new SKBitmap(final);
            using (var lienzo = new SKCanvas(chica))
            {
                lienzo.Clear(SKColors.White);
                lienzo.Scale((float)final.Width / derechaAncho, (float)final.Height / derechaAlto);
                Enderezar(lienzo, origen, ancho, alto);
                // Sin copiarla: la imagen usa los mismos píxeles (inmutables) de la reducida.
                reducida.SetImmutable();
                using var imagen = SKImage.FromBitmap(reducida);
                lienzo.DrawImage(imagen, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            chica.SetImmutable();
            using var lista = SKImage.FromBitmap(chica);
            byte[]? jpeg = null;
            foreach (var calidad in Calidades)
            {
                using var codificada = lista.Encode(SKEncodedImageFormat.Jpeg, calidad);
                jpeg = codificada.ToArray();
                if (jpeg.Length <= BytesObjetivo)
                    break;
            }
            return new FotoPreparada(jpeg!, final.Width, final.Height, FotoProducto.HuellaDe(jpeg));
        }
        finally
        {
            if (!ReferenceEquals(reducida, leida))
                reducida.Dispose();
        }
    }

    /// <summary>
    /// Si se puede leer sin gastar de más, con lo que dice medir y lo que pesa el archivo, antes de apartar su memoria: vacío si
    /// sí; si no, por qué. <paramref name="unBarrido"/>: un JPG de un solo barrido (<see cref="Barridos"/>), que se lee ya
    /// reducido; lo demás se lee completo.
    /// </summary>
    public static string? Revisar(SKEncodedImageFormat formato, int ancho, int alto, bool unBarrido, long bytes)
    {
        if (ancho < 1 || alto < 1)
            return "No se reconoce la imagen: usa una foto JPG, PNG o WebP.";
        var pixeles = (long)ancho * alto;
        if (pixeles > PixelesMaximos)
            return $"La imagen es demasiado grande ({ancho:N0} × {alto:N0}): usa una foto de menos de {PixelesMaximos / 1_000_000} megapíxeles.";
        if (!(formato == SKEncodedImageFormat.Jpeg && unBarrido) && pixeles > PixelesMaximosCompletas)
            return $"Esta imagen se lee completa y es demasiado grande ({ancho:N0} × {alto:N0}): usa una de menos de {PixelesMaximosCompletas / 1_000_000} "
                + "megapíxeles, o la foto JPG de la cámara.";
        if (pixeles > PixelesSinRevisar && pixeles > bytes * PixelesPorByteMaximos)
            return $"La imagen dice medir {ancho:N0} × {alto:N0}, pero el archivo es demasiado chico para eso: no es una foto (o se cortó).";
        return null;
    }

    /// <summary>
    /// Cuántos barridos tiene un JPG, recorriendo sus segmentos como el lector (la miniatura que el celular guarda adentro no
    /// cuenta: va dentro de su segmento). Con uno solo (la foto de cualquier cámara o celular) se lee ya reducido; con más
    /// (progresivo, o un color a la vez), el lector guarda todos los coeficientes de la imagen completa, de 2 a 6 bytes por
    /// píxel, aunque la lea reducida.
    /// </summary>
    public static int Barridos(ReadOnlySpan<byte> jpeg)
    {
        var barridos = 0;
        var i = 2;
        while (i + 1 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marca = jpeg[i + 1];
            if (marca == 0xFF)
            {
                // Relleno antes de una marca.
                i++;
                continue;
            }
            if (marca == 0xD9)
                break;
            if (marca is 0x01 or >= 0xD0 and <= 0xD7)
            {
                // Sin largo.
                i += 2;
                continue;
            }
            if (i + 3 >= jpeg.Length)
                break;
            var largo = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (largo < 2)
                break;
            i += 2 + largo;
            if (marca != 0xDA)
                continue;
            barridos++;
            // Lo del barrido llega hasta la siguiente marca: un 0xFF que no es relleno (0x00) ni un reinicio (0xD0 a 0xD7).
            while (i < jpeg.Length)
            {
                var ff = jpeg[i..].IndexOf((byte)0xFF);
                if (ff < 0 || i + ff + 1 >= jpeg.Length)
                    return barridos;
                i += ff;
                if (jpeg[i + 1] != 0x00 && jpeg[i + 1] is not (>= 0xD0 and <= 0xD7))
                    break;
                i += 2;
            }
        }
        return barridos;
    }

    /// <summary>
    /// La imagen, leída ya reducida si el formato lo permite (un JPEG, a 1/2, 1/4 u 1/8; un WebP, a lo que se pida), sin bajar del
    /// doble del lado final: una foto de 12 megapíxeles se lee en una fracción del tiempo y de la memoria.
    /// </summary>
    private static SKBitmap Leer(SKCodec codec)
    {
        var escala = Math.Min(1f, 2f * Lado / Math.Max(codec.Info.Width, codec.Info.Height));
        var tamano = codec.GetScaledDimensions(escala);
        var info = new SKImageInfo(tamano.Width, tamano.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var leida = new SKBitmap(info);
        var resultado = codec.GetPixels(info, leida.GetPixels());
        if (resultado != SKCodecResult.Success)
        {
            leida.Dispose();
            throw new ArgumentException(resultado == SKCodecResult.IncompleteInput
                ? "La foto llegó incompleta: tómala otra vez."
                : "No se pudo leer la foto: tómala otra vez o usa otra.");
        }
        return leida;
    }

    /// <summary>
    /// La imagen a la mitad (el promedio de cada 2×2) mientras su lado más largo pase del doble de <paramref name="lado"/>: cada
    /// mitad ocupa la cuarta parte, y la anterior se suelta en cuanto existe la siguiente. La de entrada no se suelta aquí.
    /// </summary>
    private static SKBitmap AMenosDelDoble(SKBitmap imagen, int lado)
    {
        var actual = imagen;
        while (Math.Max(actual.Width, actual.Height) > 2 * lado)
        {
            var mitad = new SKBitmap(new SKImageInfo(Math.Max(1, actual.Width / 2), Math.Max(1, actual.Height / 2), SKColorType.Rgba8888, SKAlphaType.Premul));
            // Lineal a la mitad exacta: cada punto cae entre cuatro y es su promedio.
            var hecha = actual.ScalePixels(mitad, new SKSamplingOptions(SKFilterMode.Linear));
            if (!ReferenceEquals(actual, imagen))
                actual.Dispose();
            actual = mitad;
            if (!hecha)
            {
                mitad.Dispose();
                throw new ArgumentException("No se pudo achicar la foto: usa otra.");
            }
        }
        return actual;
    }

    /// <summary>
    /// Endereza la foto como se vio al tomarla (la orientación que guarda el celular, EXIF): de lado, de cabeza o al espejo.
    /// <paramref name="ancho"/> y <paramref name="alto"/> son los de la imagen como se leyó.
    /// </summary>
    private static void Enderezar(SKCanvas lienzo, SKEncodedOrigin origen, int ancho, int alto)
    {
        switch (origen)
        {
            case SKEncodedOrigin.TopRight: // al espejo
                lienzo.Translate(ancho, 0);
                lienzo.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // de cabeza
                lienzo.Translate(ancho, alto);
                lienzo.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft: // de cabeza, al espejo
                lienzo.Translate(0, alto);
                lienzo.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop: // de lado, al espejo (transpuesta)
                lienzo.RotateDegrees(90);
                lienzo.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop: // de lado: se gira a la derecha (el celular en vertical)
                lienzo.Translate(alto, 0);
                lienzo.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // de lado, al espejo
                lienzo.Translate(alto, ancho);
                lienzo.RotateDegrees(90);
                lienzo.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom: // de lado: se gira a la izquierda
                lienzo.Translate(0, ancho);
                lienzo.RotateDegrees(270);
                break;
        }
    }
}
