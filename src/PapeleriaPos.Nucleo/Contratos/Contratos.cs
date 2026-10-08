using System.Text.Json.Serialization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

// Lo que viaja entre la caja y el servicio de la sucursal. Montos en centavos y cantidades en milésimas, como en el
// modelo; las fechas que se muestran ya van en hora de México.

public sealed record PresentacionDto(Guid Id, string Nombre, long FactorMilesimas, bool EsBase);

public sealed record PrecioDto(Guid Id, Guid? PresentacionId, Guid? SucursalId, long CantidadMinimaMilesimas, long PrecioCentavos);

/// <summary>Un producto listo para agregarse a la venta: con sus presentaciones y precios.</summary>
/// <param name="Presentacion">La que corresponde al código escaneado (la base si se buscó por nombre).</param>
public sealed record ProductoVentaDto(
    Guid VarianteId, string Descripcion, string? Codigo, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
    decimal? Existencia, PresentacionDto Presentacion, IReadOnlyList<PresentacionDto> Presentaciones, IReadOnlyList<PrecioDto> Precios)
{
    public ProductoEnVenta ParaVenta() => new(VarianteId, Descripcion, Impuesto, Unidad, LlevaInventario);

    public Presentacion PresentacionModelo(Guid? id = null)
    {
        var p = id is { } i ? Presentaciones.Single(x => x.Id == i) : Presentacion;
        return new Presentacion { Id = p.Id, VarianteId = VarianteId, Nombre = p.Nombre, Factor = new Cantidad(p.FactorMilesimas), EsBase = p.EsBase };
    }

    public List<Precio> PreciosModelo() => Precios.Select(p => new Precio
    {
        Id = p.Id,
        VarianteId = VarianteId,
        PresentacionId = p.PresentacionId,
        SucursalId = p.SucursalId,
        CantidadMinima = new Cantidad(p.CantidadMinimaMilesimas),
        PrecioCentavos = p.PrecioCentavos,
    }).ToList();
}

public sealed record ResultadoBusquedaDto(Guid VarianteId, string Descripcion, string? Codigo, long PrecioCentavos, decimal? Existencia);

public sealed record SolicitudEntrada(string Usuario, string Pin);

public sealed record SesionDto(Guid UsuarioId, string Nombre, Rol Rol);

/// <param name="Equipo">La PC dada de alta como esta caja; vacío si ninguna.</param>
public sealed record CajaDto(Guid Id, string Nombre, string Serie, Guid SucursalId, string Sucursal, string? Equipo = null);

public sealed record TurnoDto(Guid Id, Guid CajaId, Guid CajeroId, string Cajero, DateTime AbiertoLocal, long FondoCentavos);

public sealed record SolicitudTurno(Guid CajaId, Guid CajeroId, long FondoCentavos, Guid? Id = null);

public sealed record SolicitudMovimiento(TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, Guid UsuarioId, Guid? Id = null);

/// <summary>Una línea tal como la manda la caja. Sin variante es artículo común, con descripción y precio a mano.</summary>
/// <param name="PrecioCentavos">En un producto del catálogo, el precio cambiado en la caja (con <paramref name="AutorizacionId"/>).</param>
/// <param name="DescuentoCentesimas">Descuento en porcentaje (centésimas: 1000 = 10 %); o <paramref name="DescuentoCentavos"/> en monto. Con motivo.</param>
public sealed record LineaSolicitada(
    Guid? VarianteId, Guid? PresentacionId, long CantidadMilesimas, string? Descripcion = null, long? PrecioCentavos = null, Guid? AutorizacionId = null,
    int? DescuentoCentesimas = null, long? DescuentoCentavos = null, string? MotivoDescuento = null);

/// <summary>El servicio vuelve a armar la venta con los precios del catálogo: nunca confía en los precios de la caja.</summary>
/// <param name="VentaId">Lo pone la caja: si la respuesta no llegó y se manda otra vez (o sube con la bandeja), no se cobra dos veces.</param>
/// <param name="Impresiones">Los trabajos de impresión que se cobran: el servicio los junta en la línea "Impresiones".</param>
/// <param name="AutorizacionDescuentoId">La del descuento más alto, si pasó de lo que el cajero da solo.</param>
/// <param name="AutorizacionBajoCostoId">Si un descuento o un precio cambiado deja líneas debajo del costo.</param>
/// <param name="ClienteId">El cliente registrado del ticket. En el JSON conserva el nombre de cuando eran «clientes especiales».</param>
/// <param name="MayoreoDeCliente">
/// La caja calculó el ticket con el mayoreo siempre del cliente. Vacío en las cajas de antes, que no lo conocen: el servicio
/// calcula igual que ellas.
/// </param>
public sealed record SolicitudCobro(
    Guid CajaId, Guid TurnoId, Guid CajeroId, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<CobroSolicitado> Cobros, Guid? VentaId = null,
    IReadOnlyList<Guid>? Impresiones = null, [property: JsonPropertyName("clienteEspecialId")] Guid? ClienteId = null,
    Guid? AutorizacionDescuentoId = null, Guid? AutorizacionBajoCostoId = null, bool? MayoreoDeCliente = null);

/// <param name="ImporteCentavos">Ya con el descuento.</param>
/// <param name="Descuento">Cómo se lee el descuento ("10 %" o vacío si fue un monto).</param>
/// <param name="AhorroMayoreoCentavos">Lo que se ahorró por el mayoreo contra el menudeo (fase 3).</param>
/// <param name="Mayoreo">Cómo se lee: "Mayoreo desde 12".</param>
public sealed record LineaTicketDto(
    string Cantidad, string Descripcion, long PrecioCentavos, long ImporteCentavos, long DescuentoCentavos = 0, string? Descuento = null,
    long AhorroMayoreoCentavos = 0, string? Mayoreo = null);

public sealed record CobroTicketDto(FormaPago Forma, long MontoCentavos, long RecibidoCentavos, long CambioCentavos, string? Referencia);

/// <summary>Logo en blanco y negro listo para la impresora: un bit por punto, renglones de (Ancho + 7) / 8 bytes.</summary>
public sealed record LogoTicketDto(int Ancho, int Alto, string BitsBase64);

/// <param name="TotalCentavos">Exacto: la suma de las líneas, con el IVA incluido.</param>
/// <param name="RedondeoCentavos">El del efectivo al peso (fase 3): lo que pagó el cliente es el total más esto.</param>
/// <param name="Cliente">El cliente registrado con sus privilegios: "Escuela Benito Juárez (descuento 10 %)".</param>
public sealed record TicketDto(
    string Folio, DateTime FechaLocal, string Caja, string Cajero, IReadOnlyList<LineaTicketDto> Lineas, decimal NumeroDeArticulos,
    long TotalCentavos, long IvaCentavos, IReadOnlyList<CobroTicketDto> Cobros, IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie,
    LogoTicketDto? Logo = null, Guid? VentaId = null, long RedondeoCentavos = 0, string? Cliente = null)
{
    public long TotalCobradoCentavos => TotalCentavos + RedondeoCentavos;

    public long CambioCentavos => Cobros.Sum(c => c.CambioCentavos);
    /// <summary>Lo que dio el cliente: el efectivo recibido, la tarjeta y la transferencia (lo fiado no es pago).</summary>
    public long PagoConCentavos => Cobros.Where(c => c.Forma != FormaPago.Credito)
        .Sum(c => c.Forma == FormaPago.Efectivo ? c.RecibidoCentavos : c.MontoCentavos);

    /// <summary>Lo que quedó a deber en este ticket.</summary>
    public long CreditoCentavos => Cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos);
    public bool AbreCajon => Cobros.Any(c => c.Forma == FormaPago.Efectivo);
    public long DescuentoCentavos => Lineas.Sum(l => l.DescuentoCentavos);

    /// <summary>"Usted ahorró": los descuentos y el mayoreo.</summary>
    public long AhorroCentavos => Lineas.Sum(l => l.DescuentoCentavos + l.AhorroMayoreoCentavos);
}

public sealed record VentaDelDiaDto(Guid Id, string Folio, DateTime FechaLocal, long TotalCentavos, int Articulos, string Cajero, bool Cancelada = false);

/// <summary>El encargado se identifica con su usuario y PIN en la misma caja.</summary>
public sealed record SolicitudCorteZ(string EncargadoUsuario, string EncargadoPin, IReadOnlyDictionary<long, int> Conteo);

public sealed record CorteDto(
    TipoCorte Tipo, DateTime FechaLocal, string Cajero, string RealizadoPor, int Ventas, long FondoCentavos, long EfectivoVentasCentavos,
    long EntradasCentavos, long SalidasCentavos, long TarjetaCentavos, long TransferenciaCentavos, long EsperadoCentavos,
    long? ContadoCentavos, long? DiferenciaCentavos, long DevolucionesCentavos = 0, int Cancelaciones = 0, bool EnContingencia = false,
    long RedondeoCentavos = 0);

/// <param name="Codigo">"requiere-autorizacion" o "no-permitido" cuando lo detiene una regla.</param>
public sealed record ErrorDto(string Mensaje, string? Codigo = null);

/// <param name="LogoPngBase64">Cómo se ve el logo convertido, para la pantalla.</param>
public sealed record ConfiguracionTicketDto(IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo, string? LogoPngBase64);

/// <summary>Cambiar el ticket lo autoriza el encargado o el dueño con su usuario y PIN.</summary>
public sealed record SolicitudTextosTicket(string Usuario, string Pin, IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie);

/// <param name="ImagenBase64">El archivo tal cual (PNG, JPG o BMP); el servicio lo pasa a blanco y negro.</param>
public sealed record SolicitudLogo(string Usuario, string Pin, string? ImagenBase64);

// ── Control (entrega 2) ─────────────────────────────────────────────────────

public sealed record SolicitudPermiso(Guid UsuarioId, Guid SucursalId, AccionControlada Accion, long? MontoCentavos = null);

public sealed record DecisionDto(NivelPermiso Nivel, bool PideMotivo);

/// <summary>El encargado (o el dueño) autoriza en la misma caja con su usuario y PIN.</summary>
public sealed record SolicitudAutorizacion(
    AccionControlada Accion, Guid SucursalId, Guid? CajaId, Guid SolicitanteId, string Usuario, string Pin, long? MontoCentavos, string Detalle);

public sealed record AutorizacionDto(Guid Id, DateTime VenceUtc, string Autorizo);

/// <summary>Lo que pasa en la caja antes de cobrar y el servicio no ve (quitar un producto ya escaneado).</summary>
public sealed record SolicitudBitacora(Guid UsuarioId, Guid SucursalId, Guid? CajaId, AccionControlada Accion, string Detalle, long? MontoCentavos = null);

public sealed record EfectivoCajaDto(long EsperadoCentavos, long? LimiteCentavos)
{
    public bool PideRetiro => LimiteCentavos is { } limite && EsperadoCentavos >= limite;
}

public sealed record ConfiguracionSucursalDto(long? LimiteEfectivoCentavos);

public sealed record SolicitudConfiguracionSucursal(string Usuario, string Pin, long? LimiteEfectivoCentavos);

/// <summary>El comprobante que se imprime al sacar efectivo de la caja.</summary>
public sealed record ComprobanteMovimientoDto(
    string Folio, TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, DateTime FechaLocal, string Caja, string Usuario, IReadOnlyList<string> Encabezado);

public sealed record SolicitudReimpresion(Guid UsuarioId, Guid CajaId, string? Motivo);

public sealed record ReglaDto(AccionControlada Accion, Rol Rol, NivelPermiso Nivel, long? UmbralCentavos, bool PideMotivo);

/// <summary>Solo el dueño cambia las reglas.</summary>
public sealed record SolicitudReglas(string Usuario, string Pin, IReadOnlyList<ReglaDto> Reglas);

// ── Cancelar, devolver y verificar (entrega 2, parte 2) ─────────────────────

public sealed record LineaDevolucionDto(Guid LineaId, string Descripcion, long CantidadMilesimas, long CantidadDevueltaMilesimas, long ImporteCentavos, bool LlevaInventario);

/// <param name="PuedeCancelar">Si su turno sigue abierto (después, solo devolución).</param>
public sealed record VentaParaDevolverDto(
    Guid Id, string Folio, DateTime FechaLocal, EstadoVenta Estado, long TotalCentavos, IReadOnlyList<LineaDevolucionDto> Lineas,
    bool PuedeCancelar, int DiasParaDevolver);

public sealed record SolicitudCancelacion(Guid UsuarioId, Guid CajaId, string Motivo, Guid? AutorizacionId = null);

public sealed record LineaDevolucionSolicitada(Guid LineaId, long CantidadMilesimas, bool RegresaAlInventario = true);

public sealed record SolicitudDevolucion(Guid UsuarioId, Guid CajaId, string Motivo, IReadOnlyList<LineaDevolucionSolicitada> Lineas, Guid? AutorizacionId = null);

/// <summary>Comprobante que se imprime al reembolsar.</summary>
/// <param name="RedondeoCentavos">El del efectivo al peso: lo que sale en efectivo de lo devuelto (o, al cancelar, el de la venta).</param>
/// <param name="CobradoSinEfectivoCentavos">En una cancelación, lo que se pagó con tarjeta o transferencia: se regresa por ese medio.</param>
public sealed record ComprobanteDevolucionDto(
    string Folio, bool EsCancelacion, long TotalCentavos, long ReembolsoEfectivoCentavos, string Motivo, DateTime FechaLocal, string Caja,
    string Usuario, string? Autorizo, IReadOnlyList<LineaTicketDto> Lineas, IReadOnlyList<string> Encabezado, long RedondeoCentavos = 0,
    long CobradoSinEfectivoCentavos = 0);

public sealed record TransferenciaDto(Guid CobroId, string Folio, DateTime FechaLocal, long MontoCentavos, string? Referencia, string Cajero, Verificacion Verificacion);

public sealed record SolicitudVerificacion(string Usuario, string Pin, bool Llego);

public sealed record SolicitudConsultaBitacora(string Usuario, string Pin, DateOnly Desde, DateOnly Hasta);

public sealed record BitacoraDto(DateTime FechaLocal, string? Usuario, AccionControlada Accion, string Detalle, long? MontoCentavos, string? Autorizo, ResultadoBitacora Resultado);

// ── Varias cajas (entrega 3) ────────────────────────────────────────────────

/// <summary>Lo que contesta el servicio sin llave: para encontrarlo en la red y saber que está vivo.</summary>
/// <param name="VersionDatos">Cambia con el catálogo, los usuarios o las reglas: la caja vuelve a bajar su copia.</param>
/// <param name="Prueba">El servicio es el de la prueba con una persona real: datos ficticios, guía y avisos simulados.</param>
public sealed record EstadoServicioDto(string Servicio, string Version, IReadOnlyList<string> Sucursales, long VersionDatos = 0, bool Prueba = false);

/// <summary>Usuario y PIN de quien autoriza (el dueño para dar de alta o quitar cajas).</summary>
public sealed record SolicitudCredenciales(string Usuario, string Pin);

public sealed record CajaAltaDto(Guid Id, string Nombre, string Serie, string? Equipo);

public sealed record SucursalAltaDto(Guid Id, string Nombre, IReadOnlyList<CajaAltaDto> Cajas);

/// <summary>Sin <paramref name="CajaId"/>, se crea una caja nueva con <paramref name="Nombre"/> y <paramref name="Serie"/>.</summary>
public sealed record SolicitudAltaCaja(string Usuario, string Pin, Guid SucursalId, Guid? CajaId, string? Nombre, string? Serie, string Equipo);

/// <param name="Llave">Solo la ve el equipo: la guarda y la manda en cada llamada (encabezado X-Caja-Llave).</param>
public sealed record AltaCajaDto(CajaDto Caja, string Llave);

// ── Contingencia (entrega 3, parte 2) ───────────────────────────────────────

/// <summary>Un usuario en la copia local de la caja: el PIN va como su hash (PBKDF2), nunca en claro.</summary>
public sealed record UsuarioLocalDto(Guid Id, string Usuario, string Nombre, Rol Rol, string PinHash, bool Activo);

/// <summary>Cada código de barras y la presentación que vende (la caja de 12, la pieza…).</summary>
public sealed record CodigoLocalDto(string Codigo, Guid VarianteId, Guid PresentacionId);

/// <summary>Lo que la caja guarda para vender sin la caja principal.</summary>
/// <param name="Version">La <see cref="EstadoServicioDto.VersionDatos"/> con que se armó.</param>
/// <param name="UltimoFolio">El último folio de la serie de la caja que conoce el servicio.</param>
/// <param name="Clientes">Para las cajas de antes (CTRL+D): solo los activos con descuento, que es lo único que saben usar.</param>
/// <param name="Registrados">Todos los clientes registrados activos, con sus privilegios.</param>
public sealed record PaqueteContingenciaDto(
    long Version, CajaDto Caja, IReadOnlyList<ProductoVentaDto> Productos, IReadOnlyList<CodigoLocalDto> Codigos, IReadOnlyList<UsuarioLocalDto> Usuarios,
    IReadOnlyList<ReglaDto> Reglas,
    IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo, int UltimoFolio, long? LimiteEfectivoCentavos,
    IReadOnlyList<ClienteRegistradoDto>? Clientes = null, IReadOnlyList<ClienteRegistradoDto>? Registrados = null);

// Lo que sube con la bandeja. Cada cosa lleva el ID que le dio la caja: si llega dos veces, se guarda una.

public sealed record TurnoContingenciaDto(Guid Id, Guid CajaId, Guid CajeroId, long FondoCentavos, DateTime AbiertoUtc);

/// <param name="PrecioCentavos">El que se cobró (por presentación, con IVA): manda sobre el del catálogo.</param>
public sealed record LineaContingenciaDto(
    Guid? VarianteId, Guid? PresentacionId, string Descripcion, long CantidadMilesimas, long PrecioCentavos, int? DescuentoCentesimas = null,
    long? DescuentoCentavos = null, string? MotivoDescuento = null);

/// <param name="RedondeoCentavos">El que cobró la caja. Vacío en lo que se cobró antes de que existiera el redondeo: eso se vuelve a cobrar exacto.</param>
public sealed record VentaContingenciaDto(
    Guid Id, Guid CajaId, Guid TurnoId, Guid CajeroId, int Folio, DateTime PagadaUtc, IReadOnlyList<LineaContingenciaDto> Lineas,
    IReadOnlyList<CobroSolicitado> Cobros, [property: JsonPropertyName("clienteEspecialId")] Guid? ClienteId = null, long? RedondeoCentavos = null,
    bool? MayoreoDeCliente = null);

public sealed record MovimientoContingenciaDto(Guid Id, TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, Guid UsuarioId, DateTime FechaUtc);

public sealed record BitacoraContingenciaDto(
    Guid Id, Guid SucursalId, Guid CajaId, Guid? UsuarioId, AccionControlada Accion, string Detalle, long? MontoCentavos, Guid? AutorizoId,
    ResultadoBitacora Resultado, DateTime FechaUtc, Guid? VentaId = null);

public sealed record CorteContingenciaDto(Guid Id, Guid EncargadoId, IReadOnlyDictionary<long, int> Conteo, DateTime FechaUtc);

/// <summary>El último folio que usó la caja (aunque alguna venta no haya podido subir): la serie sigue después de él.</summary>
public sealed record FolioUsadoDto(int Folio);

// ── Telegram (entrega 4) ────────────────────────────────────────────────────

public sealed record AjustesAlertasDto(
    IReadOnlyList<TipoAlerta> Apagadas, long UmbralCorteCentavos, long UmbralDevolucionCentavos, long UmbralSalidaCentavos, int MinutosSinConexion,
    TimeOnly HoraApertura, TimeOnly HoraCierre, long UmbralAjusteCentavos = 20000);

public sealed record ChatTelegramDto(Guid Id, string Usuario, string? Nombre, DateTime VinculadoLocal);

/// <param name="Configurado">Si la caja principal tiene el token del bot.</param>
/// <param name="Bot">El nombre del bot (@...), para decir a quién escribirle.</param>
public sealed record EstadoTelegramDto(bool Configurado, string? Bot, IReadOnlyList<ChatTelegramDto> Chats, AjustesAlertasDto Alertas);

public sealed record CodigoVinculacionDto(string Codigo, DateTime VenceLocal, string? Bot);

public sealed record SolicitudAlertas(string Usuario, string Pin, AjustesAlertasDto Alertas);

public sealed record SolicitudAutorizacionRemota(AccionControlada Accion, Guid SucursalId, Guid? CajaId, Guid SolicitanteId, long? MontoCentavos, string Detalle);

public sealed record SolicitudRemotaDto(Guid Id, DateTime VenceUtc);

/// <param name="Autorizacion">Cuando la aprueban: la caja sigue con ella como si fuera la del PIN.</param>
public sealed record EstadoSolicitudRemotaDto(EstadoSolicitudRemota Estado, AutorizacionDto? Autorizacion, string? Respondio);

// ── Impresiones (entrega 5) ─────────────────────────────────────────────────

/// <summary>Los ajustes con que salió un trabajo (del PrintTicket de Windows).</summary>
public sealed record AjustesTrabajoDto(
    string Calidad, string TipoPapel, string Tamano, double AnchoMm, double AltoMm, bool Color, bool Duplex, int Copias, int PaginasPorHoja,
    bool DelPrintTicket);

/// <summary>Cobertura de tinta de una página (fracciones 0..1) y su área en pulgadas cuadradas.</summary>
public sealed record CoberturaPaginaDto(double C, double M, double Y, double K, double Gris, double AreaPulg2, bool ConColor);

/// <summary>Lo que manda el agente de una caja por cada trabajo impreso; el servicio le pone costo y precio.</summary>
/// <param name="Id">Fijo para ese trabajo (PC, cola, número y hora): si se manda dos veces, se guarda una.</param>
/// <param name="EnviadoUtc">Cuando entró a la cola de Windows: desde ahí se cuentan sus hojas en la impresora.</param>
public sealed record TrabajoMedidoDto(
    Guid Id, Guid CajaId, Guid? AtendioId, string Pc, string Cola, long SpoolId, string Documento, string? UsuarioWindows, DateTime ImpresoUtc,
    EstadoTrabajo Estado, AjustesTrabajoDto Ajustes, IReadOnlyList<CoberturaPaginaDto> Paginas, bool Medido, string? Nota, string? MiniaturaBase64,
    DateTime? EnviadoUtc = null);

/// <summary>Solo el dueño lo recibe.</summary>
public sealed record CostoImpresionDto(long TintaCentavos, long PapelCentavos, long OtrosCentavos, long TotalCentavos, decimal MargenPorcentaje);

/// <param name="CarasContadas">Las que contó la impresora (vacío mientras no se verifica).</param>
/// <param name="PrecioSoloLoQueSalioCentavos">Con un atasco o una cancelación: lo que costaría cobrar solo lo que salió.</param>
/// <param name="PrecioCompletoCentavos">Si ya se cobra solo lo que salió, el precio del trabajo completo.</param>
public sealed record ImpresionPorCobrarDto(
    Guid Id, string Documento, string Pc, DateTime FechaLocal, int Paginas, int Caras, bool Color, string Tamano, long PrecioCentavos,
    string? Atendio, string? MiniaturaBase64, string? Nota, CostoImpresionDto? Costo = null,
    TipoTrabajoImpresion Tipo = TipoTrabajoImpresion.Impresion, int? CarasContadas = null, long? PrecioSoloLoQueSalioCentavos = null,
    long? PrecioCompletoCentavos = null);

public sealed record SolicitudNoVendida(Guid UsuarioId, MotivoNoVendida Motivo, string? Nota);

public sealed record SolicitudSoloLoQueSalio(Guid UsuarioId);

/// <summary>Una opción para elegir en una lista: la clave que entiende el cálculo y el nombre que se muestra.</summary>
public sealed record OpcionDto(string Clave, string Nombre);

/// <summary>Con qué se puede cotizar o registrar copias en la sucursal (los tipos de papel salen de su configuración).</summary>
public sealed record OpcionesImpresionDto(IReadOnlyList<OpcionDto> TiposPapel, IReadOnlyList<string> Tamanos, IReadOnlyList<OpcionDto> Calidades);

/// <param name="Tamano">Carta, Oficio o A4.</param>
/// <param name="Rango">Páginas a imprimir ("1-3, 5"); vacío, todas.</param>
public sealed record OpcionesCotizacionDto(
    string TipoPapel = "Plain", string Tamano = "Carta", bool Color = true, string Calidad = "Standard", bool Duplex = false, int Copias = 1,
    int PaginasPorHoja = 1, string? Rango = null);

/// <summary>Cotizar un PDF: la caja lo midió y la caja principal le pone precio, igual que a una impresión.</summary>
/// <param name="UsuarioId">Quien cotiza: si es el dueño, la respuesta trae el costo.</param>
public sealed record SolicitudCotizacion(Guid SucursalId, Guid? UsuarioId, IReadOnlyList<CoberturaPaginaDto> Paginas, OpcionesCotizacionDto Opciones);

/// <param name="Pagina">El número de página del documento.</param>
/// <param name="Tipo">Color, B/N o En blanco.</param>
/// <param name="PrecioCentavos">Lo que le toca a esa página por copia, sin redondear el trabajo.</param>
public sealed record PaginaCotizadaDto(int Pagina, string Tipo, decimal PrecioCentavos, decimal? CostoCentavos);

public sealed record CotizacionDto(
    long PrecioCentavos, bool CobroMinimo, int Paginas, int Copias, int Caras, int Hojas, int CarasColor, string Tamano, string TipoPapel,
    IReadOnlyList<PaginaCotizadaDto> PorPagina, IReadOnlyList<string> Avisos, CostoImpresionDto? Costo);

/// <summary>Copias hechas en el panel de la impresora: el precio sugerido con la tinta típica y el papel.</summary>
public sealed record SolicitudPrecioCopias(Guid SucursalId, int Cantidad, bool Color, string Tamano, bool Duplex);

public sealed record PrecioCopiasDto(long PrecioCentavos, string Descripcion);

/// <summary>Se registran al cobrarlas: entran a la línea "Impresiones" y cuentan contra el contador de la impresora.</summary>
/// <param name="CobradoCentavos">Lo que se cobra; vacío, el precio sugerido.</param>
public sealed record SolicitudCopias(
    Guid Id, Guid CajaId, Guid? AtendioId, int Cantidad, bool Color, string Tamano, bool Duplex, long? CobradoCentavos, string? Nota);

/// <summary>Lo que hizo cada impresión de un día, con su cobro: el desglose de la línea "Impresiones" (dueño y encargado).</summary>
public sealed record ImpresionDelDiaDto(
    Guid Id, TipoTrabajoImpresion Tipo, string Documento, string Pc, DateTime FechaLocal, int Caras, int? CarasContadas, bool Color,
    long PrecioCentavos, EstadoCobroImpresion Cobro, MotivoNoVendida? Motivo, string? Atendio, CostoImpresionDto? Costo);

// ── La impresora (entrega 5, parte 2) ───────────────────────────────────────

/// <param name="Host">IP o nombre de la impresora en la red; vacío, no se lee.</param>
public sealed record ConfiguracionImpresoraDto(string? Host, int AvisoTintaPorcentaje, int MinutosEntreLecturas);

public sealed record SolicitudConfiguracionImpresora(string Usuario, string Pin, ConfiguracionImpresoraDto Configuracion);

/// <summary>Niveles en porcentaje restante (vacío: la impresora no lo dijo) y contadores de caras.</summary>
public sealed record LecturaImpresoraDto(
    DateTime TomadaLocal, int? TintaK, int? TintaC, int? TintaM, int? TintaY, int? Mantenimiento, long? Contador, long? ContadorColor,
    long? ContadorBN, string? Serie);

/// <summary>Lo que contó la impresora contra lo registrado (impresiones de las PC y copias cobradas) en un periodo.</summary>
/// <param name="SinRegistrar">Caras que contó y nadie registró: copias sin cobrar, impresiones desde el celular, páginas de prueba.</param>
public sealed record ComparacionContadorDto(
    DateTime DesdeLocal, DateTime HastaLocal, long Contadas, long? ContadasColor, int CarasImpresas, int CarasCopias, long SinRegistrar,
    long? SinRegistrarColor);

/// <param name="Error">Por qué falló la última lectura (vacío si respondió).</param>
public sealed record EstadoImpresoraDto(
    ConfiguracionImpresoraDto Configuracion, LecturaImpresoraDto? Ultima, string? Error, ComparacionContadorDto? Hoy);

/// <param name="Canal">Negro, Cian, Magenta o Amarillo.</param>
/// <param name="Observado">Puntos de nivel que bajó el cartucho en la impresora.</param>
/// <param name="Estimado">Puntos que estimó el modelo para los mismos periodos.</param>
/// <param name="Factor">La corrección que se aplicaría al costo de esa tinta.</param>
/// <param name="Actual">La corrección que se usa hoy.</param>
public sealed record CanalCalibradoDto(string Canal, double Observado, double Estimado, double? Proporcion, double Factor, double Actual, string Calidad);

/// <summary>La tinta real (niveles de la impresora) contra la estimada por el modelo: con esto se corrigen los costos.</summary>
public sealed record CalibracionDto(
    DateTime? DesdeLocal, DateTime? HastaLocal, IReadOnlyList<CanalCalibradoDto> Canales, long CarasImpresora, long CarasRegistradas,
    int? PaginasPorCaja, int PaginasPorCajaActual, IReadOnlyList<string> Cambios, DateTime? AplicadaLocal);

// ── Descuentos (entrega 5, parte 3) y clientes registrados (fase 3, entrega 2) ──

/// <summary>
/// Un cliente registrado con sus privilegios. Los primeros cinco campos son los de cuando eran «clientes especiales»:
/// así la copia de una caja que todavía no se actualiza los sigue leyendo.
/// </summary>
/// <param name="DescuentoCentesimas">Su descuento en centésimas de porcentaje (1000 = 10 %); cero, sin descuento.</param>
/// <param name="Telefono">Solo dígitos.</param>
/// <param name="PedidoSinAnticipo">Fase 4 (pedidos).</param>
/// <param name="ApartarSinPagar">Fase 4 (pedidos).</param>
/// <param name="PorRevisar">Venía de los clientes especiales: falta revisar sus datos.</param>
/// <param name="LimiteCreditoCentavos">Cuánto se le puede fiar; cero, sin crédito. Lo cambia quien puede dar crédito, por su propia ruta.</param>
/// <param name="PlazoCreditoDias">Días para pagar lo fiado.</param>
public sealed record ClienteRegistradoDto(
    Guid Id, string Nombre, int DescuentoCentesimas, string? Nota, bool Activo, string? Telefono = null, bool MayoreoSiempre = false,
    bool PedidoSinAnticipo = false, bool ApartarSinPagar = false, bool PorRevisar = false, long LimiteCreditoCentavos = 0,
    int PlazoCreditoDias = ClienteRegistrado.PlazoPorOmision)
{
    public bool TieneCredito => LimiteCreditoCentavos > 0;

    /// <summary>"descuento 10 % · mayoreo siempre · crédito" (vacío si no tiene privilegios de venta).</summary>
    public string TextoPrivilegios() => ClienteRegistrado.Privilegios(DescuentoCentesimas, MayoreoSiempre, TieneCredito);

    /// <summary>El motivo con que su descuento queda en cada línea: así se distingue de un descuento a mano.</summary>
    public string MotivoDescuento() => ClienteRegistrado.MotivoDescuento(Nombre);
}

/// <summary>El dueño o el encargado dan de alta o editan un cliente (sin Id: alta).</summary>
public sealed record SolicitudGuardarCliente(Guid UsuarioId, Guid SucursalId, ClienteRegistradoDto Cliente);

/// <summary>Un renglón de la lista de clientes, con lo que compra.</summary>
/// <param name="PromedioMensualCentavos">Lo que compra al mes: el promedio de los últimos tres meses completos.</param>
public sealed record ClienteResumenDto(ClienteRegistradoDto Cliente, long PromedioMensualCentavos, DateTime? UltimaCompraLocal);

/// <summary>Lo que compró en un mes (sin lo cancelado ni lo devuelto).</summary>
public sealed record CompraMesDto(int Anio, int Mes, int Tickets, long TotalCentavos);

/// <summary>La ficha del cliente con su evaluación: cuánto compra por mes.</summary>
public sealed record FichaClienteDto(ClienteRegistradoDto Cliente, IReadOnlyList<CompraMesDto> Meses, long PromedioMensualCentavos, DateTime? UltimaCompraLocal);

/// <summary>¿Este ticket, con sus descuentos y precios cambiados, deja líneas debajo del costo? El costo no viaja a la caja.</summary>
/// <param name="ClienteId">El cliente del ticket.</param>
/// <param name="MayoreoDeCliente">La caja calculó los precios con el mayoreo siempre del cliente.</param>
public sealed record SolicitudBajoCosto(Guid CajaId, IReadOnlyList<LineaSolicitada> Lineas, Guid? ClienteId = null, bool? MayoreoDeCliente = null);

/// <param name="FaltanteCentavos">Cuánto queda debajo del costo en total (cero: nada).</param>
/// <param name="Lineas">Las descripciones de las líneas que quedan debajo del costo.</param>
public sealed record BajoCostoDto(long FaltanteCentavos, IReadOnlyList<string> Lineas);

// ── Catálogo (fase 2, entrega 1) ────────────────────────────────────────────

/// <summary>Un renglón de la búsqueda de F3 Productos.</summary>
/// <param name="PrecioCentavos">El de menudeo de la primera variante.</param>
public sealed record ProductoCatalogoDto(
    Guid ProductoId, string Nombre, string? Marca, string Departamento, int Variantes, string? Codigo, long? PrecioCentavos, decimal? Existencia,
    bool Activo, bool PorCompletar);

public sealed record CodigoFichaDto(string Codigo, OrigenCodigo Origen = OrigenCodigo.Fabrica);

/// <param name="Id">Vacío en una presentación nueva.</param>
/// <param name="FactorMilesimas">Cuántas unidades base contiene, en milésimas (la base: 1000).</param>
public sealed record PresentacionFichaDto(Guid? Id, string Nombre, long FactorMilesimas, bool EsBase, IReadOnlyList<CodigoFichaDto> Codigos);

/// <param name="Presentacion">Vacío: precio por unidad base. Con nombre: el precio propio de esa presentación.</param>
/// <param name="CantidadMinimaMilesimas">Desde cuánto aplica (1000 = una unidad: menudeo; más, mayoreo).</param>
public sealed record PrecioFichaDto(string? Presentacion, long CantidadMinimaMilesimas, long PrecioCentavos);

public sealed record ProveedorFichaDto(Guid ProveedorId, string Proveedor, string? CodigoProveedor, long? UltimoCostoCentavos, DateTime? UltimaCompraLocal);

/// <param name="Existencia">En la sucursal, en unidades base (solo lectura).</param>
/// <param name="CostoPromedioCentavos">Solo para el dueño y el encargado (solo lectura).</param>
public sealed record VarianteFichaDto(
    Guid? Id, string Descripcion, UnidadBase Unidad, bool Activa, IReadOnlyList<PresentacionFichaDto> Presentaciones, IReadOnlyList<PrecioFichaDto> Precios,
    decimal? Existencia = null, decimal? CostoPromedioCentavos = null, IReadOnlyList<ProveedorFichaDto>? Proveedores = null);

/// <summary>La ficha de un producto en F3: lo que se edita junto (el producto, sus variantes, presentaciones, códigos y precios).</summary>
/// <param name="Marca">Por nombre: si no existe, se da de alta.</param>
public sealed record FichaProductoDto(
    Guid? Id, string Nombre, Guid DepartamentoId, string? Marca, TipoProducto Tipo, Impuesto Impuesto, string? ClaveSat, bool Activo, bool PorCompletar,
    IReadOnlyList<VarianteFichaDto> Variantes);

public sealed record SolicitudGuardarProducto(Guid UsuarioId, Guid SucursalId, FichaProductoDto Producto);

public sealed record OpcionCatalogoDto(Guid Id, string Nombre);

public sealed record OpcionesCatalogoDto(IReadOnlyList<OpcionCatalogoDto> Departamentos, IReadOnlyList<string> Marcas);

public sealed record MovimientoKardexDto(DateTime FechaLocal, TipoMovimiento Tipo, decimal Cantidad, decimal Existencia, string? Motivo, string? Documento);

/// <summary>Desde la caja: un código que se escaneó y no existe. Queda "por completar".</summary>
public sealed record SolicitudAltaRapida(Guid UsuarioId, Guid SucursalId, string Codigo, string Nombre, long PrecioCentavos, Guid DepartamentoId);

// ── Depuración del catálogo (fase 2, entrega 1, parte 2) ────────────────────

public sealed record SolicitudBaja(Guid UsuarioId, Guid SucursalId, Guid VarianteId);

/// <param name="OrigenId">La variante capturada dos veces (la que se va).</param>
/// <param name="DestinoId">La que se queda.</param>
public sealed record SolicitudJuntar(Guid UsuarioId, Guid SucursalId, Guid OrigenId, Guid DestinoId);

/// <param name="FactorMilesimas">Cuántas unidades nuevas hay en una de antes (en milésimas); vacío: la existencia se cuenta de nuevo.</param>
public sealed record SolicitudCambiarUnidad(Guid UsuarioId, Guid SucursalId, Guid VarianteId, UnidadBase Unidad, long? FactorMilesimas, long PrecioCentavos);

public sealed record DecisionDepuracionDto(DateTime FechaLocal, TipoDecisionDepuracion Tipo, string Descripcion, bool DeEleventa);

/// <summary>Una variante para elegir con cuál juntar: nombre, código y precio.</summary>
public sealed record VarianteParaJuntarDto(Guid VarianteId, string Nombre, string? Codigo, long? PrecioCentavos, UnidadBase Unidad);

// ── Compras (fase 2, entrega 2) ─────────────────────────────────────────────

/// <param name="Id">Vacío en uno nuevo.</param>
public sealed record ProveedorDto(Guid? Id, string Nombre, string? Telefono, string? Rfc, string? Notas, bool Activo = true);

public sealed record SolicitudGuardarProveedor(Guid UsuarioId, Guid SucursalId, ProveedorDto Proveedor);

/// <param name="Documento">El número de la nota o factura, si lo trae.</param>
public sealed record SolicitudAbrirCompra(Guid UsuarioId, Guid SucursalId, Guid ProveedorId, string? Documento);

/// <param name="TotalCentavos">Lo que cobra la nota; solo para quien captura costos.</param>
public sealed record CompraResumenDto(
    Guid Id, string Folio, string Proveedor, string? Documento, EstadoCompra Estado, DateTime AbiertaLocal, string Recibio, int Productos, long? TotalCentavos);

/// <summary>Pone lo contado de una presentación (cero la quita). Lo manda quien recibe, sin ver la nota.</summary>
/// <param name="CantidadMilesimas">En presentaciones: 3 cajas = 3000.</param>
public sealed record SolicitudConteo(Guid UsuarioId, Guid VarianteId, Guid PresentacionId, long CantidadMilesimas);

/// <param name="VarianteId">Vacío si se contó un código que no está en el catálogo (<paramref name="Codigo"/>).</param>
public sealed record ConteoCompraDto(
    Guid? VarianteId, Guid? PresentacionId, string Producto, string Presentacion, long CantidadMilesimas, decimal CantidadBase, string? Codigo = null);

/// <summary>Contar algo que llegó y no está en el catálogo, por su código (cero lo quita).</summary>
public sealed record SolicitudConteoDesconocido(Guid UsuarioId, string Codigo, string? Descripcion, long CantidadMilesimas);

/// <summary>Un renglón de la nota del proveedor. Sin <paramref name="RenglonId"/>, se agrega.</summary>
/// <param name="CantidadMilesimas">En la presentación de la nota (cajas, paquetes, piezas).</param>
/// <param name="ImporteCentavos">Lo que se paga por todo el renglón, con IVA y descuentos.</param>
public sealed record SolicitudRenglonCompra(
    Guid UsuarioId, Guid? RenglonId, Guid VarianteId, Guid PresentacionId, long CantidadMilesimas, long ImporteCentavos, string? CodigoProveedor = null);

/// <summary>Un renglón de la nota. Los que vienen del documento traen su descripción y unidad tal cual.</summary>
/// <param name="VarianteId">Vacío mientras un renglón del documento no se liga a su producto.</param>
/// <param name="CantidadMilesimas">En la presentación ligada (o en la unidad del documento, si no está ligado).</param>
/// <param name="Sugerencia">El producto del catálogo que se parece (sin ligar), con su presentación base.</param>
/// <param name="Nuevo">Si es un producto nuevo en borrador, lo precargado para revisarlo.</param>
/// <param name="Presentaciones">Las del producto ligado, para cambiarla en la revisión.</param>
public sealed record RenglonCompraDto(
    Guid Id, int Orden, string? CodigoProveedor, string Descripcion, string? Unidad, bool DelDocumento, Guid? VarianteId, Guid? PresentacionId,
    string? Producto, string? Presentacion, long CantidadMilesimas, long ImporteCentavos, Guid? SugerenciaId = null, string? Sugerencia = null,
    Guid? SugerenciaPresentacionId = null, ProductoNuevoDto? Nuevo = null, IReadOnlyList<PresentacionDto>? Presentaciones = null);

/// <summary>Un producto nuevo precargado desde el documento, para revisarlo y corregirlo antes de darlo de alta.</summary>
/// <param name="ContieneMilesimas">Piezas en cada unidad del documento.</param>
/// <param name="LlegoMilesimas">Cuántos llegaron, en unidades del documento; vacío: lo que contó quien recibió.</param>
/// <param name="ContadoPorCodigo">Lo que contó quien recibió con su código, en unidades del documento.</param>
/// <param name="CostoUnitarioCentavos">Por pieza, según el documento.</param>
public sealed record ProductoNuevoDto(
    string Nombre, Guid DepartamentoId, long PrecioCentavos, string? Codigo, long ContieneMilesimas, long? LlegoMilesimas, decimal? ContadoPorCodigo,
    decimal? CostoUnitarioCentavos, int? MargenCentesimas, long? PrecioSugeridoCentavos);

/// <summary>Corregir la cantidad (en su presentación o en la unidad del documento) y el importe de un renglón de la nota.</summary>
public sealed record SolicitudCorregirRenglon(Guid UsuarioId, long CantidadMilesimas, long ImporteCentavos);

/// <summary>Un renglón del documento como producto nuevo. Sin nombre se precarga solo; con él, se guarda lo que se mande.</summary>
public sealed record SolicitudProductoNuevo(
    Guid UsuarioId, string? Nombre = null, Guid? DepartamentoId = null, long? PrecioCentavos = null, string? Codigo = null, long? ContieneMilesimas = null,
    long? LlegoMilesimas = null);

/// <param name="Avisos">Lo que hay que revisar de la lectura.</param>
public sealed record DocumentoCompraDto(
    Guid Id, string Nombre, TipoDocumentoProveedor Tipo, string? Folio, long? TotalCentavos, IReadOnlyList<string> Avisos, DateTime CargadoLocal);

/// <summary>El XML de la factura o el PDF de la nota, tal cual.</summary>
public sealed record SolicitudDocumentoCompra(Guid UsuarioId, string Nombre, string ContenidoBase64);

/// <summary>Una variante de la compra: lo de la nota contra lo contado (en unidad base), el costo y el margen.</summary>
/// <param name="CostoUnitarioCentavos">Por unidad base, según la nota.</param>
/// <param name="CostoAnteriorCentavos">El último costo de este proveedor antes de esta compra.</param>
/// <param name="PrecioCentavos">El de menudeo.</param>
/// <param name="PrecioSugeridoCentavos">Si el margen queda debajo del mínimo: el precio que lo deja en el mínimo.</param>
public sealed record ComparacionCompraDto(
    Guid VarianteId, string Producto, UnidadBase Unidad, decimal Nota, decimal Contado, DiferenciaCompra Diferencia, decimal? CostoUnitarioCentavos,
    long? CostoAnteriorCentavos, long? PrecioCentavos, int? MargenCentesimas, int MargenMinimoCentesimas, long? PrecioSugeridoCentavos,
    decimal? Pedido = null);

/// <param name="Renglones">La nota: solo para quien captura costos, y cuando el conteo ya se cerró (quien cuenta no la ve).</param>
/// <param name="Comparacion">Igual que los renglones.</param>
/// <param name="Documentos">Los documentos del proveedor que se cargaron; igual que los renglones.</param>
/// <param name="OrdenId">La orden de compra de la que viene; igual que los renglones.</param>
public sealed record CompraDto(
    Guid Id, string Folio, Guid ProveedorId, string Proveedor, string? Documento, EstadoCompra Estado, DateTime AbiertaLocal, string Recibio,
    string? Costeo, DateTime? CerradaLocal, string? Motivo, IReadOnlyList<ConteoCompraDto> Conteo, IReadOnlyList<RenglonCompraDto>? Renglones,
    IReadOnlyList<ComparacionCompraDto>? Comparacion, long? TotalCentavos, IReadOnlyList<DocumentoCompraDto>? Documentos = null, Guid? OrdenId = null,
    string? Orden = null);

public sealed record SolicitudCompra(Guid UsuarioId);

public sealed record SolicitudCancelarCompra(Guid UsuarioId, string Motivo);

/// <summary>Cambiar el precio de menudeo de una variante (el sugerido después de una compra, u otro).</summary>
public sealed record SolicitudPrecioMenudeo(Guid UsuarioId, Guid SucursalId, Guid VarianteId, long PrecioCentavos);

/// <param name="MargenMinimoCentesimas">Vacío: el general.</param>
public sealed record MargenDepartamentoDto(Guid DepartamentoId, string Departamento, int? MargenMinimoCentesimas);

/// <summary>El margen mínimo general de la sucursal y el propio de cada departamento (centésimas: 3000 = 30 %).</summary>
public sealed record MargenesDto(int MargenMinimoCentesimas, IReadOnlyList<MargenDepartamentoDto> Departamentos);

/// <summary>Solo el dueño los cambia.</summary>
public sealed record SolicitudMargenes(Guid UsuarioId, MargenesDto Margenes);

// ── Órdenes de compra y abrir paquete (fase 2, entrega 2, parte 3) ──────────

public sealed record SolicitudAbrirOrden(Guid UsuarioId, Guid SucursalId, Guid ProveedorId, string? Notas);

/// <param name="Compra">El folio de la compra que la recibió.</param>
public sealed record OrdenResumenDto(Guid Id, string Folio, Guid ProveedorId, string Proveedor, EstadoOrden Estado, DateTime CreadaLocal, int Productos, string? Compra);

/// <param name="UltimoCostoCentavos">Por unidad base, con este proveedor.</param>
/// <param name="Existencia">En la sucursal, en unidad base.</param>
public sealed record OrdenRenglonDto(
    Guid VarianteId, Guid PresentacionId, string Producto, string Presentacion, long CantidadMilesimas, decimal CantidadBase, long? UltimoCostoCentavos,
    decimal? Existencia);

/// <param name="Texto">La orden lista para mandarla al proveedor (por WhatsApp o correo).</param>
public sealed record OrdenDto(
    Guid Id, string Folio, Guid ProveedorId, string Proveedor, EstadoOrden Estado, DateTime CreadaLocal, string Usuario, string? Notas,
    IReadOnlyList<OrdenRenglonDto> Renglones, string? Compra, string Texto);

/// <summary>Pone lo que se pide de una presentación (cero la quita).</summary>
public sealed record SolicitudPedido(Guid UsuarioId, Guid VarianteId, Guid PresentacionId, long CantidadMilesimas);

/// <summary>De qué orden viene una compra (vacío: de ninguna).</summary>
public sealed record SolicitudOrdenDeCompra(Guid UsuarioId, Guid? OrdenId);

/// <param name="CantidadMilesimas">Paquetes que se abren, en la unidad del paquete.</param>
/// <param name="CantidadDestinoMilesimas">Piezas que salieron de todos, en la unidad de la pieza.</param>
public sealed record SolicitudAbrirPaquete(Guid UsuarioId, Guid SucursalId, Guid OrigenId, long CantidadMilesimas, Guid DestinoId, long CantidadDestinoMilesimas);

public sealed record AperturaDto(DateTime FechaLocal, string Origen, decimal Cantidad, string Destino, decimal CantidadDestino, string Usuario);

/// <summary>La última vez que se abrió ese paquete: en qué producto y cuántas piezas traía cada uno.</summary>
public sealed record UltimaAperturaDto(Guid DestinoId, string Destino, decimal PiezasPorPaquete);

// ── Prueba con una persona real (docs/plan.md) ──────────────────────────────

/// <summary>Un aviso que le habría llegado al dueño por Telegram (en la prueba no se manda).</summary>
public sealed record AvisoPruebaDto(DateTime FechaLocal, TipoAlerta? Tipo, string Texto);

/// <param name="SalioBien">Vacío mientras no se contesta.</param>
public sealed record RespuestaTareaDto(bool? SalioBien, string? Nota);

/// <summary>Lo que contestó quien prueba, por tarea de la guía.</summary>
public sealed record RespuestasPruebaDto(string? Nombre, IReadOnlyDictionary<string, RespuestaTareaDto> Tareas, string? Comentarios);

/// <summary>Las respuestas ya escritas como documento, para guardarlas en un archivo.</summary>
public sealed record SolicitudExportarRespuestas(string Texto);

public sealed record ArchivoGuardadoDto(string Ruta);

// ── Ajustes y mermas (fase 2, entrega 3) ────────────────────────────────────

/// <summary>Corregir la existencia a la que se encontró. El motivo es obligatorio.</summary>
/// <param name="ExistenciaRealMilesimas">La que hay de verdad, en unidad base.</param>
/// <param name="AutorizacionId">Si la regla pide la del encargado.</param>
public sealed record SolicitudAjuste(Guid UsuarioId, Guid SucursalId, Guid VarianteId, long ExistenciaRealMilesimas, string Motivo, Guid? AutorizacionId = null);

/// <summary>Sacar lo dañado, caducado o perdido.</summary>
/// <param name="CantidadMilesimas">En unidad base.</param>
public sealed record SolicitudMerma(
    Guid UsuarioId, Guid SucursalId, Guid VarianteId, long CantidadMilesimas, CausaMerma Causa, string? Motivo, Guid? AutorizacionId = null);

/// <param name="CostoCentavos">Solo para el dueño y el encargado.</param>
public sealed record AjusteInventarioDto(
    DateTime FechaLocal, TipoMovimiento Tipo, string Producto, decimal Cantidad, long? CostoCentavos, CausaMerma? Causa, string Motivo, string Usuario,
    string? Autorizo);

// ── Conteo cíclico (fase 2, entrega 3, parte 2) ─────────────────────────────

/// <summary>Un producto del conteo del día. Lo que decía el sistema y la diferencia solo los ven el dueño y el encargado.</summary>
/// <param name="Presentaciones">Para contar cajas y piezas: "Caja 12 = 12".</param>
public sealed record RenglonConteoDto(
    Guid VarianteId, string Producto, string? Codigo, UnidadBase Unidad, string? Presentaciones, decimal? Contado, DateTime? ContadoLocal,
    string? ContadoPor, decimal? Esperado, decimal? Diferencia, long? CostoDiferenciaCentavos);

public sealed record ConteoCiclicoDto(Guid Id, DateOnly Fecha, int Total, int Contados, IReadOnlyList<RenglonConteoDto> Renglones);

/// <param name="CantidadMilesimas">En unidad base.</param>
public sealed record SolicitudContar(Guid UsuarioId, Guid VarianteId, long CantidadMilesimas);

// ── Conteo completo por departamento (fase 2, entrega 3, parte 3) ───────────

/// <param name="FechaLocal">Cuándo se abrió (si está abierto) o se cerró.</param>
/// <param name="Usuario">Quien lo abrió o lo cerró.</param>
/// <param name="Contados">Productos con al menos una captura.</param>
/// <param name="Personas">Quienes han capturado.</param>
public sealed record ConteoResumenDto(Guid Id, DateTime FechaLocal, string Usuario, int Contados, IReadOnlyList<string> Personas);

/// <summary>Un departamento en el conteo completo: si se está contando y cuándo se cerró el último.</summary>
/// <param name="Productos">Los de inventario activos: lo que hay que contar.</param>
public sealed record DepartamentoConteoDto(Guid DepartamentoId, string Departamento, int Productos, ConteoResumenDto? Abierto, ConteoResumenDto? UltimoCerrado);

/// <summary>Cómo abrir la página del conteo en un celular conectado al wifi de la tienda.</summary>
/// <param name="QrSvg">La primera dirección como código QR, para abrirla con la cámara.</param>
public sealed record DireccionCelularDto(IReadOnlyList<string> Direcciones, string? QrSvg);

/// <summary>Una presentación para contar por cajas o paquetes, con sus códigos para reconocer lo escaneado.</summary>
public sealed record PresentacionConteoDto(string Nombre, long FactorMilesimas, bool EsBase, IReadOnlyList<string> Codigos);

/// <summary>Un producto del departamento. Lo que decía el sistema y la diferencia solo vienen en la revisión.</summary>
/// <param name="Contado">La suma de lo que capturaron todos; vacío si nadie lo ha contado.</param>
public sealed record ProductoConteoDto(
    Guid VarianteId, string Producto, string? Codigo, UnidadBase Unidad, IReadOnlyList<PresentacionConteoDto> Presentaciones,
    decimal? Contado, decimal? Sistema, decimal? Diferencia, long? CostoDiferenciaCentavos);

public sealed record CapturaConteoDto(Guid Id, Guid VarianteId, decimal Cantidad, Guid UsuarioId, string Usuario, DateTime CapturadoLocal);

/// <param name="Revision">Si trae lo que decía el sistema: solo el dueño y el encargado, al revisar antes de cerrar o ya cerrado.</param>
/// <param name="NetoCentavos">En la revisión: lo que vale la diferencia de todo el departamento (+ sobró, − faltó).</param>
/// <param name="Capturas">Las vigentes, de la más reciente a la más vieja.</param>
public sealed record ConteoDepartamentoDto(
    Guid Id, Guid DepartamentoId, string Departamento, EstadoConteoDepartamento Estado, string AbiertoPor, DateTime AbiertoLocal,
    string? TerminadoPor, DateTime? TerminadoLocal, int Productos, int Contados, bool Revision, long? NetoCentavos,
    IReadOnlyList<ProductoConteoDto> Renglones, IReadOnlyList<CapturaConteoDto> Capturas);

public sealed record SolicitudAbrirConteo(Guid UsuarioId, Guid DepartamentoId);

/// <param name="Id">Lo genera quien captura: si llega dos veces, cuenta una.</param>
/// <param name="CantidadMilesimas">En unidad base.</param>
public sealed record SolicitudCaptura(Guid Id, Guid UsuarioId, Guid VarianteId, long CantidadMilesimas);

public sealed record SolicitudConteoCompleto(Guid UsuarioId);

// ── Etiquetas (fase 2, entrega 4, parte 1) ──────────────────────────────────

/// <summary>La hoja de etiquetas de la sucursal y cuánto se mueve todo para que caiga en las etiquetas.</summary>
/// <param name="FormatoId">Una de <see cref="FormatoEtiquetas.Comunes"/> o <see cref="FormatoEtiquetas.Propio"/>.</param>
/// <param name="AjusteXDecimas">Décimas de milímetro: + a la derecha.</param>
/// <param name="AjusteYDecimas">Décimas de milímetro: + hacia abajo.</param>
public sealed record ConfiguracionEtiquetasDto(string FormatoId, FormatoEtiquetas? Propio, int AjusteXDecimas, int AjusteYDecimas);

public sealed record SolicitudConfiguracionEtiquetas(Guid UsuarioId, ConfiguracionEtiquetasDto Configuracion);

/// <summary>Un producto para la lista de etiquetas.</summary>
/// <param name="Codigo">El que sale en la etiqueta; vacío si todavía no tiene (se le da uno interno al imprimir).</param>
/// <param name="DeFabrica">Si su código ya viene impreso en el empaque (casi nunca necesita etiqueta).</param>
/// <param name="Sugeridas">Cuántas etiquetas: lo que llegó en la compra, en piezas; una si no.</param>
public sealed record ProductoEtiquetaDto(Guid VarianteId, string Producto, string? Codigo, bool DeFabrica, int Sugeridas);

public sealed record RenglonEtiquetasDto(Guid VarianteId, int Cantidad);

/// <param name="ConPrecio">El precio de menudeo en la etiqueta (casi nunca: cambia y habría que reimprimir).</param>
public sealed record SolicitudEtiquetas(Guid UsuarioId, Guid SucursalId, IReadOnlyList<RenglonEtiquetasDto> Renglones, bool ConPrecio);

public sealed record EtiquetaDto(Guid VarianteId, string Nombre, string Codigo, long? PrecioCentavos);

/// <param name="Etiquetas">Una por cada etiqueta que se imprime, en el orden de la lista.</param>
/// <param name="CodigosNuevos">A cuántos productos se les dio código interno porque no tenían.</param>
public sealed record EtiquetasDto(IReadOnlyList<EtiquetaDto> Etiquetas, int CodigosNuevos);

// ── Hoja de códigos del mostrador (fase 2, entrega 4, parte 2) ───────────────

/// <param name="Codigo">Vacío si todavía no tiene: se le da uno interno al preparar la hoja.</param>
public sealed record RenglonHojaCodigosDto(Guid VarianteId, string Producto, string Departamento, string? Codigo);

/// <param name="Variantes">Los productos de la hoja, en el orden en que salen.</param>
public sealed record SolicitudHojaCodigos(Guid UsuarioId, IReadOnlyList<Guid> Variantes);

public sealed record SolicitudPrepararHojaCodigos(Guid UsuarioId, bool ConPrecio);

/// <param name="Productos">En el orden de la hoja, ya con código.</param>
/// <param name="CodigosNuevos">A cuántos se les dio código interno porque no tenían.</param>
public sealed record HojaCodigosDto(string Sucursal, IReadOnlyList<ProductoHojaCodigosDto> Productos, int CodigosNuevos);

public sealed record ProductoHojaCodigosDto(string Departamento, string Producto, string Codigo, long? PrecioCentavos);

// ── Sugerido de compra (fase 2, entrega 4, parte 3) ──────────────────────────

/// <param name="CoberturaDias">Cada cuánto se compra: lo que tiene que alcanzar un pedido.</param>
/// <param name="ColchonDias">Lo que se guarda de más (es el mínimo).</param>
public sealed record ParametrosSugeridoDto(int CoberturaDias, int ColchonDias);

public sealed record SolicitudParametrosSugerido(Guid UsuarioId, ParametrosSugeridoDto Parametros);

/// <summary>Un producto por pedir. Cantidades en unidad base, salvo <paramref name="Paquetes"/>.</summary>
/// <param name="VentaDiaria">Ya con la temporada.</param>
/// <param name="Temporada">Cuánto cambia la venta en estas semanas según el año pasado (1 = igual).</param>
/// <param name="Paquetes">Cuántos de <paramref name="Presentacion"/> pedir.</param>
/// <param name="CostoPaqueteCentavos">Con el último costo de este proveedor (o el promedio).</param>
public sealed record RenglonSugeridoDto(
    Guid VarianteId, string Producto, string? CodigoProveedor, UnidadBase Unidad, decimal Existencia, decimal EnCamino, decimal VentaDiaria,
    decimal Temporada, decimal Minimo, decimal Maximo, Guid PresentacionId, string Presentacion, decimal Factor, int Paquetes, long? CostoPaqueteCentavos,
    bool Urgente);

/// <param name="ProveedorId">Vacío: productos sin proveedor ligado (se elige al hacer la orden).</param>
public sealed record ProveedorSugeridoDto(Guid? ProveedorId, string Proveedor, IReadOnlyList<RenglonSugeridoDto> Renglones, long TotalCentavos);

public sealed record SugeridoDto(DateOnly Fecha, ParametrosSugeridoDto Parametros, IReadOnlyList<ProveedorSugeridoDto> Proveedores);

public sealed record RenglonOrdenSugeridaDto(Guid VarianteId, Guid PresentacionId, int Paquetes);

public sealed record SolicitudOrdenSugerida(Guid UsuarioId, Guid ProveedorId, IReadOnlyList<RenglonOrdenSugeridaDto> Renglones);

// ── Mayoreo de eleventa por revisar (fase 3, entrega 1) ─────────────────────

/// <summary>Un mayoreo de eleventa (F11) que la importación no pudo pasar a escala.</summary>
/// <param name="PrecioMayoreoCentavos">El de eleventa, por pieza (o metro, kilo).</param>
/// <param name="Cantidades">Lo más vendido con F11: "12 ×5 · 24 ×2".</param>
/// <param name="SugeridaMilesimas">La cantidad más vendida con F11, para proponerla.</param>
public sealed record MayoreoPorRevisarDto(
    Guid Id, Guid VarianteId, Guid ProductoId, string Producto, string? Codigo, UnidadBase Unidad, long MenudeoCentavos, long PrecioMayoreoCentavos,
    long CostoCentavos, int VentasConMayoreo, string Cantidades, string Motivo, long? SugeridaMilesimas);

/// <summary>La decisión sobre el mayoreo de eleventa: una escala, o sin mayoreo (las dos vacías).</summary>
/// <param name="DesdeMilesimas">Desde cuántas, en unidad base.</param>
/// <param name="PrecioCentavos">Por unidad base.</param>
public sealed record SolicitudMayoreo(Guid UsuarioId, Guid SucursalId, Guid VarianteId, long? DesdeMilesimas, long? PrecioCentavos);
