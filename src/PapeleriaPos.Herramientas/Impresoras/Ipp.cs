using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>
/// Lo poco de IPP (RFC 8010/8011) que necesita el modo «antes y después»: armar Get-Printer-Attributes y Get-Jobs (las dos solo
/// preguntan) y leer la respuesta. Los nombres de los documentos y de quien imprime se enmascaran al leerlos (<see cref="Enmascarar"/>):
/// así nunca llegan a los archivos.
/// </summary>
public static class Ipp
{
    public const short GetPrinterAttributes = 0x000B;
    public const short GetJobs = 0x000A;

    /// <summary>Lo que se pide de cada trabajo en Get-Jobs.</summary>
    public static readonly IReadOnlyList<string> AtributosDeTrabajo =
    [
        "job-id", "job-uuid", "job-name", "job-state", "job-state-reasons", "job-originating-user-name", "job-originating-host-name",
        "document-name-supplied", "document-format", "document-format-supplied", "job-impressions", "job-impressions-completed",
        "job-media-sheets", "job-media-sheets-completed", "job-pages-completed", "copies", "sides", "print-color-mode", "media",
        "time-at-creation", "time-at-processing", "time-at-completed", "date-time-at-creation", "date-time-at-completed",
        "job-printer-up-time", "output-device-assigned",
    ];

    /// <summary>Los que pueden traer el nombre de un documento o de una persona: se enmascaran.</summary>
    public static readonly IReadOnlySet<string> Privados = new HashSet<string>(StringComparer.Ordinal)
    {
        "job-name", "document-name-supplied", "job-originating-user-name", "job-originating-host-name", "requesting-user-name",
    };

    public sealed record Atributo(string Nombre, IReadOnlyList<string> Valores)
    {
        public string Texto => string.Join(", ", Valores);
    }

    /// <param name="Etiqueta">1 operación, 2 trabajo, 4 impresora, 5 no soportados.</param>
    public sealed record Grupo(byte Etiqueta, IReadOnlyList<Atributo> Atributos)
    {
        public string? this[string nombre] => Atributos.FirstOrDefault(a => a.Nombre == nombre)?.Texto;
    }

    public sealed record Respuesta(int Estado, IReadOnlyList<Grupo> Grupos)
    {
        public bool Bien => Estado < 0x0100;
        public string EstadoTexto => Estado switch
        {
            0x0000 => "successful-ok",
            0x0001 => "successful-ok-ignored-or-substituted-attributes",
            0x0400 => "client-error-bad-request",
            0x0401 => "client-error-forbidden",
            0x0402 => "client-error-not-authenticated",
            0x0406 => "client-error-not-found",
            0x040B => "client-error-attributes-or-values-not-supported",
            0x0501 => "server-error-operation-not-supported",
            0x0503 => "server-error-version-not-supported",
            _ => $"0x{Estado:X4}",
        };

        public IEnumerable<Grupo> Trabajos => Grupos.Where(g => g.Etiqueta == 0x02);
        public Grupo? Impresora => Grupos.FirstOrDefault(g => g.Etiqueta == 0x04);
    }

    /// <summary>Get-Printer-Attributes con todos sus atributos.</summary>
    public static byte[] PedirAtributos(string uri, int id) =>
        Armar(GetPrinterAttributes, id, uri, [(0x44, "requested-attributes", ["all"])]);

    /// <summary>Get-Jobs de todos (my-jobs=false), los terminados (<c>completed</c>) o los que faltan (<c>not-completed</c>).</summary>
    public static byte[] PedirTrabajos(string uri, int id, string cuales) =>
        Armar(GetJobs, id, uri, [(0x44, "which-jobs", [cuales]), (0x22, "my-jobs", ["false"]), (0x44, "requested-attributes", AtributosDeTrabajo)]);

    private static byte[] Armar(short operacion, int id, string uri, IEnumerable<(byte Etiqueta, string Nombre, IReadOnlyList<string> Valores)> mas)
    {
        if (operacion is not (GetPrinterAttributes or GetJobs))
            throw new ArgumentOutOfRangeException(nameof(operacion), "Solo se pregunta: Get-Printer-Attributes o Get-Jobs.");
        var m = new MemoryStream();
        m.Write([0x01, 0x01]); // IPP/1.1: lo entiende cualquiera
        Escribir16(m, (ushort)operacion);
        Escribir32(m, id);
        m.WriteByte(0x01);
        EscribirAtributo(m, 0x47, "attributes-charset", "utf-8");
        EscribirAtributo(m, 0x48, "attributes-natural-language", "en");
        EscribirAtributo(m, 0x45, "printer-uri", uri);
        EscribirAtributo(m, 0x42, "requesting-user-name", "diagnostico");
        foreach (var (etiqueta, nombre, valores) in mas)
        {
            for (var i = 0; i < valores.Count; i++)
            {
                if (etiqueta == 0x22)
                {
                    m.WriteByte(etiqueta);
                    Texto16(m, i == 0 ? nombre : "");
                    Escribir16(m, 1);
                    m.WriteByte(valores[i] == "true" ? (byte)1 : (byte)0);
                }
                else
                {
                    EscribirAtributo(m, etiqueta, i == 0 ? nombre : "", valores[i]);
                }
            }
        }
        m.WriteByte(0x03);
        return m.ToArray();
    }

    private static void EscribirAtributo(Stream m, byte etiqueta, string nombre, string valor)
    {
        m.WriteByte(etiqueta);
        Texto16(m, nombre);
        Texto16(m, valor);
    }

    private static void Texto16(Stream m, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Escribir16(m, (ushort)bytes.Length);
        m.Write(bytes);
    }

    private static void Escribir16(Stream m, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        m.Write(b);
    }

    private static void Escribir32(Stream m, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        m.Write(b);
    }

    /// <summary>Lee la operación de una pregunta (para que las pruebas revisen que solo se pregunta).</summary>
    public static short Operacion(ReadOnlySpan<byte> pregunta) => BinaryPrimitives.ReadInt16BigEndian(pregunta[2..]);

    /// <summary>Lee una respuesta IPP. Los atributos de <see cref="Privados"/> llegan ya enmascarados.</summary>
    public static Respuesta Leer(byte[] datos)
    {
        if (datos.Length < 9)
            throw new InvalidDataException("IPP: respuesta demasiado corta");
        var estado = BinaryPrimitives.ReadUInt16BigEndian(datos.AsSpan(2));
        var p = 8;
        var grupos = new List<Grupo>();
        List<Atributo>? actual = null;
        byte etiquetaGrupo = 0;
        string? nombre = null;
        List<string>? valores = null;

        void Cerrar()
        {
            if (nombre is not null && valores is not null && actual is not null)
                actual.Add(new Atributo(nombre, valores));
            nombre = null;
            valores = null;
        }

        while (p < datos.Length)
        {
            var etiqueta = datos[p++];
            if (etiqueta == 0x03)
                break;
            if (etiqueta < 0x10)
            {
                Cerrar();
                if (actual is not null)
                    grupos.Add(new Grupo(etiquetaGrupo, actual));
                actual = [];
                etiquetaGrupo = etiqueta;
                continue;
            }
            var (n, v) = Par(datos, ref p);
            if (n.Length > 0)
            {
                Cerrar();
                nombre = n;
                valores = [];
            }
            var texto = etiqueta == 0x34 ? Coleccion(datos, ref p) : Valor(etiqueta, v, nombre);
            if (nombre is not null && Privados.Contains(nombre))
                texto = Enmascarar(texto);
            valores?.Add(texto);
        }
        Cerrar();
        if (actual is not null)
            grupos.Add(new Grupo(etiquetaGrupo, actual));
        return new Respuesta(estado, grupos);
    }

    private static (string Nombre, byte[] Valor) Par(byte[] d, ref int p)
    {
        var largoNombre = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
        p += 2;
        var nombre = Encoding.UTF8.GetString(d, p, largoNombre);
        p += largoNombre;
        var largoValor = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
        p += 2;
        if (p + largoValor > d.Length)
            throw new InvalidDataException("IPP: respuesta cortada");
        var valor = d[p..(p + largoValor)];
        p += largoValor;
        return (nombre, valor);
    }

    /// <summary>Una colección (begCollection … endCollection) como «{miembro=valor; …}».</summary>
    private static string Coleccion(byte[] d, ref int p)
    {
        var partes = new List<string>();
        string? miembro = null;
        var valores = new List<string>();
        void Cerrar()
        {
            if (miembro is not null)
                partes.Add($"{miembro}={string.Join(",", valores)}");
            miembro = null;
            valores.Clear();
        }
        while (p < d.Length)
        {
            var etiqueta = d[p++];
            var (_, v) = Par(d, ref p);
            if (etiqueta == 0x37)
                break;
            if (etiqueta == 0x4A)
            {
                Cerrar();
                miembro = Encoding.UTF8.GetString(v);
                continue;
            }
            valores.Add(etiqueta == 0x34 ? Coleccion(d, ref p) : Valor(etiqueta, v, miembro));
        }
        Cerrar();
        return "{" + string.Join("; ", partes) + "}";
    }

    private static string Valor(byte etiqueta, byte[] v, string? nombre)
    {
        switch (etiqueta)
        {
            case 0x10: return "(unsupported)";
            case 0x12: return "(unknown)";
            case 0x13: return "(no-value)";
            case < 0x20: return $"(fuera de banda 0x{etiqueta:X2})";
            case 0x21 when v.Length == 4:
                return BinaryPrimitives.ReadInt32BigEndian(v).ToString(CultureInfo.InvariantCulture);
            case 0x23 when v.Length == 4:
                var numero = BinaryPrimitives.ReadInt32BigEndian(v);
                return Enumerado(nombre, numero) is { } que ? $"{numero} ({que})" : numero.ToString(CultureInfo.InvariantCulture);
            case 0x22 when v.Length == 1:
                return v[0] != 0 ? "true" : "false";
            case 0x31 when v.Length == 11:
                return $"{BinaryPrimitives.ReadUInt16BigEndian(v):0000}-{v[2]:00}-{v[3]:00} {v[4]:00}:{v[5]:00}:{v[6]:00} {(char)v[8]}{v[9]:00}{v[10]:00}";
            case 0x32 when v.Length == 9:
                return $"{BinaryPrimitives.ReadInt32BigEndian(v)}x{BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(4))}{(v[8] == 3 ? "dpi" : "dpcm")}";
            case 0x33 when v.Length == 8:
                return $"{BinaryPrimitives.ReadInt32BigEndian(v)}-{BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(4))}";
            case 0x35 or 0x36 when v.Length >= 4:
                var largoIdioma = BinaryPrimitives.ReadUInt16BigEndian(v);
                var inicio = 2 + largoIdioma;
                if (inicio + 2 > v.Length)
                    return Convert.ToHexString(v);
                var largoTexto = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(inicio));
                return Encoding.UTF8.GetString(v, inicio + 2, Math.Min(largoTexto, v.Length - inicio - 2));
            case 0x30:
                return v.All(b => b is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(v) : Convert.ToHexString(v);
            case >= 0x40 and < 0x60:
                return Encoding.UTF8.GetString(v);
            default:
                return Convert.ToHexString(v);
        }
    }

    private static string? Enumerado(string? nombre, int n) => (nombre, n) switch
    {
        ("job-state", 3) => "pending",
        ("job-state", 4) => "pending-held",
        ("job-state", 5) => "processing",
        ("job-state", 6) => "processing-stopped",
        ("job-state", 7) => "canceled",
        ("job-state", 8) => "aborted",
        ("job-state", 9) => "completed",
        ("printer-state", 3) => "idle",
        ("printer-state", 4) => "processing",
        ("printer-state", 5) => "stopped",
        _ => null,
    };

    /// <summary>
    /// El nombre de un documento o de una persona, enmascarado (decidido, por revisar, 2026-11-08): se quedan las 3 primeras letras, la
    /// extensión (si tiene una corta: dice si fue PDF o foto) y cuántas letras tenía. «Factura Juan.pdf» → «Fac….pdf (16)».
    /// </summary>
    public static string Enmascarar(string texto)
    {
        if (texto.Length == 0 || texto.StartsWith('('))
            return texto;
        var punto = texto.LastIndexOf('.');
        var extension = punto > 0 && texto.Length - punto is > 1 and <= 6 && texto[(punto + 1)..].All(char.IsLetterOrDigit) ? texto[punto..] : "";
        var inicio = texto[..Math.Min(3, texto.Length - extension.Length)];
        return $"{inicio}…{extension} ({texto.Length})";
    }
}
