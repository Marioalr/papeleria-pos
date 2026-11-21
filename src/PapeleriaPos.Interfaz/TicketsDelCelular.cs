using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Por dónde la caja recoge los tickets que le mandan los celulares (la preventa, docs/celular-como-caja.md, «Mandar el ticket a
/// una caja»): la red de fondo de la caja (la del latido y la copia), no la del cajero. Sin esto (el navegador, o las pruebas que
/// no lo piden), la venta no pregunta y la caja no sale como «lista para recibir» en el celular.
/// </summary>
public sealed class CanalDelCelular(HttpClient fondo, ConexionCaja conexion)
{
    private readonly Transporte _http = new(fondo, conexion);

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
