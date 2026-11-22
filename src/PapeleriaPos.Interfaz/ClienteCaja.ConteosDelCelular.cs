using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Los conteos que mandan los celulares a esta caja (docs/celular-como-caja.md, «Mandar un conteo a una caja»): los que esperan, uno con
/// lo que hay ahora de cada producto, y aceptarlo o rechazarlo. Solo con conexión: sin ella esperan en la caja principal.
/// </summary>
public sealed partial class ClienteCaja
{
    /// <summary>
    /// Los que esperan aquí. Lo pregunta el aviso de la venta cada pocos segundos: directo, sin la contingencia, para que una pregunta de
    /// fondo que no contesta no ponga la caja «sin conexión».
    /// </summary>
    public async Task<List<ConteoRecibidoDto>> ConteosRecibidosAsync(Guid cajaId) =>
        await _http.GetAsync<List<ConteoRecibidoDto>>($"api/cajas/{cajaId}/conteos-recibidos", espera: TimeSpan.FromSeconds(5)) ?? [];

    public Task<ConteoPorRevisarDto> ConteoRecibidoAsync(Guid cajaId, Guid conteoId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ConteoPorRevisarDto>($"api/cajas/{cajaId}/conteos-recibidos/{conteoId}"))!, plazoDeLaVenta: false);

    public Task<EstadoConteoDto> AceptarConteoAsync(Guid cajaId, Guid conteoId, SolicitudAceptarConteo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<EstadoConteoDto>($"api/cajas/{cajaId}/conteos-recibidos/{conteoId}/aceptar", solicitud), plazoDeLaVenta: false);

    public Task<EstadoConteoDto> RechazarConteoAsync(Guid cajaId, Guid conteoId, SolicitudRechazarConteo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<EstadoConteoDto>($"api/cajas/{cajaId}/conteos-recibidos/{conteoId}/rechazar", solicitud), plazoDeLaVenta: false);
}
