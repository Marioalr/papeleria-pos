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

    /// <summary>
    /// Un PNG de verdad en su encabezado que dice medir <paramref name="ancho"/> por <paramref name="alto"/>, pero casi no trae datos
    /// (como el de la revisión: 300 KB de blanco que decían 10,000 por 9,999). Lo que dice medir se lee sin abrir sus datos.
    /// </summary>
    public static byte[] PngQueDiceMedir(int ancho, int alto)
    {
        var png = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Pedazo(string tipo, byte[] datos)
        {
            png.AddRange(GrandeAlFinal((uint)datos.Length));
            byte[] conTipo = [.. System.Text.Encoding.ASCII.GetBytes(tipo), .. datos];
            png.AddRange(conTipo);
            png.AddRange(GrandeAlFinal(Crc32(conTipo)));
        }
        // 8 bits por color, RGB, sin entrelazar.
        Pedazo("IHDR", [.. GrandeAlFinal((uint)ancho), .. GrandeAlFinal((uint)alto), 8, 2, 0, 0, 0]);
        Pedazo("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Pedazo("IEND", []);
        return [.. png];
    }

    /// <summary>
    /// Un JPG progresivo de verdad (32 × 24, la mitad izquierda roja; 10 barridos), hecho con otro programa: SkiaSharp no los
    /// escribe. Así llegan muchas imágenes de las páginas.
    /// </summary>
    public static byte[] Progresiva() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQU"
        + "FBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wgARCAAYACADASIAAhEBAxEB/8QAFgABAQEAAAAAAAAAAAAAAAAAAAEG/8QAFgEBAQEAAAAAAAAAAAAAAAAABwgG"
        + "/9oADAMBAAIQAxAAAAHEDKP1D/KcABVlD/Kf/8QAFBABAAAAAAAAAAAAAAAAAAAAMP/aAAgBAQABBQJP/8QAFBEBAAAAAAAAAAAAAAAAAAAAIP/aAAgBAwEBPwEf/8QAFBEBAAAAAAAAAAAAAAAAAAAAIP/a"
        + "AAgBAgEBPwEf/8QAFBABAAAAAAAAAAAAAAAAAAAAMP/aAAgBAQAGPwJP/8QAFBABAAAAAAAAAAAAAAAAAAAAMP/aAAgBAQABPyFP/9oADAMBAAIAAwAAABD/AN/9/8QAFBEBAAAAAAAAAAAAAAAAAAAAIP/a"
        + "AAgBAwEBPxAf/8QAFBEBAAAAAAAAAAAAAAAAAAAAIP/aAAgBAgEBPxAf/8QAFBABAAAAAAAAAAAAAAAAAAAAMP/aAAgBAQABPxBP/9k=");

    /// <summary>El mismo JPG diciendo que mide otra cosa (su marca SOF): lo de adentro es el mismo, así que no se podría leer completo.</summary>
    public static byte[] ConMedidas(byte[] jpeg, int ancho, int alto)
    {
        var copia = (byte[])jpeg.Clone();
        for (var i = 2; i + 8 < copia.Length;)
        {
            if (copia[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marca = copia[i + 1];
            if (marca is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                (copia[i + 5], copia[i + 6], copia[i + 7], copia[i + 8]) = ((byte)(alto >> 8), (byte)alto, (byte)(ancho >> 8), (byte)ancho);
                return copia;
            }
            i += 2 + (copia[i + 2] << 8 | copia[i + 3]);
        }
        throw new Xunit.Sdk.XunitException("El JPG no trae su marca SOF.");
    }

    /// <summary>
    /// Un JPG de un barrido con un segmento APP1 que trae adentro lo que parece otro barrido (FF DA), como la miniatura que el
    /// celular guarda en la foto: no cuenta.
    /// </summary>
    public static byte[] ConMiniaturaAdentro(byte[] jpeg)
    {
        byte[] adentro = [.. "Exif\0\0"u8.ToArray(), 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6, 0xFF, 0xD9];
        var largo = adentro.Length + 2;
        return [.. jpeg.Take(2), 0xFF, 0xE1, (byte)(largo >> 8), (byte)(largo & 0xFF), .. adentro, .. jpeg.Skip(2)];
    }

    private static byte[] GrandeAlFinal(uint valor) => [(byte)(valor >> 24), (byte)(valor >> 16), (byte)(valor >> 8), (byte)valor];

    private static uint Crc32(byte[] datos)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in datos)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
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
