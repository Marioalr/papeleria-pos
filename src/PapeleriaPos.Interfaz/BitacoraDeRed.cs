using System.Globalization;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Interfaz;

/// <summary>Una caída de la red en la bitácora: qué se perdió, desde cuándo y hasta cuándo (con la hora de la PC).</summary>
/// <param name="Que">«internet», «la caja principal», «el router», «la impresora de tickets»…</param>
/// <param name="Hasta">Vacía: sigue caída.</param>
/// <param name="FinAproximado">
/// No se vio volver: la caja (o el servicio) se cerró o la PC se apagó antes. <paramref name="Hasta"/> es lo último que se
/// anotó; volvió después de eso.
/// </param>
public sealed record CaidaDeRed(string Que, DateTime Desde, DateTime? Hasta, bool FinAproximado = false)
{
    public TimeSpan? Duracion => Hasta - Desde;
}

/// <summary>
/// La bitácora de red (docs/arquitectura.md, «La red de la matriz»; lo que docs/notas.md, «Red de la tienda», pide juntar en la
/// próxima caída): lo que pasa con la red, anotado solo, con fecha y hora, en archivos de texto que se pueden abrir con el Bloc
/// de notas, uno por día (<c>red-caja-2026-10-17.log</c> en la carpeta del registro de la caja; <c>red-servicio-…</c> en la de
/// datos del servicio), que se guardan <see cref="DiasPorOmision"/> días.
/// <para>
/// Compacta: solo los cambios (se perdió algo, volvió, cambió la IP o el Wi-Fi, la red pasó de buena a lenta…) y un resumen cada
/// 15 minutos (<see cref="VigiaDeLaRed"/>). Lo que se pierde y vuelve va siempre igual, para que se pueda leer de vuelta:
/// <c>Se perdió «internet»: …</c> y <c>Volvió «internet» tras 17 min 28 s (se perdió a las 14:03:12): …</c>. De ahí sale el
/// resumen de Configuración › Acerca de (<see cref="Caidas"/>), también de los días anteriores. Cada vez que la caja o el
/// servicio abren, una línea «Empieza la bitácora de red» (con la hora en que arrancó la PC): lo que quedó caído antes se da
/// por terminado ahí, y el hueco entre la última línea y esa dice que la PC estuvo apagada (o que se fue la luz).
/// </para>
/// <para>Nunca truena: escribir es del registro diario (<see cref="RegistroDiario"/>), que no tumba nada si no puede.</para>
/// </summary>
public sealed partial class BitacoraDeRed
{
    /// <summary>Una caída cada dos meses, más o menos: el dueño junta lo de la caída al día siguiente, o el lunes.</summary>
    public const int DiasPorOmision = 10;

    /// <summary>Cuántas caídas recuerda para el resumen.</summary>
    public const int CaidasQueRecuerda = 50;

    internal const string Empieza = "Empieza la bitácora de red";
    internal const string Termina = "Termina la bitácora de red";

    private readonly RegistroDiario _registro;
    private readonly TimeProvider _reloj;
    private readonly Lock _candado = new();

    /// <summary>Lo que está caído ahora, con desde cuándo.</summary>
    private readonly Dictionary<string, DateTime> _abiertas = new(StringComparer.Ordinal);

    private readonly List<CaidaDeRed> _caidas = [];
    private bool _empezada;

    /// <summary>El día en que se quitaron por última vez los archivos viejos: el servicio corre días sin cerrarse.</summary>
    private DateOnly? _limpiaEl;

    /// <summary>Lo que se repite en el periodo del resumen: lo primero se anota y lo demás se cuenta (<see cref="AnotarOContar"/>).</summary>
    private readonly Dictionary<string, int> _contados = new(StringComparer.Ordinal);

    /// <param name="carpeta">Donde van los archivos.</param>
    /// <param name="nombre">Con qué empieza cada archivo: «red-caja» o «red-servicio».</param>
    /// <param name="tope">Hasta cuánto crece el archivo de un día (lo que se pierde y vuelve se anota aunque pase de ahí).</param>
    public BitacoraDeRed(string carpeta, string nombre, TimeProvider? reloj = null, int dias = DiasPorOmision, long tope = RegistroDiario.TopePorOmision)
    {
        _reloj = reloj ?? TimeProvider.System;
        _registro = new RegistroDiario(carpeta, nombre, dias, tope, _reloj, reserva: Math.Min(RegistroDiario.ReservaPorOmision, tope / 4));
    }

    public string Carpeta => _registro.Carpeta;

    public string Nombre => _registro.Nombre;

    /// <summary>El archivo de hoy (hora de la PC).</summary>
    public string ArchivoDeHoy => _registro.ArchivoDeHoy;

    /// <summary>La hora de la PC, al segundo (como se anota).</summary>
    private DateTime Ahora
    {
        get
        {
            var ahora = _reloj.GetLocalNow().DateTime;
            return new DateTime(ahora.Ticks - ahora.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        }
    }

    /// <summary>
    /// Al abrir: quita los archivos de hace más de los días que se guardan, lee las caídas de los que quedan (lo que quedó caído la
    /// vez anterior se da por terminado en su última línea) y anota que empieza, con la hora en que arrancó la PC.
    /// </summary>
    /// <param name="quien">«esta caja», «la caja principal».</param>
    /// <param name="arranco">Cuándo arrancó la PC (hora de la PC); vacío si no se sabe.</param>
    public void Empezar(string quien, DateTime? arranco = null)
    {
        lock (_candado)
        {
            if (_empezada)
                return;
            _empezada = true;
            _registro.Limpiar();
            _limpiaEl = Hoy;
            _caidas.Clear();
            _caidas.AddRange(LeerCaidas(LineasGuardadas()).TakeLast(CaidasQueRecuerda));
            _abiertas.Clear();
        }
        var pc = arranco is { } a ? $" (la PC arrancó a las {a.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} del {a.ToString("dd/MM", CultureInfo.InvariantCulture)})" : "";
        _registro.AnotarSiempre($"{Empieza} de {quien}{pc}.");
    }

    /// <summary>Al cerrar bien: lo dice. Si la PC se apaga de golpe (o se va la luz), no hay esta línea: el hueco lo dice.</summary>
    public void Terminar(string porque) => _registro.AnotarSiempre($"{Termina}: {porque}.");

    /// <summary>Una línea con la fecha y la hora (si el archivo de hoy llegó a su tope, puede no entrar: lo que importa va siempre).</summary>
    public void Anotar(string texto)
    {
        LimpiarSiCambioElDia();
        _registro.Anotar(texto);
    }

    /// <summary>
    /// Lo que puede repetirse sin parar (una caja que deja de latir y vuelve en un Wi-Fi malo): la primera vez en el periodo del
    /// resumen se anota; las demás se cuentan y el resumen dice cuántas (<see cref="SacarContados"/>).
    /// </summary>
    /// <param name="que">Cómo se dice en el resumen: ««Caja 2» dejó de latir».</param>
    public void AnotarOContar(string que, string texto)
    {
        lock (_candado)
        {
            if (_contados.TryGetValue(que, out var veces))
            {
                _contados[que] = veces + 1;
                return;
            }
            _contados[que] = 0;
        }
        Anotar(texto);
    }

    /// <summary>Lo que se repitió desde el último resumen, con cuántas veces más (y empieza otro periodo).</summary>
    public IReadOnlyList<(string Que, int Veces)> SacarContados()
    {
        lock (_candado)
        {
            var repetidos = _contados.Where(c => c.Value > 0).Select(c => (c.Key, c.Value)).ToList();
            _contados.Clear();
            return repetidos;
        }
    }

    private DateOnly Hoy => DateOnly.FromDateTime(_reloj.GetLocalNow().DateTime);

    /// <summary>Al cambiar el día (el servicio no se cierra en días): quita los archivos de hace más de los días que se guardan.</summary>
    private void LimpiarSiCambioElDia()
    {
        var hoy = Hoy;
        lock (_candado)
        {
            if (_limpiaEl == hoy)
                return;
            _limpiaEl = hoy;
        }
        _registro.Limpiar();
    }

    /// <summary>
    /// Se perdió algo (internet, la caja principal, el router…): se anota solo si no estaba ya perdido. Dice si lo anotó.
    /// </summary>
    public bool SePerdio(string que, string detalle)
    {
        var ahora = Ahora;
        lock (_candado)
        {
            if (!_abiertas.TryAdd(que, ahora))
                return false;
            _caidas.Add(new CaidaDeRed(que, ahora, null));
            if (_caidas.Count > CaidasQueRecuerda)
                _caidas.RemoveAt(0);
        }
        // Siempre: aunque el archivo de hoy haya llegado a su tope (un día de Wi-Fi malo), la caída de verdad se anota.
        LimpiarSiCambioElDia();
        _registro.AnotarSiempre($"Se perdió «{que}»: {detalle}");
        return true;
    }

    /// <summary>Volvió lo que se había perdido, con cuánto duró: se anota solo si estaba perdido. Dice si lo anotó.</summary>
    public bool Volvio(string que, string detalle)
    {
        var ahora = Ahora;
        DateTime desde;
        lock (_candado)
        {
            if (!_abiertas.Remove(que, out desde))
                return false;
            var i = _caidas.FindLastIndex(c => c.Que == que && c.Hasta is null);
            if (i >= 0)
                _caidas[i] = _caidas[i] with { Hasta = ahora };
        }
        LimpiarSiCambioElDia();
        _registro.AnotarSiempre($"Volvió «{que}» tras {Textos.Duracion(ahora - desde)} (se perdió a las {desde.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}): {detalle}");
        return true;
    }

    /// <summary>Si eso está perdido ahora.</summary>
    public bool Perdido(string que)
    {
        lock (_candado)
            return _abiertas.ContainsKey(que);
    }

    /// <summary>Las últimas caídas, la más reciente primero (también las de los días anteriores que todavía se guardan).</summary>
    public IReadOnlyList<CaidaDeRed> Caidas(int cuantas = 8)
    {
        lock (_candado)
            return _caidas.AsEnumerable().Reverse().Take(cuantas).ToList();
    }

    private IEnumerable<string> LineasGuardadas()
    {
        List<string> archivos;
        try
        {
            if (!Directory.Exists(Carpeta))
                return [];
            // red-caja-2026-10-17.log: el nombre lleva la fecha, así que en orden de nombre es en orden de días.
            archivos = Directory.EnumerateFiles(Carpeta, $"{Nombre}-????-??-??.log").Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return archivos.SelectMany(LeerArchivo);
    }

    private static IEnumerable<string> LeerArchivo(string ruta)
    {
        try
        {
            using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lector = new StreamReader(flujo);
            var lineas = new List<string>();
            while (lector.ReadLine() is { } linea)
                lineas.Add(linea);
            return lineas;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Las caídas de unas líneas de la bitácora, en orden: cada «Se perdió «…»» con su «Volvió «…»». Lo que quedó abierto al
    /// empezar otra vez («Empieza la bitácora de red») o al final se da por terminado en la última línea antes de eso
    /// (<see cref="CaidaDeRed.FinAproximado"/>): no se vio volver. Las líneas que no son de la bitácora se saltan.
    /// </summary>
    public static List<CaidaDeRed> LeerCaidas(IEnumerable<string> lineas)
    {
        var caidas = new List<CaidaDeRed>();
        var abiertas = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTime? ultima = null;
        foreach (var linea in lineas)
        {
            var m = LineaDeLaBitacora().Match(linea);
            if (!m.Success || !DateTime.TryParseExact(m.Groups["cuando"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cuando))
                continue;
            var texto = m.Groups["texto"].Value;
            if (texto.StartsWith(Empieza, StringComparison.Ordinal))
                Cerrar(ultima);
            else if (CambioDeLaRed().Match(texto) is { Success: true } cambio)
            {
                var que = cambio.Groups["que"].Value;
                if (cambio.Groups["como"].Value == "Se perdió")
                {
                    if (!abiertas.ContainsKey(que))
                    {
                        abiertas[que] = caidas.Count;
                        caidas.Add(new CaidaDeRed(que, cuando, null));
                    }
                }
                else if (abiertas.Remove(que, out var i))
                    caidas[i] = caidas[i] with { Hasta = cuando };
            }
            ultima = cuando;
        }
        Cerrar(ultima);
        return caidas;

        void Cerrar(DateTime? hasta)
        {
            foreach (var i in abiertas.Values)
                caidas[i] = caidas[i] with { Hasta = hasta ?? caidas[i].Desde, FinAproximado = true };
            abiertas.Clear();
        }
    }

    [GeneratedRegex(@"^(?<cuando>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})  (?<texto>.*)$")]
    private static partial Regex LineaDeLaBitacora();

    [GeneratedRegex(@"^(?<como>Se perdió|Volvió) «(?<que>[^»]+)»")]
    private static partial Regex CambioDeLaRed();
}
