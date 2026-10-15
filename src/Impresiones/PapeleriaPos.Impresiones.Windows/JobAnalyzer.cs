// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.Diagnostics;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>Analiza los datos de spool de un trabajo y produce la cobertura por página.</summary>
public static class JobAnalyzer
{
    /// <param name="spool">Datos del trabajo (spool EMF o RAW).</param>
    /// <param name="settings">Configuración del trabajo.</param>
    /// <param name="reportedPages">Páginas que reporta el spooler (para la estimación de nivel 1).</param>
    /// <param name="readDevMode">
    /// Convierte un DEVMODE incrustado en el spool en configuración. Programas como Word no ponen las copias en el
    /// DEVMODE inicial del trabajo sino en uno incrustado (ResetDC), que es el que obedece la impresora.
    /// </param>
    public static JobAnalysis Analyze(Stream spool, JobSettings settings, int reportedPages, AnalysisOptions options,
        Func<byte[], JobSettings>? readDevMode = null)
    {
        var sw = Stopwatch.StartNew();
        var buffered = new BufferedStream(spool, 256 * 1024);
        var head = new byte[4];
        int got = buffered.Read(head, 0, 4);
        if (got < 4 || !EmfSpoolReader.LooksLikeEmfSpool(head))
            return Estimate(settings, reportedPages, options, "Formato no EMF: estimación por páginas.", sw.Elapsed);

        var reader = new EmfSpoolReader(new PrefixedStream(head, buffered));
        byte[]? appliedDevMode = null;
        void UseEmbeddedDevMode()
        {
            // Hay programas que repiten el mismo DEVMODE en cada página: solo se convierte cuando cambia.
            if (readDevMode is null || reader.LastDevMode is not { } dm || ReferenceEquals(dm, appliedDevMode)
                || (appliedDevMode is not null && dm.AsSpan().SequenceEqual(appliedDevMode))) return;
            appliedDevMode = dm;
            try { settings = readDevMode(dm); }
            catch (Exception) { /* se conserva la configuración del trabajo */ }
        }
        var pages = new List<PageCoverage>();
        byte[]? thumbnail = null;
        int thumbnailPage = 0;
        bool thumbnailBlank = false;
        string? note = null;
        while (reader.NextPage() is { } emf)
        {
            UseEmbeddedDevMode();
            if (pages.Count >= options.MaxMeasuredPages)
            {
                pages.Add(Average(pages));
                note = $"Se midieron {options.MaxMeasuredPages} páginas; el resto usa el promedio.";
                continue;
            }
            try
            {
                // Miniatura: la primera página con contenido (se buscan hasta 10 páginas).
                bool wantThumb = thumbnail is null || (thumbnailBlank && pages.Count < 10);
                var page = PageRasterizer.Measure(emf, options.Dpi, wantThumb ? options.ThumbnailMaxSide : 0, out var thumb,
                    grayscaleThumbnail: !settings.Color);
                pages.Add(page);
                if (wantThumb && thumb is not null && (thumbnail is null || !page.IsBlank))
                {
                    thumbnail = thumb;
                    thumbnailPage = pages.Count;
                    thumbnailBlank = page.IsBlank;
                }
            }
            catch (Exception ex)
            {
                pages.Add(PageCoverage.Estimated(AreaSqIn(settings), settings.Color, options.FallbackCoverage));
                note = $"Página {pages.Count} estimada: {ex.Message}";
            }
        }

        // El DEVMODE incrustado puede venir después de la última página (Word lo escribe tras los datos).
        UseEmbeddedDevMode();
        if (pages.Count == 0)
            return Estimate(settings, reportedPages, options, "El spool no contenía páginas.", sw.Elapsed);
        return new JobAnalysis(settings, pages, AnalysisLevel.Measured, thumbnail, note, sw.Elapsed, thumbnailPage);
    }

    public static JobAnalysis Estimate(JobSettings settings, int reportedPages, AnalysisOptions options, string note,
        TimeSpan elapsed = default)
    {
        int n = Math.Max(1, reportedPages);
        var page = PageCoverage.Estimated(AreaSqIn(settings), settings.Color, options.FallbackCoverage);
        return new JobAnalysis(settings, Enumerable.Repeat(page, n).ToList(), AnalysisLevel.Estimated, null, note, elapsed);
    }

    /// <summary>Área imprimible aproximada (margen de 3 mm por lado, como reporta el driver).</summary>
    static double AreaSqIn(JobSettings s) =>
        Math.Max(0, s.MediaWidthMm - 6) * Math.Max(0, s.MediaHeightMm - 6) / (25.4 * 25.4);

    static PageCoverage Average(List<PageCoverage> p) => new(
        p.Average(x => x.C), p.Average(x => x.M), p.Average(x => x.Y), p.Average(x => x.K),
        p.Average(x => x.Gray), p.Average(x => x.AreaSqIn), p.Any(x => x.HasColor));

    /// <summary>Reinyecta bytes ya leídos al inicio de un flujo no posicionable.</summary>
    sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        int offset;

        public override int Read(byte[] buffer, int off, int count) => Read(buffer.AsSpan(off, count));

        public override int Read(Span<byte> buffer)
        {
            if (offset < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsSpan(offset, n).CopyTo(buffer);
                offset += n;
                return n;
            }
            return inner.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int o, int count) => throw new NotSupportedException();
    }
}
