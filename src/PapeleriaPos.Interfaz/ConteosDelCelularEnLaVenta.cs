using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Por dónde la venta pregunta por los conteos que los celulares mandaron a esta caja (docs/celular-como-caja.md, «Mandar un conteo a
/// una caja»): la red de fondo de la caja, como <see cref="CanalDelCelular"/>, no la del cajero. Sin esto (el navegador, o las pruebas
/// que no lo piden), la venta no pregunta ni enseña el chip; los conteos se ven igual en Inventario › Conteos del celular.
/// </summary>
public sealed class ConteosDelCelularEnLaVenta(HttpClient fondo, ConexionCaja conexion, TimeSpan? cada = null)
{
    /// <summary>Cada cuánto pregunta la venta abierta (no corre prisa: la lista ya está guardada en la caja principal).</summary>
    public static readonly TimeSpan CadaPorDefecto = TimeSpan.FromSeconds(10);

    private readonly Transporte _http = new(fondo, conexion);

    public TimeSpan Cada { get; } = cada ?? CadaPorDefecto;

    public async Task<List<ConteoRecibidoDto>> EsperanAsync(Guid cajaId) =>
        await _http.GetAsync<List<ConteoRecibidoDto>>($"api/cajas/{cajaId}/conteos-recibidos", espera: TimeSpan.FromSeconds(5)) ?? [];
}
