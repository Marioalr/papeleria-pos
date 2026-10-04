using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Impresiones;
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
public sealed record LineaSolicitada(
    Guid? VarianteId, Guid? PresentacionId, long CantidadMilesimas, string? Descripcion = null, long? PrecioCentavos = null, Guid? AutorizacionId = null);

/// <summary>El servicio vuelve a armar la venta con los precios del catálogo: nunca confía en los precios de la caja.</summary>
/// <param name="VentaId">Lo pone la caja: si la respuesta no llegó y se manda otra vez (o sube con la bandeja), no se cobra dos veces.</param>
/// <param name="Impresiones">Los trabajos de impresión que se cobran: el servicio los junta en la línea "Impresiones".</param>
public sealed record SolicitudCobro(
    Guid CajaId, Guid TurnoId, Guid CajeroId, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<CobroSolicitado> Cobros, Guid? VentaId = null,
    IReadOnlyList<Guid>? Impresiones = null);

public sealed record LineaTicketDto(string Cantidad, string Descripcion, long PrecioCentavos, long ImporteCentavos);

public sealed record CobroTicketDto(FormaPago Forma, long MontoCentavos, long RecibidoCentavos, long CambioCentavos, string? Referencia);

/// <summary>Logo en blanco y negro listo para la impresora: un bit por punto, renglones de (Ancho + 7) / 8 bytes.</summary>
public sealed record LogoTicketDto(int Ancho, int Alto, string BitsBase64);

public sealed record TicketDto(
    string Folio, DateTime FechaLocal, string Caja, string Cajero, IReadOnlyList<LineaTicketDto> Lineas, decimal NumeroDeArticulos,
    long TotalCentavos, long IvaCentavos, IReadOnlyList<CobroTicketDto> Cobros, IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie,
    LogoTicketDto? Logo = null, Guid? VentaId = null)
{
    public long CambioCentavos => Cobros.Sum(c => c.CambioCentavos);
    public long PagoConCentavos => Cobros.Sum(c => c.Forma == FormaPago.Efectivo ? c.RecibidoCentavos : c.MontoCentavos);
    public bool AbreCajon => Cobros.Any(c => c.Forma == FormaPago.Efectivo);
}

public sealed record VentaDelDiaDto(Guid Id, string Folio, DateTime FechaLocal, long TotalCentavos, int Articulos, string Cajero, bool Cancelada = false);

/// <summary>El encargado se identifica con su usuario y PIN en la misma caja.</summary>
public sealed record SolicitudCorteZ(string EncargadoUsuario, string EncargadoPin, IReadOnlyDictionary<long, int> Conteo);

public sealed record CorteDto(
    TipoCorte Tipo, DateTime FechaLocal, string Cajero, string RealizadoPor, int Ventas, long FondoCentavos, long EfectivoVentasCentavos,
    long EntradasCentavos, long SalidasCentavos, long TarjetaCentavos, long TransferenciaCentavos, long EsperadoCentavos,
    long? ContadoCentavos, long? DiferenciaCentavos, long DevolucionesCentavos = 0, int Cancelaciones = 0, bool EnContingencia = false);

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
public sealed record ComprobanteDevolucionDto(
    string Folio, bool EsCancelacion, long TotalCentavos, long ReembolsoEfectivoCentavos, string Motivo, DateTime FechaLocal, string Caja,
    string Usuario, string? Autorizo, IReadOnlyList<LineaTicketDto> Lineas, IReadOnlyList<string> Encabezado);

public sealed record TransferenciaDto(Guid CobroId, string Folio, DateTime FechaLocal, long MontoCentavos, string? Referencia, string Cajero, Verificacion Verificacion);

public sealed record SolicitudVerificacion(string Usuario, string Pin, bool Llego);

public sealed record SolicitudConsultaBitacora(string Usuario, string Pin, DateOnly Desde, DateOnly Hasta);

public sealed record BitacoraDto(DateTime FechaLocal, string? Usuario, AccionControlada Accion, string Detalle, long? MontoCentavos, string? Autorizo, ResultadoBitacora Resultado);

// ── Varias cajas (entrega 3) ────────────────────────────────────────────────

/// <summary>Lo que contesta el servicio sin llave: para encontrarlo en la red y saber que está vivo.</summary>
/// <param name="VersionDatos">Cambia con el catálogo, los usuarios o las reglas: la caja vuelve a bajar su copia.</param>
public sealed record EstadoServicioDto(string Servicio, string Version, IReadOnlyList<string> Sucursales, long VersionDatos = 0);

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
public sealed record PaqueteContingenciaDto(
    long Version, CajaDto Caja, IReadOnlyList<ProductoVentaDto> Productos, IReadOnlyList<CodigoLocalDto> Codigos, IReadOnlyList<UsuarioLocalDto> Usuarios,
    IReadOnlyList<ReglaDto> Reglas,
    IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo, int UltimoFolio, long? LimiteEfectivoCentavos);

// Lo que sube con la bandeja. Cada cosa lleva el ID que le dio la caja: si llega dos veces, se guarda una.

public sealed record TurnoContingenciaDto(Guid Id, Guid CajaId, Guid CajeroId, long FondoCentavos, DateTime AbiertoUtc);

/// <param name="PrecioCentavos">El que se cobró (por presentación, con IVA): manda sobre el del catálogo.</param>
public sealed record LineaContingenciaDto(Guid? VarianteId, Guid? PresentacionId, string Descripcion, long CantidadMilesimas, long PrecioCentavos);

public sealed record VentaContingenciaDto(
    Guid Id, Guid CajaId, Guid TurnoId, Guid CajeroId, int Folio, DateTime PagadaUtc, IReadOnlyList<LineaContingenciaDto> Lineas,
    IReadOnlyList<CobroSolicitado> Cobros);

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
    TimeOnly HoraApertura, TimeOnly HoraCierre);

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
public sealed record TrabajoMedidoDto(
    Guid Id, Guid CajaId, Guid? AtendioId, string Pc, string Cola, long SpoolId, string Documento, string? UsuarioWindows, DateTime ImpresoUtc,
    EstadoTrabajo Estado, AjustesTrabajoDto Ajustes, IReadOnlyList<CoberturaPaginaDto> Paginas, bool Medido, string? Nota, string? MiniaturaBase64);

/// <summary>Solo el dueño lo recibe.</summary>
public sealed record CostoImpresionDto(long TintaCentavos, long PapelCentavos, long OtrosCentavos, long TotalCentavos, decimal MargenPorcentaje);

public sealed record ImpresionPorCobrarDto(
    Guid Id, string Documento, string Pc, DateTime FechaLocal, int Paginas, int Caras, bool Color, string Tamano, long PrecioCentavos,
    string? Atendio, string? MiniaturaBase64, string? Nota, CostoImpresionDto? Costo = null);

public sealed record SolicitudNoVendida(Guid UsuarioId, MotivoNoVendida Motivo, string? Nota);
