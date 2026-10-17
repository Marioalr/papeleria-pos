using PapeleriaPos.Nucleo.Catalogo;
using SkiaSharp;

namespace PapeleriaPos.Impresion;

/// <summary>Una foto ya lista para las cajas: el JPEG chico, su tamaño y su huella (<see cref="FotoProducto.HuellaDe"/>).</summary>
public sealed record FotoPreparada(byte[] Jpeg, int Ancho, int Alto, string Huella);

/// <summary>
/// La foto de un producto, como se guarda (entrega 13, parte 2): la del celular o la de la PC, derecha (como se vio al
/// tomarla: el celular dice de qué lado estaba), en un JPEG de a lo más <see cref="Lado"/> píxeles por lado y unos 15 KB.
/// Completa, sin recortarla: en la pantalla cabe en su cuadro. Lo transparente queda blanco. La original nunca se guarda.
/// </summary>
public static class FotoDelProducto
{
    /// <summary>Lo más largo de un lado: el verificador la enseña de 120 y las piezas de Ctrl+M de 60 a 72, con el doble de nitidez.</summary>
    public const int Lado = 240;

    /// <summary>Lo más que pesa la foto que llega: la de un celular sin achicar (la página la achica antes de mandarla).</summary>
    public const int BytesMaximos = 15 * 1024 * 1024;

    /// <summary>Lo más grande que se lee: más que esto no es una foto (o es una imagen hecha para tumbar al servicio).</summary>
    public const long PixelesMaximos = 100_000_000;

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
        using var codec = SKCodec.Create(datos);
        if (codec is null || !Formatos.Contains(codec.EncodedFormat))
            throw new ArgumentException("No se reconoce la imagen: usa una foto JPG, PNG o WebP.");
        var (ancho, alto) = (codec.Info.Width, codec.Info.Height);
        if (ancho < 1 || alto < 1 || (long)ancho * alto > PixelesMaximos)
            throw new ArgumentException("La imagen es demasiado grande: usa una foto de menos de 100 megapíxeles.");

        using var leida = Leer(codec);
        var origen = codec.EncodedOrigin;
        // De lado (el celular en vertical): el ancho y el alto se cambian al enderezarla.
        var deLado = origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (derechaAncho, derechaAlto) = deLado ? (leida.Height, leida.Width) : (leida.Width, leida.Height);
        var factor = Math.Min(1.0, (double)Lado / Math.Max(derechaAncho, derechaAlto));
        var final = new SKImageInfo(Math.Max(1, (int)Math.Round(derechaAncho * factor)), Math.Max(1, (int)Math.Round(derechaAlto * factor)),
            SKColorType.Rgba8888, SKAlphaType.Premul);

        using var chica = new SKBitmap(final);
        using (var lienzo = new SKCanvas(chica))
        {
            lienzo.Clear(SKColors.White);
            lienzo.Scale((float)final.Width / derechaAncho, (float)final.Height / derechaAlto);
            Enderezar(lienzo, origen, leida.Width, leida.Height);
            using var imagen = SKImage.FromBitmap(leida);
            // Con mipmaps: una imagen grande (un PNG no se lee ya reducido) se achica sin que salga granulada.
            lienzo.DrawImage(imagen, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
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

    /// <summary>
    /// La imagen, leída ya reducida si el formato lo permite (un JPEG, a 1/2, 1/4 u 1/8), sin bajar del doble del lado final:
    /// una foto de 12 megapíxeles se lee en una fracción del tiempo y de la memoria.
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
