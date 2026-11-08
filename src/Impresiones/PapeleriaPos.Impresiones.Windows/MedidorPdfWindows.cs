// Adaptado de Control Print v1.12 (ControlPrint.App/Infrastructure/PdfReader.cs).
using System.Runtime.InteropServices;
using PapeleriaPos.Impresiones.Medicion;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>
/// Mide un PDF para cotizarlo: cada página se dibuja con el motor de PDF de Windows (Windows.Data.Pdf, incluido en
/// Windows) y se mide con el mismo método que las impresiones, a 300 dpi. El archivo no se guarda en ningún lado.
/// </summary>
public sealed class MedidorPdfWindows : IMedidorPdf
{
    /// <summary>Páginas que se miden; las demás usan el promedio (igual que las impresiones).</summary>
    public const int MaximoDePaginas = 500;
    private const double Dpi = 300;

    /// <summary>
    /// Lo más que se dibuja de una página: una carta a 300 dpi (unos 34 MB). Un plano o una lona se dibuja a menos dpi (auditoría,
    /// 2026-11-04: una hoja de 90×120 cm pedía más de un 1 GB en la caja 2); lo que se mide es la fracción de tinta, que no cambia.
    /// </summary>
    private const double PixelesMaximos = 8.5 * 300 * 11 * 300;

    public async Task<DocumentoMedido> MedirAsync(Stream pdf, IProgress<(int Hechas, int Total)>? avance = null, CancellationToken ct = default)
    {
        // El archivo llega de la pantalla (o de la red): se lee completo antes de abrirlo. Si ya viene en memoria (Cotizar), se usa ese:
        // no dos copias de un PDF grande en la caja 2 (auditoría).
        MemoryStream memoria;
        if (pdf is MemoryStream yaEnMemoria && yaEnMemoria.TryGetBuffer(out _))
        {
            memoria = yaEnMemoria;
        }
        else
        {
            memoria = new MemoryStream();
            await pdf.CopyToAsync(memoria, ct);
        }
        memoria.Position = 0;
        if (memoria.GetBuffer().AsSpan(0, (int)Math.Min(memoria.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("El archivo no es un PDF.");

        using var flujo = memoria.AsRandomAccessStream();
        PdfDocument documento;
        try
        {
            documento = await PdfDocument.LoadFromStreamAsync(flujo).AsTask(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(ex.HResult == unchecked((int)0x8007052B)
                ? "El PDF tiene contraseña; no se puede cotizar."
                : "No se pudo abrir el PDF (puede estar dañado): " + ex.Message, ex);
        }
        // Un PDF con permisos (no imprimir, no copiar) abre sin clave y se mide; el que pide clave ya falló al abrirlo (auditoría).
        var total = (int)documento.PageCount;
        if (total == 0)
            throw new InvalidDataException("El PDF no tiene páginas.");

        byte[]? miniatura = null;
        var paginas = new List<PageCoverage>(total);
        var aMedir = Math.Min(total, MaximoDePaginas);
        for (var i = 0; i < aMedir; i++)
        {
            ct.ThrowIfCancellationRequested();
            avance?.Report((i, total));
            try
            {
                using var pagina = documento.GetPage((uint)i);
                double anchoPulg = pagina.Size.Width / 96, altoPulg = pagina.Size.Height / 96;
                var dpi = Math.Min(Dpi, Math.Sqrt(PixelesMaximos / Math.Max(anchoPulg * altoPulg, 0.01)));
                var (pixeles, ancho, alto) = await DibujarAsync(pagina, Lado(anchoPulg * dpi), Lado(altoPulg * dpi));
                // La medición recorre millones de píxeles: fuera del hilo de la pantalla.
                paginas.Add(await Task.Run(() => PageRasterizer.Analyze(Pixeles(pixeles, ancho, alto), anchoPulg * altoPulg), ct));
                if (i == 0)
                    miniatura = await MiniaturaAsync(pagina);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDataException))
            {
                // Una página que Windows no puede dibujar (dañada, o sin memoria): se dice para el cajero, no «Algo falló» (auditoría).
                throw new InvalidDataException($"No se pudo medir la página {i + 1} del PDF (puede estar dañada): cotízalo por páginas.", ex);
            }
        }
        avance?.Report((aMedir, total));

        string? nota = null;
        if (total > aMedir)
        {
            var promedio = new PageCoverage(paginas.Average(p => p.C), paginas.Average(p => p.M), paginas.Average(p => p.Y),
                paginas.Average(p => p.K), paginas.Average(p => p.Gray), paginas.Average(p => p.AreaSqIn), paginas.Any(p => p.HasColor));
            paginas.AddRange(Enumerable.Repeat(promedio, total - aMedir));
            nota = $"Se midieron {aMedir} páginas; el resto usa el promedio.";
        }
        return new DocumentoMedido(paginas, miniatura, nota);
    }

    private static uint Lado(double pixeles) => (uint)Math.Max(1, Math.Round(pixeles));

    private static ReadOnlySpan<uint> Pixeles(byte[] bgra, uint ancho, uint alto) =>
        MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, (int)(ancho * alto * 4)));

    private static async Task<byte[]> MiniaturaAsync(PdfPage pagina, int ladoMayor = 320)
    {
        var escala = ladoMayor / Math.Max(pagina.Size.Width, pagina.Size.Height);
        var (pixeles, ancho, alto) = await DibujarAsync(pagina, Lado(pagina.Size.Width * escala), Lado(pagina.Size.Height * escala));
        return PngWriter.EncodeBgr32(Pixeles(pixeles, ancho, alto), (int)ancho, (int)alto);
    }

    private static async Task<(byte[] Pixeles, uint Ancho, uint Alto)> DibujarAsync(PdfPage pagina, uint ancho, uint alto)
    {
        using var salida = new InMemoryRandomAccessStream();
        await pagina.RenderToStreamAsync(salida, new PdfPageRenderOptions
        {
            DestinationWidth = ancho,
            DestinationHeight = alto,
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = BitmapEncoder.BmpEncoderId,
        });
        salida.Seek(0);
        var decodificador = await BitmapDecoder.CreateAsync(salida);
        var datos = await decodificador.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (datos.DetachPixelData(), decodificador.PixelWidth, decodificador.PixelHeight);
    }
}
