namespace PapeleriaPos.Nucleo.Contratos;

// La copia de la caja al día (entrega 5, parte 2, docs/propuesta-interfaz.md, «Rendimiento»): la caja principal avisa por
// SSE cuando sube una versión y la caja pide solo lo que cambió desde la suya.

/// <summary>
/// Las versiones de lo que lleva la copia de una caja, como las avisa la caja principal (<c>api/cajas/{id}/avisos</c>, un
/// evento «versiones» cada vez que cambia alguna).
/// </summary>
/// <param name="Base">De qué base son (cambia al arrancar el servicio y al empezar de cero la prueba).</param>
/// <param name="Catalogo">La del catálogo y lo demás que lleva la copia (<see cref="EstadoServicioDto.VersionDatos"/>).</param>
/// <param name="Existencias">La de las existencias: sube con cada venta.</param>
/// <param name="Credito">La de los saldos del crédito (<see cref="EstadoServicioDto.VersionCredito"/>).</param>
/// <param name="Formato">
/// El de la copia que arma la caja principal (<see cref="PaqueteContingenciaDto.Formato"/>). Si no es el de la caja (una de
/// las dos se actualizó antes que la otra), la caja no pide cambios y baja la copia completa solo cuando cambia la versión del
/// catálogo, o cada tanto, como con una caja principal de antes. Vacío en la de la entrega 5, parte 2, que armaba el 1.
/// </param>
public sealed record VersionesCopiaDto(Guid Base, long Catalogo, long Existencias, long Credito, int? Formato = null);

/// <summary>La existencia de un producto en la sucursal de la caja, ya en unidades.</summary>
public sealed record ExistenciaLocalDto(Guid VarianteId, decimal Cantidad);

/// <summary>El encabezado, el pie y el logo del ticket, como van en la copia.</summary>
public sealed record TicketLocalDto(IReadOnlyList<string> Encabezado, IReadOnlyList<string> Pie, LogoTicketDto? Logo);

/// <summary>
/// Lo que cambió en la copia de una caja desde su versión (<c>api/cajas/{id}/cambios</c>): los productos que cambiaron o
/// llegaron, completos y con su existencia; los que ya no van; las existencias que cambiaron; y, de lo demás que lleva la
/// copia, solo lo que cambió, completo (los usuarios con sus permisos, la caja, el ticket, la configuración, los clientes,
/// las marcas, los sinónimos, lo más vendido, los exhibidores y las temporadas) o solo lo nuevo (lo que esta caja eligió en
/// la búsqueda). Lo que no cambió viene vacío.
/// </summary>
/// <param name="PaqueteCompleto">
/// Vacío: estos son los cambios. Con texto, por qué no se pueden dar desde esa versión (otra base, otro formato, una versión
/// que el servicio no conoce o demasiados cambios): la caja baja la copia completa.
/// </param>
/// <param name="Version">La versión del catálogo con que queda la copia.</param>
/// <param name="VersionExistencias">La versión de las existencias con que queda.</param>
/// <param name="UltimoFolio">El último folio de la serie de la caja que conoce el servicio, como en la copia completa.</param>
/// <param name="Productos">Los que cambiaron o llegaron, como en la copia completa (con su existencia).</param>
/// <param name="Quitados">Los que la copia tenía y ya no van (dados de baja, juntados con otro).</param>
/// <param name="Codigos">Todos los códigos de <paramref name="Productos"/>: reemplazan los que la copia tenía de ellos.</param>
/// <param name="Existencias">Las que cambiaron desde la versión de existencias de la caja.</param>
/// <param name="Marcas">
/// Con las que el servicio lee los nombres (entrega 5, parte 3): vienen siempre que cambió algún producto, porque salen de
/// los productos (una marca nueva o renombrada, o la del último producto de una que se quitó).
/// </param>
/// <param name="Vendidos">
/// Lo más vendido, completo, si el servicio lo calculó otra vez desde el que trae la copia (<paramref name="VersionVendidos"/>).
/// </param>
/// <param name="Elegidos">
/// Lo que esta caja eligió en la búsqueda después de lo que trae su copia (<paramref name="VersionElegidos"/>): pisa lo de
/// la copia con el mismo texto.
/// </param>
/// <param name="VersionVendidos">La de lo más vendido con que queda la copia; vacía con un servicio de antes.</param>
/// <param name="VersionElegidos">Hasta dónde queda lo elegido en la copia; vacía con un servicio de antes.</param>
/// <param name="Exhibidores">
/// Los exhibidores y las temporadas (entrega 13), completos, si cambió algo de ellos: su nombre, sus fechas, sus lugares o lo
/// acomodado en ellos.
/// </param>
public sealed record CambiosCopiaDto(
    string? PaqueteCompleto, long Version, long VersionExistencias, int UltimoFolio, IReadOnlyList<ProductoVentaDto> Productos, IReadOnlyList<Guid> Quitados,
    IReadOnlyList<CodigoLocalDto> Codigos, IReadOnlyList<ExistenciaLocalDto> Existencias,
    CajaDto? Caja = null, IReadOnlyList<UsuarioLocalDto>? Usuarios = null, IReadOnlyList<PermisosLocalesDto>? Permisos = null,
    IReadOnlyList<ReglaDto>? Reglas = null, TicketLocalDto? Ticket = null, ConfiguracionSucursalDto? Configuracion = null,
    IReadOnlyList<ClienteRegistradoDto>? Clientes = null, IReadOnlyList<ClienteRegistradoDto>? Registrados = null, IReadOnlyList<string>? Marcas = null,
    IReadOnlyList<SinonimoDto>? Sinonimos = null, IReadOnlyList<VecesVendidoDto>? Vendidos = null, IReadOnlyList<ElegidoDto>? Elegidos = null,
    long? VersionVendidos = null, long? VersionElegidos = null, IReadOnlyList<ExhibidorDto>? Exhibidores = null)
{
    /// <summary>No se pueden dar los cambios desde ahí: la caja baja la copia completa.</summary>
    public static CambiosCopiaDto Completo(string motivo) => new(motivo, 0, 0, 0, [], [], [], []);

    /// <summary>
    /// Si trae algo del catálogo o de lo demás de la copia (no solo existencias, lo más vendido o lo elegido, que se vuelven a
    /// pedir si se pierden): entonces la copia se guarda ya.
    /// </summary>
    public bool CambiaCatalogo =>
        Productos.Count > 0 || Quitados.Count > 0 || Caja is not null || Usuarios is not null || Permisos is not null || Reglas is not null
        || Ticket is not null || Configuracion is not null || Registrados is not null || Clientes is not null || Marcas is not null || Sinonimos is not null
        || Exhibidores is not null;

    /// <summary>
    /// Si cambia algo de lo que arma la búsqueda por nombre (los productos, sus códigos, las marcas, los sinónimos): entonces
    /// su índice se arma otra vez (entrega 5, parte 3). Lo más vendido y lo elegido solo ordenan: el índice sirve igual.
    /// </summary>
    public bool CambiaLaBusqueda => Productos.Count > 0 || Quitados.Count > 0 || Marcas is not null || Sinonimos is not null;
}
