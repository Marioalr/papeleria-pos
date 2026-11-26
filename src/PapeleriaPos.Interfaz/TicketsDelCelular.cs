using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Por dónde la caja recoge los tickets que le mandan los celulares (la preventa, docs/celular-como-caja.md, «Mandar el ticket a
/// una caja»): la red de fondo de la caja (la del latido y la copia), no la del cajero. Sin esto (el navegador, o las pruebas que
/// no lo piden), la venta no pregunta y la caja no sale como «lista para recibir» en el celular.
/// </summary>
/// <param name="impresora">
/// La impresora de tickets de esta caja como está en caja.json («tcp://ip:9100», el nombre de una de Windows, o vacía): se le dice a la
/// caja principal al preguntar qué imprimir de lo que cobró el celular (paso 11).
/// </param>
public sealed class CanalDelCelular(HttpClient fondo, ConexionCaja conexion, string? impresora = null)
{
    private readonly Transporte _http = new(fondo, conexion);

    /// <summary>
    /// Los tickets del celular que esta caja ya puso en la cola de su impresora: si la caja principal los da otra vez (se perdió la
    /// respuesta), no salen dos veces. Uno solo para toda la caja, aunque pregunte más de una pantalla (dos pestañas del navegador).
    /// </summary>
    private readonly HashSet<Guid> _yaEnLaCola = [];

    /// <summary>Si ese ticket todavía no estaba en la cola de la impresora de esta caja (y desde ahora cuenta como puesto).</summary>
    internal bool EsNuevo(Guid id)
    {
        lock (_yaEnLaCola)
            return _yaEnLaCola.Add(id);
    }

    /// <summary>
    /// Lo que cobró el celular y le toca imprimir a esta caja (su impresora es de Windows); al preguntar, dice qué impresora de tickets
    /// tiene: con una de red, la caja principal le manda directo y aquí no llega nada.
    /// </summary>
    public async Task<List<TicketPorImprimirDto>> PorImprimirAsync(Guid cajaId) =>
        await _http.GetAsync<List<TicketPorImprimirDto>>($"api/cajas/{cajaId}/por-imprimir?impresora={Uri.EscapeDataString(impresora?.Trim() ?? "")}",
            espera: TimeSpan.FromSeconds(3)) ?? [];

    /// <summary>Qué pasó con uno: salió de la impresora de esta caja, o no (y por qué).</summary>
    public Task ImpresoAsync(Guid cajaId, SolicitudTicketImpreso resultado) =>
        _http.PostAsync<object>($"api/cajas/{cajaId}/por-imprimir", resultado, sinRespuesta: true, espera: TimeSpan.FromSeconds(3));

    /// <summary>
    /// Empieza a imprimir lo que cobra el celular (pregunta cada <see cref="ImpresionesDelCelular.Cada"/>) mientras la ventana de la caja
    /// esté abierta, se venda o no; se detiene al desecharlo.
    /// </summary>
    public ImpresionesDelCelular Imprimir(Guid cajaId, ColaDeImpresion cola, TimeSpan? cada = null)
    {
        var impresiones = new ImpresionesDelCelular(this, cajaId, cola, cada);
        impresiones.Empezar();
        return impresiones;
    }

    /// <summary>Lo que espera a esta caja; dice cuántos esperan y quién atiende (el celular lo enseña al elegir la caja).</summary>
    public async Task<List<TicketRecibidoDto>> PendientesAsync(Guid cajaId, int esperan, Guid atiende) =>
        await _http.GetAsync<List<TicketRecibidoDto>>($"api/cajas/{cajaId}/recibidos?esperan={esperan}&atiende={atiende}", espera: TimeSpan.FromSeconds(3)) ?? [];

    /// <summary>Los que ya quedaron guardados en la espera de esta caja: el celular recibe su «Enviado».</summary>
    public Task GuardadosAsync(Guid cajaId, IReadOnlyList<Guid> ids) =>
        _http.PostAsync<object>($"api/cajas/{cajaId}/recibidos", new SolicitudRecibidos(ids), sinRespuesta: true, espera: TimeSpan.FromSeconds(3));

    /// <summary>Empieza a recoger (cada <see cref="TicketsDelCelular.Cada"/>) mientras la venta esté abierta; se detiene al desecharlo.</summary>
    public TicketsDelCelular Escuchar(Guid cajaId, Func<(int Esperan, Guid Atiende)> estado,
        Func<IReadOnlyList<TicketRecibidoDto>, Task<IReadOnlyList<Guid>>> alLlegar, TimeSpan? cada = null)
    {
        var escucha = new TicketsDelCelular(this, cajaId, estado, alLlegar, cada);
        escucha.Empezar();
        return escucha;
    }
}

/// <summary>
/// La venta abierta recoge lo que le mandan: pregunta cada <see cref="Cada"/>, la venta los pone en su espera y, ya guardados, se
/// le dice al servicio, que entonces le contesta al celular. Si algo falla (sin red, la caja principal ocupada), en la siguiente
/// vuelta; el mismo ticket dos veces no se duplica (la venta lo reconoce por su Id).
/// </summary>
/// <param name="estado">Cuántos esperan y quién atiende.</param>
/// <param name="alLlegar">La venta los pone en su espera y regresa los que ya quedaron guardados.</param>
public sealed class TicketsDelCelular(
    CanalDelCelular canal, Guid cajaId, Func<(int Esperan, Guid Atiende)> estado,
    Func<IReadOnlyList<TicketRecibidoDto>, Task<IReadOnlyList<Guid>>> alLlegar, TimeSpan? cada = null) : IAsyncDisposable
{
    public static readonly TimeSpan Cada = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _fin = new();
    private Task? _vueltas;

    internal void Empezar() => _vueltas ??= VueltasAsync(_fin.Token);

    private async Task VueltasAsync(CancellationToken ct)
    {
        var espera = cada ?? Cada;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await UnaVueltaAsync();
            }
            catch (Exception ex) when (ex is ErrorCaja or HttpRequestException or TaskCanceledException or InvalidOperationException
                                           or ObjectDisposedException)
            {
                // Sin red o la caja principal ocupada: en la siguiente vuelta.
            }
            try
            {
                await Task.Delay(espera, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Una pregunta: lo que llegó va a la espera y, ya guardado, se confirma.</summary>
    public async Task UnaVueltaAsync()
    {
        var (esperan, atiende) = estado();
        var llegaron = await canal.PendientesAsync(cajaId, esperan, atiende);
        if (llegaron.Count == 0)
            return;
        var guardados = await alLlegar(llegaron);
        if (guardados.Count > 0)
            await canal.GuardadosAsync(cajaId, guardados);
    }

    public async ValueTask DisposeAsync()
    {
        await _fin.CancelAsync();
        if (_vueltas is { } vueltas)
            await vueltas.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _fin.Dispose();
    }
}

/// <summary>
/// La ventana de la caja imprime lo que cobra el celular (docs/celular-como-caja.md, paso 11): pregunta cada <see cref="Cada"/> a la caja
/// principal, pone cada ticket en la cola de su impresora (<see cref="ColaDeImpresion"/>, sin abrir el cajón: nadie está dando cambio
/// ahí) y le dice si salió. Si no sale, se queda en la cola como cualquier papel («Reintentar» o «No imprimirlo», en la venta); si
/// después sale, también se dice. Sin impresora (se ve en pantalla) no imprime nada: lo dice.
/// </summary>
public sealed class ImpresionesDelCelular(CanalDelCelular canal, Guid cajaId, ColaDeImpresion cola, TimeSpan? cada = null) : IAsyncDisposable
{
    public static readonly TimeSpan Cada = TimeSpan.FromSeconds(2);

    /// <summary>Cuántas veces se intenta decirle a la caja principal qué pasó (el Wi-Fi), y cada cuánto se mira si lo que no salió ya salió.</summary>
    private const int IntentosDeDecir = 5;
    private static readonly TimeSpan CadaMirada = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _fin = new();
    private Task? _vueltas;

    internal void Empezar() => _vueltas ??= VueltasAsync(_fin.Token);

    private async Task VueltasAsync(CancellationToken ct)
    {
        var espera = cada ?? Cada;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await UnaVueltaAsync();
            }
            catch (Exception ex) when (ex is ErrorCaja or HttpRequestException or TaskCanceledException or InvalidOperationException
                                           or ObjectDisposedException)
            {
                // Sin red o la caja principal ocupada: en la siguiente vuelta.
            }
            try
            {
                await Task.Delay(espera, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Una pregunta: lo que llegó va a la cola de la impresora, y cada uno dice después si salió.</summary>
    public async Task UnaVueltaAsync()
    {
        foreach (var ticket in await canal.PorImprimirAsync(cajaId))
        {
            if (!canal.EsNuevo(ticket.Id))
                continue;
            if (cola.MuestraVistaPrevia)
            {
                _ = DecirAsync(ticket.Id, false, "Esta caja no tiene impresora de tickets.");
                continue;
            }
            var trabajo = cola.Encolar($"Ticket {ticket.Ticket.FolioImpreso}", DisenoTicket.Armar(ticket.Ticket), abrirCajon: false);
            _ = SeguirAsync(ticket.Id, trabajo);
        }
    }

    private async Task SeguirAsync(Guid id, TrabajoDeImpresion trabajo)
    {
        try
        {
            var salio = await trabajo.Resultado.WaitAsync(_fin.Token);
            await DecirAsync(id, salio, trabajo.Motivo);
            // No salió: sigue en la cola de la caja. Si sale con «Reintentar», la venta deja de estar «sin imprimir».
            while (!salio && trabajo.Estado != EstadoImpresion.SinImprimir)
            {
                await Task.Delay(CadaMirada, _fin.Token);
                if (trabajo.Estado == EstadoImpresion.Impreso)
                {
                    salio = true;
                    await DecirAsync(id, true, null);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Se cerró la caja.
        }
    }

    private async Task DecirAsync(Guid id, bool salio, string? motivo)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await canal.ImpresoAsync(cajaId, new SolicitudTicketImpreso(id, salio, motivo));
                return;
            }
            catch (Exception ex) when (intento < IntentosDeDecir && ex is ErrorCaja or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                try
                {
                    await Task.Delay(Cada, _fin.Token);
                }
                catch (Exception alCerrar) when (alCerrar is OperationCanceledException or ObjectDisposedException)
                {
                    // Se cerró la caja.
                    return;
                }
            }
            catch (Exception ex) when (ex is ErrorCaja or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // La caja principal no se enteró: la venta sigue «sin imprimir» allá; su primera impresión no pide motivo.
                return;
            }
        }
    }

    private int _cerrado;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _cerrado, 1) != 0)
            return;
        await _fin.CancelAsync();
        if (_vueltas is { } vueltas)
            await vueltas.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _fin.Dispose();
    }
}
