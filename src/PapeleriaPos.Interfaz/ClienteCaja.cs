using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
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
public sealed class ClienteCaja(HttpClient http, ConexionCaja conexion, Contingencia? contingencia = null)
{
    public const string EncabezadoLlave = "X-Caja-Llave";

    private readonly Transporte _http = new(http, conexion);

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
        ConRespaldoAsync(() => _http.GetAsync<TurnoDto>($"api/cajas/{cajaId}/turno", nadaEsNulo: true), l => l.Turno(cajaId),
            (l, turno) => l.TurnoEnLineaAsync(turno));

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

    public Task<TicketDto> ReimprimirAsync(Guid ventaId, SolicitudReimpresion solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<TicketDto>($"api/ventas/{ventaId}/reimpresion", solicitud));

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

    /// <summary>El corte del turno con el total que se contó, a nombre de quien entró. Sin conexión, lo hace esta caja y se cuadra al subir.</summary>
    public Task<CorteDto> CorteZAsync(Guid turnoId, SolicitudCorteZ solicitud) =>
        ConRespaldoAsync(() => _http.PostAsync<CorteDto>($"api/turnos/{turnoId}/corte-z", solicitud), l => l.CorteZ(turnoId, solicitud),
            (l, _) => l.TurnoCerradoEnLineaAsync());

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
    /// Cuánto se espera a la caja principal por un código que no está en la copia (docs/propuesta-interfaz.md,
    /// «Rendimiento»: medio segundo). Pasado eso, la línea sale y se le dice al cajero que lo escanee otra vez.
    /// </summary>
    public static readonly TimeSpan PlazoPorOmision = TimeSpan.FromMilliseconds(500);

    /// <summary>El de <see cref="PlazoPorOmision"/>; las pruebas lo cambian para no depender de lo que tarda la PC.</summary>
    public TimeSpan PlazoDeLaPrincipal { get; init; } = PlazoPorOmision;

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
    /// guarda, y si falla no estorba la venta. Lo elegido sale primero la vez siguiente también en la copia de esta caja.
    /// </summary>
    public async Task RegistrarBusquedaAsync(SolicitudBusqueda busqueda)
    {
        if (busqueda.VarianteId is { } elegido)
            contingencia?.Aprender(busqueda.Texto, elegido);
        if (contingencia is { EnLinea: false })
            return;
        try
        {
            await _http.PostAsync<object>("api/busquedas", busqueda, sinRespuesta: true);
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
    /// fiado se anota en la cuenta propia antes de mandarlo (si la respuesta se pierde, ya cuenta). Si se pierde y sin
    /// conexión no se puede fiar (no conoce los saldos, o pasa el límite sin autorización en la copia), puede que ya esté
    /// en la cuenta del cliente: cobrarlo de otra forma lo dejaría fiado y pagado. Entonces el ticket espera la conexión
    /// (<paramref name="soloEnLinea"/>) y se manda otra vez con el mismo ID: si ya se guardó, regresa el mismo ticket.
    /// </summary>
    public Task<TicketDto> CobrarAsync(SolicitudCobro solicitud, bool soloEnLinea = false)
    {
        solicitud = solicitud with { VentaId = solicitud.VentaId ?? Ids.Nuevo() };
        if (soloEnLinea && contingencia is { EnLinea: false })
            return Task.FromException<TicketDto>(FiadoSinConfirmar());
        var fiado = solicitud.Cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos);
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
            }, l => l.Cobrar(solicitud), (l, ticket) => l.CobroEnLineaAsync(solicitud.TurnoId, ticket), siSePierde: fiado > 0 ? FiadoSinConfirmar : null);
    }

    private static ErrorCaja FiadoSinConfirmar() => new(
        "Se perdió la conexión al fiar y no se sabe si quedó en su cuenta. Cuando vuelva la conexión, cobra otra vez este ticket "
        + "(si ya quedó, sale el mismo); mientras, no lo cobres de otra forma.", "fiado-sin-confirmar");

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
            }, l => l.Abonar(s), (l, c) => l.AbonoEnLineaAsync(s.TurnoId, c), siSePierde: AbonoSinConfirmar);

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
            }, null, (l, c) => l.SaldoDevueltoEnLineaAsync(s.TurnoId, c), siSePierde: () =>
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
    /// hacer: el error que lo dice, en lugar del de sin conexión.
    /// </param>
    private async Task<T> ConRespaldoAsync<T>(
        Func<Task<T>> enLinea, Func<Contingencia, T>? sinConexion = null, Func<Contingencia, T, Task>? trasEnLinea = null,
        Func<ErrorCaja>? siSePierde = null)
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
            if (siSePierde is null)
                return Local(contingencia, sinConexion);
            try
            {
                return Local(contingencia, sinConexion);
            }
            catch (ErrorCaja)
            {
                throw siSePierde();
            }
        }
        if (trasEnLinea is not null)
            await trasEnLinea(contingencia, resultado);
        return resultado;
    }

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

    /// <summary>Una diferencia como en el prototipo: «−$50.00», «$12.00» (con el signo menos, no un guion).</summary>
    public static string ConSigno(long centavos) => (centavos < 0 ? "−" : "") + Dinero(Math.Abs(centavos));

    /// <summary>En pesos cerrados, para las cifras grandes del corte del día: «$12,345».</summary>
    public static string Pesos(long centavos) =>
        (centavos < 0 ? "−" : "") + "$" + Math.Round(Math.Abs(centavos) / 100m, MidpointRounding.AwayFromZero).ToString("#,##0", Mexico);

    /// <summary>
    /// "$1,234.50", "1234.5" o "1234" a centavos. Vacío o inválido: null. También lo que no cabe en centavos (un código
    /// largo del lector en un campo de dinero): antes tumbaba la pantalla al dibujarla.
    /// </summary>
    public static long? LeerPesos(string? texto)
    {
        var limpio = (texto ?? "").Replace("$", "").Replace(",", "").Trim();
        return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var pesos) && pesos >= 0 && pesos <= MaximoPesos
            ? (long)Math.Round(pesos * 100, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>Lo más que cabe en centavos (<see cref="long"/>).</summary>
    private const decimal MaximoPesos = long.MaxValue / 100m - 1;
}
