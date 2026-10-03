using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Herramientas.Importacion;

/// <summary>
/// Datos listos para guardar en una base nueva: catálogo, histórico de ventas y existencias. Lo llenan el generador
/// de datos ficticios y el importador de eleventa, y lo guarda <see cref="GuardadoLote"/>.
/// </summary>
public class LoteDeDatos
{
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

    /// <summary>De dónde salen los IDs al guardar; vacío para IDs nuevos al azar.</summary>
    public virtual Func<Guid>? GeneradorIds => null;

    public long ValorInventarioCentavos => Movimientos.Sum(m => m.CostoCentavos);

    public long VentasCentavos => Ventas.Where(v => !v.Cancelada).Sum(v => v.TotalCentavos);
}
