namespace PapeleriaPos.Eleventa;

// Las filas tal como vienen de eleventa (esquema 216). Los montos son pesos con IVA incluido y las fechas, hora local.

public sealed record FilaDepartamento(int Id, string Nombre, bool Activo);

/// <param name="TipoVenta">'U' por unidad; 'D' a granel (admite fracciones).</param>
/// <param name="MedidaId">1 pieza, 3 metro, 4 kilo, 5 litro (tabla MEDIDAS).</param>
/// <param name="Precio">Precio final con impuestos (PFINAL).</param>
public sealed record FilaProducto(
    int Id, string Codigo, string Descripcion, char TipoVenta, int? MedidaId, double Costo, double Precio, double Mayoreo,
    int? DepartamentoId, bool Eliminado);

public sealed record FilaExistencia(int ProductoId, double Cantidad);

public sealed record FilaPromocion(string Codigo, double Desde, double PrecioConImpuestos);

public sealed record FilaTicket(int Id, int? Folio, DateTime? VendidoEn, double Total, bool Cancelado);

/// <param name="TotalArticulo">Vacío en las líneas viejas: entonces el importe es precio × cantidad.</param>
/// <param name="Ganancia">Utilidad por pieza (precio menos costo): la de la línea es la cantidad por esta.</param>
public sealed record FilaLinea(
    int TicketId, string Codigo, string Nombre, double Cantidad, double? TotalArticulo, double PrecioFinal, double Ganancia,
    int? DepartamentoId, bool UsaMayoreo);

public sealed class DatosEleventa
{
    public List<FilaDepartamento> Departamentos { get; init; } = [];
    public List<FilaProducto> Productos { get; init; } = [];
    public List<FilaExistencia> Existencias { get; init; } = [];
    public List<FilaPromocion> Promociones { get; init; } = [];
    public List<FilaTicket> Tickets { get; init; } = [];
    public List<FilaLinea> Lineas { get; init; } = [];

    /// <summary>Lo del piloto que la lectura trae en la misma foto (vacío si no se pidió).</summary>
    public DatosDelPiloto? Piloto { get; init; }
}

// ── Solo del piloto (el puente): lo que la lectura de cada pocos minutos trae además del catálogo ──

/// <summary>
/// Lo que la lectura le pregunta a eleventa para el piloto, en la misma foto que el catálogo y las existencias
/// (docs/migracion-eleventa.md, «Lo que falta para el piloto»).
/// </summary>
/// <param name="CajaId">La caja de eleventa de la caja 2 (<c>CAJAS.ID</c>): sus turnos abiertos.</param>
/// <param name="Turnos">Los turnos que adoptó la caja nueva y siguen sin su corte: si eleventa ya los cerró.</param>
public sealed record ConsultaDelPiloto(int CajaId, IReadOnlyCollection<int> Turnos);

/// <param name="Inicio">Hora de la tienda, como todo en eleventa.</param>
public sealed record FilaTurno(int Id, int CajaId, DateTime Inicio, DateTime? Termino, decimal? EfectivoAlCierre);

/// <summary>Lo que faltó (salida) o sobró (entrada) al cerrar un turno en eleventa: el movimiento del corte que lo anota.</summary>
public sealed record FilaCierreDeTurno(int TurnoId, bool Falto, decimal Monto);

public sealed class DatosDelPiloto
{
    /// <summary>Los turnos abiertos de la caja y los adoptados (abiertos o ya cerrados).</summary>
    public List<FilaTurno> Turnos { get; init; } = [];

    public List<FilaCierreDeTurno> Cierres { get; init; } = [];
}

/// <summary>
/// Las listas <c>IN</c> de Firebird 2.0 admiten menos de 1500 elementos: se mandan de a 1000. Son números, así que van en
/// el texto de la consulta.
/// </summary>
internal static class ListasIn
{
    public const int Tamano = 1000;

    public static IEnumerable<string> Grupos(IEnumerable<int> numeros) =>
        numeros.Distinct().Chunk(Tamano).Select(g => string.Join(", ", g.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
}
