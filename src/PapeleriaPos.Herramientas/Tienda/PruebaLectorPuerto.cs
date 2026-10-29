using System.Globalization;
using System.Text.Json;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Herramientas.Tienda;

/// <summary>
/// probar-lector --puerto (piloto alfa, docs/pruebas-en-tienda.md, «El lector de la caja 2 por puerto COM»): el lector configurado
/// como puerto COM, como lo tiene eleventa en la caja 2. Abre el puerto con los mismos ajustes y la misma lectura que la caja
/// (<see cref="LectorPorPuerto"/>), y dice qué llegó: los bytes de cada pedazo con su tiempo, cada código con lo que tardó y con qué
/// terminó, y si el puerto está ocupado (eleventa abierto) o no existe. Al final, cómo queda «Lector» en caja.json.
/// </summary>
public static class PruebaLectorPuerto
{
    /// <summary>Los ajustes de la línea de comandos, con las mismas reglas que «Lector» en caja.json.</summary>
    public static (AjustesLector? Ajustes, string? Problema) Ajustes(string[] args)
    {
        var campos = new Dictionary<string, string>();
        foreach (var (opcion, campo) in Opciones)
            if (Valor(args, opcion) is { } valor)
                campos[campo] = valor;
        if (!campos.ContainsKey("Puerto"))
            return (null, "Indica el puerto con --puerto (por ejemplo, --puerto COM3: el que tiene eleventa en la caja 2).");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(campos));
        var (ajustes, problema) = AjustesLector.Leer(json.RootElement.Clone());
        return (ajustes, problema?.Replace("«Lector» en caja.json", "En lo que escribiste", StringComparison.Ordinal));
    }

    private static readonly (string Opcion, string Campo)[] Opciones =
    [
        ("--puerto", "Puerto"), ("--baudios", "Baudios"), ("--paridad", "Paridad"), ("--bits", "BitsDeDatos"), ("--parada", "BitsDeParada"),
        ("--fin", "Fin"), ("--silencio", "Silencio"), ("--flujo", "ControlDeFlujo"), ("--dtr", "Dtr"), ("--rts", "Rts"),
    ];

    private static string? Valor(string[] args, string opcion)
    {
        var i = Array.FindIndex(args, a => a.Equals(opcion, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>«Puertos COM en esta PC: COM1, COM3.»</summary>
    public static string Puertos(IReadOnlyList<string> nombres) =>
        nombres.Count > 0 ? $"Puertos COM en esta PC: {string.Join(", ", nombres)}." : "Esta PC no tiene puertos COM ahora (un lector USB en modo COM aparece al conectarlo).";

    /// <summary>Lo que se dice cuando cambia cómo está el puerto.</summary>
    public static string Estado(EstadoLector estado, AjustesLector ajustes, TimeSpan reintento) => estado.Situacion switch
    {
        SituacionLector.Listo => $"{ajustes.Puerto} abierto ({ajustes.Resumen}). Escanea códigos, uno por uno; Esc para terminar.",
        SituacionLector.Ocupado => $"{ajustes.Puerto} está OCUPADO: lo tiene abierto otro programa (eleventa, o la caja del POS). Ciérralo: aquí se "
            + $"vuelve a intentar cada {reintento.TotalSeconds:0} s. Esc para terminar.",
        SituacionLector.SinConectar => $"No se encuentra {ajustes.Puerto}: {estado.Detalle} (¿el lector desconectado o en otro puerto?). Se vuelve a "
            + $"buscar cada {reintento.TotalSeconds:0} s. Esc para terminar.",
        SituacionLector.ConError => $"{ajustes.Puerto} no abre: {estado.Detalle}. Se vuelve a intentar cada {reintento.TotalSeconds:0} s. Esc para terminar.",
        SituacionLector.Abriendo => $"Se desconectó {ajustes.Puerto}: se vuelve a buscar.",
        _ => $"{ajustes.Puerto}: {estado.Situacion}.",
    };

    /// <summary>Un pedazo como llegó: «+3 ms  37 35 30 31  «7501»».</summary>
    public static string Pedazo(byte[] bytes, TimeSpan desdeElAnterior) =>
        $"  +{desdeElAnterior.TotalMilliseconds,5:0} ms  {DecodificadorLector.Hex(bytes)}  «{DecodificadorLector.Visible(bytes)}»";

    /// <summary>Un código (o lo que se descartó), con lo que tardó y con qué terminó.</summary>
    public static IEnumerable<string> Describir(Lectura lectura)
    {
        if (lectura.Codigo is { } codigo)
        {
            yield return $"«{codigo}» ({codigo.Length} caracteres), en {Ms(lectura.Tardo)}; terminó con {lectura.FinTexto}.";
            if (codigo.All(char.IsAsciiDigit) && codigo.Length is >= 11 and <= 13)
                yield return $"  Son {codigo.Length} cifras: compáralas con el código de ese producto en eleventa.";
        }
        else
            yield return $"Se descartó: {lectura.Descartada}. Llegó «{lectura.VisibleCorto}»"
                + (lectura.Largo <= 48 ? $" ({lectura.Hex})." : ".");
    }

    /// <summary>Al terminar: cuántos códigos, con qué terminaron y cómo queda «Lector» en caja.json.</summary>
    public static IEnumerable<string> Resumen(IReadOnlyList<Lectura> lecturas, AjustesLector ajustes, int erroresDeLinea)
    {
        var codigos = lecturas.Where(l => l.Codigo is not null).ToList();
        var descartadas = lecturas.Count - codigos.Count;
        if (lecturas.Count == 0)
        {
            yield return $"No llegó nada por {ajustes.Puerto}. Si el lector pitó al escanear: revisa que sea el puerto de eleventa y que el lector "
                + "esté en modo COM (y no como teclado); prueba también con --dtr false o --rts false.";
            yield break;
        }
        var cuenta = LectorPorPuerto.Cuenta(codigos.Count, descartadas, 0);
        yield return $"{char.ToUpperInvariant(cuenta[0])}{cuenta[1..]}.";
        if (erroresDeLinea > 0 || descartadas > 0)
            yield return $"  {(erroresDeLinea > 0 ? $"El puerto dio {erroresDeLinea} errores de la línea. " : "")}Con basura, la velocidad (--baudios) o la "
                + "paridad no son las del lector: cópialas de eleventa.";
        if (codigos.Count == 0)
            yield break;
        var tardo = codigos.Max(l => l.Tardo);
        // «Fin» no hace falta: la caja toma CR, LF, CRLF, un tabulador o una pausa sola (la guía dice no ponerlo; revisión del PR #142).
        yield return $"  Terminaron con {string.Join(" y ", codigos.Select(l => l.FinTexto).Distinct())}: la caja lo toma sola, sin poner «Fin». "
            + $"El más lento tardó {Ms(tardo)} del primer byte al fin.";
        var texto = new List<string> { $"\"Puerto\": \"{ajustes.Puerto}\"", $"\"Baudios\": {ajustes.Baudios.ToString(CultureInfo.InvariantCulture)}" };
        if (ajustes.Paridad != ParidadLector.Ninguna)
            texto.Add($"\"Paridad\": \"{ajustes.Paridad}\"");
        if (ajustes.BitsDeDatos != 8)
            texto.Add($"\"BitsDeDatos\": {ajustes.BitsDeDatos}");
        if (ajustes.BitsDeParada != 1)
            texto.Add($"\"BitsDeParada\": {ajustes.BitsDeParada.ToString(CultureInfo.InvariantCulture)}");
        if (ajustes.ControlDeFlujo != ControlDeFlujoLector.Ninguno)
            texto.Add($"\"ControlDeFlujo\": \"{ajustes.ControlDeFlujo}\"");
        if (!ajustes.Dtr)
            texto.Add("\"Dtr\": false");
        if (!ajustes.Rts)
            texto.Add("\"Rts\": false");
        yield return "Para caja.json (C:\\ProgramData\\PapeleriaPos\\Caja\\caja.json), después de la primera llave:";
        yield return $"  \"Lector\": {{ {string.Join(", ", texto)} }},";
    }

    private static string Ms(TimeSpan tiempo) => $"{tiempo.TotalMilliseconds:N0} ms";
}
