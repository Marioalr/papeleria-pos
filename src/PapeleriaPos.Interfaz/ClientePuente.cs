using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Solo del piloto con eleventa (se quita al dejarlo, con <see cref="PilotoConEleventa"/>): lo que la caja 2 le pide al
/// puente de su servicio. Va aparte de <see cref="ClienteCaja"/>: nada de esto se hace sin conexión (necesita al servicio, y
/// el servicio a eleventa).
/// </summary>
public sealed class ClientePuente(HttpClient http, ConexionCaja conexion)
{
    private readonly Transporte _http = new(http, conexion);

    /// <summary>Los tickets con ese folio de eleventa (el impreso), para devolver: si hay más de uno, se elige por la hora y el total.</summary>
    public async Task<FolioEleventaDto> FolioAsync(int folio) => (await _http.GetAsync<FolioEleventaDto>($"api/eleventa/folio/{folio}"))!;

    /// <summary>Un ticket cobrado en eleventa, leído en vivo, como lo enseña la ventana de devoluciones (la ida).</summary>
    public async Task<VentaParaDevolverDto> TicketAsync(int ticket) => (await _http.GetAsync<VentaParaDevolverDto>($"api/eleventa/tickets/{ticket}"))!;

    /// <summary>Devolver aquí parte de un ticket cobrado en eleventa: sale de esta caja y va a eleventa por la bandeja.</summary>
    public Task<ComprobanteDevolucionDto> DevolverAsync(int ticket, SolicitudDevolucion solicitud) =>
        _http.PostAsync<ComprobanteDevolucionDto>($"api/eleventa/tickets/{ticket}/devolucion", solicitud);

    /// <summary>Lo que quedó por revisar con eleventa y lo que conviene saber, para la pantalla.</summary>
    public async Task<PuenteEleventaDto> PantallaAsync() => (await _http.GetAsync<PuenteEleventaDto>("api/eleventa/pantalla"))!;

    /// <summary>
    /// Lo que ya se anotó a mano en eleventa: deja de mandarse y de enseñarse. Lo hace quien tiene «Configuración de la tienda».
    /// Una devolución de aquí de un ticket que está en eleventa lleva cuál es su anotación allá.
    /// </summary>
    public Task AnotarAManoAsync(Guid envioId, SolicitudAnotadoAMano solicitud) =>
        _http.PostAsync<object>($"api/eleventa/envios/{envioId}/a-mano", solicitud, sinRespuesta: true);

    /// <summary>Las devoluciones de eleventa que pueden ser la anotación a mano de una devolución de aquí, para elegir la suya.</summary>
    public async Task<List<AnotacionEnEleventaDto>> AnotacionesAsync(Guid envioId) =>
        (await _http.GetAsync<List<AnotacionEnEleventaDto>>($"api/eleventa/envios/{envioId}/anotaciones"))!;

    /// <summary>
    /// Reanudar el puente después de que se detuvo porque eleventa se restauró: lo que se perdió allá queda por revisar.
    /// Regresa cómo quedó, para la pantalla.
    /// </summary>
    public Task<PuenteEleventaDto> ReanudarAsync(SolicitudReanudarPuente solicitud) => _http.PostAsync<PuenteEleventaDto>("api/eleventa/reanudar", solicitud);

    /// <summary>Una devolución de eleventa que no se pudo pasar a su venta, ya revisada.</summary>
    public Task VueltaRevisadaAsync(Guid vueltaId, SolicitudRevision solicitud) =>
        _http.PostAsync<object>($"api/eleventa/vuelta/{vueltaId}/revisada", solicitud, sinRespuesta: true);
}
