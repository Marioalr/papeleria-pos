using System.Globalization;
using System.Text;

namespace PapeleriaPos.Impresiones.Medicion;

/// <summary>
/// Lo que contesta una impresora por la red, para revisarla en la tienda (2026-11-03: la L5590 nunca se ha leído, y hay que distinguir
/// las dos WF-6590). <b>Solo lee:</b> GET de SNMP (los mismos OID que lee el módulo, más los que dicen qué impresora es) y la página de
/// estado. Lo usa <c>diagnosticar-impresoras</c> de PapeleriaPos.Herramientas; quién pregunta (la red de verdad o una prueba) lo pone
/// quien llama.
/// </summary>
public static class DiagnosticoImpresora
{
    /// <summary>Una pregunta SNMP y lo que contestó (o por qué no).</summary>
    public sealed record Respuesta(string Oid, string Que, string? Valor, string? Error);

    /// <summary>Lo que se vio de una impresora.</summary>
    /// <param name="Lectura">Cómo la leería el módulo (niveles, contador, serie y caja de mantenimiento).</param>
    /// <param name="Modelo">Lo que dice ser (hrDeviceDescr o sysDescr).</param>
    /// <param name="Perfil">El modelo que conoce el módulo, si es uno (WF-6590 o L5590).</param>
    /// <param name="Hallazgos">Lo que hay que anotar, en palabras.</param>
    public sealed record Resultado(string Host, IReadOnlyList<Respuesta> Respuestas, PrinterStatus Lectura, string? Modelo, PerfilDeImpresora? Perfil,
        IReadOnlyList<string> Hallazgos);

    private const string Suministros = "1.3.6.1.2.1.43.11.1.1";
    private const string Epson = "1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.115.116.1.0.1";

    /// <summary>Los suministros que se preguntan (de 1 a este): la WF-6590 tiene 4 tintas; se pregunta de más por si la L5590 las numera distinto.</summary>
    public const int SuministrosQueSePreguntan = 8;

    /// <summary>Lo que se pregunta, en orden, con lo que es.</summary>
    public static IReadOnlyList<(string Oid, string Que)> Preguntas { get; } = Armar();

    private static List<(string, string)> Armar()
    {
        var lista = new List<(string, string)>
        {
            ("1.3.6.1.2.1.1.1.0", "sysDescr (qué dice ser)"),
            ("1.3.6.1.2.1.1.5.0", "sysName (su nombre en la red)"),
            ("1.3.6.1.2.1.25.3.2.1.3.1", "hrDeviceDescr (el modelo: así la reconoce Control Print)"),
            ("1.3.6.1.2.1.43.5.1.1.17.1", "prtGeneralSerialNumber (la serie)"),
            ("1.3.6.1.2.1.43.10.2.1.4.1.1", "prtMarkerLifeCount (el contador de caras)"),
        };
        for (var i = 1; i <= SuministrosQueSePreguntan; i++)
        {
            lista.Add(($"{Suministros}.6.1.{i}", $"suministro {i}: descripción"));
            lista.Add(($"{Suministros}.8.1.{i}", $"suministro {i}: capacidad"));
            lista.Add(($"{Suministros}.9.1.{i}", $"suministro {i}: nivel"));
        }
        lista.Add((Epson, "Epson «st» (@BDC ST2: serie y contadores de color y B/N)"));
        return lista;
    }

    /// <summary>
    /// Pregunta todo a la impresora en <paramref name="host"/>, una pregunta a la vez (con <paramref name="preguntar"/>: SNMP v1 GET), y su
    /// página de estado (<paramref name="pagina"/>; vacío, no se pide). Lo que no contesta queda con su error: nunca truena.
    /// </summary>
    public static async Task<Resultado> LeerAsync(string host, Func<string, CancellationToken, Task<SnmpClient.Value>> preguntar,
        Func<CancellationToken, Task<string>>? pagina, CancellationToken ct = default)
    {
        var respuestas = new List<Respuesta>();
        var valores = new Dictionary<string, SnmpClient.Value>();
        foreach (var (oid, que) in Preguntas)
        {
            try
            {
                var valor = await preguntar(oid, ct);
                if (valor.IsError)
                {
                    respuestas.Add(new Respuesta(oid, que, null, "no lo tiene"));
                    continue;
                }
                valores[oid] = valor;
                respuestas.Add(new Respuesta(oid, que, Texto(valor), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                respuestas.Add(new Respuesta(oid, que, null, ex.Message));
            }
        }
        string? html = null;
        string? errorPagina = null;
        if (pagina is not null)
        {
            try
            {
                html = await pagina(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errorPagina = ex.Message;
            }
        }
        return Concluir(host, respuestas, valores, html, errorPagina);
    }

    private static Resultado Concluir(string host, List<Respuesta> respuestas, Dictionary<string, SnmpClient.Value> valores, string? html, string? errorPagina)
    {
        var hallazgos = new List<string>();
        string? Cadena(string oid) => valores.TryGetValue(oid, out var v) ? v.AsString().Trim('\0', ' ') : null;
        if (respuestas.All(r => r.Error is not null))
            hallazgos.Add("No contestó nada por SNMP (¿está prendida, es su IP, tiene SNMP activo con la comunidad «public»?).");

        var modelo = Cadena("1.3.6.1.2.1.25.3.2.1.3.1") is { Length: > 0 } dispositivo ? dispositivo : Cadena("1.3.6.1.2.1.1.1.0");
        var perfil = PerfilesImpresora.PorDriver(modelo);
        if (modelo is not null)
            hallazgos.Add(perfil is null
                ? $"Dice ser «{modelo}»: no es un modelo que conozca el módulo."
                : $"Dice ser «{modelo}»: el módulo la lleva como {perfil.Modelo}.");

        // Las tintas como las lee el módulo (PrinterStatusReader): por la descripción de cada suministro.
        int? k = null, c = null, m = null, y = null;
        var sinNivel = new List<string>();
        for (var i = 1; i <= SuministrosQueSePreguntan; i++)
        {
            var descripcion = Cadena($"{Suministros}.6.1.{i}");
            if (descripcion is null || !valores.TryGetValue($"{Suministros}.9.1.{i}", out var nivelSnmp))
                continue;
            var nivel = (int)nivelSnmp.AsInteger();
            if (nivel < 0)
            {
                sinNivel.Add($"«{descripcion}» ({(nivel == -3 ? "-3: «queda algo»" : $"{nivel}: no se sabe")})");
                continue;
            }
            // El módulo toma el nivel como porcentaje (en la WF-6590 la capacidad es 100); si aquí es otra, hay que convertirlo.
            if (valores.TryGetValue($"{Suministros}.8.1.{i}", out var capacidad) && capacidad.AsInteger() is > 0 and not 100 and var maximo)
                hallazgos.Add($"El suministro {i} («{descripcion}») tiene capacidad {maximo}, no 100: el módulo toma su nivel ({nivel}) como porcentaje y "
                    + $"habría que convertirlo ({Math.Round(nivel * 100.0 / maximo)} %).");
            if (descripcion.StartsWith("Black", StringComparison.OrdinalIgnoreCase)) k ??= nivel;
            else if (descripcion.StartsWith("Cyan", StringComparison.OrdinalIgnoreCase)) c ??= nivel;
            else if (descripcion.StartsWith("Magenta", StringComparison.OrdinalIgnoreCase)) m ??= nivel;
            else if (descripcion.StartsWith("Yellow", StringComparison.OrdinalIgnoreCase)) y ??= nivel;
            else hallazgos.Add($"El suministro {i} («{descripcion}») no empieza con Black, Cyan, Magenta ni Yellow: el módulo no lo lee como tinta.");
        }
        if (sinNivel.Count > 0)
            hallazgos.Add($"Sin nivel por SNMP: {string.Join(", ", sinNivel)}. Una EcoTank puede no medir su tinta: entonces no hay avisos ni "
                + "calibración de esa tinta.");
        if (k is null && c is null && m is null && y is null && sinNivel.Count == 0 && respuestas.Any(r => r.Error is null))
            hallazgos.Add("No dio los niveles de tinta por el Printer-MIB (43.11.1.1).");

        long? contador = valores.TryGetValue("1.3.6.1.2.1.43.10.2.1.4.1.1", out var vida) ? vida.AsInteger() : null;
        hallazgos.Add(contador is { } total
            ? $"Contador de caras: {total.ToString("N0", CultureInfo.InvariantCulture)} (con esto se verifican las hojas que salieron)."
            : "No dio su contador de caras: sin él no se verifican las hojas ni se compara contra lo cobrado.");

        string? serie = Cadena("1.3.6.1.2.1.43.5.1.1.17.1") is { Length: > 0 } s ? s : null;
        long? color = null, bn = null;
        if (valores.TryGetValue(Epson, out var st) && EpsonSt2.TryParse(st.Data, out var st2))
        {
            serie ??= st2.Serial;
            (color, bn) = (st2.ColorPages, st2.MonoPages);
            if (color is { } cc && bn is { } bb)
            {
                var suma = cc + bb;
                var cuadra = contador switch
                {
                    null => ".",
                    { } t when t == suma => ", que suman su contador.",
                    { } t => $": no suman su contador ({suma.ToString("N0", CultureInfo.InvariantCulture)} contra {t.ToString("N0", CultureInfo.InvariantCulture)}); revisar.",
                };
                hallazgos.Add($"Epson «st» contestó: {cc.ToString("N0", CultureInfo.InvariantCulture)} color y {bb.ToString("N0", CultureInfo.InvariantCulture)} B/N{cuadra}");
            }
            else
            {
                hallazgos.Add("Epson «st» contestó, sin los contadores de color y B/N (campo 0x36).");
            }
        }
        else
        {
            hallazgos.Add("Epson «st» no contestó: sin la serie de ahí ni los contadores de color y B/N.");
        }
        hallazgos.Add(serie is null ? "No dijo su serie: para distinguir las dos WF-6590 hay que anotarla de su etiqueta o su página."
            : $"Serie: {serie} (así se distingue de otra igual).");

        int? caja = html is null ? null : PrinterStatusReader.ParseMaintenance(html);
        hallazgos.Add(html is null
            ? $"Su página de estado no abrió{(errorPagina is null ? "" : $" ({errorPagina})")}: sin la barra de la caja de mantenimiento."
            : caja is { } vidaCaja
                ? $"Su página de estado tiene la barra de la caja de mantenimiento: le queda {vidaCaja} %."
                : "Su página de estado abrió, sin la barra de la caja de mantenimiento (Ink_Waste): o no tiene caja, o la dice de otra forma.");

        var lectura = new PrinterStatus
        {
            InkK = k, InkC = c, InkM = m, InkY = y, Maintenance = caja, LifeCount = contador, ColorCount = color, MonoCount = bn, Serial = serie,
            Errors = respuestas.Where(r => r.Error is not null && r.Error != "no lo tiene").Select(r => $"{r.Que}: {r.Error}").ToList(),
        };
        return new Resultado(host, respuestas, lectura, modelo, perfil, hallazgos);
    }

    /// <summary>Un valor SNMP para leerlo: el texto si es texto, el número si es número, y si no, sus bytes.</summary>
    public static string Texto(SnmpClient.Value v) => v.Type switch
    {
        0x02 or 0x41 or 0x42 or 0x43 or 0x46 => v.AsInteger().ToString(CultureInfo.InvariantCulture),
        0x04 when v.Data.All(b => b is >= 0x20 and < 0x7F or 0x0D or 0x0A or 0x09 or 0) => v.AsString().Trim('\0'),
        _ => Convert.ToHexString(v.Data),
    };

    /// <summary>El informe de una impresora, para la terminal y para guardarlo.</summary>
    public static string Informe(Resultado r)
    {
        var texto = new StringBuilder();
        texto.AppendLine(CultureInfo.InvariantCulture, $"## {r.Host}{(r.Perfil is { } p ? $" · {p.NombreCorto}" : "")}");
        texto.AppendLine();
        foreach (var hallazgo in r.Hallazgos)
            texto.AppendLine(CultureInfo.InvariantCulture, $"- {hallazgo}");
        var l = r.Lectura;
        texto.AppendLine();
        texto.AppendLine(CultureInfo.InvariantCulture,
            $"Como la leería el módulo: negro {Nivel(l.InkK)}, cian {Nivel(l.InkC)}, magenta {Nivel(l.InkM)}, amarillo {Nivel(l.InkY)}, "
            + $"caja de mantenimiento {Nivel(l.Maintenance)}, contador {l.LifeCount?.ToString(CultureInfo.InvariantCulture) ?? "—"}.");
        texto.AppendLine();
        texto.AppendLine("| OID | Qué es | Contestó |");
        texto.AppendLine("|---|---|---|");
        foreach (var respuesta in r.Respuestas)
            texto.AppendLine(CultureInfo.InvariantCulture, $"| {respuesta.Oid} | {respuesta.Que} | {(respuesta.Error is { } e ? $"({e})" : respuesta.Valor)} |");
        return texto.ToString();
    }

    private static string Nivel(int? nivel) => nivel is { } n ? $"{n} %" : "—";
}
