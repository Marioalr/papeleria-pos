using System.Text.Json.Serialization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
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
/// <param name="Version">
/// La versión del catálogo de la que salieron sus precios (entrega 5): la de la copia de la caja, o la del servicio cuando se
/// lo pidió. El cobro la lleva, para que el servicio sepa si sus precios cambiaron después. Vacía en un servicio de antes.
/// </param>
public sealed record ProductoVentaDto(
    Guid VarianteId, string Descripcion, string? Codigo, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario,
    decimal? Existencia, PresentacionDto Presentacion, IReadOnlyList<PresentacionDto> Presentaciones, IReadOnlyList<PrecioDto> Precios,
    long? Version = null)
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

/// <param name="Vendidos">Cuántas veces se vendió en 90 días: en una familia, Enter agrega el más vendido.</param>
/// <param name="Partes">El nombre en pedazos (color, medida, marca…), para juntar las familias en la lista.</param>
/// <param name="Parecido">Nada coincidía exacto: salió por un error de dedo («sobre manial»).</param>
/// <param name="PorSinonimo">La palabra de la tienda por la que salió («boligrafo», si su nombre dice «Lapicero»).</param>
public sealed record ResultadoBusquedaDto(
    Guid VarianteId, string Descripcion, string? Codigo, long PrecioCentavos, decimal? Existencia, int Vendidos = 0,
    IReadOnlyList<ParteDeNombre>? Partes = null, bool Parecido = false, string? PorSinonimo = null);

/// <summary>Un sinónimo de la tienda: al escribir <paramref name="Palabra"/> también sale lo que se llama <paramref name="Equivale"/>.</summary>
public sealed record SinonimoDto(Guid Id, string Palabra, string Equivale);

/// <param name="AlReves">También al revés: al escribir lo que equivale, sale lo de la palabra.</param>
public sealed record SolicitudSinonimo(Guid UsuarioId, Guid SucursalId, string Palabra, string Equivale, bool AlReves);

public sealed record SolicitudQuitarSinonimo(Guid UsuarioId, Guid SucursalId);

/// <summary>Lo que se buscó en una caja y qué se eligió (vacío: no salió nada o no se eligió nada).</summary>
public sealed record SolicitudBusqueda(Guid CajaId, string Texto, int Resultados, Guid? VarianteId);

public sealed record SolicitudEntrada(string Usuario, string Pin);

/// <summary>
/// Quien entró a la caja, con todo lo que puede (entrega 1): la caja pinta con esto sus pestañas, teclas y candados, sin
/// preguntar en cada clic, y el servicio vuelve a revisar cada operación.
/// </summary>
/// <param name="Rol">De antes de la entrega 1: ya no decide nada. Solo lo leen las cajas que no se han actualizado.</param>
/// <param name="Permisos">
/// Cada acción, con quién la autoriza (lo mismo que <c>api/usuarios/{id}/permisos</c>). Vacío si lo mandó un servicio de
/// antes: la pantalla de venta los pide aparte y, sin ellos, todo se ve y el servicio decide.
/// </param>
/// <param name="Resumen">Lo que puede, en pocas palabras («Todos los permisos», «Permisos por defecto»…): el menú del usuario.</param>
public sealed record SesionDto(Guid UsuarioId, string Nombre, Rol Rol, IReadOnlyList<PermisoDeSesion>? Permisos = null, string? Resumen = null)
{
    public PermisoDeSesion? Permiso(AccionControlada accion) => Permisos?.FirstOrDefault(p => p.Accion == accion);

    /// <summary>Lo puede hacer sin pedir autorización. Sin los permisos no se sabe: se ve, y el servicio decide.</summary>
    public bool Puede(AccionControlada accion) => Permisos is null || Permiso(accion)?.Nivel == NivelPermiso.Permitido;

    /// <summary>Se le enseña: lo puede hacer o lo puede pedir («No»: no aparece). Sin los permisos, también se ve.</summary>
    public bool Ve(AccionControlada accion) => Permisos is null || Permiso(accion) is { Nivel: not NivelPermiso.NoPermitido };

    /// <summary>Iguales si dicen lo mismo, también cada permiso (la de la caja principal y la de la copia de la caja).</summary>
    public bool Equals(SesionDto? otra) =>
        otra is not null && (UsuarioId, Nombre, Rol, Resumen) == (otra.UsuarioId, otra.Nombre, otra.Rol, otra.Resumen)
        && (Permisos is null) == (otra.Permisos is null) && (Permisos ?? []).SequenceEqual(otra.Permisos ?? []);

    public override int GetHashCode() => HashCode.Combine(UsuarioId, Nombre, Rol, Resumen);
}

/// <summary>
/// Una ficha de la entrada de la caja (entrega 6): quien puede entrar en ella, sin su usuario, su PIN ni el hash del PIN.
/// Con su letra se elige desde el teclado (<see cref="FichasEntrada"/>).
/// </summary>
/// <param name="Tecla">Vacía si ya no alcanzó letra (más de 26 usuarios): se toca.</param>
/// <param name="Resumen">Lo que puede hacer, en pocas palabras: «Permisos por defecto», «Todos los permisos»…</param>
/// <param name="TurnoAbierto">Tiene abierto el turno de esa caja.</param>
/// <param name="HaceCorte">Puede hacer el corte de un turno: la caja con el turno de otro dice quién lo hace.</param>
public sealed record FichaUsuarioDto(Guid UsuarioId, string Nombre, string? Tecla, string Resumen, bool TurnoAbierto, bool HaceCorte);

/// <summary>Entrar con la ficha: el usuario se tocó en la caja; el PIN lo revisa el servicio (sin conexión, la caja con su copia).</summary>
public sealed record SolicitudEntradaFicha(Guid UsuarioId, string Pin);

/// <param name="Equipo">La PC dada de alta como esta caja; vacío si ninguna.</param>
public sealed record CajaDto(Guid Id, string Nombre, string Serie, Guid SucursalId, string Sucursal, string? Equipo = null);

public sealed record TurnoDto(Guid Id, Guid CajaId, Guid CajeroId, string Cajero, DateTime AbiertoLocal, long FondoCentavos);

public sealed record SolicitudTurno(Guid CajaId, Guid CajeroId, long FondoCentavos, Guid? Id = null);

public sealed record SolicitudMovimiento(TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, Guid UsuarioId, Guid? Id = null);

/// <summary>Una línea tal como la manda la caja. Sin variante es artículo común, con descripción y precio a mano.</summary>
/// <param name="PrecioCentavos">En un producto del catálogo, el precio cambiado en la caja (con <paramref name="AutorizacionId"/>).</param>
/// <param name="DescuentoCentesimas">Descuento en porcentaje (centésimas: 1000 = 10 %); o <paramref name="DescuentoCentavos"/> en monto. Con motivo.</param>
/// <param name="VersionCatalogo">
/// Al cobrar (entrega 5): la versión del catálogo de la que salieron los precios que el cajero vio en esta línea
/// (<see cref="ProductoVentaDto.Version"/>), si no es la del ticket (<see cref="SolicitudCobro.VersionCatalogo"/>): un producto
/// que llegó de la caja principal, o una línea de antes de que bajara la copia de ahora.
/// </param>
public sealed record LineaSolicitada(
    Guid? VarianteId, Guid? PresentacionId, long CantidadMilesimas, string? Descripcion = null, long? PrecioCentavos = null, Guid? AutorizacionId = null,
    int? DescuentoCentesimas = null, long? DescuentoCentavos = null, string? MotivoDescuento = null, long? VersionCatalogo = null);

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
/// <param name="VersionCatalogo">
/// La versión de la copia del catálogo con que la caja puso los precios del ticket (entrega 5; cada línea puede decir otra,
/// <see cref="LineaSolicitada.VersionCatalogo"/>). Si el precio o el mayoreo de algo del ticket cambió después, el servicio no
/// cobra: contesta con los de ahora (<see cref="PreciosCambiadosDto"/>), para que el ticket diga lo que vio el cajero.
/// Vacía en las cajas de antes: se cobra como siempre.
/// </param>
public sealed record SolicitudCobro(
    Guid CajaId, Guid TurnoId, Guid CajeroId, IReadOnlyList<LineaSolicitada> Lineas, IReadOnlyList<CobroSolicitado> Cobros, Guid? VentaId = null,
    IReadOnlyList<Guid>? Impresiones = null, [property: JsonPropertyName("clienteEspecialId")] Guid? ClienteId = null,
    Guid? AutorizacionDescuentoId = null, Guid? AutorizacionBajoCostoId = null, bool? MayoreoDeCliente = null, Guid? AutorizacionCreditoId = null,
    long? VersionCatalogo = null);

/// <summary>
/// El cobro no se hizo (entrega 5): el precio o el mayoreo de algo del ticket cambió después de la versión con que la caja
/// puso sus precios. Llega con un 409 y se lee también como <see cref="ErrorDto"/>.
/// </summary>
/// <param name="Productos">Los que cambiaron, como están ahora (con su versión).</param>
/// <param name="VersionCatalogo">Hasta esta versión, lo demás del ticket no cambió.</param>
public sealed record PreciosCambiadosDto(string Mensaje, IReadOnlyList<ProductoVentaDto> Productos, long VersionCatalogo, string Codigo = PreciosCambiadosDto.CodigoDelError)
{
    public const string CodigoDelError = "precios-cambiados";
}

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
/// <param name="Folio">El de esta caja (A-000123): con él se abre la devolución en la caja nueva.</param>
/// <param name="FolioEleventa">En el piloto, el que le dio eleventa si llegó a tiempo: es el que se imprime, para devolver en cualquier caja.</param>
/// <param name="YaCobrada">
/// El cobro llegó con el Id de una venta que ya estaba (se había perdido la respuesta, entrega 5, parte 2): es esa, como se
/// cobró entonces, y la pantalla lo dice.
/// </param>
public sealed record TicketDto(
    string Folio, DateTime FechaLocal, string Caja, string Cajero, IReadOnlyList<LineaTicketDto> Lineas, decimal NumeroDeArticulos,
    long TotalCentavos, long IvaCentavos, IReadOnlyList<CobroTicketDto> Cobros, IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie,
    LogoTicketDto? Logo = null, Guid? VentaId = null, long RedondeoCentavos = 0, string? Cliente = null, CreditoTicketDto? Credito = null,
    int? FolioEleventa = null, bool YaCobrada = false)
{
    /// <summary>El folio que se lee en el ticket y en la pantalla.</summary>
    public string FolioImpreso => FolioEleventa?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Folio;

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

/// <summary>
/// El corte del turno (entrega 6, 2026-10-14): quien ya entró con su PIN y tiene el permiso escribe el total del efectivo
/// que contó, sin billete por billete y sin volver a escribir su PIN. El servicio revisa el permiso de ese usuario.
/// </summary>
/// <param name="UsuarioId">Quien hace el corte: el de la sesión.</param>
/// <param name="ContadoCentavos">El total del efectivo que contó.</param>
/// <param name="Id">
/// El del corte, desde la caja (entrega 5, parte 2): si la caja principal lo guardó pero la respuesta no llegó, el que hace la
/// caja sin conexión es el mismo, y al subir no choca con el turno ya cerrado. Vacío (una caja de antes): uno nuevo.
/// </param>
public sealed record SolicitudCorteZ(Guid UsuarioId, long ContadoCentavos, Guid? Id = null);

/// <param name="UmbralAvisoCentavos">
/// En el corte Z: el umbral de la sucursal, si la diferencia pasó de él y el aviso sale por Telegram. Vacío si no.
/// </param>
public sealed record CorteDto(
    TipoCorte Tipo, DateTime FechaLocal, string Cajero, string RealizadoPor, int Ventas, long FondoCentavos, long EfectivoVentasCentavos,
    long EntradasCentavos, long SalidasCentavos, long TarjetaCentavos, long TransferenciaCentavos, long EsperadoCentavos,
    long? ContadoCentavos, long? DiferenciaCentavos, long DevolucionesCentavos = 0, int Cancelaciones = 0, bool EnContingencia = false,
    long RedondeoCentavos = 0, long CreditoCentavos = 0, int Abonos = 0, long AbonosEfectivoCentavos = 0, long AbonosTarjetaCentavos = 0,
    long AbonosTransferenciaCentavos = 0, long SaldoDevueltoCentavos = 0, long? UmbralAvisoCentavos = null);

/// <summary>
/// El corte X (entrega 6, 2026-10-14): lo que la pantalla enseña del turno antes de cerrarlo. Es a ciegas: no trae el
/// efectivo (ni el fondo, lo esperado, las entradas o las salidas), porque quien hace el corte cuenta sin saber cuánto
/// debería haber. Tarjeta, transferencias, abonos y lo fiado son información. También dice si quien pregunta puede hacer
/// el corte y, si no, quién puede.
/// </summary>
/// <param name="TransferenciasPorVerificar">Vacío sin conexión: la caja no lo sabe.</param>
/// <param name="PuedeCortar">Quien pregunta tiene «hacer el corte de un turno»; el servicio lo vuelve a revisar al cerrar.</param>
/// <param name="QuienesCortan">Quienes lo pueden hacer, para decírselo a quien no puede.</param>
/// <param name="VeElDia">Quien pregunta tiene «ver y cerrar el corte del día».</param>
public sealed record CorteInformativoDto(
    string Cajero, DateTime AbiertoLocal, int Ventas, int Cancelaciones, long TarjetaCentavos, long TransferenciaCentavos,
    int? TransferenciasPorVerificar, long CreditoCentavos, int Abonos, long AbonosTarjetaCentavos, long AbonosTransferenciaCentavos,
    bool PuedeCortar, IReadOnlyList<QuienAutoriza> QuienesCortan, bool VeElDia, bool EnContingencia = false);

/// <summary>
/// El corte del día (entrega 6, 2026-10-14), para quien tiene «ver y cerrar el corte del día»: todas las cajas y turnos de
/// la sucursal. Los turnos del día son los que se abrieron, se cerraron o siguen abiertos ese día; lo demás (lo vendido,
/// las formas de pago, las salidas, las devoluciones, lo fiado…) es lo de esos turnos que pasó ese día: de un turno que
/// viene de otro día, solo lo de hoy.
/// </summary>
/// <param name="EfectivoCentavos">Lo que se cobró en efectivo en las ventas (con su redondeo).</param>
/// <param name="FiadoCentavos">Lo que se vendió a crédito: ya cuenta en lo vendido y no es dinero.</param>
/// <param name="TransferenciasPorVerificar">De las ventas y los abonos de esos turnos.</param>
/// <param name="EntradasCentavos">Las entradas de efectivo (F7), que no son venta.</param>
/// <param name="AbonosCentavos">Lo que entró por abonos de clientes (no son venta), en todas las formas.</param>
/// <param name="ImpresionesCentavos">Lo que se cobró de impresiones en las ventas.</param>
/// <param name="PorQueNoSeCierra">Lo que falta para cerrar el día; vacío: se puede cerrar.</param>
/// <param name="Avisos">Lo que conviene saber y no detiene el cierre.</param>
/// <param name="Piloto">El piloto con eleventa: el corte del día se hace allá, aquí solo se ve.</param>
/// <param name="Cierre">Si ya se cerró después de su último turno.</param>
public sealed record CorteDelDiaDto(
    DateOnly Fecha, IReadOnlyList<TurnoDelDiaDto> Turnos, long VendidoCentavos, int Tickets, long EfectivoCentavos, long TarjetaCentavos,
    long TransferenciaCentavos, long FiadoCentavos, int TransferenciasPorVerificar, IReadOnlyList<SalidaDelDiaDto> Salidas, long EntradasCentavos,
    int Devoluciones, long DevolucionesCentavos, int Cancelaciones, long CancelacionesCentavos, int Abonos, long AbonosCentavos,
    long ImpresionesCentavos, IReadOnlyList<string> PorQueNoSeCierra, IReadOnlyList<string> Avisos, bool Piloto, CierreDelDiaDto? Cierre);

/// <param name="Tickets">Los de ese día (de un turno que viene de otro día, solo los de hoy), como lo vendido y las formas de pago.</param>
/// <param name="EsperadoCentavos">El efectivo que debería haber en todo el turno: el de su corte o, si sigue abierto, el de ahora. Vacío en el piloto.</param>
/// <param name="ContadoCentavos">Vacío mientras el turno sigue abierto.</param>
public sealed record TurnoDelDiaDto(
    Guid Id, Guid CajaId, string Caja, string Cajero, DateTime AbiertoLocal, DateTime? CerradoLocal, int Tickets, long VendidoCentavos,
    long TarjetaCentavos, long TransferenciaCentavos, long? EsperadoCentavos, long? ContadoCentavos, long? DiferenciaCentavos, bool EnContingencia);

/// <summary>Las salidas de efectivo del día por su motivo (hoy el motivo se escribe libre: las que dicen lo mismo van juntas).</summary>
public sealed record SalidaDelDiaDto(string Motivo, int Veces, long MontoCentavos);

/// <summary>Qué pasó con el resumen del día al cerrarlo.</summary>
public enum EnvioResumen
{
    /// <summary>Quedó en la cola de Telegram (sale en cuanto haya internet).</summary>
    Enviado,

    /// <summary>Ya había salido con el último corte del día: no se manda dos veces.</summary>
    YaHabiaSalido,

    /// <summary>No hay un chat del dueño vinculado.</summary>
    SinTelegram,

    /// <summary>El resumen del día está apagado en los avisos.</summary>
    Apagado,
}

/// <param name="Resumen">Solo en la respuesta al cerrarlo.</param>
public sealed record CierreDelDiaDto(DateTime CerradoLocal, string CerradoPor, EnvioResumen? Resumen = null);

public sealed record SolicitudCierreDia(Guid UsuarioId);

/// <param name="Codigo">"requiere-autorizacion" o "no-permitido" cuando lo detiene una regla.</param>
public sealed record ErrorDto(string Mensaje, string? Codigo = null);

/// <param name="LogoPngBase64">Cómo se ve el logo convertido, para la pantalla.</param>
public sealed record ConfiguracionTicketDto(IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo, string? LogoPngBase64);

/// <summary>
/// El encabezado y el pie del ticket (la Configuración nueva, entrega 7): los cambia quien entró a la caja con «Configurar
/// el ticket», sin volver a escribir su PIN.
/// </summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudTextosTicket(Guid UsuarioId, IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie);

/// <param name="UsuarioId">Quien entró a la caja, con «Configurar el ticket».</param>
/// <param name="ImagenBase64">El archivo tal cual (PNG, JPG o BMP); el servicio lo pasa a blanco y negro. Vacío: quita el logo.</param>
public sealed record SolicitudLogo(Guid UsuarioId, string? ImagenBase64);

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

/// <summary>
/// La configuración de la tienda (la Configuración nueva, entrega 7: Tienda, Venta y cobro y Efectivo y corte). La cambia
/// quien tiene «Configuración de la tienda»; la leen todas las cajas: el nombre en la entrada, las fichas de la espera en
/// la venta, el fondo que propone «Abrir turno» y el límite de efectivo.
/// </summary>
/// <param name="NombreTienda">Vacío: la entrada dice el de la sucursal.</param>
/// <param name="HoraApertura">El horario de la tienda: fuera de él, abrir un turno avisa y de noche no se avisa de la impresora.</param>
/// <param name="LimiteEfectivoCentavos">Al llegar, la caja pide un retiro. Vacío: sin límite.</param>
/// <param name="FondoSugeridoCentavos">Lo que propone «Abrir turno». Vacío: el fondo se escribe cada vez.</param>
/// <param name="UmbralCorteCentavos">Una diferencia mayor en el corte de un turno avisa al dueño.</param>
/// <param name="FichasEnEspera">Los tickets en espera que se ven como ficha; los demás, en «Más». Una caja principal de antes no lo manda: 3.</param>
public sealed record ConfiguracionSucursalDto(
    string? NombreTienda, string? Direccion, string? Telefono, TimeOnly HoraApertura, TimeOnly HoraCierre, long? LimiteEfectivoCentavos,
    long? FondoSugeridoCentavos, long UmbralCorteCentavos, int FichasEnEspera = ConfiguracionSucursal.FichasEnEsperaPorOmision);

/// <summary>
/// Un cambio a la configuración de la tienda: lo hace quien entró a la caja con «Configuración de la tienda», sin volver a
/// escribir su PIN, y en la bitácora queda solo lo que cambió.
/// </summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
/// <param name="Configuracion">Como debe quedar.</param>
/// <param name="Antes">
/// Como la tenía la pantalla al cambiarla: se toma solo lo que es distinto entre las dos, y lo demás se queda como esté en
/// el servicio (lo pudo cambiar otra caja, o el horario en Telegram, mientras tanto). Vacío: todo como dice
/// <paramref name="Configuracion"/>.
/// </param>
public sealed record SolicitudConfiguracionSucursal(Guid UsuarioId, ConfiguracionSucursalDto Configuracion, ConfiguracionSucursalDto? Antes = null);

/// <summary>Los rápidos de la venta en su orden (Alt+1 a Alt+6), como deben quedar. Los cambia quien tiene «Configuración de la tienda».</summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudRapidos(Guid UsuarioId, IReadOnlyList<Rapido> Rapidos);

/// <summary>El comprobante que se imprime al sacar efectivo de la caja.</summary>
public sealed record ComprobanteMovimientoDto(
    string Folio, TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, DateTime FechaLocal, string Caja, string Usuario, IReadOnlyList<string> Encabezado);

public sealed record SolicitudReimpresion(Guid UsuarioId, Guid CajaId, string? Motivo);

/// <summary>De antes de la entrega 1: la regla de un rol, como la copia de la caja se la lleva a una caja que no se ha actualizado.</summary>
public sealed record ReglaDto(AccionControlada Accion, Rol Rol, NivelPermiso Nivel, long? UmbralCentavos, bool PideMotivo);

// ── Cancelar, devolver y verificar (entrega 2, parte 2) ─────────────────────

public sealed record LineaDevolucionDto(Guid LineaId, string Descripcion, long CantidadMilesimas, long CantidadDevueltaMilesimas, long ImporteCentavos, bool LlevaInventario);

/// <param name="PuedeCancelar">
/// Si su turno sigue abierto (después, solo devolución). En el piloto con eleventa, además, si se cobró hoy: el turno de
/// esa caja no se cierra ahí (su corte se hace en eleventa).
/// </param>
/// <param name="NoSeDevuelveEnElPiloto">
/// El piloto con eleventa (2026-10-12): por qué este ticket no se cancela ni se devuelve en esta caja (no se cobró todo
/// en efectivo); vacío si sí, o fuera del piloto. Solo para convivir con eleventa: se quita al dejarlo.
/// </param>
public sealed record VentaParaDevolverDto(
    Guid Id, string Folio, DateTime FechaLocal, EstadoVenta Estado, long TotalCentavos, IReadOnlyList<LineaDevolucionDto> Lineas,
    bool PuedeCancelar, int DiasParaDevolver, long CreditoCentavos = 0, string? Cliente = null, string? NoSeDevuelveEnElPiloto = null);

public sealed record SolicitudCancelacion(Guid UsuarioId, Guid CajaId, string Motivo, Guid? AutorizacionId = null);

public sealed record LineaDevolucionSolicitada(Guid LineaId, long CantidadMilesimas, bool RegresaAlInventario = true);

public sealed record SolicitudDevolucion(Guid UsuarioId, Guid CajaId, string Motivo, IReadOnlyList<LineaDevolucionSolicitada> Lineas, Guid? AutorizacionId = null);

/// <summary>Comprobante que se imprime al reembolsar.</summary>
/// <param name="RedondeoCentavos">El del efectivo al peso: lo que sale en efectivo de lo devuelto (o, al cancelar, el de la venta).</param>
/// <param name="CobradoSinEfectivoCentavos">En una cancelación, lo que se pagó con tarjeta o transferencia: se regresa por ese medio.</param>
public sealed record ComprobanteDevolucionDto(
    string Folio, bool EsCancelacion, long TotalCentavos, long ReembolsoEfectivoCentavos, string Motivo, DateTime FechaLocal, string Caja,
    string Usuario, string? Autorizo, IReadOnlyList<LineaTicketDto> Lineas, IReadOnlyList<string> Encabezado, long RedondeoCentavos = 0,
    long CobradoSinEfectivoCentavos = 0, long AlCreditoCentavos = 0, string? Cliente = null, long? SaldoClienteCentavos = null,
    long TarjetaCentavos = 0, long TransferenciaCentavos = 0);

/// <param name="CobroId">El cobro de la venta, o el abono si <paramref name="EsAbono"/>.</param>
public sealed record TransferenciaDto(
    Guid CobroId, string Folio, DateTime FechaLocal, long MontoCentavos, string? Referencia, string Cajero, Verificacion Verificacion,
    bool EsAbono = false, string? Cliente = null);

public sealed record SolicitudVerificacion(string Usuario, string Pin, bool Llego);

public sealed record BitacoraDto(DateTime FechaLocal, string? Usuario, AccionControlada Accion, string Detalle, long? MontoCentavos, string? Autorizo, ResultadoBitacora Resultado);

// ── Varias cajas (entrega 3) ────────────────────────────────────────────────

/// <summary>Lo que contesta el servicio sin llave: para encontrarlo en la red y saber que está vivo.</summary>
/// <param name="VersionDatos">
/// Cambia con lo que lleva la copia de las cajas (el catálogo, los usuarios y sus permisos, la configuración…): la caja pide
/// lo que cambió (o, una de antes, vuelve a bajar su copia). Un PIN mal tecleado o abrir un turno ya no la cambian.
/// </param>
/// <param name="Prueba">El servicio es el de la prueba con una persona real: datos ficticios, guía y avisos simulados.</param>
/// <param name="VersionCredito">Cambia con cada fiado, abono o cambio de crédito: la caja vuelve a pedir los saldos.</param>
/// <param name="PilotoEleventa">
/// El piloto con eleventa: el servicio manda sus ventas a eleventa (<c>Eleventa:Escribir</c>), la caja solo cobra y
/// devuelve en efectivo y su corte se hace en eleventa (2026-10-12). Solo para convivir con eleventa: se quita al dejarlo.
/// </param>
/// <param name="AvisoBase">
/// La base contra apagones (entrega 10): el servicio se apagó de golpe y la base no pasó la revisión al arrancar. La caja
/// lo muestra en la franja hasta que se restaure el respaldo. Vacío si todo está bien.
/// </param>
/// <param name="VersionExistencias">
/// Cambia con cada cambio de existencia (una venta, una compra): la caja pide las que cambiaron desde la suya (entrega 5,
/// parte 2). Vacía en un servicio de antes: entonces la existencia de la copia se pone al día al bajarla completa.
/// </param>
/// <param name="Base">
/// De qué base son las versiones (cambia al arrancar el servicio y al empezar de cero la prueba): con otra, los cambios
/// desde la versión de la caja no sirven y se baja la copia completa. Vacía en un servicio de antes.
/// </param>
public sealed record EstadoServicioDto(
    string Servicio, string Version, IReadOnlyList<string> Sucursales, long VersionDatos = 0, bool Prueba = false, long VersionCredito = 0,
    bool PilotoEleventa = false, string? AvisoBase = null, long? VersionExistencias = null, Guid? Base = null);

/// <summary>Usuario y PIN de quien autoriza: dar de alta una caja (antes de que haya sesión) y Telegram, que sigue como estaba.</summary>
public sealed record SolicitudCredenciales(string Usuario, string Pin);

/// <summary>
/// Quitar la PC de una caja (robada, reemplazada o que ya no se usa): lo hace quien entró a la caja con «Dar de alta y
/// quitar cajas», sin volver a escribir su PIN (la Configuración nueva, entrega 7).
/// </summary>
/// <param name="UsuarioId">Quien entró a la caja.</param>
public sealed record SolicitudQuitarEquipo(Guid UsuarioId);

public sealed record CajaAltaDto(Guid Id, string Nombre, string Serie, string? Equipo);

public sealed record SucursalAltaDto(Guid Id, string Nombre, IReadOnlyList<CajaAltaDto> Cajas);

/// <summary>Sin <paramref name="CajaId"/>, se crea una caja nueva con <paramref name="Nombre"/> y <paramref name="Serie"/>.</summary>
public sealed record SolicitudAltaCaja(string Usuario, string Pin, Guid SucursalId, Guid? CajaId, string? Nombre, string? Serie, string Equipo);

/// <param name="Llave">Solo la ve el equipo: la guarda y la manda en cada llamada (encabezado X-Caja-Llave).</param>
public sealed record AltaCajaDto(CajaDto Caja, string Llave);

// ── Contingencia (entrega 3, parte 2) ───────────────────────────────────────

/// <summary>
/// Un usuario en la copia local de la caja: el PIN va como su hash (PBKDF2), nunca en claro. Los dados de baja también
/// vienen (sin el hash), para que sus letras no se le den a otro; no entran.
/// </summary>
/// <param name="Rol">De antes de la entrega 1: solo para una caja que no se ha actualizado (la de ahora usa los permisos).</param>
public sealed record UsuarioLocalDto(Guid Id, string Usuario, string Nombre, Rol Rol, string PinHash, bool Activo);

/// <summary>Los permisos de un usuario en la copia de la caja; sin usuario, los de defecto.</summary>
public sealed record PermisosLocalesDto(Guid? UsuarioId, IReadOnlyList<Permiso> Permisos);

/// <summary>Cada código de barras y la presentación que vende (la caja de 12, la pieza…).</summary>
public sealed record CodigoLocalDto(string Codigo, Guid VarianteId, Guid PresentacionId);

/// <summary>Lo que la caja guarda para vender sin la caja principal.</summary>
/// <param name="Version">La <see cref="EstadoServicioDto.VersionDatos"/> con que se armó.</param>
/// <param name="UltimoFolio">El último folio de la serie de la caja que conoce el servicio.</param>
/// <param name="Clientes">Para las cajas de antes (CTRL+D): solo los activos con descuento, que es lo único que saben usar.</param>
/// <param name="Registrados">Todos los clientes registrados activos, con sus privilegios.</param>
/// <param name="Reglas">De antes de la entrega 1: solo para una caja que no se ha actualizado.</param>
/// <param name="Permisos">
/// Los permisos de cada usuario activo y los de defecto (entrega 1). Vacío en una copia de antes: la caja decide con las
/// reglas como lo dejaría la migración, y la vuelve a bajar en el siguiente latido.
/// </param>
/// <param name="Configuracion">
/// La configuración de la tienda (entrega 7): sin conexión, la entrada dice el nombre de la tienda, la venta enseña sus
/// fichas de espera y «Abrir turno» propone el fondo. Vacía en una copia de antes: lo de siempre (3 fichas, sin fondo
/// sugerido).
/// </param>
/// <param name="Base">
/// De qué base es la copia (<see cref="EstadoServicioDto.Base"/>), y con <paramref name="Formato"/> y
/// <paramref name="VersionExistencias"/>, desde dónde pedir lo que cambió (entrega 5, parte 2). Vacíos en un servicio de antes:
/// esa copia solo se renueva entera.
/// </param>
/// <param name="Formato">Cómo está armada la copia: si la caja o el servicio cambian de formato, se baja completa.</param>
/// <param name="VersionExistencias">La versión de las existencias con que se armó.</param>
public sealed record PaqueteContingenciaDto(
    long Version, CajaDto Caja, IReadOnlyList<ProductoVentaDto> Productos, IReadOnlyList<CodigoLocalDto> Codigos, IReadOnlyList<UsuarioLocalDto> Usuarios,
    IReadOnlyList<ReglaDto> Reglas,
    IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo, int UltimoFolio, long? LimiteEfectivoCentavos,
    IReadOnlyList<ClienteRegistradoDto>? Clientes = null, IReadOnlyList<ClienteRegistradoDto>? Registrados = null,
    IReadOnlyList<string>? Marcas = null, IReadOnlyList<SinonimoDto>? Sinonimos = null, IReadOnlyList<PermisosLocalesDto>? Permisos = null,
    ConfiguracionSucursalDto? Configuracion = null, Guid? Base = null, int? Formato = null, long? VersionExistencias = null);

// Lo que sube con la bandeja. Cada cosa lleva el ID que le dio la caja: si llega dos veces, se guarda una.

public sealed record TurnoContingenciaDto(Guid Id, Guid CajaId, Guid CajeroId, long FondoCentavos, DateTime AbiertoUtc);

/// <param name="PrecioCentavos">El que se cobró (por presentación, con IVA): manda sobre el del catálogo.</param>
public sealed record LineaContingenciaDto(
    Guid? VarianteId, Guid? PresentacionId, string Descripcion, long CantidadMilesimas, long PrecioCentavos, int? DescuentoCentesimas = null,
    long? DescuentoCentavos = null, string? MotivoDescuento = null);

/// <param name="RedondeoCentavos">El que cobró la caja. Vacío en lo que se cobró antes de que existiera el redondeo: eso se vuelve a cobrar exacto.</param>
/// <param name="SaldoConocidoCentavos">Si fió: lo que la caja creía que debía (con eso imprimió el ticket).</param>
/// <param name="ExcesoAutorizadoCentavos">Si fió: lo que pasó del límite con permiso en la caja.</param>
/// <param name="VenceCredito">Si fió: el vencimiento que se imprimió.</param>
public sealed record VentaContingenciaDto(
    Guid Id, Guid CajaId, Guid TurnoId, Guid CajeroId, int Folio, DateTime PagadaUtc, IReadOnlyList<LineaContingenciaDto> Lineas,
    IReadOnlyList<CobroSolicitado> Cobros, [property: JsonPropertyName("clienteEspecialId")] Guid? ClienteId = null, long? RedondeoCentavos = null,
    bool? MayoreoDeCliente = null, long? SaldoConocidoCentavos = null, long ExcesoAutorizadoCentavos = 0, DateOnly? VenceCredito = null);

public sealed record MovimientoContingenciaDto(Guid Id, TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo, Guid UsuarioId, DateTime FechaUtc);

public sealed record BitacoraContingenciaDto(
    Guid Id, Guid SucursalId, Guid CajaId, Guid? UsuarioId, AccionControlada Accion, string Detalle, long? MontoCentavos, Guid? AutorizoId,
    ResultadoBitacora Resultado, DateTime FechaUtc, Guid? VentaId = null);

/// <summary>El corte del turno que hizo la caja sin conexión.</summary>
/// <param name="RealizadoPorId">Quien lo hizo («encargadoId» desde antes del 2026-10-14).</param>
/// <param name="Conteo">Billete por billete: lo que manda una caja de antes del 2026-10-14. Las de ahora mandan <paramref name="ContadoCentavos"/>.</param>
/// <param name="ContadoCentavos">El total que se contó.</param>
public sealed record CorteContingenciaDto(
    Guid Id, [property: JsonPropertyName("encargadoId")] Guid RealizadoPorId, IReadOnlyDictionary<long, int>? Conteo, DateTime FechaUtc,
    long? ContadoCentavos = null);

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
/// <param name="SaldoCentavos">Lo que debe (negativo: lo que tiene a favor).</param>
/// <param name="Puntualidad">Qué tan puntual paga lo que se le fía.</param>
public sealed record ClienteResumenDto(
    ClienteRegistradoDto Cliente, long PromedioMensualCentavos, DateTime? UltimaCompraLocal, long SaldoCentavos = 0, long VencidoCentavos = 0,
    PuntualidadDto? Puntualidad = null);

/// <summary>Lo que compró en un mes (sin lo cancelado ni lo devuelto).</summary>
public sealed record CompraMesDto(int Anio, int Mes, int Tickets, long TotalCentavos);

/// <summary>La ficha del cliente con su evaluación: cuánto compra por mes y qué tan puntual paga.</summary>
public sealed record FichaClienteDto(
    ClienteRegistradoDto Cliente, IReadOnlyList<CompraMesDto> Meses, long PromedioMensualCentavos, DateTime? UltimaCompraLocal,
    PuntualidadDto? Puntualidad = null);

/// <summary>
/// Qué tan puntual paga lo que se le fía (parte 3): de sus compras a crédito recientes, cuántas liquidó a tiempo (a más
/// tardar el día que vencían) y en cuántos días; y cuántas ya vencieron sin pagarse. Sale de <see cref="CuentaCredito.Puntualidad"/>.
/// </summary>
/// <param name="Compras">Las compras a crédito que cuentan: las recientes y las que todavía debe (sin las que devolvió completas).</param>
/// <param name="Liquidadas">De esas, las que ya pagó.</param>
/// <param name="ATiempo">De las liquidadas, las que pagó a más tardar el día que vencían.</param>
/// <param name="DiasPromedio">Los días que tarda en liquidar una compra; sin liquidadas, nada.</param>
/// <param name="Vencidas">Las que ya vencieron y todavía debe.</param>
public sealed record PuntualidadDto(int Compras, int Liquidadas, int ATiempo, int? DiasPromedio, int Vencidas)
{
    public bool SinDatos => Compras == 0;

    /// <summary>Las que ya se pueden calificar: las que liquidó y las que se le vencieron sin pagar.</summary>
    public int Calificadas => Liquidadas + Vencidas;

    /// <summary>"Liquida en 9 días, en promedio · 7 de 9 a tiempo · 1 compra vencida sin pagar".</summary>
    public string Texto()
    {
        if (SinDatos)
            return $"Sin compras a crédito en los últimos {CuentaCredito.MesesDePuntualidad} meses.";
        var partes = new List<string>();
        if (Liquidadas > 0)
            partes.Add(DiasPromedio switch
            {
                0 => "Liquida el mismo día, en promedio",
                1 => "Liquida en 1 día, en promedio",
                var dias => $"Liquida en {dias} días, en promedio",
            });
        if (Calificadas > 0)
            partes.Add($"{ATiempo} de {Calificadas} a tiempo");
        if (Vencidas > 0)
            partes.Add(Vencidas == 1 ? "1 compra vencida sin pagar" : $"{Vencidas} compras vencidas sin pagar");
        return partes.Count > 0 ? string.Join(" · ", partes) : "Todavía no liquida sus compras a crédito; ninguna ha vencido.";
    }

    /// <summary>Para la lista: "7 de 9" (las vencidas sin pagar cuentan como tarde) o "—".</summary>
    public string Corto() => Calificadas > 0 ? $"{ATiempo} de {Calificadas}" : "—";
}

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
/// <param name="DeEleventa">
/// En el piloto con eleventa (con su lectura prendida), un precio que pone la lectura: el de menudeo o una promoción por
/// cantidad de eleventa. No se cambia aquí. Lo dice el servicio al leer la ficha; al guardar, no lo lee.
/// </param>
public sealed record PrecioFichaDto(string? Presentacion, long CantidadMinimaMilesimas, long PrecioCentavos, bool DeEleventa = false);

public sealed record ProveedorFichaDto(Guid ProveedorId, string Proveedor, string? CodigoProveedor, long? UltimoCostoCentavos, DateTime? UltimaCompraLocal);

/// <param name="Existencia">En la sucursal, en unidades base (solo lectura).</param>
/// <param name="CostoPromedioCentavos">Solo para el dueño y el encargado (solo lectura).</param>
/// <param name="DeEleventa">
/// En el piloto con eleventa (con su lectura prendida), una variante que viene de eleventa: si está activa, los códigos de su
/// presentación base (se pueden agregar otros) y los precios de eleventa no se cambian aquí, ni se depura. Lo dice el
/// servicio al leer la ficha; al guardar, no lo lee.
/// </param>
public sealed record VarianteFichaDto(
    Guid? Id, string Descripcion, UnidadBase Unidad, bool Activa, IReadOnlyList<PresentacionFichaDto> Presentaciones, IReadOnlyList<PrecioFichaDto> Precios,
    decimal? Existencia = null, decimal? CostoPromedioCentavos = null, IReadOnlyList<ProveedorFichaDto>? Proveedores = null, bool DeEleventa = false);

/// <summary>La ficha de un producto en F3: lo que se edita junto (el producto, sus variantes, presentaciones, códigos y precios).</summary>
/// <param name="Marca">Por nombre: si no existe, se da de alta.</param>
/// <param name="EnEleventa">
/// En el piloto con eleventa (con su lectura prendida), si algo de este producto viene de eleventa: por qué eso no se
/// cambia aquí (lo de cada variante y cada precio lo dicen sus <c>DeEleventa</c>). Vacío: todo se cambia aquí.
/// </param>
/// <param name="ProductoDeEleventa">
/// En el piloto, el nombre, el departamento y si se vende los pone la lectura: es de eleventa y de una sola variante.
/// </param>
public sealed record FichaProductoDto(
    Guid? Id, string Nombre, Guid DepartamentoId, string? Marca, TipoProducto Tipo, Impuesto Impuesto, string? ClaveSat, bool Activo, bool PorCompletar,
    IReadOnlyList<VarianteFichaDto> Variantes, string? EnEleventa = null, bool ProductoDeEleventa = false);

public sealed record SolicitudGuardarProducto(Guid UsuarioId, Guid SucursalId, FichaProductoDto Producto);

public sealed record OpcionCatalogoDto(Guid Id, string Nombre);

/// <param name="Departamentos">Los activos: los únicos que se ofrecen (al filtrar la lista, en la ficha, en el alta rápida y en las compras).</param>
/// <param name="Desactivados">
/// Los desactivados (Productos › Departamentos), solo para decir el de un producto que se quedó en uno: no se ofrecen.
/// Vacío en un servicio de antes.
/// </param>
public sealed record OpcionesCatalogoDto(
    IReadOnlyList<OpcionCatalogoDto> Departamentos, IReadOnlyList<string> Marcas, IReadOnlyList<OpcionCatalogoDto>? Desactivados = null);

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
/// <param name="Version">
/// La de la guía que se contestó: con otra versión, el mismo Id puede ser otra tarea. Las de la primera prueba
/// (2026-10-10) se guardaron sin ella: son la 1.
/// </param>
public sealed record RespuestasPruebaDto(string? Nombre, IReadOnlyDictionary<string, RespuestaTareaDto> Tareas, string? Comentarios, int Version = 1);

/// <summary>Las respuestas ya escritas como documento, para guardarlas en un archivo.</summary>
public sealed record SolicitudExportarRespuestas(string Texto);

/// <summary>Para la siguiente persona: las respuestas de esa versión de la guía se guardan como documento y la guía queda en blanco.</summary>
public sealed record SolicitudGuiaEnBlanco(int Version, string Texto);

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

// ── Crédito y abonos (fase 3, entrega 2, parte 2) ────────────────────────────

/// <summary>Lo que falta pagar de un cargo y cuándo vence.</summary>
public sealed record CargoPendienteDto(DateOnly Vence, long PendienteCentavos);

/// <summary>La cuenta de un cliente: lo que debe, lo que tiene por verificar, sus condiciones y lo pendiente por cargo.</summary>
/// <param name="SaldoCentavos">Lo que debe; negativo, lo que tiene a favor.</param>
/// <param name="PorVerificarCentavos">Abonos por transferencia sin verificar: ya bajaron el saldo, pero no dan crédito.</param>
/// <param name="Estimado">Sin conexión: el saldo que conoce la caja.</param>
public sealed record CreditoClienteDto(
    Guid ClienteId, long SaldoCentavos, long PorVerificarCentavos, long LimiteCentavos, int PlazoDias, IReadOnlyList<CargoPendienteDto> Pendientes,
    bool Estimado = false)
{
    public long AFavorCentavos => Math.Max(0, -SaldoCentavos);

    /// <summary>Lo que tiene a favor sin lo que viene de transferencias por verificar: lo que se le puede devolver en efectivo.</summary>
    public long DevolvibleCentavos => Math.Max(0, AFavorCentavos - PorVerificarCentavos);

    public long DisponibleCentavos => Math.Max(0, LimiteCentavos - SaldoCentavos - PorVerificarCentavos);
    public long Vencido(DateOnly hoy) => Pendientes.Where(p => p.Vence < hoy).Sum(p => p.PendienteCentavos);
    public DateOnly? VenceMasAntiguo => Pendientes.Min(p => (DateOnly?)p.Vence);
    public long Exceso(long creditoCentavos) => CuentaCredito.Exceso(SaldoCentavos, PorVerificarCentavos, creditoCentavos, LimiteCentavos);
}

/// <summary>Lo que el ticket dice de la cuenta: lo fiado, el saldo con eso y hasta cuándo se paga.</summary>
public sealed record CreditoTicketDto(long FiadoCentavos, long SaldoCentavos, DateOnly? Vence, bool Estimado = false);

/// <summary>Quien puede dar crédito pone el límite (cero, sin crédito) y el plazo.</summary>
public sealed record SolicitudCreditoCliente(Guid UsuarioId, Guid SucursalId, long LimiteCentavos, int PlazoDias);

/// <summary>Un ajuste del saldo (positivo: debe más). Con el mismo Id, se guarda una vez.</summary>
/// <param name="Desde">Para el saldo inicial de la libreta: la fecha de lo que debía (de ahí cuenta el plazo).</param>
public sealed record SolicitudAjusteCredito(Guid Id, Guid UsuarioId, Guid SucursalId, long MontoCentavos, string Motivo, DateOnly? Desde = null);

/// <summary>Un abono. El Id lo pone la caja: si llega dos veces, se guarda una.</summary>
/// <param name="MontoCentavos">Vacío: liquidar.</param>
/// <param name="RecibidoCentavos">En efectivo, lo que entregó el cliente.</param>
/// <param name="SaldoVistoCentavos">El saldo que vio la caja: si cambió mientras tanto, no se guarda (lo cobrado sería otro).</param>
public sealed record SolicitudAbono(
    Guid Id, Guid CajaId, Guid TurnoId, Guid UsuarioId, Guid ClienteId, FormaPago Forma, long? MontoCentavos, long RecibidoCentavos = 0,
    string? Referencia = null, long? SaldoVistoCentavos = null);

/// <summary>El comprobante de un abono (o de su cancelación), o de un saldo a favor que se le devolvió.</summary>
/// <param name="SaldoCentavos">Lo que quedó a deber con el abono (negativo: lo que le queda a favor).</param>
/// <param name="SaldoTrasCancelarCentavos">Si se canceló, lo que quedó a deber al cancelarlo.</param>
/// <param name="Tipo">Un saldo devuelto: el monto sube la cuenta y lo cobrado es lo que se le entregó.</param>
public sealed record ComprobanteAbonoDto(
    Guid AbonoId, string Folio, DateTime FechaLocal, string Caja, string Usuario, Guid ClienteId, string Cliente, FormaPago Forma, long MontoCentavos,
    long RedondeoCentavos, long RecibidoCentavos, long CambioCentavos, string? Referencia, long SaldoAnteriorCentavos, long SaldoCentavos,
    DateOnly? Vence, IReadOnlyList<string> Encabezado, bool Cancelado = false, bool Estimado = false, string? MotivoCancelacion = null,
    long? SaldoTrasCancelarCentavos = null, TipoAbono Tipo = TipoAbono.Abono)
{
    public bool EsSaldoDevuelto => Tipo == TipoAbono.SaldoDevuelto;

    public long CobradoCentavos => MontoCentavos + RedondeoCentavos;
    public bool AbreCajon => Forma == FormaPago.Efectivo;
}

/// <param name="Tipo">Un saldo a favor que se devolvió en efectivo: se reimprime, pero no se cancela.</param>
public sealed record AbonoDelDiaDto(
    Guid AbonoId, string Folio, DateTime FechaLocal, string Cliente, FormaPago Forma, long MontoCentavos, string Usuario, bool Cancelado, bool DeEsteTurno,
    TipoAbono Tipo = TipoAbono.Abono);

/// <summary>Un renglón de la cuenta, con el saldo después de él.</summary>
public sealed record MovimientoCreditoDto(
    Guid Id, DateTime FechaLocal, TipoMovimientoCredito Tipo, string Concepto, string? Folio, long MontoCentavos, long SaldoCentavos, DateOnly? Vence,
    bool EnContingencia);

/// <summary>La cuenta con sus últimos movimientos (del más reciente al más viejo).</summary>
public sealed record CuentaClienteDto(CreditoClienteDto Credito, IReadOnlyList<MovimientoCreditoDto> Movimientos);

// ── Estado de cuenta y saldo a favor (fase 3, entrega 2, parte 3) ───────────

/// <summary>Devolverle en efectivo lo que tiene a favor. El Id lo pone la caja: si llega dos veces, se guarda una.</summary>
/// <param name="MontoCentavos">Vacío: todo (al peso).</param>
/// <param name="SaldoVistoCentavos">El saldo que vio la caja: si cambió mientras tanto, no se guarda.</param>
/// <param name="AutorizacionId">La del encargado, si la regla la pide.</param>
public sealed record SolicitudDevolverSaldo(
    Guid Id, Guid CajaId, Guid TurnoId, Guid UsuarioId, long? MontoCentavos, long? SaldoVistoCentavos = null, Guid? AutorizacionId = null);

/// <summary>Lo que falta pagar de una compra (o de un ajuste) en el estado de cuenta, con su vencimiento.</summary>
public sealed record PendienteEstadoDto(DateOnly Fecha, string Concepto, string? Folio, long MontoCentavos, long PendienteCentavos, DateOnly Vence);

/// <summary>
/// El estado de cuenta de un cliente (parte 3), para imprimirlo o mandarlo como texto: lo que debía al empezar, cada
/// compra a crédito y cada abono desde esa fecha con el saldo que dejó, lo que debe y lo que falta pagar de cada compra.
/// </summary>
/// <param name="Desde">Por omisión, desde la compra más vieja que sigue sin pagarse (y al menos los últimos 30 días).</param>
/// <param name="SaldoAnteriorCentavos">Lo que debía antes de <paramref name="Desde"/> (negativo: a favor).</param>
/// <param name="Movimientos">Del más viejo al más reciente.</param>
public sealed record EstadoCuentaDto(
    Guid ClienteId, string Cliente, string? Telefono, DateTime FechaLocal, DateOnly Desde, long SaldoAnteriorCentavos,
    IReadOnlyList<MovimientoCreditoDto> Movimientos, long SaldoCentavos, long VencidoCentavos, long PorVerificarCentavos,
    IReadOnlyList<PendienteEstadoDto> Pendientes, long LimiteCentavos, int PlazoDias, IReadOnlyList<string> Encabezado);

// ── Crédito sin conexión (fase 3, entrega 2, parte 2.4) ─────────────────────

/// <summary>La caja pide los saldos y pregunta cuáles de los suyos (fiados y abonos sin confirmar) ya están en ellos.</summary>
public sealed record SolicitudSaldosCredito(IReadOnlyList<Guid> Pendientes);

/// <summary>
/// Los saldos de la sucursal en una sola foto: los que tienen crédito, deben o tienen algo por verificar (los demás, en
/// cero). <paramref name="Incluidos"/> son los de la caja que ya están sumados: esos los borra de su cuenta propia.
/// </summary>
public sealed record SaldosCreditoDto(long Version, IReadOnlyList<CreditoClienteDto> Cuentas, IReadOnlyList<Guid> Incluidos);

/// <summary>Un abono hecho sin conexión: el servicio lo guarda tal cual (ya se cobró) y revisa lo que no cuadre.</summary>
/// <param name="SaldoConocidoCentavos">Lo que la caja creía que debía (con eso imprimió el comprobante).</param>
public sealed record AbonoContingenciaDto(
    Guid Id, Guid CajaId, Guid TurnoId, Guid UsuarioId, Guid ClienteId, FormaPago Forma, long MontoCentavos, long RedondeoCentavos,
    long RecibidoCentavos, long CambioCentavos, string? Referencia, DateTime FechaUtc, long SaldoConocidoCentavos, DateOnly? Vence = null);

/// <summary>Lo de crédito que no cuadró al volver la conexión, para que el encargado o el dueño lo revisen.</summary>
public sealed record RevisionCreditoDto(
    Guid Id, Guid ClienteId, string Cliente, DateTime CreadaLocal, MotivoRevisionCredito Motivo, string Detalle, long MontoCentavos);

/// <summary>Quien revisó, con una nota (qué se hizo).</summary>
public sealed record SolicitudRevision(Guid UsuarioId, string? Nota);

/// <summary>La lectura de eleventa de la caja 2 en el piloto (docs/plan.md, paso 1).</summary>
/// <param name="Origen">De dónde lee (la PC, el puerto y la base; sin la clave).</param>
/// <param name="Bien">Si la última lectura salió bien; si no, <paramref name="Error"/> dice por qué y se vende con lo de antes.</param>
/// <param name="Resultado">Lo que cambió en la última lectura que salió bien.</param>
public sealed record EstadoEleventaDto(
    bool Activa, string? Origen, DateTime? UltimaLocal, bool Bien, string? Resultado, string? Error, DateTime? ProximaLocal, int Lecturas,
    IReadOnlyDictionary<string, int> Avisos);

/// <summary>La bandeja de salida hacia eleventa de la caja 2 en el piloto (docs/plan.md, paso 3).</summary>
/// <param name="Destino">A dónde y como qué caja de eleventa (sin la clave).</param>
/// <param name="Problema">Por qué no está mandando (falta configuración); vacío si todo está.</param>
/// <param name="UltimoError">El de la última venta que no entró, dicho para la tienda.</param>
/// <param name="PorAtender">Lo pendiente y lo que hay que revisar, de la más vieja a la más nueva.</param>
public sealed record EstadoEnviosEleventaDto(
    bool Activo, string? Destino, string? Problema, int Pendientes, int PorRevisar, int FoliosRepetidos, DateTime? UltimoEnvioLocal,
    string? UltimoError, IReadOnlyList<EnvioEleventaDto> PorAtender);

/// <param name="Descripcion">«Venta A-000123», «Devolución de A-000123», «Salida de efectivo: Garrafón de agua», «Cierre del turno».</param>
/// <param name="TotalCentavos">Lo cobrado, o lo que entró o salió de la caja.</param>
public sealed record EnvioEleventaDto(
    Guid Id, TipoEnvioEleventa Tipo, string Descripcion, DateTime FechaLocal, long TotalCentavos, EstadoEnvioEleventa Estado, int Intentos, string? Error,
    int? FolioEleventa);
