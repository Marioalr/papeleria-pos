using PapeleriaPos.Nucleo.Contratos;
using SkiaSharp;

namespace PapeleriaPos.Impresion;

/// <summary>
/// Convierte la imagen del logo al formato de la impresora térmica: blanco y negro, un bit por punto, centrado y a lo
/// más 384 puntos de ancho (unos 48 mm en papel de 80 mm). Los grises se simulan con puntos (difusión de error).
/// </summary>
public static class LogoTicket
{
    public const int AnchoMaximo = 384;
    public const int AltoMaximo = 240;
    public const int BytesMaximos = 5 * 1024 * 1024;

    public static LogoTicketDto DesdeImagen(byte[] archivo, int anchoMaximo = AnchoMaximo, int altoMaximo = AltoMaximo)
    {
        if (archivo.Length == 0 || archivo.Length > BytesMaximos)
            throw new ArgumentException("El logo debe ser una imagen de menos de 5 MB.", nameof(archivo));

        using var original = Decodificar(archivo)
            ?? throw new ArgumentException("No se reconoce la imagen: usa PNG, JPG o BMP.", nameof(archivo));

        var escala = Math.Min(1.0, Math.Min((double)anchoMaximo / original.Width, (double)altoMaximo / original.Height));
        var ancho = Math.Max(1, (int)Math.Round(original.Width * escala));
        var alto = Math.Max(1, (int)Math.Round(original.Height * escala));

        using var reducida = new SKBitmap(new SKImageInfo(ancho, alto, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var lienzo = new SKCanvas(reducida))
        {
            lienzo.Clear(SKColors.White); // lo transparente queda blanco
            using var imagen = SKImage.FromBitmap(original);
            lienzo.DrawImage(imagen, new SKRect(0, 0, ancho, alto), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        // Grises (0 = negro, 255 = blanco) y difusión de error de Floyd–Steinberg.
        var gris = new float[ancho * alto];
        for (var y = 0; y < alto; y++)
            for (var x = 0; x < ancho; x++)
            {
                var c = reducida.GetPixel(x, y);
                gris[y * ancho + x] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            }

        var bytesPorRenglon = (ancho + 7) / 8;
        var bits = new byte[bytesPorRenglon * alto];
        for (var y = 0; y < alto; y++)
            for (var x = 0; x < ancho; x++)
            {
                var i = y * ancho + x;
                var negro = gris[i] < 128;
                if (negro)
                    bits[y * bytesPorRenglon + x / 8] |= (byte)(0x80 >> (x % 8));
                var error = gris[i] - (negro ? 0 : 255);
                Repartir(gris, ancho, alto, x + 1, y, error * 7 / 16);
                Repartir(gris, ancho, alto, x - 1, y + 1, error * 3 / 16);
                Repartir(gris, ancho, alto, x, y + 1, error * 5 / 16);
                Repartir(gris, ancho, alto, x + 1, y + 1, error * 1 / 16);
            }

        return new LogoTicketDto(ancho, alto, Convert.ToBase64String(bits));
    }

    /// <summary>El logo ya convertido, como PNG, para verlo en pantalla tal como saldrá impreso.</summary>
    public static byte[] ComoPng(LogoTicketDto logo)
    {
        var bits = Convert.FromBase64String(logo.BitsBase64);
        var bytesPorRenglon = (logo.Ancho + 7) / 8;
        using var mapa = new SKBitmap(logo.Ancho, logo.Alto);
        for (var y = 0; y < logo.Alto; y++)
            for (var x = 0; x < logo.Ancho; x++)
            {
                var negro = (bits[y * bytesPorRenglon + x / 8] & (0x80 >> (x % 8))) != 0;
                mapa.SetPixel(x, y, negro ? SKColors.Black : SKColors.White);
            }
        using var imagen = SKImage.FromBitmap(mapa);
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);
        return datos.ToArray();
    }

    /// <summary>SkiaSharp lanza su propio error con datos que no son imagen; aquí se vuelve "no se reconoce".</summary>
    private static SKBitmap? Decodificar(byte[] archivo)
    {
        try
        {
            return SKBitmap.Decode(archivo);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void Repartir(float[] gris, int ancho, int alto, int x, int y, float error)
    {
        if (x >= 0 && x < ancho && y < alto)
            gris[y * ancho + x] += error;
    }
}
