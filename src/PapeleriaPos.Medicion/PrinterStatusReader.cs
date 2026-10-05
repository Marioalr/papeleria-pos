// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

using System.Net.Http;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Medicion;

/// <summary>Estado de consumibles de la impresora. Los niveles son porcentaje restante (0..100); null = desconocido.</summary>
public sealed record PrinterStatus
{
    public DateTime TakenUtc { get; init; } = DateTime.UtcNow;
    public int? InkK { get; init; }
    public int? InkC { get; init; }
    public int? InkM { get; init; }
    public int? InkY { get; init; }
    /// <summary>Vida restante de la caja de mantenimiento (%).</summary>
    public int? Maintenance { get; init; }
    public long? LifeCount { get; init; }
    public long? ColorCount { get; init; }
    public long? MonoCount { get; init; }
    public string? Serial { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// Lee el estado de la WF-6590 por red: SNMP Printer-MIB (tinta, contador), comando Epson "st" (ST2) y la
/// página de estado web (caja de mantenimiento, que no expone SNMP).
/// </summary>
public sealed partial class PrinterStatusReader(string host)
{
    const string LevelOid = "1.3.6.1.2.1.43.11.1.1.9.1.";
    const string DescOid = "1.3.6.1.2.1.43.11.1.1.6.1.";
    const string LifeCountOid = "1.3.6.1.2.1.43.10.2.1.4.1.1";
    // "st" + [1, 0, 1] sobre el OID de comandos Epson → respuesta "@BDC ST2".
    const string EpsonStatusOid = "1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.115.116.1.0.1";

    public string Host { get; } = host;

    public async Task<PrinterStatus> ReadAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        int? k = null, c = null, m = null, y = null, maintenance = null;
        long? life = null, color = null, mono = null;
        string? serial = null;

        try
        {
            for (int i = 1; i <= 4; i++)
            {
                var desc = (await SnmpClient.GetAsync(Host, DescOid + i, ct: ct)).AsString();
                var level = await SnmpClient.GetAsync(Host, LevelOid + i, ct: ct);
                if (level.IsError) continue;
                int v = (int)level.AsInteger();
                if (v < 0) continue; // -2 desconocido, -3 "hay algo"
                if (desc.StartsWith("Black", StringComparison.OrdinalIgnoreCase)) k = v;
                else if (desc.StartsWith("Cyan", StringComparison.OrdinalIgnoreCase)) c = v;
                else if (desc.StartsWith("Magenta", StringComparison.OrdinalIgnoreCase)) m = v;
                else if (desc.StartsWith("Yellow", StringComparison.OrdinalIgnoreCase)) y = v;
            }
            var lc = await SnmpClient.GetAsync(Host, LifeCountOid, ct: ct);
            if (!lc.IsError) life = lc.AsInteger();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"SNMP: {ex.Message}"); }

        try
        {
            var st = await SnmpClient.GetAsync(Host, EpsonStatusOid, ct: ct);
            if (!st.IsError && EpsonSt2.TryParse(st.Data, out var parsed))
            {
                serial = parsed.Serial;
                color = parsed.ColorPages;
                mono = parsed.MonoPages;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"ST2: {ex.Message}"); }

        try { maintenance = await ReadMaintenanceFromWebAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"Web: {ex.Message}"); }

        return new PrinterStatus
        {
            InkK = k, InkC = c, InkM = m, InkY = y, Maintenance = maintenance,
            LifeCount = life, ColorCount = color, MonoCount = mono, Serial = serial, Errors = errors,
        };
    }

    async Task<int?> ReadMaintenanceFromWebAsync(CancellationToken ct)
    {
        // La impresora usa un certificado autofirmado: se acepta solo para este host.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
                string.Equals(req.RequestUri?.Host, Host, StringComparison.OrdinalIgnoreCase),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var html = await http.GetStringAsync($"https://{Host}/PRESENTATION/HTML/TOP/PRTINFO.HTML", ct);
        return ParseMaintenance(html);
    }

    /// <summary>La barra de la caja (Ink_Waste.PNG) mide 50 px al 100 %.</summary>
    internal static int? ParseMaintenance(string html)
    {
        var match = WasteBar().Match(html);
        if (!match.Success) return null;
        return Math.Clamp((int)Math.Round(int.Parse(match.Groups[1].Value) * 2.0), 0, 100);
    }

    [GeneratedRegex(@"Ink_Waste\.PNG['""]\s+height=['""]?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WasteBar();
}

/// <summary>Respuesta binaria "@BDC ST2" de impresoras Epson: campos [código][longitud][datos].</summary>
public sealed record EpsonSt2(string? Serial, long? ColorPages, long? MonoPages, IReadOnlyDictionary<byte, byte[]> Fields)
{
    public static bool TryParse(ReadOnlySpan<byte> data, out EpsonSt2 result)
    {
        result = new EpsonSt2(null, null, null, new Dictionary<byte, byte[]>());
        int start = data.IndexOf("ST2\r\n"u8);
        if (start < 0 || start + 7 > data.Length) return false;
        int p = start + 5;
        int length = data[p] | data[p + 1] << 8;
        p += 2;
        int end = Math.Min(data.Length, p + length);
        var fields = new Dictionary<byte, byte[]>();
        while (p + 2 <= end)
        {
            byte code = data[p], len = data[p + 1];
            if (p + 2 + len > end) break;
            fields[code] = data.Slice(p + 2, len).ToArray();
            p += 2 + len;
        }

        string? serial = fields.TryGetValue(0x40, out var s) ? System.Text.Encoding.ASCII.GetString(s) : null;
        long? a = null, b = null;
        // Campo 0x36: 8 bytes FF y luego dos contadores de 32 bits: páginas a color y en B/N
        // (verificado con una impresión B/N real el 2026-09-26: solo subió el segundo).
        if (fields.TryGetValue(0x36, out var f36) && f36.Length >= 16)
        {
            a = BitConverter.ToUInt32(f36, 8);
            b = BitConverter.ToUInt32(f36, 12);
        }
        result = new EpsonSt2(serial, a, b, fields);
        return true;
    }
}
