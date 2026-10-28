using System.Threading.Channels;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Entrega en la pantalla los códigos del lector por puerto (<see cref="LectorPorPuerto"/>), uno tras otro y en su orden, como un
/// escaneo del teclado: caja.js (<c>escaneoExterno</c>) lo pone donde está el cursor por el mismo camino que una ráfaga del lector
/// reconocida (el cuadro de captura, «Pagó con», «Varios», el mostrador, los campos para códigos de las páginas), con las mismas
/// protecciones. Dice a dónde fue cada uno (Configuración › Acerca de › Lector) y anota en el registro lo que no se usó.
/// <para>
/// Lo que el puerto leyó con la ventana de la caja detrás de otra (eleventa al frente, o un aviso de la propia caja) no se usa: con
/// el lector como teclado, tampoco le llegaría a la caja. Cuenta la ventana al frente cuando se leyó, no cuando se entrega. Se anota.
/// </para>
/// <para>
/// Cada código va con su número: caja.js le dice a la venta que lo recibió (<c>LectorRecibido</c>) después de dárselo, y mientras
/// tanto lo que actúa sobre lo último escaneado (el cobro, F6, «5*») lo espera (<see cref="LectorPorPuerto.EsperarLoLeidoAsync"/>).
/// Lo que no va a la cola de la venta (otro campo, una página, no se usó) deja de esperarse aquí.
/// </para>
/// </summary>
public sealed class EntregaDelLector : IAsyncDisposable
{
    private readonly LectorPorPuerto _lector;
    private readonly Func<string, long, Task<string?>> _aLaPagina;
    private readonly RegistroDiario? _registro;
    private readonly Channel<Lectura> _cola = Channel.CreateUnbounded<Lectura>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _entregando;

    /// <param name="aLaPagina">
    /// Le da el código (con su número) a caja.js y regresa lo que hizo con él («captura», «cobro», «sin-lugar:Motivo»…; ver
    /// <see cref="Describir"/>).
    /// </param>
    public EntregaDelLector(LectorPorPuerto lector, Func<string, long, Task<string?>> aLaPagina, RegistroDiario? registro = null)
    {
        _lector = lector;
        _aLaPagina = aLaPagina;
        _registro = registro;
        lector.Codigo += AlLeer;
    }

    private void AlLeer(Lectura lectura) => _cola.Writer.TryWrite(lectura);

    /// <summary>
    /// Empieza a entregar. En la ventana de Windows se llama desde su hilo: ahí se le habla a la página, y ahí regresa cada entrega.
    /// </summary>
    public void Iniciar() => _entregando ??= EntregarTodoAsync();

    private async Task EntregarTodoAsync()
    {
        await foreach (var lectura in _cola.Reader.ReadAllAsync())
        {
            try
            {
                await EntregarAsync(lectura);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Lo que sea: los códigos que siguen se siguen entregando.
                _registro?.AnotarError("Lector: no se entregó un código", ex);
            }
        }
    }

    /// <summary>Uno: a la página, si se leyó con la caja al frente; dice a dónde fue.</summary>
    public async Task EntregarAsync(Lectura lectura)
    {
        string? resultado;
        if (!lectura.AlFrente)
            resultado = SegundoPlano;
        else
        {
            try
            {
                resultado = await _aLaPagina(lectura.Codigo!, lectura.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // La página se está volviendo a cargar, o el navegador se cayó: este código se pierde (lo dice el registro).
                _registro?.AnotarSinRepetir("Lector: la página no recibió un código", $"{ex.GetType().Name}: {ex.Message}");
                resultado = null;
            }
        }
        // Lo que va a la cola de la venta lo da por recibido la venta (caja.js se lo dice); lo demás, ya.
        if (!VaALaVenta(resultado))
            _lector.Recibido(lectura.Id);
        var (texto, usada) = Describir(resultado);
        if (!usada)
            _registro?.AnotarSinRepetir("Lector", $"un código no se usó: {texto["no se usó: ".Length..]}");
        _lector.Entregado(lectura, texto, usada);
    }

    /// <summary>Si caja.js se lo dio a la venta (el cuadro de captura, el cobro, «Varios» o el mostrador): ella dice cuándo lo recibió.</summary>
    public static bool VaALaVenta(string? resultado) =>
        (resultado?.Split(':', 2)[0]) is "captura" or "cobro" or "varios" or "mostrador";

    /// <summary>Lo que dice la entrega cuando la ventana de la caja no está al frente.</summary>
    public const string SegundoPlano = "segundo-plano";

    /// <summary>
    /// Lo que hizo caja.js con el código, dicho para Configuración › Acerca de › Lector, y si se usó. caja.js contesta una palabra y,
    /// a veces, detrás de «:», el campo («campo:Código del producto», «sin-lugar:Motivo»).
    /// </summary>
    public static (string Texto, bool Usada) Describir(string? resultado)
    {
        var (que, campo) = resultado?.Split(':', 2) is [var a, var b] ? (a, b.Trim()) : (resultado, "");
        var en = campo.Length > 0 ? $" «{campo}»" : "";
        return que switch
        {
            "captura" => ("a la venta (el cuadro de captura)", true),
            "escrito" => ("al cuadro de captura, con un panel abierto: se quedó escrito ahí", true),
            "cobro" => ("al ticket, desde el cobro (nunca como dinero)", true),
            "varios" => ("a «Varios» (Ins)", true),
            "mostrador" => ("al mostrador (Ctrl+M)", true),
            "numeros" => ($"a la página, como el siguiente producto{(en.Length > 0 ? $" (el cursor estaba en{en})" : "")}", true),
            "campo" => ($"al campo{en}", true),
            "busqueda" => ($"a la búsqueda de la página{en}", true),
            "protegido" => ($"no se usó: el cursor estaba en{(en.Length > 0 ? en : " «Efectivo contado»")}, donde un código no se toma como dinero", false),
            "sin-lugar" => (campo.Length > 0 ? $"no se usó: el cursor estaba en{en}, que no es para códigos" : "no se usó: no había dónde escribirlo", false),
            "sin-pantalla" or "sin-caja" => ("no se usó: la caja no estaba en la venta (la entrada, abrir el turno, el corte de otro turno)", false),
            "sin-pagina" => ("no se usó: la página que lo esperaba ya no estaba", false),
            SegundoPlano => ("no se usó: la ventana de la caja no estaba al frente cuando se leyó", false),
            _ => ("no se usó: la página no contestó", false),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _lector.Codigo -= AlLeer;
        _cola.Writer.TryComplete();
        if (_entregando is { } entregando)
            await entregando.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
