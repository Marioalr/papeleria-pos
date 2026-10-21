using System.Diagnostics;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Un renglón de una venta como quedó cobrado: lo que sale del inventario, en milésimas de la unidad base.</summary>
/// <param name="Cantidad">En presentaciones (2 paquetes), como en el ticket.</param>
public sealed record RenglonVendido(Guid? VarianteId, string Descripcion, long Cantidad, long CantidadBase, bool LlevaInventario);

/// <summary>Una venta que cobró una caja simulada, como se la contestaron (en línea) o como la cobró con su copia (sin conexión).</summary>
public sealed class VentaSimulada
{
    public required int Caja { get; init; }
    public required Guid VentaId { get; init; }

    /// <summary>«SA-000012».</summary>
    public required string Folio { get; init; }

    public required int Numero { get; init; }
    public required long TotalCentavos { get; init; }

    /// <summary>Lo que entró al cajón en efectivo (lo que se recibió menos el cambio).</summary>
    public required long EfectivoCentavos { get; init; }

    public required long TarjetaCentavos { get; init; }
    public required long TransferenciaCentavos { get; init; }

    /// <summary>La cobró la caja principal; si no, la caja con su copia (y subió con la bandeja).</summary>
    public required bool EnLinea { get; init; }

    /// <summary>Se mandó otra vez con el mismo Id (no se sabía si había quedado) y ya estaba: es la de antes.</summary>
    public required bool YaCobrada { get; init; }

    public required IReadOnlyList<RenglonVendido> Renglones { get; init; }

    /// <summary>Cuándo se cobró, desde que empezó el día.</summary>
    public required TimeSpan Cuando { get; init; }

    /// <summary>Se va a cancelar (el cliente se arrepintió): nadie la elige para una devolución.</summary>
    public volatile bool PorCancelar;

    public volatile bool Cancelada;

    /// <summary>Ya se eligió para una devolución (una por venta).</summary>
    public volatile bool Devuelta;
}

/// <summary>Una devolución o una cancelación, en la caja que la hizo (de ahí sale el reembolso).</summary>
/// <param name="Renglones">Lo que regresa al inventario (si llegó dañado, entra y sale como merma: no cambia la existencia).</param>
public sealed record DevolucionSimulada(
    int Caja, Guid VentaId, string Folio, bool EsCancelacion, long ReembolsoEfectivoCentavos,
    IReadOnlyList<(Guid VarianteId, long CantidadBase, bool Regresa)> Renglones);

public sealed record MovimientoSimulado(TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, bool EnLinea);

/// <summary>La red de una caja cambió de estado (buena, lenta, caída), según la caja.</summary>
public sealed record CambioDeRed(TimeSpan Cuando, EstadoRed Estado);

/// <summary>La caída de la red de una caja: cuándo empezó y terminó, y cuándo lo notó y se puso al día la caja.</summary>
public sealed class CaidaSimulada
{
    public required int Caja { get; init; }
    public required TimeSpan Inicio { get; init; }
    public TimeSpan? Fin { get; set; }

    /// <summary>Cuando la caja dejó de trabajar con la caja principal (su primer cobro o latido que no llegó).</summary>
    public TimeSpan? LoNoto { get; set; }

    /// <summary>Cuando, ya de vuelta, la caja subió lo que hizo sin conexión y trabaja otra vez con la caja principal.</summary>
    public TimeSpan? AlDia { get; set; }

    public int VentasSinConexion { get; set; }

    /// <summary>La caída duraba más que el día de esa caja: terminó con él.</summary>
    public bool TerminoConElDia { get; set; }
}

/// <summary>
/// Lo que comparten las cajas en el día simulado: el reloj del día, las ventas de todas (de ahí sale lo que se devuelve en
/// otra caja), el producto que da de alta la caja 1 y lo que hace el dueño. También lo que se anota para el informe.
/// </summary>
public sealed class DiaSimulado(OpcionesSimulador opciones)
{
    private readonly Lock _candado = new();
    private readonly List<VentaSimulada> _ventas = [];
    private readonly List<(TimeSpan Cuando, string Que)> _sucesos = [];
    private long _inicio;

    public OpcionesSimulador Opciones => opciones;

    /// <summary>En cuánto tiempo se vende el día (cero: sin esperas).</summary>
    public TimeSpan Duracion { get; } = TimeSpan.FromMinutes(opciones.Minutos);

    public void Empezar() => _inicio = Stopwatch.GetTimestamp();

    /// <summary>Desde que empezó el día (antes, cero).</summary>
    public TimeSpan Transcurrido => _inicio == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_inicio);

    /// <summary>Espera a que llegue ese punto del día (de 0 a 1). Si la caja va atrasada (la fila), no espera.</summary>
    public async Task EsperarAsync(double momento, CancellationToken ct)
    {
        if (Duracion == TimeSpan.Zero)
            return;
        var falta = Duracion * momento - Transcurrido;
        if (falta > TimeSpan.Zero)
            await Task.Delay(falta, ct);
    }

    /// <summary>El producto que da de alta la caja 1 a media jornada; vacío si no se pudo (sin conexión, se vendió como artículo común).</summary>
    public TaskCompletionSource<ProductoVentaDto?> ProductoNuevo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>El dueño cambia un precio (lo pone el simulador): la caja 1 lo pide a media venta de un ticket que lo lleva.</summary>
    public Func<Task> CambiarPrecioAsync { get; set; } = () => Task.CompletedTask;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource> _precioEnLaCopia = new();

    /// <summary>Se completa cuando el precio que cambió el dueño ya está en la copia de esa caja (llegó con el aviso).</summary>
    public TaskCompletionSource PrecioEnLaCopia(int caja) =>
        _precioEnLaCopia.GetOrAdd(caja, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Vendida(VentaSimulada venta)
    {
        lock (_candado)
            _ventas.Add(venta);
    }

    public IReadOnlyList<VentaSimulada> Ventas
    {
        get
        {
            lock (_candado)
                return _ventas.ToList();
        }
    }

    /// <summary>
    /// Un ticket de antes para devolver algo: sin cancelar ni por cancelar, sin otra devolución, con algo del inventario y, si
    /// hay, de otra caja (en la tienda se devuelve en la caja que esté libre). Queda apartado: otra caja no lo elige.
    /// </summary>
    public VentaSimulada? ParaDevolver(int caja, Random azar)
    {
        lock (_candado)
        {
            var posibles = _ventas
                .Where(v => v is { Cancelada: false, PorCancelar: false, Devuelta: false }
                            && v.Renglones.Any(r => r is { LlevaInventario: true, VarianteId: not null }))
                .ToList();
            var deOtra = posibles.Where(v => v.Caja != caja).ToList();
            var lista = deOtra.Count > 0 ? deOtra : posibles;
            if (lista.Count == 0)
                return null;
            var elegida = lista[azar.Next(lista.Count)];
            elegida.Devuelta = true;
            return elegida;
        }
    }

    /// <summary>Algo que pasó en el día, con la hora (desde que empezó), para el informe; también se dice en la consola.</summary>
    public void Anotar(string que)
    {
        lock (_candado)
            _sucesos.Add((Transcurrido, que));
        Avisar(que);
    }

    /// <summary>A dónde se dice cómo va (la consola); nada en la prueba automática.</summary>
    public TextWriter Salida { get; init; } = TextWriter.Null;

    /// <summary>Cómo va, en la consola, con la hora del día simulado.</summary>
    public void Avisar(string que)
    {
        var cuando = Transcurrido;
        lock (_consola)
            Salida.WriteLine($"[{(int)cuando.TotalMinutes:00}:{cuando.Seconds:00}] {que}");
    }

    private readonly Lock _consola = new();

    public IReadOnlyList<(TimeSpan Cuando, string Que)> Sucesos
    {
        get
        {
            lock (_candado)
                return _sucesos.OrderBy(s => s.Cuando).ToList();
        }
    }
}
