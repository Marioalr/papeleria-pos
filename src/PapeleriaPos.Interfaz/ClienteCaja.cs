using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Error que se le muestra al cajero tal cual (viene del servicio o de la conexión).</summary>
public sealed class ErrorCaja(string mensaje) : Exception(mensaje);

/// <summary>Qué caja es esta PC. Vacío: la primera caja de la sucursal (útil en pruebas).</summary>
public sealed class OpcionesCaja
{
    public Guid? CajaId { get; set; }
}

/// <summary>Habla con el servicio de la sucursal. La caja no toca la base de datos: todo pasa por aquí.</summary>
public sealed class ClienteCaja(HttpClient http)
{
    public Task<List<CajaDto>> CajasAsync() => GetAsync<List<CajaDto>>("api/cajas")!;

    public Task<SesionDto> EntrarAsync(string usuario, string pin) => PostAsync<SesionDto>("api/sesion", new SolicitudEntrada(usuario, pin));

    public Task<TurnoDto?> TurnoAsync(Guid cajaId) => GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true);

    public Task<TurnoDto> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondo) =>
        PostAsync<TurnoDto>("api/turnos", new SolicitudTurno(cajaId, cajeroId, fondo));

    public Task MovimientoAsync(Guid turnoId, SolicitudMovimiento movimiento) => PostAsync<object>($"api/turnos/{turnoId}/movimientos", movimiento, sinRespuesta: true);

    public Task<CorteDto> CorteXAsync(Guid turnoId, Guid usuarioId) => PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-x?usuarioId={usuarioId}", null);

    public Task<CorteDto> CorteZAsync(Guid turnoId, SolicitudCorteZ solicitud) => PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-z", solicitud);

    public Task<ProductoVentaDto?> PorCodigoAsync(string codigo, Guid sucursalId) =>
        GetAsync<ProductoVentaDto>($"api/productos/codigo/{Uri.EscapeDataString(codigo)}?sucursalId={sucursalId}", noEncontradoEsNulo: true);

    public Task<ProductoVentaDto?> PorVarianteAsync(Guid varianteId, Guid sucursalId) =>
        GetAsync<ProductoVentaDto>($"api/productos/{varianteId}?sucursalId={sucursalId}", noEncontradoEsNulo: true);

    public async Task<List<ResultadoBusquedaDto>> BuscarAsync(string texto, Guid sucursalId) =>
        await GetAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}") ?? [];

    public Task<TicketDto> CobrarAsync(SolicitudCobro solicitud) => PostAsync<TicketDto>("api/ventas", solicitud);

    public async Task<List<VentaDelDiaDto>> VentasDelDiaAsync(Guid cajaId) =>
        await GetAsync<List<VentaDelDiaDto>>($"api/cajas/{cajaId}/ventas-del-dia") ?? [];

    public Task<TicketDto?> TicketAsync(Guid ventaId) => GetAsync<TicketDto>($"api/ventas/{ventaId}/ticket");

    public Task<TicketDto?> UltimoTicketAsync(Guid cajaId) => GetAsync<TicketDto>($"api/cajas/{cajaId}/ultimo-ticket");

    private async Task<T?> GetAsync<T>(string url, bool nadaEsNulo = false, bool noEncontradoEsNulo = false)
    {
        using var respuesta = await EnviarAsync(() => http.GetAsync(url));
        if ((nadaEsNulo && respuesta.StatusCode == HttpStatusCode.NoContent) || (noEncontradoEsNulo && respuesta.StatusCode == HttpStatusCode.NotFound))
            return default;
        await RevisarAsync(respuesta);
        return await respuesta.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T> PostAsync<T>(string url, object? cuerpo, bool sinRespuesta = false)
    {
        using var respuesta = await EnviarAsync(() => http.PostAsJsonAsync(url, cuerpo));
        await RevisarAsync(respuesta);
        return sinRespuesta ? default! : (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(Func<Task<HttpResponseMessage>> envio)
    {
        try
        {
            return await envio();
        }
        catch (HttpRequestException)
        {
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal.");
        }
    }

    private static async Task RevisarAsync(HttpResponseMessage respuesta)
    {
        if (respuesta.IsSuccessStatusCode)
            return;
        ErrorDto? error = null;
        try
        {
            error = await respuesta.Content.ReadFromJsonAsync<ErrorDto>();
        }
        catch (Exception)
        {
            // Sin mensaje del servicio: se usa el genérico.
        }
        throw new ErrorCaja(error?.Mensaje ?? $"El servicio respondió con un error ({(int)respuesta.StatusCode}).");
    }
}

public static class Formato
{
    public static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static string Dinero(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    public static string Numero(decimal valor) => valor.ToString("#,##0.###", Mexico);

    /// <summary>"$1,234.50", "1234.5" o "1234" a centavos. Vacío o inválido: null.</summary>
    public static long? LeerPesos(string? texto)
    {
        var limpio = (texto ?? "").Replace("$", "").Replace(",", "").Trim();
        return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var pesos) && pesos >= 0
            ? (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero)
            : null;
    }
}
