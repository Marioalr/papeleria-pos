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

    /// <summary>La caja de prueba arranca su propio servicio: se espera a que conteste antes de conectarse.</summary>
    public Task Listo { get; init; } = Task.CompletedTask;

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

    // ── Catálogo (F3, solo con la caja principal) ──

    public Task<List<ProductoCatalogoDto>> CatalogoAsync(Guid sucursalId, string? texto, Guid? departamentoId, bool porCompletar, bool inactivos) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoCatalogoDto>>(
            $"api/catalogo/productos?sucursalId={sucursalId}&q={Uri.EscapeDataString(texto ?? "")}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "") + $"&porCompletar={porCompletar}&inactivos={inactivos}") ?? []);

    public Task<OpcionesCatalogoDto> OpcionesCatalogoAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!);

    public Task<FichaProductoDto?> FichaProductoAsync(Guid productoId, Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.GetAsync<FichaProductoDto>($"api/catalogo/productos/{productoId}?sucursalId={sucursalId}&usuarioId={usuarioId}",
            noEncontradoEsNulo: true));

    public Task<FichaProductoDto> GuardarProductoAsync(SolicitudGuardarProducto solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<FichaProductoDto>("api/catalogo/productos", solicitud));

    public async Task<string> CodigoInternoAsync() =>
        (await ConRespaldoAsync(() => _http.PostAsync<CodigoGenerado>("api/catalogo/codigo-interno", null))).Codigo;

    public Task<List<MovimientoKardexDto>> KardexAsync(Guid varianteId, Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<MovimientoKardexDto>>($"api/catalogo/variantes/{varianteId}/kardex?sucursalId={sucursalId}") ?? []);

    public Task<ProductoVentaDto> AltaRapidaAsync(SolicitudAltaRapida solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ProductoVentaDto>("api/catalogo/alta-rapida", solicitud));

    private sealed record CodigoGenerado(string Codigo);

    public Task DarDeBajaAsync(SolicitudBaja solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/baja", solicitud, sinRespuesta: true));

    public Task JuntarAsync(SolicitudJuntar solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/juntar", solicitud, sinRespuesta: true));

    public Task CambiarUnidadAsync(SolicitudCambiarUnidad solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/unidad", solicitud, sinRespuesta: true));

    public Task<List<DecisionDepuracionDto>> DecisionesDepuracionAsync() =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<DecisionDepuracionDto>>("api/catalogo/depuracion") ?? []);

    public Task<List<VarianteParaJuntarDto>> VariantesParaJuntarAsync(string texto, Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<VarianteParaJuntarDto>>(
            $"api/catalogo/variantes?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}") ?? []);

    // ── Compras (F4, solo con la caja principal) ──

    public Task<List<ProveedorDto>> ProveedoresAsync(bool inactivos = false) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProveedorDto>>($"api/proveedores?inactivos={inactivos}") ?? []);

    public Task<ProveedorDto> GuardarProveedorAsync(SolicitudGuardarProveedor solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ProveedorDto>("api/proveedores", solicitud));

    public Task<List<CompraResumenDto>> ComprasAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<CompraResumenDto>>($"api/sucursales/{sucursalId}/compras?usuarioId={usuarioId}") ?? []);

    public Task<CompraDto> AbrirCompraAsync(SolicitudAbrirCompra solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>("api/compras", solicitud));

    public Task<CompraDto> CompraAsync(Guid compraId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<CompraDto>($"api/compras/{compraId}?usuarioId={usuarioId}"))!);

    public Task<CompraDto> ContarAsync(Guid compraId, SolicitudConteo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/conteo", solicitud));

    public Task<CompraDto> TerminarConteoAsync(Guid compraId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/terminar-conteo", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> RenglonCompraAsync(Guid compraId, SolicitudRenglonCompra solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/renglones", solicitud));

    public Task<CompraDto> CargarDocumentoCompraAsync(Guid compraId, SolicitudDocumentoCompra solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/documentos", solicitud));

    public Task<CompraDto> ContarDesconocidoAsync(Guid compraId, SolicitudConteoDesconocido solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/conteo/desconocido", solicitud));

    public Task<CompraDto> CorregirRenglonAsync(Guid compraId, Guid renglonId, SolicitudCorregirRenglon solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/renglones/{renglonId}/corregir", solicitud));

    public Task<CompraDto> ProductoNuevoAsync(Guid compraId, Guid renglonId, SolicitudProductoNuevo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/renglones/{renglonId}/nuevo", solicitud));

    public Task<CompraDto> NoEsNuevoAsync(Guid compraId, Guid renglonId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/renglones/{renglonId}/no-es-nuevo", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> ProponerNuevosAsync(Guid compraId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/nuevos/proponer", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> DarDeAltaNuevosAsync(Guid compraId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/nuevos/alta", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> QuitarRenglonCompraAsync(Guid compraId, Guid renglonId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/renglones/{renglonId}/quitar", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> CerrarCompraAsync(Guid compraId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/cerrar", new SolicitudCompra(usuarioId)));

    public Task<CompraDto> CancelarCompraAsync(Guid compraId, SolicitudCancelarCompra solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/cancelar", solicitud));

    public Task<CompraDto> OrdenDeCompraAsync(Guid compraId, SolicitudOrdenDeCompra solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CompraDto>($"api/compras/{compraId}/orden", solicitud));

    public Task<List<OrdenResumenDto>> OrdenesAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<OrdenResumenDto>>($"api/sucursales/{sucursalId}/ordenes") ?? []);

    public Task<OrdenDto> AbrirOrdenAsync(SolicitudAbrirOrden solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<OrdenDto>("api/ordenes", solicitud));

    public Task<OrdenDto> OrdenAsync(Guid ordenId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<OrdenDto>($"api/ordenes/{ordenId}"))!);

    public Task<OrdenDto> PedirAsync(Guid ordenId, SolicitudPedido solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<OrdenDto>($"api/ordenes/{ordenId}/renglones", solicitud));

    public Task<OrdenDto> CancelarOrdenAsync(Guid ordenId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<OrdenDto>($"api/ordenes/{ordenId}/cancelar", new SolicitudCompra(usuarioId)));

    public Task<AperturaDto> AbrirPaqueteAsync(SolicitudAbrirPaquete solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AperturaDto>("api/aperturas", solicitud));

    public Task<List<AperturaDto>> AperturasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<AperturaDto>>($"api/sucursales/{sucursalId}/aperturas") ?? []);

    public Task<UltimaAperturaDto?> UltimaAperturaAsync(Guid origenId) =>
        ConRespaldoAsync(() => _http.GetAsync<UltimaAperturaDto>($"api/variantes/{origenId}/ultima-apertura", nadaEsNulo: true));

    public Task CambiarMenudeoAsync(SolicitudPrecioMenudeo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/precio-menudeo", solicitud, sinRespuesta: true));

    public Task<AjusteInventarioDto> AjustarAsync(SolicitudAjuste solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AjusteInventarioDto>("api/inventario/ajustes", solicitud));

    public Task<AjusteInventarioDto> MermaAsync(SolicitudMerma solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AjusteInventarioDto>("api/inventario/mermas", solicitud));

    public Task<List<AjusteInventarioDto>> AjustesAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<AjusteInventarioDto>>($"api/sucursales/{sucursalId}/ajustes?usuarioId={usuarioId}") ?? []);

    public Task<ConteoCiclicoDto> ConteoDelDiaAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ConteoCiclicoDto>($"api/sucursales/{sucursalId}/conteo-del-dia?usuarioId={usuarioId}"))!);

    public Task<ConteoCiclicoDto> ContarDelDiaAsync(Guid conteoId, SolicitudContar solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoCiclicoDto>($"api/conteos/{conteoId}/contar", solicitud));

    public Task<List<DepartamentoConteoDto>> ConteosCompletosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<DepartamentoConteoDto>>($"api/sucursales/{sucursalId}/conteos-completos") ?? []);

    public Task<ConteoDepartamentoDto> AbrirConteoAsync(Guid sucursalId, SolicitudAbrirConteo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoDepartamentoDto>($"api/sucursales/{sucursalId}/conteos-completos", solicitud));

    public Task<ConteoDepartamentoDto> ConteoCompletoAsync(Guid conteoId, Guid usuarioId, bool revision = false) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ConteoDepartamentoDto>($"api/conteos-completos/{conteoId}?usuarioId={usuarioId}&revision={revision}"))!);

    public Task<ConteoDepartamentoDto> CapturarConteoAsync(Guid conteoId, SolicitudCaptura solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoDepartamentoDto>($"api/conteos-completos/{conteoId}/capturas", solicitud));

    public Task<ConteoDepartamentoDto> QuitarCapturaAsync(Guid conteoId, Guid capturaId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoDepartamentoDto>($"api/conteos-completos/{conteoId}/capturas/{capturaId}/quitar",
            new SolicitudConteoCompleto(usuarioId)));

    public Task<ConteoDepartamentoDto> CerrarConteoAsync(Guid conteoId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoDepartamentoDto>($"api/conteos-completos/{conteoId}/cerrar", new SolicitudConteoCompleto(usuarioId)));

    public Task<ConteoDepartamentoDto> CancelarConteoAsync(Guid conteoId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<ConteoDepartamentoDto>($"api/conteos-completos/{conteoId}/cancelar", new SolicitudConteoCompleto(usuarioId)));

    public Task<DireccionCelularDto> DireccionCelularAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<DireccionCelularDto>("api/conteo-celular"))!);

    public Task<ConfiguracionEtiquetasDto> ConfiguracionEtiquetasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ConfiguracionEtiquetasDto>($"api/sucursales/{sucursalId}/etiquetas/configuracion"))!);

    public Task<ConfiguracionEtiquetasDto> GuardarConfiguracionEtiquetasAsync(Guid sucursalId, SolicitudConfiguracionEtiquetas solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionEtiquetasDto>($"api/sucursales/{sucursalId}/etiquetas/configuracion", solicitud));

    public Task<List<ProductoEtiquetaDto>> EtiquetasDeCompraAsync(Guid compraId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoEtiquetaDto>>($"api/compras/{compraId}/etiquetas") ?? []);

    public Task<List<ProductoEtiquetaDto>> SinCodigoAsync(Guid departamentoId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoEtiquetaDto>>($"api/etiquetas/sin-codigo?departamentoId={departamentoId}") ?? []);

    public Task<EtiquetasDto> PrepararEtiquetasAsync(SolicitudEtiquetas solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<EtiquetasDto>("api/etiquetas", solicitud));

    public Task<List<RenglonHojaCodigosDto>> HojaCodigosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<RenglonHojaCodigosDto>>($"api/sucursales/{sucursalId}/hoja-codigos") ?? []);

    public Task<List<RenglonHojaCodigosDto>> GuardarHojaCodigosAsync(Guid sucursalId, SolicitudHojaCodigos solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<List<RenglonHojaCodigosDto>>($"api/sucursales/{sucursalId}/hoja-codigos", solicitud));

    public Task<HojaCodigosDto> PrepararHojaCodigosAsync(Guid sucursalId, SolicitudPrepararHojaCodigos solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<HojaCodigosDto>($"api/sucursales/{sucursalId}/hoja-codigos/preparar", solicitud));

    public Task<MargenesDto> MargenesAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<MargenesDto>($"api/sucursales/{sucursalId}/margenes"))!);

    public Task<MargenesDto> GuardarMargenesAsync(Guid sucursalId, SolicitudMargenes solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<MargenesDto>($"api/sucursales/{sucursalId}/margenes", solicitud));

    // ── Descuentos ──

    /// <summary>Sin conexión, los de la copia local (solo los activos).</summary>
    public Task<List<ClienteEspecialDto>> ClientesEspecialesAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ClienteEspecialDto>>($"api/sucursales/{sucursalId}/clientes-especiales") ?? [],
            l => l.ClientesEspeciales());

    public Task<List<ClienteEspecialDto>> GuardarClientesEspecialesAsync(Guid sucursalId, SolicitudClientesEspeciales solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<List<ClienteEspecialDto>>($"api/sucursales/{sucursalId}/clientes-especiales", solicitud));

    /// <summary>Solo con la caja principal (ahí está el costo); sin conexión, el costo no se revisa.</summary>
    public Task<BajoCostoDto> BajoCostoAsync(SolicitudBajoCosto solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<BajoCostoDto>("api/descuentos/bajo-costo", solicitud), _ => new BajoCostoDto(0, []));

    // ── Impresiones (solo con la caja principal: ahí llegan los trabajos de todas las PC) ──

    public Task<List<ImpresionPorCobrarDto>> ImpresionesPorCobrarAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{sucursalId}/impresiones/por-cobrar?usuarioId={usuarioId}") ?? []);

    public Task NoVendidaAsync(Guid trabajoId, SolicitudNoVendida solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/impresiones/{trabajoId}/no-vendida", solicitud, sinRespuesta: true));

    public Task<ImpresionPorCobrarDto> SoloLoQueSalioAsync(Guid trabajoId, SolicitudSoloLoQueSalio solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ImpresionPorCobrarDto>($"api/impresiones/{trabajoId}/solo-lo-que-salio", solicitud));

    public Task<List<ImpresionDelDiaDto>> ImpresionesDelDiaAsync(Guid sucursalId, DateOnly fecha, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ImpresionDelDiaDto>>(
            $"api/sucursales/{sucursalId}/impresiones?fecha={fecha:yyyy-MM-dd}&usuarioId={usuarioId}") ?? []);

    public Task<OpcionesImpresionDto> OpcionesImpresionAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<OpcionesImpresionDto>($"api/sucursales/{sucursalId}/impresiones/opciones"))!);

    public Task<CotizacionDto> CotizarAsync(SolicitudCotizacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CotizacionDto>("api/impresiones/cotizar", solicitud));

    public Task<PrecioCopiasDto> PrecioCopiasAsync(SolicitudPrecioCopias solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<PrecioCopiasDto>("api/impresiones/copias/precio", solicitud));

    public Task<ImpresionPorCobrarDto> RegistrarCopiasAsync(SolicitudCopias solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ImpresionPorCobrarDto>("api/impresiones/copias", solicitud));

    // ── La impresora (la lee la caja principal) ──

    public Task<EstadoImpresoraDto> ImpresoraAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<EstadoImpresoraDto>($"api/sucursales/{sucursalId}/impresora"))!);

    public Task<EstadoImpresoraDto> GuardarImpresoraAsync(Guid sucursalId, SolicitudConfiguracionImpresora solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<EstadoImpresoraDto>($"api/sucursales/{sucursalId}/impresora", solicitud));

    public Task<EstadoImpresoraDto> LeerImpresoraAsync(Guid sucursalId) =>
        ConRespaldoAsync(() => _http.PostAsync<EstadoImpresoraDto>($"api/sucursales/{sucursalId}/impresora/leer", null));

    public Task<CalibracionDto> CalibracionAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<CalibracionDto>($"api/sucursales/{sucursalId}/impresora/calibracion"))!);

    public Task<CalibracionDto> AplicarCalibracionAsync(Guid sucursalId, SolicitudCredenciales credenciales) =>
        ConRespaldoAsync(() => _http.PostAsync<CalibracionDto>($"api/sucursales/{sucursalId}/impresora/calibracion", credenciales));

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

    // ── La prueba con una persona real (solo existe en el servicio de prueba) ──

    public Task<List<AvisoPruebaDto>> AvisosPruebaAsync() =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<AvisoPruebaDto>>("api/prueba/avisos") ?? []);

    public Task<RespuestasPruebaDto> RespuestasPruebaAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<RespuestasPruebaDto>("api/prueba/respuestas"))!);

    public Task<RespuestasPruebaDto> GuardarRespuestasPruebaAsync(RespuestasPruebaDto respuestas) =>
        ConRespaldoAsync(() => _http.PutAsync<RespuestasPruebaDto>("api/prueba/respuestas", respuestas));

    public Task<ArchivoGuardadoDto> ExportarRespuestasPruebaAsync(string texto) =>
        ConRespaldoAsync(() => _http.PostAsync<ArchivoGuardadoDto>("api/prueba/respuestas/exportar", new SolicitudExportarRespuestas(texto)));

    public Task EmpezarDeCeroAsync() =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/prueba/empezar-de-cero", null, sinRespuesta: true));

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
