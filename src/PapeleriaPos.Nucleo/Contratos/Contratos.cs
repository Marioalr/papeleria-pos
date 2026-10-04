using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;
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

public sealed record CajaDto(Guid Id, string Nombre, string Serie, Guid SucursalId, string Sucursal);

public sealed record TurnoDto(Guid Id, Guid CajaId, Guid CajeroId, string Cajero, DateTime AbiertoLocal, long FondoCentavos);

public sealed record SolicitudTurno(Guid CajaId, Guid CajeroId, long FondoCentavos);

public sealed record SolicitudMovimiento(TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, Guid UsuarioId);

/// <summary>Una línea tal como la manda la caja. Sin variante es artículo común, con descripción y precio a mano.</summary>
/// <param name="PrecioCentavos">En un producto del catálogo, el precio cambiado en la caja (con <paramref name="AutorizacionId"/>).</param>
public sealed record LineaSolicitada(
    Guid? VarianteId, Guid? PresentacionId, long CantidadMilesimas, string? Descripcion = null, long? PrecioCentavos = null, Guid? AutorizacionId = null);

/// <summary>El servicio vuelve a armar la venta con los precios del catálogo: nunca confía en los precios de la caja.</summary>
public sealed record SolicitudCobro(Guid CajaId, Guid TurnoId, Guid CajeroId, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<CobroSolicitado> Cobros);

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
    long? ContadoCentavos, long? DiferenciaCentavos, long DevolucionesCentavos = 0, int Cancelaciones = 0);

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
