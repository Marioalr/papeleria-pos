using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Herramientas.Ficticios;

/// <summary>Lo que inventa <see cref="GeneradorFicticios"/>, todavía en memoria.</summary>
public sealed class DatosFicticios(IdsDeterministas generadorIds)
{
    /// <summary>Se sigue usando al guardar, para que los IDs que nacen ahí (historial de precios) también se repitan.</summary>
    public IdsDeterministas GeneradorIds { get; } = generadorIds;

    public Sucursal Matriz { get; internal set; } = null!;
    public List<Sucursal> Sucursales { get; } = [];
    public List<Departamento> Departamentos { get; } = [];
    public List<Marca> Marcas { get; } = [];
    public List<Producto> Productos { get; } = [];
    public List<Variante> Variantes { get; } = [];
    public List<Presentacion> Presentaciones { get; } = [];
    public List<CodigoBarras> Codigos { get; } = [];
    public List<Precio> Precios { get; } = [];
    public List<Proveedor> Proveedores { get; } = [];
    public List<ProveedorVariante> ProveedoresVariantes { get; } = [];
    public List<VentaHistorica> Ventas { get; } = [];
    public List<MovimientoInventario> Movimientos { get; } = [];
    public List<MapeoImportacion> Mapeos { get; } = [];

    public AnomaliasFicticias Resumen { get; } = new();

    public long ValorInventarioCentavos => Movimientos.Sum(m => m.CostoCentavos);

    public long VentasCentavos => Ventas.Where(v => !v.Cancelada).Sum(v => v.TotalCentavos);
}

/// <summary>Lo que se metió a propósito para que el diagnóstico lo encuentre.</summary>
public sealed class AnomaliasFicticias
{
    /// <summary>Variantes con existencia que no se vendieron en todo el periodo.</summary>
    public List<Guid> SinMovimiento { get; } = [];

    public List<Guid> PrecioBajoCosto { get; } = [];

    /// <summary>Mercancía sin ningún código de barras.</summary>
    public List<Guid> SinCodigo { get; } = [];

    /// <summary>El mismo producto capturado dos veces: (original, duplicado).</summary>
    public List<(Guid Original, Guid Duplicado)> Duplicados { get; } = [];
}

/// <summary>UUIDv7 a partir de una semilla: hora que avanza un milisegundo por ID y bytes aleatorios reproducibles.</summary>
public sealed class IdsDeterministas(int semilla, DateTime inicioUtc)
{
    private readonly Random _r = new(unchecked(semilla * 31 + 7));
    private DateTimeOffset _momento = new(DateTime.SpecifyKind(inicioUtc, DateTimeKind.Utc));

    public Guid Nuevo()
    {
        Span<byte> aleatorios = stackalloc byte[10];
        _r.NextBytes(aleatorios);
        _momento = _momento.AddMilliseconds(1);
        return Ids.Version7(_momento, aleatorios);
    }
}
