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
}
