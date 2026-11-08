using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// servicio.json (un JSON con comentarios, como appsettings.json) cambiado sin volverlo a escribir: solo se cambia el valor de lo que
/// cambió, lo que falta se agrega al final de su sección y lo que sobra se quita con su renglón. Lo demás —los comentarios, el orden,
/// los espacios y las secciones que el instalador no conoce— se queda tal cual, y también la marca BOM del archivo y su salto de
/// renglón (el del Bloc de notas, \r\n).
/// <para>
/// Se pierden solo los comentarios que estaban dentro de lo que se quita o de un valor que se reemplaza entero (una sección que no era
/// un objeto). Después de cambiarlo se vuelve a leer: si no quedó igual a lo pedido, no se usa (<see cref="Igualar"/> truena y quien lo
/// llama escribe el archivo completo).
/// </para>
/// </summary>
public sealed class TextoJsonc
{
    private static readonly JsonReaderOptions Lectura = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private byte[] _texto;
    private readonly bool _bom;
    private readonly string _salto;

    public TextoJsonc(byte[] archivo)
    {
        _bom = archivo is [0xEF, 0xBB, 0xBF, ..];
        _texto = _bom ? archivo[3..] : archivo;
        _salto = _texto.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
    }

    /// <summary>El archivo como queda, con su BOM si lo tenía.</summary>
    public byte[] Bytes => _bom ? [0xEF, 0xBB, 0xBF, .. _texto] : _texto;

    public string Texto => Encoding.UTF8.GetString(_texto);

    /// <summary>Lo que dice el archivo (sin los comentarios).</summary>
    public JsonObject Objeto() => Json.Objeto(_texto, ServicioJson.Archivo, comentarios: true);

    /// <summary>
    /// Lo que cambia de <paramref name="actual"/> a <paramref name="objetivo"/>, en palabras para el registro: «Eleventa:Servidor»,
    /// «Instalacion (se agrega)», «ControlPrint (se quita)». Solo los nombres, nunca un valor.
    /// </summary>
    public static List<string> Cambios(JsonObject actual, JsonObject objetivo)
    {
        var operaciones = new List<Operacion>();
        Diferencias([], actual, objetivo, operaciones);
        return operaciones.Select(o => o.Describir()).ToList();
    }

    /// <summary>
    /// Deja el texto con lo de <paramref name="objetivo"/>, cambiando lo menos posible. Si al volverlo a leer no dice lo mismo (algo raro
    /// en el archivo), truena con InvalidOperationException y el texto no se usa.
    /// </summary>
    public void Igualar(JsonObject objetivo)
    {
        var operaciones = new List<Operacion>();
        Diferencias([], Objeto(), objetivo, operaciones);
        foreach (var operacion in operaciones)
            Aplicar(operacion);
        JsonObject quedo;
        try
        {
            quedo = Objeto();
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Al cambiar servicio.json quedó mal escrito.", ex);
        }
        if (!Json.Iguales(quedo, objetivo))
            throw new InvalidOperationException("Al cambiar servicio.json no quedó como se pidió.");
    }

    /// <summary>Todo el objeto escrito de nuevo, con unos renglones de comentario arriba (cuando cambiarlo por partes no se pudo).</summary>
    public static TextoJsonc Completo(JsonObject objetivo, IEnumerable<string> comentario, bool bom, string salto)
    {
        var texto = new StringBuilder();
        foreach (var renglon in comentario)
            texto.Append("// ").Append(renglon).Append(salto);
        texto.Append(Json.Escribir(objetivo).Replace("\n", salto)).Append(salto);
        var bytes = Encoding.UTF8.GetBytes(texto.ToString());
        return new TextoJsonc(bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
    }

    public bool ConBom => _bom;

    public string Salto => _salto;

    // ── Qué cambia ──

    private abstract record Operacion(string[] Ruta, string Nombre)
    {
        protected string Completo => Ruta.Length == 0 ? Nombre : $"{string.Join(':', Ruta)}:{Nombre}";

        public abstract string Describir();
    }

    private sealed record Reemplazo(string[] Ruta, string Nombre, JsonNode? Valor) : Operacion(Ruta, Nombre)
    {
        public override string Describir() => Completo;
    }

    private sealed record Agregado(string[] Ruta, string Nombre, JsonNode? Valor) : Operacion(Ruta, Nombre)
    {
        public override string Describir() => $"{Completo} (se agrega)";
    }

    private sealed record Quitado(string[] Ruta, string Nombre) : Operacion(Ruta, Nombre)
    {
        public override string Describir() => $"{Completo} (se quita)";
    }

    private static void Diferencias(string[] ruta, JsonObject actual, JsonObject objetivo, List<Operacion> operaciones)
    {
        foreach (var (nombre, valor) in actual)
        {
            if (Json.Clave(objetivo, nombre) is not { } clave)
            {
                operaciones.Add(new Quitado(ruta, nombre));
                continue;
            }
            var nuevo = objetivo[clave];
            if (Json.Iguales(valor, nuevo))
                continue;
            if (valor is JsonObject antes && nuevo is JsonObject despues)
                Diferencias([.. ruta, nombre], antes, despues, operaciones);
            else
                operaciones.Add(new Reemplazo(ruta, nombre, nuevo));
        }
        foreach (var (nombre, valor) in objetivo)
            if (Json.Clave(actual, nombre) is null)
                operaciones.Add(new Agregado(ruta, nombre, valor));
    }

    // ── Dónde está cada cosa en el texto ──

    /// <summary>Una clave de un objeto: dónde empieza su nombre (la comilla) y dónde empieza y acaba su valor (el fin, sin incluirlo).</summary>
    private readonly record struct Propiedad(string Nombre, int InicioNombre, int InicioValor, int FinValor, bool EsObjeto);

    /// <summary>El objeto de esa ruta (por sus nombres, desde la raíz): su «{», su «}» y sus claves.</summary>
    private (int Abre, int Cierra, List<Propiedad> Propiedades) Ubicar(string[] ruta)
    {
        var abre = Saltar(0);
        if (abre >= _texto.Length || _texto[abre] != (byte)'{')
            throw new InvalidOperationException("servicio.json no empieza con «{».");
        var propiedades = Propiedades(abre, out var cierra);
        foreach (var nombre in ruta)
        {
            var indice = propiedades.FindIndex(p => string.Equals(p.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
            if (indice < 0 || !propiedades[indice].EsObjeto)
                throw new InvalidOperationException($"En servicio.json no está la sección «{nombre}».");
            abre = propiedades[indice].InicioValor;
            propiedades = Propiedades(abre, out cierra);
        }
        return (abre, cierra, propiedades);
    }

    private List<Propiedad> Propiedades(int abre, out int cierra)
    {
        var lector = new Utf8JsonReader(_texto.AsSpan(abre), Lectura);
        if (!lector.Read() || lector.TokenType != JsonTokenType.StartObject)
            throw new InvalidOperationException("Ahí no empieza un objeto de servicio.json.");
        var propiedades = new List<Propiedad>();
        while (lector.Read())
        {
            if (lector.TokenType == JsonTokenType.EndObject)
            {
                cierra = abre + (int)lector.TokenStartIndex;
                return propiedades;
            }
            var nombre = lector.GetString() ?? "";
            var inicioNombre = abre + (int)lector.TokenStartIndex;
            lector.Read();
            var inicioValor = abre + (int)lector.TokenStartIndex;
            var esObjeto = lector.TokenType == JsonTokenType.StartObject;
            if (lector.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                lector.Skip();
            propiedades.Add(new Propiedad(nombre, inicioNombre, inicioValor, abre + (int)lector.BytesConsumed, esObjeto));
        }
        throw new InvalidOperationException("servicio.json se acaba antes de cerrar sus llaves.");
    }

    // ── Cambiar el texto ──

    private void Aplicar(Operacion operacion)
    {
        var (abre, cierra, propiedades) = Ubicar(operacion.Ruta);
        var indice = propiedades.FindIndex(p => string.Equals(p.Nombre, operacion.Nombre, StringComparison.OrdinalIgnoreCase));
        switch (operacion)
        {
            case Agregado agregado:
                Agregar(abre, cierra, propiedades, agregado.Nombre, agregado.Valor);
                break;
            case Reemplazo reemplazo when indice >= 0:
                var propiedad = propiedades[indice];
                Empalmar(propiedad.InicioValor, propiedad.FinValor, Formatear(reemplazo.Valor, Sangria(propiedad.InicioNombre)));
                break;
            case Quitado when indice >= 0:
                Quitar(propiedades, indice);
                break;
            default:
                throw new InvalidOperationException($"En servicio.json no está «{operacion.Nombre}».");
        }
    }

    /// <summary>Al final del objeto: después de la última clave (con su coma) o, si no tiene, antes de su «}».</summary>
    private void Agregar(int abre, int cierra, List<Propiedad> propiedades, string nombre, JsonNode? valor)
    {
        var sangria = propiedades.Count > 0 && InicioDeRenglon(propiedades[0].InicioNombre) is not null
            ? Sangria(propiedades[0].InicioNombre)
            : Sangria(abre) + "  ";
        var propiedad = Json.Nombre(nombre) + ": " + Formatear(valor, sangria);
        if (propiedades.Count == 0)
        {
            // Vacío (como la plantilla, que solo trae comentarios): en su propio renglón, antes de la «}».
            if (InicioDeRenglon(cierra) is { } renglon)
                Empalmar(renglon, renglon, sangria + propiedad + _salto);
            else
                Empalmar(cierra, cierra, _salto + sangria + propiedad + _salto + Sangria(abre));
            return;
        }
        var ultima = propiedades[^1];
        var siguiente = Saltar(ultima.FinValor);
        if (siguiente < _texto.Length && _texto[siguiente] == (byte)',')
        {
            // La última ya trae una coma de más (se permite): la nueva va después de ella.
            if (FinDeRenglon(siguiente + 1) is { } fin)
                Empalmar(fin, fin, sangria + propiedad + _salto);
            else
                Empalmar(siguiente + 1, siguiente + 1, _salto + sangria + propiedad);
            return;
        }
        if (FinDeRenglon(ultima.FinValor) is { } finDeRenglon)
        {
            // La coma justo después del valor (antes de un comentario de su renglón) y la nueva en el renglón siguiente.
            Empalmar(finDeRenglon, finDeRenglon, sangria + propiedad + _salto);
            Empalmar(ultima.FinValor, ultima.FinValor, ",");
        }
        else
            Empalmar(ultima.FinValor, ultima.FinValor, "," + _salto + sangria + propiedad);
    }

    /// <summary>Con su renglón y su coma (la de después o, si era la última, la de antes).</summary>
    private void Quitar(List<Propiedad> propiedades, int indice)
    {
        var propiedad = propiedades[indice];
        var inicio = InicioDeRenglon(propiedad.InicioNombre) ?? propiedad.InicioNombre;
        var siguiente = Saltar(propiedad.FinValor);
        if (siguiente < _texto.Length && _texto[siguiente] == (byte)',')
        {
            Empalmar(inicio, FinDeRenglon(siguiente + 1) ?? siguiente + 1, "");
            return;
        }
        Empalmar(inicio, FinDeRenglon(propiedad.FinValor) ?? propiedad.FinValor, "");
        if (indice == 0)
            return;
        var coma = Saltar(propiedades[indice - 1].FinValor);
        if (coma < inicio && _texto[coma] == (byte)',')
            Empalmar(coma, coma + 1, "");
    }

    private void Empalmar(int desde, int hasta, string texto) =>
        _texto = [.. _texto.AsSpan(0, desde), .. Encoding.UTF8.GetBytes(texto), .. _texto.AsSpan(hasta)];

    /// <summary>El valor con la sangría de su renglón en cada renglón de adentro, y el salto del archivo.</summary>
    private string Formatear(JsonNode? valor, string sangria) => Json.Escribir(valor).Replace("\n", _salto + sangria);

    private bool Blanco(int i) => _texto[i] is (byte)' ' or (byte)'\t';

    /// <summary>El principio del renglón de <paramref name="i"/> si antes de él solo hay espacios; vacío si hay algo más.</summary>
    private int? InicioDeRenglon(int i)
    {
        var j = i;
        while (j > 0 && Blanco(j - 1))
            j--;
        return j == 0 || _texto[j - 1] == (byte)'\n' ? j : null;
    }

    /// <summary>Los espacios con que empieza el renglón de <paramref name="i"/>.</summary>
    private string Sangria(int i)
    {
        var inicio = i;
        while (inicio > 0 && _texto[inicio - 1] != (byte)'\n')
            inicio--;
        var fin = inicio;
        while (fin < _texto.Length && Blanco(fin))
            fin++;
        return Encoding.UTF8.GetString(_texto, inicio, fin - inicio);
    }

    /// <summary>
    /// Después del salto de renglón si de <paramref name="i"/> al final del renglón solo hay espacios o un comentario «//»; vacío si
    /// hay algo más en el renglón.
    /// </summary>
    private int? FinDeRenglon(int i)
    {
        var j = i;
        while (j < _texto.Length && Blanco(j))
            j++;
        if (j + 1 < _texto.Length && _texto[j] == (byte)'/' && _texto[j + 1] == (byte)'/')
            while (j < _texto.Length && _texto[j] != (byte)'\n')
                j++;
        if (j >= _texto.Length)
            return j;
        if (_texto[j] == (byte)'\n')
            return j + 1;
        if (j + 1 < _texto.Length && _texto[j] == (byte)'\r' && _texto[j + 1] == (byte)'\n')
            return j + 2;
        return null;
    }

    /// <summary>Lo primero que no es espacio, salto ni comentario, desde <paramref name="i"/>.</summary>
    private int Saltar(int i)
    {
        while (i < _texto.Length)
        {
            var c = _texto[i];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                i++;
            else if (c == (byte)'/' && i + 1 < _texto.Length && _texto[i + 1] == (byte)'/')
                while (i < _texto.Length && _texto[i] != (byte)'\n')
                    i++;
            else if (c == (byte)'/' && i + 1 < _texto.Length && _texto[i + 1] == (byte)'*')
            {
                var fin = _texto.AsSpan(i + 2).IndexOf("*/"u8);
                i = fin < 0 ? _texto.Length : i + 2 + fin + 2;
            }
            else
                break;
        }
        return i;
    }
}
