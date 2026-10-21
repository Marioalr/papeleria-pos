namespace PapeleriaPos.Interfaz;

/// <summary>Cómo está la red con la caja principal (entrega 5, parte 2).</summary>
public enum EstadoRed
{
    /// <summary>Contesta rápido: lo que necesita a la caja principal se hace con ella.</summary>
    Buena,

    /// <summary>Contesta, pero tarde: la caja trabaja como sin conexión (con su copia y la bandeja) hasta que esté buena.</summary>
    Lenta,

    /// <summary>No contesta: sin conexión.</summary>
    Caida,
}

/// <summary>
/// Las reglas con que se decide el estado de la red (<see cref="CalidadDeLaRed"/>).
/// </summary>
/// <param name="UmbralBuena">
/// Un latido que contesta en menos de esto es bueno (al volver a contestar después de caerse, vuelve de una vez); vacío: todo
/// el que contesta.
/// </param>
/// <param name="UmbralLenta">Uno que tarda esto o más es lento; vacío: ninguno.</param>
/// <param name="LentosParaBajar">Cuántos lentos seguidos para pasar de buena a lenta (uno suelto no basta).</param>
/// <param name="BuenosParaVolver">Cuántos seguidos de menos de <paramref name="UmbralVolver"/> para volver de lenta a buena.</param>
/// <param name="Inestable">
/// Si la red se cae o se pone lenta otra vez antes de esto desde la vez anterior, va y viene: al volver a contestar pasa por
/// lenta (y necesita <paramref name="BuenosParaVolver"/>) en lugar de volver de una vez.
/// </param>
/// <param name="UmbralVolver">
/// Para volver de lenta, uno que contesta en menos de esto cuenta (entrega 5, parte 2, revisión): bien abajo de lo lento, pero
/// no tanto como lo bueno; si no, una red que siempre contesta entre los dos umbrales se quedaría lenta para siempre después de
/// un tropiezo. Vacío: el de lo bueno.
/// </param>
public sealed record ReglasDeLaRed(
    TimeSpan? UmbralBuena, TimeSpan? UmbralLenta, int LentosParaBajar, int BuenosParaVolver, TimeSpan Inestable, TimeSpan? UmbralVolver = null)
{
    /// <summary>
    /// Las de la caja de verdad. En la red de la tienda un latido (una petición chica) tarda milisegundos; 100 ms ya es una
    /// red con problemas y 300 ms una que haría esperar cada cobro. Dos lentos seguidos (10 s) para bajar; para volver, tres
    /// seguidos de menos de 200 ms (15 s): un Wi-Fi regular (150 ms) vuelve después de un tropiezo, un pico suelto no la cambia,
    /// y no alterna a cada latido (para bajar otra vez hacen falta dos de 300 ms o más). Por medir en la caja 2 por Wi-Fi.
    /// </summary>
    public static readonly ReglasDeLaRed DeLaTienda = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), 2, 3, TimeSpan.FromMinutes(2),
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Solo si contesta o no, como antes de la entrega 5, parte 2: las pruebas de lo demás de la caja no dependen de lo que
    /// tarda la PC en contestar un latido (lo que no se haya cumplido su plazo sí la pone lenta).
    /// </summary>
    public static readonly ReglasDeLaRed SoloSiContesta = new(null, null, 2, 3, TimeSpan.Zero);
}

/// <summary>
/// El estado de la red con la caja principal: buena, lenta o caída (entrega 5, parte 2, docs/propuesta-interfaz.md,
/// «Rendimiento»). Lo decide el latido con lo que tarda en contestar, con histéresis: bajar es rápido (un latido que no
/// contesta, dos lentos seguidos, o algo que el cajero esperaba y no llegó a tiempo) y volver es lento (tres seguidos bien abajo
/// de lo lento), para no alternar a cada latido. Si la red va y viene, al volver a contestar pasa por lenta.
/// </summary>
public sealed class CalidadDeLaRed(ReglasDeLaRed reglas, TimeProvider reloj)
{
    private readonly Lock _candado = new();
    private int _lentos;
    private int _buenos;
    private DateTimeOffset? _ultimaBajada;
    private bool _vaYViene;

    public ReglasDeLaRed Reglas => reglas;

    public EstadoRed Estado { get; private set; } = EstadoRed.Buena;

    /// <summary>Lo que tardó en contestar el último latido; vacío si no contestó.</summary>
    public TimeSpan? Latencia { get; private set; }

    /// <summary>
    /// El latido contestó en <paramref name="latencia"/>. Con <paramref name="ocupada"/> (la propia caja baja su copia
    /// completa y llena la red), uno lento no cuenta. Dice si cambió el estado.
    /// </summary>
    public bool Contesto(TimeSpan latencia, bool ocupada = false)
    {
        lock (_candado)
        {
            var antes = Estado;
            Latencia = latencia;
            var lento = reglas.UmbralLenta is { } lenta && latencia >= lenta;
            var bueno = reglas.UmbralBuena is not { } buena || latencia < buena;
            var paraVolver = bueno || (reglas.UmbralVolver is { } volver && latencia < volver);
            if (lento && ocupada)
            {
                // No dice nada de la red de la tienda: lo deja como estaba.
                if (Estado == EstadoRed.Caida)
                    Estado = EstadoRed.Lenta;
            }
            else if (lento)
            {
                _buenos = 0;
                _lentos++;
                if (Estado == EstadoRed.Buena && _lentos >= reglas.LentosParaBajar)
                    Bajar(EstadoRed.Lenta);
                else if (Estado == EstadoRed.Caida)
                    Estado = EstadoRed.Lenta;
            }
            else if (paraVolver)
            {
                _lentos = 0;
                _buenos++;
                if (Estado == EstadoRed.Caida)
                    Estado = bueno && !_vaYViene ? EstadoRed.Buena : EstadoRed.Lenta;
                else if (Estado == EstadoRed.Lenta && _buenos >= reglas.BuenosParaVolver)
                    Estado = EstadoRed.Buena;
            }
            else
            {
                // Entre lo que cuenta para volver y lo lento: ni baja ni cuenta para volver.
                (_lentos, _buenos) = (0, 0);
                if (Estado == EstadoRed.Caida)
                    Estado = EstadoRed.Lenta;
            }
            return Estado != antes;
        }
    }

    /// <summary>El latido (o algo de fondo) no pudo conectarse: caída, de una vez. Dice si cambió el estado.</summary>
    public bool Fallo()
    {
        lock (_candado)
        {
            var antes = Estado;
            (Latencia, _lentos, _buenos) = (null, 0, 0);
            if (Estado == EstadoRed.Buena)
                Bajar(EstadoRed.Caida);
            else
                Estado = EstadoRed.Caida;
            return Estado != antes;
        }
    }

    /// <summary>
    /// Algo que el cajero esperaba no llegó a tiempo (pasó su plazo): lenta, de una vez. Ya esperó; que lo siguiente no lo
    /// haga esperar otra vez. Dice si cambió el estado.
    /// </summary>
    public bool PlazoVencido()
    {
        lock (_candado)
        {
            if (Estado != EstadoRed.Buena)
                return false;
            _buenos = 0;
            Bajar(EstadoRed.Lenta);
            return true;
        }
    }

    private void Bajar(EstadoRed a)
    {
        var ahora = reloj.GetUtcNow();
        _vaYViene = _ultimaBajada is { } anterior && ahora - anterior < reglas.Inestable;
        _ultimaBajada = ahora;
        (Estado, _lentos) = (a, 0);
    }
}
