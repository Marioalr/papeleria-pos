using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo;
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

    /// <summary>Quien atiende ahora en esta caja: a su nombre quedan las impresiones que salen de esta PC.</summary>
    public Guid? UsuarioEnCaja { get; set; }

    public bool Lista => !PideAlta || (CajaId is not null && Llave is not null);
}

/// <summary>
/// Habla con el servicio de la sucursal. La caja no toca la base de datos: todo pasa por aquí. Con contingencia (la
/// ventana de Windows), si la caja principal no contesta, lo que se puede hacer sin ella se hace con la copia local y
/// queda en la bandeja; lo demás espera a que vuelva la conexión.
/// </summary>
public sealed class ClienteCaja(HttpClient http, ConexionCaja conexion, Contingencia? contingencia = null)
{
    public const string EncabezadoLlave = "X-Caja-Llave";

    private readonly Transporte _http = new(http, conexion);

    public Task<List<CajaDto>> CajasAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<List<CajaDto>>("api/cajas"))!, l => l.Cajas());

    public Task<SesionDto> EntrarAsync(string usuario, string pin) =>
        ConRespaldoAsync(() => _http.PostAsync<SesionDto>("api/sesion", new SolicitudEntrada(usuario, pin)), l => l.Entrar(usuario, pin));

    public Task<TurnoDto?> TurnoAsync(Guid cajaId) =>
        ConRespaldoAsync(() => _http.GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true), l => l.Turno(cajaId),
            (l, turno) => l.TurnoEnLineaAsync(turno));

    public Task<TurnoDto> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondo)
    {
        var solicitud = new SolicitudTurno(cajaId, cajeroId, fondo, Ids.Nuevo());
        return ConRespaldoAsync(() => _http.PostAsync<TurnoDto>("api/turnos", solicitud), l => l.AbrirTurno(solicitud),
            (l, turno) => l.TurnoAbiertoEnLinea(turno));
    }

    public Task<ComprobanteMovimientoDto> MovimientoAsync(Guid turnoId, SolicitudMovimiento movimiento)
    {
        movimiento = movimiento with { Id = movimiento.Id ?? Ids.Nuevo() };
        return ConRespaldoAsync(() => _http.PostAsync<ComprobanteMovimientoDto>($"api/turnos/{turnoId}/movimientos", movimiento),
            l => l.Movimiento(turnoId, movimiento), (l, comprobante) => l.MovimientoEnLinea(turnoId, movimiento.Id!.Value, comprobante));
    }

    public Task<TicketDto> ReimprimirAsync(Guid ventaId, SolicitudReimpresion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TicketDto>($"api/ventas/{ventaId}/reimpresion", solicitud));

    public Task<EfectivoCajaDto?> EfectivoAsync(Guid turnoId) =>
        ConRespaldoAsync(() => _http.GetAsync<EfectivoCajaDto>($"api/turnos/{turnoId}/efectivo"), l => l.Efectivo(turnoId));

    public Task BitacoraAsync(SolicitudBitacora solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/bitacora", solicitud, sinRespuesta: true), l => l.Bitacora(solicitud));

    public Task<DecisionDto> PermisoAsync(SolicitudPermiso solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<DecisionDto>("api/permisos", solicitud), l => l.Permiso(solicitud));

    public Task<AutorizacionDto> AutorizarAsync(SolicitudAutorizacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AutorizacionDto>("api/autorizaciones", solicitud), l => l.Autorizar(solicitud));

    public Task<ConfiguracionSucursalDto?> ConfiguracionSucursalAsync(Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ConfiguracionSucursalDto>($"api/sucursales/{sucursalId}/configuracion"));

    public Task<ConfiguracionSucursalDto> GuardarConfiguracionSucursalAsync(Guid sucursalId, SolicitudConfiguracionSucursal solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionSucursalDto>($"api/sucursales/{sucursalId}/configuracion", solicitud));

    public Task<CorteDto> CorteXAsync(Guid turnoId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-x?usuarioId={usuarioId}", null), l => l.CorteX(turnoId, usuarioId));

    public Task<CorteDto> CorteZAsync(Guid turnoId, SolicitudCorteZ solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-z", solicitud), l => l.CorteZ(turnoId, solicitud),
            (l, _) => l.TurnoCerradoEnLinea());

    public Task<ProductoVentaDto?> PorCodigoAsync(string codigo, Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ProductoVentaDto>($"api/productos/codigo/{Uri.EscapeDataString(codigo)}?sucursalId={sucursalId}",
            noEncontradoEsNulo: true), l => l.PorCodigo(codigo));

    public Task<ProductoVentaDto?> PorVarianteAsync(Guid varianteId, Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ProductoVentaDto>($"api/productos/{varianteId}?sucursalId={sucursalId}", noEncontradoEsNulo: true),
            l => l.PorVariante(varianteId));

    public Task<List<ResultadoBusquedaDto>> BuscarAsync(string texto, Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ResultadoBusquedaDto>>(
            $"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}") ?? [], l => l.Buscar(texto));

    /// <summary>La venta lleva su ID desde la caja: si la respuesta no llega y se cobra sin conexión, al subir no se duplica.</summary>
    public Task<TicketDto> CobrarAsync(SolicitudCobro solicitud)
    {
        solicitud = solicitud with { VentaId = solicitud.VentaId ?? Ids.Nuevo() };
        return ConRespaldoAsync(() => _http.PostAsync<TicketDto>("api/ventas", solicitud), l => l.Cobrar(solicitud),
            (l, ticket) => l.CobroEnLinea(solicitud.TurnoId, ticket));
    }

    public Task<List<VentaDelDiaDto>> VentasDelDiaAsync(Guid cajaId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<VentaDelDiaDto>>($"api/cajas/{cajaId}/ventas-del-dia") ?? []);

    public Task<TicketDto?> TicketAsync(Guid ventaId) => ConRespaldoAsync(() => _http.GetAsync<TicketDto>($"api/ventas/{ventaId}/ticket"));

    public Task<TicketDto?> UltimoTicketAsync(Guid cajaId) => ConRespaldoAsync(() => _http.GetAsync<TicketDto>($"api/cajas/{cajaId}/ultimo-ticket"));

    public Task<ConfiguracionTicketDto?> ConfiguracionTicketAsync(Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket"));

    public Task<ConfiguracionTicketDto> GuardarTextosTicketAsync(Guid sucursalId, SolicitudTextosTicket solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket/textos", solicitud));

    /// <summary>Sin imagen, quita el logo.</summary>
    public Task<ConfiguracionTicketDto> GuardarLogoTicketAsync(Guid sucursalId, SolicitudLogo solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket/logo", solicitud));

    public Task<VentaParaDevolverDto?> VentaPorFolioAsync(Guid sucursalId, string folio) =>
        ConRespaldoAsync(() => _http.GetAsync<VentaParaDevolverDto>($"api/sucursales/{sucursalId}/ventas/folio/{Uri.EscapeDataString(folio)}",
            noEncontradoEsNulo: true));

    public Task<ComprobanteDevolucionDto> CancelarAsync(Guid ventaId, SolicitudCancelacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/cancelacion", solicitud), null,
            (l, comprobante) => l.DevolucionEnLinea(comprobante));

    public Task<ComprobanteDevolucionDto> DevolverAsync(Guid ventaId, SolicitudDevolucion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/devoluciones", solicitud), null,
            (l, comprobante) => l.DevolucionEnLinea(comprobante));

    public Task<List<TransferenciaDto>> TransferenciasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<TransferenciaDto>>($"api/sucursales/{sucursalId}/transferencias") ?? []);

    public Task VerificarTransferenciaAsync(Guid cobroId, SolicitudVerificacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/cobros/{cobroId}/verificacion", solicitud, sinRespuesta: true));

    public Task<List<BitacoraDto>> ConsultarBitacoraAsync(Guid sucursalId, SolicitudConsultaBitacora solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<List<BitacoraDto>>($"api/sucursales/{sucursalId}/bitacora/consulta", solicitud));

    public Task<List<ReglaDto>> ReglasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ReglaDto>>($"api/sucursales/{sucursalId}/reglas") ?? []);

    public Task<List<ReglaDto>> GuardarReglasAsync(Guid sucursalId, SolicitudReglas solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<List<ReglaDto>>($"api/sucursales/{sucursalId}/reglas", solicitud));

    // ── Impresiones (solo con la caja principal: ahí llegan los trabajos de todas las PC) ──

    public Task<List<ImpresionPorCobrarDto>> ImpresionesPorCobrarAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{sucursalId}/impresiones/por-cobrar?usuarioId={usuarioId}") ?? []);

    public Task NoVendidaAsync(Guid trabajoId, SolicitudNoVendida solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/impresiones/{trabajoId}/no-vendida", solicitud, sinRespuesta: true));

    // ── Telegram (solo con la caja principal: el bot vive ahí) ──

    public Task<EstadoTelegramDto> TelegramAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<EstadoTelegramDto>($"api/sucursales/{sucursalId}/telegram"))!);

    public Task<CodigoVinculacionDto> CodigoTelegramAsync(Guid sucursalId, SolicitudCredenciales credenciales) =>
        ConRespaldoAsync(() => _http.PostAsync<CodigoVinculacionDto>($"api/sucursales/{sucursalId}/telegram/codigo", credenciales));

    public Task DesvincularTelegramAsync(Guid sucursalId, Guid chatId, SolicitudCredenciales credenciales) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/sucursales/{sucursalId}/telegram/chats/{chatId}/desvincular", credenciales, sinRespuesta: true));

    public Task<AjustesAlertasDto> GuardarAlertasAsync(Guid sucursalId, SolicitudAlertas solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<AjustesAlertasDto>($"api/sucursales/{sucursalId}/telegram/alertas", solicitud));

    public Task PruebaTelegramAsync(Guid sucursalId, SolicitudCredenciales credenciales) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/sucursales/{sucursalId}/telegram/prueba", credenciales, sinRespuesta: true));

    public Task<SolicitudRemotaDto> PedirPorTelegramAsync(SolicitudAutorizacionRemota solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<SolicitudRemotaDto>("api/autorizaciones/remotas", solicitud));

    public Task<EstadoSolicitudRemotaDto> EstadoPorTelegramAsync(Guid solicitudId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<EstadoSolicitudRemotaDto>($"api/autorizaciones/remotas/{solicitudId}"))!);

    public Task CancelarPorTelegramAsync(Guid solicitudId) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/autorizaciones/remotas/{solicitudId}/cancelar", null, sinRespuesta: true));

    // ── Alta de la caja (antes de tener llave; el servicio todavía no es el guardado) ──

    public Task<EstadoServicioDto?> EstadoAsync(Uri servicio) => _http.GetAsync<EstadoServicioDto>("api/estado", servicio: servicio);

    public Task<List<SucursalAltaDto>> OpcionesAltaAsync(Uri servicio, SolicitudCredenciales credenciales) =>
        _http.PostAsync<List<SucursalAltaDto>>("api/alta/opciones", credenciales, servicio: servicio);

    public Task<AltaCajaDto> AltaAsync(Uri servicio, SolicitudAltaCaja solicitud) => _http.PostAsync<AltaCajaDto>("api/alta", solicitud, servicio: servicio);

    public Task QuitarEquipoAsync(Guid cajaId, SolicitudCredenciales credenciales) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/cajas/{cajaId}/quitar-equipo", credenciales, sinRespuesta: true));

    /// <summary>
    /// En línea, va a la caja principal y le avisa a la contingencia lo que pasó (para su cuenta del turno). Sin
    /// conexión, usa la copia local si esa operación se puede hacer sin la caja principal.
    /// </summary>
    private async Task<T> ConRespaldoAsync<T>(
        Func<Task<T>> enLinea, Func<Contingencia, T>? sinConexion = null, Func<Contingencia, T, Task>? trasEnLinea = null)
    {
        if (contingencia is null)
            return await enLinea();
        if (!contingencia.EnLinea)
            return Local(contingencia, sinConexion);
        T resultado;
        try
        {
            resultado = await enLinea();
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            contingencia.MarcarSinConexion();
            return Local(contingencia, sinConexion);
        }
        if (trasEnLinea is not null)
            await trasEnLinea(contingencia, resultado);
        return resultado;
    }

    private Task<T> ConRespaldoAsync<T>(Func<Task<T>> enLinea, Func<Contingencia, T>? sinConexion, Action<Contingencia, T> trasEnLinea) =>
        ConRespaldoAsync(enLinea, sinConexion, (l, r) =>
        {
            trasEnLinea(l, r);
            return Task.CompletedTask;
        });

    private Task ConRespaldoAsync(Func<Task<object>> enLinea, Action<Contingencia> sinConexion) =>
        ConRespaldoAsync(enLinea, l =>
        {
            sinConexion(l);
            return new object();
        });

    private static T Local<T>(Contingencia contingencia, Func<Contingencia, T>? sinConexion)
    {
        if (sinConexion is null)
            throw new ErrorCaja("No hay conexión con la caja principal: esto espera a que vuelva.", "sin-conexion");
        try
        {
            return sinConexion(contingencia);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ErrorCaja(ex.Message);
        }
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
