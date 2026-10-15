// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>Cliente SNMP v1 mínimo (GET) sin dependencias.</summary>
public static class SnmpClient
{
    public sealed record Value(byte Type, byte[] Data)
    {
        public bool IsError => Type is 0x80 or 0x81 or 0x82 or 0x05;

        public long AsInteger()
        {
            long v = Data.Length > 0 && (Data[0] & 0x80) != 0 && Type == 0x02 ? -1 : 0;
            foreach (var b in Data) v = (v << 8) | b;
            return v;
        }

        public string AsString() => Encoding.ASCII.GetString(Data);
    }

    public static async Task<Value> GetAsync(string host, string oid, string community = "public",
        int timeoutMs = 2000, CancellationToken ct = default)
    {
        var request = BuildGet(community, oid, Random.Shared.Next(1, int.MaxValue));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var address = IPAddress.TryParse(host, out var ip) ? ip
            : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct))[0];
        await udp.SendAsync(request, new IPEndPoint(address, 161), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var result = await udp.ReceiveAsync(timeout.Token);
            return ParseResponse(result.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"SNMP sin respuesta de {host}");
        }
    }

    internal static byte[] BuildGet(string community, string oid, int requestId)
    {
        var varbind = Tlv(0x30, [.. Tlv(0x06, EncodeOid(oid)), .. Tlv(0x05, [])]);
        var pdu = Tlv(0xA0, [.. Int(requestId), .. Int(0), .. Int(0), .. Tlv(0x30, varbind)]);
        return Tlv(0x30, [.. Int(0), .. Tlv(0x04, Encoding.ASCII.GetBytes(community)), .. pdu]);
    }

    static byte[] Int(int v)
    {
        var bytes = BitConverter.GetBytes(v).Reverse().SkipWhile((b, i) => b == 0 && i < 3).ToArray();
        if ((bytes[0] & 0x80) != 0) bytes = [0, .. bytes];
        return Tlv(0x02, bytes);
    }

    static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] len = content.Length < 128 ? [(byte)content.Length]
            : content.Length < 256 ? [0x81, (byte)content.Length]
            : [0x82, (byte)(content.Length >> 8), (byte)content.Length];
        return [tag, .. len, .. content];
    }

    static byte[] EncodeOid(string oid)
    {
        var parts = oid.Trim('.').Split('.').Select(uint.Parse).ToArray();
        var list = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (var arc in parts.Skip(2))
        {
            var stack = new Stack<byte>();
            uint v = arc;
            stack.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) stack.Push((byte)(0x80 | (v & 0x7F)));
            list.AddRange(stack);
        }
        return [.. list];
    }

    internal static Value ParseResponse(byte[] r)
    {
        int p = 0;
        Enter(r, ref p, 0x30);
        Skip(r, ref p);                 // versión
        Skip(r, ref p);                 // comunidad
        Enter(r, ref p, 0xA2);          // GetResponse
        Skip(r, ref p);                 // request id
        var (_, err) = Read(r, ref p);  // error-status
        Skip(r, ref p);                 // error-index
        if (err.Length > 0 && err[^1] != 0) return new Value(0x81, []);
        Enter(r, ref p, 0x30);
        Enter(r, ref p, 0x30);
        Skip(r, ref p);                 // OID
        var (tag, data) = Read(r, ref p);
        return new Value(tag, data);
    }

    static int Length(byte[] r, ref int p)
    {
        int b = r[p++];
        if (b < 0x80) return b;
        int n = b & 0x7F, len = 0;
        for (int i = 0; i < n; i++) len = (len << 8) | r[p++];
        return len;
    }

    static void Enter(byte[] r, ref int p, byte tag)
    {
        if (r[p] != tag) throw new InvalidDataException($"SNMP: se esperaba 0x{tag:X2}, llegó 0x{r[p]:X2}");
        p++;
        Length(r, ref p);
    }

    static void Skip(byte[] r, ref int p) { p++; int len = Length(r, ref p); p += len; }

    static (byte tag, byte[] data) Read(byte[] r, ref int p)
    {
        byte tag = r[p++];
        int len = Length(r, ref p);
        var data = r[p..(p + len)];
        p += len;
        return (tag, data);
    }
}
