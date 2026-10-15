// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.Buffers.Binary;
using System.Text;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>
/// Lector secuencial del formato de spool "NT EMF 1.008" ([MS-EMFSPOOL]).
/// Entrega el EMF de cada página conforme lo lee, sin cargar el trabajo completo en memoria.
/// </summary>
public sealed class EmfSpoolReader
{
    const uint EMRI_METAFILE = 0x01, EMRI_DEVMODE = 0x03, EMRI_BW_METAFILE = 0x0A, EMRI_METAFILE_DATA = 0x0C;
    const int MaxRecordSize = 512 * 1024 * 1024;

    readonly Stream stream;
    long position;

    public string DocumentName { get; private set; } = "";

    /// <summary>DEVMODE más reciente embebido en el spool (puede cambiar entre páginas).</summary>
    public byte[]? LastDevMode { get; private set; }

    public EmfSpoolReader(Stream stream)
    {
        this.stream = stream;
        ReadHeader();
    }

    public static bool LooksLikeEmfSpool(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == 0x00010000;

    void ReadHeader()
    {
        Span<byte> h = stackalloc byte[16];
        ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x00010000)
            throw new InvalidDataException("El trabajo no es un spool EMF (versión esperada 0x00010000).");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
        int docOffset = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        if (headerSize < 16 || headerSize > 64 * 1024) throw new InvalidDataException("Cabecera EMF spool inválida.");

        var rest = new byte[headerSize - 16];
        ReadExactly(rest);
        if (docOffset >= 16 && docOffset < headerSize)
        {
            int start = docOffset - 16, end = start;
            while (end + 1 < rest.Length && (rest[end] != 0 || rest[end + 1] != 0)) end += 2;
            DocumentName = Encoding.Unicode.GetString(rest, start, end - start);
        }
        SkipToAlignment();
    }

    /// <summary>Devuelve el EMF de la siguiente página, o null al terminar el spool.</summary>
    public byte[]? NextPage()
    {
        Span<byte> rec = stackalloc byte[8];
        while (true)
        {
            if (!TryReadExactly(rec)) return null;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(rec);
            int size = BinaryPrimitives.ReadInt32LittleEndian(rec[4..]);
            if (size < 0 || size > MaxRecordSize) throw new InvalidDataException($"Registro EMF spool con tamaño inválido ({size}).");

            switch (id)
            {
                case EMRI_METAFILE or EMRI_BW_METAFILE or EMRI_METAFILE_DATA:
                {
                    var page = new byte[size];
                    ReadExactly(page);
                    SkipToAlignment();
                    return page;
                }
                case EMRI_DEVMODE:
                {
                    var dm = new byte[size];
                    ReadExactly(dm);
                    LastDevMode = dm;
                    break;
                }
                default:
                    Skip(size);
                    break;
            }
            SkipToAlignment();
        }
    }

    void SkipToAlignment()
    {
        int pad = (int)((4 - position % 4) % 4);
        if (pad > 0) Skip(pad, allowEof: true);
    }

    void Skip(long count, bool allowEof = false)
    {
        var buf = new byte[(int)Math.Min(count, 81920)];
        while (count > 0)
        {
            int n = stream.Read(buf, 0, (int)Math.Min(count, buf.Length));
            if (n == 0)
            {
                if (allowEof) return;
                throw new EndOfStreamException();
            }
            count -= n;
            position += n;
        }
    }

    void ReadExactly(Span<byte> target)
    {
        if (!TryReadExactly(target)) throw new EndOfStreamException("Spool EMF truncado.");
    }

    bool TryReadExactly(Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int n = stream.Read(target[total..]);
            if (n == 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException("Spool EMF truncado.");
            }
            total += n;
        }
        position += total;
        return true;
    }
}
