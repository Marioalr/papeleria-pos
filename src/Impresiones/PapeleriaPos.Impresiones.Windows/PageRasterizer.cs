// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.Runtime.InteropServices;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>
/// Dibuja el EMF de una página con GDI en memoria y estima la cobertura CMYK:
/// el componente gris de cada píxel va a K (GCR total) y el resto a C/M/Y.
/// </summary>
public static class PageRasterizer
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct ENHMETAHEADER
    {
        public uint iType, nSize;
        public RECT rclBounds, rclFrame;
        public uint dSignature, nVersion, nBytes, nRecords;
        public ushort nHandles, sReserved;
        public uint nDescription, offDescription, nPalEntries;
        public SIZE szlDevice, szlMillimeters;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern IntPtr SetEnhMetaFileBits(uint cb, byte[] data);
    [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(IntPtr h);
    [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileHeader(IntPtr h, uint cb, out ENHMETAHEADER hdr);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool PlayEnhMetaFile(IntPtr hdc, IntPtr hemf, ref RECT rect);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")]
    static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, int rop);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    const int HALFTONE = 4, SRCCOPY = 0x00CC0020;

    /// <summary>Rasteriza la página a <paramref name="dpi"/> y mide la cobertura. Opcionalmente genera una miniatura PNG.</summary>
    public static unsafe PageCoverage Measure(byte[] emf, double dpi, int thumbnailMaxSide, out byte[]? thumbnailPng,
        bool grayscaleThumbnail = false)
    {
        thumbnailPng = null;
        var hemf = SetEnhMetaFileBits((uint)emf.Length, emf);
        if (hemf == IntPtr.Zero) throw new InvalidDataException("EMF de página inválido.");
        var dc = IntPtr.Zero;
        var bmp = IntPtr.Zero;
        try
        {
            GetEnhMetaFileHeader(hemf, (uint)Marshal.SizeOf<ENHMETAHEADER>(), out var h);
            // La superficie del dispositivo de referencia es el área imprimible de la impresora.
            double wIn = h.szlMillimeters.Cx / 25.4, hIn = h.szlMillimeters.Cy / 25.4;
            if (wIn <= 0 || hIn <= 0) throw new InvalidDataException("EMF sin dispositivo de referencia.");
            int w = Math.Max(1, (int)Math.Round(wIn * dpi)), ht = Math.Max(1, (int)Math.Round(hIn * dpi));

            dc = CreateCompatibleDC(IntPtr.Zero);
            bmp = CreateDib(dc, w, ht, out var bits);
            SelectObject(dc, bmp);
            SetStretchBltMode(dc, HALFTONE);
            SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
            new Span<uint>((void*)bits, w * ht).Fill(0x00FFFFFF);

            // rclFrame está en 0.01 mm relativo al origen del dispositivo de referencia.
            double k = dpi / 2540.0;
            var rect = new RECT
            {
                Left = (int)Math.Round(h.rclFrame.Left * k), Top = (int)Math.Round(h.rclFrame.Top * k),
                Right = (int)Math.Round(h.rclFrame.Right * k), Bottom = (int)Math.Round(h.rclFrame.Bottom * k)
            };
            PlayEnhMetaFile(dc, hemf, ref rect);

            var coverage = Analyze(new ReadOnlySpan<uint>((void*)bits, w * ht), wIn * hIn);
            if (thumbnailMaxSide > 0) thumbnailPng = Thumbnail(dc, w, ht, thumbnailMaxSide, grayscaleThumbnail);
            return coverage;
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            DeleteEnhMetaFile(hemf);
        }
    }

    /// <summary>Cobertura de una imagen ya dibujada (píxeles BGRA/BGRX, fondo blanco), p. ej. una página de PDF.</summary>
    public static PageCoverage Analyze(ReadOnlySpan<uint> pixels, double areaSqIn)
    {
        double sc = 0, sm = 0, sy = 0, sk = 0, sg = 0;
        bool color = false;
        foreach (uint v in pixels)
        {
            if ((v & 0x00FFFFFF) == 0x00FFFFFF) continue;
            int b = (int)(v & 0xFF), g = (int)((v >> 8) & 0xFF), r = (int)((v >> 16) & 0xFF);
            int max = Math.Max(r, Math.Max(g, b));
            sg += 1 - (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            sk += 1 - max / 255.0;
            if (max == 0) continue;
            if (r != g || g != b) color = true;
            double scale = 1.0 / 255.0; // (max-x)/max * (1-k) == (max-x)/255
            sc += (max - r) * scale;
            sm += (max - g) * scale;
            sy += (max - b) * scale;
        }
        double n = pixels.Length;
        return new PageCoverage(sc / n, sm / n, sy / n, sk / n, sg / n, areaSqIn, color);
    }

    static unsafe IntPtr CreateDib(IntPtr dc, int w, int h, out IntPtr bits)
    {
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection");
        return bmp;
    }

    static unsafe byte[] Thumbnail(IntPtr srcDc, int w, int h, int maxSide, bool grayscale)
    {
        double s = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
        int tw = Math.Max(1, (int)(w * s)), th = Math.Max(1, (int)(h * s));
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bmp = CreateDib(dc, tw, th, out var bits);
        try
        {
            SelectObject(dc, bmp);
            SetStretchBltMode(dc, HALFTONE);
            SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
            StretchBlt(dc, 0, 0, tw, th, srcDc, 0, 0, w, h, SRCCOPY);
            var pixels = new Span<uint>((void*)bits, tw * th);
            if (grayscale)
            {
                // Trabajo en B/N: la miniatura muestra lo que realmente sale de la impresora.
                foreach (ref uint v in pixels)
                {
                    uint b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                    uint y = (uint)(0.299 * r + 0.587 * g + 0.114 * b + 0.5);
                    v = (y << 16) | (y << 8) | y;
                }
            }
            return PngWriter.EncodeBgr32(pixels, tw, th);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }
}
