using System.Globalization;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Quién entra en la caja simulada: su usuario y su PIN (los de la base de prueba, <c>preparar-prueba</c>).</summary>
public sealed record Credencial(string Usuario, string Pin)
{
    /// <summary>«usuario:pin»; vacío si no se entiende.</summary>
    public static Credencial? Leer(string? texto) =>
        texto?.Split(':', 2) is [var usuario, var pin] && usuario.Trim().Length > 0 && pin.Trim().Length > 0 ? new(usuario.Trim(), pin.Trim()) : null;
}

/// <summary>
/// La caída de la red de una caja a media jornada (<c>--caida 30s@50%</c>): desde qué punto del día y cuánto dura, en tiempo
/// («30s», «2m», «1m30s») o en tickets de esa caja («5t»). En tickets no depende de lo que tarda la PC: así la usa la prueba
/// automática. Empieza en el primer ticket de esa caja que cae en ese punto del día.
/// </summary>
/// <param name="Inicio">De 0 a 1: en qué punto del día.</param>
/// <param name="Caja">Qué caja (1, 2…); vacío: la última, la que en la tienda va por Wi-Fi.</param>
public sealed partial record CaidaDeRed(double Inicio, TimeSpan? Duracion = null, int? Tickets = null, int? Caja = null)
{
    public static CaidaDeRed? Leer(string texto, out string? error)
    {
        error = null;
        var m = Forma().Match(texto.Trim());
        if (!m.Success)
        {
            error = $"No entiendo la caída «{texto}»: va como «30s@50%» (30 segundos a la mitad del día), «2m@40%» o «5t@50%» (5 tickets).";
            return null;
        }
        var inicio = double.Parse(m.Groups["inicio"].Value, CultureInfo.InvariantCulture) / 100;
        if (inicio is < 0 or >= 1)
        {
            error = "La caída empieza entre el 0 % y el 99 % del día.";
            return null;
        }
        if (m.Groups["tickets"].Success)
        {
            var tickets = int.Parse(m.Groups["tickets"].Value, CultureInfo.InvariantCulture);
            if (tickets > 0)
                return new CaidaDeRed(inicio, Tickets: tickets);
            error = "La caída dura al menos un ticket.";
            return null;
        }
        var minutos = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
        var segundos = m.Groups["seg"].Success ? int.Parse(m.Groups["seg"].Value, CultureInfo.InvariantCulture) : 0;
        var duracion = TimeSpan.FromMinutes(minutos) + TimeSpan.FromSeconds(segundos);
        if (duracion > TimeSpan.Zero)
            return new CaidaDeRed(inicio, duracion);
        error = "La caída dura algo: «30s», «2m», «1m30s» o «5t».";
        return null;
    }

    [GeneratedRegex(@"^(?:(?<tickets>\d+)t|(?:(?<min>\d+)m)?(?:(?<seg>\d+)s)?)@(?<inicio>\d+(?:\.\d+)?)%$", RegexOptions.IgnoreCase)]
    private static partial Regex Forma();

    /// <summary>«30 s», «2 min», «5 tickets».</summary>
    public string Cuanto => Tickets is { } t
        ? $"{t} {(t == 1 ? "ticket" : "tickets")}"
        : Duracion!.Value.TotalSeconds < 60 ? $"{Duracion.Value.TotalSeconds:0} s"
        : Duracion.Value.Seconds == 0 ? $"{Duracion.Value.TotalMinutes:0} min" : $"{(int)Duracion.Value.TotalMinutes} min {Duracion.Value.Seconds} s";
}

/// <summary>
/// Cómo se simula el día pico (docs/prueba-sucursales-linux.md, «Simulador de cajas»): cuántas cajas, cuántos tickets cada una,
/// en cuánto tiempo, y si la red de una se cae a media jornada.
/// </summary>
public sealed record OpcionesSimulador
{
    public int Cajas { get; init; } = 2;

    /// <summary>Tickets de cada caja.</summary>
    public int Tickets { get; init; } = 120;

    /// <summary>En cuánto tiempo se vende el día (comprimido). Con 0, sin esperas: cada caja vende lo más rápido que puede.</summary>
    public double Minutos { get; init; } = 10;

    public CaidaDeRed? Caida { get; init; }

    /// <summary>Con la misma semilla y la misma base, el mismo día: los mismos tickets, productos, pagos y devoluciones.</summary>
    public int Semilla { get; init; } = 2026;

    /// <summary>Con «dar de alta y quitar cajas»: da de alta las cajas simuladas y cambia un precio a media jornada.</summary>
    public Credencial Dueno { get; init; } = new("dueno", "3333");

    /// <summary>Quien vende en cada caja simulada.</summary>
    public Credencial Cajero { get; init; } = new("cajero", "1111");

    /// <summary>Quien autoriza las cancelaciones y hace el corte de cada turno.</summary>
    public Credencial Encargado { get; init; } = new("encargado", "2222");

    public long FondoCentavos { get; init; } = 50_000;

    /// <summary>Dónde se corre, para el informe: «PC de desarrollo», «caja principal de la tienda».</summary>
    public string Donde { get; init; } = Environment.MachineName;

    /// <summary>Lo más que se espera a que algo se asiente: que suba la bandeja, que la copia se ponga al día.</summary>
    public TimeSpan Espera { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>En qué punto del día el dueño cambia un precio: a media venta de un ticket de la caja 1 que lo lleva.</summary>
    public double CambioDePrecio { get; init; } = 0.4;

    /// <summary>En qué punto del día la caja 1 da de alta un producto que no existía; la última caja lo vende poco después.</summary>
    public double ProductoNuevo { get; init; } = 0.6;

    /// <summary>
    /// Si quien atiende hace las pausas de una persona (entre escaneos, al teclear, al contar el dinero). Con
    /// <see cref="Minutos"/> en 0, no: cada caja vende lo más rápido que puede.
    /// </summary>
    public bool ConPausas => Minutos > 0;

    /// <summary>Lee las opciones de <c>simular-cajas</c>; lo que falta, como viene por omisión.</summary>
    public static OpcionesSimulador? Leer(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var opciones = new OpcionesSimulador();
        string? Opcion(string nombre)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i] == nombre)
                    return args[i + 1];
            return null;
        }
        int? Entero(string nombre, int minimo, int maximo, ref string? falla)
        {
            if (Opcion(nombre) is not { } texto)
                return null;
            if (int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= minimo && n <= maximo)
                return n;
            falla ??= $"{nombre} va de {minimo} a {maximo}.";
            return null;
        }
        Credencial? Quien(string nombre, ref string? falla)
        {
            if (Opcion(nombre) is not { } texto)
                return null;
            if (Credencial.Leer(texto) is { } credencial)
                return credencial;
            falla ??= $"{nombre} va como usuario:PIN.";
            return null;
        }

        string? falla = null;
        if (Entero("--cajas", 1, 26, ref falla) is { } cajas)
            opciones = opciones with { Cajas = cajas };
        if (Entero("--tickets", 1, 100_000, ref falla) is { } tickets)
            opciones = opciones with { Tickets = tickets };
        if (Entero("--semilla", int.MinValue, int.MaxValue, ref falla) is { } semilla)
            opciones = opciones with { Semilla = semilla };
        if (Opcion("--minutos") is { } minutos)
        {
            if (double.TryParse(minutos, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) && m is >= 0 and <= 24 * 60)
                opciones = opciones with { Minutos = m };
            else
                falla ??= "--minutos va de 0 (sin esperas) a 1440.";
        }
        if (Opcion("--caida") is { } caida)
        {
            if (CaidaDeRed.Leer(caida, out var errorCaida) is { } leida)
                opciones = opciones with { Caida = leida };
            else
                falla ??= errorCaida;
        }
        if (Entero("--caida-caja", 1, 26, ref falla) is { } enCaja)
        {
            if (opciones.Caida is null)
                falla ??= "--caida-caja va con --caida.";
            else
                opciones = opciones with { Caida = opciones.Caida with { Caja = enCaja } };
        }
        if (opciones.Caida?.Caja is { } cual && cual > opciones.Cajas)
            falla ??= $"La caída es de la caja {cual}, pero se simulan {opciones.Cajas}.";
        if (Quien("--dueno", ref falla) is { } dueno)
            opciones = opciones with { Dueno = dueno };
        if (Quien("--cajero", ref falla) is { } cajero)
            opciones = opciones with { Cajero = cajero };
        if (Quien("--encargado", ref falla) is { } encargado)
            opciones = opciones with { Encargado = encargado };
        if (Opcion("--donde") is { Length: > 0 } donde)
            opciones = opciones with { Donde = donde.Trim() };
        error = falla;
        return falla is null ? opciones : null;
    }

    /// <summary>La caja de la caída: la que se pidió o la última.</summary>
    public int CajaDeLaCaida => Caida?.Caja ?? Cajas;
}
