// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

using System.Net.Http;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Impresiones.Medicion;

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

    /// <summary>
    /// El código de error del ST2 de Epson (campo 0x02; vacío si no lo dio o no hay error). 0x06 es «sin papel» según el protocolo de Epson
    /// que documenta gutenprint (escputil); en la tienda todavía no se ha visto (agregado en el POS, 2026-11-28).
    /// </summary>
    public int? EpsonError { get; init; }

    /// <summary>Las bandejas del Printer-MIB (prtInputTable) que contestaron (agregado en el POS, 2026-11-28).</summary>
    public List<InputTray> Inputs { get; init; } = [];

    /// <summary>
    /// Si la impresora dijo que se quedó sin papel: su ST2 trae el error 0x06, o una bandeja del Printer-MIB dice 0 hojas (con lo que le cabe). Vacío si no se
    /// sabe (no lo dijo de ninguna forma).
    /// </summary>
    public bool? PaperOut => EpsonError == EpsonSt2.ErrorSinPapel || Inputs.Any(i => i.CurrentLevel == 0 && i.MaxCapacity > 0) ? true
        : EpsonError is not null || Inputs.Count > 0 ? false : null;
}

/// <summary>
/// Una bandeja del Printer-MIB: prtInputCurrentLevel y prtInputMaxCapacity (RFC 3805: -1 «otro», -2 «no se sabe», -3 «hay al menos una»).
/// Solo dice cuántas hojas le quedan si los dos son números de verdad (<see cref="Reports"/>); las de inyección de Epson, por lo que se
/// sabe, a lo más dicen si hay o no hay (2026-11-28, docs/impresion.md, «Las hojas en la charola»).
/// </summary>
public sealed record InputTray(int Index, int? CurrentLevel, int? MaxCapacity)
{
    public bool Reports => CurrentLevel is >= 0 && MaxCapacity is > 0;
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
    // Printer-MIB, prtInputTable (agregado en el POS, 2026-11-28): lo que queda en cada bandeja y lo que le cabe.
    const string InputMaxOid = "1.3.6.1.2.1.43.8.2.1.9.1.";
    const string InputLevelOid = "1.3.6.1.2.1.43.8.2.1.10.1.";

    public string Host { get; } = host;

    /// <summary>
    /// Lo que conteste: una parte que falle (o que tarde, como la página con la impresora ocupada) queda en <see cref="PrinterStatus.Errors"/>
    /// y lo demás se conserva. Solo se cancela con <paramref name="ct"/>.
    /// </summary>
    public async Task<PrinterStatus> ReadAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        int? k = null, c = null, m = null, y = null, maintenance = null;
        long? life = null, color = null, mono = null;
        string? serial = null;
        int? epsonError = null;
        var inputs = new List<InputTray>();

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
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"SNMP: {ex.Message}"); }

        try
        {
            // Las bandejas: la WF-6590 tiene dos (más las opcionales), la L5590 una; la que no existe contesta con error y se termina.
            for (int i = 1; i <= 3; i++)
            {
                var level = await SnmpClient.GetAsync(Host, InputLevelOid + i, ct: ct);
                if (level.IsError) break;
                var max = await SnmpClient.GetAsync(Host, InputMaxOid + i, ct: ct);
                inputs.Add(new InputTray(i, (int)level.AsInteger(), max.IsError ? null : (int)max.AsInteger()));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"SNMP bandejas: {ex.Message}"); }

        try
        {
            var st = await SnmpClient.GetAsync(Host, EpsonStatusOid, ct: ct);
            if (!st.IsError && EpsonSt2.TryParse(st.Data, out var parsed))
            {
                serial = parsed.Serial;
                color = parsed.ColorPages;
                mono = parsed.MonoPages;
                epsonError = parsed.ErrorCode;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"ST2: {ex.Message}"); }

        try { maintenance = await ReadMaintenanceFromWebAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"Web: {ex.Message}"); }

        return new PrinterStatus
        {
            InkK = k, InkC = c, InkM = m, InkY = y, Maintenance = maintenance,
            LifeCount = life, ColorCount = color, MonoCount = mono, Serial = serial, Errors = errors, EpsonError = epsonError, Inputs = inputs,
        };
    }

    /// <summary>
    /// Solo el contador (Printer-MIB) y el ST2 de Epson (color, B/N y serie), sin los niveles ni la página web: para leerlo seguido mientras
    /// la impresora se mueve (las tarjetas «Copias o celular» del POS, 2026-11-08). Agregado en el POS; Control Print no lo tenía.
    /// </summary>
    public async Task<PrinterStatus> ReadCounterAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        long? life = null, color = null, mono = null;
        string? serial = null;
        try
        {
            var lc = await SnmpClient.GetAsync(Host, LifeCountOid, ct: ct);
            if (!lc.IsError) life = lc.AsInteger();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"SNMP: {ex.Message}"); }
        try
        {
            var st = await SnmpClient.GetAsync(Host, EpsonStatusOid, ct: ct);
            if (!st.IsError && EpsonSt2.TryParse(st.Data, out var parsed))
                (serial, color, mono) = (parsed.Serial, parsed.ColorPages, parsed.MonoPages);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"ST2: {ex.Message}"); }
        return new PrinterStatus { LifeCount = life, ColorCount = color, MonoCount = mono, Serial = serial, Errors = errors };
    }

    async Task<int?> ReadMaintenanceFromWebAsync(CancellationToken ct) => ParseMaintenance(await StatusPageAsync(Host, ct));

    /// <summary>
    /// La página de estado de una impresora Epson (solo un GET; también la usa <see cref="DiagnosticoImpresora"/>). La impresora usa un
    /// certificado autofirmado: se acepta solo para ese host.
    /// </summary>
    public static async Task<string> StatusPageAsync(string host, CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
                string.Equals(req.RequestUri?.Host, host, StringComparison.OrdinalIgnoreCase),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        return await http.GetStringAsync($"https://{host}/PRESENTATION/HTML/TOP/PRTINFO.HTML", ct);
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
    /// <summary>El error «sin papel» del campo 0x02 (gutenprint, escputil; agregado en el POS, 2026-11-28; por verificar en la tienda).</summary>
    public const int ErrorSinPapel = 0x06;

    /// <summary>
    /// El código de error (campo 0x02, un byte) mientras la impresora está en error (campo 0x01, el estado, en 0x00); vacío si no hay error o
    /// no lo dio.
    /// </summary>
    public int? ErrorCode => Fields.TryGetValue(0x02, out var e) && e.Length >= 1 && (!Fields.TryGetValue(0x01, out var st) || st.Length == 0 || st[0] == 0x00)
        ? e[0] : null;

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
