using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Error que se le muestra al cajero tal cual (viene del servicio o de la conexión).</summary>
public sealed class ErrorCaja(string mensaje, string? codigo = null) : Exception(mensaje)
{
    /// <summary>
    /// "requiere-autorizacion" o "no-permitido" cuando lo detiene una regla; "caja-sin-alta" si el servicio no
    /// reconoce la llave; "sin-conexion" si no contesta.
    /// </summary>
    public string? Codigo { get; } = codigo;
}

/// <summary>A qué servicio habla esta PC y como qué caja. La ventana de Windows la guarda al dar de alta la caja.</summary>
public sealed class ConexionCaja
{
    public required Uri Servicio { get; set; }

    /// <summary>Vacío: la primera caja de la sucursal (la caja del navegador en la misma PC del servicio).</summary>
    public Guid? CajaId { get; set; }

    public string? Llave { get; set; }

    /// <summary>La ventana de Windows se da de alta antes de vender; la del navegador, en la PC del servicio, no.</summary>
    public bool PideAlta { get; init; }

    /// <summary>Lo pone el anfitrión: dónde guardar la conexión después del alta.</summary>
    public Func<ConexionCaja, Task>? AlGuardar { get; init; }

    public bool Lista => !PideAlta || (CajaId is not null && Llave is not null);
}

/// <summary>Habla con el servicio de la sucursal. La caja no toca la base de datos: todo pasa por aquí.</summary>
public sealed class ClienteCaja(HttpClient http, ConexionCaja conexion)
{
    public const string EncabezadoLlave = "X-Caja-Llave";

    public Task<List<CajaDto>> CajasAsync() => GetAsync<List<CajaDto>>("api/cajas")!;

    public Task<SesionDto> EntrarAsync(string usuario, string pin) => PostAsync<SesionDto>("api/sesion", new SolicitudEntrada(usuario, pin));

    public Task<TurnoDto?> TurnoAsync(Guid cajaId) => GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true);

    public Task<TurnoDto> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondo) =>
        PostAsync<TurnoDto>("api/turnos", new SolicitudTurno(cajaId, cajeroId, fondo));

    public Task<ComprobanteMovimientoDto> MovimientoAsync(Guid turnoId, SolicitudMovimiento movimiento) =>
        PostAsync<ComprobanteMovimientoDto>($"api/turnos/{turnoId}/movimientos", movimiento);

    public Task<TicketDto> ReimprimirAsync(Guid ventaId, SolicitudReimpresion solicitud) =>
        PostAsync<TicketDto>($"api/ventas/{ventaId}/reimpresion", solicitud);

    public Task<EfectivoCajaDto?> EfectivoAsync(Guid turnoId) => GetAsync<EfectivoCajaDto>($"api/turnos/{turnoId}/efectivo");

    public Task BitacoraAsync(SolicitudBitacora solicitud) => PostAsync<object>("api/bitacora", solicitud, sinRespuesta: true);

    public Task<DecisionDto> PermisoAsync(SolicitudPermiso solicitud) => PostAsync<DecisionDto>("api/permisos", solicitud);

    public Task<AutorizacionDto> AutorizarAsync(SolicitudAutorizacion solicitud) => PostAsync<AutorizacionDto>("api/autorizaciones", solicitud);

    public Task<ConfiguracionSucursalDto?> ConfiguracionSucursalAsync(Guid sucursalId) =>
        GetAsync<ConfiguracionSucursalDto>($"api/sucursales/{sucursalId}/configuracion");

    public Task<ConfiguracionSucursalDto> GuardarConfiguracionSucursalAsync(Guid sucursalId, SolicitudConfiguracionSucursal solicitud) =>
        PutAsync<ConfiguracionSucursalDto>($"api/sucursales/{sucursalId}/configuracion", solicitud);

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

    public Task<ConfiguracionTicketDto?> ConfiguracionTicketAsync(Guid sucursalId) =>
        GetAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket");

    public Task<ConfiguracionTicketDto> GuardarTextosTicketAsync(Guid sucursalId, SolicitudTextosTicket solicitud) =>
        PutAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket/textos", solicitud);

    /// <summary>Sin imagen, quita el logo.</summary>
    public Task<ConfiguracionTicketDto> GuardarLogoTicketAsync(Guid sucursalId, SolicitudLogo solicitud) =>
        PutAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket/logo", solicitud);

    public Task<VentaParaDevolverDto?> VentaPorFolioAsync(Guid sucursalId, string folio) =>
        GetAsync<VentaParaDevolverDto>($"api/sucursales/{sucursalId}/ventas/folio/{Uri.EscapeDataString(folio)}", noEncontradoEsNulo: true);

    public Task<ComprobanteDevolucionDto> CancelarAsync(Guid ventaId, SolicitudCancelacion solicitud) =>
        PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/cancelacion", solicitud);

    public Task<ComprobanteDevolucionDto> DevolverAsync(Guid ventaId, SolicitudDevolucion solicitud) =>
        PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/devoluciones", solicitud);

    public async Task<List<TransferenciaDto>> TransferenciasAsync(Guid sucursalId) =>
        await GetAsync<List<TransferenciaDto>>($"api/sucursales/{sucursalId}/transferencias") ?? [];

    public Task VerificarTransferenciaAsync(Guid cobroId, SolicitudVerificacion solicitud) =>
        PostAsync<object>($"api/cobros/{cobroId}/verificacion", solicitud, sinRespuesta: true);

    public Task<List<BitacoraDto>> ConsultarBitacoraAsync(Guid sucursalId, SolicitudConsultaBitacora solicitud) =>
        PostAsync<List<BitacoraDto>>($"api/sucursales/{sucursalId}/bitacora/consulta", solicitud);

    public async Task<List<ReglaDto>> ReglasAsync(Guid sucursalId) =>
        await GetAsync<List<ReglaDto>>($"api/sucursales/{sucursalId}/reglas") ?? [];

    public Task<List<ReglaDto>> GuardarReglasAsync(Guid sucursalId, SolicitudReglas solicitud) =>
        PutAsync<List<ReglaDto>>($"api/sucursales/{sucursalId}/reglas", solicitud);

    // ── Alta de la caja (antes de tener llave; el servicio todavía no es el guardado) ──

    public Task<EstadoServicioDto?> EstadoAsync(Uri servicio) => GetAsync<EstadoServicioDto>("api/estado", servicio: servicio);

    public Task<List<SucursalAltaDto>> OpcionesAltaAsync(Uri servicio, SolicitudCredenciales credenciales) =>
        PostAsync<List<SucursalAltaDto>>("api/alta/opciones", credenciales, servicio: servicio);

    public Task<AltaCajaDto> AltaAsync(Uri servicio, SolicitudAltaCaja solicitud) => PostAsync<AltaCajaDto>("api/alta", solicitud, servicio: servicio);

    public Task QuitarEquipoAsync(Guid cajaId, SolicitudCredenciales credenciales) =>
        PostAsync<object>($"api/cajas/{cajaId}/quitar-equipo", credenciales, sinRespuesta: true);

    private async Task<T> PutAsync<T>(string url, object cuerpo)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Put, url, cuerpo);
        await RevisarAsync(respuesta);
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T?> GetAsync<T>(string url, bool nadaEsNulo = false, bool noEncontradoEsNulo = false, Uri? servicio = null)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Get, url, servicio: servicio);
        if ((nadaEsNulo && respuesta.StatusCode == HttpStatusCode.NoContent) || (noEncontradoEsNulo && respuesta.StatusCode == HttpStatusCode.NotFound))
            return default;
        await RevisarAsync(respuesta);
        return await respuesta.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T> PostAsync<T>(string url, object? cuerpo, bool sinRespuesta = false, Uri? servicio = null)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Post, url, cuerpo, servicio);
        await RevisarAsync(respuesta);
        return sinRespuesta ? default! : (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Cada llamada va a la caja principal guardada y lleva la llave de esta caja.</summary>
    private async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string url, object? cuerpo = null, Uri? servicio = null)
    {
        using var mensaje = new HttpRequestMessage(metodo, new Uri(servicio ?? conexion.Servicio, url));
        if (conexion.Llave is { } llave)
            mensaje.Headers.Add(EncabezadoLlave, llave);
        if (metodo != HttpMethod.Get)
            mensaje.Content = JsonContent.Create(cuerpo, cuerpo?.GetType() ?? typeof(object));
        try
        {
            return await http.SendAsync(mensaje);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal.", "sin-conexion");
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
        throw new ErrorCaja(error?.Mensaje ?? $"El servicio respondió con un error ({(int)respuesta.StatusCode}).", error?.Codigo);
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
