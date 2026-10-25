using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>Error que se le muestra al cajero tal cual (viene del servicio o de la conexión).</summary>
public sealed class ErrorCaja(string mensaje, string? codigo = null) : Exception(mensaje)
{
    /// <summary>
    /// "requiere-autorizacion" o "no-permitido" cuando lo detiene un permiso; "caja-sin-alta" si el servicio no
    /// reconoce la llave; "sin-conexion" si no contesta; "precios-cambiados" si el cobro no se hizo porque cambió un precio
    /// (<see cref="PreciosCambiados"/>).
    /// </summary>
    public string? Codigo { get; } = codigo;

    /// <summary>El cobro no se hizo: estos son los precios de ahora de lo que cambió (entrega 5).</summary>
    public PreciosCambiadosDto? PreciosCambiados { get; init; }

    /// <summary>
    /// «sin-conexion» porque pasó el plazo (la caja principal está, pero no contestó a tiempo), no porque no se pudo conectar
    /// (entrega 5, parte 2): la red cuenta como lenta, no como caída.
    /// </summary>
    public bool PorPlazo { get; init; }

    /// <summary>
    /// «sin-conexion» sin que nada llegara a la caja principal: ni se pudo conectar (apagada, sin red). Lo que se mandaba no
    /// quedó guardado allá; en los demás casos (pasó el plazo, se cortó a media respuesta) puede que sí.
    /// </summary>
    public bool NoSeMando { get; init; }

    /// <summary>
    /// El código HTTP con que contestó el servicio (400, 403, 409, 500…); vacío si no contestó («sin-conexion»). Un 500 puede
    /// llegar después de guardar: no dice si se hizo (revisión del PR #138).
    /// </summary>
    public int? Estado { get; init; }
}

/// <summary>Lo que dijo la caja principal de un código que no está en la copia de esta caja.</summary>
/// <param name="Producto">El producto, si lo tiene.</param>
/// <param name="Contesto">Contestó a tiempo (sin producto: no existe).</param>
/// <param name="Pregunto">Se le preguntó; no, si esta caja ya sabe que la caja principal no contesta.</param>
public sealed record CodigoDeLaPrincipal(ProductoVentaDto? Producto, bool Contesto, bool Pregunto = true)
{
    public static readonly CodigoDeLaPrincipal NoExiste = new(null, Contesto: true);
    public static readonly CodigoDeLaPrincipal NoContesto = new(null, Contesto: false);
    public static readonly CodigoDeLaPrincipal SinConexion = new(null, Contesto: false, Pregunto: false);
}

/// <summary>A qué servicio habla esta PC y como qué caja. La ventana de Windows la guarda al dar de alta la caja.</summary>
public sealed class ConexionCaja
{
    public required Uri Servicio { get; set; }

    /// <summary>Vacío: la primera caja de la sucursal (la caja del navegador en la misma PC del servicio).</summary>
    public Guid? CajaId { get; set; }

    public string? Llave { get; set; }

    /// <summary>
    /// La llave pública de la caja principal (la propia de su servicio, que no está en su base): se recuerda al darse de alta o la
    /// primera vez que su dirección prueba quién es, y desde entonces la caja no se cambia sola a un servicio que no firme con ella
    /// (<see cref="ReencuentroDeLaPrincipal"/>). Vacía con un servicio de antes.
    /// </summary>
    public string? LlaveDelServicio { get; set; }

    /// <summary>
    /// La dirección que puso el dueño al darla de alta (en una caja de antes, la que ya tenía): solo ahí se aprende la llave del
    /// servicio la primera vez. Una dirección que la caja encontró sola en la red no la enseña: si no, una copia de la base que
    /// contestara sola se quedaría como la de confianza. Vacía: con la que abrió la caja.
    /// </summary>
    public Uri? DireccionDeConfianza { get; set; }

    private volatile bool _sinComprobar;

    /// <summary>
    /// Después de una caída (o al abrir la caja), mientras la dirección guardada no pruebe otra vez que es la caja principal de esta
    /// caja: no se le manda la llave a nadie (<see cref="Transporte"/>), y la caja trabaja con su copia.
    /// </summary>
    public bool SinComprobar
    {
        get => _sinComprobar;
        set => _sinComprobar = value;
    }

    /// <summary>La ventana de Windows se da de alta antes de vender; la del navegador, en la PC del servicio, no.</summary>
    public bool PideAlta { get; init; }

    /// <summary>Lo pone el anfitrión: dónde guardar la conexión después del alta.</summary>
    public Func<ConexionCaja, Task>? AlGuardar { get; init; }

    /// <summary>Quien atiende ahora en esta caja: a su nombre quedan las impresiones que salen de esta PC.</summary>
    public Guid? UsuarioEnCaja { get; set; }

    /// <summary>La caja de prueba arranca su propio servicio: se espera a que conteste antes de conectarse.</summary>
    public Task Listo { get; init; } = Task.CompletedTask;

    public bool Lista => !PideAlta || (CajaId is not null && Llave is not null);

    /// <summary>Solo en la prueba con una persona real: la caja hace como si la caja principal no contestara.</summary>
    public bool SimularSinConexion { get; set; }
}

/// <summary>
/// Habla con el servicio de la sucursal. La caja no toca la base de datos: todo pasa por aquí. Con contingencia (la
/// ventana de Windows), si la caja principal no contesta, lo que se puede hacer sin ella se hace con la copia local y
/// queda en la bandeja; lo demás espera a que vuelva la conexión.
/// </summary>
/// <param name="descargas">
/// Para bajar los archivos que arma la caja principal (el Excel de un reporte), con su propio tope (<see cref="RedCaja.TopeDescargas"/>);
/// sin él, el mismo de lo demás.
/// </param>
public sealed class ClienteCaja(HttpClient http, ConexionCaja conexion, Contingencia? contingencia = null, HttpClient? descargas = null)
{
    public const string EncabezadoLlave = "X-Caja-Llave";

    private readonly Transporte _http = new(http, conexion);

    private readonly Transporte _archivos = new(descargas ?? http, conexion);

    public Task<List<CajaDto>> CajasAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<List<CajaDto>>("api/cajas"))!, l => l.Cajas());

    public Task<SesionDto> EntrarAsync(string usuario, string pin) =>
        ConRespaldoAsync(() => _http.PostAsync<SesionDto>("api/sesion", new SolicitudEntrada(usuario, pin)), l => l.Entrar(usuario, pin));

    /// <summary>Las fichas de la entrada: quién entra en esta caja, con su letra. Sin conexión, de la copia local.</summary>
    public Task<List<FichaUsuarioDto>> FichasAsync(Guid cajaId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<FichaUsuarioDto>>($"api/cajas/{cajaId}/usuarios") ?? [], l => l.Fichas(cajaId));

    /// <summary>Entrar con la ficha que se tocó y el PIN. Sin conexión, el PIN se revisa con la copia local.</summary>
    public Task<SesionDto> EntrarAsync(Guid cajaId, Guid usuarioId, string pin) =>
        ConRespaldoAsync(() => _http.PostAsync<SesionDto>($"api/cajas/{cajaId}/sesion", new SolicitudEntradaFicha(usuarioId, pin)),
            l => l.Entrar(usuarioId, pin));

    /// <summary>
    /// Para abrir la caja sin esperar a la red (entrega 5, parte 3): esta caja y su turno como los conoce su copia, después de
    /// leerla (fuera del hilo de la pantalla). Vacío si no hay copia de esta caja (la primera vez, o el navegador): entonces
    /// se le pregunta al servicio. Lo que conteste el servicio después pone la pantalla al día.
    /// </summary>
    public async Task<(CajaDto Caja, TurnoDto? Turno)?> DeLaCopiaAsync(Guid? cajaId)
    {
        if (contingencia is null)
            return null;
        await contingencia.CopiaLeida.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (contingencia.Paquete is not { } paquete || (cajaId is { } id && paquete.Caja.Id != id))
            return null;
        // El nombre de la tienda en la entrada, también de la copia.
        ConfiguracionConocida ??= contingencia.ConfiguracionSucursal();
        return (paquete.Caja, contingencia.Turno(paquete.Caja.Id));
    }

    /// <summary>Las fichas de la entrada como las conoce la copia (iguales a las del servicio); vacío sin copia.</summary>
    public List<FichaUsuarioDto>? FichasDeLaCopia(Guid cajaId)
    {
        // Sin esperar: si todavía se está leyendo la copia, se le pregunta al servicio.
        if (contingencia is not { CopiaLeida.IsCompleted: true })
            return null;
        if (contingencia.Paquete is not { } paquete || paquete.Caja.Id != cajaId)
            return null;
        try
        {
            return contingencia.Fichas(cajaId);
        }
        catch (Exception ex) when (ex is ErrorCaja or InvalidOperationException)
        {
            return null;
        }
    }

    public Task<TurnoDto?> TurnoAsync(Guid cajaId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true) ?? await TurnoQueNoTieneAsync(cajaId),
            l => l.Turno(cajaId), (l, turno) => l.TurnoEnLineaAsync(turno));

    /// <summary>
    /// La caja principal no tiene abierto un turno de esta caja, y esta caja sí tiene el suyo: si allá no existe (se restauró un
    /// respaldo de antes de que se abriera, 2026-10-23), se vuelve a dar de alta con su mismo Id y se sigue en él, con lo que se
    /// cobró sin conexión (que sube antes). Si allá ya existe (se cerró desde otra caja), nada cambia: no hay turno, como siempre.
    /// Primero el que no se pudo dar de alta al volver la caja principal porque tenía abierto otro (el de antes del respaldo, que
    /// ahora ya se cerró; revisión del PR #136): ese es el de la caja.
    /// </summary>
    private async Task<TurnoDto?> TurnoQueNoTieneAsync(Guid cajaId)
    {
        if (contingencia is null)
            return null;
        TurnoDto?[] suyos = [contingencia.TurnoPorDarDeAlta(cajaId)?.Turno, contingencia.Turno(cajaId)];
        foreach (var suyo in suyos.OfType<TurnoDto>().DistinctBy(t => t.Id))
        {
            if (await contingencia.DarDeAltaElTurnoAsync(suyo) is not null)
                continue;
            if (contingencia.Pendientes > 0 || contingencia.ConError > 0)
                await contingencia.SubirAsync();
            if (await _http.GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true) is { } abierto)
                return abierto;
        }
        return null;
    }

    public Task<TurnoDto> AbrirTurnoAsync(Guid cajaId, Guid cajeroId, long fondo)
    {
        var solicitud = new SolicitudTurno(cajaId, cajeroId, fondo, Ids.Nuevo());
        return ConRespaldoAsync(() => _http.PostAsync<TurnoDto>("api/turnos", solicitud), l => l.AbrirTurno(solicitud),
            (l, turno) => l.TurnoAbiertoEnLineaAsync(turno));
    }

    public Task<ComprobanteMovimientoDto> MovimientoAsync(Guid turnoId, SolicitudMovimiento movimiento)
    {
        movimiento = movimiento with { Id = movimiento.Id ?? Ids.Nuevo() };
        return ConRespaldoAsync(() => _http.PostAsync<ComprobanteMovimientoDto>($"api/turnos/{turnoId}/movimientos", movimiento),
            l => l.Movimiento(turnoId, movimiento), (l, comprobante) => l.MovimientoEnLineaAsync(turnoId, movimiento.Id!.Value, comprobante));
    }

    /// <summary>Lo que dice la caja sin la caja principal en las entradas y salidas del turno: se ven y se cancelan allá.</summary>
    public const string MovimientosSinConexion =
        "Sin conexión con la caja principal: las entradas y salidas de este turno se ven, y una equivocada se cancela, cuando vuelva.";

    /// <summary>Las entradas y salidas del turno (F7/F8), para cancelar una equivocada (2026-10-22). Solo con la caja principal.</summary>
    public Task<MovimientosDelTurnoDto> MovimientosDelTurnoAsync(Guid turnoId) =>
        SoloConLaPrincipalAsync<MovimientosDelTurnoDto>(MovimientosSinConexion, $"api/turnos/{turnoId}/movimientos");

    /// <summary>
    /// Cancela una entrada o salida equivocada (2026-10-22), con motivo y, si su permiso la pide, la autorización. Regresa cómo
    /// quedaron las del turno; la cuenta de esta caja (para el corte sin conexión) deja de contarla. Si se pierde la respuesta,
    /// no se sabe si quedó: lo dice (<see cref="CancelacionSinConfirmar"/>) y, al mandarla otra vez con el mismo Id, la caja
    /// principal contesta como si fuera la primera (revisión del PR #133).
    /// </summary>
    public Task<MovimientosDelTurnoDto> CancelarMovimientoAsync(Guid turnoId, MovimientoDelTurnoDto movimiento, SolicitudCancelarMovimiento solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<MovimientosDelTurnoDto>($"api/turnos/{turnoId}/movimientos/{movimiento.Id}/cancelacion", solicitud), null,
            (l, _) => l.MovimientoCanceladoEnLineaAsync(turnoId, movimiento, solicitud.Id),
            siSePierde: _ => new ErrorCaja(CancelacionSinConfirmar, "cancelacion-sin-confirmar"));

    /// <summary>Lo que dice la caja si se perdió la respuesta de una cancelación: no se sabe si quedó.</summary>
    public const string CancelacionSinConfirmar =
        "Se perdió la conexión al cancelar y no se sabe si quedó: no muevas el efectivo todavía. Cuando vuelva la conexión, oprime Enter otra vez "
        + "(si ya quedó, no se cancela dos veces).";

    public Task<TicketDto> ReimprimirAsync(Guid ventaId, SolicitudReimpresion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TicketDto>($"api/ventas/{ventaId}/reimpresion", solicitud));

    /// <summary>
    /// La primera impresión del ticket de una venta que no salió al cobrar (2026-10-22): sin motivo, y la caja principal la anota
    /// como primera impresión. Si ya salió, <see cref="ErrorCaja"/> con <see cref="SolicitudPrimeraImpresion.CodigoConMotivo"/>.
    /// Solo con la caja principal, como reimprimir.
    /// </summary>
    public Task<TicketDto> PrimeraImpresionAsync(Guid ventaId, SolicitudPrimeraImpresion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TicketDto>($"api/ventas/{ventaId}/primera-impresion", solicitud));

    /// <summary>El ticket de esa venta no salió de la impresora y se dejó así («No imprimirlo»): su primera impresión no pedirá motivo.</summary>
    public Task TicketSinImprimirAsync(Guid ventaId, SolicitudTicketSinImprimir solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/ventas/{ventaId}/sin-imprimir", solicitud, sinRespuesta: true));

    public Task<EfectivoCajaDto?> EfectivoAsync(Guid turnoId) =>
        ConRespaldoAsync(() => _http.GetAsync<EfectivoCajaDto>($"api/turnos/{turnoId}/efectivo"), l => l.Efectivo(turnoId));

    public Task BitacoraAsync(SolicitudBitacora solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/bitacora", solicitud, sinRespuesta: true), l => l.Bitacora(solicitud));

    public Task<DecisionDto> PermisoAsync(SolicitudPermiso solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<DecisionDto>("api/permisos", solicitud), l => l.Permiso(solicitud));

    /// <summary>Todo lo que puede quien entró, con quién autoriza cada cosa. Sin conexión, de la copia local.</summary>
    public Task<IReadOnlyList<PermisoDeSesion>> PermisosDeSesionAsync(Guid usuarioId, Guid sucursalId) =>
        ConRespaldoAsync<IReadOnlyList<PermisoDeSesion>>(
            async () => await _http.GetAsync<List<PermisoDeSesion>>($"api/usuarios/{usuarioId}/permisos?sucursalId={sucursalId}") ?? [],
            l => l.PermisosDeSesion(usuarioId));

    /// <summary>Quién puede hacer algo sin pedir autorización, por su nombre: se le dice a quien no lo tiene (entrega 8).</summary>
    public Task<List<QuienAutoriza>> QuienesPuedenAsync(Guid sucursalId, AccionControlada accion) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<QuienAutoriza>>($"api/sucursales/{sucursalId}/quienes-pueden?accion={accion}") ?? []);

    public Task<AutorizacionDto> AutorizarAsync(SolicitudAutorizacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AutorizacionDto>("api/autorizaciones", solicitud), l => l.Autorizar(solicitud));

    /// <summary>
    /// La configuración de la tienda (entrega 7): el nombre de la entrada, las fichas de la espera, el fondo sugerido… Sin
    /// conexión, la de la copia local. Se recuerda la última (<see cref="ConfiguracionConocida"/>) para pintar sin esperar.
    /// </summary>
    public async Task<ConfiguracionSucursalDto> ConfiguracionSucursalAsync(Guid sucursalId)
    {
        var configuracion = await ConRespaldoAsync(async () => (await _http.GetAsync<ConfiguracionSucursalDto>($"api/sucursales/{sucursalId}/configuracion"))!,
            l => l.ConfiguracionSucursal());
        ConfiguracionConocida = configuracion;
        return configuracion;
    }

    /// <summary>La última configuración de la tienda que se leyó o se guardó en esta caja; vacía si todavía no.</summary>
    public ConfiguracionSucursalDto? ConfiguracionConocida { get; private set; }

    /// <summary>Con «Configuración de la tienda»: lo que cambió va a la bitácora.</summary>
    public async Task<ConfiguracionSucursalDto> GuardarConfiguracionSucursalAsync(Guid cajaId, SolicitudConfiguracionSucursal solicitud)
    {
        var configuracion = await ConRespaldoAsync(() => _http.PutAsync<ConfiguracionSucursalDto>($"api/cajas/{cajaId}/configuracion", solicitud));
        ConfiguracionConocida = configuracion;
        return configuracion;
    }

    /// <summary>Los rápidos en su orden, como deben quedar (con «Configuración de la tienda»).</summary>
    public Task<List<Rapido>> GuardarRapidosAsync(Guid cajaId, SolicitudRapidos solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<List<Rapido>>($"api/cajas/{cajaId}/configuracion/rapidos", solicitud));

    /// <summary>La cuenta del turno (guarda un corte parcial). La pantalla no la enseña antes de cerrar: ver <see cref="CorteInformativoAsync"/>.</summary>
    public Task<CorteDto> CorteXAsync(Guid turnoId, Guid usuarioId) =>
        ConRespaldoAsync(() => _http.PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-x?usuarioId={usuarioId}", null), l => l.CorteX(turnoId, usuarioId));

    /// <summary>
    /// El corte X que enseña la pantalla (entrega 6): a ciegas, sin el efectivo, y si quien entró puede hacer el corte.
    /// Sin conexión, con la cuenta y los permisos de la copia de esta caja.
    /// </summary>
    public Task<CorteInformativoDto> CorteInformativoAsync(Guid turnoId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<CorteInformativoDto>($"api/turnos/{turnoId}/corte-informativo?usuarioId={usuarioId}"))!,
            l => l.CorteInformativo(turnoId, usuarioId));

    /// <summary>
    /// El corte del turno con el total que se contó, a nombre de quien entró. Sin conexión, lo hace esta caja y se cuadra al
    /// subir. Lleva su Id desde aquí: si la caja principal lo guardó pero la respuesta no llegó, el que hace esta caja es el
    /// mismo, y al subir ya está (no choca con el turno ya cerrado). Sin el plazo corto de la venta (entrega 5, parte 2): es el
    /// cierre del turno, no un cliente en la fila; se espera a la caja principal con el tope de siempre.
    /// </summary>
    public Task<CorteDto> CorteZAsync(Guid turnoId, SolicitudCorteZ solicitud)
    {
        solicitud = solicitud with { Id = solicitud.Id ?? Ids.Nuevo() };
        return ConRespaldoAsync(() => _http.PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-z", solicitud), l => l.CorteZ(turnoId, solicitud),
            (l, _) => l.TurnoCerradoEnLineaAsync(), plazoDeLaVenta: false);
    }

    /// <summary>Lo que dice la caja cuando no hay caja principal: el corte del día junta todas las cajas.</summary>
    public const string CorteDelDiaSinConexion = "Sin conexión con la caja principal: el corte del día junta todas las cajas y se ve cuando vuelva.";

    /// <summary>El corte del día (entrega 6): todas las cajas y turnos de la sucursal. Necesita la caja principal.</summary>
    public async Task<CorteDelDiaDto> CorteDelDiaAsync(Guid cajaId, Guid usuarioId)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(CorteDelDiaSinConexion, "sin-conexion");
        return (await _http.GetAsync<CorteDelDiaDto>($"api/cajas/{cajaId}/corte-del-dia?usuarioId={usuarioId}"))!;
    }

    /// <summary>
    /// Cierra el día y manda su resumen por Telegram. Si esta caja todavía sube lo que hizo sin conexión, espera: el
    /// servicio también lo sabe por el latido, pero con unos segundos de atraso.
    /// </summary>
    public async Task<CierreDelDiaDto> CerrarElDiaAsync(Guid cajaId, Guid usuarioId)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(CorteDelDiaSinConexion, "sin-conexion");
        if (contingencia is { Pendientes: > 0 and var pendientes })
            throw new ErrorCaja($"Esta caja todavía sube lo que hizo sin conexión ({pendientes} por subir): el cierre espera a que termine.");
        return await _http.PostAsync<CierreDelDiaDto>($"api/cajas/{cajaId}/corte-del-dia/cierre", new SolicitudCierreDia(usuarioId));
    }

    /// <summary>
    /// Cierra el día que se ve (2026-10-17): si ya no es hoy (pasó la medianoche con el corte abierto), el servicio no lo
    /// cierra, ni ese ni el nuevo. Lo demás, como <see cref="CerrarElDiaAsync(Guid, Guid)"/>.
    /// </summary>
    public async Task<CierreDelDiaDto> CerrarElDiaAsync(Guid cajaId, Guid usuarioId, DateOnly fecha)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(CorteDelDiaSinConexion, "sin-conexion");
        if (contingencia is { Pendientes: > 0 and var pendientes })
            throw new ErrorCaja($"Esta caja todavía sube lo que hizo sin conexión ({pendientes} por subir): el cierre espera a que termine.");
        return await _http.PostAsync<CierreDelDiaDto>($"api/cajas/{cajaId}/corte-del-dia/cierre", new SolicitudCierreDia(usuarioId, fecha));
    }

    /// <summary>
    /// El corte del día de una fecha (2026-10-17): hoy, o uno que ya pasó, solo para verlo e imprimirlo. Con las ventas por
    /// departamento y, si quien entró ve la utilidad, la utilidad. Necesita la caja principal.
    /// </summary>
    public async Task<CorteDelDiaDto> CorteDelDiaAsync(Guid cajaId, Guid usuarioId, DateOnly fecha)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(CorteDelDiaSinConexion, "sin-conexion");
        return (await _http.GetAsync<CorteDelDiaDto>(
            $"api/cajas/{cajaId}/corte-del-dia?usuarioId={usuarioId}&fecha={fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"))!;
    }

    /// <summary>
    /// El recordatorio de cerrar el día (2026-10-17): vacío si no toca (antes de la hora de cierre, el día ya cerrado, sin el
    /// permiso o en el piloto) y sin conexión con la caja principal, que es la que sabe si el día se cerró.
    /// </summary>
    public async Task<RecordatorioCierreDto?> RecordatorioCierreAsync(Guid cajaId, Guid usuarioId)
    {
        if (contingencia is { ServidorResponde: false })
            return null;
        return await _http.GetAsync<RecordatorioCierreDto>($"api/cajas/{cajaId}/corte-del-dia/recordatorio?usuarioId={usuarioId}", nadaEsNulo: true);
    }

    public Task<ProductoVentaDto?> PorCodigoAsync(string codigo, Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ProductoVentaDto>($"api/productos/codigo/{Uri.EscapeDataString(codigo)}?sucursalId={sucursalId}",
            noEncontradoEsNulo: true), l => l.PorCodigo(codigo));

    /// <summary>
    /// La foto del catálogo de la copia de esta caja: el escaneo busca aquí, sin la red, también en línea (entrega 5). Vacía
    /// en el navegador sin base local y mientras la caja no tiene copia: entonces el código se pregunta como antes. Sin
    /// conexión (o mientras sube lo que hizo sin ella), solo la copia: lo que llegó después de la caja principal no se puede
    /// cobrar sin ella, y lo que se ve en el ticket es lo que se cobra.
    /// </summary>
    public FotoCatalogo? Foto => contingencia switch
    {
        null => null,
        { EnLinea: true } => contingencia.Foto,
        _ => contingencia.FotoDeLaCopia,
    };

    /// <summary>
    /// Los precios de ahora de un producto, que dijo el cobro (entrega 5): quedan en la foto de la copia, para que el
    /// siguiente escaneo ya los traiga.
    /// </summary>
    public void PreciosAlDia(ProductoVentaDto producto) => contingencia?.AgregarALaFoto(producto);

    /// <summary>
    /// Las promociones de ahora que dijo el cobro (fase 3), si las dijo: quedan en la foto, para que el ticket se ponga al día con
    /// ellas y el siguiente escaneo ya las traiga. Y el día con que las aplicó la caja principal: si no es el de esta PC (su reloj
    /// está mal), la caja usa ese, así el ticket que se pone al día se cobra a la siguiente. Devuelve el aviso de la fecha, si
    /// está mal.
    /// </summary>
    public string? PromocionesAlDia(PreciosCambiadosDto precios)
    {
        if (precios.Promociones is { } promociones)
            contingencia?.PromocionesALaFoto(promociones, precios.VersionCatalogo);
        return precios.DiaPromociones is { } dia ? contingencia?.DiaDeLaCajaPrincipal(dia) : null;
    }

    /// <summary>El día de hoy de la caja (hora de México): con él aplica las promociones (fase 3).</summary>
    public DateOnly Hoy => contingencia?.Hoy ?? DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>
    /// De dónde saca una venta de esta caja sus promociones (fase 3): de la foto de su copia (la de ahora en cada cálculo, con lo
    /// que dijo el cobro encima), con el día de la caja. Sin copia todavía, ninguna: el cobro tampoco las aplica.
    /// <para>
    /// Sin base local (el navegador de desarrollo sin Caja:BaseLocal) no hay copia: no se aplican promociones ni se manda el día,
    /// y el servicio cobra sin ellas, como las calculó la pantalla. Es solo para probar en desarrollo; la caja de verdad (la
    /// ventana de Windows) siempre tiene su base local. Mandar el día sin copia haría que el servicio aplicara promociones que la
    /// pantalla no puso, sin dónde guardarlas para ponerse al día.
    /// </para>
    /// </summary>
    public PromocionesDeLaVenta PromocionesDeLaVenta => new(v => Foto?.Promociones.De(v) ?? [], () => Hoy);

    /// <summary>El día con que se pusieron las promociones del ticket, para el cobro; vacío sin copia (no se pusieron).</summary>
    private DateOnly? DiaPromociones => Foto is null ? null : Hoy;

    /// <summary>
    /// Cuánto se espera a la caja principal por un código que no está en la copia (docs/propuesta-interfaz.md,
    /// «Rendimiento»: medio segundo). Pasado eso, la línea sale y se le dice al cajero que lo escanee otra vez.
    /// </summary>
    public static readonly TimeSpan PlazoPorOmision = TimeSpan.FromMilliseconds(500);

    /// <summary>El de <see cref="PlazoPorOmision"/>; las pruebas lo cambian para no depender de lo que tarda la PC.</summary>
    public TimeSpan PlazoDeLaPrincipal { get; init; } = PlazoPorOmision;

    /// <summary>
    /// Lo más que se espera una lectura de la venta que también se puede hacer con la copia (entrega 5, parte 2): la caja de
    /// verdad lleva <see cref="Transporte.PlazoLectura"/> (<see cref="RegistroInterfaz"/>). Vacío, solo el tope del cliente
    /// (las pruebas de lo demás: así no dependen de lo que tarda la PC).
    /// </summary>
    public TimeSpan? PlazoLectura { get; set; }

    /// <summary>Lo más que se espera el cobro y lo demás de la venta que escribe: en la caja de verdad, <see cref="Transporte.PlazoEscritura"/>.</summary>
    public TimeSpan? PlazoEscritura { get; set; }

    /// <summary>
    /// Un código que no está en la copia (un producto recién dado de alta en otra caja): se pregunta a la caja principal en
    /// segundo plano, con <see cref="PlazoDeLaPrincipal"/>. Si lo tiene, queda en la foto para el siguiente escaneo. No
    /// cambia a sin conexión si no contesta a tiempo: eso lo decide el latido. Sin conexión, ni pregunta; mientras sube lo
    /// que hizo sin ella, tampoco: el cobro todavía va sin conexión, y lo que no está en la copia no se podría cobrar.
    /// </summary>
    public async Task<CodigoDeLaPrincipal> BuscarEnLaPrincipalAsync(string codigo, Guid sucursalId)
    {
        if (contingencia is { EnLinea: false })
            return CodigoDeLaPrincipal.SinConexion;
        try
        {
            var producto = await _http.GetAsync<ProductoVentaDto>($"api/productos/codigo/{Uri.EscapeDataString(codigo)}?sucursalId={sucursalId}",
                noEncontradoEsNulo: true, espera: PlazoDeLaPrincipal);
            if (producto is null)
                return CodigoDeLaPrincipal.NoExiste;
            contingencia?.AgregarALaFoto(producto, codigo);
            return new CodigoDeLaPrincipal(producto, Contesto: true);
        }
        catch (ErrorCaja)
        {
            return CodigoDeLaPrincipal.NoContesto;
        }
    }

    public Task<ProductoVentaDto?> PorVarianteAsync(Guid varianteId, Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ProductoVentaDto>($"api/productos/{varianteId}?sucursalId={sucursalId}", noEncontradoEsNulo: true),
            l => l.PorVariante(varianteId));

    /// <param name="cajaId">Con la caja, lo que ella eligió antes con ese texto sale primero.</param>
    /// <param name="maximo">Cuántos productos: la pantalla de venta pide más, para juntar las familias completas.</param>
    public Task<List<ResultadoBusquedaDto>> BuscarAsync(string texto, Guid sucursalId, Guid? cajaId = null, int maximo = 30) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ResultadoBusquedaDto>>(
            $"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}{(cajaId is { } caja ? $"&cajaId={caja}" : "")}&maximo={maximo}") ?? [],
            l => l.Buscar(texto, maximo));

    /// <summary>
    /// La búsqueda de la venta contra la copia de esta caja, sin la red (entrega 5, parte 3): la misma del servicio, con lo
    /// que esta caja eligió antes. Vacía (null) si no hay copia todavía (la caja recién instalada, o el navegador sin base
    /// local): entonces se le pregunta al servicio con <see cref="BuscarAsync"/>.
    /// </summary>
    public List<ResultadoBusquedaDto>? BuscarEnLaCopia(string texto, int maximo = 30) =>
        Foto is { SeBusca: true } foto ? foto.Buscar(texto, maximo, elegidoAqui: contingencia?.ElegidoAqui(texto)) : null;

    /// <summary>
    /// Lo que se buscó y qué se eligió. Es para aprender (el orden, lo que falta en el catálogo): sin conexión no se
    /// guarda, y si falla no estorba la venta. Lo que el servicio guardó sale primero la vez siguiente también en la copia de
    /// esta caja, como en el servicio.
    /// </summary>
    public async Task RegistrarBusquedaAsync(SolicitudBusqueda busqueda)
    {
        if (contingencia is { EnLinea: false })
            return;
        try
        {
            await _http.PostAsync<object>("api/busquedas", busqueda, sinRespuesta: true);
            if (busqueda.VarianteId is { } elegido)
                contingencia?.Aprender(busqueda.Texto, elegido);
        }
        catch (ErrorCaja)
        {
            // Se pierde esta: la siguiente sí queda.
        }
    }

    /// <summary>Los rápidos de la sucursal. Sin conexión no hay: la fila queda vacía hasta que vuelva.</summary>
    public Task<List<Rapido>> RapidosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<Rapido>>($"api/sucursales/{sucursalId}/rapidos") ?? [], _ => []);

    /// <summary>
    /// La venta lleva su ID desde la caja: si la respuesta no llega y se cobra sin conexión, al subir no se duplica. Lo
    /// fiado se anota en la cuenta propia antes de mandarlo (si la respuesta se pierde, ya cuenta).
    /// <para>
    /// Si la caja principal no contesta (o su respuesta se pierde) y sin conexión no se puede cobrar (algo que la copia no tiene,
    /// las impresiones; al fiar, no conoce los saldos o pasa el límite), puede que la caja principal ya lo haya guardado: cobrarlo
    /// de otra forma, o cambiado con el mismo ID, dejaría dos cobros distintos de una venta. Entonces su folio queda apartado
    /// (<see cref="Contingencia.NoSeSabeSiQuedo"/>) y el ticket solo se cobra en línea, con el mismo ID (también con
    /// <paramref name="soloEnLinea"/>): si ya se guardó, regresa el mismo ticket (<see cref="TicketDto.YaCobrada"/>). Si ni se
    /// pudo conectar, no llegó: sin conexión, lo que diga la copia.
    /// </para>
    /// </summary>
    public Task<TicketDto> CobrarAsync(SolicitudCobro solicitud, bool soloEnLinea = false)
    {
        solicitud = solicitud with { VentaId = solicitud.VentaId ?? Ids.Nuevo(), DiaPromociones = solicitud.DiaPromociones ?? DiaPromociones };
        var ventaId = solicitud.VentaId.Value;
        var fiado = solicitud.Cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos);
        if ((soloEnLinea || contingencia?.CobroSinConfirmar(ventaId) == true) && contingencia is { EnLinea: false })
            return Task.FromException<TicketDto>(contingencia.ErrorSinConfirmar(ventaId, fiado > 0 || soloEnLinea));
        return ConRespaldoAsync(async () =>
            {
                if (fiado > 0 && solicitud.ClienteId is { } cliente && contingencia is not null)
                    await contingencia.AnotarFiadoAsync(solicitud.VentaId.Value, cliente, fiado);
                try
                {
                    return await _http.PostAsync<TicketDto>("api/ventas", solicitud);
                }
                catch (ErrorCaja ex) when (fiado > 0 && ex.Codigo != "sin-conexion")
                {
                    // Lo rechazó por una regla (o cambió un precio): no se fió.
                    if (contingencia is not null)
                        await contingencia.QuitarCreditoAsync(solicitud.VentaId.Value);
                    throw;
                }
            }, l => l.Cobrar(solicitud), (l, ticket) => l.CobroEnLineaAsync(solicitud.TurnoId, ticket), siSePierde: error =>
            {
                // Ni se pudo conectar: no llegó (lo fiado ya se anotó en la cuenta propia: eso sí espera a la conexión).
                if (error.NoSeMando && fiado == 0)
                    return null;
                contingencia!.NoSeSabeSiQuedo(ventaId, fiado > 0);
                return contingencia.ErrorSinConfirmar(ventaId, fiado > 0);
            });
    }

    /// <summary>Un cobro que se mandó y no se sabe si quedó (<see cref="CobrarAsync"/>): espera la conexión.</summary>
    public static ErrorCaja SinConfirmar(bool fiado) => fiado
        ? new("Se perdió la conexión al fiar y no se sabe si quedó en su cuenta. Cuando vuelva la conexión, cobra otra vez este ticket "
            + "(si ya quedó, sale el mismo); mientras, no lo cobres de otra forma.", "fiado-sin-confirmar")
        : new("La caja principal no contestó a tiempo y no se sabe si el cobro quedó. Cuando vuelva la conexión, cobra otra vez este "
            + "ticket tal como está (si ya quedó, sale el mismo); mientras, no lo cobres de otra forma.", "cobro-sin-confirmar");

    public Task<List<VentaDelDiaDto>> VentasDelDiaAsync(Guid cajaId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<VentaDelDiaDto>>($"api/cajas/{cajaId}/ventas-del-dia") ?? []);

    public Task<TicketDto?> TicketAsync(Guid ventaId) => ConRespaldoAsync(() => _http.GetAsync<TicketDto>($"api/ventas/{ventaId}/ticket"));

    public Task<TicketDto?> UltimoTicketAsync(Guid cajaId) => ConRespaldoAsync(() => _http.GetAsync<TicketDto>($"api/cajas/{cajaId}/ultimo-ticket"));

    public Task<ConfiguracionTicketDto?> ConfiguracionTicketAsync(Guid sucursalId) =>
        ConRespaldoAsync(() => _http.GetAsync<ConfiguracionTicketDto>($"api/sucursales/{sucursalId}/ticket"));

    /// <summary>El encabezado y el pie, con «Configurar el ticket» de quien entró a la caja.</summary>
    public Task<ConfiguracionTicketDto> GuardarTextosTicketAsync(Guid cajaId, SolicitudTextosTicket solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionTicketDto>($"api/cajas/{cajaId}/configuracion/ticket/textos", solicitud));

    /// <summary>Sin imagen, quita el logo.</summary>
    public Task<ConfiguracionTicketDto> GuardarLogoTicketAsync(Guid cajaId, SolicitudLogo solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionTicketDto>($"api/cajas/{cajaId}/configuracion/ticket/logo", solicitud));

    /// <summary>«Al cobrar»: imprimir el ticket, ofrecerlo o no imprimirlo, con «Configurar el ticket» de quien entró a la caja.</summary>
    public Task<ConfiguracionTicketDto> GuardarAlCobrarAsync(Guid cajaId, SolicitudAlCobrar solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ConfiguracionTicketDto>($"api/cajas/{cajaId}/configuracion/ticket/al-cobrar", solicitud));

    /// <summary>Lo que dice Configuración › Respaldos sin la caja principal: los respaldos se hacen y se ven allá.</summary>
    public const string RespaldosSinConexion = "Sin conexión con la caja principal: los respaldos se ven, y se respalda, cuando vuelva.";

    /// <summary>Configuración › Respaldos (2026-10-22): dónde se guardan y el último, con «Configuración de la tienda».</summary>
    public Task<RespaldosDto> RespaldosAsync(Guid cajaId, Guid usuarioId) =>
        SoloConLaPrincipalAsync<RespaldosDto>(RespaldosSinConexion, $"api/cajas/{cajaId}/configuracion/respaldos?usuarioId={usuarioId}");

    /// <summary>
    /// «Respaldar ahora»: la caja principal empieza la copia de la base y contesta enseguida (revisión del PR #133); cómo va lo
    /// dice <see cref="RespaldosAsync"/> (<see cref="RespaldosDto.Ahora"/>). Si ya había una en camino, no empieza otra.
    /// </summary>
    public Task<RespaldosDto> RespaldarAhoraAsync(Guid cajaId, SolicitudRespaldo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<RespaldosDto>($"api/cajas/{cajaId}/configuracion/respaldos", solicitud));

    /// <summary>Restaurar un respaldo (2026-10-23): los de la caja principal, el más reciente primero, y lo que dice la advertencia.</summary>
    public Task<RestaurablesDto> RespaldosParaRestaurarAsync(Guid cajaId, Guid usuarioId) =>
        SoloConLaPrincipalAsync<RestaurablesDto>(RespaldosSinConexion, $"api/cajas/{cajaId}/configuracion/respaldos/lista?usuarioId={usuarioId}");

    /// <summary>
    /// Pide restaurar un respaldo, con el PIN otra vez. La caja principal contesta con la restauración pedida y, si corre como
    /// servicio de Windows, se reinicia: cómo quedó lo dice <see cref="RespaldosAsync"/> en cuanto vuelve
    /// (<see cref="RespaldosDto.Restauracion"/>).
    /// </summary>
    public Task<RespaldosDto> RestaurarAsync(Guid cajaId, SolicitudRestaurar solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<RespaldosDto>($"api/cajas/{cajaId}/configuracion/respaldos/restaurar", solicitud));

    /// <summary>Quita la restauración pedida que espera a que la caja principal vuelva a arrancar (sin el reinicio automático).</summary>
    public Task<RespaldosDto> CancelarRestauracionAsync(Guid cajaId, SolicitudRespaldo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<RespaldosDto>($"api/cajas/{cajaId}/configuracion/respaldos/restaurar/cancelar", solicitud));

    public Task<VentaParaDevolverDto?> VentaPorFolioAsync(Guid sucursalId, string folio) =>
        ConRespaldoAsync(() => _http.GetAsync<VentaParaDevolverDto>($"api/sucursales/{sucursalId}/ventas/folio/{Uri.EscapeDataString(folio)}",
            noEncontradoEsNulo: true));

    public Task<ComprobanteDevolucionDto> CancelarAsync(Guid ventaId, SolicitudCancelacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/cancelacion", solicitud), null,
            (l, comprobante) => l.DevolucionEnLineaAsync(comprobante));

    public Task<ComprobanteDevolucionDto> DevolverAsync(Guid ventaId, SolicitudDevolucion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteDevolucionDto>($"api/ventas/{ventaId}/devoluciones", solicitud), null,
            (l, comprobante) => l.DevolucionEnLineaAsync(comprobante));

    public Task<List<TransferenciaDto>> TransferenciasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<TransferenciaDto>>($"api/sucursales/{sucursalId}/transferencias") ?? []);

    public Task VerificarTransferenciaAsync(Guid cobroId, SolicitudVerificacion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/cobros/{cobroId}/verificacion", solicitud, sinRespuesta: true));

    /// <summary>La bitácora de la sucursal entre dos días, para quien entró con «Consultar la bitácora».</summary>
    public Task<List<BitacoraDto>> ConsultarBitacoraAsync(Guid cajaId, Guid usuarioId, DateOnly desde, DateOnly hasta) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<BitacoraDto>>(
            $"api/cajas/{cajaId}/bitacora?usuarioId={usuarioId}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}") ?? []);

    // ── Las ventas de otros días y el historial de cortes (paridad con eleventa, 2026-10-17): solo con la caja principal ──

    /// <summary>
    /// Lo que dice la caja sin la caja principal: las ventas de todas las cajas y de todos los días están allá; aquí solo
    /// está la copia para vender.
    /// </summary>
    public const string VentasSinConexion =
        "Sin conexión con la caja principal: esta caja solo tiene su copia para vender. Las ventas (las de hoy, las de otros días y las de "
        + "otras cajas) se ven cuando vuelva.";

    /// <summary>Lo que dice la caja sin la caja principal: el historial junta los cortes de todas las cajas.</summary>
    public const string CortesSinConexion =
        "Sin conexión con la caja principal: el historial de cortes junta los de todas las cajas y se ve cuando vuelva.";

    /// <summary>
    /// Las ventas de un día (vacío: hoy en la caja principal) de una caja (vacía: todas) y de un cajero (vacío: todos); con
    /// <paramref name="folio"/>, las de ese folio en cualquier día (solo el número, sin los reportes, en esta caja). Las de
    /// hoy de esta caja las ve cualquiera; las de otro día o de otra caja, quien tiene «Ver reportes de ventas» (lo revisa el
    /// servicio).
    /// </summary>
    public Task<VentasDeUnDiaDto> VentasDeUnDiaAsync(Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? caja, Guid? cajero, string? folio = null) =>
        SoloConLaPrincipalAsync<VentasDeUnDiaDto>(VentasSinConexion, $"api/cajas/{cajaId}/ventas?usuarioId={usuarioId}" + Filtros(fecha, caja, cajero)
            + (string.IsNullOrWhiteSpace(folio) ? "" : $"&folio={Uri.EscapeDataString(folio.Trim())}"));

    /// <summary>
    /// Los turnos de un día (vacío: hoy) con su corte, de una caja (vacía: todas) y de un cajero (vacío: todos), para quien
    /// hace el corte de un turno o ve el del día (lo revisa el servicio).
    /// </summary>
    public Task<HistorialCortesDto> HistorialCortesAsync(Guid cajaId, Guid usuarioId, DateOnly? fecha, Guid? caja, Guid? cajero) =>
        SoloConLaPrincipalAsync<HistorialCortesDto>(CortesSinConexion, $"api/cajas/{cajaId}/cortes?usuarioId={usuarioId}" + Filtros(fecha, caja, cajero));

    // ── Los reportes de ventas (entrega 9, parte 1, 2026-10-22): solo con la caja principal ──

    /// <summary>Lo que dice la caja sin la caja principal: las ventas de todas las cajas y del año pasado están allá.</summary>
    public const string ReportesSinConexion =
        "Sin conexión con la caja principal: los reportes juntan las ventas de todas las cajas y del año pasado, y se ven cuando vuelva.";

    /// <summary>
    /// El reporte de ventas de <paramref name="periodo"/> del día <paramref name="fecha"/> (vacío: hoy en la caja principal),
    /// contra el mismo periodo del año pasado, para quien tiene «Ver reportes de ventas» (lo revisa el servicio).
    /// </summary>
    public Task<ReporteVentasDto> ReporteVentasAsync(Guid cajaId, Guid usuarioId, PeriodoReporte periodo, DateOnly? fecha = null) =>
        SoloConLaPrincipalAsync<ReporteVentasDto>(ReportesSinConexion, $"api/cajas/{cajaId}/reportes/ventas?usuarioId={usuarioId}&periodo={periodo}"
            + (fecha is { } dia ? $"&fecha={dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : ""));

    /// <summary>
    /// Ventas por período (entrega 9, parte 2): una página de los tickets de <paramref name="desde"/> a <paramref name="hasta"/>
    /// (vacíos: hoy en la caja principal) de <paramref name="caja"/> (vacía: todas), con los totales de todos esos días, para quien
    /// tiene «Ver reportes de ventas» (lo revisa el servicio).
    /// </summary>
    public Task<VentasPorPeriodoDto> VentasPorPeriodoAsync(Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? caja, int pagina = 0) =>
        SoloConLaPrincipalAsync<VentasPorPeriodoDto>(ReportesSinConexion,
            $"api/cajas/{cajaId}/reportes/ventas-por-periodo?usuarioId={usuarioId}{Dias(desde, hasta)}{(caja is { } c ? $"&caja={c}" : "")}&pagina={pagina}");

    // ── Los Excel (entrega 9, parte 2): los arma la caja principal, con los permisos de quien entró; la caja solo los guarda ──

    /// <summary>Lo que dice la caja si no puede pedir un Excel: lo arma la caja principal.</summary>
    public const string ExcelSinConexion = "Sin conexión con la caja principal: ella arma el Excel; se puede exportar cuando vuelva.";

    /// <summary>Lo que dice si la caja principal contestó que sí pero no terminó a tiempo (muchos días de tickets).</summary>
    public const string ExcelTarda = "La caja principal sigue armando el Excel y tarda más de lo normal: vuelve a intentarlo en un momento, o con menos días.";

    /// <summary>El reporte de ventas que se ve, en Excel: una hoja por tabla.</summary>
    public Task<ArchivoPorGuardar> ExcelReporteVentasAsync(Guid cajaId, Guid usuarioId, PeriodoReporte periodo, DateOnly? fecha = null) =>
        ArchivoDeLaPrincipalAsync($"api/cajas/{cajaId}/reportes/ventas/excel?usuarioId={usuarioId}&periodo={periodo}"
            + (fecha is { } dia ? $"&fecha={Iso(dia)}" : ""));

    /// <summary>Todos los tickets de esos días en Excel, con sus totales y cada día.</summary>
    public Task<ArchivoPorGuardar> ExcelVentasPorPeriodoAsync(Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? caja) =>
        ArchivoDeLaPrincipalAsync($"api/cajas/{cajaId}/reportes/ventas-por-periodo/excel?usuarioId={usuarioId}{Dias(desde, hasta)}"
            + (caja is { } c ? $"&caja={c}" : ""));

    /// <summary>Todos los movimientos del inventario de esos filtros en Excel (el costo, solo para quien lo ve).</summary>
    public Task<ArchivoPorGuardar> ExcelMovimientosAsync(Guid sucursalId, Guid usuarioId, FiltroMovimientosDto filtro) =>
        ArchivoDeLaPrincipalAsync($"api/sucursales/{sucursalId}/inventario/movimientos/excel?usuarioId={usuarioId}"
            + $"&desde={Iso(filtro.Desde)}&hasta={Iso(filtro.Hasta)}"
            + (filtro.Clase is { } clase ? $"&clase={clase}" : "")
            + (string.IsNullOrWhiteSpace(filtro.Texto) ? "" : $"&q={Uri.EscapeDataString(filtro.Texto.Trim())}")
            + (filtro.DepartamentoId is { } d ? $"&departamentoId={d}" : "")
            + (filtro.QuienId is { } q ? $"&quienId={q}" : ""));

    /// <summary>Todo lo que está en su mínimo o debajo con esos filtros, en Excel.</summary>
    public Task<ArchivoPorGuardar> ExcelBajosAsync(Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? texto, bool soloConMinimo) =>
        ArchivoDeLaPrincipalAsync($"api/sucursales/{sucursalId}/inventario/bajos/excel?usuarioId={usuarioId}&conMinimo={soloConMinimo}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "")
            + (string.IsNullOrWhiteSpace(texto) ? "" : $"&q={Uri.EscapeDataString(texto.Trim())}"));

    /// <summary>Lo que vale el inventario en Excel: cada departamento y todos los productos del elegido con ese filtro.</summary>
    public Task<ArchivoPorGuardar> ExcelValorAsync(Guid sucursalId, Guid usuarioId, Guid? departamentoId, FiltroValor filtro) =>
        ArchivoDeLaPrincipalAsync($"api/sucursales/{sucursalId}/inventario/valor/excel?usuarioId={usuarioId}&filtro={filtro}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : ""));

    /// <summary>
    /// Los productos de la lista con esos filtros (Productos › Productos en Excel, 2026-10-22), en Excel: una fila por variante y sus
    /// presentaciones en otra hoja; el costo y el margen, solo para quien los ve.
    /// </summary>
    public Task<ArchivoPorGuardar> ExcelProductosAsync(Guid sucursalId, Guid usuarioId, string? texto, FiltroProductos filtro, Guid? departamentoId,
        bool inactivos) =>
        ArchivoDeLaPrincipalAsync($"api/catalogo/lista/excel?sucursalId={sucursalId}&usuarioId={usuarioId}&q={Uri.EscapeDataString(texto ?? "")}"
            + $"&filtro={filtro}" + (departamentoId is { } d ? $"&departamentoId={d}" : "") + $"&inactivos={inactivos}");

    /// <summary>
    /// Lo completo de un renglón de la bitácora que cambió muchas cosas a la vez (una importación de Excel: cada producto con lo de
    /// antes y lo que quedó), en Excel.
    /// </summary>
    public Task<ArchivoPorGuardar> ExcelDetalleBitacoraAsync(Guid cajaId, Guid usuarioId, Guid bitacoraId) =>
        ArchivoDeLaPrincipalAsync($"api/cajas/{cajaId}/bitacora/{bitacoraId}/excel?usuarioId={usuarioId}");

    /// <summary>
    /// Un archivo que arma la caja principal: sin ella, lo dice sin esperar. Con su propio tope (<see cref="RedCaja.TopeDescargas"/>):
    /// si se le acaba, la caja principal sigue armándolo, no se fue, y la caja no pasa a sin conexión por eso (revisión del PR
    /// #134; si de verdad se fue, el latido lo nota).
    /// </summary>
    private async Task<ArchivoPorGuardar> ArchivoDeLaPrincipalAsync(string url)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(ExcelSinConexion, "sin-conexion");
        try
        {
            return await _archivos.GetArchivoAsync(url);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion" && ex.PorPlazo && contingencia is not { ServidorResponde: false })
        {
            throw new ErrorCaja(ExcelTarda, "plazo") { PorPlazo = true };
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            contingencia?.MarcarSinConexion();
            throw new ErrorCaja(ExcelSinConexion, "sin-conexion");
        }
    }

    private static string Iso(DateOnly dia) => dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Dias(DateOnly? desde, DateOnly? hasta) =>
        (desde is { } d ? $"&desde={Iso(d)}" : "") + (hasta is { } h ? $"&hasta={Iso(h)}" : "");

    private static string Filtros(DateOnly? fecha, Guid? caja, Guid? cajero) =>
        (fecha is { } dia ? $"&fecha={dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "") + (caja is { } c ? $"&caja={c}" : "")
        + (cajero is { } u ? $"&cajero={u}" : "");

    /// <summary>
    /// Lo que solo sabe la caja principal (lo de todas las cajas y todos los días): sin ella, el error lo dice con
    /// <paramref name="sinConexion"/>, sin esperar. Si deja de contestar a media consulta, la caja lo sabe desde ya, como con
    /// lo demás.
    /// </summary>
    private async Task<T> SoloConLaPrincipalAsync<T>(string sinConexion, string url)
    {
        if (contingencia is { ServidorResponde: false })
            throw new ErrorCaja(sinConexion, "sin-conexion");
        try
        {
            return (await _http.GetAsync<T>(url))!;
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            contingencia?.MarcarSinConexion();
            throw new ErrorCaja(sinConexion, "sin-conexion");
        }
    }

    // ── Usuarios y permisos (entrega 1, solo con la caja principal): cada cambio regresa la lista completa ──

    /// <summary>Los usuarios con sus permisos y los de defecto, para quien entró con «Usuarios y permisos».</summary>
    public Task<UsuariosYPermisosDto> UsuariosYPermisosAsync(Guid cajaId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos?usuarioId={usuarioId}"))!);

    /// <summary>Un permiso de un usuario o, sin usuario, de los de defecto.</summary>
    public Task<UsuariosYPermisosDto> CambiarPermisoAsync(Guid cajaId, Guid? usuarioId, SolicitudPermisoUsuario solicitud) =>
        ConRespaldoAsync(async () => (await _http.PutAsync<UsuariosYPermisosDto>(
            $"api/cajas/{cajaId}/usuarios-y-permisos/{(usuarioId is { } id ? id.ToString() : "defecto")}", solicitud))!);

    public Task<UsuariosYPermisosDto> CopiarPermisosAsync(Guid cajaId, Guid usuarioId, SolicitudCopiarPermisos solicitud) =>
        ConRespaldoAsync(async () => (await _http.PostAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/{usuarioId}/copiar", solicitud))!);

    public Task<UsuariosYPermisosDto> TodosLosPermisosAsync(Guid cajaId, Guid usuarioId, SolicitudUsuarios solicitud) =>
        ConRespaldoAsync(async () => (await _http.PostAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/{usuarioId}/todos", solicitud))!);

    public Task<UsuariosYPermisosDto> PermisosDeDefectoAsync(Guid cajaId, Guid usuarioId, SolicitudUsuarios solicitud) =>
        ConRespaldoAsync(async () => (await _http.PostAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/{usuarioId}/defecto", solicitud))!);

    public Task<UsuariosYPermisosDto> NuevoUsuarioAsync(Guid cajaId, SolicitudNuevoUsuario solicitud) =>
        ConRespaldoAsync(async () => (await _http.PostAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/usuarios", solicitud))!);

    public Task<UsuariosYPermisosDto> CambiarPinAsync(Guid cajaId, Guid usuarioId, SolicitudPinUsuario solicitud) =>
        ConRespaldoAsync(async () => (await _http.PutAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/{usuarioId}/pin", solicitud))!);

    public Task<UsuariosYPermisosDto> ActivarUsuarioAsync(Guid cajaId, Guid usuarioId, SolicitudActivarUsuario solicitud) =>
        ConRespaldoAsync(async () => (await _http.PutAsync<UsuariosYPermisosDto>($"api/cajas/{cajaId}/usuarios-y-permisos/{usuarioId}/activo", solicitud))!);

    // ── Catálogo (F3, solo con la caja principal) ──

    public Task<List<ProductoCatalogoDto>> CatalogoAsync(Guid sucursalId, string? texto, Guid? departamentoId, bool porCompletar, bool inactivos) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoCatalogoDto>>(
            $"api/catalogo/productos?sucursalId={sucursalId}&q={Uri.EscapeDataString(texto ?? "")}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "") + $"&porCompletar={porCompletar}&inactivos={inactivos}") ?? []);

    public Task<List<SinonimoDto>> SinonimosAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<List<SinonimoDto>>("api/catalogo/sinonimos"))!);

    public Task<List<SinonimoDto>> AgregarSinonimoAsync(SolicitudSinonimo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<List<SinonimoDto>>("api/catalogo/sinonimos", solicitud));

    public Task<List<SinonimoDto>> QuitarSinonimoAsync(Guid id, SolicitudQuitarSinonimo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<List<SinonimoDto>>($"api/catalogo/sinonimos/{id}/quitar", solicitud));

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

    /// <summary>La lista de la página de Productos (entrega 8): abre llena, lo más vendido primero; los primeros <paramref name="maximo"/>.</summary>
    public Task<ListaProductosDto> ListaProductosAsync(Guid sucursalId, Guid usuarioId, string? texto, FiltroProductos filtro, Guid? departamentoId,
        bool inactivos, int maximo) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ListaProductosDto>(
            $"api/catalogo/lista?sucursalId={sucursalId}&usuarioId={usuarioId}&q={Uri.EscapeDataString(texto ?? "")}&filtro={filtro}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "") + $"&inactivos={inactivos}&maximo={maximo}"))!);

    /// <summary>Los últimos movimientos del kardex de una variante, con lo que quedó después de cada uno (el detalle de Productos).</summary>
    public Task<List<MovimientoProductoDto>> UltimosMovimientosAsync(Guid varianteId, Guid sucursalId, int maximo = 8) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<MovimientoProductoDto>>(
            $"api/catalogo/variantes/{varianteId}/movimientos?sucursalId={sucursalId}&maximo={maximo}") ?? []);

    public Task<ProductoVentaDto> AltaRapidaAsync(SolicitudAltaRapida solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ProductoVentaDto>("api/catalogo/alta-rapida", solicitud));

    private sealed record CodigoGenerado(string Codigo);

    public Task DarDeBajaAsync(SolicitudBaja solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/baja", solicitud, sinRespuesta: true));

    public Task JuntarAsync(SolicitudJuntar solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/juntar", solicitud, sinRespuesta: true));

    public Task CambiarUnidadAsync(SolicitudCambiarUnidad solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/unidad", solicitud, sinRespuesta: true));

    public Task<List<MayoreoPorRevisarDto>> MayoreoPorRevisarAsync() =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<MayoreoPorRevisarDto>>("api/catalogo/depuracion/mayoreo") ?? []);

    public Task DecidirMayoreoAsync(SolicitudMayoreo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>("api/catalogo/depuracion/mayoreo", solicitud, sinRespuesta: true));

    public Task<List<DecisionDepuracionDto>> DecisionesDepuracionAsync() =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<DecisionDepuracionDto>>("api/catalogo/depuracion") ?? []);

    public Task<List<VarianteParaJuntarDto>> VariantesParaJuntarAsync(string texto, Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<VarianteParaJuntarDto>>(
            $"api/catalogo/variantes?q={Uri.EscapeDataString(texto)}&sucursalId={sucursalId}") ?? []);

    // ── Departamentos (Productos › Departamentos, solo con la caja principal) ──

    /// <summary>Todos, con cuántos productos activos tiene cada uno, y si en el piloto con eleventa se cambian allá.</summary>
    public Task<DepartamentosDto> DepartamentosAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<DepartamentosDto>("api/catalogo/departamentos"))!);

    public Task<DepartamentosDto> DarDeAltaDepartamentoAsync(SolicitudDepartamento solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<DepartamentosDto>("api/catalogo/departamentos", solicitud));

    public Task<DepartamentosDto> CambiarNombreDepartamentoAsync(Guid departamentoId, SolicitudDepartamento solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<DepartamentosDto>($"api/catalogo/departamentos/{departamentoId}/nombre", solicitud));

    /// <summary>Si tiene productos activos, pasan antes al departamento de <see cref="SolicitudDesactivarDepartamento.PasarA"/>.</summary>
    public Task<DepartamentosDto> DesactivarDepartamentoAsync(Guid departamentoId, SolicitudDesactivarDepartamento solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<DepartamentosDto>($"api/catalogo/departamentos/{departamentoId}/desactivar", solicitud));

    public Task<DepartamentosDto> ReactivarDepartamentoAsync(Guid departamentoId, SolicitudReactivarDepartamento solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<DepartamentosDto>($"api/catalogo/departamentos/{departamentoId}/reactivar", solicitud));

    /// <summary>
    /// En el piloto con eleventa (con su lectura prendida), lo que dicen Productos e Inventario: de los productos de eleventa
    /// no se cambia lo que pone la lectura, y no se ofrece lo que mueve existencias. Vacíos, fuera del piloto.
    /// </summary>
    public Task<TrastiendaEnElPilotoDto> TrastiendaEnElPilotoAsync() =>
        ConRespaldoAsync(async () => (await _http.GetAsync<TrastiendaEnElPilotoDto>("api/catalogo/piloto"))!);

    // ── Cambiar precios de varios a la vez (Productos › Cambiar precios, solo con la caja principal) ──

    /// <summary>Cómo quedan los precios con esa regla, sin cambiar nada; el margen, solo para quien ve la utilidad.</summary>
    public Task<VistaPreciosDeVariosDto> VistaPreciosDeVariosAsync(SolicitudPreciosDeVarios solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<VistaPreciosDeVariosDto>("api/catalogo/precios-de-varios/vista", solicitud));

    /// <summary>
    /// Aplica lo que se vio (con su huella y el Id de esta vez): si algo cambió desde la vista, el servicio no lo aplica; si
    /// ya lo había aplicado (se perdió la respuesta), no lo hace otra vez.
    /// </summary>
    public Task<PreciosDeVariosAplicadosDto> CambiarPreciosDeVariosAsync(SolicitudPreciosDeVarios solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<PreciosDeVariosAplicadosDto>("api/catalogo/precios-de-varios", solicitud));

    // ── Promociones (Productos › Promociones, fase 3; solo con la caja principal) ──

    public Task<ListaPromocionesDto> PromocionesAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ListaPromocionesDto>($"api/promociones?sucursalId={sucursalId}&usuarioId={usuarioId}"))!);

    public Task<PromocionFichaDto> PromocionAsync(Guid id, Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<PromocionFichaDto>($"api/promociones/{id}?sucursalId={sucursalId}&usuarioId={usuarioId}"))!);

    public Task<VistaPromocionDto> VistaPromocionAsync(SolicitudPromocion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<VistaPromocionDto>("api/promociones/vista", solicitud));

    public Task<ProductosPromocionDto> ProductosPromocionAsync(SolicitudProductosPromocion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ProductosPromocionDto>("api/promociones/productos", solicitud));

    public Task<PromocionFichaDto> GuardarPromocionAsync(SolicitudPromocion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<PromocionFichaDto>("api/promociones", solicitud));

    public Task<PromocionFichaDto> TerminarPromocionAsync(Guid id, SolicitudTerminarPromocion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<PromocionFichaDto>($"api/promociones/{id}/terminar", solicitud));

    // ── Productos en Excel: importar (Productos › Productos en Excel, 2026-10-22, solo con la caja principal) ──

    /// <summary>Qué cambia con ese Excel, sin cambiar nada: lo que cambia producto por producto, lo nuevo y lo que se ignora (y por qué).</summary>
    public Task<VistaImportacionDto> VistaImportacionAsync(SolicitudImportarProductos solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<VistaImportacionDto>("api/catalogo/importar/vista", solicitud));

    /// <summary>
    /// Aplica lo que se vio (el mismo archivo, con su huella y el Id de esta vez): si algo cambió desde la vista, el servicio no lo
    /// aplica; si ya lo había aplicado (se perdió la respuesta), no lo hace otra vez.
    /// </summary>
    public Task<ImportacionAplicadaDto> ImportarProductosAsync(SolicitudImportarProductos solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ImportacionAplicadaDto>("api/catalogo/importar", solicitud));

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

    // ── Agregar inventario, como en eleventa (2026-10-24): solo con la caja principal ──

    /// <summary>El producto que se va a agregar: su existencia de ahora y, para quien ve los costos, su costo de ahora.</summary>
    public Task<ProductoParaAgregarDto> ParaAgregarAsync(Guid sucursalId, Guid varianteId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ProductoParaAgregarDto>(
            $"api/sucursales/{sucursalId}/inventario/agregar/{varianteId}?usuarioId={usuarioId}"))!);

    /// <summary>
    /// Agrega lo que llegó. Con el mismo Id (se perdió la respuesta y se manda otra vez), una sola vez. Si la red falla, el error es
    /// el de la red tal cual (revisión del PR #138): dice si ni se mandó (<see cref="ErrorCaja.NoSeMando"/>) o si no se sabe.
    /// </summary>
    public Task<AgregadoDto> AgregarInventarioAsync(SolicitudAgregarInventario solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<AgregadoDto>("api/inventario/agregar", solicitud), siSePierde: error => error);

    /// <summary>Lo agregado hoy en la sucursal, de lo más reciente a lo más viejo; el costo, solo para quien lo ve.</summary>
    public Task<AgregadoHoyDto> AgregadoHoyAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<AgregadoHoyDto>($"api/sucursales/{sucursalId}/inventario/agregado-hoy?usuarioId={usuarioId}"))!);

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

    /// <summary>Lo pendiente de cada tarea del inventario (solo cuentas), para sus tarjetas (entrega 8).</summary>
    public Task<PendientesInventarioDto> PendientesInventarioAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<PendientesInventarioDto>($"api/sucursales/{sucursalId}/inventario/pendientes"))!);

    public Task<SugeridoDto> SugeridoAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<SugeridoDto>($"api/sucursales/{sucursalId}/sugerido?usuarioId={usuarioId}"))!);

    public Task<ParametrosSugeridoDto> ParametrosSugeridoAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ParametrosSugeridoDto>($"api/sucursales/{sucursalId}/sugerido/parametros"))!);

    public Task<ParametrosSugeridoDto> GuardarParametrosSugeridoAsync(Guid sucursalId, SolicitudParametrosSugerido solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<ParametrosSugeridoDto>($"api/sucursales/{sucursalId}/sugerido/parametros", solicitud));

    public Task<OrdenDto> OrdenDelSugeridoAsync(Guid sucursalId, SolicitudOrdenSugerida solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<OrdenDto>($"api/sucursales/{sucursalId}/sugerido/orden", solicitud));

    public Task<MargenesDto> MargenesAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<MargenesDto>($"api/sucursales/{sucursalId}/margenes"))!);

    public Task<MargenesDto> GuardarMargenesAsync(Guid sucursalId, SolicitudMargenes solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<MargenesDto>($"api/sucursales/{sucursalId}/margenes", solicitud));

    // ── Los reportes del inventario (F4, como en eleventa; solo con la caja principal) ──

    /// <summary>Lo que vale el inventario a costo, por departamento, y una página de los productos del elegido (con «Ver costos…»).</summary>
    public Task<ValorInventarioDto> ValorInventarioAsync(Guid sucursalId, Guid usuarioId, Guid? departamentoId, FiltroValor filtro, int pagina) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<ValorInventarioDto>(
            $"api/sucursales/{sucursalId}/inventario/valor?usuarioId={usuarioId}&filtro={filtro}&pagina={pagina}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "")))!);

    /// <summary>Una página de los movimientos del kardex con esos filtros, del más reciente al más viejo.</summary>
    public Task<MovimientosInventarioDto> MovimientosInventarioAsync(Guid sucursalId, Guid usuarioId, FiltroMovimientosDto filtro, int pagina) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<MovimientosInventarioDto>(
            $"api/sucursales/{sucursalId}/inventario/movimientos?usuarioId={usuarioId}"
            + $"&desde={filtro.Desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&hasta={filtro.Hasta.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + (filtro.Clase is { } clase ? $"&clase={clase}" : "")
            + (string.IsNullOrWhiteSpace(filtro.Texto) ? "" : $"&q={Uri.EscapeDataString(filtro.Texto.Trim())}")
            + (filtro.DepartamentoId is { } d ? $"&departamentoId={d}" : "")
            + (filtro.QuienId is { } q ? $"&quienId={q}" : "")
            + $"&pagina={pagina}"))!);

    /// <summary>Para el filtro «Quién» de los movimientos: los usuarios de la sucursal, también los dados de baja.</summary>
    public Task<List<OpcionCatalogoDto>> QuienesMovimientosAsync(Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<OpcionCatalogoDto>>(
            $"api/sucursales/{sucursalId}/inventario/movimientos/quienes?usuarioId={usuarioId}") ?? []);

    /// <summary>Lo que está en su mínimo o debajo, por departamento, con cuánto pedir: una página.</summary>
    public Task<BajosInventarioDto> BajosInventarioAsync(Guid sucursalId, Guid usuarioId, Guid? departamentoId, string? texto, bool soloConMinimo, int pagina) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<BajosInventarioDto>(
            $"api/sucursales/{sucursalId}/inventario/bajos?usuarioId={usuarioId}&conMinimo={soloConMinimo}&pagina={pagina}"
            + (departamentoId is { } d ? $"&departamentoId={d}" : "")
            + (string.IsNullOrWhiteSpace(texto) ? "" : $"&q={Uri.EscapeDataString(texto.Trim())}")))!);

    // ── Clientes registrados (fase 3, entrega 2) ──

    /// <summary>Los activos, para elegir uno en la venta. Sin conexión, los de la copia local.</summary>
    public Task<List<ClienteRegistradoDto>> ClientesRegistradosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ClienteRegistradoDto>>($"api/sucursales/{sucursalId}/clientes-registrados") ?? [],
            l => l.Clientes());

    /// <summary>La pantalla de clientes, con lo que compra cada uno (solo con la caja principal).</summary>
    public Task<List<ClienteResumenDto>> ListaClientesAsync(Guid sucursalId, bool inactivos) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ClienteResumenDto>>($"api/sucursales/{sucursalId}/clientes?inactivos={inactivos}") ?? []);

    public Task<FichaClienteDto?> FichaClienteAsync(Guid clienteId) =>
        ConRespaldoAsync(() => _http.GetAsync<FichaClienteDto>($"api/clientes/{clienteId}", noEncontradoEsNulo: true));

    public Task<ClienteRegistradoDto> GuardarClienteAsync(SolicitudGuardarCliente solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<ClienteRegistradoDto>("api/clientes", solicitud));

    // ── Crédito y abonos (fase 3, entrega 2, parte 2): con la caja principal ──

    /// <summary>La cuenta del cliente con sus últimos movimientos (solo con la caja principal).</summary>
    public Task<CuentaClienteDto> CuentaClienteAsync(Guid clienteId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<CuentaClienteDto>($"api/clientes/{clienteId}/credito"))!);

    /// <summary>Lo que debe y sus condiciones; sin conexión, lo que sabe esta caja (marcado como estimado).</summary>
    public Task<CreditoClienteDto> CreditoAsync(Guid clienteId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<CuentaClienteDto>($"api/clientes/{clienteId}/credito"))!.Credito,
            l => l.Credito(clienteId) ?? throw new InvalidOperationException("Esta caja todavía no conoce los saldos del crédito: espera a que vuelva la conexión."));

    /// <summary>Las cuentas de los que tienen crédito o deben algo; sin conexión, las que conoce esta caja (sin los saldos, un error).</summary>
    public Task<List<CreditoClienteDto>> CreditosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<CreditoClienteDto>>($"api/sucursales/{sucursalId}/creditos") ?? [], l => l.Creditos());

    public Task<ClienteRegistradoDto> GuardarCondicionesAsync(Guid clienteId, SolicitudCreditoCliente s) =>
        ConRespaldoAsync(() => _http.PutAsync<ClienteRegistradoDto>($"api/clientes/{clienteId}/credito", s));

    public Task<CuentaClienteDto> AjustarCreditoAsync(Guid clienteId, SolicitudAjusteCredito s) =>
        ConRespaldoAsync(() => _http.PostAsync<CuentaClienteDto>($"api/clientes/{clienteId}/ajustes", s));

    /// <summary>
    /// El Id lo pone la caja: si la respuesta se pierde y se manda otra vez (o se hace sin conexión), se guarda uno. Se
    /// anota en la cuenta propia antes de mandarlo; sin conexión, con el saldo que conoce esta caja.
    /// </summary>
    /// <param name="soloEnLinea">Se perdió la respuesta de este mismo abono: hasta saber si quedó, solo se manda en línea.</param>
    public Task<ComprobanteAbonoDto> AbonarAsync(SolicitudAbono s, bool soloEnLinea = false) =>
        soloEnLinea && contingencia is { EnLinea: false } ? Task.FromException<ComprobanteAbonoDto>(AbonoSinConfirmar()) : ConRespaldoAsync(async () =>
            {
                if (contingencia is not null)
                    await contingencia.AnotarAbonoAsync(s);
                try
                {
                    return await _http.PostAsync<ComprobanteAbonoDto>("api/abonos", s);
                }
                catch (ErrorCaja ex) when (ex.Codigo != "sin-conexion")
                {
                    if (contingencia is not null)
                        await contingencia.QuitarCreditoAsync(s.Id);
                    throw;
                }
            }, l => l.Abonar(s), (l, c) => l.AbonoEnLineaAsync(s.TurnoId, c), siSePierde: _ => AbonoSinConfirmar());

    private static ErrorCaja AbonoSinConfirmar() => new(
        "Se perdió la conexión al abonar y no se sabe si quedó. Cuando vuelva la conexión, abona otra vez sin cerrar esta ventana "
        + "(si ya quedó, sale el mismo comprobante); mientras, no lo cobres de nuevo.", "abono-sin-confirmar");

    public Task<ComprobanteAbonoDto> CancelarAbonoAsync(Guid abonoId, Guid turnoId, SolicitudCancelacion s) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteAbonoDto>($"api/abonos/{abonoId}/cancelacion", s),
            trasEnLinea: (l, c) => l.AbonoCanceladoEnLineaAsync(turnoId, c));

    /// <summary>El estado de cuenta (parte 3), con el encabezado del ticket de esta sucursal: solo con la caja principal.</summary>
    /// <param name="desde">Vacío: desde la compra más vieja que sigue sin pagarse (y al menos los últimos 30 días).</param>
    public Task<EstadoCuentaDto> EstadoDeCuentaAsync(Guid clienteId, Guid sucursalId, DateOnly? desde = null) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<EstadoCuentaDto>($"api/clientes/{clienteId}/estado-de-cuenta?sucursalId={sucursalId}"
            + (desde is { } d ? $"&desde={d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "")))!);

    /// <summary>
    /// Devolverle en efectivo lo que tiene a favor (parte 3): solo en línea, porque se decide con el saldo de la caja
    /// principal, y no si esta caja todavía tiene algo del cliente sin subir. El Id lo pone la caja: si se pierde la
    /// respuesta y se manda otra vez, sale el mismo comprobante. Se anota en la cuenta propia antes de mandarlo, para que
    /// sin conexión ya no se cuente con ese saldo a favor.
    /// </summary>
    /// <param name="montoCentavos">Lo que va a subir la cuenta (el mismo cálculo que el servicio).</param>
    public Task<ComprobanteAbonoDto> DevolverSaldoAsync(Guid clienteId, SolicitudDevolverSaldo s, long montoCentavos) =>
        ConRespaldoAsync(async () =>
            {
                if (contingencia?.CreditoSinSubir(clienteId) == true)
                    throw new ErrorCaja("Esta caja todavía tiene lo fiado o abonado de este cliente sin subir a la caja principal: "
                        + "espera a que suba para devolverle su saldo.");
                if (contingencia is not null)
                    await contingencia.AnotarSaldoDevueltoAsync(s.Id, clienteId, montoCentavos);
                try
                {
                    var comprobante = await _http.PostAsync<ComprobanteAbonoDto>($"api/clientes/{clienteId}/saldo-devuelto", s);
                    _devolucionesSinConfirmar.TryRemove(clienteId, out _);
                    return comprobante;
                }
                catch (ErrorCaja ex) when (ex.Codigo != "sin-conexion")
                {
                    // No quedó: ya no se cuenta, y si había una sin confirmar, ya se sabe que no pasó.
                    if (contingencia is not null)
                        await contingencia.QuitarCreditoAsync(s.Id);
                    if (ex.Codigo != "requiere-autorizacion")
                        _devolucionesSinConfirmar.TryRemove(clienteId, out _);
                    throw;
                }
            }, null, (l, c) => l.SaldoDevueltoEnLineaAsync(s.TurnoId, c), siSePierde: _ =>
            {
                _devolucionesSinConfirmar[clienteId] = (s, montoCentavos);
                return SaldoDevueltoSinConfirmar();
            });

    /// <summary>
    /// La devolución de saldo que se mandó y no se sabe si quedó (se perdió la respuesta): aunque se cierre la ventana, al
    /// volver a su cuenta se reintenta con el mismo Id hasta saberlo.
    /// </summary>
    public (SolicitudDevolverSaldo Solicitud, long MontoCentavos)? DevolucionSinConfirmar(Guid clienteId) =>
        _devolucionesSinConfirmar.TryGetValue(clienteId, out var pendiente) ? pendiente : null;

    private readonly ConcurrentDictionary<Guid, (SolicitudDevolverSaldo Solicitud, long MontoCentavos)> _devolucionesSinConfirmar = new();

    private static ErrorCaja SaldoDevueltoSinConfirmar() => new(
        "Se perdió la conexión al devolver el saldo y no se sabe si quedó. No entregues el efectivo todavía: cuando vuelva la conexión, "
        + "oprime «Devolver» otra vez (si ya quedó, sale el mismo comprobante).", "saldo-sin-confirmar");

    public Task<ComprobanteAbonoDto> ReimprimirAbonoAsync(Guid abonoId, SolicitudReimpresion s) =>
        ConRespaldoAsync(() => _http.PostAsync<ComprobanteAbonoDto>($"api/abonos/{abonoId}/reimpresion", s));

    public Task<List<AbonoDelDiaDto>> AbonosDelDiaAsync(Guid cajaId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<AbonoDelDiaDto>>($"api/cajas/{cajaId}/abonos-del-dia") ?? []);

    /// <summary>Lo del crédito sin conexión que no cuadró y nadie ha revisado (solo con la caja principal).</summary>
    public Task<List<RevisionCreditoDto>> RevisionesCreditoAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<RevisionCreditoDto>>($"api/sucursales/{sucursalId}/creditos/por-revisar") ?? []);

    public Task MarcarRevisadaAsync(Guid revisionId, SolicitudRevision s) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/creditos/revisiones/{revisionId}", s, sinRespuesta: true));

    public Task VerificarAbonoAsync(Guid abonoId, SolicitudVerificacion s) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/abonos/{abonoId}/verificacion", s, sinRespuesta: true));

    // ── Descuentos ──

    /// <summary>Solo con la caja principal (ahí está el costo); sin conexión, el costo no se revisa.</summary>
    public Task<BajoCostoDto> BajoCostoAsync(SolicitudBajoCosto solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<BajoCostoDto>("api/descuentos/bajo-costo",
            solicitud with { DiaPromociones = solicitud.DiaPromociones ?? DiaPromociones }), _ => new BajoCostoDto(0, []));

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

    public Task<CalibracionDto> AplicarCalibracionAsync(Guid sucursalId, SolicitudCalibracion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CalibracionDto>($"api/sucursales/{sucursalId}/impresora/calibracion", solicitud));

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
    // Van directo a su servicio (en esta misma PC), sin pasar por sin conexión: también contestan mientras se simula que
    // no hay conexión con la caja principal.

    public async Task<List<AvisoPruebaDto>> AvisosPruebaAsync() => await _http.GetAsync<List<AvisoPruebaDto>>("api/prueba/avisos") ?? [];

    /// <summary>Las de esa versión de la guía (<see cref="GuiaPrueba.Version"/>): las de otra no se cargan.</summary>
    public async Task<RespuestasPruebaDto> RespuestasPruebaAsync(int version) =>
        (await _http.GetAsync<RespuestasPruebaDto>($"api/prueba/respuestas?version={version}"))!;

    public Task<RespuestasPruebaDto> GuardarRespuestasPruebaAsync(RespuestasPruebaDto respuestas) =>
        _http.PutAsync<RespuestasPruebaDto>("api/prueba/respuestas", respuestas);

    public Task<ArchivoGuardadoDto> ExportarRespuestasPruebaAsync(string texto) =>
        _http.PostAsync<ArchivoGuardadoDto>("api/prueba/respuestas/exportar", new SolicitudExportarRespuestas(texto));

    /// <summary>El documento al escritorio y la guía en blanco para la siguiente persona; devuelve dónde quedó el documento.</summary>
    public Task<ArchivoGuardadoDto> DejarGuiaEnBlancoAsync(int version, string texto) =>
        _http.PostAsync<ArchivoGuardadoDto>("api/prueba/respuestas/en-blanco", new SolicitudGuiaEnBlanco(version, texto));

    public async Task EmpezarDeCeroAsync()
    {
        // Lo que falta subir es de la base que se va a quitar: primero que suba.
        if (conexion.SimularSinConexion || contingencia is { Pendientes: > 0 })
            throw new ErrorCaja("Primero vuelve a conectar y espera a que suba lo que se hizo sin conexión.");
        await _http.PostAsync<object>("api/prueba/empezar-de-cero", null, sinRespuesta: true);
        // Lo de la caja (el crédito propio, los saldos, el turno, el folio y los tickets en espera) era de la base anterior.
        contingencia?.OlvidarCredito();
    }

    /// <summary>
    /// Los tickets en espera, en la base local de la caja (caja-local.db): un apagón o un reinicio no los borra. Sin base
    /// local (el navegador de desarrollo sin Caja:BaseLocal) no se guardan, como antes.
    /// </summary>
    public List<TicketGuardado> TicketsEnEspera() => contingencia?.TicketsEnEspera() ?? [];

    /// <summary>En la cola del escritor de la base local: la pantalla no se detiene mientras se guardan.</summary>
    public Task GuardarEnEsperaAsync(IReadOnlyList<TicketGuardado> tickets) => contingencia?.GuardarEnEsperaAsync(tickets) ?? Task.CompletedTask;

    /// <summary>Si los tickets en espera se guardan en esta caja: si no, al dejar la venta se pierden (el corte lo avisa).</summary>
    public bool GuardaEspera => contingencia is not null;

    // ── Alta de la caja (antes de tener llave; el servicio todavía no es el guardado) ──

    public Task<EstadoServicioDto?> EstadoAsync(Uri servicio) => _http.GetAsync<EstadoServicioDto>("api/estado", servicio: servicio);

    /// <summary>
    /// Si el servicio está en el piloto con eleventa (<see cref="PilotoConEleventa"/>): entonces la caja solo cobra y
    /// devuelve en efectivo. Sin conexión, lo último que se supo (la contingencia lo guarda). Solo para convivir con
    /// eleventa: se quita al dejarlo.
    /// </summary>
    public async Task<bool> PilotoEleventaAsync()
    {
        if (contingencia is { EnLinea: false })
            return contingencia.PilotoEleventa;
        try
        {
            var piloto = (await _http.GetAsync<EstadoServicioDto>("api/estado"))?.PilotoEleventa == true;
            if (contingencia is not null)
                await contingencia.RecordarPilotoEleventaAsync(piloto);
            return piloto;
        }
        catch (ErrorCaja) when (contingencia is not null)
        {
            return contingencia.PilotoEleventa;
        }
    }

    /// <summary>
    /// La bitácora de red de la caja principal (Configuración › Acerca de): sus últimas caídas, dónde está y lo que ve ahora. Vacía
    /// si no contesta o es un servicio de antes: no espera a una caja principal que no está.
    /// </summary>
    public async Task<ResumenDeRedDto?> RedDeLaPrincipalAsync()
    {
        if (contingencia is { EnLinea: false })
            return null;
        try
        {
            return await _http.GetAsync<ResumenDeRedDto>("api/red", noEncontradoEsNulo: true, espera: PlazoLectura);
        }
        catch (ErrorCaja)
        {
            return null;
        }
    }

    /// <summary>La versión del servicio de la caja principal (Configuración › Acerca de); vacía si no contesta.</summary>
    public async Task<string?> VersionDeLaPrincipalAsync()
    {
        if (contingencia is { EnLinea: false })
            return null;
        return (await _http.GetAsync<EstadoServicioDto>("api/estado"))?.Version;
    }

    public Task<List<SucursalAltaDto>> OpcionesAltaAsync(Uri servicio, SolicitudCredenciales credenciales) =>
        _http.PostAsync<List<SucursalAltaDto>>("api/alta/opciones", credenciales, servicio: servicio);

    public Task<AltaCajaDto> AltaAsync(Uri servicio, SolicitudAltaCaja solicitud) => _http.PostAsync<AltaCajaDto>("api/alta", solicitud, servicio: servicio);

    /// <summary>Quita la PC de una caja (con «Dar de alta y quitar cajas» de quien entró): esa PC ya no vende hasta darla de alta otra vez.</summary>
    public Task QuitarEquipoAsync(Guid cajaId, SolicitudQuitarEquipo solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<object>($"api/cajas/{cajaId}/quitar-equipo", solicitud, sinRespuesta: true));

    /// <summary>
    /// En línea, va a la caja principal y le avisa a la contingencia lo que pasó (para su cuenta del turno). Sin
    /// conexión, usa la copia local si esa operación se puede hacer sin la caja principal.
    /// </summary>
    /// <param name="siSePierde">
    /// Si la caja principal no contesta a media operación (puede que ya la haya guardado) y sin conexión tampoco se puede
    /// hacer: el error que lo dice, en lugar del de sin conexión (con el de la red, para saber si llegó). Vacío: el de sin
    /// conexión.
    /// </param>
    /// <param name="plazoDeLaVenta">Con lo que tiene copia, los plazos de la venta; sin esto, el tope de siempre (el corte Z).</param>
    /// <remarks>
    /// Los plazos (entrega 5, parte 2): lo que también se puede hacer con la copia lleva los de la venta
    /// (<see cref="Transporte.PlazoLectura"/>, <see cref="Transporte.PlazoEscritura"/>) en lugar de 15 s; si pasa el plazo, se
    /// hace con la copia (el cobro, a la bandeja con el mismo Id) y la red queda lenta. Con la red lenta, lo que tiene copia se
    /// hace con ella sin preguntar; lo que no (devolver, el catálogo, los reportes) se intenta con la caja principal, con su
    /// tope de siempre, si no hay nada por subir. Los plazos son solo de la operación: lo que se anota después de lo que pasó
    /// en línea (la cuenta del turno, que se pide a la caja principal) lleva los suyos.
    /// </remarks>
    private async Task<T> ConRespaldoAsync<T>(
        Func<Task<T>> enLinea, Func<Contingencia, T>? sinConexion = null, Func<Contingencia, T, Task>? trasEnLinea = null,
        Func<ErrorCaja, ErrorCaja?>? siSePierde = null, bool plazoDeLaVenta = true)
    {
        if (contingencia is null)
            return await enLinea();
        // Al abrir la caja, la caja principal prueba quién es antes de recibir la llave: se espera eso (milisegundos en la red de la
        // tienda, a lo más unos segundos), no la red entera.
        await contingencia.ComprobarAntesDeLlamarAsync();
        if (!contingencia.EnLinea && (sinConexion is not null || !contingencia.SeIntentaSinCopia))
            return Local(contingencia, sinConexion, seMando: false);
        T resultado;
        try
        {
            resultado = await ConPlazosAsync(enLinea, sinConexion is not null && plazoDeLaVenta);
        }
        catch (ErrorCaja ex) when (ex.Codigo == "sin-conexion")
        {
            // Pasó el plazo de algo que el cajero esperaba: la red está lenta (ya esperó; lo siguiente va con la copia). Sin
            // poder conectarse: caída. Lo que no tiene copia y pasó su tope no dice nada de la red: su error, como está.
            if (!ex.PorPlazo)
                contingencia.MarcarSinConexion();
            else if (sinConexion is not null)
                contingencia.MarcarLenta();
            ErrorCaja sinCopia;
            if (sinConexion is null && ex.PorPlazo)
                sinCopia = ex;
            else
            {
                try
                {
                    return Local(contingencia, sinConexion);
                }
                catch (ErrorCaja error)
                {
                    sinCopia = error;
                }
            }
            throw siSePierde?.Invoke(ex) ?? sinCopia;
        }
        if (trasEnLinea is not null)
            await trasEnLinea(contingencia, resultado);
        return resultado;
    }

    /// <summary>
    /// Lo de la caja principal con los plazos de la venta, si <paramref name="conPlazos"/>: solo esto los lleva. Al terminar,
    /// quien llama sigue sin ellos (un método asíncrono no le cambia sus valores de <see cref="AsyncLocal{T}"/>).
    /// </summary>
    private async Task<T> ConPlazosAsync<T>(Func<Task<T>> enLinea, bool conPlazos)
    {
        Transporte.PlazosDeLaVenta = conPlazos ? (PlazoLectura, PlazoEscritura) : null;
        return await enLinea();
    }

    private Task ConRespaldoAsync(Func<Task<object>> enLinea, Action<Contingencia> sinConexion) =>
        ConRespaldoAsync(enLinea, l =>
        {
            sinConexion(l);
            return new object();
        });

    // ── Lo que se señala: el mostrador y las temporadas (entrega 13) ─────────

    /// <summary>
    /// Lo último que contestó la caja principal de los exhibidores y las temporadas (al acomodar en Ctrl+M o al cambiarlos en la
    /// Configuración), con la base y la versión del catálogo de las que salió. Solo manda sobre una copia de su misma base y
    /// mientras la copia no llegue a su versión: así lo que se acaba de acomodar se ve igual al volver a abrir Ctrl+M, sin
    /// esperar a que la copia se ponga al día (un segundo, con el aviso). Al llegar la copia a esa versión, o con una copia de
    /// otra base (la caja principal arrancó otra vez, empezó de cero o se restauró un respaldo: sus versiones quieren decir otra
    /// cosa), se suelta y manda la copia.
    /// </summary>
    private MostradorDto? _mostradorReciente;

    /// <summary>
    /// Los exhibidores y las temporadas para Ctrl+M y el chip de la temporada, sin la red: los de la copia de esta caja (o lo
    /// que acaba de contestar la caja principal, si es de la misma base y más nuevo que ella). Vacío (null) sin una copia que
    /// los traiga (el navegador sin base local, la caja recién instalada, o mientras se lee la copia al abrir): entonces se le
    /// pregunta a la caja principal (<see cref="MostradorAsync"/>), cada vez, para ver lo que se cambió en otra caja.
    /// </summary>
    public IReadOnlyList<ExhibidorDto>? ExhibidoresDeLaCopia()
    {
        // Sin esperar: mientras se lee la copia al abrir, como si no la hubiera.
        var copia = contingencia is { CopiaLeida.IsCompleted: true } ? contingencia.Paquete : null;
        if (copia?.Exhibidores is not { } deLaCopia)
            return null;
        if (_mostradorReciente is { } reciente)
        {
            if (reciente.Base is { } base_ && base_ == copia.Base && reciente.Version > copia.Version)
                return reciente.Exhibidores;
            // La copia ya trae lo que contestó, o es de otra base: manda la copia.
            _mostradorReciente = null;
        }
        return deLaCopia;
    }

    /// <summary>
    /// Los exhibidores y las temporadas como los tiene la caja principal (la Configuración, «Acomodar», la caja sin copia). Sin
    /// conexión, los de la copia de esta caja, si los trae.
    /// </summary>
    public async Task<MostradorDto> MostradorAsync()
    {
        var mostrador = await ConRespaldoAsync(async () => (await _http.GetAsync<MostradorDto>("api/mostrador"))!,
            l => l.Paquete is { Exhibidores: { } exhibidores } copia
                ? new MostradorDto(copia.Version, exhibidores, copia.Base)
                : throw new InvalidOperationException("Esta caja todavía no tiene el mostrador en su copia: se ve cuando vuelva la conexión."));
        return Recordar(mostrador);
    }

    /// <summary>Un exhibidor o una temporada nuevo (Configuración › Mostrador y temporadas).</summary>
    public Task<MostradorDto> NuevoExhibidorAsync(SolicitudNuevoExhibidor solicitud) =>
        CambiarMostradorAsync(() => _http.PostAsync<MostradorDto>("api/mostrador", solicitud));

    /// <summary>Su nombre o, de una temporada, sus fechas.</summary>
    public Task<MostradorDto> CambiarExhibidorAsync(Guid exhibidorId, SolicitudCambiarExhibidor solicitud) =>
        CambiarMostradorAsync(() => _http.PutAsync<MostradorDto>($"api/mostrador/{exhibidorId}", solicitud));

    public Task<MostradorDto> QuitarExhibidorAsync(Guid exhibidorId, SolicitudMostrador solicitud) =>
        CambiarMostradorAsync(() => _http.PostAsync<MostradorDto>($"api/mostrador/{exhibidorId}/quitar", solicitud));

    public Task<MostradorDto> AgregarLugarAsync(Guid exhibidorId, SolicitudLugar solicitud) =>
        CambiarMostradorAsync(() => _http.PostAsync<MostradorDto>($"api/mostrador/{exhibidorId}/lugares", solicitud));

    public Task<MostradorDto> CambiarLugarAsync(Guid lugarId, SolicitudLugar solicitud) =>
        CambiarMostradorAsync(() => _http.PutAsync<MostradorDto>($"api/mostrador/lugares/{lugarId}", solicitud));

    public Task<MostradorDto> MoverLugarAsync(Guid lugarId, SolicitudMoverLugar solicitud) =>
        CambiarMostradorAsync(() => _http.PostAsync<MostradorDto>($"api/mostrador/lugares/{lugarId}/mover", solicitud));

    public Task<MostradorDto> QuitarLugarAsync(Guid lugarId, SolicitudMostrador solicitud) =>
        CambiarMostradorAsync(() => _http.PostAsync<MostradorDto>($"api/mostrador/lugares/{lugarId}/quitar", solicitud));

    /// <summary>Ctrl+M › Acomodar: cómo quedan los productos de uno o más lugares.</summary>
    public Task<MostradorDto> AcomodarAsync(SolicitudAcomodo solicitud) =>
        CambiarMostradorAsync(() => _http.PutAsync<MostradorDto>("api/mostrador/acomodo", solicitud));

    /// <summary>Lo cambia la caja principal, con quien entró: sin conexión no se puede (se dice), y lo que contesta se recuerda.</summary>
    private async Task<MostradorDto> CambiarMostradorAsync(Func<Task<MostradorDto>> cambio) => Recordar(await ConRespaldoAsync(cambio));

    /// <summary>Lo más nuevo que contestó; de otra base, lo último (su versión no se compara con la de antes).</summary>
    private MostradorDto Recordar(MostradorDto mostrador)
    {
        if (_mostradorReciente is not { } antes || antes.Base != mostrador.Base || mostrador.Version >= antes.Version)
            _mostradorReciente = mostrador;
        return mostrador;
    }

    // ── Las fotos de los productos (entrega 13, parte 2) ────────────────────

    private FotosDeLaCaja? _fotosSinCopia;

    /// <summary>
    /// Las fotos de los productos para la pantalla (Ctrl+M, la lista de la búsqueda, el verificador): las de esta caja, de su
    /// base, sin la red. Sin base local (el navegador, en la misma PC de la caja principal), las del servicio.
    /// </summary>
    public FotosDeLaCaja Fotos => contingencia?.Fotos ?? (_fotosSinCopia ??= new FotosDeLaCaja(() => _http.GetAsync<FotosDto>("api/fotos")));

    /// <summary>
    /// La foto de un producto, tal cual (para enseñarla en el celular o en Productos): la de la caja principal; sin conexión, la
    /// que tiene esta caja. Vacía si no tiene.
    /// </summary>
    public Task<byte[]?> FotoAsync(Guid varianteId) =>
        ConRespaldoAsync(() => _http.GetBytesAsync($"api/fotos/{varianteId}", maximo: FotosDeLaCaja.BytesMaximos), l => l.Fotos.Datos(varianteId));

    /// <summary>
    /// Una foto nueva para ese producto, o en lugar de la que tenía: el servicio la revisa, la endereza, la achica y la guarda;
    /// regresa cómo quedó. Con «Productos, precios y etiquetas»; sin conexión no se puede (se dice).
    /// </summary>
    public Task<FotoGuardadaDto> GuardarFotoAsync(Guid varianteId, SolicitudFoto solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<FotoGuardadaDto>($"api/fotos/{varianteId}", solicitud));

    /// <summary>Quita la foto de ese producto: en las cajas vuelven a salir sus iniciales.</summary>
    public Task QuitarFotoAsync(Guid varianteId, SolicitudQuitarFoto solicitud) =>
        ConRespaldoAsync(async () => await _http.PostAsync<object>($"api/fotos/{varianteId}/quitar", solicitud, sinRespuesta: true));

    // ── Empezar y terminar la temporada, las tarjetas de precio y lo más preguntado (entrega 13, parte 3) ──
    // Productos (F3) los pide a la caja principal: sin conexión no se pueden (se dice). La venta solo cuenta lo que se pregunta.

    public Task<TemporadasDto> TemporadasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<TemporadasDto>($"api/temporadas?sucursalId={sucursalId}"))!);

    /// <summary>La que sigue con la lista del año pasado; el costo, el margen y el sugerido, solo si quien entró ve la utilidad.</summary>
    public Task<TemporadaParaEmpezarDto> EmpezarTemporadaAsync(Guid exhibidorId, Guid sucursalId, Guid usuarioId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<TemporadaParaEmpezarDto>(
            $"api/temporadas/{exhibidorId}/empezar?sucursalId={sucursalId}&usuarioId={usuarioId}"))!);

    public Task<PreciosConfirmadosDto> ConfirmarPreciosDeTemporadaAsync(Guid exhibidorId, SolicitudConfirmarPrecios solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<PreciosConfirmadosDto>($"api/temporadas/{exhibidorId}/precios", solicitud));

    public Task<HojaDeTemporadaDto> HojaDeTemporadaAsync(Guid exhibidorId, SolicitudHojaDeTemporada solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<HojaDeTemporadaDto>($"api/temporadas/{exhibidorId}/hoja", solicitud));

    public Task<TemporadaParaTerminarDto> ParaTerminarTemporadaAsync(Guid exhibidorId, Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<TemporadaParaTerminarDto>($"api/temporadas/{exhibidorId}/terminar?sucursalId={sucursalId}"))!);

    /// <summary>Lo contado de un producto al terminar la temporada, al escribirlo: la caja principal lo guarda con lo que dice el sistema.</summary>
    public Task<TemporadaParaTerminarDto> ContarEnTemporadaAsync(Guid exhibidorId, SolicitudContado solicitud) =>
        ConRespaldoAsync(() => _http.PutAsync<TemporadaParaTerminarDto>($"api/temporadas/{exhibidorId}/contado", solicitud));

    public Task<TemporadaParaTerminarDto> TerminarTemporadaAsync(Guid exhibidorId, SolicitudTerminarTemporada solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TemporadaParaTerminarDto>($"api/temporadas/{exhibidorId}/terminar", solicitud));

    /// <summary>Las tarjetas por volver a imprimir y cuántas hay.</summary>
    public Task<TarjetasDto> TarjetasAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => (await _http.GetAsync<TarjetasDto>($"api/tarjetas?sucursalId={sucursalId}"))!);

    public Task<List<ProductoConTarjetaDto>> TarjetasDeExhibidorAsync(Guid exhibidorId, Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoConTarjetaDto>>($"api/tarjetas/exhibidor/{exhibidorId}?sucursalId={sucursalId}") ?? []);

    public Task<List<ProductoConTarjetaDto>> MasPreguntadosAsync(Guid sucursalId) =>
        ConRespaldoAsync(async () => await _http.GetAsync<List<ProductoConTarjetaDto>>($"api/tarjetas/mas-preguntados?sucursalId={sucursalId}") ?? []);

    public Task<TarjetasParaImprimirDto> PrepararTarjetasAsync(SolicitudPrepararTarjetas solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TarjetasParaImprimirDto>("api/tarjetas/preparar", solicitud));

    public Task<TarjetasDto> TarjetasImpresasAsync(SolicitudTarjetasImpresas solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TarjetasDto>("api/tarjetas/impresas", solicitud));

    public Task<TarjetasDto> QuitarTarjetaAsync(Guid varianteId, SolicitudQuitarTarjeta solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TarjetasDto>($"api/tarjetas/{varianteId}/quitar", solicitud));

    /// <summary>
    /// Una consulta del verificador (F9): cuenta para lo más preguntado (Productos › Tarjetas de precio). Como lo que se busca:
    /// sin conexión no se cuenta, y si falla no estorba (el verificador no la espera).
    /// </summary>
    public async Task ContarConsultaAsync(Guid sucursalId, Guid varianteId)
    {
        if (contingencia is { EnLinea: false })
            return;
        try
        {
            await _http.PostAsync<object>("api/consultas", new SolicitudConsulta(sucursalId, varianteId, conexion.CajaId), sinRespuesta: true);
        }
        catch (ErrorCaja)
        {
            // Se pierde esta: la siguiente sí cuenta.
        }
    }

    /// <param name="seMando">Ya se intentó con la caja principal; si no, lo que no tiene copia ni salió de esta caja (NoSeMando).</param>
    private static T Local<T>(Contingencia contingencia, Func<Contingencia, T>? sinConexion, bool seMando = true)
    {
        if (sinConexion is null)
            throw new ErrorCaja(contingencia.Pendientes > 0 || contingencia.EstadoDeLaRed == EstadoRed.Caida
                ? "No hay conexión con la caja principal: esto espera a que vuelva."
                : "La red con la caja principal está lenta: esto espera a que mejore.", "sin-conexion") { NoSeMando = !seMando };
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

    /// <summary>Una diferencia como en el prototipo: «−$50.00», «$12.00» (con el signo menos, no un guion).</summary>
    public static string ConSigno(long centavos) => (centavos < 0 ? "−" : "") + Dinero(Math.Abs(centavos));

    /// <summary>En pesos cerrados, para las cifras grandes del corte del día: «$12,345».</summary>
    public static string Pesos(long centavos) =>
        (centavos < 0 ? "−" : "") + "$" + Math.Round(Math.Abs(centavos) / 100m, MidpointRounding.AwayFromZero).ToString("#,##0", Mexico);

    /// <summary>
    /// Un número escrito en la caja, con la regla de los números del POS: la de los productos en Excel
    /// (<see cref="LecturaDeCeldas.NumeroDeTexto"/>), la misma para el dinero de todas las pantallas y para «Agregar inventario»
    /// (revisión del PR #138). El punto es el decimal y la coma solo separa los miles, de tres en tres («1,250.50»); una coma que
    /// podría ser de decimales («12,50», «1,5») no se adivina: vacío, con <paramref name="comaDecimal"/>. A diferencia del Excel,
    /// sin espacios por dentro («12 50» tampoco se adivina); el «$» puede llevar uno después («$ 12.50»).
    /// </summary>
    public static decimal? LeerNumero(string? texto, out bool comaDecimal)
    {
        comaDecimal = false;
        var escrito = (texto ?? "").Trim();
        if (escrito.StartsWith('$'))
            escrito = "$" + escrito[1..].TrimStart();
        return escrito.Length == 0 || escrito.Any(char.IsWhiteSpace) ? null : LecturaDeCeldas.NumeroDeTexto(escrito, out comaDecimal);
    }

    /// <summary>
    /// Dinero escrito en la caja, en centavos y redondeado al centavo, con la regla de los números del POS (<see cref="LeerNumero"/>):
    /// «$1,234.50», «1,250» ($1,250.00), «12.5» ($12.50), «1.250» ($1.25). Vacío si no es dinero: una coma que podría ser de
    /// decimales («12,50»: antes se leía $1,250.00 sin avisar; <see cref="ComaDeCentavos"/> dice cómo escribirlo), lo negativo, o
    /// lo que no cabe en centavos (un código largo del lector en un campo de dinero: antes tumbaba la pantalla al dibujarla).
    /// </summary>
    public static long? LeerPesos(string? texto) =>
        LeerNumero(texto, out _) is { } pesos && pesos >= 0 && pesos <= MaximoPesos ? (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// Si lo escrito no es dinero por una coma que podría ser de decimales: «Los centavos van con punto: 12.50 (la coma solo separa
    /// los miles: 1,250.00).», con lo escrito ya corregido si se puede. Vacío si no es eso: cada pantalla dice lo suyo.
    /// </summary>
    public static string? ComaDeCentavos(string? texto) =>
        LeerNumero(texto, out var coma) is null && coma
            ? $"Los centavos van con punto: {ConPunto(texto!, "12.50")} (la coma solo separa los miles: 1,250.00)."
            : null;

    /// <summary>
    /// Lo mismo para lo que no es dinero (una cantidad, un porcentaje): «Los decimales van con punto: 2.5 (la coma solo separa los
    /// miles: 1,000).». Vacío si no es eso.
    /// </summary>
    public static string? ComaDeDecimales(string? texto) =>
        LeerNumero(texto, out var coma) is null && coma
            ? $"Los decimales van con punto: {ConPunto(texto!, "2.5")} (la coma solo separa los miles: 1,000)."
            : null;

    /// <summary>
    /// Lo escrito con la coma de decimales, como va: «12,50» → «12.50». Solo si trae una sola coma con una a tres cifras después,
    /// algo antes y ningún punto; si no, <paramref name="ejemplo"/>.
    /// </summary>
    public static string ConPunto(string texto, string ejemplo)
    {
        texto = texto.Trim();
        var coma = texto.IndexOf(',');
        var despues = texto.Length - coma - 1;
        return coma > 0 && texto.Count(c => c == ',') == 1 && !texto.Contains('.') && despues is >= 1 and <= 3 ? texto.Replace(',', '.') : ejemplo;
    }

    /// <summary>Lo más que cabe en centavos (<see cref="long"/>).</summary>
    private const decimal MaximoPesos = long.MaxValue / 100m - 1;
}
