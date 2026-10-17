using SkiaSharp;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// Imágenes para probar las fotos de los productos (entrega 13, parte 2), hechas aquí: una «foto» con degradados y figuras (se
/// comprime como una foto de verdad), la misma con la orientación que guarda el celular (EXIF), una con transparencia y una de
/// puro ruido (lo más difícil de comprimir).
/// </summary>
internal static class ImagenesDePrueba
{
    /// <summary>
    /// Una «foto» del tamaño que se pida: un degradado de fondo, figuras de colores y la mitad izquierda roja y la derecha azul
    /// en la franja de en medio, para saber de qué lado quedó cada cosa.
    /// </summary>
    public static byte[] Foto(int ancho, int alto, SKEncodedImageFormat formato = SKEncodedImageFormat.Jpeg, int calidad = 92)
    {
        using var mapa = new SKBitmap(ancho, alto);
        using (var lienzo = new SKCanvas(mapa))
        {
            using var fondo = new SKPaint();
            fondo.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(ancho, alto),
                [new SKColor(240, 230, 210), new SKColor(120, 140, 170)], SKShaderTileMode.Clamp);
            lienzo.DrawRect(0, 0, ancho, alto, fondo);
            using var pintura = new SKPaint { IsAntialias = true };
            for (var i = 0; i < 12; i++)
            {
                pintura.Color = new SKColor((byte)(40 + i * 17), (byte)(200 - i * 13), (byte)(90 + i * 11));
                lienzo.DrawCircle(ancho * (i + 1) / 13f, alto * (i % 3 + 1) / 4f, Math.Min(ancho, alto) / 9f, pintura);
            }
            pintura.Color = SKColors.Red;
            lienzo.DrawRect(0, alto * 0.4f, ancho / 2f, alto * 0.2f, pintura);
            pintura.Color = SKColors.Blue;
            lienzo.DrawRect(ancho / 2f, alto * 0.4f, ancho / 2f, alto * 0.2f, pintura);
        }
        return Codificar(mapa, formato, calidad);
    }

    /// <summary>Mitad izquierda roja y mitad derecha azul, nada más: para ver de qué lado queda cada una al enderezarla.</summary>
    public static byte[] Mitades(int ancho, int alto)
    {
        using var mapa = new SKBitmap(ancho, alto);
        using (var lienzo = new SKCanvas(mapa))
        {
            lienzo.Clear(SKColors.Blue);
            using var rojo = new SKPaint { Color = SKColors.Red };
            lienzo.DrawRect(0, 0, ancho / 2f, alto, rojo);
        }
        return Codificar(mapa, SKEncodedImageFormat.Jpeg, 95);
    }

    /// <summary>Un PNG transparente con un cuadro negro en medio (como la imagen de un producto copiada de una página).</summary>
    public static byte[] Transparente(int lado)
    {
        using var mapa = new SKBitmap(new SKImageInfo(lado, lado, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var lienzo = new SKCanvas(mapa))
        {
            lienzo.Clear(SKColors.Transparent);
            using var negro = new SKPaint { Color = SKColors.Black };
            lienzo.DrawRect(lado / 3f, lado / 3f, lado / 3f, lado / 3f, negro);
        }
        return Codificar(mapa, SKEncodedImageFormat.Png, 100);
    }

    /// <summary>Puro ruido, con su propia semilla (siempre el mismo): lo peor para comprimir.</summary>
    public static byte[] Ruido(int ancho, int alto)
    {
        var azar = new Random(2026);
        using var mapa = new SKBitmap(ancho, alto);
        for (var y = 0; y < alto; y++)
            for (var x = 0; x < ancho; x++)
                mapa.SetPixel(x, y, new SKColor((byte)azar.Next(256), (byte)azar.Next(256), (byte)azar.Next(256)));
        return Codificar(mapa, SKEncodedImageFormat.Jpeg, 95);
    }

    /// <summary>
    /// La misma foto JPEG con la orientación que guarda el celular (la etiqueta EXIF 0x0112, en un APP1 justo después del
    /// inicio): 6 es «de lado, girarla a la derecha», como sale una foto con el celular en vertical.
    /// </summary>
    public static byte[] ConOrientacion(byte[] jpeg, ushort orientacion)
    {
        var tiff = new List<byte>();
        tiff.AddRange("II"u8.ToArray());
        tiff.AddRange(BitConverter.GetBytes((ushort)42));
        tiff.AddRange(BitConverter.GetBytes(8u));
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        tiff.AddRange(BitConverter.GetBytes((ushort)0x0112));
        tiff.AddRange(BitConverter.GetBytes((ushort)3));
        tiff.AddRange(BitConverter.GetBytes(1u));
        tiff.AddRange(BitConverter.GetBytes(orientacion));
        tiff.AddRange(new byte[2]);
        tiff.AddRange(BitConverter.GetBytes(0u));
        var exif = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var largo = exif.Length + 2;
        return [.. jpeg.Take(2), 0xFF, 0xE1, (byte)(largo >> 8), (byte)(largo & 0xFF), .. exif, .. jpeg.Skip(2)];
    }

    /// <summary>
    /// Un BMP que dice medir <paramref name="ancho"/> por <paramref name="alto"/> pero casi no trae datos: la que se hace para
    /// tumbar a quien la lea completa.
    /// </summary>
    public static byte[] BmpEnorme(int ancho, int alto)
    {
        var bmp = new List<byte>();
        bmp.AddRange("BM"u8.ToArray());
        bmp.AddRange(BitConverter.GetBytes(54u + 64));
        bmp.AddRange(BitConverter.GetBytes(0u));
        bmp.AddRange(BitConverter.GetBytes(54u));
        bmp.AddRange(BitConverter.GetBytes(40u));
        bmp.AddRange(BitConverter.GetBytes(ancho));
        bmp.AddRange(BitConverter.GetBytes(alto));
        bmp.AddRange(BitConverter.GetBytes((ushort)1));
        bmp.AddRange(BitConverter.GetBytes((ushort)24));
        bmp.AddRange(new byte[24]);
        bmp.AddRange(new byte[64]);
        return [.. bmp];
    }

    /// <summary>Lee una imagen (para revisar cómo quedó).</summary>
    public static SKBitmap Leer(byte[] datos) => SKBitmap.Decode(datos) ?? throw new Xunit.Sdk.XunitException("No se pudo leer la imagen.");

    /// <summary>Si el color se parece más al rojo que al azul (y es claramente uno de los dos).</summary>
    public static bool EsRojo(SKColor color) => color.Red > 150 && color.Blue < 110;

    public static bool EsAzul(SKColor color) => color.Blue > 150 && color.Red < 110;

    private static byte[] Codificar(SKBitmap mapa, SKEncodedImageFormat formato, int calidad)
    {
        using var imagen = SKImage.FromBitmap(mapa);
        using var datos = imagen.Encode(formato, calidad);
        return datos.ToArray();
    }
}
