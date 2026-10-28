using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PapeleriaPos.Impresiones.ControlPrint;

/// <summary>
/// El protocolo de red de Control Print, versión 1 (la de Control Print 1.8 a 1.12; ControlPrint.Net, Protocol.cs): HTTPS
/// con el certificado propio del servidor y el código de conexión de Ajustes → Red. El saludo (<c>api/hello</c>) es lo
/// único que contesta sin el código: devuelve una prueba HMAC del código ligada al reto del cliente y a la huella del
/// certificado. Así se sabe que el servidor es el que conoce el código antes de mandárselo, y desde ahí se fija su
/// certificado: nadie en medio puede hacerse pasar por él ni leer el código.
/// </summary>
public static class ProtocoloControlPrint
{
    /// <summary>La versión del protocolo que habla este cliente; si el servidor dice otra, no se conecta.</summary>
    public const int Version = 1;

    public const int PuertoPorOmision = 47815;

    internal const string EncabezadoCodigo = "X-CP-Key";
    internal const string EncabezadoEquipo = "X-CP-Device";
    internal const string EncabezadoVersion = "X-CP-Version";

    /// <summary>Huella SHA-256 de un certificado (sus bytes DER), en hexadecimal: la misma que calcula Control Print.</summary>
    public static string Huella(byte[] certificado) => Convert.ToHexString(SHA256.HashData(certificado));

    /// <summary>La huella como se lee: «7B:D8:39:…».</summary>
    public static string HuellaLegible(string huella) =>
        string.Join(':', Enumerable.Range(0, huella.Length / 2).Select(i => huella.Substring(i * 2, 2)));

    /// <summary>Una huella escrita con o sin «:», espacios o minúsculas; null si no son 64 cifras hexadecimales.</summary>
    public static string? LeerHuella(string texto)
    {
        var cifras = new string(texto.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        return cifras.Length == 64 && cifras.All(Uri.IsHexDigit) ? cifras : null;
    }

    /// <summary>La prueba de que el servidor conoce el código: HMAC-SHA256 con el código, del reto y la huella.</summary>
    public static string Prueba(string codigo, string reto, string huella) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(NormalizarCodigo(codigo)), Encoding.UTF8.GetBytes($"{reto}|{huella}")));

    /// <summary>Sin guiones ni espacios y en mayúsculas: el código se acepta escrito de cualquier forma, como en Control Print.</summary>
    public static string NormalizarCodigo(string codigo) =>
        new(codigo.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Los códigos de Control Print tienen 16 letras y números (ABCD-EFGH-JKLM-NPQR).</summary>
    public static bool CodigoCompleto(string codigo) => NormalizarCodigo(codigo).Length == 16;

    /// <summary>Las letras y números de los códigos (Protocol.NewKey de Control Print): sin I, O, 0 ni 1, que se confunden.</summary>
    internal const string AlfabetoCodigo = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Si un texto parece el código de conexión, escrito de cualquier forma: con guiones, espacios, puntos, guiones bajos o rayas
    /// en medio, o sin nada, y en mayúsculas o minúsculas (Control Print lo acepta así). Lo parece si sus letras y números son 16
    /// del alfabeto de Control Print, o 16 letras y números juntos o en cuatro grupos de cuatro (mal copiado, con una O por un
    /// 0). Una IP no lo parece. Lo que lo parece no se toma por el nombre de una PC: no se busca en la red ni se repite.
    /// </summary>
    public static bool PareceCodigo(string texto)
    {
        texto = texto.Trim();
        if (IPAddress.TryParse(texto, out _))
            return false;
        var normalizado = NormalizarCodigo(texto);
        if (normalizado.Length != 16)
            return false;
        if (normalizado.All(c => AlfabetoCodigo.Contains(c)))
            return true;
        var grupos = new List<int>();
        var largo = 0;
        foreach (var c in texto)
        {
            if (char.IsLetterOrDigit(c))
                largo++;
            else if (largo > 0)
            {
                grupos.Add(largo);
                largo = 0;
            }
        }
        if (largo > 0)
            grupos.Add(largo);
        return normalizado.All(char.IsAsciiLetterOrDigit) && (grupos is [16] or [4, 4, 4, 4]);
    }

    /// <summary>Como el servidor: nombres en camelCase y los enum como texto, con los nombres de Control Print.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ConvertidorEnumsControlPrint() },
    };
}

/// <summary>
/// Los enum de Control Print viajan como texto, con sus nombres en inglés (<see cref="JsonStringEnumMemberNameAttribute"/>
/// en cada valor). Al leer se aceptan también números, y un valor que esta versión no conoce (de un Control Print más
/// nuevo) queda como <c>Desconocido</c> en lugar de romper toda la respuesta: a Control Print le pasó con «Guest» entre la
/// 1.8 y la 1.9.
/// </summary>
internal sealed class ConvertidorEnumsControlPrint : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum && typeToConvert.Namespace == typeof(ConvertidorEnumsControlPrint).Namespace;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Convertidor<>).MakeGenericType(typeToConvert))!;

    private sealed class Convertidor<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly Dictionary<T, string> Nombres = Enum.GetValues<T>().ToDictionary(
            v => v,
            v => typeof(T).GetField(v.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? v.ToString());

        private static readonly Dictionary<string, T> Valores = Nombres
            .Where(n => n.Value != "Desconocido")
            .ToDictionary(n => n.Value, n => n.Key, StringComparer.OrdinalIgnoreCase);

        private static readonly T Desconocido = Enum.TryParse<T>("Desconocido", out var valor) ? valor : default;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number when reader.TryGetInt32(out var numero) && Enum.IsDefined(typeof(T), numero) && numero >= 0:
                    return (T)Enum.ToObject(typeof(T), numero);
                case JsonTokenType.String when reader.GetString() is { } texto && Valores.TryGetValue(texto, out var valor):
                    return valor;
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    reader.Skip();
                    return Desconocido;
                default:
                    return Desconocido;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Nombres.TryGetValue(value, out var nombre) ? nombre : value.ToString());
    }
}
