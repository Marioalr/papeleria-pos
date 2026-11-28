using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PapeleriaPos.Nucleo.Imagenes;
using SkiaSharp;

namespace PapeleriaPos.Impresion;

public enum FormatoDeImagen
{
    Png,
    Jpeg,
}

/// <summary>
/// Una imagen de «Juntar imágenes para Word» (docs/propuestas/juntar-imagenes.md), tal como llegó: <see cref="Datos"/> son los bytes del
/// PNG o del JPEG que se copió, sin achicar ni volver a comprimir (el dueño, 2026-11-28: WhatsApp ya las comprime, casi ninguna es foto,
/// y si se corrige algo en Word una achicada se ve mal). Solo se vuelve a escribir, en PNG y sin perder nada, la que hay que enderezar,
/// girar o pasar a gris, o la que llegó en otro formato que Word no lee en RTF (WebP, GIF, BMP).
/// </summary>
/// <param name="Huella">SHA-256 de lo que llegó: la misma imagen copiada dos veces se cuenta una vez.</param>
public sealed record ImagenJuntada(byte[] Datos, FormatoDeImagen Formato, int AnchoPx, int AltoPx, string Huella)
{
    public ImagenPorAcomodar ParaAcomodar(int giro) => giro % 180 == 0 ? new(AnchoPx, AltoPx) : new(AltoPx, AnchoPx);
}

/// <summary>Leer, preparar y enseñar las imágenes que se juntan.</summary>
public static class ImagenesJuntadas
{
    /// <summary>Lo más grande que se acepta: una captura de 4K es de 8 megapíxeles; una foto de celular sin comprimir, de 12 a 50.</summary>
    public const long PixelesMaximos = 60_000_000;

    /// <summary>
    /// Lo que llegó del portapapeles o de un archivo. PNG y JPEG derechos van tal cual; lo demás se vuelve PNG (sin perder). Null si no es
    /// una imagen que se pueda leer.
    /// </summary>
    public static ImagenJuntada? Preparar(byte[] datos)
    {
        if (datos.Length == 0)
            return null;
        var huella = Convert.ToHexString(SHA256.HashData(datos));
        using var skData = SKData.CreateCopy(datos);
        using var codec = SKCodec.Create(skData);
        if (codec is null || (long)codec.Info.Width * codec.Info.Height is var pixeles && (pixeles <= 0 || pixeles > PixelesMaximos))
            return null;
        var formato = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => FormatoDeImagen.Png,
            SKEncodedImageFormat.Jpeg => FormatoDeImagen.Jpeg,
            _ => (FormatoDeImagen?)null,
        };
        if (formato is { } f && codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return new ImagenJuntada(datos, f, codec.Info.Width, codec.Info.Height, huella);
        using var leida = SKBitmap.Decode(codec);
        if (leida is null)
            return null;
        var png = Dibujar(leida, codec.EncodedOrigin, gris: false);
        return png with { Huella = huella };
    }

    /// <summary>
    /// Como va a Word: girada (90, 180 o 270, a la derecha) y en gris si se pidió. Sin cambios, la misma (los mismos bytes); con cambios, un
    /// PNG con todos sus píxeles.
    /// </summary>
    public static ImagenJuntada ComoVa(ImagenJuntada imagen, int giro, bool gris)
    {
        giro = ((giro % 360) + 360) % 360;
        if (giro == 0 && !gris)
            return imagen;
        using var leida = SKBitmap.Decode(imagen.Datos) ?? throw new InvalidOperationException("La imagen ya no se pudo leer.");
        var origen = giro switch
        {
            90 => SKEncodedOrigin.RightTop,
            180 => SKEncodedOrigin.BottomRight,
            270 => SKEncodedOrigin.LeftBottom,
            _ => SKEncodedOrigin.TopLeft,
        };
        return Dibujar(leida, origen, gris) with { Huella = imagen.Huella };
    }

    /// <summary>Una miniatura JPEG para la pantalla (nunca va a Word): a lo más <paramref name="lado"/> píxeles por lado, sobre blanco.</summary>
    public static byte[] Miniatura(ImagenJuntada imagen, int lado = 112)
    {
        using var leida = SKBitmap.Decode(imagen.Datos) ?? throw new InvalidOperationException("La imagen ya no se pudo leer.");
        var factor = Math.Min(1.0, (double)lado / Math.Max(leida.Width, leida.Height));
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(leida.Width * factor)), Math.Max(1, (int)Math.Round(leida.Height * factor)));
        using var chica = new SKBitmap(info);
        using (var lienzo = new SKCanvas(chica))
        {
            lienzo.Clear(SKColors.White);
            using var imagenSk = SKImage.FromBitmap(leida);
            lienzo.DrawImage(imagenSk, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var jpeg = SKImage.FromBitmap(chica).Encode(SKEncodedImageFormat.Jpeg, 75);
        return jpeg.ToArray();
    }

    /// <summary>Endereza o gira, y pasa a gris si se pide; en PNG, del mismo tamaño en píxeles.</summary>
    private static ImagenJuntada Dibujar(SKBitmap leida, SKEncodedOrigin origen, bool gris)
    {
        var deLado = origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (ancho, alto) = deLado ? (leida.Height, leida.Width) : (leida.Width, leida.Height);
        using var final = new SKBitmap(new SKImageInfo(ancho, alto, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var lienzo = new SKCanvas(final))
        {
            lienzo.Clear(SKColors.Transparent);
            FotoDelProducto.Enderezar(lienzo, origen, leida.Width, leida.Height);
            using var pintura = new SKPaint();
            if (gris)
            {
                // La luminancia (Rec. 709), dejando la transparencia como venía.
                pintura.ColorFilter = SKColorFilter.CreateColorMatrix(
                [
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0, 0, 0, 1, 0,
                ]);
            }
            // Píxel por píxel (mismo tamaño, sin suavizar): nada se mezcla con su vecino.
            using var imagenSk = SKImage.FromBitmap(leida);
            lienzo.DrawImage(imagenSk, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), pintura);
        }
        using var png = SKImage.FromBitmap(final).Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        return new ImagenJuntada(bytes, FormatoDeImagen.Png, ancho, alto, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}

/// <summary>
/// El bloque que se pega en Word (formato RTF, el que Word entiende al copiar de otro documento): cada renglón del acomodo es un párrafo
/// con sus imágenes en línea, cada una con su tamaño en centímetros, la separación entre ellas (un espacio de 1 punto ensanchado) y debajo
/// del renglón. Word las pasa de hoja él solo. Con la hoja (para «Abrir en Word»), el documento lleva además su tamaño y sus márgenes.
/// </summary>
public static class DocumentoParaWord
{
    /// <summary>Twips (vigésimos de punto) por centímetro.</summary>
    public const double TwipsPorCm = 1440 / 2.54;

    /// <summary>Lo que mide un espacio en letra de 1 punto (unos 0.25 pt): se descuenta del ensanchado.</summary>
    private const int AnchoDelEspacio = 5;

    /// <param name="imagenes">Como van (ya giradas y en gris si se pidió), en el orden de <paramref name="acomodo"/>.</param>
    /// <param name="hoja">Para «Abrir en Word»: el tamaño de la hoja y sus márgenes. Null al copiar (se pega donde está el cursor).</param>
    public static string Rtf(IReadOnlyList<ImagenJuntada> imagenes, Acomodo acomodo, double separacion, bool lineasDeCorte, Hoja? hoja)
    {
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Calibri;}}{\colortbl;\red154\green163\blue173;}");
        if (hoja is not null)
        {
            rtf.Append(CultureInfo.InvariantCulture, $@"\paperw{Tw(hoja.Ancho)}\paperh{Tw(hoja.Alto)}");
            rtf.Append(CultureInfo.InvariantCulture, $@"\margl{Tw(hoja.Izquierda)}\margr{Tw(hoja.Derecha)}\margt{Tw(hoja.Arriba)}\margb{Tw(hoja.Abajo)}");
            if (hoja.Ancho > hoja.Alto)
                rtf.Append(@"\landscape");
            rtf.Append(@"\sectd");
            if (hoja.Ancho > hoja.Alto)
                rtf.Append(@"\lndscpsxn");
        }
        rtf.Append('\n');
        var sepTw = Tw(Math.Max(0, separacion));
        var ensanchado = Math.Max(0, sepTw - AnchoDelEspacio);
        foreach (var renglon in acomodo.Renglones())
        {
            rtf.Append(CultureInfo.InvariantCulture, $@"\pard\plain\ql\li0\ri0\fi0\sb0\sa{sepTw}\sl0\f0\fs2 ");
            for (var i = 0; i < renglon.Count; i++)
            {
                if (i > 0)
                    rtf.Append(CultureInfo.InvariantCulture, $@"{{\fs2\expndtw{ensanchado} }}");
                var pieza = renglon[i];
                var imagen = imagenes[pieza.Indice];
                if (lineasDeCorte)
                    rtf.Append(@"{\chbrdr\brdrs\brdrw5\brdrcf1 ");
                rtf.Append(imagen.Formato == FormatoDeImagen.Png ? @"{\pict\pngblip" : @"{\pict\jpegblip");
                rtf.Append(CultureInfo.InvariantCulture,
                    $@"\picw{imagen.AnchoPx}\pich{imagen.AltoPx}\picwgoal{Tw(pieza.Ancho)}\pichgoal{Tw(pieza.Alto)}");
                rtf.Append('\n');
                Hex(rtf, imagen.Datos);
                rtf.Append('}');
                if (lineasDeCorte)
                    rtf.Append('}');
            }
            rtf.Append("\\par\n");
        }
        rtf.Append('}');
        return rtf.ToString();
    }

    /// <summary>RTF es texto de un byte por letra: así va al portapapeles y al archivo.</summary>
    public static byte[] Bytes(string rtf) => Encoding.ASCII.GetBytes(rtf);

    internal static int Tw(double cm) => (int)Math.Round(cm * TwipsPorCm, MidpointRounding.AwayFromZero);

    private static void Hex(StringBuilder rtf, byte[] datos)
    {
        const string Digitos = "0123456789abcdef";
        for (var i = 0; i < datos.Length; i++)
        {
            rtf.Append(Digitos[datos[i] >> 4]).Append(Digitos[datos[i] & 0xF]);
            if (i % 64 == 63)
                rtf.Append('\n');
        }
    }
}
