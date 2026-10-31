using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// Lo de JSON que comparten servicio.json y caja.json. Las claves se buscan sin distinguir mayúsculas, como las leen el servicio (la
/// configuración de .NET) y la caja (PropertyNameCaseInsensitive): «eleventa» y «Eleventa» son la misma, y una que ya está se cambia con
/// el nombre que tiene. Todo con el DOM de System.Text.Json, sin reflexión (el .exe va recortado).
/// </summary>
internal static class Json
{
    /// <summary>Como lee servicio.json el servicio (la configuración de .NET): con comentarios y comas de más al final.</summary>
    public static readonly JsonDocumentOptions ConComentarios = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>El nombre que ya tiene esa clave en el objeto (sin distinguir mayúsculas); vacío si no está.</summary>
    public static string? Clave(JsonObject objeto, string nombre)
    {
        foreach (var (clave, _) in objeto)
            if (string.Equals(clave, nombre, StringComparison.OrdinalIgnoreCase))
                return clave;
        return null;
    }

    public static JsonNode? Valor(JsonObject objeto, string nombre) => Clave(objeto, nombre) is { } clave ? objeto[clave] : null;

    /// <summary>Cambia el valor (con el nombre que ya tiene, en su lugar) o lo agrega al final.</summary>
    public static void Poner(JsonObject objeto, string nombre, JsonNode? valor)
    {
        if (Clave(objeto, nombre) is { } clave)
            objeto[clave] = valor;
        else
            objeto[nombre] = valor;
    }

    public static void Quitar(JsonObject objeto, string nombre)
    {
        if (Clave(objeto, nombre) is { } clave)
            objeto.Remove(clave);
    }

    /// <summary>La sección (un objeto) con ese nombre; si no está o no es un objeto, una nueva en su lugar.</summary>
    public static JsonObject Seccion(JsonObject objeto, string nombre)
    {
        if (Valor(objeto, nombre) is JsonObject seccion)
            return seccion;
        var nueva = new JsonObject();
        Poner(objeto, nombre, nueva);
        return nueva;
    }

    /// <summary>Un texto: el de JSON, o lo que diga un número o un sí/no («3», «true»), como lo lee la configuración de .NET.</summary>
    public static string? Texto(JsonNode? nodo) => nodo switch
    {
        null => null,
        JsonValue valor when valor.GetValueKind() == JsonValueKind.String => valor.GetValue<string>(),
        JsonValue valor => valor.ToJsonString(),
        _ => null,
    };

    /// <summary>Un sí/no de la configuración: true, o el texto «true» (como lo lee la configuración de .NET).</summary>
    public static bool SiNo(JsonNode? nodo) => nodo switch
    {
        JsonValue valor when valor.GetValueKind() == JsonValueKind.True => true,
        JsonValue valor when valor.GetValueKind() == JsonValueKind.String => bool.TryParse(valor.GetValue<string>().Trim(), out var si) && si,
        _ => false,
    };

    /// <summary>
    /// Si dos valores son el mismo: los objetos con las mismas claves (sin distinguir mayúsculas, en cualquier orden) y los mismos
    /// valores; los textos tal cual; los números como están escritos.
    /// </summary>
    public static bool Iguales(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        switch (a)
        {
            case JsonObject x when b is JsonObject y:
                if (x.Count != y.Count)
                    return false;
                foreach (var (clave, valor) in x)
                    if (Clave(y, clave) is not { } otra || !Iguales(valor, y[otra]))
                        return false;
                return true;
            case JsonArray x when b is JsonArray y:
                if (x.Count != y.Count)
                    return false;
                for (var i = 0; i < x.Count; i++)
                    if (!Iguales(x[i], y[i]))
                        return false;
                return true;
            case JsonValue x when b is JsonValue y:
                var tipo = x.GetValueKind();
                if (tipo != y.GetValueKind())
                    return false;
                return tipo switch
                {
                    JsonValueKind.String => x.GetValue<string>() == y.GetValue<string>(),
                    JsonValueKind.Number => x.ToJsonString() == y.ToJsonString(),
                    _ => true,
                };
            default:
                return false;
        }
    }

    /// <summary>
    /// Si un objeto trae la misma clave dos veces (sin distinguir mayúsculas): el servicio no arranca con un servicio.json así («A
    /// duplicate key»), y la caja tomaría la última.
    /// </summary>
    public static string? ClaveRepetida(JsonNode? nodo, string ruta = "")
    {
        switch (nodo)
        {
            case JsonObject objeto:
                var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (clave, valor) in objeto)
                {
                    var completa = ruta.Length == 0 ? clave : $"{ruta}:{clave}";
                    if (!vistas.Add(clave))
                        return completa;
                    if (ClaveRepetida(valor, completa) is { } repetida)
                        return repetida;
                }
                return null;
            case JsonArray arreglo:
                for (var i = 0; i < arreglo.Count; i++)
                    if (ClaveRepetida(arreglo[i], $"{ruta}:{i}") is { } repetida)
                        return repetida;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Un objeto JSON de un archivo (servicio.json con comentarios; caja.json sin ellos). Lo que no se entiende se dice con su renglón,
    /// sin el contenido del archivo (FormatException).
    /// </summary>
    public static JsonObject Objeto(byte[] bytes, string nombre, bool comentarios)
    {
        JsonObject objeto;
        string? repetida;
        try
        {
            if (JsonNode.Parse(SinBom(bytes), null, comentarios ? ConComentarios : default) is not JsonObject leido)
                throw new FormatException($"{nombre} no es un objeto (va entre llaves: {{ … }}).");
            objeto = leido;
            // Recorrerlo todo de una vez: JsonObject arma sus claves al usarlas, y ahí sale una repetida.
            repetida = ClaveRepetida(objeto);
        }
        catch (JsonException ex)
        {
            throw new FormatException(
                $"{nombre} está mal escrito{(ex.LineNumber is { } renglon ? $" (renglón {renglon + 1}{(ex.BytePositionInLine is { } columna ? $", columna {columna + 1}" : "")})" : "")}.",
                ex);
        }
        catch (ArgumentException ex)
        {
            // Una clave repetida tal cual (dos veces «Eleventa»): JsonObject no la acepta.
            throw new FormatException($"{nombre} trae una clave repetida: {ex.Message}", ex);
        }
        if (repetida is not null)
            throw new FormatException($"{nombre} trae dos veces «{repetida}» (una con otras mayúsculas): el servicio no arranca así.");
        return objeto;
    }

    public static ReadOnlySpan<byte> SinBom(byte[] bytes) =>
        bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes.AsSpan(3) : bytes.AsSpan();

    /// <summary>Un valor escrito con sangría de dos espacios, con «\n» como salto (quien lo pone lo cambia por el del archivo).</summary>
    public static string Escribir(JsonNode? valor)
    {
        using var memoria = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(memoria, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            // Sin í en «Papelería» ni en las rutas: se lee como se escribió.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            if (valor is null)
                escritor.WriteNullValue();
            else
                valor.WriteTo(escritor);
        }
        return Encoding.UTF8.GetString(memoria.ToArray());
    }

    /// <summary>Un nombre de clave entre comillas, como va en el JSON.</summary>
    public static string Nombre(string nombre) => "\"" + JsonEncodedText.Encode(nombre, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
}
